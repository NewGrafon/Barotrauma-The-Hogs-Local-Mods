using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reflection;
using System.Xml.Linq;
using Barotrauma;
using Barotrauma.Items.Components;
using Microsoft.Xna.Framework;

namespace NGLinkingPin
{
    // ============================================================================================
    //  NG Linking Pin — standalone replacement for the "Linking Pin Lua" workshop mod (3696973977).
    //  Lets you wire ANY storage container (cabinet/locker/shelf, incl. modded & decor) to a
    //  fabricator/deconstructor so its contents count as ingredients.
    //
    //  Improvements over the original:
    //   • Panel injection is done at the PREFAB level (ConfigElement), so basic vanilla cabinets
    //     with an empty non-selectable <ConnectionPanel/> become rewirable (the original only added
    //     the pin, leaving such panels impossible to open). Item rebuilds components from
    //     ConfigElement at runtime, so this covers every instance, clone and level transition
    //     without a per-spawn Harmony patch.
    //   • #4: only items that ARE containers (have <ItemContainer>) or fabricators get the pin —
    //     turrets/loaders/vents/periscopes are left alone (no accidental linking).
    //   • #2: containers gated by access (idcard/keycard <RequiredItem> inside <ItemContainer>,
    //     e.g. securesteelcabinet) are excluded and forced Linkable=false.
    //   • #3: wire detection tracks ALL partners (GetWiredPartnerIds), and ShouldDisplaySideBySide
    //     covers any container, so a fabricator opens every linked container, not just the first.
    //
    //  The Lua side (ng_linking_pin.lua) drives polling, linkedTo updates and multiplayer sync.
    //  MP-safe: loaded identically on host + clients; the pin lives in the shared prefab so
    //  connection indices match across peers and the engine syncs the wires.
    //  RUS: Самостоятельная замена воркшоп-мода "Linking Pin Lua". Позволяет связать ЛЮБОЙ
    //  RUS: контейнер-хранилище (в т.ч. модовый/декор) с фабрикатором. Панель с пином прописывается
    //  RUS: в префабе → работают даже ваниль-шкафы с пустой панелью. Пин получают только контейнеры
    //  RUS: и фабрикаторы (#4); контейнеры с допуском исключены (#2); связи мульти-партнёрные (#3).
    // ============================================================================================
    public sealed class LinkingPinHelper : IAssemblyPlugin
    {
        public const string PIN = "inventory_link";
        private const int MAX_WIRES = 200;
        private const string WIRE_PREFAB = "wire";
        private const string Tag = "[NG] [Linking Pin] ";

        private static readonly Identifier MobileTag = new Identifier("mobilecontainer");
        private static readonly Identifier ContainerTag = new Identifier("container");
        private static readonly Identifier StatusMonitorTag = new Identifier("statusmonitor");

        // Fabricator identifiers added to each container's allowedLinks so the engine considers the
        // container link-allowed and draws its inventory side-by-side when the fabricator is opened (#3).
        // RUS: Идентификаторы фабрикаторов, добавляемые в allowedLinks контейнеров, чтобы движок
        // RUS: показывал их инвентарь рядом при открытии фабрикатора (#3).
        private static readonly Identifier[] FabricatorLinks =
        {
            new Identifier("fabricator"),
            new Identifier("medicalfabricator"),
            new Identifier("deconstructor"),
        };

        public static readonly List<string> LastMarked = new List<string>();

        public void PreInitPatching() { }
        public void Initialize() { }
        public void Dispose() { }

        public void OnLoadCompleted()
        {
            try { PrepareContainerPrefabs(); }
            catch (Exception e)
            {
                try { DebugConsole.NewMessage(Tag + "init error: " + e.Message, Color.Orange); } catch { }
            }
        }

        // ----------------------------------------------------------------------------------------
        //  Session / inspection helpers
        // ----------------------------------------------------------------------------------------

        public static bool IsInGame() => GameMain.GameSession?.IsRunning ?? false;

        public static bool HasInventoryPin(Item item)
        {
            var panel = item.GetComponent<ConnectionPanel>();
            if (panel == null) return false;
            return panel.Connections.Any(c => c.Name == PIN);
        }

        public static bool IsAlreadyLinked(Item itemA, Item itemB)
        {
            foreach (var entity in itemA.linkedTo)
            {
                if (entity == itemB) return true;
            }
            return false;
        }

        // Show side by side for any storage container (decor cabinets have no tags) and status monitors.
        // RUS: Показывать рядом любой контейнер (у декора нет тегов) и статус-мониторы.
        public static bool ShouldDisplaySideBySide(Item item)
        {
            if (item.HasTag(ContainerTag) || item.HasTag(StatusMonitorTag)) return true;
            return item.GetComponent<ItemContainer>() != null && item.GetComponent<Fabricator>() == null;
        }

        // Eligible = a placed, non-carried item (attached holdable, static body, or bodyless wall item).
        // RUS: Подходит = установленный, не носимый предмет (прикреплён/статичное тело/без тела).
        public static bool IsEligible(Item item)
        {
            if (item.ParentInventory != null) return false;
            var holdable = item.GetComponent<Holdable>();
            if (holdable != null && holdable.Attached) return true;
            if (item.body != null && item.body.BodyType == FarseerPhysics.BodyType.Static) return true;
            if (item.body == null) return true;
            return false;
        }

        // ----------------------------------------------------------------------------------------
        //  Wire-state scan — returns ALL partners wired through the inventory_link pin (#3).
        // ----------------------------------------------------------------------------------------

        public static List<ushort> GetWiredPartnerIds(Item item)
        {
            var result = new List<ushort>();
            var panel = item.GetComponent<ConnectionPanel>();
            var conn = panel?.Connections.FirstOrDefault(c => c.Name == PIN);
            if (conn == null) return result;

            foreach (var wire in conn.Wires)
            {
                if (wire == null) continue;
                var other = wire.OtherConnection(conn);
                if (other != null && other.Name == PIN && other.Item != null && other.Item != item)
                {
                    if (!result.Contains(other.Item.ID)) { result.Add(other.Item.ID); }
                }
            }
            return result;
        }

        // CSV variant for Lua (avoids marshalling a generic List across the C#/Lua boundary).
        // RUS: CSV-вариант для Lua (без передачи generic-коллекции через границу C#/Lua).
        public static string GetWiredPartnersCSV(Item item)
        {
            var ids = GetWiredPartnerIds(item);
            return ids.Count == 0 ? "" : string.Join(",", ids);
        }

        public static bool HasLinkWire(Item itemA, Item itemB)
        {
            var panel = itemA.GetComponent<ConnectionPanel>();
            var conn = panel?.Connections.FirstOrDefault(c => c.Name == PIN);
            if (conn == null) return false;

            foreach (var wire in conn.Wires)
            {
                if (wire == null) continue;
                var other = wire.OtherConnection(conn);
                if (other != null && other.Name == PIN && other.Item == itemB) return true;
            }
            return false;
        }

        // ----------------------------------------------------------------------------------------
        //  Pre-existing link wires — wire up linkedTo pairs (sub-editor placed) that have pins but
        //  no wire yet. In-game only. Cheap no-op once all pairs are wired.
        // ----------------------------------------------------------------------------------------

        public static void SpawnWiresForPreexistingLinks()
        {
            if (!IsInGame()) return;

            var wirePrefab = ItemPrefab.Prefabs.Find(p => p.Identifier == new Identifier(WIRE_PREFAB));
            if (wirePrefab == null) return;

            var processed = new HashSet<(ushort, ushort)>();
            foreach (var item in Item.ItemList)
            {
                foreach (var linkedEntity in item.linkedTo)
                {
                    if (!(linkedEntity is Item linkedItem)) continue;

                    ushort idLow = item.ID < linkedItem.ID ? item.ID : linkedItem.ID;
                    ushort idHigh = item.ID < linkedItem.ID ? linkedItem.ID : item.ID;
                    var pair = (idLow, idHigh);
                    if (processed.Contains(pair)) continue;
                    processed.Add(pair);

                    if (!HasInventoryPin(item) || !HasInventoryPin(linkedItem)) continue;
                    if (HasLinkWire(item, linkedItem)) continue;

                    SpawnLinkWire(wirePrefab, item, linkedItem);
                }
            }
        }

        private static void SpawnLinkWire(ItemPrefab wirePrefab, Item itemA, Item itemB)
        {
            try
            {
                var connA = itemA.GetComponent<ConnectionPanel>()?.Connections.FirstOrDefault(c => c.Name == PIN);
                var connB = itemB.GetComponent<ConnectionPanel>()?.Connections.FirstOrDefault(c => c.Name == PIN);
                if (connA == null || connB == null) return;

                var wireItem = new Item(
                    new Rectangle((int)itemA.Position.X, (int)itemA.Position.Y, 16, 8),
                    wirePrefab, itemA.Submarine, callOnItemLoaded: false, id: Entity.NullEntityID);

                var wire = wireItem.GetComponent<Wire>();
                if (wire == null) { wireItem.Remove(); return; }

                wire.Width = 0.01f;
                connA.ConnectWire(wire);
                connB.ConnectWire(wire);
                wire.Connect(connA, 0, addNode: false, sendNetworkEvent: false);
                wire.Connect(connB, 1, addNode: false, sendNetworkEvent: false);
            }
            catch (Exception e)
            {
                DebugConsole.Log(Tag + $"SpawnLinkWire failed ({itemA.Prefab.Identifier} <-> {itemB.Prefab.Identifier}): {e.Message}");
            }
        }

        // ----------------------------------------------------------------------------------------
        //  Prefab preparation — selectable ConnectionPanel + inventory_link pin on storage
        //  containers (and fabricators); access-gated containers excluded. Idempotent.
        // ----------------------------------------------------------------------------------------

        private static bool _ru;
        private static string T(string ru, string en) => _ru ? ru : en;

        private static MethodInfo _linkableSetter;
        private static void SetLinkable(MapEntityPrefab prefab, bool value)
        {
            if (_linkableSetter == null)
            {
                _linkableSetter = typeof(MapEntityPrefab)
                    .GetProperty("Linkable", BindingFlags.Public | BindingFlags.Instance)
                    ?.GetSetMethod(nonPublic: true);
            }
            try { _linkableSetter?.Invoke(prefab, new object[] { value }); } catch { }
        }

        private static FieldInfo _allowedLinksField;
        private static void AddFabricatorLinks(ItemPrefab prefab)
        {
            if (_allowedLinksField == null)
            {
                _allowedLinksField = typeof(ItemPrefab).GetField("allowedLinks",
                    BindingFlags.NonPublic | BindingFlags.Instance);
            }
            if (_allowedLinksField == null) return;
            try
            {
                var current = _allowedLinksField.GetValue(prefab) as ImmutableHashSet<Identifier>
                              ?? ImmutableHashSet<Identifier>.Empty;
                var updated = current;
                foreach (var id in FabricatorLinks) { updated = updated.Add(id); }
                if (updated != current) { _allowedLinksField.SetValue(prefab, updated); }
            }
            catch { }
        }

        private static bool NameIs(XElement e, string name)
            => e.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase);

        private static bool HasAttr(XElement e, string name, string value = null)
            => e.Attributes().Any(a => a.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase)
                && (value == null || a.Value.Equals(value, StringComparison.OrdinalIgnoreCase)));

        public static int PrepareContainerPrefabs()
        {
            try { _ru = GameSettings.CurrentConfig.Language.ToString().IndexOf("russ", StringComparison.OrdinalIgnoreCase) >= 0; }
            catch { _ru = false; }

            int wirable = 0, excluded = 0;
            LastMarked.Clear();

            foreach (var prefab in ItemPrefab.Prefabs)
            {
                try
                {
                    var root = prefab?.ConfigElement?.Element;
                    if (root == null) continue;

                    var containers = root.Elements().Where(e => NameIs(e, "ItemContainer")).ToList();
                    if (containers.Count == 0) continue;                  // not a storage container
                    if (prefab.Tags.Contains(MobileTag)) continue;        // carried crate/backpack

                    // #2: access-gated containers (idcard <RequiredItem> inside the container).
                    bool accessRequired = containers.Any(c => c.Elements().Any(ch => NameIs(ch, "RequiredItem")));
                    if (accessRequired)
                    {
                        SetLinkable(prefab, false);
                        excluded++;
                        continue;
                    }

                    PreparePanel(root);
                    SetLinkable(prefab, true);
                    AddFabricatorLinks(prefab);   // #3: let fabricators show this container side-by-side
                    wirable++;
                    if (LastMarked.Count < 400) { LastMarked.Add(prefab.Identifier.Value); }
                }
                catch { }
            }

            try
            {
                DebugConsole.NewMessage(
                    Tag + T($"контейнеров готово к связыванию: {wirable}; исключено по допуску: {excluded} (список: nglinkpin_list).",
                            $"containers ready to wire: {wirable}; excluded (access): {excluded} (list: nglinkpin_list)."),
                    wirable > 0 ? Color.Cyan : Color.Gray);
            }
            catch { }
            return wirable;
        }

        private static void PreparePanel(XElement root)
        {
            var panel = root.Elements().FirstOrDefault(e => NameIs(e, "ConnectionPanel"));
            if (panel == null)
            {
                panel = new XElement("ConnectionPanel");
                root.Add(panel);
            }

            if (panel.Elements().Any(e => NameIs(e, "input") && HasAttr(e, "name", PIN))) return; // idempotent

            if (!HasAttr(panel, "canbeselected", "true"))
            {
                panel.SetAttributeValue("canbeselected", "true");
                panel.SetAttributeValue("selectkey", "Action");
                panel.SetAttributeValue("msg", "ItemMsgRewireScrewdriver");
                if (!HasAttr(panel, "hudpriority")) panel.SetAttributeValue("hudpriority", "10");
            }

            if (!panel.Elements().Any(e => NameIs(e, "GuiFrame")))
            {
                panel.Add(new XElement("GuiFrame",
                    new XAttribute("relativesize", "0.2,0.32"),
                    new XAttribute("minsize", "400,250"),
                    new XAttribute("maxsize", "480,300"),
                    new XAttribute("anchor", "Center"),
                    new XAttribute("style", "ConnectionPanel")));
            }

            if (!panel.Elements().Any(e => NameIs(e, "RequiredItem")))
            {
                panel.Add(new XElement("RequiredItem",
                    new XAttribute("items", "screwdriver"),
                    new XAttribute("type", "Equipped")));
            }

            panel.Add(new XElement("input",
                new XAttribute("name", PIN),
                new XAttribute("displayname", "connection." + PIN),
                new XAttribute("maxwires", MAX_WIRES)));
        }
    }
}
