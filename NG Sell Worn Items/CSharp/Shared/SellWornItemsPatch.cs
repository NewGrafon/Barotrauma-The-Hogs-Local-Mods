using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using Barotrauma;
using HarmonyLib;
using Microsoft.Xna.Framework;

namespace SellWornItems
{
    // Влияет на продажу предметов торговцам:
    //   1) можно ли вообще продать (порог прочности + принудительное разрешение для предметов с ценой);
    //   2) цена продажи зависит от прочности предмета;
    //   3) диагностика: пишет в консоль, что блокирует продажу конкретного предмета.
    public sealed class SellWornItemsPlugin : IAssemblyPlugin
    {
        // ============================ НАСТРОЙКА ============================
        // true  -> продавать можно ЛЮБОЙ предмет с ценой (игнор прочности, спавна и содержимого).
        // false -> уважать порог прочности и спавн; пустое оружие продаётся (тех. деталь repair
        //          игнорируется), но если внутри магазин/модуль/прицел — продажа блокируется.
        public static bool ForceSellableIfHasPrice = false;

        // Минимальный % прочности для продажи (ниже — продать нельзя, когда force = false).
        public static float MinConditionPercentageToSell = 30f;

        // Делать ли цену продажи пропорциональной прочности.
        public static bool ScaleSellPriceByCondition = true;

        // Писать ли диагностику в консоль (каждый тип предмета — один раз).
        public static bool DebugLog = true;
        // ==================================================================

        private static Harmony _harmony;
        private static readonly HashSet<string> _logged = new HashSet<string>();

        public void PreInitPatching() { }

        public void Initialize()
        {
            try
            {
                if (_harmony != null) { return; }
                _harmony = new Harmony("com.aaac.sellwornitems");

                // (1) Продаваемость: порог прочности (transpiler) + принудительное разрешение/диагностика (postfix).
                var isSellable = AccessTools.Method(typeof(CargoManager), "IsItemSellable");
                if (isSellable != null)
                {
                    _harmony.Patch(isSellable,
                        postfix: new HarmonyMethod(typeof(SellWornItemsPlugin), nameof(IsItemSellable_Postfix)),
                        transpiler: new HarmonyMethod(typeof(SellWornItemsPlugin), nameof(IsItemSellable_Transpiler)));
                    Log("патч CargoManager.IsItemSellable установлен.", Color.LightGreen);
                }
                else
                {
                    Log("CargoManager.IsItemSellable НЕ найден!", Color.OrangeRed);
                }

                // (2) Цена в зависимости от прочности.
                var sellPrice = AccessTools.Method(typeof(Location.StoreInfo), "GetAdjustedItemSellPrice");
                if (sellPrice != null)
                {
                    _harmony.Patch(sellPrice,
                        postfix: new HarmonyMethod(typeof(SellWornItemsPlugin), nameof(GetAdjustedItemSellPrice_Postfix)));
                    Log("патч Location.StoreInfo.GetAdjustedItemSellPrice установлен.", Color.LightGreen);
                }
                else
                {
                    Log("Location.StoreInfo.GetAdjustedItemSellPrice НЕ найден!", Color.OrangeRed);
                }

                Log($"=== SellWornItems загружен === force={ForceSellableIfHasPrice}, порог={MinConditionPercentageToSell}%, цена_от_прочности={ScaleSellPriceByCondition}", Color.Cyan);
            }
            catch (Exception ex)
            {
                Log("ОШИБКА инициализации: " + ex.Message, Color.Red);
            }
        }

        // (1a) Заменяет в IL константу 90.0f на наш порог прочности.
        public static IEnumerable<CodeInstruction> IsItemSellable_Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var codes = new List<CodeInstruction>(instructions);
            int replaced = 0;
            foreach (var c in codes)
            {
                if (c.opcode == OpCodes.Ldc_R4 && c.operand is float f && Math.Abs(f - 90f) < 0.01f)
                {
                    c.operand = MinConditionPercentageToSell;
                    replaced++;
                }
            }
            Log(replaced > 0
                ? $"порог продажи: 90% -> {MinConditionPercentageToSell}% (заменено: {replaced})"
                : "ВНИМАНИЕ: константа 90 в IsItemSellable не найдена.",
                replaced > 0 ? Color.LightGreen : Color.OrangeRed);
            return codes;
        }

        // (1b) Разрешаем продажу, если внутри ствола НЕТ ничего ПРОДАВАЕМОГО (магазина/модулей):
        //      технические непродаваемые детали (repair и т.п.) игнорируем, а продаваемое
        //      содержимое (магазин/прицел/глушитель) — блокирует продажу (как и требовалось).
        //      Также уважаем порог прочности и анти-эксплойт "заспавнено в этом аутпосте".
        //      НИЧЕГО не удаляется из оружия — мы лишь меняем разрешение на продажу.
        public static void IsItemSellable_Postfix(Item item, ref bool __result)
        {
            try
            {
                if (__result || item?.Prefab == null) { return; }
                if (item.Removed || !item.Prefab.CanBeSold) { return; } // без цены продать нельзя — и не шумим в логе

                bool spawnedOutpost = item.SpawnedInCurrentOutpost;
                bool tooWorn = item.ConditionPercentage < MinConditionPercentageToSell;
                bool hasValuableContents = HasSellableContainedItem(item);

                bool allow = ForceSellableIfHasPrice || (!spawnedOutpost && !tooWorn && !hasValuableContents);
                if (allow) { __result = true; }

                if (DebugLog)
                {
                    string reason = "";
                    if (spawnedOutpost) { reason += " ЗаспавненоВАутпосте"; }
                    if (tooWorn) { reason += $" Прочность<{MinConditionPercentageToSell:F0}%({item.ConditionPercentage:F0})"; }
                    if (hasValuableContents) { reason += " ВнутриЦенноеСодержимое(магазин/модуль)"; }
                    if (reason == "") { reason = " (скрытый контейнер/трекинг продажи)"; }
                    var id = item.Prefab.Identifier.Value;
                    if (string.IsNullOrEmpty(id)) { id = item.Prefab.Name?.Value ?? "?"; }
                    if (_logged.Add(id))
                    {
                        Log($"'{id}':{reason} -> {(allow ? "РАЗРЕШЕНО" : "ЗАБЛОКИРОВАНО")}",
                            allow ? Color.LightGreen : Color.Yellow);
                    }
                }
            }
            catch { }
        }

        // Есть ли внутри предмета хоть одно ПРОДАВАЕМОЕ содержимое (магазин/модуль с ценой)?
        // Технические непродаваемые детали (repair, прокси перезарядки и т.п.) не считаются.
        private static bool HasSellableContainedItem(Item item)
        {
            var contained = item.ContainedItems;
            if (contained == null) { return false; }
            foreach (var c in contained)
            {
                if (c != null && c.Prefab != null && c.Prefab.CanBeSold) { return true; }
            }
            return false;
        }

        // (2) Домножает цену продажи на средний коэффициент прочности продаваемых экземпляров этого типа.
        public static void GetAdjustedItemSellPrice_Postfix(ItemPrefab item, ref int __result)
        {
            if (!ScaleSellPriceByCondition || __result <= 1 || item == null) { return; }
            try
            {
                float factor = GetConditionFactorForPrefab(item);
                if (factor < 1f)
                {
                    __result = Math.Max((int)(__result * factor), 1);
                }
            }
            catch { }
        }

        private static float GetConditionFactorForPrefab(ItemPrefab prefab)
        {
            var mainSub = Submarine.MainSub;
            float sum = 0f;
            int n = 0;
            var list = Item.ItemList;
            for (int i = 0; i < list.Count; i++)
            {
                var it = list[i];
                if (it == null || it.Removed || it.Prefab != prefab) { continue; }
                if (it.SpawnedInCurrentOutpost) { continue; }

                bool onMainSub = mainSub != null && it.Submarine == mainSub;
                bool inCrew = it.GetRootInventoryOwner() is Character c && !c.IsDead && c.TeamID == CharacterTeamType.Team1;
                if (!onMainSub && !inCrew) { continue; }

                float cond = it.ConditionPercentage;
                if (cond < 0f) { cond = 0f; } else if (cond > 100f) { cond = 100f; }
                sum += cond / 100f;
                n++;
            }
            return n > 0 ? sum / n : 1f;
        }

        public void OnLoadCompleted() { }

        public void Dispose()
        {
            try { _harmony?.UnpatchSelf(); } catch { }
            _harmony = null;
            _logged.Clear();
        }

        private static void Log(string text, Color color)
        {
            try { DebugConsole.NewMessage("[SellWornItems] " + text, color); } catch { }
        }
    }
}
