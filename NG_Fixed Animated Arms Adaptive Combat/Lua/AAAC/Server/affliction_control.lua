AAAC = AAAC or {}
if AAAC.__serverAfflictionControlLoaded then return AAAC end
AAAC.__serverAfflictionControlLoaded = true

if CLIENT and Game.IsMultiplayer then
    return AAAC
end

local WATCHED = {
    spasm_shot = 'ShotPain',
    tinnitus_shot = 'ShotTinnitus'
}

local UPDATE_INTERVAL = 0.20
local nextUpdate = 0

local function now()
    local ok, t = pcall(function() return Timer.GetTime() end)
    if ok and t then return tonumber(t) or 0 end
    ok, t = pcall(function() return Game.GameTime end)
    if ok and t then return tonumber(t) or 0 end
    return 0
end

local function getFeatureKey(feature)
    return 'Enable' .. tostring(feature)
end

local function isFeatureDisabled(feature)
    local cfg = nil
    if AAAC.GetServerConfig then
        local ok, result = pcall(AAAC.GetServerConfig)
        if ok and type(result) == 'table' then cfg = result end
    end
    if not cfg and AAAC.ServerConfig then cfg = AAAC.ServerConfig end
    if not cfg then return false end
    local value = cfg[getFeatureKey(feature)]
    if value == nil then return false end
    return value ~= true
end

local function getPrefab(identifier)
    local prefab = nil
    local ok, found, result = pcall(function() return AfflictionPrefab.Prefabs.TryGet(identifier) end)
    if ok and found and result then
        return result
    end
    pcall(function() prefab = AfflictionPrefab.Prefabs[identifier] end)
    return prefab
end

local function getMainLimb(character)
    if not character or not character.AnimController then return nil end
    local limb = nil
    pcall(function() limb = character.AnimController.MainLimb end)
    if limb then return limb end
    pcall(function() limb = character.AnimController.GetLimb(LimbType.Torso) end)
    return limb
end

local function setAffliction(character, identifier, strength, source)
    if not character or character.Removed or not character.CharacterHealth then return false end
    local prefab = getPrefab(identifier)
    if not prefab then return false end
    local limb = getMainLimb(character)
    if not limb then return false end

    local affliction = nil
    local ok, result = pcall(function() return prefab.Instantiate(strength or 0, source) end)
    if ok and result then
        affliction = result
    else
        ok, result = pcall(function() return prefab.Instantiate(strength or 0) end)
        if ok and result then affliction = result end
    end
    if not affliction then return false end

    local applied = false
    applied = pcall(function()
        character.CharacterHealth.ApplyAffliction(limb, affliction, true)
    end)
    if not applied then
        pcall(function()
            character.CharacterHealth.ApplyAffliction(limb, affliction, true, false, false)
        end)
        applied = true
    end
    return applied
end

local function getAffliction(health, identifier)
    local affliction = nil
    pcall(function() affliction = health.GetAffliction(identifier, false) end)
    if not affliction then
        pcall(function() affliction = health.GetAffliction(identifier) end)
    end
    return affliction
end

local function clearAfflictionInstance(affliction)
    if not affliction then return end
    pcall(function() affliction.SetStrength(0) end)
    pcall(function() affliction.Strength = 0 end)
    pcall(function() affliction.NonClampedStrength = 0 end)
    pcall(function() affliction.Duration = 0 end)
    pcall(function() affliction.DamagePerSecond = 0 end)
    pcall(function() affliction.GrainEffectStrength = 0 end)
    pcall(function() affliction.PendingGrainEffectStrength = 0 end)
end

local function nukeAffliction(character, identifier)
    if not character or character.Removed or not character.CharacterHealth then return end
    local health = character.CharacterHealth

    -- First, explicitly set the affliction to 0 using the same pattern as medical mods.
    pcall(function() setAffliction(character, identifier, 0, character) end)

    -- Then remove any remaining instance using all available API methods.
    pcall(function() health.ReduceAfflictionOnAllLimbs(identifier, 1000000) end)
    pcall(function()
        local limb = getMainLimb(character)
        if limb then
            health.ReduceAfflictionOnLimb(limb, identifier, 1000000)
        end
    end)
    pcall(function()
        health.RemoveAfflictions(function(affliction)
            local id = nil
            pcall(function() id = tostring(affliction.Prefab.Identifier) end)
            if not id then pcall(function() id = tostring(affliction.Identifier) end) end
            if id == identifier then
                clearAfflictionInstance(affliction)
                return true
            end
            return false
        end)
    end)

    local affliction = getAffliction(health, identifier)
    clearAfflictionInstance(affliction)
end

local function purgeCharacter(character)
    if isFeatureDisabled('ShotPain') then
        nukeAffliction(character, 'spasm_shot')
    end
    if isFeatureDisabled('ShotTinnitus') then
        nukeAffliction(character, 'tinnitus_shot')
    end
end

Hook.Add('character.applyDamage', 'AAAC.Server.AfflictionDamageStrip', function(character, attackResult)
    if not attackResult or not attackResult.Afflictions then return end
    local afflictions = attackResult.Afflictions
    for i = #afflictions, 1, -1 do
        local affliction = afflictions[i]
        local identifier = nil
        pcall(function() identifier = tostring(affliction.Prefab.Identifier) end)
        if not identifier then pcall(function() identifier = tostring(affliction.Identifier) end) end
        if identifier and WATCHED[identifier] and isFeatureDisabled(WATCHED[identifier]) then
            table.remove(afflictions, i)
            if character then
                nukeAffliction(character, identifier)
            end
        end
    end
end)

Hook.Add('character.applyAffliction', 'AAAC.Server.DirectAfflictionStrip', function(characterHealth, limbHealth, newAffliction)
    local identifier = nil
    pcall(function() identifier = tostring(newAffliction.Prefab.Identifier) end)
    if not identifier then pcall(function() identifier = tostring(newAffliction.Identifier) end) end
    if identifier and WATCHED[identifier] and isFeatureDisabled(WATCHED[identifier]) then
        local character = nil
        pcall(function() character = characterHealth.Character end)
        if character then
            nukeAffliction(character, identifier)
        end
        clearAfflictionInstance(newAffliction)
        return true
    end
end)

Hook.Add('think', 'AAAC.Server.AfflictionCleanupThink', function()
    local t = now()
    if t < nextUpdate then return end
    nextUpdate = t + UPDATE_INTERVAL

    if not isFeatureDisabled('ShotPain') and not isFeatureDisabled('ShotTinnitus') then return end

    for character in Character.CharacterList do
        if character and not character.Removed and character.CharacterHealth then
            purgeCharacter(character)
        end
    end
end)

return AAAC
