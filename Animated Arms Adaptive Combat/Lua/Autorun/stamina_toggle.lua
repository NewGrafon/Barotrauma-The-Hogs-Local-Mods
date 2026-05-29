AAAC = AAAC or {}

local WATCHED = {
    ["stamina"] = true,
}
local CLEAR_STEPS_MS = {0, 25, 100, 250}

local function featureEnabled()
    if AAAC and AAAC.IsFeatureEnabled then
        return AAAC.IsFeatureEnabled("Stamina")
    end
    return true
end

local function getAffliction(character, identifier)
    if not character or not character.CharacterHealth then return nil end
    local affliction = nil
    pcall(function() affliction = character.CharacterHealth.GetAffliction(identifier, false) end)
    if affliction == nil then
        pcall(function() affliction = character.CharacterHealth.GetAffliction(identifier, true) end)
    end
    if affliction == nil then
        pcall(function() affliction = character.CharacterHealth.GetAffliction(identifier) end)
    end
    return affliction
end

local function clearAfflictionInstance(affliction)
    if not affliction then return end
    pcall(function() affliction.SetStrength(0) end)
    pcall(function() affliction.Strength = 0 end)
    pcall(function() affliction.NonClampedStrength = 0 end)
    pcall(function() affliction.Duration = 0 end)
end

local function resolveCharacterFromHealth(characterHealth)
    if not characterHealth then return nil end
    local character = nil
    pcall(function() character = characterHealth.Character end)
    if character ~= nil then return character end

    pcall(function()
        for c in Character.CharacterList do
            if c and c.CharacterHealth == characterHealth then
                character = c
                break
            end
        end
    end)
    return character
end

local function clearStamina(character)
    if not character or character.Removed or not character.CharacterHealth then return end
    pcall(function() character.CharacterHealth.ReduceAfflictionOnAllLimbs("stamina", 1000000) end)
    clearAfflictionInstance(getAffliction(character, "stamina"))
    if SERVER then
        pcall(function()
            Networking.CreateEntityEvent(character, Character.CharacterStatusEventData.__new(true))
        end)
    end
end

local function scheduleStaminaClear(character)
    if featureEnabled() or not character then return end
    for _, delay in ipairs(CLEAR_STEPS_MS) do
        Timer.Wait(function()
            if not featureEnabled() then
                clearStamina(character)
            end
        end, delay)
    end
end

local function sweepAll()
    if featureEnabled() then return end
    pcall(function()
        for character in Character.CharacterList do
            if character and not character.Removed and character.CharacterHealth then
                clearStamina(character)
            end
        end
    end)
end

Hook.Add("character.applyAffliction", "AAAC.Stamina.BlockOnApply", function(characterHealth, limbHealth, newAffliction)
    if featureEnabled() or newAffliction == nil then return end
    local identifier = nil
    pcall(function() identifier = tostring(newAffliction.Prefab.Identifier) end)
    if not identifier then pcall(function() identifier = tostring(newAffliction.Identifier) end) end
    if identifier and WATCHED[identifier] then
        local character = resolveCharacterFromHealth(characterHealth) or Character.Controlled
        if character then scheduleStaminaClear(character) end
        clearAfflictionInstance(newAffliction)
    end
end)

Hook.Add("roundStart", "AAAC.Stamina.SweepOnRoundStart", function()
    Timer.Wait(function() sweepAll() end, 200)
end)

local previousOnServerConfigChanged = AAAC.OnServerConfigChanged
AAAC.OnServerConfigChanged = function(config)
    if previousOnServerConfigChanged then
        pcall(previousOnServerConfigChanged, config)
    end
    if config and config.EnableStamina == false then
        sweepAll()
    end
end
