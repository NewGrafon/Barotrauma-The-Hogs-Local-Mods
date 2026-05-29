-- interact1.lua
local menuItems = {
    ["cursor-anim"] = true,
    ["cursor-anim1"] = true,
    ["push-anim"] = true,
    ["push-anim1"] = true,
    ["hands-up-anim"] = true,
    ["spit-anim"] = true,
    ["fisththrust"] = true,
    ["fisthdamagevolume"] = true,
    ["pushthrust"] = true,
    ["pushdamagevolume"] = true,
    ["grab-anim"] = true,
    ["grabthrust"] = true,
    ["grabdamagevolume"] = true,
    ["point-anim"] = true,
    ["point-anim1"] = true,
    ["handcuffs-anim"] = true,
    ["actor-menu-proxy"] = true,
    ["actor-combat-proxy"] = true,
}

-- Добавляем конфигурацию для UI кнопки (как во втором файле)
local CFG = {
    Debug = false,
    ButtonText = "interactions",
    ItemIdentifier = "actor-menu-proxy",  -- Предмет для спавна
    MainWidth = 200,
    MainHeight = 28,
    RightClearance = 430,
    TopOffset = 20,
    ClientClickCooldown = 0.20,
    MsgSpawnRequest = "rbd_interactions_spawn_proxy",  -- Сообщение для мультиплеера
}

-- Вспомогательные функции (адаптированы из второго файла)
local function now()
    local t = nil
    if Game and Game.GameSession then t = Game.GameSession.RoundDuration end
    return tonumber(t) or os.clock()
end

local function getPrefab()
    local prefab = nil
    pcall(function() prefab = ItemPrefab.GetItemPrefab(CFG.ItemIdentifier) end)
    if not prefab then
        pcall(function() prefab = ItemPrefab.Prefabs[CFG.ItemIdentifier] end)
    end
    return prefab
end

local function spawnProxyForCharacter(character)
    if not character or not character.Inventory then 
        print("[ERROR] No valid character or inventory")
        return false 
    end
    
    local prefab = getPrefab()
    if not prefab then
        print("[ERROR] Item prefab not found: " .. tostring(CFG.ItemIdentifier))
        return false
    end
    
    -- Спавним предмет в инвентарь персонажа
    pcall(function()
        Entity.Spawner.AddItemToSpawnQueue(prefab, character.Inventory, nil, nil, function(item)
            if item then 
                print("[RBD-INTERACTIONS] Spawned " .. tostring(item.Name) .. " into inventory")
            end
        end)
    end)
    
    return true
end

if CLIENT then
    local lastToggleTime = 0
    local TOGGLE_COOLDOWN = 0.25 
    
    -- UI элементы (как во втором файле)
    local GameMainStatic = LuaUserData.CreateStatic("Barotrauma.GameMain")
    local extraUIWidth = GUI.UIWidth < GameMainStatic.GraphicsWidth and (GameMainStatic.GraphicsWidth - GUI.UIWidth) or 0
    
    local UI = {
        frame = nil,
        mainBtn = nil,
        mainX = 0,
        topY = 0,
        lastClickAt = -1000,
    }
    
    -- Функция создания UI (как во втором файле)
    local function ensureUI()
        if UI.frame and UI.mainBtn then return true end
        if not GUI then return false end
        
        UI.frame = GUI.Frame(GUI.RectTransform(Vector2(1, 1)), nil)
        UI.frame.CanBeFocused = false
        
        local mainW = math.floor((CFG.MainWidth or 200) * GUI.xScale)
        local mainH = math.max(28, math.floor(CFG.MainHeight or 28))
        local rightClearance = math.floor((CFG.RightClearance or 430) * GUI.xScale)
        
        UI.topY = math.floor((-GameMainStatic.GraphicsHeight * 0.5) + (CFG.TopOffset or 20))
        UI.mainX = math.floor((GUI.UIWidth * 0.5) - rightClearance - (mainW * 0.5) - extraUIWidth)
        
        UI.mainBtn = GUI.Button(
            GUI.RectTransform(Point(mainW, mainH), UI.frame.RectTransform, GUI.Anchor.Center),
            CFG.ButtonText,
            GUI.Alignment.Center,
            "GUIButtonSmall"
        )
        
        UI.mainBtn.RectTransform.AbsoluteOffset = Point(UI.mainX, UI.topY)
        UI.mainBtn.Visible = true
        UI.mainBtn.Enabled = true
        
        -- Обработчик нажатия на кнопку
        UI.mainBtn.OnClicked = function()
            local t = now()
            if (t - (UI.lastClickAt or -1000)) < (CFG.ClientClickCooldown or 0.20) then return true end
            UI.lastClickAt = t
            
            local character = Character.Controlled
            if character == nil or character.Inventory == nil then 
                print("[ERROR] No controlled character")
                return true 
            end
            
            if Game.IsMultiplayer then
                -- В мультиплеере отправляем запрос на сервер
                local msg = Networking.Start(CFG.MsgSpawnRequest)
                if msg then
                    Networking.Send(msg)
                end
            else
                -- В одиночной игре спавним напрямую
                spawnProxyForCharacter(character)
            end
            
            return true
        end
        
        return true
    end
    
    -- Функция обновления UI (как во втором файле)
    local function refreshUI()
        if not ensureUI() then return end
        UI.mainBtn.Visible = true
        UI.mainBtn.Enabled = true
        if UI.mainBtn.Text ~= CFG.ButtonText then 
            UI.mainBtn.Text = CFG.ButtonText 
        end
        UI.mainBtn.RectTransform.AbsoluteOffset = Point(UI.mainX, UI.topY)
    end
    
    -- Добавляем UI в цикл обновления (как во втором файле)
    Hook.Patch("Barotrauma.GameScreen", "AddToGUIUpdateList", function()
        if UI.frame then UI.frame.AddToGUIUpdateList(false, 1) end
    end)
    Hook.Patch("Barotrauma.SubEditorScreen", "AddToGUIUpdateList", function()
        if UI.frame then UI.frame.AddToGUIUpdateList(false, 1) end
    end)
    Hook.Patch("Barotrauma.NetLobbyScreen", "AddToGUIUpdateList", function()
        if UI.frame then UI.frame.AddToGUIUpdateList(false, 1) end
    end)
    
    -- Постоянное обновление UI
    Hook.Add("think", "RBD_INTERACTIONS_ALWAYS_VISIBLE", function()
        refreshUI()
    end)
    
    -- Обработчик клавиши N (оригинальный функционал)
    Hook.Add("keyUpdate", "actor_menu_toggle", function()
        local currentTime = 0
        if Timer then currentTime = Timer.GetTime() end
        if currentTime == nil then currentTime = 0 end
        
        if currentTime - lastToggleTime < TOGGLE_COOLDOWN then return end
        
        -- Check configured interact key (default N)
        local interactPressed = false
        if AAAC and AAAC.IsConfiguredKeyHit then
            interactPressed = AAAC.IsConfiguredKeyHit("Interact", {"N"})
        else
            interactPressed = PlayerInput.KeyHit(Keys.N)
        end
        if not interactPressed then return end 
        
        -- Ignore if typing in chat
        if GUI.KeyboardDispatcher.Subscriber then return end
        
        local character = Character.Controlled
        if character == nil or character.Inventory == nil then return end
        
        local inventory = character.Inventory
        local foundAny = false
        
        -- 1. Check if ANY combat menu item is in inventory. If so, drop them all to close the menu.
        for i = 0, inventory.Capacity - 1 do
            local item = inventory.GetItemAt(i)
            if item and menuItems[item.Prefab.Identifier.Value] then
                item.Drop(character)
                foundAny = true
            end
        end
        
        -- 2. If nothing was found to remove, we want to open the menu.
        if not foundAny then
            if Game.IsMultiplayer then
                local msg = Networking.Start("SpawnActorMenu")
                if msg then
                    Networking.Send(msg)
                end
            else
                -- Singleplayer logic: Spawn directly
                local prefab1 = ItemPrefab.GetItemPrefab("actor-menu-proxy")
                local prefab2 = ItemPrefab.GetItemPrefab("actor-combat-proxy")
                if prefab1 then Entity.Spawner.AddItemToSpawnQueue(prefab1, inventory) end
                if prefab2 then Entity.Spawner.AddItemToSpawnQueue(prefab2, inventory) end
            end
        end
        
        lastToggleTime = currentTime
    end)
end

if SERVER then
    -- Обработчик для спавна через UI кнопку
    Networking.Receive(CFG.MsgSpawnRequest, function(msg, client)
        local character = client.Character
        if not character or not character.Inventory then return end
        
        -- Используем ту же функцию спавна
        spawnProxyForCharacter(character)
    end)
    
    -- Оригинальный обработчик для клавиши N
    Networking.Receive("SpawnActorMenu", function(msg, client)
        local character = client.Character
        if not character or not character.Inventory then return end
        
        local prefab1 = ItemPrefab.GetItemPrefab("actor-menu-proxy")
        local prefab2 = ItemPrefab.GetItemPrefab("actor-combat-proxy")
        if prefab1 then Entity.Spawner.AddItemToSpawnQueue(prefab1, character.Inventory) end
        if prefab2 then Entity.Spawner.AddItemToSpawnQueue(prefab2, character.Inventory) end
    end)
end