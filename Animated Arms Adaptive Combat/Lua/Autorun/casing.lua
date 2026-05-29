-- ╔══════════════════════════════════════════╗
-- ║       Casing Limiter — Barotrauma        ║
-- ║  Лимит только на выброшенные гильзы      ║
-- ╚══════════════════════════════════════════╝

-- Максимум ВЫБРОШЕННЫХ гильз в мире одновременно.
-- Гильзы в инвентарях/ящиках не считаются и не удаляются.
local MAX_DROPPED_CASINGS = 10

local casingQueue     = {}   -- все известные гильзы
local pendingRemovals = {}
local isProcessing    = false

-- ──────────────────────────────────────────
local casingId = nil
local function HasCasingTag(item)
    if not item then return false end
    local ok, result = pcall(function()
        if casingId == nil then casingId = Identifier("casing") end
        return item.HasTag(casingId)
    end)
    if ok and result then return true end
    local ok2, r2 = pcall(function()
        return string.find(tostring(item.Tags or ""), "casing") ~= nil
    end)
    return ok2 and r2 == true
end

local function IsAlive(item)
    if not item then return false end
    local ok, removed = pcall(function() return item.Removed end)
    return ok and not removed
end

local function IsDropped(item)
    if not item then return false end
    local ok, result = pcall(function()
        return item.ParentInventory == nil
    end)
    return ok and result == true
end

-- ──────────────────────────────────────────
local function SafeRemove(item)
    if not IsAlive(item) then return end
    local done = false
    pcall(function() Entity.Spawner.AddEntityToRemoveQueue(item); done = true end)
    if not done then pcall(function() item.Remove() end) end
end

-- ──────────────────────────────────────────
local function ProcessRemovals()
    if isProcessing then return end
    isProcessing = true

    local function RemoveBatch()
        for i = #pendingRemovals, 1, -1 do
            if not IsAlive(pendingRemovals[i]) then table.remove(pendingRemovals, i) end
        end

        if #pendingRemovals == 0 then isProcessing = false; return end

        local count = math.min(5, #pendingRemovals)
        for i = 1, count do
            SafeRemove(table.remove(pendingRemovals, 1))
        end

        if #pendingRemovals > 0 then Timer.Wait(RemoveBatch, 50) else isProcessing = false end
    end

    RemoveBatch()
end

-- ──────────────────────────────────────────
-- Считаем только выброшенные гильзы (не в инвентаре)
local function CountDropped()
    local n = 0
    for _, item in ipairs(casingQueue) do
        if IsAlive(item) and IsDropped(item) then
            n = n + 1
        end
    end
    return n
end

-- Удаляем старые выброшенные гильзы пока их больше лимита
local function EnqueueExcess()
    while CountDropped() > MAX_DROPPED_CASINGS do
        local found = false
        for i = 1, #casingQueue do
            local item = casingQueue[i]
            if IsAlive(item) and IsDropped(item) then
                table.remove(casingQueue, i)
                table.insert(pendingRemovals, item)
                found = true
                break
            end
        end
        if not found then break end
    end

    if #pendingRemovals > 0 and not isProcessing then
        ProcessRemovals()
    end
end

-- ──────────────────────────────────────────
Hook.Add("item.created", "CasingLimiter.OnCreated", function(item)
    if not item then return end
    if not HasCasingTag(item) then return end
    table.insert(casingQueue, item)
    EnqueueExcess()
end)

-- ──────────────────────────────────────────
local function ScheduleCleanup()
    Timer.Wait(function()
        for i = #casingQueue, 1, -1 do
            if not IsAlive(casingQueue[i]) then table.remove(casingQueue, i) end
        end
        for i = #pendingRemovals, 1, -1 do
            if not IsAlive(pendingRemovals[i]) then table.remove(pendingRemovals, i) end
        end
        ScheduleCleanup()
    end, 5000)
end
ScheduleCleanup()

-- ──────────────────────────────────────────
local function InitCasingQueue()
    casingQueue     = {}
    pendingRemovals = {}
    isProcessing    = false

    local found = 0
    pcall(function()
        for _, item in pairs(Item.ItemList) do
            if IsAlive(item) and HasCasingTag(item) then
                table.insert(casingQueue, item)
                found = found + 1
            end
        end
    end)

    local dropped = CountDropped()
    print(string.format("[CasingLimiter] Гильз всего: %d | выброшено: %d | лимит: %d",
        found, dropped, MAX_DROPPED_CASINGS))

    EnqueueExcess()
end

Timer.Wait(InitCasingQueue, 1000)
Hook.Add("roundStart", "CasingLimiter.RoundStart", function()
    Timer.Wait(InitCasingQueue, 1000)
end)