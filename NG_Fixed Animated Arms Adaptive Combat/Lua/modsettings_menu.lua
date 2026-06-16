if not CLIENT then return end

AAAC = AAAC or dofile(PerformanceFix.Path .. "/Lua/aaac_settings.lua")
local easySettings = dofile(PerformanceFix.Path .. "/Lua/easysettings.lua")
local GUIComponent = LuaUserData.CreateStatic("Barotrauma.GUIComponent")

local function GetPlayerLanguage()
    local lang = tostring(GameSettings.CurrentConfig.Language)

    if (lang == "nil" or lang == nil) and CLIENT and Game.Client then
        lang = tostring(Game.Client.Language)
    end

    if lang == "Russian" then
        return "ru"
    elseif lang == "Chinese" or lang == "ChineseSimplified" or lang == "zh-CN" then
        return "cn"
    else
        return "en"
    end
end

local LANG = GetPlayerLanguage()
AAAC.MenuBindings = AAAC.MenuBindings or {}
AAAC.__suppressMenuCallbacks = AAAC.__suppressMenuCallbacks or false

local function T(rus, eng, chn)
    if LANG == "ru" then
        return rus
    elseif LANG == "cn" then
        return chn
    else
        return eng
    end
end

local function ClearElements(guicomponent, removeItself)
    if guicomponent == nil then return end

    local toRemove = {}
    for value in guicomponent.GetAllChildren() do
        table.insert(toRemove, value)
    end

    for _, value in pairs(toRemove) do
        value.RemoveChild(value)
    end

    if guicomponent.Parent and removeItself then
        guicomponent.Parent.RemoveChild(guicomponent)
    end
end

local function isPrivileged()
    return AAAC.IsPrivilegedClient and AAAC.IsPrivilegedClient(Game.Client)
end

local function getEI()
    local ok1, config = pcall(function() return require("EnhancedImmersion.Config.init") end)
    local ok2, utils = pcall(function() return require("EnhancedImmersion.Utils.utils") end)
    return ok1 and config or nil, ok2 and utils or nil
end

local function applyEIConfig(tempConfig)
    local config, utils = getEI()
    if not config or not utils or not EI then return false end

    EI.Config.LoadConfig(tempConfig)

    if Game.IsMultiplayer and AAAC.IsPrivilegedClient(Game.Client) then
        local msg = utils.writeConfigNetworkMessage(tempConfig)
        Networking.Send(msg)
    end

    if not EI.Config.Values.SyncConfig or AAAC.IsPrivilegedClient(Game.Client) or Game.IsSingleplayer then
        EI.Config.WriteToFile()
        utils.UpdateGlobalZoomScale()
        return true
    end

    return false
end

local function openPerformanceFix()
    if AAAC.GUIFrame then
        ClearElements(AAAC.GUIFrame, true)
        AAAC.GUIFrame = nil
    end

    if PerformanceFix and PerformanceFix.ShowGUI then
        PerformanceFix.ShowGUI(GUI.GUI.PauseMenu)
    end
end

local function flashMenu(menuContent, ok)
    if not menuContent then return end
    if ok then
        menuContent.Flash(Color(154, 213, 163, 255))
    else
        menuContent.Flash(Color(213, 154, 154, 255))
    end
end

local function pushServerConfigImmediate(serverConfig, canEditServer, menuContent)
    if not canEditServer then return false end
    local ok = AAAC.PushServerConfig(serverConfig)
    flashMenu(menuContent, ok)
    return ok
end

local function bindTickBoxChanged(toggle, callback)
    if not toggle or type(callback) ~= "function" then return end

    local function invoke()
        if AAAC.__suppressMenuCallbacks then return end
        callback(toggle.State == GUIComponent.ComponentState.Selected)
    end

    toggle.OnSelected = function()
        invoke()
    end

    pcall(function()
        toggle.OnDeselected = function()
            invoke()
        end
    end)

    pcall(function()
        toggle.OnClicked = function()
            Timer.Wait(function()
                invoke()
            end, 1)
            return true
        end
    end)
end


local function applyEIConfigImmediate(tempEIConfig, menuContent)
    local ok = applyEIConfig(tempEIConfig)
    flashMenu(menuContent, ok)
    return ok
end

Hook.Add("stop", "AAAC.CleanupGUI", function()
    if AAAC.GUIFrame then
        ClearElements(AAAC.GUIFrame, true)
        AAAC.GUIFrame = nil
    end
end)

local function addSectionTitle(parent, text)
    return GUI.TextBlock(GUI.RectTransform(Vector2(1, 0.055), parent.RectTransform), text, nil, GUI.GUIStyle.SubHeadingFont, GUI.Alignment.Center, true)
end

local function createCompactSlider(parent, labelText, minValue, maxValue, initialValue, onMoved, enabled)
    GUI.TextBlock(GUI.RectTransform(Vector2(1, 0.045), parent.RectTransform), labelText, nil, nil, GUI.Alignment.Center, true)

    local row = GUI.LayoutGroup(GUI.RectTransform(Vector2(1, 0.06), parent.RectTransform), true)
    row.Stretch = true
    row.RelativeSpacing = 0.02

    local slider = GUI.ScrollBar(GUI.RectTransform(Vector2(0.78, 1), row.RectTransform), 0.1, nil, "GUISlider")
    slider.Range = Vector2(minValue, maxValue)
    slider.BarScrollValue = initialValue
    slider.Enabled = enabled ~= false

    local valueText = GUI.TextBlock(GUI.RectTransform(Vector2(0.2, 1), row.RectTransform), tostring(math.floor(initialValue)), nil, nil, GUI.Alignment.Center)

    slider.OnMoved = function()
        local value = math.floor(slider.BarScrollValue + 0.5)
        valueText.Text = tostring(value)
        if AAAC.__suppressMenuCallbacks then return end
        if onMoved then onMoved(value) end
    end

    return slider, valueText
end

local function addServerToggleBlock(parent, labelText, featureName, serverConfig, canEditServer, menuContent, tooltip)
    GUI.TextBlock(GUI.RectTransform(Vector2(1, 0.04), parent.RectTransform), labelText, nil, nil, nil, true)

    local row = GUI.LayoutGroup(GUI.RectTransform(Vector2(1, 0.055), parent.RectTransform), true)
    row.Stretch = true
    row.RelativeSpacing = 0.02

    GUI.TextBlock(GUI.RectTransform(Vector2(0.62, 1), row.RectTransform), "", nil, nil, nil, true)

    local toggle = GUI.TickBox(GUI.RectTransform(Vector2(0.36, 1), row.RectTransform), T("Включить на сервере", "Enable on server", "在服务器上启用"))
    local configKey = AAAC.GetFeatureEnabledKey(featureName)
    toggle.Selected = serverConfig[configKey] ~= false
    toggle.Enabled = canEditServer
    toggle.ToolTip = tooltip or T(
        "Админ сервера может отключить эту функцию для всех.",
        "Server admin can disable this feature for everyone.",
        "服务器管理员可以为所有人禁用此功能。"
    )
    AAAC.MenuBindings[featureName] = { toggle = toggle }
    bindTickBoxChanged(toggle, function(isSelected)
        serverConfig[configKey] = isSelected
        pushServerConfigImmediate(serverConfig, canEditServer, menuContent)
    end)
end

local function addKeybindBlock(parent, labelText, settingName, defaultKeys, featureName)
    GUI.TextBlock(GUI.RectTransform(Vector2(1, 0.04), parent.RectTransform), labelText, nil, nil, nil, true)

    local row = GUI.LayoutGroup(GUI.RectTransform(Vector2(1, 0.065), parent.RectTransform), true)
    row.Stretch = true
    row.RelativeSpacing = 0.02

    local button = GUI.Button(
        GUI.RectTransform(Vector2(1, 1), row.RectTransform),
        AAAC.GetKeybindString(AAAC.GetKeybind(settingName, defaultKeys)),
        GUI.Alignment.Center,
        "GUITextBox"
    )

    button.OnClicked = function()
        button.Text = "< ... >"
        AAAC.CaptureNextKeybind(function(keys)
            if keys == nil then
                button.Text = AAAC.GetKeybindString(AAAC.GetKeybind(settingName, defaultKeys))
                return
            end
            AAAC.SetKeybind(settingName, keys)
            button.Text = AAAC.GetKeybindString(keys)
        end)
        return true
    end

    AAAC.MenuBindings[featureName] = AAAC.MenuBindings[featureName] or {}
end

local function refreshBoundControlsFromConfig()
    local refs = AAAC.MenuBindings or {}
    local config = AAAC.GetServerConfig()
    if not refs or not config then return end

    AAAC.__suppressMenuCallbacks = true

    local function setToggle(ref, value)
        if ref and ref.toggle then
            ref.toggle.Selected = value ~= false
        end
    end

    setToggle(refs.AutoReload, config.EnableAutoReload)
    setToggle(refs.QuickSwap, config.EnableQuickSwap)
    setToggle(refs.ShotPain, config.EnableShotPain)
    setToggle(refs.ShotTinnitus, config.EnableShotTinnitus)
    setToggle(refs.Stamina, config.EnableStamina)


    AAAC.__suppressMenuCallbacks = false
end

local function BuildAAACGUI(frame)
    pcall(function() AAAC.RequestServerConfig() end)
    AAAC.MenuBindings = {}

    if AAAC.GUIFrame then
        ClearElements(AAAC.GUIFrame, true)
        AAAC.GUIFrame = nil
    end

    local canEditServer = Game.IsSingleplayer or isPrivileged()
    local tempServerConfig = AAAC.GetServerConfig()
    tempServerConfig = {
        Version = tempServerConfig.Version,
        EnableAutoReload = tempServerConfig.EnableAutoReload,
        EnableQuickSwap = tempServerConfig.EnableQuickSwap,
        EnableStrangle = tempServerConfig.EnableStrangle,
        EnableShotPain = tempServerConfig.EnableShotPain,
        EnableShotTinnitus = tempServerConfig.EnableShotTinnitus,
        EnableStamina = tempServerConfig.EnableStamina,
        EnableGrabAll = true
    }

    local eiConfig, eiUtils = getEI()
    local tempEIConfig = nil
    if eiConfig then
        local baseConfig = eiConfig.ReadFromFile()
        tempEIConfig = {
            CameraZoom = baseConfig.CameraZoom,
            PeriscopeOffset = baseConfig.PeriscopeOffset,
            AimOffset = baseConfig.AimOffset,
            SyncConfig = baseConfig.SyncConfig
        }
    end

    local menuContent = GUI.Frame(GUI.RectTransform(Vector2(0.31, 0.82), frame.RectTransform, GUI.Anchor.Center), "GUIFrame")
    AAAC.GUIFrame = menuContent

    local content = GUI.ListBox(GUI.RectTransform(Vector2(1, 0.94), menuContent.RectTransform, GUI.Anchor.TopCenter))
    easySettings.CloseButton(menuContent)

    addSectionTitle(content.Content, T("Настройки Animated Arms", "Animated Arms Settings", "Animated Arms 设置"))

    GUI.TextBlock(
        GUI.RectTransform(Vector2(1, 0.05), content.Content.RectTransform),
        T(
            "Нажми поле и затем клавишу. Esc отменяет привязку. Здесь можно менять бинды и эффекты мода.",
            "Click a field and press a key. Esc cancels binding. Here you can change keybinds and mod effects.",
            "点击字段后按下按键，Esc 取消绑定。你可以在这里修改按键和模组效果。"
        ),
        nil, nil, GUI.Alignment.Center, true
    )

    addSectionTitle(content.Content, T("Бинды функций", "Function keybinds", "功能按键绑定"))

    addKeybindBlock(content.Content, T("Быстрая смена", "Quick swap", "快速切换"), "QuickSwap", {"F"}, "QuickSwap")
    addKeybindBlock(content.Content, T("Перезарядка", "Reload", "装填"), "AutoReload", {"CapsLock"}, "AutoReload")
    addKeybindBlock(content.Content, T("Меню взаимодействия", "Interact menu", "互动菜单"), "Interact", {"N"}, "Interact")
    addKeybindBlock(content.Content, T("Положение лёжа", "Prone toggle", "卧倒切换"), "CrawlToggle", {"M"}, "CrawlToggle")

    addSectionTitle(content.Content, T("Боевые эффекты", "Combat effects", "战斗效果"))

    addServerToggleBlock(
        content.Content,
        T("Система боли от попаданий", "Shot pain system", "命中痛苦系统"),
        "ShotPain",
        tempServerConfig,
        canEditServer,
        menuContent,
        T(
            "Отключает affliction spasm_shot у всех игроков.",
            "Disables the spasm_shot affliction for everyone.",
            "为所有人禁用 spasm_shot 病症。"
        )
    )

    addServerToggleBlock(
        content.Content,
        T("Система оглушения от выстрела", "Shot tinnitus system", "射击耳鸣系统"),
        "ShotTinnitus",
        tempServerConfig,
        canEditServer,
        menuContent,
        T(
            "Отключает affliction tinnitus_shot у всех игроков.",
            "Disables the tinnitus_shot affliction for everyone.",
            "为所有人禁用 tinnitus_shot 病症。"
        )
    )

    addServerToggleBlock(
        content.Content,
        T("Система стамины", "Stamina system", "体力系统"),
        "Stamina",
        tempServerConfig,
        canEditServer,
        menuContent,
        T(
            "Отключает affliction stamina у всех игроков.",
            "Disables the stamina affliction for everyone.",
            "为所有人禁用 stamina 病症。"
        )
    )

    if eiConfig and tempEIConfig then
        addSectionTitle(content.Content, T("Управление камерой", "Camera control", "镜头控制"))

        local eiLocked = Game.IsMultiplayer and eiConfig.Values and eiConfig.Values.SyncConfig and not canEditServer
        local cameraEnabled = not eiLocked

        local camSlider = createCompactSlider(content.Content, T("Отдаление камеры", "Camera zoom", "镜头缩放"), 0, 100, tempEIConfig.CameraZoom, function(value)
            tempEIConfig.CameraZoom = value
            applyEIConfigImmediate(tempEIConfig, menuContent)
        end, cameraEnabled)

        if EI and EI.Config and EI.Config.DisableCameraZoom and camSlider then
            camSlider.Enabled = false
            camSlider.ToolTip = T("Отключено из-за несовместимого мода камеры.", "Disabled because of an incompatible camera mod.", "由于与其他镜头模组冲突，此项已禁用。")
        end

        createCompactSlider(content.Content, T("Смещение перископа", "Periscope offset", "潜望镜偏移"), 0, 100, tempEIConfig.PeriscopeOffset, function(value)
            tempEIConfig.PeriscopeOffset = value
            applyEIConfigImmediate(tempEIConfig, menuContent)
        end, cameraEnabled)

        createCompactSlider(content.Content, T("Смещение при прицеливании", "Aim offset", "瞄准偏移"), 0, 100, tempEIConfig.AimOffset, function(value)
            tempEIConfig.AimOffset = value
            applyEIConfigImmediate(tempEIConfig, menuContent)
        end, cameraEnabled)

        local syncTick = GUI.TickBox(GUI.RectTransform(Vector2(1, 0.055), content.Content.RectTransform), T("Синхронизировать настройки камеры с сервером", "Sync camera settings with server", "与服务器同步镜头设置"))
        syncTick.Selected = tempEIConfig.SyncConfig == true
        syncTick.Enabled = canEditServer
        bindTickBoxChanged(syncTick, function(isSelected)
            tempEIConfig.SyncConfig = isSelected
            applyEIConfigImmediate(tempEIConfig, menuContent)
        end)

        if eiLocked then
            syncTick.ToolTip = T("Сервер принудительно синхронизирует камеру.", "Server forces camera sync.", "服务器强制同步镜头设置。")
        end

    end

    addSectionTitle(content.Content, T("Другие меню", "Other menus", "其他菜单"))

    local pfButton = GUI.Button(
        GUI.RectTransform(Vector2(1, 0.06), content.Content.RectTransform),
        T("Открыть Performance Fix", "Open Performance Fix", "打开 Performance Fix"),
        GUI.Alignment.Center,
        "GUIButtonSmall"
    )
    pfButton.OnClicked = function()
        openPerformanceFix()
        return true
    end

    refreshBoundControlsFromConfig()
end


AAAC.OnServerConfigChanged = function()
    if not CLIENT then return end
    if AAAC.GUIFrame and GUI.GUI and GUI.GUI.PauseMenuOpen and GUI.GUI.PauseMenu then
        pcall(refreshBoundControlsFromConfig)
    end
end

AAAC.ShowGUI = function(frame)
    local ok, err = pcall(function()
        BuildAAACGUI(frame)
    end)

    if not ok then
        print("[AAAC] Failed to open settings menu: " .. tostring(err))
        if AAAC.GUIFrame then
            ClearElements(AAAC.GUIFrame, true)
            AAAC.GUIFrame = nil
        end
    end
end

easySettings.AddMenu(T("Animated Arms", "Animated Arms", "Animated Arms"), AAAC.ShowGUI, {
    Color = Color(93, 161, 109, 255)
})

AAAC.ToggleGUI = function()
    GUI.GUI.TogglePauseMenu()

    if GUI.GUI.PauseMenuOpen then
        easySettings.Open(T("Animated Arms", "Animated Arms", "Animated Arms"))
    end
end
