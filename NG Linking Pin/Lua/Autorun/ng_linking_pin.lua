-- ng_linking_pin.lua
-- NG Linking Pin — runtime driver. Standalone replacement for "Linking Pin Lua".
--   * Prefab prep (selectable panel + inventory_link pin) is done in C# (LinkingPinHelper).
--   * Here: build the target set, detect wires (ALL partners — #3), maintain linkedTo, sync in MP,
--     and wire up pre-existing (sub-editor) links.
-- No print() in polling paths; debug output is in the commands at the bottom.
-- RUS: Среда выполнения NG Linking Pin. Подготовка префабов — в C#. Здесь: набор целей, детект
-- RUS: проводов (ВСЕ партнёры — #3), ведение linkedTo, MP-синхронизация, провода для уже связанных.

LuaUserData.RegisterType("NGLinkingPin.LinkingPinHelper")
local Helper = LuaUserData.CreateStatic("NGLinkingPin.LinkingPinHelper")

local NET_ADD    = "nglinkpin.add"
local NET_REMOVE = "nglinkpin.remove"

InventoryWiringTargets = {}

-- ---------------------------------------------------------------------------
-- Helpers
-- ---------------------------------------------------------------------------

local function prepare()
    pcall(function() Helper.PrepareContainerPrefabs() end)
end

local function buildTargetTable()
    local t = {}
    for _, item in pairs(Item.ItemList) do
        pcall(function()
            if Helper.HasInventoryPin(item) and Helper.IsEligible(item) then
                t[item.ID] = true
            end
        end)
    end
    InventoryWiringTargets = t
end

-- Parse the CSV partner-id list returned by C# into a set { [id]=true }.
local function partnerSet(item)
    local set = {}
    local csv = Helper.GetWiredPartnersCSV(item)
    if csv ~= nil and csv ~= "" then
        for token in string.gmatch(tostring(csv), "([^,]+)") do
            local id = tonumber(token)
            if id ~= nil then set[id] = true end
        end
    end
    return set
end

-- ---------------------------------------------------------------------------
-- Link apply / remove (+ MP broadcast from server)
-- ---------------------------------------------------------------------------

local function applyLink(a, b)
    if not Helper.IsAlreadyLinked(a, b) then
        a.AddLinked(b)
        b.AddLinked(a)
    end
    a.DisplaySideBySideWhenLinked = Helper.ShouldDisplaySideBySide(a)
    b.DisplaySideBySideWhenLinked = Helper.ShouldDisplaySideBySide(b)
end

local function removeLink(a, b)
    a.RemoveLinked(b)
    b.RemoveLinked(a)
end

local function handleLinkChange(idA, idB, addLink)
    local a = Entity.FindEntityByID(idA)
    local b = Entity.FindEntityByID(idB)
    if a == nil or b == nil then return end

    if addLink then applyLink(a, b) else removeLink(a, b) end

    if SERVER and Game ~= nil and Game.IsMultiplayer then
        local msg = Networking.Start(addLink and NET_ADD or NET_REMOVE)
        msg.WriteUInt16(UShort(idA))
        msg.WriteUInt16(UShort(idB))
        Networking.Send(msg)
    end
end

-- ---------------------------------------------------------------------------
-- Link poll — multi-partner (#3): each item tracks the SET of its wired partners.
-- ---------------------------------------------------------------------------

local wireState = {}  -- wireState[itemId] = { [partnerId]=true, ... }
local linkPollErrors, injectPollErrors = 0, 0

local function runLinkPoll()
    if not Helper.IsInGame() then return end
    if Game ~= nil and Game.IsMultiplayer and not SERVER then return end

    for itemId, _ in pairs(InventoryWiringTargets) do
        local item = Entity.FindEntityByID(itemId)
        local current = {}
        if item ~= nil then current = partnerSet(item) end

        local old = wireState[itemId] or {}
        for pid, _ in pairs(current) do
            if not old[pid] then handleLinkChange(itemId, pid, true) end
        end
        for pid, _ in pairs(old) do
            if not current[pid] then handleLinkChange(itemId, pid, false) end
        end
        wireState[itemId] = current
    end

    -- Items that left the target set: drop all their links.
    for itemId, old in pairs(wireState) do
        if InventoryWiringTargets[itemId] == nil then
            for pid, _ in pairs(old) do handleLinkChange(itemId, pid, false) end
            wireState[itemId] = nil
        end
    end
end

-- ---------------------------------------------------------------------------
-- Polling loops
-- ---------------------------------------------------------------------------

local LINK_MS, INJECT_MS = 1000, 5000

local function scheduleLinkPoll()
    Timer.Wait(function()
        local ok = pcall(runLinkPoll)
        if not ok then linkPollErrors = linkPollErrors + 1 end
        scheduleLinkPoll()
    end, LINK_MS)
end

local function scheduleInjectPoll()
    Timer.Wait(function()
        local ok = pcall(function()
            buildTargetTable()
            Helper.SpawnWiresForPreexistingLinks()
        end)
        if not ok then injectPollErrors = injectPollErrors + 1 end
        scheduleInjectPoll()
    end, INJECT_MS)
end

-- ---------------------------------------------------------------------------
-- Startup
-- ---------------------------------------------------------------------------

prepare()
buildTargetTable()
scheduleLinkPoll()
scheduleInjectPoll()

Hook.Add("roundStart", "nglinkpin.reset", function()
    wireState = {}
    pcall(function()
        prepare()
        buildTargetTable()
    end)
end)

-- ---------------------------------------------------------------------------
-- MP client receive handlers
-- ---------------------------------------------------------------------------

local function tryRegisterNetHandlers()
    if not CLIENT then return end
    if Game == nil or not Game.IsMultiplayer then return end

    Networking.Receive(NET_ADD, function(msg)
        local a = Entity.FindEntityByID(msg.ReadUInt16())
        local b = Entity.FindEntityByID(msg.ReadUInt16())
        if a ~= nil and b ~= nil then applyLink(a, b) end
    end)

    Networking.Receive(NET_REMOVE, function(msg)
        local a = Entity.FindEntityByID(msg.ReadUInt16())
        local b = Entity.FindEntityByID(msg.ReadUInt16())
        if a ~= nil and b ~= nil then removeLink(a, b) end
    end)
end

pcall(tryRegisterNetHandlers)

-- ---------------------------------------------------------------------------
-- Debug commands
-- ---------------------------------------------------------------------------

_G.nglinkpin_list = function()
    local ok, err = pcall(function()
        local list = Helper.LastMarked
        local count = list.Count
        print("[NG] [Linking Pin] wirable container prefabs (" .. tostring(count) .. "):")
        for i = 0, count - 1 do print("  - " .. tostring(list[i])) end
        if count == 0 then print("  (none)") end
    end)
    if not ok then print("[NG] [Linking Pin] list error: " .. tostring(err)) end
end

_G.nglinkpin_status = function()
    local n = 0
    for _ in pairs(InventoryWiringTargets) do n = n + 1 end
    print("[NG] [Linking Pin] === STATUS ===")
    print("  wiring targets : " .. tostring(n))
    print("  poll errors    : link=" .. tostring(linkPollErrors) .. "  inject=" .. tostring(injectPollErrors))
    print("  live wired pairs:")
    local found = false
    for itemId, _ in pairs(InventoryWiringTargets) do
        local item = Entity.FindEntityByID(itemId)
        if item ~= nil then
            local set = partnerSet(item)
            for pid, _ in pairs(set) do
                local p = Entity.FindEntityByID(pid)
                found = true
                print(string.format("    [%d]%s -> [%d]%s",
                    itemId, tostring(item.Prefab.Identifier),
                    pid, p ~= nil and tostring(p.Prefab.Identifier) or "?"))
            end
        end
    end
    if not found then print("    (none)") end
end

_G.nglinkpin_reapply = function()
    local ok, n = pcall(function() return Helper.PrepareContainerPrefabs() end)
    if ok then print("[NG] [Linking Pin] prefabs prepared; wirable containers: " .. tostring(n))
    else print("[NG] [Linking Pin] error: " .. tostring(n)) end
    pcall(buildTargetTable)
end
