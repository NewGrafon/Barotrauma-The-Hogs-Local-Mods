local MOD_PATH = table.pack(...)[1]

if CLIENT and Game.IsMultiplayer and not SERVER then return end

AAAC = AAAC or {}
AAAC.AfflictionTools = AAAC.AfflictionTools or {}

local DEBUG = false
local function dbg(msg)
    if DEBUG then print('[AAAC DISEASE] ' .. tostring(msg)) end
end

local MARKERS = {
    spasm_shot = {
        setting = 'EnableShotPain',
        marker = 'aaac_disable_shotpain',
        item = 'aaac_shotpain_enforcer'
    },
    tinnitus_shot = {
        setting = 'EnableShotTinnitus',
        marker = 'aaac_disable_shottinnitus',
        item = 'aaac_shottinnitus_enforcer'
    }
}

local WATCHED_AFFLICTIONS = {
    spasm_shot = 'EnableShotPain',
    tinnitus_shot = 'EnableShotTinnitus',
    stamina = 'EnableStamina'
}

local UPDATE_INTERVAL = 0.75
local SPAWN_COOLDOWN = 2.0
local nextUpdateTime = 0
local pendingSpawnUntil = {}
local itemPrefabs = {}
local settingsLoadAttempted = false
local currentFlags = {
    EnableShotPain = true,
    EnableShotTinnitus = true,
    EnableStamina = true
}
local lastFlagSignature = nil

local function now()
    local ok, value = pcall(function()
        if Timer ~= nil and Timer.GetTime ~= nil then return Timer.GetTime() end
        if Game ~= nil and Game.GameTime ~= nil then return Game.GameTime end
        return os.clock()
    end)
    if ok and type(value) == 'number' then return value end
    return os.clock()
end

local function tryDofile(path)
    local ok, result = pcall(function() return dofile(path) end)
    if ok and type(result) == 'table' then return result end
    if _G.AAAC ~= nil and type(_G.AAAC) == 'table' then return _G.AAAC end
    return nil
end

local function ensureSettingsLoaded(force)
    if AAAC ~= nil and type(AAAC.GetServerConfig) == 'function' then return true end
    if settingsLoadAttempted and not force then return false end
    settingsLoadAttempted = true

    local tryPaths = {}
    if MOD_PATH ~= nil then
        table.insert(tryPaths, tostring(MOD_PATH) .. '/Lua/aaac_settings.lua')
    end
    local sourcePath = nil
    pcall(function() sourcePath = debug.getinfo(1, 'S').source end)
    if type(sourcePath) == 'string' and sourcePath ~= '' then
        local cleaned = sourcePath:gsub('^@', '')
        local root = cleaned:gsub('[\\/]+Lua[\\/]+Autorun[\\/]+affliction_control%.lua$', '')
        if root ~= cleaned then
            table.insert(tryPaths, root .. '/Lua/aaac_settings.lua')
        end
    end

    for _, path in ipairs(tryPaths) do
        local result = tryDofile(path)
        if type(result) == 'table' and type(result.GetServerConfig) == 'function' then
            AAAC = result
            break
        end
    end

    if AAAC ~= nil and type(AAAC.GetServerConfig) == 'function' and not AAAC.__aaacDiseaseHookInstalled then
        local previous = AAAC.OnServerConfigChanged
        AAAC.OnServerConfigChanged = function(config)
            if type(previous) == 'function' then pcall(previous, config) end
            if type(config) == 'table' then
                currentFlags.EnableShotPain = config.EnableShotPain ~= false
                currentFlags.EnableShotTinnitus = config.EnableShotTinnitus ~= false
                currentFlags.EnableStamina = config.EnableStamina ~= false
            end
            nextUpdateTime = 0
        end
        AAAC.__aaacDiseaseHookInstalled = true
    end

    return AAAC ~= nil and type(AAAC.GetServerConfig) == 'function'
end

local function readServerConfigFromFile()
    local path = nil
    if AAAC ~= nil and type(AAAC.ServerConfigPath) == 'string' then
        path = AAAC.ServerConfigPath
    else
        path = Game.SaveFolder .. '/ModConfigs/AAAC_ServerSettings.json'
    end
    local cachePath = nil
    if AAAC ~= nil and type(AAAC.ServerConfigCachePath) == 'string' then
        cachePath = AAAC.ServerConfigCachePath
    else
        cachePath = Game.SaveFolder .. '/ModConfigs/AAAC_ServerSettings_ClientCache.json'
    end

    local function load(pathToRead)
        if pathToRead == nil then return nil end
        if not File.Exists(pathToRead) then return nil end
        local ok, parsed = pcall(function() return json.parse(File.Read(pathToRead)) end)
        if ok and type(parsed) == 'table' then return parsed end
        return nil
    end

    return load(path) or load(cachePath)
end

local function updateFlags()
    ensureSettingsLoaded(false)
    local cfg = nil

    if AAAC ~= nil and type(AAAC.GetServerConfig) == 'function' then
        local ok, result = pcall(function() return AAAC.GetServerConfig() end)
        if ok and type(result) == 'table' then cfg = result end
    end

    if cfg == nil then
        cfg = readServerConfigFromFile()
    end

    local pain = true
    local tinnitus = true
    local stamina = true
    if type(cfg) == 'table' then
        if type(cfg.EnableShotPain) == 'boolean' then pain = cfg.EnableShotPain end
        if type(cfg.EnableShotTinnitus) == 'boolean' then tinnitus = cfg.EnableShotTinnitus end
        if type(cfg.EnableStamina) == 'boolean' then stamina = cfg.EnableStamina end
    end

    currentFlags.EnableShotPain = pain
    currentFlags.EnableShotTinnitus = tinnitus
    currentFlags.EnableStamina = stamina

    local sig = tostring(pain) .. '|' .. tostring(tinnitus) .. '|' .. tostring(stamina)
    if sig ~= lastFlagSignature then
        lastFlagSignature = sig
        print('[AAAC] Disease toggles -> pain=' .. tostring(pain) .. ' tinnitus=' .. tostring(tinnitus) .. ' stamina=' .. tostring(stamina))
    end
end

local function getCharacterKey(character)
    if character == nil then return nil end
    local key = nil
    pcall(function() key = tostring(character.ID) end)
    if key == nil or key == 'nil' then key = tostring(character) end
    return key
end

local function isValidCharacter(character)
    if character == nil then return false end
    local ok = true
    pcall(function()
        if character.Removed or character.IsDead then ok = false end
        if character.CharacterHealth == nil or character.Inventory == nil then ok = false end
    end)
    return ok
end

local function getAffliction(character, identifier)
    if not isValidCharacter(character) then return nil end
    local aff = nil
    pcall(function() aff = character.CharacterHealth.GetAffliction(identifier, false) end)
    if aff == nil then pcall(function() aff = character.CharacterHealth.GetAffliction(identifier, true) end) end
    if aff == nil then pcall(function() aff = character.CharacterHealth.GetAffliction(identifier) end) end
    return aff
end

local function hasMarker(character, identifier)
    local aff = getAffliction(character, identifier)
    if aff == nil then return false end
    local strength = 0
    pcall(function() strength = tonumber(aff.Strength) or 0 end)
    return strength > 0
end

local function clearAffliction(character, identifier)
    if not isValidCharacter(character) then return end
    local aff = getAffliction(character, identifier)
    if aff ~= nil then
        pcall(function() aff.SetStrength(0) end)
        pcall(function() aff.Strength = 0 end)
        pcall(function() aff.NonClampedStrength = -1000000 end)
    end
    pcall(function() character.CharacterHealth.ReduceAffliction(identifier, 1000000) end)
    pcall(function() character.CharacterHealth.ReduceAfflictionOnAllLimbs(identifier, 1000000) end)
end

local function hasInventoryItem(character, identifier)
    if not isValidCharacter(character) then return false end
    local found = false
    pcall(function()
        for item in character.Inventory.AllItems do
            if item ~= nil and item.Prefab ~= nil then
                local itemId = tostring(item.Prefab.Identifier.Value or item.Prefab.Identifier)
                if itemId == identifier then
                    found = true
                    return
                end
            end
        end
    end)
    return found
end

local function ensurePrefabLoaded(identifier)
    if itemPrefabs[identifier] ~= nil then return itemPrefabs[identifier] end
    local prefab = nil
    pcall(function() prefab = ItemPrefab.GetItemPrefab(identifier) end)
    itemPrefabs[identifier] = prefab
    return prefab
end

local function invCap(inv)
    local c = 0
    pcall(function() c = tonumber(inv.Capacity) or 0 end)
    return c
end

local function invTryPut(inv, it, slot)
    if inv == nil or it == nil then return false end
    if slot ~= nil then
        local ok, res = pcall(function() return inv.TryPutItem(it, slot, true, false, nil, true) end)
        if ok and res == true then return true end
        local ok2, res2 = pcall(function() return inv.TryPutItem(it, slot, true, false, nil) end)
        if ok2 and res2 == true then return true end
    end
    local ok3, res3 = pcall(function() return inv.TryPutItem(it, nil) end)
    return ok3 and res3 == true or false
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
    if invTryPut(inv, it, nil) then return true end
    local cap = invCap(inv)
    for slot = 0, cap - 1 do
        if invTryPut(inv, it, slot) then return true end
    end
    return false
end

local function spawnEnforcer(character, entry)
    if not isValidCharacter(character) then return end
    local charKey = getCharacterKey(character)
    if charKey == nil then return end

    local pendingKey = charKey .. ':' .. entry.item
    local t = now()
    if pendingSpawnUntil[pendingKey] ~= nil and pendingSpawnUntil[pendingKey] > t then return end
    if hasMarker(character, entry.marker) then return end
    if hasInventoryItem(character, entry.item) then return end

    local prefab = ensurePrefabLoaded(entry.item)
    if prefab == nil then return end

    pendingSpawnUntil[pendingKey] = t + SPAWN_COOLDOWN
    pcall(function()
        Entity.Spawner.AddItemToSpawnQueue(prefab, character.WorldPosition, nil, nil, function(newItem)
            if not isValidCharacter(character) or newItem == nil or newItem.Removed then return end
            local putOk = forcePutInto(character.Inventory, newItem)
            dbg('spawn callback ' .. tostring(entry.item) .. ' put=' .. tostring(putOk))
            if not putOk then
                Timer.Wait(function()
                    if newItem ~= nil and not newItem.Removed then
                        pcall(function() newItem.Remove() end)
                    end
                end, 300)
            end
        end)
    end)
end

local function removeInventoryItems(character, identifier)
    if not isValidCharacter(character) then return end
    local toRemove = {}
    pcall(function()
        for item in character.Inventory.AllItems do
            if item ~= nil and item.Prefab ~= nil then
                local itemId = tostring(item.Prefab.Identifier.Value or item.Prefab.Identifier)
                if itemId == identifier then
                    table.insert(toRemove, item)
                end
            end
        end
    end)
    for _, item in ipairs(toRemove) do
        pcall(function()
            Entity.Spawner.AddEntityToRemoveQueue(item)
        end)
        pcall(function()
            item.Remove()
        end)
    end
end

local function removeMarker(character, entry)
    if not isValidCharacter(character) then return end
    clearAffliction(character, entry.marker)
    removeInventoryItems(character, entry.item)
    local charKey = getCharacterKey(character)
    if charKey ~= nil then
        pendingSpawnUntil[charKey .. ':' .. entry.item] = nil
    end
end

local function processCharacter(character)
    if not isValidCharacter(character) then return end

    if currentFlags.EnableShotPain == false then
        spawnEnforcer(character, MARKERS.spasm_shot)
        clearAffliction(character, 'spasm_shot')
    else
        removeMarker(character, MARKERS.spasm_shot)
    end
    if currentFlags.EnableShotTinnitus == false then
        spawnEnforcer(character, MARKERS.tinnitus_shot)
        clearAffliction(character, 'tinnitus_shot')
    else
        removeMarker(character, MARKERS.tinnitus_shot)
    end
    if currentFlags.EnableStamina == false then
        clearAffliction(character, 'stamina')
    end
end

local function resolveCharacterFromHealth(characterOrHealth)
    if isValidCharacter(characterOrHealth) then return characterOrHealth end
    if characterOrHealth == nil then return nil end

    local resolved = nil
    pcall(function()
        for character in Character.CharacterList do
            if character ~= nil and character.CharacterHealth == characterOrHealth then
                resolved = character
                return
            end
        end
    end)
    return resolved
end

local function getAfflictionIdentifier(affliction)
    if affliction == nil then return nil end
    local id = nil
    pcall(function()
        if affliction.Identifier ~= nil then
            id = tostring(affliction.Identifier.Value or affliction.Identifier)
        end
    end)
    if id == nil then
        pcall(function() id = tostring(affliction.Prefab.Identifier.Value or affliction.Prefab.Identifier) end)
    end
    return id
end

local function shouldClearAffliction(identifier)
    local flagKey = WATCHED_AFFLICTIONS[identifier]
    if flagKey == nil then return false end
    return currentFlags[flagKey] == false
end

local function scheduleImmediateClear(character, identifier)
    if not isValidCharacter(character) or not shouldClearAffliction(identifier) then return end
    clearAffliction(character, identifier)
    Timer.Wait(function()
        if shouldClearAffliction(identifier) then clearAffliction(character, identifier) end
    end, 1)
    Timer.Wait(function()
        if shouldClearAffliction(identifier) then clearAffliction(character, identifier) end
    end, 25)
    Timer.Wait(function()
        if shouldClearAffliction(identifier) then clearAffliction(character, identifier) end
    end, 100)
end

local function processAllPlayerCharacters()
    local seen = {}

    if Game.IsMultiplayer and Client ~= nil and Client.ClientList ~= nil then
        pcall(function()
            for client in Client.ClientList do
                if client ~= nil and client.Character ~= nil then
                    local character = client.Character
                    local key = getCharacterKey(character)
                    if key ~= nil and not seen[key] then
                        seen[key] = true
                        processCharacter(character)
                    end
                end
            end
        end)
    end

    if Character.Controlled ~= nil then
        local key = getCharacterKey(Character.Controlled)
        if key ~= nil and not seen[key] then
            seen[key] = true
            processCharacter(Character.Controlled)
        end
    end

    if not Game.IsMultiplayer then
        pcall(function()
            for character in Character.CharacterList do
                local key = getCharacterKey(character)
                if key ~= nil and not seen[key] then
                    local isPlayer = false
                    pcall(function() isPlayer = character.IsPlayer end)
                    if isPlayer then
                        seen[key] = true
                        processCharacter(character)
                    end
                end
            end
        end)
    end
end

Hook.Add('roundStart', 'AAAC.AfflictionTools.RefreshFlags', function()
    settingsLoadAttempted = false
    ensureSettingsLoaded(true)
    updateFlags()
    nextUpdateTime = 0
    pendingSpawnUntil = {}
end)

Hook.Add('think', 'AAAC.AfflictionTools.EnforcerSpawnLoop', function()
    if Game.Paused or not Game.RoundStarted then return end
    local t = now()
    if t < nextUpdateTime then return end
    updateFlags()
    processAllPlayerCharacters()
    nextUpdateTime = t + UPDATE_INTERVAL
end)

Hook.Add('character.applyAffliction', 'AAAC.AfflictionTools.ClearDisabledStaminaFast', function(characterOrHealth, limbHealth, affliction)
    updateFlags()
    local identifier = getAfflictionIdentifier(affliction)
    if identifier == nil or not shouldClearAffliction(identifier) then return end
    local character = resolveCharacterFromHealth(characterOrHealth)
    if character ~= nil then
        scheduleImmediateClear(character, identifier)
    end
end)

Hook.Add('character.applyDamage', 'AAAC.AfflictionTools.ClearDisabledStaminaFastDamage', function(character, attackResult)
    updateFlags()
    if not isValidCharacter(character) or attackResult == nil or attackResult.Afflictions == nil then return end
    pcall(function()
        for affliction in attackResult.Afflictions do
            local identifier = getAfflictionIdentifier(affliction)
            if identifier ~= nil and shouldClearAffliction(identifier) then
                scheduleImmediateClear(character, identifier)
                return
            end
        end
    end)
end)


Hook.Add('character.created', 'AAAC.AfflictionTools.CharacterCreated', function(character)
    if character == nil then return end
    Timer.Wait(function()
        updateFlags()
        processCharacter(character)
    end, 100)
end)
