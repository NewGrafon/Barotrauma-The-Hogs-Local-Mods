using System.Collections.Generic;
using System.Reflection;
using Barotrauma;
using Barotrauma.Items.Components;
using HarmonyLib;

namespace NGLinkingPin
{
    // Patches Fabricator.RefreshAvailableIngredients so that when two Fabricators are linked, the
    // linked one contributes its OUTPUT container only (vanilla would scan its INPUT slot, which is
    // wrong). Linked plain containers are handled by vanilla (their inventory is scanned normally).
    //   Prefix  — temporarily removes linked Fabricators from linkedTo so vanilla skips them.
    //   Postfix — restores them and adds their OutputContainer contents to availableIngredients.
    // Safe: RefreshAvailableIngredients runs on the main thread and never yields, so prefix/postfix
    // always bracket each call. Ported from "Linking Pin Lua".
    // RUS: Патч Fabricator.RefreshAvailableIngredients: у связанных фабрикаторов берём только их
    // RUS: ВЫХОДНОЙ контейнер (ваниль ошибочно сканирует входной). Перенесено из "Linking Pin Lua".
    public sealed class FabricatorPatch : IAssemblyPlugin
    {
        private Harmony harmony;
        private static FieldInfo _availableIngredientsField;

        private static readonly Dictionary<Fabricator, List<Item>> _suppressedFabs
            = new Dictionary<Fabricator, List<Item>>();

        public void Initialize()
        {
            harmony = new Harmony("nglinkingpin.fabricator");

            _availableIngredientsField = typeof(Fabricator).GetField(
                "availableIngredients", BindingFlags.NonPublic | BindingFlags.Instance);

            var method = typeof(Fabricator).GetMethod(
                "RefreshAvailableIngredients", BindingFlags.NonPublic | BindingFlags.Instance);

            if (method != null)
            {
                harmony.Patch(method,
                    prefix: new HarmonyMethod(typeof(FabricatorPatch), nameof(Prefix)),
                    postfix: new HarmonyMethod(typeof(FabricatorPatch), nameof(Postfix)));
            }
            else
            {
                DebugConsole.Log("[NG] [Linking Pin] WARNING: Fabricator.RefreshAvailableIngredients not found — fabricator-to-fabricator linking will not work.");
            }
        }

        public void OnLoadCompleted() { }
        public void PreInitPatching() { }

        public void Dispose()
        {
            harmony?.UnpatchSelf();
            harmony = null;
            _suppressedFabs.Clear();
        }

        private static void Prefix(Fabricator __instance)
        {
            var removed = new List<Item>();
            var linkedTo = __instance.Item.linkedTo;
            for (int i = linkedTo.Count - 1; i >= 0; i--)
            {
                if (linkedTo[i] is Item linkedItem && linkedItem.GetComponent<Fabricator>() != null)
                {
                    removed.Add(linkedItem);
                    linkedTo.RemoveAt(i);
                }
            }
            _suppressedFabs[__instance] = removed;
        }

        private static void Postfix(Fabricator __instance)
        {
            // Restore inventory_link-linked fabricators removed in the prefix.
            List<Item> removed = null;
            if (_suppressedFabs.TryGetValue(__instance, out removed))
            {
                _suppressedFabs.Remove(__instance);
                foreach (var item in removed) { __instance.Item.linkedTo.Add(item); }
            }

            if (_availableIngredientsField == null) return;
            var available = _availableIngredientsField.GetValue(__instance) as Dictionary<Identifier, List<Item>>;
            if (available == null) return;

            // (1) inventory_link fabricator<->fabricator: share OUTPUT container contents.
            if (removed != null)
            {
                foreach (var linkedItem in removed)
                {
                    var linkedFab = linkedItem.GetComponent<Fabricator>();
                    if (linkedFab?.OutputContainer == null) continue;
                    foreach (var containedItem in linkedFab.OutputContainer.Inventory.AllItems)
                        AddItemAndNested(available, containedItem);
                }
            }

            // (2) share_containers pin: pull the INPUT container pools that wired machines own, so
            //     several fabricators craft from one set of containers via a single wire each.
            // RUS: Пин share_containers: тянем пулы контейнеров, подключённых к соединённым машинам —
            // RUS: несколько фабрикаторов крафтят из одного набора контейнеров одним проводом.
            foreach (var partner in LinkingPinHelper.GetSharePartners(__instance.Item))
            {
                foreach (var entity in partner.linkedTo)
                {
                    if (!(entity is Item linked)) continue;
                    // Only plain storage containers (not the partner machines themselves).
                    if (linked.GetComponent<Fabricator>() != null || linked.GetComponent<Deconstructor>() != null) continue;
                    var container = linked.GetComponent<ItemContainer>();
                    if (container == null) continue;
                    foreach (var it in container.Inventory.AllItems)
                        AddItemAndNested(available, it);
                }
            }
        }

        private static void AddItemAndNested(Dictionary<Identifier, List<Item>> available, Item item)
        {
            AddToAvailable(available, item);
            var nested = item.GetComponent<ItemContainer>();
            if (nested != null)
            {
                foreach (var nestedItem in nested.Inventory.AllItems)
                    AddToAvailable(available, nestedItem);
            }
        }

        private static void AddToAvailable(Dictionary<Identifier, List<Item>> available, Item item)
        {
            var id = item.Prefab.Identifier;
            if (!available.TryGetValue(id, out var list))
            {
                list = new List<Item>();
                available[id] = list;
            }
            if (!list.Contains(item)) { list.Add(item); }
        }
    }
}
