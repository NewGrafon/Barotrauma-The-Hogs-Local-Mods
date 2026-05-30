using System;
using System.Collections.Generic;
using System.Reflection.Emit;
using Barotrauma;
using HarmonyLib;
using Microsoft.Xna.Framework;

namespace SellWornItems
{
    // Глобально влияет на продажу предметов торговцам (ваниль + любые моды), на клиенте и на сервере:
    //   1) меняет минимальный порог прочности, при котором предмет вообще можно продать
    //      (в ванили жёстко зашито 90% в Barotrauma.CargoManager.IsItemSellable);
    //   2) делает ЦЕНУ продажи зависящей от прочности предмета
    //      (в ванили цена от состояния не зависит — патчим Location.StoreInfo.GetAdjustedItemSellPrice).
    public sealed class SellWornItemsPlugin : IAssemblyPlugin
    {
        // ============================ НАСТРОЙКА ============================
        // Минимальный процент прочности (0..100), при котором предмет МОЖНО продать.
        //   0f   -> продавать можно в ЛЮБОМ состоянии;
        //   50f  -> только предметы с прочностью >= 50%;
        //   90f  -> как в ванили.
        public static float MinConditionPercentageToSell = 30f;

        // Делать ли цену продажи пропорциональной прочности.
        //   true  -> предмет на 40% прочности продаётся за ~40% цены;
        //   false -> цена не зависит от прочности (как в ванили).
        public static bool ScaleSellPriceByCondition = true;
        // ==================================================================

        private static Harmony _harmony;

        public void PreInitPatching() { }

        public void Initialize()
        {
            try
            {
                if (_harmony != null) { return; }
                _harmony = new Harmony("com.aaac.sellwornitems");

                // (1) Порог прочности для самой возможности продажи (приватный метод).
                var isSellable = AccessTools.Method(typeof(CargoManager), "IsItemSellable");
                if (isSellable != null)
                {
                    _harmony.Patch(isSellable,
                        transpiler: new HarmonyMethod(typeof(SellWornItemsPlugin), nameof(IsItemSellable_Transpiler)));
                }
                else
                {
                    Log("CargoManager.IsItemSellable не найден — порог не изменён.", Color.OrangeRed);
                }

                // (2) Цена продажи в зависимости от прочности. Один общий метод — его используют
                //     и витрина, и расчёт реальной выплаты (и в одиночной игре, и на сервере).
                var sellPrice = AccessTools.Method(typeof(Location.StoreInfo), "GetAdjustedItemSellPrice");
                if (sellPrice != null)
                {
                    _harmony.Patch(sellPrice,
                        postfix: new HarmonyMethod(typeof(SellWornItemsPlugin), nameof(GetAdjustedItemSellPrice_Postfix)));
                }
                else
                {
                    Log("Location.StoreInfo.GetAdjustedItemSellPrice не найден — цена от прочности не применена.", Color.OrangeRed);
                }

                Log($"=== SellWornItems загружен === порог = {MinConditionPercentageToSell}%, цена_от_прочности = {ScaleSellPriceByCondition}", Color.LightGreen);
            }
            catch (Exception ex)
            {
                Log("ОШИБКА инициализации: " + ex.Message, Color.Red);
            }
        }

        // (1) Заменяет в IL единственную константу 90.0f на наш порог.
        public static IEnumerable<CodeInstruction> IsItemSellable_Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var codes = new List<CodeInstruction>(instructions);
            int replaced = 0;
            foreach (var c in codes)
            {
                // мутируем operand на месте, чтобы сохранить метки/блоки исключений
                if (c.opcode == OpCodes.Ldc_R4 && c.operand is float f && Math.Abs(f - 90f) < 0.01f)
                {
                    c.operand = MinConditionPercentageToSell;
                    replaced++;
                }
            }
            Log(replaced > 0
                ? $"порог продажи: 90% -> {MinConditionPercentageToSell}% (заменено: {replaced})"
                : "ВНИМАНИЕ: константа 90 в IsItemSellable не найдена (игра обновилась?). Порог НЕ изменён.",
                replaced > 0 ? Color.LightGreen : Color.OrangeRed);
            return codes;
        }

        // (2) Домножает цену продажи на средний коэффициент прочности экземпляров этого типа,
        //     которые сейчас можно продать (на лодке игроков / в инвентаре экипажа).
        //     Для штучной продажи это точное значение; для смешанных стопок — усреднённое.
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
            catch { /* при любой ошибке оставляем ванильную цену */ }
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
            return n > 0 ? sum / n : 1f; // ничего не нашли -> не трогаем цену
        }

        public void OnLoadCompleted() { }

        public void Dispose()
        {
            try { _harmony?.UnpatchSelf(); } catch { }
            _harmony = null;
        }

        private static void Log(string text, Color color)
        {
            try { DebugConsole.NewMessage("[SellWornItems] " + text, color); } catch { }
        }
    }
}
