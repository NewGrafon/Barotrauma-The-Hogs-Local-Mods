-- I'm sorry for the eyes of anyone looking at the GUI code.

local MultiLineTextBox = dofile(PerformanceFix.Path .. "/Lua/MultiLineTextBox.lua")
local easySettings = dofile(PerformanceFix.Path .. "/Lua/easysettings.lua")

-- Определение языка игрока (как в pda.lua)
local function GetPlayerLanguage()
    -- Пытаемся получить язык из настроек игры
    local lang = tostring(GameSettings.CurrentConfig.Language)
    
    -- Если не удалось, пробуем через Game.Client
    if (lang == "nil" or lang == nil) and CLIENT and Game.Client then
        lang = tostring(Game.Client.Language)
    end
    
    -- Возвращаем язык или английский по умолчанию
    if lang == "Russian" then
        return "ru"
    elseif lang == "Chinese" or lang == "ChineseSimplified" or lang == "zh-CN" then
        return "cn"
    else
        return "en" -- Default to English for other languages
    end
end

local LANG = GetPlayerLanguage()

-- Функция перевода
local function T(rus, eng, chn)
    if LANG == "ru" then
        return rus
    elseif LANG == "cn" then
        return chn
    else
        return eng
    end
end

Game.AddCommand("performancefix", T(
    "открывает меню улучшения производительности",
    "opens performance fix gui",
    "打开性能优化菜单"
), function ()
    PerformanceFix.ToggleGUI()
end)

local GUIComponent = LuaUserData.CreateStatic("Barotrauma.GUIComponent")

local function CommaStringToTable(str)
    local tbl = {}

    for word in string.gmatch(str, '([^,]+)') do
        table.insert(tbl, word)
    end

    return tbl
end

local function ClearElements(guicomponent, removeItself)
    local toRemove = {}

    for value in guicomponent.GetAllChildren() do
        table.insert(toRemove, value)
    end

    for index, value in pairs(toRemove) do
        value.RemoveChild(value)
    end

    if guicomponent.Parent and removeItself then
        guicomponent.Parent.RemoveChild(guicomponent)
    end
end

local function GetAmountOfPrefab(prefabs)
    local amount = 0
    for key, value in prefabs do
        amount = amount + 1
    end

    return amount
end

Hook.Add("stop", "PerformanceFix.CleanupGUI", function ()
    if selectedGUIText then
        selectedGUIText.Parent.RemoveChild(selectedGUIText)
    end

    if PerformanceFix.GUIFrame then
        ClearElements(PerformanceFix.GUIFrame, true)
    end
end)

PerformanceFix.ShowGUI = function (frame)
    PerformanceFix.GUIFrame = frame

    local config = easySettings.BasicList(frame)

    GUI.TextBlock(GUI.RectTransform(Vector2(1, 0.05), config.Content.RectTransform), T(
        "Активных предметов: " .. tostring(#Item.ItemList),
        "Active Items: " .. tostring(#Item.ItemList),
        "活跃物品: " .. tostring(#Item.ItemList)
    ), nil, nil)

    GUI.TextBlock(GUI.RectTransform(Vector2(1, 0.05), config.Content.RectTransform), T(
        "Активных персонажей: " .. tostring(#Character.CharacterList),
        "Active Characters: " .. tostring(#Character.CharacterList),
        "活跃角色: " .. tostring(#Character.CharacterList)
    ), nil, nil)

    GUI.TextBlock(GUI.RectTransform(Vector2(1, 0.05), config.Content.RectTransform), T(
        "Активных стен: " .. tostring(#Structure.WallList),
        "Active Walls: " .. tostring(#Structure.WallList),
        "活跃墙壁: " .. tostring(#Structure.WallList)
    ), nil, nil)

    GUI.TextBlock(GUI.RectTransform(Vector2(1, 0.05), config.Content.RectTransform), T(
        "Активных подлодок: " .. tostring(#Submarine.Loaded),
        "Active Submarines: " .. tostring(#Submarine.Loaded),
        "活跃潜艇: " .. tostring(#Submarine.Loaded)
    ), nil, nil)

    local shadowCastingLights = 0
    local drawBehindSubLights = 0
    for key, value in pairs(Item.ItemList) do
        local light = value.GetComponentString("LightComponent")

        if light and light.IsOn then
            if light.CastShadows then shadowCastingLights = shadowCastingLights + 1 end
            if light.DrawBehindSubs then drawBehindSubLights = drawBehindSubLights + 1 end
        end
    end

    GUI.TextBlock(GUI.RectTransform(Vector2(1, 0.05), config.Content.RectTransform), T(
        "Освещение за подлодкой: " .. tostring(drawBehindSubLights),
        "Draw Behind Sub Lights: " .. tostring(drawBehindSubLights),
        "潜艇后方光照: " .. tostring(drawBehindSubLights)
    ), nil, nil)

    GUI.TextBlock(GUI.RectTransform(Vector2(1, 0.05), config.Content.RectTransform), T(
        "Освещение с тенями: " .. tostring(shadowCastingLights),
        "Shadow Casting Lights: " .. tostring(shadowCastingLights),
        "投射阴影的光源: " .. tostring(shadowCastingLights)
    ), nil, nil)

    GUI.TextBlock(GUI.RectTransform(Vector2(1, 0.05), config.Content.RectTransform), "", nil, nil)

    GUI.TextBlock(GUI.RectTransform(Vector2(1, 0.05), config.Content.RectTransform), T(
        "Загружено префабов предметов: " .. tostring(GetAmountOfPrefab(ItemPrefab.Prefabs)),
        "Item Prefabs Loaded: " .. tostring(GetAmountOfPrefab(ItemPrefab.Prefabs)),
        "已加载物品预设: " .. tostring(GetAmountOfPrefab(ItemPrefab.Prefabs))
    ), nil, nil)

    GUI.TextBlock(GUI.RectTransform(Vector2(1, 0.05), config.Content.RectTransform), T(
        "Загружено префабов персонажей: " .. tostring(GetAmountOfPrefab(CharacterPrefab.Prefabs)),
        "Character Prefabs Loaded: " .. tostring(GetAmountOfPrefab(CharacterPrefab.Prefabs)),
        "已加载角色预设: " .. tostring(GetAmountOfPrefab(CharacterPrefab.Prefabs))
    ), nil, nil)

    GUI.TextBlock(GUI.RectTransform(Vector2(1, 0.05), config.Content.RectTransform), T(
        "Подлодок в памяти: " .. tostring(#SubmarineInfo.SavedSubmarines),
        "Submarines Loaded In Memory: " .. tostring(#SubmarineInfo.SavedSubmarines),
        "内存中的潜艇: " .. tostring(#SubmarineInfo.SavedSubmarines)
    ), nil, nil)

    GUI.TextBlock(GUI.RectTransform(Vector2(1, 0.1), config.Content.RectTransform), T(
        "Конфигурация улучшения производительности",
        "Performance Fix Config",
        "性能优化配置"
    ), nil, nil, GUI.Alignment.Center)

    local btn = GUI.Button(GUI.RectTransform(Vector2(1, 0.2), config.Content.RectTransform), T(
        "Сохранить конфиг и перезагрузить клиентскую часть",
        "Save Config and Reload Client-Side Performance Fix",
        "保存配置并重新加载客户端性能优化"
    ), GUI.Alignment.Center, "GUIButtonSmall")
    btn.OnClicked = function ()
        File.Write(PerformanceFix.Path .. "/config.json", json.serialize(PerformanceFix.Config))

        dofile(PerformanceFix.Path .. "/Lua/performancefix.lua")
    end

    GUI.TextBlock(GUI.RectTransform(Vector2(1, 0.1), config.Content.RectTransform), T(
        "Примечание: Для применения серверных конфигураций требуется перезапуск или команда reloadlua. Для выделенных серверов нужно редактировать файл config.json, этот GUI не будет работать.",
        "Note: Server configurations require you to either restart or use the command reloadlua to change it. For dedicated servers you need to edit the file config.json, this GUI wont work.",
        "注意：服务器配置需要重新启动或使用reloadlua命令才能更改。对于专用服务器，您需要编辑config.json文件，此GUI将无法工作。"
    ), nil, nil, GUI.Alignment.Center, true)

    GUI.TextBlock(GUI.RectTransform(Vector2(1, 0.05), config.Content.RectTransform), T(
        "Максимальный аккумулятор тактов",
        "Timing Accumulator Max",
        "定时累加器最大值"
    ), nil, nil, GUI.Alignment.Center, true)

    GUI.TextBlock(GUI.RectTransform(Vector2(1, 0.1), config.Content.RectTransform), T(
        "Более низкие значения заставляют игру более агрессивно пропускать такты, что может улучшить производительность при сильных лагах. Стандартное значение игры - 250.",
        "Lower values of Timing Accumulator Max means the game will more aggressively skip ticks, thus it can improve performance when your game is running really slowly. The games default is 250.",
        "较低的定时累加器最大值意味着游戏会更积极地跳过滴答，从而在游戏运行非常缓慢时提高性能。游戏默认值为250。"
    ), nil, nil, GUI.Alignment.Center, true)

    local accumulatorMax = GUI.NumberInput(GUI.RectTransform(Vector2(1, 0.1), config.Content.RectTransform), NumberType.Int)

    accumulatorMax.MinValueInt = 1
    accumulatorMax.MaxValueInt = 1000
    accumulatorMax.valueStep = 10

    if PerformanceFix.Config.accumulatorMax == nil then
        accumulatorMax.IntValue = 250
    else
        accumulatorMax.IntValue = PerformanceFix.Config.accumulatorMax
    end

    accumulatorMax.OnValueChanged = function ()
        PerformanceFix.Config.accumulatorMax = accumulatorMax.IntValue
    end

    GUI.TextBlock(GUI.RectTransform(Vector2(1, 0.05), config.Content.RectTransform), T(
        "Интервал обновления объектов карты (клиент)",
        "Client Map Entity Interval",
        "客户端地图实体更新间隔"
    ), nil, nil, GUI.Alignment.Center, true)

    local clientMapEntityUpdateInterval = GUI.NumberInput(GUI.RectTransform(Vector2(1, 0.1), config.Content.RectTransform), NumberType.Int)

    clientMapEntityUpdateInterval.MinValueInt = 1
    clientMapEntityUpdateInterval.MaxValueInt = 60

    clientMapEntityUpdateInterval.IntValue = PerformanceFix.Config.clientMapEntityUpdateInterval
    
    clientMapEntityUpdateInterval.OnValueChanged = function ()
        PerformanceFix.Config.clientMapEntityUpdateInterval = clientMapEntityUpdateInterval.IntValue
    end

    GUI.TextBlock(GUI.RectTransform(Vector2(1, 0.05), config.Content.RectTransform), T(
        "Интервал обновления объектов карты (сервер)",
        "Server Map Entity Interval",
        "服务器地图实体更新间隔"
    ), nil, nil, GUI.Alignment.Center, true)

    local serverMapEntityUpdateInterval = GUI.NumberInput(GUI.RectTransform(Vector2(1, 0.1), config.Content.RectTransform), NumberType.Int)

    serverMapEntityUpdateInterval.MinValueInt = 1
    serverMapEntityUpdateInterval.MaxValueInt = 60

    serverMapEntityUpdateInterval.IntValue = PerformanceFix.Config.serverMapEntityUpdateInterval
    
    serverMapEntityUpdateInterval.OnValueChanged = function ()
        PerformanceFix.Config.serverMapEntityUpdateInterval = serverMapEntityUpdateInterval.IntValue
    end

    GUI.TextBlock(GUI.RectTransform(Vector2(1, 0.05), config.Content.RectTransform), T(
        "Интервал обновления питания (только клиент, только мультиплеер)",
        "Powered Update Interval (Client-Side only and only works on multiplayer)",
        "供电器更新间隔（仅客户端，仅多人游戏）"
    ), nil, nil, GUI.Alignment.Center, true)

    local poweredUpdateInterval = GUI.NumberInput(GUI.RectTransform(Vector2(1, 0.1), config.Content.RectTransform), NumberType.Int)

    poweredUpdateInterval.MinValueInt = 1
    poweredUpdateInterval.MaxValueInt = 60

    poweredUpdateInterval.IntValue = PerformanceFix.Config.poweredUpdateInterval or 1
    
    poweredUpdateInterval.OnValueChanged = function ()
        PerformanceFix.Config.poweredUpdateInterval = poweredUpdateInterval.IntValue
    end

    GUI.TextBlock(GUI.RectTransform(Vector2(1, 0.05), config.Content.RectTransform), T(
        "Приоритетные предметы (клиент)",
        "Client High Priority Items",
        "客户端高优先级物品"
    ), nil, nil, GUI.Alignment.Center, true)

    local clientHighPriorityItems = MultiLineTextBox(config.Content.RectTransform, "", 0.2)

    clientHighPriorityItems.Text = table.concat(PerformanceFix.Config.clientItemHighPriority, ",")

    clientHighPriorityItems.OnTextChangedDelegate = function (textBox)
        PerformanceFix.Config.clientItemHighPriority = CommaStringToTable(textBox.Text)
    end

    GUI.TextBlock(GUI.RectTransform(Vector2(1, 0.05), config.Content.RectTransform), T(
        "Приоритетные предметы (сервер)",
        "Server High Priority Items",
        "服务器高优先级物品"
    ), nil, nil, GUI.Alignment.Center, true)

    local serverHighPriorityItems = MultiLineTextBox(config.Content.RectTransform, "", 0.2)

    serverHighPriorityItems.Text = table.concat(PerformanceFix.Config.serverItemHighPriority, ",")

    serverHighPriorityItems.OnTextChangedDelegate = function (textBox)
        PerformanceFix.Config.serverItemHighPriority = CommaStringToTable(textBox.Text)
    end

    GUI.TextBlock(GUI.RectTransform(Vector2(1, 0.05), config.Content.RectTransform), T(
        "Приоритетные компоненты (клиент)",
        "Client High Priority Components",
        "客户端高优先级组件"
    ), nil, nil, GUI.Alignment.Center, true)

    local clientHighPriorityComponents = MultiLineTextBox(config.Content.RectTransform, "", 0.2)

    clientHighPriorityComponents.Text = table.concat(PerformanceFix.Config.clientComponentPriority, ",")

    clientHighPriorityComponents.OnTextChangedDelegate = function (textBox)
        PerformanceFix.Config.clientComponentPriority = CommaStringToTable(textBox.Text)
    end

    GUI.TextBlock(GUI.RectTransform(Vector2(1, 0.05), config.Content.RectTransform), T(
        "Приоритетные компоненты (сервер)",
        "Server High Priority Components",
        "服务器高优先级组件"
    ), nil, nil, GUI.Alignment.Center, true)

    local serverHighPriorityComponents = MultiLineTextBox(config.Content.RectTransform, "", 0.2)

    serverHighPriorityComponents.Text = table.concat(PerformanceFix.Config.serverComponentPriority, ",")

    serverHighPriorityComponents.OnTextChangedDelegate = function (textBox)
        PerformanceFix.Config.serverComponentPriority = CommaStringToTable(textBox.Text)
    end

    GUI.TextBlock(GUI.RectTransform(Vector2(1, 0.1), config.Content.RectTransform), T(
        "Конфигурация обновления персонажей (экспериментально)",
        "Character Update Config (Extra Experimental)",
        "角色更新配置（实验性功能）"
    ), nil, nil, GUI.Alignment.Center, true)

    GUI.TextBlock(GUI.RectTransform(Vector2(1, 0.05), config.Content.RectTransform), T(
        "Интервал обновления персонажей (клиент)",
        "Client Character Update Interval",
        "客户端角色更新间隔"
    ), nil, nil, GUI.Alignment.Center, true)
    
    local clientCharacterUpdateInterval = GUI.NumberInput(GUI.RectTransform(Vector2(1, 0.1), config.Content.RectTransform), NumberType.Int)

    clientCharacterUpdateInterval.MinValueInt = 1
    clientCharacterUpdateInterval.MaxValueInt = 60

    clientCharacterUpdateInterval.IntValue = PerformanceFix.Config.clientCharacterUpdateInterval
    
    clientCharacterUpdateInterval.OnValueChanged = function ()
        PerformanceFix.Config.clientCharacterUpdateInterval = clientCharacterUpdateInterval.IntValue
    end

    GUI.TextBlock(GUI.RectTransform(Vector2(1, 0.05), config.Content.RectTransform), T(
        "Интервал обновления персонажей (сервер)",
        "Server Character Update Interval",
        "服务器角色更新间隔"
    ), nil, nil, GUI.Alignment.Center, true)

    local serverCharacterUpdateInterval = GUI.NumberInput(GUI.RectTransform(Vector2(1, 0.1), config.Content.RectTransform), NumberType.Int)

    serverCharacterUpdateInterval.MinValueInt = 1
    serverCharacterUpdateInterval.MaxValueInt = 60

    serverCharacterUpdateInterval.IntValue = PerformanceFix.Config.serverCharacterUpdateInterval

    serverCharacterUpdateInterval.OnValueChanged = function ()
        PerformanceFix.Config.serverCharacterUpdateInterval = serverCharacterUpdateInterval.IntValue
    end

    GUI.TextBlock(GUI.RectTransform(Vector2(1, 0.05), config.Content.RectTransform), T(
        "Приоритетные персонажи",
        "High Priority Characters",
        "高优先级角色"
    ), nil, nil, GUI.Alignment.Center, true)

    local highPriorityCharacters = MultiLineTextBox(config.Content.RectTransform, "", 0.2)

    highPriorityCharacters.Text = table.concat(PerformanceFix.Config.highPriorityCharacters, ",")

    highPriorityCharacters.OnTextChangedDelegate = function (textBox)
        PerformanceFix.Config.highPriorityCharacters = CommaStringToTable(textBox.Text)
    end

    GUI.TextBlock(GUI.RectTransform(Vector2(1, 0.1), config.Content.RectTransform), T(
        "ВНИМАНИЕ: НИЖЕПЕРЕЧИСЛЕННЫЕ КОНФИГИ ПОСТОЯННЫ ДЛЯ ОДИНОЧНОЙ ИГРЫ, В МУЛЬТИПЛЕЕРЕ ОНИ СБРАСЫВАЮТСЯ ПЕРЕЗАПУСКОМ РАУНДА.",
        "WARNING: THE BELOW CONFIGS ARE PERMANENT FOR SINGLEPLAYER AND IN MULTIPLAYER ARE REVERSIBLE BY RESTARTING THE ROUND.",
        "警告：以下配置在单人游戏中是永久性的，在多人游戏中可通过重新开始回合来重置。"
    ), nil, nil, GUI.Alignment.Center, true)

    local singleplayerPermanent = GUI.TickBox(GUI.RectTransform(Vector2(1, 0.2), config.Content.RectTransform), T(
        "Разрешить постоянные конфиги в одиночной игре",
        "Allow Permanent Configs In Singleplayer",
        "允许在单人游戏中应用永久配置"
    ))

    singleplayerPermanent.Selected = PerformanceFix.Config.allowSingleplayerPermanentConfigs or false

    singleplayerPermanent.OnSelected = function ()
        PerformanceFix.Config.allowSingleplayerPermanentConfigs = singleplayerPermanent.State == GUIComponent.ComponentState.Selected
    end

    local shadowCasting = GUI.TickBox(GUI.RectTransform(Vector2(1, 0.2), config.Content.RectTransform), T(
        "Отключить освещение с тенями",
        "Disable Shadow Casting Lights",
        "禁用投射阴影的光源"
    ))

    shadowCasting.Selected = PerformanceFix.Config.disableShadowCastingLights

    shadowCasting.OnSelected = function ()
        PerformanceFix.Config.disableShadowCastingLights = shadowCasting.State == GUIComponent.ComponentState.Selected
    end

    local drawBehindSub = GUI.TickBox(GUI.RectTransform(Vector2(1, 0.2), config.Content.RectTransform), T(
        "Отключить освещение за подлодкой",
        "Disable Draw Behind Subs Lights",
        "禁用潜艇后方光源"
    ))

    drawBehindSub.Selected = PerformanceFix.Config.disableDrawBehindSubsLights

    drawBehindSub.OnSelected = function ()
        PerformanceFix.Config.disableDrawBehindSubsLights = drawBehindSub.State == GUIComponent.ComponentState.Selected
    end

    local hideInGameWires = GUI.TickBox(GUI.RectTransform(Vector2(1, 0.2), config.Content.RectTransform), T(
        "Скрыть провода в игре",
        "Hide In Game Wires",
        "在游戏中隐藏电线"
    ))

    hideInGameWires.Selected = PerformanceFix.Config.hideInGameWires

    hideInGameWires.OnSelected = function ()
        PerformanceFix.Config.hideInGameWires = hideInGameWires.State == GUIComponent.ComponentState.Selected
    end

    local hideInGameComponents = GUI.TickBox(GUI.RectTransform(Vector2(1, 0.2), config.Content.RectTransform), T(
        "Скрыть компоненты в игре",
        "Hide In Game Components",
        "在游戏中隐藏组件"
    ))

    hideInGameComponents.Selected = PerformanceFix.Config.hideInGameComponents

    hideInGameComponents.OnSelected = function ()
        PerformanceFix.Config.hideInGameComponents = hideInGameComponents.State == GUIComponent.ComponentState.Selected
    end
end

PerformanceFix.ToggleGUI = function ()
    GUI.GUI.TogglePauseMenu()

    if GUI.GUI.PauseMenuOpen then
        PerformanceFix.ShowGUI(GUI.GUI.PauseMenu)
    end
end