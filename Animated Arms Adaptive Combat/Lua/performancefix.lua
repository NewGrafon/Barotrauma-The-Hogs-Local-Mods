local invertedHighPriorityItems = {}
local invertedHighPriorityCharacters = {}
local highPriorityComponents = {}

local signalComponents = {}
local signalComponentLookup = {}
local nextSignalUpdateTime = 0

-- Определение языка игрока для сообщений об ошибках (исправленная версия)
local function GetPlayerLanguage()
    if CLIENT then
        -- Пробуем получить язык через GameSettings (более надежный способ)
        local success, lang = pcall(function()
            return tostring(GameSettings.CurrentConfig.Language)
        end)
        
        if success and lang and lang ~= "nil" then
            return lang
        end
        
        -- Если не получилось через GameSettings, пробуем через Game.Client (с защитой)
        if Game and Game.Client then
            local clientSuccess, clientLang = pcall(function()
                return tostring(Game.Client.Language)
            end)
            if clientSuccess and clientLang and clientLang ~= "nil" then
                return clientLang
            end
        end
    end
    return "English" -- Значение по умолчанию
end

local LANG = GetPlayerLanguage()

local function AddSignalComponent(item)
    if item == nil or item.Removed or signalComponentLookup[item] then
        return
    end

    signalComponentLookup[item] = true
    table.insert(signalComponents, item)
end

local function RemoveSignalComponent(item)
    if item == nil or not signalComponentLookup[item] then
        return
    end

    signalComponentLookup[item] = nil
    for index, trackedItem in ipairs(signalComponents) do
        if trackedItem == item then
            table.remove(signalComponents, index)
            break
        end
    end
end

-- Функция перевода ошибок
local function T(rus, eng, chn)
    if LANG == "Russian" then
        return rus
    elseif LANG == "Chinese" then
        return chn
    else
        return eng
    end
end

if SERVER then
    Game.mapEntityUpdateInterval = PerformanceFix.Config.serverMapEntityUpdateInterval
    Game.characterUpdateInterval = PerformanceFix.Config.serverCharacterUpdateInterval

    highPriorityComponents = PerformanceFix.Config.serverComponentPriority

    for key, value in pairs(PerformanceFix.Config.serverItemHighPriority) do
        invertedHighPriorityItems[value] = key
    end

    for key, value in pairs(PerformanceFix.Config.highPriorityCharacters) do
        invertedHighPriorityCharacters[value] = key
    end

    Hook.Add("item.equip", "highPriorityHands", function(item, char)
        Game.RemovePriorityItem(item)
        Game.AddPriorityItem(item)
    end)
else
    local result, error = pcall(function()
        Game.mapEntityUpdateInterval = PerformanceFix.Config.clientMapEntityUpdateInterval
        Game.characterUpdateInterval = PerformanceFix.Config.clientCharacterUpdateInterval

        if Game.IsMultiplayer then
            Game.poweredUpdateInterval = PerformanceFix.Config.poweredUpdateInterval or 1
        end

        Timer.AccumulatorMax = (PerformanceFix.Config.accumulatorMax or 50) / 1000
    end)

    if error then
        printerror("The below error most likely was thrown because of an outdated Lua client, please consider updating.")
        printerror(error)
    end

    highPriorityComponents = PerformanceFix.Config.clientComponentPriority

    for key, value in pairs(PerformanceFix.Config.clientItemHighPriority) do
        invertedHighPriorityItems[value] = key
    end

    for key, value in pairs(PerformanceFix.Config.highPriorityCharacters) do
        invertedHighPriorityCharacters[value] = key
    end

    Hook.Add("item.equip", "highPriorityHands", function(item, char)
        Game.RemovePriorityItem(item)
        Game.AddPriorityItem(item)
    end)
end


Hook.Add("think", "signalUpdatePerformanceFix", function()
    if #signalComponents == 0 then
        return
    end

    local now = os.clock()
    local updateInterval = math.max(tonumber(Game.mapEntityUpdateInterval) or 0.25, 0.25)
    if now < nextSignalUpdateTime then
        return
    end

    nextSignalUpdateTime = now + updateInterval
    local signalValue = tostring(Game.mapEntityUpdateInterval)

    for index = #signalComponents, 1, -1 do
        local value = signalComponents[index]
        if value == nil or value.Removed then
            if value ~= nil then
                signalComponentLookup[value] = nil
            end
            table.remove(signalComponents, index)
        else
            value.SendSignal(signalValue, "signal_out")
        end
    end
end)

local function IsPriority(item)
    if item.HasTag("highpriority") or invertedHighPriorityItems[item.Prefab.Identifier.Value] ~= nil then
        return true
    end

    for _, comp in pairs(highPriorityComponents) do
        if item.GetComponentString(comp) ~= nil then
            return true
        end
    end

    return false
end

local function SetPriority()
    signalComponents = {}
    signalComponentLookup = {}
    nextSignalUpdateTime = 0

    if CLIENT then
        for k, v in pairs(Item.ItemList) do
            if PerformanceFix.Config.allowSingleplayerPermanentConfigs and Game.IsSingleplayer then
                break
            end

            if v.HasTag("performancefix") then
                AddSignalComponent(v)
            end

            local light = v.GetComponentString("LightComponent")

            if light ~= nil then
                if PerformanceFix.Config.disableShadowCastingLights then
                    light.CastShadows = false
                end
                if PerformanceFix.Config.disableDrawBehindSubsLights then
                    light.DrawBehindSubs = false
                end
            end

            if PerformanceFix.Config.hideInGameWires then
                local wire = v.GetComponentString("Wire")

                if wire and #wire.Connections ~= 0 then
                    wire.Item.HiddenInGame = true
                end
            end

            if PerformanceFix.Config.hideInGameComponents then
                if v.HasTag("logic") then
                    v.HiddenInGame = true
                end
            end
        end
    end

    Game.ClearPriorityItem()
    Game.ClearPriorityCharacter()

    for key, value in pairs(Item.ItemList) do
        if IsPriority(value) then
            Game.AddPriorityItem(value)
        end
    end

    for key, value in pairs(Character.CharacterList) do
        if invertedHighPriorityCharacters[value.SpeciesName.Value] then
            Game.AddPriorityCharacter(value)
        end

        if value.Inventory ~= nil then
            local rightItem = value.Inventory.GetItemInLimbSlot(InvSlotType.RightHand)
            local leftItem = value.Inventory.GetItemInLimbSlot(InvSlotType.LeftHand)

            if rightItem ~= nil then
                Game.AddPriorityItem(rightItem)
            end

            if leftItem ~= nil then
                Game.AddPriorityItem(leftItem)
            end
        end
    end
end

Hook.Add("roundStart", "initRoundStart", function()
    Timer.Wait(function()
        SetPriority()
    end, 1000)
end)


Hook.Add("characterCreated", "addToPriority", function(character)
    if invertedHighPriorityCharacters[character.SpeciesName.Value] then
        Game.AddPriorityCharacter(character)
    end
end)

Hook.Add("item.created", "addToPriority", function (item)
    if CLIENT and item.HasTag("performancefix") then
        AddSignalComponent(item)
    end

    if IsPriority(item) then
        Game.AddPriorityItem(item)
    end
end)

Hook.Add("item.removed", "removePerformanceFixSignalComponent", function(item)
    RemoveSignalComponent(item)
end)


SetPriority()