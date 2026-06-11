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
    //  ФИКС 1: не крутить в Update уже отработавшие OnInserted-эффекты контейнера.
    //
    //  Проблема (профайлер + чтение движка): ItemContainer.OnItemContained (ItemContainer.cs:442-445)
    //  на КАЖДЫЙ StatusEffect Containable'а добавляет запись в activeContainedItems — НЕЗАВИСИМО от
    //  типа эффекта. У стволов с по-патронной зарядкой (SPAS-13/Mosin/M1/...) Containable патрона =
    //  ~18 эффектов (OnInserted-анимация перезарядки + 1 OnRemoved). 8 патронов × 18 = ~144 записи.
    //  Цикл Update (ItemContainer.cs:700-711) каждый кадр зовёт на каждую ShouldApplyEffects
    //  (строит targets: AddRange(AllPropertyObjects), а для NearbyItems — скан мира) + GetComponent<Wearable>,
    //  но применяет ТОЛЬКО OnActive/OnContaining/OnWearing — для OnInserted/OnRemoved это холостой ход.
    //  Отложенная анимация (delay=) ставится один раз при вставке (Apply(OnInserted), стр.454) как
    //  fire-and-forget DelayedEffect — циклом Update НЕ крутится. Значит после вставки OnInserted —
    //  балласт. Решение: после OnItemContained выкинуть из activeContainedItems отработавшие записи.
    //
    //  ВАЖНО: ActionType — ПОСЛЕДОВАТЕЛЬНЫЙ enum (Always=0..OnInserted=24,OnRemoved=25), НЕ [Flags].
    //  У эффекта ровно один type. Сравниваем через ==, а не битовой маской.
    //
    //  Оставляем записи, что реально используются: OnActive/OnContaining/OnWearing (цикл Update),
    //  OnRemoved (OnItemRemoved:528), BlameEquipperForDeath (BlameEquipperForDeath():544).
    //  containedItems (список ОТРИСОВКИ, стр.59) НЕ трогаем -> визуал цел. Shared -> грузится на
    //  КЛИЕНТЕ (FPS) и СЕРВЕРЕ (CPU); операция детерминированная, activeContainedItems не сетевой
    //  -> рассинхрона нет. По умолчанию ВКЛ; клиентское меню умеет переключать на лету (см. SetEnabled).
    // ==========================================================================================
    public sealed class ContainedEffectsOptPlugin : IAssemblyPlugin
    {
        private static Harmony _h;

        // По умолчанию ВКЛ. Тумблер из клиентского меню (NG Logger&Optimizations) переключает на лету.
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

                // OnItemRemoved — только чтобы чистить «загашник» подрезанных записей (для корректного возврата при тумблере).
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

        // Переключение фикса на лету (зовётся из клиентского меню; на сервере не вызывается, остаётся ВКЛ).
        //  on=true  -> подрезать уже загруженные контейнеры;
        //  on=false -> вернуть подрезанные записи назад (чтобы СРАЗУ увидеть разницу в нагрузке).
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
        // Типы, что ОБЯЗАНЫ оставаться в activeContainedItems (см. шапку файла).
        private static bool MustKeep(ActionType t) =>
            t == ActionType.OnActive || t == ActionType.OnContaining ||
            t == ActionType.OnWearing || t == ActionType.OnRemoved;

        private static FieldInfo    _activeField;
        private static PropertyInfo _statusEffectProp;
        private static PropertyInfo _blameProp;
        private static PropertyInfo _itemProp;
        private static bool         _failed;

        // Подрезанные записи, спрятанные на случай выключения фикса (чтобы вернуть их назад).
        // Ключ — контейнер (по ссылке); при сборке мусора контейнера запись уходит автоматически.
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
            if (_statusEffectProp == null) { _failed = true; return false; } // структура движка изменилась — не вмешиваемся
            return true;
        }

        private static Item EntryItem(object entry)
        {
            try { return _itemProp?.GetValue(entry) as Item; } catch { return null; }
        }

        // true -> запись надо ОСТАВИТЬ в activeContainedItems
        private static bool KeepEntry(object entry)
        {
            if (!EnsureProps(entry)) { return true; }
            if (_blameProp != null && _blameProp.GetValue(entry) is bool blame && blame) { return true; }
            if (_statusEffectProp.GetValue(entry) is not StatusEffect eff) { return true; }
            return MustKeep(eff.type);
        }

        // ===== POSTFIX: ItemContainer.OnItemContained(Item containedItem, bool _) =====
        // Вызывается ПОСЛЕ того, как движок применил OnInserted (ItemContainer.cs:454).
        public static void OnContainedPostfix(ItemContainer __instance, Item containedItem)
        {
            if (_failed) { return; }
            try
            {
                IList active = GetActive(__instance);
                if (active == null || active.Count == 0) { return; }

                // снять из загашника устаревшие записи про этот предмет (он только что пере-вставлен)
                PurgeStash(__instance, containedItem);

                if (!ContainedEffectsOptPlugin.Enabled) { return; } // фикс выключен — список остаётся полным

                List<object> stash = null;
                for (int i = active.Count - 1; i >= 0; i--)
                {
                    object entry = active[i];
                    if (entry == null) { continue; }
                    // трогаем только записи только что вставленного предмета
                    if (containedItem != null && !ReferenceEquals(EntryItem(entry), containedItem)) { continue; }
                    if (KeepEntry(entry)) { continue; }

                    stash ??= _stash.GetValue(__instance, _ => new List<object>());
                    stash.Add(entry);
                    active.RemoveAt(i);
                }
            }
            catch { _failed = true; }
        }

        // ===== POSTFIX: ItemContainer.OnItemRemoved(Item containedItem) — чистим загашник =====
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

        // ===== Подрезать ВСЕ контейнеры (вызов при включении фикса на лету) =====
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

        // ===== Вернуть всё подрезанное назад (вызов при выключении фикса на лету) =====
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
                            if (c.Inventory != null && !c.Inventory.Contains(item)) { continue; } // предмета уже нет
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
