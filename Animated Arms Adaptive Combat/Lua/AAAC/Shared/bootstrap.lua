AAAC = AAAC or {}
if AAAC.__runtimeBootstrap then
    return AAAC
end
AAAC.__runtimeBootstrap = true

local function safeDofile(path)
    local ok, result = pcall(function() return dofile(path) end)
    if not ok then
        print('[AAAC] bootstrap dofile failed: ' .. tostring(path) .. ' :: ' .. tostring(result))
        return nil
    end
    return result
end

if not AAAC.Path or AAAC.Path == '' then
    AAAC.Path = table.pack(...)[1] or AAAC.Path
end

if AAAC.Path and (not AAAC.GetServerConfig or not AAAC.IsFeatureEnabled) then
    safeDofile(AAAC.Path .. '/Lua/aaac_settings.lua')
end

AAAC.Runtime = AAAC.Runtime or {}

function AAAC.Runtime.IsServerSide()
    return SERVER or Game.IsSingleplayer or (Game.IsMultiplayer and not CLIENT)
end

function AAAC.Runtime.GetServerConfig()
    if AAAC.GetServerConfig then
        local ok, cfg = pcall(AAAC.GetServerConfig)
        if ok and type(cfg) == 'table' then return cfg end
    end
    return AAAC.ServerConfig or AAAC.DefaultServerConfig or {}
end

function AAAC.Runtime.IsFeatureEnabled(featureName, fallback)
    if AAAC.IsFeatureEnabled then
        local ok, enabled = pcall(AAAC.IsFeatureEnabled, featureName)
        if ok and enabled ~= nil then return enabled == true end
    end
    local cfg = AAAC.Runtime.GetServerConfig()
    local key = 'Enable' .. tostring(featureName)
    local value = cfg[key]
    if value == nil then return fallback == true end
    return value == true
end

return AAAC
