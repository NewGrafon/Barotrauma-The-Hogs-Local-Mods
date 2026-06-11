using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using Barotrauma;
using HarmonyLib;
using Microsoft.Xna.Framework;

namespace SellWornItems
{
    // RU/EN localization local to THIS mod (separate assembly -> cannot reuse the logger mod's Loc).
    // RUS: Локализация RU/EN, локальная для ЭТОГО мода (отдельная сборка -> нельзя переиспользовать Loc мода-логгера).
    internal static class Loc
    {
        public const string Tag = "[NG] [Sell Worn Items] ";
        private static int _ru = -1;
        public static bool Ru
        {
            get
            {
                if (_ru < 0)
                {
                    try { _ru = GameSettings.CurrentConfig.Language.ToString().IndexOf("russ", StringComparison.OrdinalIgnoreCase) >= 0 ? 1 : 0; }
                    catch { _ru = 0; }
                }
                return _ru == 1;
            }
        }
        public static string T(string ru, string en) => Ru ? ru : en;
    }

    // Affects selling items to merchants:
    //   1) whether it can be sold at all (condition threshold + force-allow for priced items);
    //   2) the sell price scales with the item's condition;
    //   3) diagnostics: logs what blocks a specific item's sale.
    // RUS: Влияет на продажу предметов торговцам:
    // RUS:   1) можно ли вообще продать (порог прочности + принудительное разрешение для предметов с ценой);
    // RUS:   2) цена продажи зависит от прочности предмета;
    // RUS:   3) диагностика: пишет в консоль, что блокирует продажу конкретного предмета.
    public sealed class SellWornItemsPlugin : IAssemblyPlugin
    {
        // ============================ SETTINGS / НАСТРОЙКА ============================
        // true  -> ANY priced item can be sold (ignores condition, spawn and contents).
        // false -> respect the condition threshold and spawn; empty guns sell (the technical
        //          "repair" part is ignored), but a magazine/module/scope inside blocks the sale.
        // RUS: true  -> продавать можно ЛЮБОЙ предмет с ценой (игнор прочности, спавна и содержимого).
        // RUS: false -> уважать порог прочности и спавн; пустое оружие продаётся (тех. деталь repair
        // RUS:          игнорируется), но если внутри магазин/модуль/прицел — продажа блокируется.
        public static bool ForceSellableIfHasPrice = false;

        // Minimum condition % to sell (below this you can't sell, when force = false).
        // RUS: Минимальный % прочности для продажи (ниже — продать нельзя, когда force = false).
        public static float MinConditionPercentageToSell = 30f;

        // Whether to make the sell price proportional to condition.
        // RUS: Делать ли цену продажи пропорциональной прочности.
        public static bool ScaleSellPriceByCondition = true;

        // Whether to print diagnostics to console (once per item type).
        // RUS: Писать ли диагностику в консоль (каждый тип предмета — один раз).
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

                // (1) Sellability: condition threshold (transpiler) + force-allow/diagnostics (postfix).
                // RUS: (1) Продаваемость: порог прочности (transpiler) + принудительное разрешение/диагностика (postfix).
                var isSellable = AccessTools.Method(typeof(CargoManager), "IsItemSellable");
                if (isSellable != null)
                {
                    _harmony.Patch(isSellable,
                        postfix: new HarmonyMethod(typeof(SellWornItemsPlugin), nameof(IsItemSellable_Postfix)),
                        transpiler: new HarmonyMethod(typeof(SellWornItemsPlugin), nameof(IsItemSellable_Transpiler)));
                    Log(Loc.T("патч CargoManager.IsItemSellable установлен.", "CargoManager.IsItemSellable patch installed."), Color.LightGreen);
                }
                else
                {
                    Log(Loc.T("CargoManager.IsItemSellable НЕ найден!", "CargoManager.IsItemSellable NOT found!"), Color.OrangeRed);
                }

                // (2) Price depending on condition.
                // RUS: (2) Цена в зависимости от прочности.
                var sellPrice = AccessTools.Method(typeof(Location.StoreInfo), "GetAdjustedItemSellPrice");
                if (sellPrice != null)
                {
                    _harmony.Patch(sellPrice,
                        postfix: new HarmonyMethod(typeof(SellWornItemsPlugin), nameof(GetAdjustedItemSellPrice_Postfix)));
                    Log(Loc.T("патч Location.StoreInfo.GetAdjustedItemSellPrice установлен.", "Location.StoreInfo.GetAdjustedItemSellPrice patch installed."), Color.LightGreen);
                }
                else
                {
                    Log(Loc.T("Location.StoreInfo.GetAdjustedItemSellPrice НЕ найден!", "Location.StoreInfo.GetAdjustedItemSellPrice NOT found!"), Color.OrangeRed);
                }

                Log(Loc.Ru
                    ? $"=== SellWornItems загружен === force={ForceSellableIfHasPrice}, порог={MinConditionPercentageToSell}%, цена_от_прочности={ScaleSellPriceByCondition}"
                    : $"=== SellWornItems loaded === force={ForceSellableIfHasPrice}, threshold={MinConditionPercentageToSell}%, priceByCondition={ScaleSellPriceByCondition}", Color.Cyan);
            }
            catch (Exception ex)
            {
                Log(Loc.T("ОШИБКА инициализации: ", "Init ERROR: ") + ex.Message, Color.Red);
            }
        }

        // (1a) Replaces the IL constant 90.0f with our condition threshold.
        // RUS: (1a) Заменяет в IL константу 90.0f на наш порог прочности.
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
                ? Loc.T($"порог продажи: 90% -> {MinConditionPercentageToSell}% (заменено: {replaced})", $"sell threshold: 90% -> {MinConditionPercentageToSell}% (replaced: {replaced})")
                : Loc.T("ВНИМАНИЕ: константа 90 в IsItemSellable не найдена.", "WARNING: the 90 constant in IsItemSellable was not found."),
                replaced > 0 ? Color.LightGreen : Color.OrangeRed);
            return codes;
        }

        // (1b) Allow selling if the gun holds NOTHING SELLABLE (no magazine/modules): technical
        //      unsellable parts (repair etc.) are ignored, but sellable contents (mag/scope/suppressor)
        //      block the sale (as intended). Also respect the condition threshold and the
        //      "spawned in this outpost" anti-exploit. NOTHING is removed from the gun — we only change
        //      the sell permission.
        // RUS: (1b) Разрешаем продажу, если внутри ствола НЕТ ничего ПРОДАВАЕМОГО (магазина/модулей):
        // RUS:      технические непродаваемые детали (repair и т.п.) игнорируем, а продаваемое
        // RUS:      содержимое (магазин/прицел/глушитель) — блокирует продажу (как и требовалось).
        // RUS:      Также уважаем порог прочности и анти-эксплойт «заспавнено в этом аутпосте».
        // RUS:      НИЧЕГО не удаляется из оружия — мы лишь меняем разрешение на продажу.
        public static void IsItemSellable_Postfix(Item item, ref bool __result)
        {
            try
            {
                if (__result || item?.Prefab == null) { return; }
                if (item.Removed || !item.Prefab.CanBeSold) { return; } // no price -> can't sell -> don't spam the log   // RUS: без цены продать нельзя — и не шумим в логе

                bool spawnedOutpost = item.SpawnedInCurrentOutpost;
                bool tooWorn = item.ConditionPercentage < MinConditionPercentageToSell;
                bool hasValuableContents = HasSellableContainedItem(item);

                bool allow = ForceSellableIfHasPrice || (!spawnedOutpost && !tooWorn && !hasValuableContents);
                if (allow) { __result = true; }

                if (DebugLog)
                {
                    string reason = "";
                    if (spawnedOutpost) { reason += Loc.T(" ЗаспавненоВАутпосте", " SpawnedInOutpost"); }
                    if (tooWorn) { reason += Loc.Ru ? $" Прочность<{MinConditionPercentageToSell:F0}%({item.ConditionPercentage:F0})" : $" Condition<{MinConditionPercentageToSell:F0}%({item.ConditionPercentage:F0})"; }
                    if (hasValuableContents) { reason += Loc.T(" ВнутриЦенноеСодержимое(магазин/модуль)", " HasSellableContents(magazine/module)"); }
                    if (reason == "") { reason = Loc.T(" (скрытый контейнер/трекинг продажи)", " (hidden container/sale tracking)"); }
                    var id = item.Prefab.Identifier.Value;
                    if (string.IsNullOrEmpty(id)) { id = item.Prefab.Name?.Value ?? "?"; }
                    if (_logged.Add(id))
                    {
                        Log($"'{id}':{reason} -> {(allow ? Loc.T("РАЗРЕШЕНО", "ALLOWED") : Loc.T("ЗАБЛОКИРОВАНО", "BLOCKED"))}",
                            allow ? Color.LightGreen : Color.Yellow);
                    }
                }
            }
            catch { }
        }

        // Does the item hold at least one SELLABLE content (priced magazine/module)?
        // Technical unsellable parts (repair, reload proxies, etc.) don't count.
        // RUS: Есть ли внутри предмета хоть одно ПРОДАВАЕМОЕ содержимое (магазин/модуль с ценой)?
        // RUS: Технические непродаваемые детали (repair, прокси перезарядки и т.п.) не считаются.
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

        // (2) Scales the sell price by the average condition factor of sellable instances of this type.
        // RUS: (2) Домножает цену продажи на средний коэффициент прочности продаваемых экземпляров этого типа.
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
            try { DebugConsole.NewMessage(Loc.Tag + text, color); } catch { }
        }
    }
}
