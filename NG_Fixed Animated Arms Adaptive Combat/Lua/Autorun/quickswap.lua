if not CLIENT then return end

local lastSwapTime = 0
local SWAP_COOLDOWN = 0.5

Hook.Add("keyUpdate", "quickswap_bag~hand", function()
    local currentTime = Timer.GetTime()
    if currentTime - lastSwapTime < SWAP_COOLDOWN then return end
    if AAAC and AAAC.IsFeatureEnabled and not AAAC.IsFeatureEnabled("QuickSwap") then return end
    local quickSwapPressed = false
    if AAAC and AAAC.IsConfiguredKeyHit then
        quickSwapPressed = AAAC.IsConfiguredKeyHit("QuickSwap", {"F"})
    else
        quickSwapPressed = PlayerInput.KeyHit(InputType.ToggleInventory)
    end
    if not quickSwapPressed then return end 
    if GUI.KeyboardDispatcher.Subscriber then return end
    local character = Character.Controlled; if character == nil then return end
    local inventory = character.Inventory; if inventory == nil then return end
    local bagSlotIndex = inventory.FindLimbSlot(InvSlotType.Bag); if bagSlotIndex < 0 then return end
    
    -- Проверка предметов в руках перед перемещением в сумку
    for handItem in character.HeldItems do
        -- Добавляем проверку NonInteractable как в autoreload
        if handItem.NonInteractable then return end
        if inventory.TryPutItem(handItem, bagSlotIndex, true, false, character) then
            lastSwapTime = currentTime
            return
        end
    end
    
    local bagItem = inventory.GetItemAt(bagSlotIndex); if bagItem == nil then return end
    
    -- Проверка предмета в сумке перед перемещением в руку
    if bagItem.NonInteractable then return end
    
    for _, handSlotType in ipairs { InvSlotType.LeftHand, InvSlotType.RightHand } do
        local handSlotIndex = inventory.FindLimbSlot(handSlotType)
        if handSlotIndex >= 0 then
            if inventory.TryPutItem(bagItem, handSlotIndex, true, false, character) then
                lastSwapTime = currentTime
                return
            end
        end
    end
end)

if not SERVER then return end

local lastSwapTimeServer = 0
local SWAP_COOLDOWN_SERVER = 0.5

Hook.Add("keyUpdate", "quickswap_bag~hand", function()
    local currentTime = Timer.GetTime()
    if currentTime - lastSwapTimeServer < SWAP_COOLDOWN_SERVER then return end
    if AAAC and AAAC.IsFeatureEnabled and not AAAC.IsFeatureEnabled("QuickSwap") then return end
    local quickSwapPressed = false
    if AAAC and AAAC.IsConfiguredKeyHit then
        quickSwapPressed = AAAC.IsConfiguredKeyHit("QuickSwap", {"F"})
    else
        quickSwapPressed = PlayerInput.KeyHit(InputType.ToggleInventory)
    end
    if not quickSwapPressed then return end 
    if GUI.KeyboardDispatcher.Subscriber then return end
    local character = Character.Controlled; if character == nil then return end
    local inventory = character.Inventory; if inventory == nil then return end
    local bagSlotIndex = inventory.FindLimbSlot(InvSlotType.Bag); if bagSlotIndex < 0 then return end
    
    -- Проверка предметов в руках перед перемещением в сумку
    for handItem in character.HeldItems do
        -- Добавляем проверку NonInteractable как в autoreload
        if handItem.NonInteractable then return end
        if inventory.TryPutItem(handItem, bagSlotIndex, true, false, character) then
            lastSwapTimeServer = currentTime
            return
        end
    end
    
    local bagItem = inventory.GetItemAt(bagSlotIndex); if bagItem == nil then return end
    
    -- Проверка предмета в сумке перед перемещением в руку
    if bagItem.NonInteractable then return end
    
    for _, handSlotType in ipairs { InvSlotType.LeftHand, InvSlotType.RightHand } do
        local handSlotIndex = inventory.FindLimbSlot(handSlotType)
        if handSlotIndex >= 0 then
            if inventory.TryPutItem(bagItem, handSlotIndex, true, false, character) then
                lastSwapTimeServer = currentTime
                return
            end
        end
    end
end)