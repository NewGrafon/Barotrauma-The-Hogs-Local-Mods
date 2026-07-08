using System.Linq;
using System.Reflection;
using Barotrauma;
using Barotrauma.Items.Components;
using HarmonyLib;

namespace NGLinkingPin
{
    // ============================================================================================
    //  Runtime screwdriver gate for OUR wiring panels.
    //
    //  Vanilla gates opening a ConnectionPanel via a <RequiredItem items="screwdriver"> in the panel XML
    //  + Item.TryInteract's HasRequiredItems check. LinkingPinHelper injects that RequiredItem into the
    //  prefab ConfigElement, but on a plain vanilla cabinet that had NO ConnectionPanel the freshly-added
    //  RequiredItem doesn't reliably end up on the runtime component, so the linking panel could be opened
    //  with no screwdriver (reported). This patch enforces the gate DIRECTLY and uniformly: block
    //  ConnectionPanel.Select for any panel carrying our inventory_link pin unless the character has a
    //  screwdriver equipped. No dependence on XML parsing. Machines without our pin are untouched (vanilla).
    //  Skipped outside a running round (sub editor / menus) so editor wiring keeps working.
    //  RUS: Рантайм-гейт отвёртки для НАШИХ панелей провязки. Ваниль гейтит открытие панели через
    //  RUS: <RequiredItem screwdriver> в XML, но на плоском ванильном шкафу без ConnectionPanel добавленный
    //  RUS: в рантайме RequiredItem ненадёжно попадает на компонент → панель открывалась без отвёртки.
    //  RUS: Патч форсит гейт напрямую: блокируем ConnectionPanel.Select для панели с пином inventory_link,
    //  RUS: если у персонажа нет отвёртки в руках. Без зависимости от парсинга XML. Машины без нашего пина
    //  RUS: не трогаем. Вне раунда (sub-редактор/меню) пропускаем — провязка в редакторе работает.
    // ============================================================================================
    public sealed class ConnectionPanelGate : IAssemblyPlugin
    {
        private Harmony harmony;
        private static readonly Identifier Screwdriver = new Identifier("screwdriver");

        public void Initialize()
        {
            harmony = new Harmony("nglinkingpin.panelgate");
            var select = typeof(ConnectionPanel).GetMethod("Select", BindingFlags.Public | BindingFlags.Instance);
            if (select != null)
            {
                harmony.Patch(select, prefix: new HarmonyMethod(typeof(ConnectionPanelGate), nameof(Select_Prefix)));
            }
            else
            {
                DebugConsole.Log("[NG] [Linking Pin] ConnectionPanel.Select not found — screwdriver gate disabled.");
            }
        }

        public void OnLoadCompleted() { }
        public void PreInitPatching() { }
        public void Dispose() { harmony?.UnpatchSelf(); harmony = null; }

        private static bool Select_Prefix(ConnectionPanel __instance, Character picker, ref bool __result)
        {
            try
            {
                // Only gate OUR container wiring panels (those carrying the inventory_link pin).
                if (__instance == null || !__instance.Connections.Any(c => c.Name == LinkingPinHelper.PIN)) { return true; }
                if (picker == null) { return true; }                 // network/deselect edge — let vanilla handle
                if (!LinkingPinHelper.IsInGame()) { return true; }   // sub editor / menus — don't gate
                if (HasScrewdriver(picker)) { return true; }         // screwdriver equipped -> allow opening
                __result = false;                                    // no screwdriver -> block; the container inventory still opens
                return false;
            }
            catch { return true; }
        }

        private static bool HasScrewdriver(Character c)
        {
            foreach (var item in c.HeldItems)
            {
                if (item == null) { continue; }
                if (item.Prefab.Identifier == Screwdriver || item.HasTag(Screwdriver)) { return true; }
            }
            return false;
        }
    }
}
