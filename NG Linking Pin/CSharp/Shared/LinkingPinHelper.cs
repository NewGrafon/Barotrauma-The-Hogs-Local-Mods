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
        // Second pin, fabricators/deconstructors only: wiring two of these shares one machine's
        // inventory_link containers with the other, so several fabricators can craft from one pool
        // of containers via a single wire each (no side-by-side UI — pure ingredient sharing).
        // RUS: Второй пин (только фабрикаторы/деструкторы): соединение двух таких пинов даёт одному
        // RUS: фабрикатору доступ к контейнерам другого — общий пул для крафта одним проводом.
        public const string SHARE_PIN = "share_containers";
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

        // Machines wired to this item through the share_containers pin (read live; no linkedTo).
        // RUS: Машины, соединённые с этим предметом через пин share_containers (читается вживую).
        public static List<Item> GetSharePartners(Item item)
        {
            var result = new List<Item>();
            var conn = item.GetComponent<ConnectionPanel>()?.Connections.FirstOrDefault(c => c.Name == SHARE_PIN);
            if (conn == null) return result;

            foreach (var wire in conn.Wires)
            {
                if (wire == null) continue;
                var other = wire.OtherConnection(conn);
                if (other != null && other.Name == SHARE_PIN && other.Item != null && other.Item != item)
                {
                    if (!result.Contains(other.Item)) { result.Add(other.Item); }
                }
            }
            return result;
        }

        // Last exception from the share-containers patch (surfaced by the nglinkpin_share command).
        public static string LastShareError = "";

        // Per-machine record of the container IDs WE added to its linkedTo via the share pin, so we
        // can remove exactly those when sharing changes. Keyed by machine ID.
        // RUS: По каждой машине — ID контейнеров, которые МЫ добавили в её linkedTo через share-пин,
        // RUS: чтобы потом убрать ровно их. Ключ — ID машины.
        private static readonly Dictionary<ushort, HashSet<ushort>> _shareCache = new Dictionary<ushort, HashSet<ushort>>();

        public static void ClearShareCache() => _shareCache.Clear();

        // Makes containers shared via the share_containers pin actually appear in a fabricator's
        // linkedTo (one-directional: only the machine's list, never the container's — so the base
        // wire poll never sees a phantom link and there is no add/remove thrashing). Vanilla then
        // DISPLAYS those containers side-by-side and uses them as ingredients. 1-hop only: a machine
        // gets its share-partners' DIRECTLY-wired containers, not their transitively-shared ones.
        // Runs on ALL peers (server + clients) — derived purely from already-synced wires + linkedTo,
        // so every peer converges without extra networking. Idempotent.
        // RUS: Делает так, чтобы контейнеры, расшаренные пином share_containers, реально попадали в
        // RUS: linkedTo фабрикатора (одно-направленно — только в список машины, не контейнера, иначе
        // RUS: базовый поллинг увидит «фантомную» связь и начнётся дёрганье). Дальше ваниль их
        // RUS: ОТОБРАЖАЕТ и использует как ингредиенты. Только 1 уровень: машина получает ПРЯМО
        // RUS: подключённые контейнеры своих партнёров, не их транзитивные. Работает на всех пирах.
        public static void ReconcileShareLinks()
        {
            if (!IsInGame()) return;

            foreach (var item in Item.ItemList)
            {
                if (item == null) continue;
                var panel = item.GetComponent<ConnectionPanel>();
                if (panel == null || !panel.Connections.Any(c => c.Name == SHARE_PIN)) continue;

                var directPartners = new HashSet<ushort>(GetWiredPartnerIds(item));
                var desired = new HashSet<ushort>();

                foreach (var partner in GetSharePartners(item))
                {
                    var partnerDirect = new HashSet<ushort>(GetWiredPartnerIds(partner));
                    foreach (var e in partner.linkedTo)
                    {
                        if (!(e is Item c) || c == item) continue;
                        if (!partnerDirect.Contains(c.ID)) continue;        // only the partner's DIRECT links
                        if (directPartners.Contains(c.ID)) continue;        // already wired to us directly
                        if (c.GetComponent<ItemContainer>() == null) continue;
                        if (c.GetComponent<Fabricator>() != null || c.GetComponent<Deconstructor>() != null) continue;
                        desired.Add(c.ID);
                    }
                }

                _shareCache.TryGetValue(item.ID, out var current);

                foreach (var cid in desired)
                {
                    if (current != null && current.Contains(cid)) continue;
                    if (Entity.FindEntityByID(cid) is Item c && !IsAlreadyLinked(item, c))
                    {
                        item.AddLinked(c);
                        c.DisplaySideBySideWhenLinked = true; // ensure it shows in the machine's UI
                    }
                }
                if (current != null)
                {
                    foreach (var cid in current)
                    {
                        if (desired.Contains(cid)) continue;
                        if (Entity.FindEntityByID(cid) is Item c) { item.RemoveLinked(c); }
                    }
                }
                _shareCache[item.ID] = desired;
            }
        }

        // Diagnostic report: per fabricator/deconstructor — pins present, own linked containers, and
        // share partners with their container counts. Printed by the nglinkpin_share console command.
        // RUS: Диагностика: по каждому фабрикатору/деструктору — пины, свои связанные контейнеры и
        // RUS: партнёры по «общим контейнерам» с числом контейнеров. Печатает команда nglinkpin_share.
        public static string ShareDiag()
        {
            var sb = new System.Text.StringBuilder();
            int machines = 0;
            foreach (var item in Item.ItemList)
            {
                if (item == null) continue;
                if (item.GetComponent<Fabricator>() == null && item.GetComponent<Deconstructor>() == null) continue;
                machines++;
                var panel = item.GetComponent<ConnectionPanel>();
                bool invPin = panel?.Connections.Any(c => c.Name == PIN) ?? false;
                bool sharePin = panel?.Connections.Any(c => c.Name == SHARE_PIN) ?? false;
                int own = CountLinkedContainers(item);
                var partners = GetSharePartners(item);
                sb.Append($"[{item.ID}] {item.Prefab.Identifier} | invPin={invPin} sharePin={sharePin} | ownContainers={own} | sharePartners={partners.Count}");
                foreach (var p in partners)
                {
                    sb.Append($" -> [{p.ID}]{p.Prefab.Identifier}(containers={CountLinkedContainers(p)})");
                }
                sb.Append("\n");
            }
            if (machines == 0) sb.Append("(no fabricators/deconstructors in the world)\n");
            if (!string.IsNullOrEmpty(LastShareError)) sb.Append("LAST PATCH ERROR: " + LastShareError + "\n");
            return sb.ToString();
        }

        private static int CountLinkedContainers(Item machine)
        {
            int n = 0;
            foreach (var e in machine.linkedTo)
            {
                if (e is Item li && li.GetComponent<ItemContainer>() != null
                    && li.GetComponent<Fabricator>() == null && li.GetComponent<Deconstructor>() == null) { n++; }
            }
            return n;
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

        // Special allowedLinks token: makes IsLinkAllowed accept ANY item (ConnectionPanel.cs:296).
        private static readonly Identifier ItemLink = new Identifier("item");

        private static FieldInfo _allowedLinksField;
        private static void AddAllowedLinks(ItemPrefab prefab, params Identifier[] ids)
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
                foreach (var id in ids) { updated = updated.Add(id); }
                if (updated != current) { _allowedLinksField.SetValue(prefab, updated); }
            }
            catch { }
        }

        private static bool NameIs(XElement e, string name)
            => e.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase);

        // Mirrors ConnectionPanel.OnItemLoaded: the engine refuses to wire (and logs an error for)
        // an item that has a Dynamic physics body without an attachable Holdable. That's exactly the
        // held/worn items (weapons, armor, masks, magazines, ID cards) which merely happen to own an
        // <ItemContainer>. Installed furniture (Dynamic body + Holdable attachable="true") and
        // bodyless/static machines (fabricators) pass. Prevents panel spam on non-furniture.
        // RUS: Повторяет ConnectionPanel.OnItemLoaded: движок не даёт провязывать предмет с Dynamic-
        // RUS: телом без Holdable attachable — это носимое (оружие/броня/маски/магазины/удостоверения),
        // RUS: у которого просто есть <ItemContainer>. Мебель (Dynamic + Holdable attachable) и
        // RUS: бесстелесные/статичные машины (фабрикаторы) проходят.
        private static bool IsEngineWirable(XElement root)
        {
            var body = root.Elements().FirstOrDefault(e => NameIs(e, "Body"));
            if (body == null) return true;                                // no body — always wirable

            var bt = body.Attributes()
                .FirstOrDefault(a => a.Name.LocalName.Equals("bodytype", StringComparison.OrdinalIgnoreCase))?.Value;
            bool dynamicBody = string.IsNullOrEmpty(bt) || bt.Equals("Dynamic", StringComparison.OrdinalIgnoreCase);
            if (!dynamicBody) return true;                                // static/kinematic — wirable

            var holdable = root.Elements().FirstOrDefault(e => NameIs(e, "Holdable"));
            return holdable != null && HasAttr(holdable, "attachable", "true");
        }

        private static bool HasAttr(XElement e, string name, string value = null)
            => e.Attributes().Any(a => a.Name.LocalName.Equals(name, StringComparison.OrdinalIgnoreCase)
                && (value == null || a.Value.Equals(value, StringComparison.OrdinalIgnoreCase)));

        // A prefab you open/pry via a Door (locker doors, loading units) already uses the Action key for
        // open/pry, so a selectkey="Action" wiring panel would keep popping up over that interaction.
        // RUS: Предмет, открываемый/взламываемый через Door (двери локеров, loading units), уже занимает
        // RUS: клавишу Action под открытие/взлом — панель с selectkey="Action" всплывала бы поверх этого.
        private static bool HasPryableDoor(XElement root)
            => root.Elements().Any(e => NameIs(e, "Door"));

        // True if the panel already requires a screwdriver (its "items" list, comma-separated, contains it).
        // RUS: True, если панель уже требует отвёртку (в её списке "items" через запятую есть screwdriver).
        private static bool RequiresScrewdriver(XElement panel)
        {
            foreach (var ri in panel.Elements().Where(e => NameIs(e, "RequiredItem")))
            {
                var items = ri.Attributes()
                    .FirstOrDefault(a => a.Name.LocalName.Equals("items", StringComparison.OrdinalIgnoreCase))?.Value;
                if (items == null) continue;
                foreach (var it in items.Split(','))
                {
                    if (it.Trim().Equals("screwdriver", StringComparison.OrdinalIgnoreCase)) return true;
                }
            }
            return false;
        }

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
                    if (!IsEngineWirable(root)) continue;                 // held/worn item — engine can't wire it
                    if (HasPryableDoor(root)) continue;                   // pryable locker / loading unit: its Action key is open/pry — don't add an Action-key wiring panel

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
                    AddAllowedLinks(prefab, FabricatorLinks);   // #3: container shows side-by-side at fabricators

                    // Fabricators/deconstructors get the extra share_containers pin + "item" link
                    // (so a fabricator will display ANY linked container, whatever its identifier).
                    // RUS: Фабрикаторы/деструкторы получают пин share_containers + линк "item"
                    // RUS: (чтобы фабрикатор показывал ЛЮБОЙ связанный контейнер, независимо от id).
                    if (root.Elements().Any(e => NameIs(e, "Fabricator") || NameIs(e, "Deconstructor")))
                    {
                        AddPanelInput(root, SHARE_PIN);
                        AddAllowedLinks(prefab, ItemLink);
                    }
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

            // Guarantee the screwdriver gate on EVERY pin-bearing panel. The old guard only added it when the
            // panel had NO RequiredItem, so a container whose panel already carried a DIFFERENT RequiredItem
            // ended up selectable with no screwdriver (the reported "open the panel without a screwdriver").
            // RUS: Гарантируем гейт отвёртки на КАЖДОЙ панели с пином. Старый guard добавлял его лишь когда у
            // RUS: панели НЕТ RequiredItem — контейнер с ДРУГИМ RequiredItem открывался без отвёртки (баг).
            if (!RequiresScrewdriver(panel))
            {
                panel.Add(new XElement("RequiredItem",
                    new XAttribute("items", "screwdriver"),
                    new XAttribute("type", "Equipped")));
            }

            AddPanelInput(root, PIN);
        }

        // Adds a named input to the item's ConnectionPanel if it isn't already present.
        // RUS: Добавляет именованный input в ConnectionPanel предмета, если его ещё нет.
        private static void AddPanelInput(XElement root, string pin)
        {
            var panel = root.Elements().FirstOrDefault(e => NameIs(e, "ConnectionPanel"));
            if (panel == null) return;
            if (panel.Elements().Any(e => NameIs(e, "input") && HasAttr(e, "name", pin))) return;
            panel.Add(new XElement("input",
                new XAttribute("name", pin),
                new XAttribute("displayname", "connection." + pin),
                new XAttribute("maxwires", MAX_WIRES)));
        }
    }
}
