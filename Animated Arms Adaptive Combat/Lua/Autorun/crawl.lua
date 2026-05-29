AAAC = AAAC or {}

local afflictionCrawling = AfflictionPrefab.Prefabs[Identifier("aa_crawling")]
local afflictionTransition = AfflictionPrefab.Prefabs[Identifier("aa_crawling-1")]
local disablePrefab = AfflictionPrefab.Prefabs[Identifier("aaac_disable_crawl")]
local toggleState = {}
local lastToggleTime = {}
local transitionStartTime = {}
local TOGGLE_DELAY = 1.0
local TOGGLE_KEY_DEFAULT = {"M"}
local DISABLE_AFFLICTION = "aaac_disable_crawl"
local ENFORCER_ITEM = "aaac_crawl_enforcer"
local ENFORCER_INTERVAL = 0.75
local ENFORCER_COOLDOWN = 2.0
local pendingSpawnUntil = {}
local enforcerPrefab = nil
local enforcerTimer = 0

local function featureEnabled()
    return true
end

local function getId(character, client)
    if client and client.AccountId then
        return client.AccountId.StringRepresentation
    end
    if character then
        return tostring(character.ID)
    end
    return "LocalPlayer"
end

local function getAffliction(character, identifier)
    if not character or not character.CharacterHealth then return nil end
    local affliction = nil
    pcall(function() affliction = character.CharacterHealth.GetAffliction(identifier, false) end)
    if affliction == nil then pcall(function() affliction = character.CharacterHealth.GetAffliction(identifier, true) end) end
    if affliction == nil then pcall(function() affliction = character.CharacterHealth.GetAffliction(identifier) end) end
    return affliction
end

local function afflictionStrength(character, identifier)
    local affliction = getAffliction(character, identifier)
    if not affliction then return 0 end
    local strength = 0
    pcall(function() strength = tonumber(affliction.Strength) or 0 end)
    return strength
end

local function hasDisableMarker(character)
    return false
end

local function clearSingleAffliction(character, identifier)
    if not character or not character.CharacterHealth then return end
    pcall(function() character.CharacterHealth.ReduceAffliction(identifier, 1000000) end)
    pcall(function() character.CharacterHealth.ReduceAfflictionOnAllLimbs(identifier, 1000000) end)
    local affliction = getAffliction(character, identifier)
    if affliction then
        pcall(function() affliction.SetStrength(0) end)
        pcall(function() affliction.Strength = 0 end)
        pcall(function() affliction.NonClampedStrength = 0 end)
        pcall(function() affliction.Duration = 0 end)
    end
end

local function clearCrawlAfflictions(character)
    clearSingleAffliction(character, "aa_crawling-1")
    clearSingleAffliction(character, "aa_crawling")
    clearSingleAffliction(character, "aa_crawling-1-gas")
    clearSingleAffliction(character, "aa_crawling-gas")
end

local function applyDisableMarker(character)
end

local function removeDisableMarker(character)
end

local function hasAffliction(character, identifier)
    return afflictionStrength(character, identifier) > 0
end

local function isBusy(character, id)
    if toggleState[id] ~= nil then return true end
    return hasAffliction(character, "aa_crawling-1") or hasAffliction(character, "aa_crawling-1-gas")
end

local function isProne(character)
    return hasAffliction(character, "aa_crawling") or hasAffliction(character, "aa_crawling-gas")
end

local function crawlDisabled(character)
    return false
end

local function broadcastCharacter(character)
    if SERVER and character then
        pcall(function()
            Networking.CreateEntityEvent(character, Character.CharacterStatusEventData.__new(true))
        end)
    end
end

local function detachFromParent(it)
    if it == nil then return end
    local pinv = nil
    pcall(function() pinv = it.ParentInventory end)
    if pinv ~= nil then
        pcall(function() pinv.RemoveItem(it, true) end)
        pcall(function() pinv.RemoveItem(it, false, true) end)
        pcall(function() pinv.RemoveItem(it) end)
    end
end

local function forcePutInto(inv, it)
    if inv == nil or it == nil then return false end
    detachFromParent(it)
    local ok, res = pcall(function() return inv.TryPutItem(it, nil) end)
    if ok and res == true then return true end
    local cap = 0
    pcall(function() cap = tonumber(inv.Capacity) or 0 end)
    for slot = 0, cap - 1 do
        local ok2, res2 = pcall(function() return inv.TryPutItem(it, slot, true, false, nil, true) end)
        if ok2 and res2 == true then return true end
        local ok3, res3 = pcall(function() return inv.TryPutItem(it, slot, true, false, nil) end)
        if ok3 and res3 == true then return true end
    end
    return false
end

local function spawnEnforcer(character)
end

local function removeEnforcers(character)
end

local function startTransition(character, client, targetProne)
    if not character or character.Removed or character.IsDead or not character.IsHuman then return end
    local id = getId(character, client)
    if isBusy(character, id) then return end
    toggleState[id] = targetProne
    transitionStartTime[id] = Timer.Time
    lastToggleTime[id] = Timer.Time
    clearCrawlAfflictions(character)
    if afflictionTransition then
        character.CharacterHealth.ApplyAffliction(character.AnimController.MainLimb, afflictionTransition.Instantiate(1000000))
    end
    broadcastCharacter(character)
end

local function completeTransition(character, client)
    if not character or character.Removed or character.IsDead or not character.IsHuman then return end
    local id = getId(character, client)
    local targetProne = toggleState[id]
    if targetProne == nil then return end
    if Timer.Time - (transitionStartTime[id] or 0) < TOGGLE_DELAY then return end
    clearCrawlAfflictions(character)
    if targetProne and not crawlDisabled(character) and afflictionCrawling then
        character.CharacterHealth.ApplyAffliction(character.AnimController.MainLimb, afflictionCrawling.Instantiate(1000000))
    end
    toggleState[id] = nil
    transitionStartTime[id] = nil
    broadcastCharacter(character)
end

local function tryToggle(character, client)
    if not character or character.Removed or character.IsDead or not character.IsHuman then return end
    if crawlDisabled(character) then
        clearCrawlAfflictions(character)
        return
    end
    local id = getId(character, client)
    local now = Timer.Time
    if lastToggleTime[id] and now - lastToggleTime[id] < TOGGLE_DELAY then return end
    if isBusy(character, id) then return end
    startTransition(character, client, not isProne(character))
end

if CLIENT then
    Hook.Add("keyUpdate", "AAAC.CrawlToggle.Client", function()
        if not Game.RoundStarted then return end
        if GUI.KeyboardDispatcher.Subscriber ~= nil then return end
        local character = Character.Controlled
        if not character then return end
        if crawlDisabled(character) then
            clearCrawlAfflictions(character)
            return
        end
        if not AAAC.IsConfiguredKeyHit("CrawlToggle", TOGGLE_KEY_DEFAULT) then return end
        if Game.IsMultiplayer then
            local msg = Networking.Start("AAACCrawlToggle")
            if msg then Networking.Send(msg) end
        else
            tryToggle(character)
        end
    end)
end

if SERVER and Game.IsMultiplayer then
    Networking.Receive("AAACCrawlToggle", function(msg, client)
        if client and client.Character then
            tryToggle(client.Character, client)
        end
    end)
end

local function applyDisabledState(character, client)
end

local function resolveCharacterFromHealth(characterOrHealth)
    if characterOrHealth == nil then return nil end
    local character = nil
    pcall(function()
        if characterOrHealth.Character ~= nil then character = characterOrHealth.Character end
    end)
    if character ~= nil then return character end
    pcall(function()
        if characterOrHealth.CharacterHealth ~= nil then character = characterOrHealth end
    end)
    return character
end

local watchedIdentifiers = {
    ["aa_crawling"] = true,
    ["aa_crawling-1"] = true,
    ["aa_crawling-gas"] = true,
    ["aa_crawling-1-gas"] = true,
}

local function scheduleClear(character)
end


local function processCharacters(callback)
    if SERVER and Game.IsMultiplayer then
        local clients = Client.ClientList
        if clients then
            for _, client in pairs(clients) do
                if client and client.Character then callback(client.Character, client) end
            end
        end
    else
        local character = Character.Controlled
        if character then callback(character, nil) end
    end
end

if SERVER then
    Hook.Add("think", "AAAC.CrawlToggle.ServerThink", function()
        if not Game.RoundStarted then return end
        processCharacters(function(character, client)
            completeTransition(character, client)
            applyDisabledState(character, client)
        end)
        local now = Timer.GetTime()
        if not featureEnabled() and now - enforcerTimer >= ENFORCER_INTERVAL then
            enforcerTimer = now
            processCharacters(function(character)
                spawnEnforcer(character)
            end)
        end
    end)
elseif CLIENT and not Game.IsMultiplayer then
    Hook.Add("think", "AAAC.CrawlToggle.SingleplayerThink", function()
        if not Game.RoundStarted then return end
        completeTransition(Character.Controlled)
        applyDisabledState(Character.Controlled)
    end)
end
