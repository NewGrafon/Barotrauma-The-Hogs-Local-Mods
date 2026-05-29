Timer.Wait(function()
    if SERVER and NTC == nil then
        return
    end
end, 1)

-- Language settings (shared with pda.lua)
if not terminalLanguages then
    terminalLanguages = {}
end
if not userLanguageInitialized then
    userLanguageInitialized = {}
end

local BaseHeartrate = 60
local UpperTachycardiaBound = 180
local UpperFibrillationBound = 300

-- Таблица для хранения имен оригинальных владельцев
local originalOwnerNames = {}

-- Функция для получения языка предмета
local function GetItemLanguage(item)
    if item == nil then return "en" end
    local itemId = tostring(item.ID)
    return terminalLanguages[itemId] or "en"
end

local function DetermineHeartrate(person)
    if person == nil or person.CharacterHealth == nil or person.IsDead then
        return 0
    end

    local arrestAffliction = person.CharacterHealth.GetAffliction("cardiacarrest")
    if arrestAffliction ~= nil and arrestAffliction.Strength >= 0.5 then
        return 0
    end

    local rate = BaseHeartrate
    local tachy = person.CharacterHealth.GetAffliction("tachycardia")
    local fibrill = person.CharacterHealth.GetAffliction("fibrillation")

    if fibrill ~= nil then
        rate = HF.Lerp(
            UpperTachycardiaBound,
            UpperFibrillationBound,
            fibrill.Strength / 100 * (1 + math.random() * 0.5)
        )
    elseif tachy ~= nil then
        rate = HF.Lerp(BaseHeartrate, UpperTachycardiaBound, tachy.Strength / 100)
    end

    return rate
end

local activeBeacons = {}
local trackedHeadsets = {}

local function GetCurrentHolder(obj)  
    local rootOwner = obj.GetRootInventoryOwner()  
    if rootOwner ~= nil   
       and rootOwner ~= obj   
       and LuaUserData.IsTargetType(rootOwner, "Barotrauma.Character")   
       and rootOwner.IsHuman then  
        return rootOwner  
    end  
      
    return nil  
end

-- Функция для сохранения имени оригинального владельца
local function SetOriginalOwnerName(obj, holder)
    if originalOwnerNames[obj] == nil and holder ~= nil then
        originalOwnerNames[obj] = holder.Name
        return holder.Name
    end
    return originalOwnerNames[obj]
end

-- Функция для получения имени оригинального владельца
local function GetOriginalOwnerName(obj)
    return originalOwnerNames[obj] or "UNKNOWN"
end

local function GetUserPreferredLanguage()
    local userLang = tostring(GameSettings.CurrentConfig.Language)

    if userLang == "Russian" then
        return "ru"
    elseif userLang == "Chinese" or userLang == "ChineseSimplified" then
        return "cn"
    end

    return "en"
end

local function InitializeTrackedItem(item)
    if item == nil or item.Removed then return end

    local identifier = item.Prefab.Identifier
    if identifier ~= "pda" and identifier ~= "medheadset" then
        return
    end

    if identifier == "pda" then
        if activeBeacons[item] == nil then
            activeBeacons[item] = false
        end
    else
        trackedHeadsets[item] = true
    end

    local itemId = tostring(item.ID)
    if not terminalLanguages[itemId] and not userLanguageInitialized[itemId] then
        terminalLanguages[itemId] = GetUserPreferredLanguage()
        userLanguageInitialized[itemId] = true
    elseif not terminalLanguages[itemId] then
        terminalLanguages[itemId] = "en"
    end

    local currentHolder = GetCurrentHolder(item)
    if currentHolder ~= nil then
        SetOriginalOwnerName(item, currentHolder)
    end
end

-- Функция для получения показателей персонажа (без дыхания)
local function GetCharacterVitals(character)
    local pressure = 0
    local pulse = 0
    
    if character == nil or character.IsDead then
        return pressure, pulse
    end
    
    -- Если персонаж мертв, все показатели = 0
    if not character.IsDead then
        pressure = HF.Round(HF.GetAfflictionStrength(character, "bloodpressure", 100))
        pulse = HF.Round(DetermineHeartrate(character))
    end
    
    return pressure, pulse
end

-- Локализованные строки
local function GetLocalizedString(item, key)
    local lang = GetItemLanguage(item)
    
    local strings = {
        dead = {
            en = "DEAD",
            ru = "МЁРТВ",
            cn = "死亡"
        },
        not_detected = {
            en = "NOT DETECTED",
            ru = "НЕ ОБНАРУЖЕН",
            cn = "未检测到"
        },
        pulse = {
            en = "HR",
            ru = "Пульс",
            cn = "脉搏"
        },
        pressure = {
            en = "BP",
            ru = "Давлен",
            cn = "血压"
        },
        warning_no_pulse = {
            en = "⚠ NO PULSE!",
            ru = "⚠ НЕТ ПУЛЬСА!",
            cn = "⚠ 无脉搏！"
        },
        warning_low_pressure = {
            en = "⚠ LOW PRESSURE!",
            ru = "⚠ НИЗКОЕ ДАВЛЕНИЕ!",
            cn = "⚠ 低血压！"
        }
    }
    
    return strings[key][lang] or strings[key]["en"]
end

-- Основная функция обновления дисплея
local function UpdateDisplay(obj, isHeadset)
    if obj == nil then return end
    
    -- Инициализация если нужно
    if activeBeacons[obj] == nil then
        activeBeacons[obj] = false
    end
    
    -- Для КПК проверяем активность
    if not isHeadset and not activeBeacons[obj] then
        obj.SonarLabel = ""
        obj.SoundRange = 0
        return
    end
    
    -- Проверяем питание (только для КПК)
    local hasPower = true
    if not isHeadset then
        local miniMap = obj.GetComponentString("MiniMap")
        hasPower = miniMap ~= nil and miniMap.Voltage > 0.5
    end
    
    if ((isHeadset) or (activeBeacons[obj] and hasPower)) then
        obj.SoundRange = 50000
        
        -- Получаем текущего держателя
        local currentHolder = GetCurrentHolder(obj)
        
        -- Сохраняем/получаем имя оригинального владельца
        local originalName = SetOriginalOwnerName(obj, currentHolder)
        
        if currentHolder ~= nil then
            -- Устройство у персонажа
            local subject = currentHolder
            local nameDisplay = originalName  -- Имя оригинального владельца
            
            -- Получаем показатели текущего держателя (без дыхания)
            local pressure, pulse = GetCharacterVitals(subject)
            
            if subject.IsDead then
                obj.SonarLabel = nameDisplay .. " \n" .. GetLocalizedString(obj, "dead")
            else
                -- Формируем основной текст с показателями
                local displayText = nameDisplay .. " \n" .. 
                                    GetLocalizedString(obj, "pulse") .. " " .. pulse .. 
                                    " | " .. GetLocalizedString(obj, "pressure") .. " " .. pressure .. "%"
                
                -- Проверяем критические состояния и добавляем предупреждение снизу
                if pulse == 0 then
                    -- Нет пульса
                    displayText = displayText .. "\n" .. GetLocalizedString(obj, "warning_no_pulse")
                elseif pressure < 55 then
                    -- Низкое давление (меньше 55%)
                    displayText = displayText .. "\n" .. GetLocalizedString(obj, "warning_low_pressure")
                end
                
                obj.SonarLabel = displayText
            end
        else
            -- Устройство не у персонажа
            local nameDisplay = GetOriginalOwnerName(obj)
            local displayText = nameDisplay .. " \n" .. GetLocalizedString(obj, "not_detected")
            obj.SonarLabel = displayText
        end
    else
        -- Нет питания или выключено
        if not isHeadset then
            obj.SonarLabel = ""
            obj.SoundRange = 0
        end
    end
end

-- Общий таймер для обновления КПК и гарнитур (120 тиков = ~2 секунды)
local deviceUpdateTimer = 0
Hook.Add("think", "pda_device_update", function()
    deviceUpdateTimer = deviceUpdateTimer + 1

    if deviceUpdateTimer < 120 then
        return
    end

    deviceUpdateTimer = 0

    for obj, isActive in pairs(activeBeacons) do
        if obj == nil or obj.Removed then
            activeBeacons[obj] = nil
        elseif isActive then
            UpdateDisplay(obj, false)
        end
    end

    for item in pairs(trackedHeadsets) do
        if item == nil or item.Removed then
            trackedHeadsets[item] = nil
        else
            UpdateDisplay(item, true)
        end
    end
end)

Hook.Add("pda.on", "activate_device", function(effect, dt, obj, targets, location)
    activeBeacons[obj] = true
    UpdateDisplay(obj, false)
end)

Hook.Add("pda.off", "deactivate_device", function(effect, dt, obj, targets, location)
    activeBeacons[obj] = false
    UpdateDisplay(obj, false)
end)

-- Оставляем хук для гарнитуры для мгновенного обновления при изменениях
Hook.Add("medheadset.update", "headset_monitor", function(effect, dt, obj, targets, location)
    UpdateDisplay(obj, true)
end)

-- Очистка таблиц при уничтожении предмета
Hook.Add("item.removed", "cleanup_pda_tables", function(item)
    if activeBeacons[item] ~= nil then
        activeBeacons[item] = nil
    end
    if trackedHeadsets[item] ~= nil then
        trackedHeadsets[item] = nil
    end
    if originalOwnerNames[item] ~= nil then
        originalOwnerNames[item] = nil
    end
    -- Also clean up language data
    local itemId = tostring(item.ID)
    if terminalLanguages[itemId] ~= nil then
        terminalLanguages[itemId] = nil
    end
    if userLanguageInitialized[itemId] ~= nil then
        userLanguageInitialized[itemId] = nil
    end
end)

-- Инициализация при спавне
Hook.Add("item.spawn", "pda_init_on_spawn", function(item)
    Timer.Wait(function()
        if item == nil or item.Removed then return end

        InitializeTrackedItem(item)

        if item.Prefab.Identifier == "medheadset" then
            UpdateDisplay(item, true)
        end
    end, 50)
end)

-- Также инициализируем существующие предметы при загрузке
Timer.Wait(function()
    local allItems = Item.ItemList
    for _, item in ipairs(allItems) do
        InitializeTrackedItem(item)
    end
end, 2000) -- Задержка 2 секунды после загрузки
