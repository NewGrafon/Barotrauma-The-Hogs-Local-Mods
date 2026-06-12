using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Reflection;
using Barotrauma;
using HarmonyLib;
using Microsoft.Xna.Framework;

namespace NGNearbyOpt
{
    // ==========================================================================================
    //  FIX 2: skip empty NearbyItems scans (searching for items "nearby").
    //
    //  Problem (profiler: PDA-mech-anim Comp:CustomInterface ~419µs/frame; same family as AK-74):
    //  a StatusEffect with target="NearbyItems" calls StatusEffect.AddNearbyTargets, which in the
    //  general case (targetidentifiers NOT powered/junctionbox/relaycomponent) ITERATES THE WHOLE
    //  Item.ItemList (tens of thousands of items) — an O(N) world scan. Continuous TickBoxes (PDA:
    //  Fabricator/Deconstruct/Recipes) run a NearbyItems cleanup effect on a spawn-proxy once a second
    //  even when the proxy is ABSENT. The profiler averages it to hundreds of µs/frame, and it GROWS
    //  with the item count.
    //
    //  Solution (behavior-identical): keep an index of identifiers/tags present in the world. If an
    //  effect targets ONLY NearbyItems, has targetidentifiers, no "item" wildcard, and NONE of those
    //  ids are currently present in the world — there are definitely no item targets, so we skip the
    //  full scan. When a target exists (proxy spawned) the original runs as usual. The character scan
    //  (NearbyCharacters) is left alone (it iterates Character.CharacterList, usually small).
    //
    //  Index:
    //   _staticCounts — refcount of prefab-identifier + prefab-tags of present items (static, maintained
    //                   on item create/remove: ctor with ItemList.Add / Item.Remove);
    //   _dynamicEver  — set of EVER-added dynamic tags (only grows = conservative; maintained on
    //                   AddTag/ReplaceTag + seeding instance tags on create/load).
    //  Conservative: when in doubt we DON'T skip (false-negatives impossible; an extra scan is harmless).
    //  MP-safe: the index is derived from the same item world on client and server -> identical decision.
    //  ON by default; the client menu can toggle it live (SetEnabled — instant, no state to restore).
    //
    //  RUS: ФИКС 2: пропускать пустые NearbyItems-сканы (поиск предметов рядом).
    //  RUS: Проблема (профайлер: PDA-mech-anim Comp:CustomInterface ~419µs/кадр; та же семья — AK-74):
    //  RUS: StatusEffect с target="NearbyItems" зовёт StatusEffect.AddNearbyTargets, который в общем
    //  RUS: случае (targetidentifiers НЕ powered/junctionbox/relaycomponent) ПЕРЕБИРАЕТ ВЕСЬ Item.ItemList
    //  RUS: (десятки тысяч предметов) — O(N) скан мира. У континуальных TickBox'ов (PDA: Fabricator/
    //  RUS: Deconstruct/Recipes) cleanup-эффект NearbyItems на спавн-прокси гоняет этот скан раз в секунду,
    //  RUS: даже когда прокси НЕТ. Усредняется профайлером в сотни µs/кадр и РАСТЁТ с числом предметов.
    //  RUS: Решение (поведение идентично): ведём индекс присутствующих в мире identifier/тегов. Если у
    //  RUS: эффекта таргет ТОЛЬКО NearbyItems, заданы targetidentifiers, нет wildcard "item", и НИ один
    //  RUS: из id сейчас не присутствует в мире — целей точно нет, пропускаем полный скан. Когда цель есть
    //  RUS: (прокси заспавнен) — отрабатывает оригинал. Скан персонажей (NearbyCharacters) не трогаем.
    //  RUS: Индекс: _staticCounts — refcount prefab-identifier + prefab-тегов присутствующих предметов
    //  RUS: (на создании/удалении); _dynamicEver — множество КОГДА-ЛИБО добавленных динам. тегов (растёт).
    //  RUS: Консервативно: если сомневаемся — НЕ пропускаем. MP-safe. По умолчанию ВКЛ; меню умеет на лету.
    // ==========================================================================================
    public sealed class NearbyTargetsOptPlugin : IAssemblyPlugin
    {
        private static Harmony _h;
        public static bool Enabled { get; private set; } = true;

        public void PreInitPatching() { }

        public void Initialize()
        {
            try
            {
                if (_h != null) { return; }
                _h = new Harmony("ng.nearbytargetsopt");
                BindingFlags sp = BindingFlags.Static | BindingFlags.Public;

                MethodInfo addNearby = AccessTools.Method(typeof(StatusEffect), "AddNearbyTargets");
                if (addNearby == null)
                {
                    DebugConsole.NewMessage(NetEventLogger.Loc.Tag + NetEventLogger.Loc.T("StatusEffect.AddNearbyTargets не найден — оптимизация поиска предметов не применена.", "StatusEffect.AddNearbyTargets not found — nearby-item search optimization not applied."), Color.Orange);
                    return;
                }
                _h.Patch(addNearby, prefix: new HarmonyMethod(typeof(NearbyTargetsPatch).GetMethod(nameof(NearbyTargetsPatch.Prefix), sp)));

                // Item-creation accounting — postfix on the constructor with ItemList.Add (Item.cs:1182; ctor 1167 delegates to it).
                // RUS: Учёт создания предмета — постфикс на конструктор с ItemList.Add (Item.cs:1182; ctor 1167 делегирует в него).
                ConstructorInfo ctor = AccessTools.Constructor(typeof(Item),
                    new[] { typeof(Rectangle), typeof(ItemPrefab), typeof(Submarine), typeof(bool), typeof(ushort) });
                if (ctor != null)
                {
                    _h.Patch(ctor, postfix: new HarmonyMethod(typeof(NearbyTargetsIndex).GetMethod(nameof(NearbyTargetsIndex.ItemCreated), sp)));
                }

                MethodInfo remove = AccessTools.Method(typeof(Item), "Remove");
                if (remove != null)
                {
                    _h.Patch(remove, postfix: new HarmonyMethod(typeof(NearbyTargetsIndex).GetMethod(nameof(NearbyTargetsIndex.ItemRemoved), sp)));
                }

                MethodInfo addTag = AccessTools.Method(typeof(Item), "AddTag", new[] { typeof(Identifier) });
                if (addTag != null)
                {
                    _h.Patch(addTag, postfix: new HarmonyMethod(typeof(NearbyTargetsIndex).GetMethod(nameof(NearbyTargetsIndex.TagAdded), sp)));
                }

                MethodInfo replaceTag = AccessTools.Method(typeof(Item), "ReplaceTag", new[] { typeof(Identifier), typeof(Identifier) });
                if (replaceTag != null)
                {
                    _h.Patch(replaceTag, postfix: new HarmonyMethod(typeof(NearbyTargetsIndex).GetMethod(nameof(NearbyTargetsIndex.TagReplaced), sp)));
                }

                NearbyTargetsIndex.PopulateExisting(); // existing items (important for reloadcs)   // RUS: существующие предметы (важно для reloadcs)
                DebugConsole.NewMessage(NetEventLogger.Loc.Tag + NetEventLogger.Loc.T("Оптимизация поиска предметов рядом активна: пустые NearbyItems-сканы пропускаются.", "Nearby-item search optimization active: empty NearbyItems scans are skipped."), Color.LightGreen);
            }
            catch (Exception ex)
            {
                DebugConsole.NewMessage(NetEventLogger.Loc.Tag + NetEventLogger.Loc.T("NearbyTargets ошибка инициализации: ", "NearbyTargets init error: ") + ex.Message, Color.Red);
                _h = null;
            }
        }

        public void OnLoadCompleted() { }

        public void Dispose()
        {
            try { _h?.UnpatchSelf(); } catch { }
            _h = null;
            NearbyTargetsIndex.Clear();
        }

        // Live toggle (called from the client menu). Instant: the prefix respects the flag immediately,
        // the index is always maintained (no state to restore).
        // RUS: Переключение на лету (зовётся из клиентского меню). Мгновенно: префикс сразу уважает флаг,
        // RUS: индекс ведётся всегда (без состояния для восстановления).
        public static void SetEnabled(bool on) { Enabled = on; }
    }

    public static class NearbyTargetsIndex
    {
        private static readonly Dictionary<Identifier, int> _staticCounts = new Dictionary<Identifier, int>();
        private static readonly HashSet<Identifier>         _dynamicEver  = new HashSet<Identifier>();
        private static readonly HashSet<Item>               _registered   = new HashSet<Item>();
        private static FieldInfo _tagsField;
        private static bool _failed;

        // true -> at least one of the ids may be present in the world (must NOT skip the scan).
        // RUS: true -> хоть один из id может присутствовать в мире (нельзя пропускать скан).
        public static bool MaybePresent(ImmutableHashSet<Identifier> ids)
        {
            if (_failed) { return true; } // index broken -> conservative: always full scan (original behavior)   // RUS: индекс сломан -> консервативно: всегда полный скан
            foreach (Identifier id in ids)
            {
                if (_staticCounts.ContainsKey(id) || _dynamicEver.Contains(id)) { return true; }
            }
            return false;
        }

        private static void Bump(Identifier id, int delta)
        {
            _staticCounts.TryGetValue(id, out int c);
            c += delta;
            if (c <= 0) { _staticCounts.Remove(id); } else { _staticCounts[id] = c; }
        }

        private static void RegisterStatic(Item item, int delta)
        {
            if (item?.Prefab == null) { return; }
            Bump(item.Prefab.Identifier, delta);
            if (item.Prefab.Tags != null)
            {
                foreach (Identifier t in item.Prefab.Tags) { Bump(t, delta); }
            }
        }

        // Seed the item's dynamic (instance-specific) tags into _dynamicEver (via the private 'tags' field).
        // RUS: Засеять динамические (инстанс-специфичные) теги предмета в _dynamicEver (через приватное поле tags).
        private static void SeedInstanceTags(Item item)
        {
            try
            {
                _tagsField ??= AccessTools.Field(typeof(Item), "tags");
                if (_tagsField?.GetValue(item) is IEnumerable<Identifier> dyn)
                {
                    foreach (Identifier t in dyn) { _dynamicEver.Add(t); }
                }
            }
            catch { }
        }

        public static void ItemCreated(Item __instance)
        {
            if (_failed) { return; }
            try
            {
                if (_registered.Add(__instance)) { RegisterStatic(__instance, +1); }
                SeedInstanceTags(__instance); // initial instance tags (from the sub file, etc.)   // RUS: начальные инстанс-теги (из саб-файла и т.п.)
            }
            catch { _failed = true; }
        }

        public static void ItemRemoved(Item __instance)
        {
            if (_failed) { return; }
            try { if (_registered.Remove(__instance)) { RegisterStatic(__instance, -1); } } catch { }
        }

        public static void TagAdded(Identifier tag)
        {
            if (_failed) { return; }
            try { _dynamicEver.Add(tag); } catch { }
        }

        public static void TagReplaced(Identifier tag, Identifier newTag)
        {
            if (_failed) { return; }
            try { _dynamicEver.Add(newTag); } catch { }
        }

        public static void PopulateExisting()
        {
            try
            {
                foreach (Item it in Item.ItemList)
                {
                    if (_registered.Add(it)) { RegisterStatic(it, +1); }
                    SeedInstanceTags(it);
                }
            }
            catch { }
        }

        public static void Clear()
        {
            try { _staticCounts.Clear(); _dynamicEver.Clear(); _registered.Clear(); } catch { }
        }
    }

    public static class NearbyTargetsPatch
    {
        private static readonly Identifier ItemWildcard = "item".ToIdentifier();

        // true -> run the original; false -> skip (there are definitely no item targets in the world).
        // RUS: true -> выполнить оригинал; false -> пропустить (целей-предметов в мире точно нет).
        public static bool Prefix(StatusEffect __instance, List<ISerializableEntity> targets)
        {
            if (!NearbyTargetsOptPlugin.Enabled) { return true; }
            try
            {
                if (!__instance.HasTargetType(StatusEffect.TargetType.NearbyItems)) { return true; }
                // if it ALSO scans characters — don't interfere (we don't optimize that).
                // RUS: если есть ещё и скан персонажей — не вмешиваемся (его не оптимизируем).
                if (__instance.HasTargetType(StatusEffect.TargetType.NearbyCharacters)) { return true; }

                ImmutableHashSet<Identifier> ids = __instance.TargetIdentifiers;
                if (ids == null || ids.Count == 0) { return true; }    // null = matches any item   // RUS: матчит любой предмет
                if (ids.Contains(ItemWildcard)) { return true; }       // "item" = matches any item   // RUS: матчит любой предмет
                if (NearbyTargetsIndex.MaybePresent(ids)) { return true; } // a matching target may be in the world   // RUS: подходящая цель может быть в мире

                return false; // no matching item in the world -> the full Item.ItemList scan isn't needed   // RUS: подходящего предмета нет -> полный скан не нужен
            }
            catch { return true; } // fail-safe: on any error, defer to the original   // RUS: при любой ошибке отдаём управление оригиналу
        }
    }
}
