local easySettings = {}

easySettings.Settings = {}

local GUIComponent = LuaUserData.CreateStatic("Barotrauma.GUIComponent")

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
        return "en"
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

local function GetChildren(comp)
    local tbl = {}
    for value in comp.GetAllChildren() do
        table.insert(tbl, value)
    end
    return tbl
end

Hook.Patch("Barotrauma.GUI", "TogglePauseMenu", {}, function ()
    if GUI.GUI.PauseMenuOpen then
        local frame = GUI.GUI.PauseMenu

        local list = GetChildren(GetChildren(frame)[2])[1]

        for key, value in pairs(easySettings.Settings) do
            local button = GUI.Button(GUI.RectTransform(Vector2(1, 0.1), list.RectTransform), value.Name, GUI.Alignment.Center, "GUIButtonSmall")

            if value.Options and value.Options.Color then
                pcall(function() button.Color = value.Options.Color end)
                pcall(function() button.HoverColor = value.Options.Color end)
                pcall(function() button.SelectedColor = value.Options.Color end)
            end

            button.OnClicked = function ()
                value.OnOpen(frame)
            end
        end
    end
end, Hook.HookMethodType.After)

easySettings.SaveTable = function (path, tbl)
    File.Write(path, json.serialize(tbl))
end

easySettings.LoadTable = function (path)
    if not File.Exists(path) then
        return {}
    end

    return json.parse(File.Read(path))
end

easySettings.AddMenu = function (name, onOpen, options)
    table.insert(easySettings.Settings, {Name = name, OnOpen = onOpen, Options = options or {}})
end

easySettings.BasicList = function (parent, size)
    local menuContent = GUI.Frame(GUI.RectTransform(size or Vector2(0.6, 0.6), parent.RectTransform, GUI.Anchor.Center))
    local menuList = GUI.ListBox(GUI.RectTransform(Vector2(1, 0.95), menuContent.RectTransform, GUI.Anchor.TopCenter))

    easySettings.CloseButton(menuContent)

    return menuList
end

easySettings.TickBox = function (parent, text, onSelected, state)
    if state == nil then state = true end

    local tickBox = GUI.TickBox(GUI.RectTransform(Vector2(1, 0.2), parent.RectTransform), text)
    tickBox.Selected = state
    tickBox.OnSelected = function ()
        onSelected(tickBox.State == GUIComponent.ComponentState.Selected)
    end

    return tickBox
end

easySettings.Slider = function (parent, min, max, onSelected, value)
    local scrollBar = GUI.ScrollBar(GUI.RectTransform(Vector2(1, 0.1), parent.RectTransform), 0.1, nil, "GUISlider")
    scrollBar.Range = Vector2(min, max)
    scrollBar.BarScrollValue = value or max / 2
    scrollBar.OnMoved = function ()
        onSelected(scrollBar.BarScrollValue)
    end

    return scrollBar
end

easySettings.CloseButton = function (parent)
    local button = GUI.Button(GUI.RectTransform(Vector2(1, 0.05), parent.RectTransform, GUI.Anchor.BottomCenter), T(
        "Закрыть",
        "Close",
        "关闭"
    ), GUI.Alignment.Center, "GUIButton")

    button.OnClicked = function ()
        GUI.GUI.TogglePauseMenu()
    end

    return button
end

easySettings.Open = function (name)
    for key, value in pairs(easySettings.Settings) do
        if value.Name == name then
            local frame = GUI.GUI.PauseMenu

            value.OnOpen(frame)
        end
    end
end

return easySettings