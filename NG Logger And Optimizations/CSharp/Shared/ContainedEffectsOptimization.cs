using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using Barotrauma;
using Barotrauma.Items.Components;
using HarmonyLib;
using Microsoft.Xna.Framework;

namespace NGContainerOpt
{
    // ==========================================================================================
    //  FIX 1: don't churn already-spent OnInserted container effects in Update.
    //
    //  Problem (profiler + reading the engine): ItemContainer.OnItemContained (ItemContainer.cs:442-445)
    //  adds an entry to activeContainedItems for EVERY StatusEffect of the Containable — REGARDLESS of
    //  the effect type. For per-round-reload guns (SPAS-13/Mosin/M1/...) a shell's Containable has ~18
    //  effects (the OnInserted reload animation + 1 OnRemoved). 8 shells × 18 = ~144 entries. The Update
    //  loop (ItemContainer.cs:700-711) calls ShouldApplyEffects on each every frame (building targets:
    //  AddRange(AllPropertyObjects), and for NearbyItems a world scan) + GetComponent<Wearable>, but it
    //  only applies OnActive/OnContaining/OnWearing — for OnInserted/OnRemoved that's idle work. The
    //  delayed animation (delay=) is scheduled once on insert (Apply(OnInserted), line 454) as a
    //  fire-and-forget DelayedEffect — NOT driven by the Update loop. So after insert OnInserted is dead
    //  weight. Fix: after OnItemContained, drop the spent entries from activeContainedItems.
    //
    //  IMPORTANT: ActionType is a SEQUENTIAL enum (Always=0..OnInserted=24,OnRemoved=25), NOT [Flags].
    //  An effect has exactly one type. Compare with ==, not a bitmask.
    //
    //  We keep the entries that are actually used: OnActive/OnContaining/OnWearing (Update loop),
    //  OnRemoved (OnItemRemoved:528), BlameEquipperForDeath (BlameEquipperForDeath():544).
    //  containedItems (the DRAW list, line 59) is left untouched -> visuals intact. Shared -> loads on
    //  the CLIENT (FPS) and SERVER (CPU); the operation is deterministic, activeContainedItems isn't
    //  networked -> no desync. ON by default; the client menu can toggle it live (see SetEnabled).
    //
    //  RUS: ФИКС 1: не крутить в Update уже отработавшие OnInserted-эффекты контейнера.
    //  RUS: Проблема (профайлер + чтение движка): ItemContainer.OnItemContained (ItemContainer.cs:442-445)
    //  RUS: на КАЖДЫЙ StatusEffect Containable'а добавляет запись в activeContainedItems — НЕЗАВИСИМО от
    //  RUS: типа эффекта. У стволов с по-патронной зарядкой (SPAS-13/Mosin/M1/...) Containable патрона =
    //  RUS: ~18 эффектов (OnInserted-анимация перезарядки + 1 OnRemoved). 8 патронов × 18 = ~144 записи.
    //  RUS: Цикл Update (ItemContainer.cs:700-711) каждый кадр зовёт на каждую ShouldApplyEffects
    //  RUS: (строит targets: AddRange(AllPropertyObjects), а для NearbyItems — скан мира) + GetComponent<Wearable>,
    //  RUS: но применяет ТОЛЬКО OnActive/OnContaining/OnWearing — для OnInserted/OnRemoved это холостой ход.
    //  RUS: Отложенная анимация (delay=) ставится один раз при вставке (Apply(OnInserted), стр.454) как
    //  RUS: fire-and-forget DelayedEffect — циклом Update НЕ крутится. После вставки OnInserted — балласт.
    //  RUS: Решение: после OnItemContained выкинуть из activeContainedItems отработавшие записи.
    //  RUS: ВАЖНО: ActionType — ПОСЛЕДОВАТЕЛЬНЫЙ enum (Always=0..OnInserted=24,OnRemoved=25), НЕ [Flags].
    //  RUS: У эффекта ровно один type. Сравниваем через ==, а не битовой маской.
    //  RUS: Оставляем записи, что реально используются: OnActive/OnContaining/OnWearing (цикл Update),
    //  RUS: OnRemoved (OnItemRemoved:528), BlameEquipperForDeath. containedItems (список ОТРИСОВКИ) НЕ трогаем.
    //  RUS: Shared -> КЛИЕНТ (FPS) и СЕРВЕР (CPU); детерминированно, activeContainedItems не сетевой -> без
    //  RUS: рассинхрона. По умолчанию ВКЛ; клиентское меню умеет переключать на лету (см. SetEnabled).
    // ==========================================================================================
    public sealed class ContainedEffectsOptPlugin : IAssemblyPlugin
    {
        private static Harmony _h;

        // ON by default. The toggle in the client menu (NG Logger And Optimizations) flips it live.
        // RUS: По умолчанию ВКЛ. Тумблер из клиентского меню (NG Logger&Optimizations) переключает на лету.
        public static bool Enabled { get; private set; } = true;

        public void PreInitPatching() { }

        public void Initialize()
        {
            try
            {
                if (_h != null) { return; }
                _h = new Harmony("ng.containedeffectsopt");
                BindingFlags sp = BindingFlags.Static | BindingFlags.Public;

                MethodInfo onContained = AccessTools.Method(typeof(ItemContainer), "OnItemContained");
                if (onContained == null)
                {
                    DebugConsole.NewMessage(NetEventLogger.Loc.Tag + NetEventLogger.Loc.T("ItemContainer.OnItemContained не найден — оптимизация контейнеров не применена.", "ItemContainer.OnItemContained not found — container optimization not applied."), Color.Orange);
                    return;
                }
                _h.Patch(onContained, postfix: new HarmonyMethod(typeof(TrimSpentEffectsPatch).GetMethod(
                    nameof(TrimSpentEffectsPatch.OnContainedPostfix), sp)));

                // OnItemRemoved — only to clean the "stash" of trimmed entries (for a correct restore on toggle).
                // RUS: OnItemRemoved — только чтобы чистить «загашник» подрезанных записей (для корректного возврата при тумблере).
                MethodInfo onRemoved = AccessTools.Method(typeof(ItemContainer), "OnItemRemoved");
                if (onRemoved != null)
                {
                    _h.Patch(onRemoved, postfix: new HarmonyMethod(typeof(TrimSpentEffectsPatch).GetMethod(
                        nameof(TrimSpentEffectsPatch.OnRemovedPostfix), sp)));
                }

                DebugConsole.NewMessage(NetEventLogger.Loc.Tag + NetEventLogger.Loc.T("Оптимизация контейнеров активна: отработавшие OnInserted-эффекты не крутятся в Update.", "Container optimization active: spent OnInserted effects no longer churn in Update."), Color.LightGreen);
            }
            catch (Exception ex)
            {
                DebugConsole.NewMessage(NetEventLogger.Loc.Tag + NetEventLogger.Loc.T("Ошибка инициализации оптимизации контейнеров: ", "Container optimization init error: ") + ex.Message, Color.Red);
                _h = null;
            }
        }

        public void OnLoadCompleted() { }

        public void Dispose()
        {
            try { _h?.UnpatchSelf(); } catch { }
            _h = null;
        }

        // Toggle the fix live (called from the client menu; not called on the server, stays ON there).
        //  on=true  -> trim the already-loaded containers;
        //  on=false -> put the trimmed entries back (to see the load difference IMMEDIATELY).
        // RUS: Переключение фикса на лету (зовётся из клиентского меню; на сервере не вызывается, остаётся ВКЛ).
        // RUS:  on=true  -> подрезать уже загруженные контейнеры;
        // RUS:  on=false -> вернуть подрезанные записи назад (чтобы СРАЗУ увидеть разницу в нагрузке).
        public static void SetEnabled(bool on)
        {
            if (on == Enabled) { return; }
            Enabled = on;
            try
            {
                if (on) { TrimSpentEffectsPatch.TrimAllContainers(); }
                else    { TrimSpentEffectsPatch.RestoreAllContainers(); }
            }
            catch (Exception ex)
            {
                DebugConsole.NewMessage(NetEventLogger.Loc.Tag + "SetEnabled(" + on + "): " + ex.Message, Color.Orange);
            }
        }
    }

    public static class TrimSpentEffectsPatch
    {
        // Types that MUST stay in activeContainedItems (see the file header).
        // RUS: Типы, что ОБЯЗАНЫ оставаться в activeContainedItems (см. шапку файла).
        private static bool MustKeep(ActionType t) =>
            t == ActionType.OnActive || t == ActionType.OnContaining ||
            t == ActionType.OnWearing || t == ActionType.OnRemoved;

        private static FieldInfo    _activeField;
        private static PropertyInfo _statusEffectProp;
        private static PropertyInfo _blameProp;
        private static PropertyInfo _itemProp;
        private static bool         _failed;

        // Trimmed entries, kept aside in case the fix is disabled (so we can put them back).
        // Key = the container (by reference); when the container is GC'd, its entry drops automatically.
        // RUS: Подрезанные записи, спрятанные на случай выключения фикса (чтобы вернуть их назад).
        // RUS: Ключ — контейнер (по ссылке); при сборке мусора контейнера запись уходит автоматически.
        private static readonly ConditionalWeakTable<ItemContainer, List<object>> _stash =
            new ConditionalWeakTable<ItemContainer, List<object>>();

        private static IList GetActive(ItemContainer c)
        {
            if (_activeField == null)
            {
                _activeField = AccessTools.Field(typeof(ItemContainer), "activeContainedItems");
                if (_activeField == null) { _failed = true; return null; }
            }
            return _activeField.GetValue(c) as IList;
        }

        private static bool EnsureProps(object entry)
        {
            if (_statusEffectProp != null) { return true; }
            Type t = entry.GetType();
            _statusEffectProp = t.GetProperty("StatusEffect");
            _blameProp        = t.GetProperty("BlameEquipperForDeath");
            _itemProp         = t.GetProperty("Item");
            if (_statusEffectProp == null) { _failed = true; return false; } // engine structure changed — don't interfere   // RUS: структура движка изменилась — не вмешиваемся
            return true;
        }

        private static Item EntryItem(object entry)
        {
            try { return _itemProp?.GetValue(entry) as Item; } catch { return null; }
        }

        // true -> the entry must be KEPT in activeContainedItems
        // RUS: true -> запись надо ОСТАВИТЬ в activeContainedItems
        private static bool KeepEntry(object entry)
        {
            if (!EnsureProps(entry)) { return true; }
            if (_blameProp != null && _blameProp.GetValue(entry) is bool blame && blame) { return true; }
            if (_statusEffectProp.GetValue(entry) is not StatusEffect eff) { return true; }
            return MustKeep(eff.type);
        }

        // ===== POSTFIX: ItemContainer.OnItemContained(Item containedItem, bool _) =====
        // Called AFTER the engine has applied OnInserted (ItemContainer.cs:454).
        // RUS: Вызывается ПОСЛЕ того, как движок применил OnInserted (ItemContainer.cs:454).
        public static void OnContainedPostfix(ItemContainer __instance, Item containedItem)
        {
            if (_failed) { return; }
            try
            {
                IList active = GetActive(__instance);
                if (active == null || active.Count == 0) { return; }

                // remove stale stash entries for this item (it was just re-inserted)
                // RUS: снять из загашника устаревшие записи про этот предмет (он только что пере-вставлен)
                PurgeStash(__instance, containedItem);

                if (!ContainedEffectsOptPlugin.Enabled) { return; } // fix disabled — the list stays full   // RUS: фикс выключен — список остаётся полным

                List<object> stash = null;
                for (int i = active.Count - 1; i >= 0; i--)
                {
                    object entry = active[i];
                    if (entry == null) { continue; }
                    // only touch entries of the just-inserted item
                    // RUS: трогаем только записи только что вставленного предмета
                    if (containedItem != null && !ReferenceEquals(EntryItem(entry), containedItem)) { continue; }
                    if (KeepEntry(entry)) { continue; }

                    stash ??= _stash.GetValue(__instance, _ => new List<object>());
                    stash.Add(entry);
                    active.RemoveAt(i);
                }
            }
            catch { _failed = true; }
        }

        // ===== POSTFIX: ItemContainer.OnItemRemoved(Item containedItem) — clean the stash =====
        // RUS: ===== POSTFIX: ItemContainer.OnItemRemoved(Item containedItem) — чистим загашник =====
        public static void OnRemovedPostfix(ItemContainer __instance, Item containedItem)
        {
            if (_failed) { return; }
            try { PurgeStash(__instance, containedItem); } catch { }
        }

        private static void PurgeStash(ItemContainer c, Item item)
        {
            if (item == null) { return; }
            if (!_stash.TryGetValue(c, out List<object> stash) || stash == null) { return; }
            for (int i = stash.Count - 1; i >= 0; i--)
            {
                if (ReferenceEquals(EntryItem(stash[i]), item)) { stash.RemoveAt(i); }
            }
        }

        // ===== Trim ALL containers (called when enabling the fix live) =====
        // RUS: ===== Подрезать ВСЕ контейнеры (вызов при включении фикса на лету) =====
        public static void TrimAllContainers()
        {
            if (_failed) { return; }
            foreach (Item it in Item.ItemList)
            {
                if (it == null) { continue; }
                foreach (ItemContainer c in it.GetComponents<ItemContainer>())
                {
                    try
                    {
                        IList active = GetActive(c);
                        if (active == null) { continue; }
                        List<object> stash = null;
                        for (int i = active.Count - 1; i >= 0; i--)
                        {
                            object entry = active[i];
                            if (entry == null || KeepEntry(entry)) { continue; }
                            stash ??= _stash.GetValue(c, _ => new List<object>());
                            stash.Add(entry);
                            active.RemoveAt(i);
                        }
                    }
                    catch { }
                }
            }
        }

        // ===== Restore everything that was trimmed (called when disabling the fix live) =====
        // RUS: ===== Вернуть всё подрезанное назад (вызов при выключении фикса на лету) =====
        public static void RestoreAllContainers()
        {
            if (_failed) { return; }
            foreach (KeyValuePair<ItemContainer, List<object>> kv in _stash)
            {
                ItemContainer c = kv.Key;
                List<object> stash = kv.Value;
                if (c == null || stash == null || stash.Count == 0) { continue; }
                try
                {
                    IList active = GetActive(c);
                    if (active != null)
                    {
                        foreach (object entry in stash)
                        {
                            Item item = EntryItem(entry);
                            if (item == null) { continue; }
                            if (c.Inventory != null && !c.Inventory.Contains(item)) { continue; } // the item is no longer there   // RUS: предмета уже нет
                            if (!active.Contains(entry)) { active.Add(entry); }
                        }
                    }
                }
                catch { }
                stash.Clear();
            }
        }
    }
}
