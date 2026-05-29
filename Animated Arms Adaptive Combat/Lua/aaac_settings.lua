AAAC = AAAC or {}
if AAAC.__settingsInitialized then
    return AAAC
end
AAAC.__settingsInitialized = true
AAAC.__serverConfigReceived = AAAC.__serverConfigReceived or false
AAAC.__lastServerConfigRequest = AAAC.__lastServerConfigRequest or 0
AAAC.__lastServerConfigBroadcast = AAAC.__lastServerConfigBroadcast or 0

AAAC.ConfigDirectory = Game.SaveFolder .. "/ModConfigs/"
AAAC.ConfigPath = AAAC.ConfigDirectory .. "AAAC_ModSettings.json"
AAAC.ServerConfigPath = AAAC.ConfigDirectory .. "AAAC_ServerSettings.json"
AAAC.ServerConfigCachePath = AAAC.ConfigDirectory .. "AAAC_ServerSettings_ClientCache.json"
AAAC.Keys = AAAC.Keys or Keys

AAAC.DefaultConfig = {
    Version = 5,
    Keybinds = {
        AutoReload = {"R"},
        QuickSwap = {"F"},
        Interact = {"N"},
        CrawlToggle = {"M"},
        Grab = {"G"},
        Strangle = {"J"}
    }
}

AAAC.DefaultServerConfig = {
    Version = 6,
    MaxCasings = 10,
    EnableAutoReload = true,
    EnableQuickSwap = true,
    EnableInteract = true,
    EnableCrawlToggle = true,
    EnableShotPain = true,
    EnableShotTinnitus = true,
    EnableStamina = true,
    EnableStrangle = true,
    EnableGrabAll = true
}

local function deepCopy(value)
    if type(value) ~= "table" then return value end
    local copy = {}
    for k, v in pairs(value) do
        copy[k] = deepCopy(v)
    end
    return copy
end

local function mergeDefaults(target, defaults)
    if type(target) ~= "table" then
        target = {}
    end

    for key, value in pairs(defaults) do
        if type(value) == "table" then
            if type(target[key]) ~= "table" then
                target[key] = deepCopy(value)
            else
                target[key] = mergeDefaults(target[key], value)
            end
        elseif target[key] == nil then
            target[key] = value
        end
    end

    return target
end

local function tablesEqual(a, b)
    if type(a) ~= type(b) then return false end
    if type(a) ~= "table" then return a == b end
    local countA, countB = 0, 0
    for k, v in pairs(a) do
        countA = countA + 1
        if not tablesEqual(v, b[k]) then return false end
    end
    for _ in pairs(b) do countB = countB + 1 end
    return countA == countB
end

local function ensureConfigDir()
    if not File.DirectoryExists(AAAC.ConfigDirectory) then
        File.CreateDirectory(AAAC.ConfigDirectory)
    end
end

local function migrateLocalConfig(config)
    local version = tonumber(config.Version) or 1
    config = mergeDefaults(config, AAAC.DefaultConfig)
    config.Keybinds = config.Keybinds or {}

    if version < 2 then
        local oldReload = config.Keybinds.AutoReload
        if oldReload == nil or tablesEqual(oldReload, {"F"}) then
            config.Keybinds.AutoReload = {"CapsLock"}
        end

        local oldQuickSwap = config.Keybinds.QuickSwap
        if oldQuickSwap == nil or tablesEqual(oldQuickSwap, {"Tab"}) then
            config.Keybinds.QuickSwap = {"F"}
        end

        if config.Keybinds.CrawlToggle == nil then
            config.Keybinds.CrawlToggle = {"M"}
        end
    end

    if version < 4 then
        if config.Keybinds.Grab == nil then
            config.Keybinds.Grab = {"G"}
        end
    end

    if version < 5 then
        if config.Keybinds.Strangle == nil then
            config.Keybinds.Strangle = {"J"}
        end
    end

    config.Version = AAAC.DefaultConfig.Version
    return config
end

local function migrateServerConfig(config)
    local version = tonumber(config.Version) or 1
    config = mergeDefaults(config, AAAC.DefaultServerConfig)

    if version < 2 then
        if config.EnableAutoReload == nil then config.EnableAutoReload = true end
        if config.EnableQuickSwap == nil then config.EnableQuickSwap = true end
        if config.EnableInteract == nil then config.EnableInteract = true end
        if config.EnableCrawlToggle == nil then config.EnableCrawlToggle = true end
    end

    if version < 3 then
        if config.EnableShotPain == nil then config.EnableShotPain = true end
        if config.EnableShotTinnitus == nil then config.EnableShotTinnitus = true end
    end

    if version < 4 then
        if config.EnableStrangle == nil then config.EnableStrangle = true end
    end

    if version < 5 then
        if config.EnableGrabAll == nil then config.EnableGrabAll = true end
    end

    if version < 6 then
        if config.EnableStamina == nil then config.EnableStamina = true end
    end

    config.EnableAutoReload = true
    config.EnableQuickSwap = true
    config.EnableGrabAll = true
    config.Version = AAAC.DefaultServerConfig.Version
    return config
end

local function loadJson(path)
    if not File.Exists(path) then return nil end
    local ok, parsed = pcall(function()
        return json.parse(File.Read(path))
    end)
    if ok and type(parsed) == "table" then
        return parsed
    end
    return nil
end

function AAAC.GetConfig()
    if AAAC.Config ~= nil then
        return AAAC.Config
    end

    ensureConfigDir()
    local config = loadJson(AAAC.ConfigPath)
    if config == nil then
        config = deepCopy(AAAC.DefaultConfig)
    end

    config = migrateLocalConfig(config)
    AAAC.Config = config
    pcall(function() File.Write(AAAC.ConfigPath, json.serialize(config)) end)
    return AAAC.Config
end

function AAAC.SaveConfig(config)
    ensureConfigDir()
    config = migrateLocalConfig(config or AAAC.GetConfig())
    AAAC.Config = config
    File.Write(AAAC.ConfigPath, json.serialize(config))
    return AAAC.Config
end

function AAAC.ReloadConfig()
    AAAC.Config = nil
    return AAAC.GetConfig()
end

function AAAC.GetServerConfig()
    if AAAC.ServerConfig ~= nil then
        return AAAC.ServerConfig
    end

    ensureConfigDir()
    local config = nil

    if SERVER or Game.IsSingleplayer then
        config = loadJson(AAAC.ServerConfigPath)
    end

    if config == nil then
        config = loadJson(AAAC.ServerConfigCachePath)
    end

    if config == nil then
        config = deepCopy(AAAC.DefaultServerConfig)
    end

    config = migrateServerConfig(config)
    AAAC.ServerConfig = config
    AAAC.__serverConfigReceived = true

    pcall(function() File.Write(AAAC.ServerConfigCachePath, json.serialize(config)) end)
    if SERVER or Game.IsSingleplayer then
        pcall(function() File.Write(AAAC.ServerConfigPath, json.serialize(config)) end)
    end

    return AAAC.ServerConfig
end

function AAAC.SaveServerConfig(config)
    ensureConfigDir()
    config = migrateServerConfig(config or AAAC.GetServerConfig())
    AAAC.ServerConfig = config
    AAAC.__serverConfigReceived = true

    pcall(function() File.Write(AAAC.ServerConfigCachePath, json.serialize(config)) end)
    if SERVER or Game.IsSingleplayer then
        pcall(function() File.Write(AAAC.ServerConfigPath, json.serialize(config)) end)
    end

    return AAAC.ServerConfig
end

function AAAC.IsPrivilegedClient(client)
    if Game.IsSingleplayer then return true end

    local target = client
    if target == nil and CLIENT then
        target = Game.Client
    end

    if SERVER and target == nil then
        return true
    end

    if target == nil then return false end

    local ok, result = pcall(function()
        if target.IsServerOwner then return true end
        if target.HasPermission and target.HasPermission(ClientPermissions.All) then return true end
        if SERVER and Game.Server and Game.Server.OwnerConnection and target.Connection ~= nil then
            if target.Connection == Game.Server.OwnerConnection then
                return true
            end
        end
        return false
    end)

    return ok and result or false
end

function AAAC.GetSetting(name, defaultValue)
    local config = AAAC.GetConfig()
    local value = config[name]
    if value == nil then
        return defaultValue
    end
    return value
end

function AAAC.GetKeybind(name, defaultValue)
    local config = AAAC.GetConfig()
    local keybinds = config.Keybinds or {}
    local keys = keybinds[name]

    if type(keys) == "string" then
        keys = { keys }
    end

    if type(keys) ~= "table" or #keys == 0 then
        keys = defaultValue or AAAC.DefaultConfig.Keybinds[name] or {}
    end

    return keys
end

function AAAC.SetKeybind(name, keys)
    local config = AAAC.GetConfig()
    config.Keybinds = config.Keybinds or {}

    if type(keys) == "string" then
        keys = { keys }
    end

    if type(keys) ~= "table" or #keys == 0 then
        keys = deepCopy(AAAC.DefaultConfig.Keybinds[name] or {})
    end

    config.Keybinds[name] = keys
    return AAAC.SaveConfig(config)
end

function AAAC.GetMaxCasings()
    local config = AAAC.GetServerConfig()
    local value = tonumber(config.MaxCasings or AAAC.DefaultServerConfig.MaxCasings) or AAAC.DefaultServerConfig.MaxCasings
    if value < 0 then value = 0 end
    if value > 1000 then value = 1000 end
    return math.floor(value)
end

function AAAC.GetFeatureEnabledKey(featureName)
    return "Enable" .. tostring(featureName)
end

function AAAC.IsFeatureEnabled(featureName)
    local name = tostring(featureName)
    if name == "GrabAll" or name == "AutoReload" or name == "QuickSwap" or name == "Interact" or name == "CrawlToggle" then
        return true
    end
    local config = AAAC.GetServerConfig()
    local key = AAAC.GetFeatureEnabledKey(featureName)
    local value = config[key]
    if value == nil then
        return true
    end
    return value == true
end

function AAAC.WriteServerConfigMessage(values)
    values = migrateServerConfig(values or AAAC.GetServerConfig())
    local msg = Networking.Start("AAACConfigSync")
    msg.WriteRangedInteger(values.MaxCasings, 0, 1000)
    msg.WriteBoolean(values.EnableAutoReload)
    msg.WriteBoolean(values.EnableQuickSwap)
    msg.WriteBoolean(values.EnableInteract)
    msg.WriteBoolean(values.EnableCrawlToggle)
    msg.WriteBoolean(values.EnableShotPain)
    msg.WriteBoolean(values.EnableShotTinnitus)
    msg.WriteBoolean(values.EnableStamina)
    msg.WriteBoolean(values.EnableStrangle)
    msg.WriteBoolean(values.EnableGrabAll)
    return msg
end

function AAAC.ReadServerConfigMessage(msg)
    local config = {}
    config.MaxCasings = msg.ReadRangedInteger(0, 1000)
    config.EnableAutoReload = msg.ReadBoolean()
    config.EnableQuickSwap = msg.ReadBoolean()
    config.EnableInteract = msg.ReadBoolean()
    config.EnableCrawlToggle = msg.ReadBoolean()
    config.EnableShotPain = msg.ReadBoolean()
    config.EnableShotTinnitus = msg.ReadBoolean()
    config.EnableStamina = msg.ReadBoolean()
    config.EnableStrangle = msg.ReadBoolean()
    config.EnableGrabAll = true
    local _readLegacyGrabAll = msg.ReadBoolean()
    return migrateServerConfig(config)
end

function AAAC.ApplyIncomingServerConfig(values)
    local config = migrateServerConfig(values)
    AAAC.ServerConfig = config
    AAAC.__serverConfigReceived = true

    pcall(function()
        ensureConfigDir()
        File.Write(AAAC.ServerConfigCachePath, json.serialize(config))
    end)

    if SERVER or Game.IsSingleplayer then
        pcall(function()
            ensureConfigDir()
            File.Write(AAAC.ServerConfigPath, json.serialize(config))
        end)
    end

    if type(AAAC.ApplyCasingLimit) == "function" then
        pcall(AAAC.ApplyCasingLimit)
    end
    if type(AAAC.OnServerConfigChanged) == "function" then
        pcall(AAAC.OnServerConfigChanged, AAAC.ServerConfig)
    end
end

function AAAC.RequestServerConfig()
    if CLIENT and Game.IsMultiplayer and Game.Client and not SERVER then
        AAAC.__lastServerConfigRequest = Timer.GetTime() or 0
        Networking.Send(Networking.Start("AAACConfigRequest"))
        return true
    end
    return false
end

function AAAC.PushServerConfig(values)
    values = migrateServerConfig(values or AAAC.GetServerConfig())

    if Game.IsSingleplayer then
        AAAC.SaveServerConfig(values)
        AAAC.ApplyIncomingServerConfig(values)
        return true
    end

    if SERVER then
        AAAC.SaveServerConfig(values)
        AAAC.ApplyIncomingServerConfig(values)
        Networking.Send(AAAC.WriteServerConfigMessage(values))
        return true
    end

    if CLIENT and (AAAC.IsPrivilegedClient(Game.Client) or (Game.Client and Game.Client.IsServerOwner)) then
        AAAC.ApplyIncomingServerConfig(values)
        Networking.Send(AAAC.WriteServerConfigMessage(values))
        AAAC.__lastServerConfigRequest = 0
        Timer.Wait(function()
            pcall(AAAC.RequestServerConfig)
        end, 100)
        return true
    end

    return false
end

function AAAC.GetKeybindString(keys)
    if type(keys) == "string" then
        keys = { keys }
    end

    if type(keys) ~= "table" or #keys == 0 then
        return ""
    end

    local result = tostring(keys[1])
    for index = 2, #keys do
        result = result .. " + " .. tostring(keys[index])
    end
    return result
end

function AAAC.IsKeybindHit(keys)
    if type(keys) == "string" then
        keys = { keys }
    end

    if type(keys) ~= "table" or #keys == 0 then
        return false
    end

    for index, key in ipairs(keys) do
        if AAAC.Keys[key] == nil then
            return false
        end

        local pressed = false
        if index == #keys then
            pressed = PlayerInput.KeyHit(Keys[key])
        else
            pressed = PlayerInput.KeyDown(Keys[key])
        end

        if not pressed then
            return false
        end
    end

    return true
end

function AAAC.IsKeybindDown(keys)
    if type(keys) == "string" then
        keys = { keys }
    end

    if type(keys) ~= "table" or #keys == 0 then
        return false
    end

    for _, key in ipairs(keys) do
        if AAAC.Keys[key] == nil or not PlayerInput.KeyDown(Keys[key]) then
            return false
        end
    end

    return true
end

function AAAC.IsConfiguredKeyHit(name, defaultValue)
    return AAAC.IsKeybindHit(AAAC.GetKeybind(name, defaultValue))
end

function AAAC.IsConfiguredKeyDown(name, defaultValue)
    return AAAC.IsKeybindDown(AAAC.GetKeybind(name, defaultValue))
end

function AAAC.IsOnlyControlKeysPressed()
    local pressed = PlayerInput.GetKeyboardState.GetPressedKeys()

    for _, key in pairs(pressed) do
        local keyName = tostring(key)
        local isNotShift = string.find(keyName, "Shift", 1, true) == nil
        local isNotCtrl = string.find(keyName, "Control", 1, true) == nil
        local isNotAlt = string.find(keyName, "Alt", 1, true) == nil

        if isNotShift and isNotCtrl and isNotAlt then
            return false
        end
    end

    return true
end

function AAAC.CaptureNextKeybind(callback)
    if type(callback) ~= "function" then
        callback = function() end
    end

    Hook.Remove("think", "AAAC.CaptureNextKeybind")

    Hook.Add("think", "AAAC.CaptureNextKeybind", function()
        if PlayerInput.KeyDown(Keys.Escape) then
            Hook.Remove("think", "AAAC.CaptureNextKeybind")
            callback(nil)
            return
        end

        if AAAC.IsOnlyControlKeysPressed() then
            return
        end

        local pressedKeys = PlayerInput.GetKeyboardState.GetPressedKeys()
        if pressedKeys == nil or #pressedKeys == 0 then
            return
        end

        Hook.Remove("think", "AAAC.CaptureNextKeybind")

        local result = {}
        for _, key in pairs(pressedKeys) do
            table.insert(result, 1, tostring(key))
        end

        callback(result)
    end)
end

function AAAC.InitializeNetworking()
    if AAAC.__networkInitialized then return end
    if not Game.IsMultiplayer then return end

    AAAC.__networkInitialized = true

    Networking.Receive("AAACConfigSync", function(msg, client)
        if SERVER then
            local privileged = false
            local ok = pcall(function()
                if client == nil then
                    privileged = true
                    return
                end
                if AAAC.IsPrivilegedClient(client) then
                    privileged = true
                    return
                end
                if Game.Server and Game.Server.OwnerConnection and client.Connection == Game.Server.OwnerConnection then
                    privileged = true
                    return
                end
            end)
            if not ok then privileged = false end
            if privileged then
                local newConfig = AAAC.ReadServerConfigMessage(msg)
                AAAC.SaveServerConfig(newConfig)
                if type(AAAC.ApplyIncomingServerConfig) == "function" then
                    pcall(AAAC.ApplyIncomingServerConfig, newConfig)
                elseif type(AAAC.ApplyCasingLimit) == "function" then
                    pcall(AAAC.ApplyCasingLimit)
                end
                Networking.Send(AAAC.WriteServerConfigMessage(newConfig))

                if CLIENT and Game.Client and Game.Client.IsServerOwner then
                    pcall(AAAC.ApplyIncomingServerConfig, newConfig)
                end
            end
        elseif CLIENT then
            AAAC.ApplyIncomingServerConfig(AAAC.ReadServerConfigMessage(msg))
            AAAC.__lastServerConfigRequest = Timer.GetTime() or 0
        end
    end)

    if CLIENT then
        local function requestServerConfig()
            AAAC.RequestServerConfig()
        end

        Hook.Add("loaded", "AAAC.RequestServerConfig", requestServerConfig)
        Hook.Add("roundStart", "AAAC.RequestServerConfig.RoundStart", function()
            Timer.Wait(function()
                pcall(requestServerConfig)
            end, 500)
        end)
    end

    if CLIENT then
        Hook.Add("think", "AAAC.RequestServerConfig.Think", function()
            if not Game.IsMultiplayer or SERVER then return end
            local now = Timer.GetTime() or 0
            if AAAC.__serverConfigReceived and now - (AAAC.__lastServerConfigRequest or 0) < 5.0 then return end
            if now - (AAAC.__lastServerConfigRequest or 0) < 2.0 then return end
            AAAC.__lastServerConfigRequest = now
            pcall(AAAC.RequestServerConfig)
        end)
    end

    if SERVER then
        Hook.Add("think", "AAAC.BroadcastServerConfig.Think", function()
            if not Game.IsMultiplayer or not Game.RoundStarted then return end
            local now = Timer.GetTime() or 0
            if now - (AAAC.__lastServerConfigBroadcast or 0) < 2.0 then return end
            AAAC.__lastServerConfigBroadcast = now
            Networking.Send(AAAC.WriteServerConfigMessage(AAAC.GetServerConfig()))
            if CLIENT and Game.Client and Game.Client.IsServerOwner then
                pcall(AAAC.ApplyIncomingServerConfig, AAAC.GetServerConfig())
            end
        end)
    end

    if SERVER then
        Hook.Add("loaded", "AAAC.SendConfigToAllClients", function()
            Networking.Send(AAAC.WriteServerConfigMessage(AAAC.GetServerConfig()))
        end)

        Hook.Add("roundStart", "AAAC.SendConfigToAllClients.RoundStart", function()
            Timer.Wait(function()
                Networking.Send(AAAC.WriteServerConfigMessage(AAAC.GetServerConfig()))
            end, 1000)
        end)

        Hook.Add("client.connected", "AAAC.SendConfigToClient", function(client)
            Networking.Send(AAAC.WriteServerConfigMessage(AAAC.GetServerConfig()), client and client.Connection)
        end)

        Networking.Receive("AAACConfigRequest", function(msg, client)
            if client then
                Networking.Send(AAAC.WriteServerConfigMessage(AAAC.GetServerConfig()), client.Connection)
            end
        end)
    end
end

Hook.Add("loaded", "AAAC.InitializeNetworking", function()
    pcall(AAAC.InitializeNetworking)
end)

Hook.Add("roundStart", "AAAC.InitializeNetworking.RoundStart", function()
    pcall(AAAC.InitializeNetworking)
end)

AAAC.InitializeNetworking()
AAAC.GetConfig()
AAAC.GetServerConfig()

return AAAC
