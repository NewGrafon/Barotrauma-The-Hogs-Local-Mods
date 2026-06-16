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

if CLIENT then
    local lastToggleTime = 0
    local TOGGLE_COOLDOWN = 0.25 

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
    Networking.Receive("SpawnActorMenu", function(msg, client)
        local character = client.Character
        if not character or not character.Inventory then return end
        
        local prefab1 = ItemPrefab.GetItemPrefab("actor-menu-proxy")
        local prefab2 = ItemPrefab.GetItemPrefab("actor-combat-proxy")
        if prefab1 then Entity.Spawner.AddItemToSpawnQueue(prefab1, character.Inventory) end
        if prefab2 then Entity.Spawner.AddItemToSpawnQueue(prefab2, character.Inventory) end
    end)
end
