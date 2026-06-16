AAAC = AAAC or {}

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

local DISABLE_INTERACT_AFFLICTION = "aaac_disable_interact"
local ID_CARD_SLOT = InvSlotType.Card
local ID_CARD_IDENTIFIERS = {
    ["idcard"] = true,
    ["idcardsecure"] = true,
    ["idcardcommand"] = true,
}

local TOGGLE_COOLDOWN = 0.35
local SERVER_COOLDOWN = 0.35
local lastToggleTime = 0
local clientLastToggleTime = {}

local function featureEnabled()
    return true
end

local function afflictionStrength(character, identifier)
    if not character or not character.CharacterHealth then return 0 end
    local affliction = nil
    pcall(function() affliction = character.CharacterHealth.GetAffliction(identifier, false) end)
    if affliction == nil then pcall(function() affliction = character.CharacterHealth.GetAffliction(identifier, true) end) end
    if affliction == nil then pcall(function() affliction = character.CharacterHealth.GetAffliction(identifier) end) end
    if affliction == nil then return 0 end
    local strength = 0
    pcall(function() strength = tonumber(affliction.Strength) or 0 end)
    return strength
end

local function interactDisabled(character)
    return false
end

local function getIdentifier(item)
    if not item or not item.Prefab or not item.Prefab.Identifier then return nil end
    return tostring(item.Prefab.Identifier.Value or item.Prefab.Identifier)
end

local function isMenuItem(item)
    local identifier = getIdentifier(item)
    return identifier ~= nil and menuItems[identifier] == true
end

local function isIdCard(item)
    local identifier = getIdentifier(item)
    return identifier ~= nil and ID_CARD_IDENTIFIERS[identifier] == true
end

local function getCharacterInventory(character)
    return character and character.Inventory or nil
end

local function findIdCard(character)
    local inventory = getCharacterInventory(character)
    if not inventory then return nil end

    local cardItem = inventory.GetItemAt(ID_CARD_SLOT)
    if isIdCard(cardItem) and cardItem.OwnInventory then
        return cardItem
    end

    for i = 0, inventory.Capacity - 1 do
        local item = inventory.GetItemAt(i)
        if isIdCard(item) and item.OwnInventory then
            return item
        end
    end

    return nil
end

local function eachMenuItem(character, callback)
    if not character or not character.Inventory or type(callback) ~= "function" then return end
    local inventory = character.Inventory

    for i = 0, inventory.Capacity - 1 do
        local item = inventory.GetItemAt(i)
        if isMenuItem(item) then
            callback(item)
        elseif isIdCard(item) and item.OwnInventory then
            local idInventory = item.OwnInventory
            for j = 0, idInventory.Capacity - 1 do
                local nested = idInventory.GetItemAt(j)
                if isMenuItem(nested) then
                    callback(nested)
                end
            end
        end
    end
end

local function removeMenuItems(character)
    local removed = false
    eachMenuItem(character, function(item)
        removed = true
        pcall(function() item.Drop(character) end)
        pcall(function() if item.ParentInventory ~= nil then item.ParentInventory.TryPutItem(nil, item, false, false, nil, true, true) end end)
        pcall(function() if Entity and Entity.Spawner then Entity.Spawner.AddItemToRemoveQueue(item) end end)
    end)
    return removed
end

AAAC.RemoveAllInteractMenuItems = removeMenuItems

local function spawnMenuItems(character)
    if not character or character.Removed or character.IsDead or interactDisabled(character) then return false end
    local inventory = getCharacterInventory(character)
    if not inventory then return false end

    local targetInventory = inventory
    local idCard = findIdCard(character)
    if idCard and idCard.OwnInventory then
        targetInventory = idCard.OwnInventory
    end

    local prefab1 = ItemPrefab.GetItemPrefab("actor-menu-proxy")
    local prefab2 = ItemPrefab.GetItemPrefab("actor-combat-proxy")
    if prefab1 then Entity.Spawner.AddItemToSpawnQueue(prefab1, targetInventory) end
    if prefab2 then Entity.Spawner.AddItemToSpawnQueue(prefab2, targetInventory) end
    return prefab1 ~= nil or prefab2 ~= nil
end

if CLIENT then
    Hook.Add("keyUpdate", "AAAC.InteractToggle", function()
        if not Game.RoundStarted then return end
        if GUI.KeyboardDispatcher.Subscriber ~= nil then return end

        local character = Character.Controlled
        if not character or not character.Inventory then return end
        if interactDisabled(character) then
            removeMenuItems(character)
            return
        end

        local currentTime = Timer.GetTime() or 0
        if currentTime - lastToggleTime < TOGGLE_COOLDOWN then return end

        local interactPressed = false
        if AAAC and AAAC.IsConfiguredKeyHit then
            interactPressed = AAAC.IsConfiguredKeyHit("Interact", {"N"})
        else
            interactPressed = PlayerInput.KeyHit(Keys.N)
        end
        if not interactPressed then return end

        if removeMenuItems(character) then
            lastToggleTime = currentTime
            return
        end

        if Game.IsMultiplayer then
            local msg = Networking.Start("SpawnActorMenu")
            if msg then Networking.Send(msg) end
        else
            spawnMenuItems(character)
        end

        lastToggleTime = currentTime
    end)
end

if SERVER then
    Networking.Receive("SpawnActorMenu", function(msg, client)
        if not client or not client.Character then return end
        local character = client.Character
        if not character or not character.Inventory then return end

        local currentTime = Timer.GetTime() or 0
        local key = tostring(client.SessionId or client.Name or client)
        if clientLastToggleTime[key] and currentTime - clientLastToggleTime[key] < SERVER_COOLDOWN then return end
        clientLastToggleTime[key] = currentTime

        if interactDisabled(character) then
            removeMenuItems(character)
            return
        end

        if removeMenuItems(character) then
            return
        end

        spawnMenuItems(character)
    end)

    Hook.Add("think", "AAAC.InteractDisableEnforcer", function()
        if not Game.RoundStarted then return end
        local clients = Client.ClientList
        if not clients then return end
        for _, client in pairs(clients) do
            if client and client.Character and interactDisabled(client.Character) then
                pcall(removeMenuItems, client.Character)
            end
        end
    end)
end

if Game.IsSingleplayer then
    Hook.Add("think", "AAAC.InteractDisableEnforcer.Singleplayer", function()
        if not Game.RoundStarted then return end
        local character = Character.Controlled
        if character and interactDisabled(character) then
            pcall(removeMenuItems, character)
        end
    end)
end
