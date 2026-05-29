if SERVER then return end

LuaUserData.RegisterType("Barotrauma.Items.Components.ItemContainer+SlotRestrictions")
LuaUserData.RegisterType('System.Collections.Immutable.ImmutableArray`1[[Barotrauma.Items.Components.ItemContainer+SlotRestrictions, Barotrauma]]')
LuaUserData.MakeFieldAccessible(Descriptors['Barotrauma.Items.Components.ItemContainer'], 'slotRestrictions')
LuaUserData.MakeFieldAccessible(Descriptors['Barotrauma.ItemInventory'], 'slots')
LuaUserData.MakeFieldAccessible(Descriptors["Barotrauma.CharacterInventory"], "slots")

local RETRY_SETTINGS = {
    DELAY = 0.3,
    MAX_TRIES = 3,
    TIMEOUT = 5,
    GENERATION_SPAN = 0.5
}

local pendingRetries = {}

local function calculateSlotSpace(slotRule, slot)
    return slotRule.MaxStackSize - #slot.items
end

local function manageInventoryItems(character, mainHand, offHand, handInventory, mainSlots, offSlots)

    local function queueForRetry(magItem)
        if not magItem then return end

        local now = Timer.GetTime()
        local itemId = magItem.ID
        local generationId = math.floor(now / RETRY_SETTINGS.GENERATION_SPAN)

        if not pendingRetries[itemId] then
            pendingRetries[itemId] = {
                item = magItem,
                generations = {}
            }
        end

        local itemEntry = pendingRetries[itemId]
        if not itemEntry.generations[generationId] then
            itemEntry.generations[generationId] = {
                tries = 0,
                nextTry = now + RETRY_SETTINGS.DELAY,
                expires = now + RETRY_SETTINGS.TIMEOUT
            }
        else
            itemEntry.generations[generationId].expires = now + RETRY_SETTINGS.TIMEOUT
        end
    end

    if not handInventory then return end

    local inventorySlots = handInventory.slots

    local function getInventoryExcludingHands()
        local allItems = character.Inventory.AllItemsMod
        for i = #allItems, 1, -1 do
            local item = allItems[i]
            if (mainHand and item.ID == mainHand.ID) or (offHand and item.ID == offHand.ID) then
                table.remove(allItems, i)
            end
        end
        return allItems
    end

    local function attemptMagazineStack(mag)
        if not mag or mag.ConditionPercentage > 0 then
            return false
        end

        local function stackInContainer(container, magazine)
            local magType = magazine.Prefab.Identifier
            for idx, slot in ipairs(container.slots) do
                for _, item in ipairs(slot.items) do
                    if item.HasTag("weapon") or item.HasTag("tool") then goto next end
                    if item.Prefab.Identifier.Equals(magType) and item.ConditionPercentage == 0 and item.ID ~= magazine.ID then
                        if container.CanBePutInSlot(magazine, idx-1) then
                            container.TryPutItem(magazine, idx-1, false, true, nil)
                            return true
                        end
                    end
                    ::next::
                end
            end
            return false
        end

        if stackInContainer(character.Inventory, mag) then
            return true
        end

        for item in getInventoryExcludingHands() do
            if item.OwnInventory and stackInContainer(item.OwnInventory, mag) then
                return true
            end
        end

        return false
    end

    local function stackMagazine(mag)
        if not mag then return false end

        local stacked = attemptMagazineStack(mag)

        if not stacked then
            queueForRetry(mag)
        end

        return stacked
    end

    local function removeMagazine(slotIndex)
        local mag = inventorySlots[slotIndex].items[1]

        if stackMagazine(mag) then return true end

        local characterSlots = character.Inventory.slots
        for i = #characterSlots, 1, -1 do
            if i == 4 or i == 5 or i == 8 then
                if character.Inventory.TryPutItem(mag, i-1, false, true, nil) then
                    return true
                end
            end
        end

        for i = #characterSlots, 1, -1 do
            if i <= 8 or i == 19 then goto skip end
            if character.Inventory.CanBePutInSlot(mag, i-1) then
                character.Inventory.TryPutItem(mag, i-1, false, false, nil)
                return true
            end
            ::skip::
        end

        mag.Drop(character, true, true)
        return false
    end

    local function locateItemsForSlot(slotIndex, quantity)
        local foundItems = {}

        for item in getInventoryExcludingHands() do
            local count = 0
            if item.HasTag("weapon") or item.HasTag("tool") then goto skip end
            if handInventory.CanBePutInSlot(item, slotIndex) and item.ConditionPercentage > 0 then
                if foundItems[item.Prefab.Identifier.value] == nil then 
                    foundItems[item.Prefab.Identifier.value] = {} 
                end

                table.insert(foundItems[item.Prefab.Identifier.value], item)
                count = count + 1
                if count >= quantity then break end
            end
            if item.OwnInventory then
                for subItem in item.OwnInventory.AllItemsMod do
                    if handInventory.CanBePutInSlot(subItem, slotIndex) and subItem.ConditionPercentage > 0 then
                        if foundItems[subItem.Prefab.Identifier.value] == nil then 
                            foundItems[subItem.Prefab.Identifier.value] = {} 
                        end

                        table.insert(foundItems[subItem.Prefab.Identifier.value], subItem)
                        count = count + 1
                        if count >= quantity then break end
                    end
                end
            end
            ::skip::
        end

        local bestMatch = {}
        local maxCount = 0
        for _, items in pairs(foundItems) do
            if #items > maxCount then
                maxCount = #items
                bestMatch = items
            end
        end

        return bestMatch
    end

    local function findAlternativeMagazine(slotIndex, currentMag)
        for item in getInventoryExcludingHands() do
            if item.HasTag("weapon") or item.HasTag("tool") then goto skip end
            if item and item.ID ~= currentMag.ID and handInventory.CanBePutInSlot(item, slotIndex) and item.ConditionPercentage > 0 then
                return item
            end
            if item.OwnInventory then
                for subItem in item.OwnInventory.AllItemsMod do
                    if subItem and subItem.ID ~= currentMag.ID and handInventory.CanBePutInSlot(subItem, slotIndex) and subItem.ConditionPercentage > 0 then
                        return subItem
                    end
                end
            end
            ::skip::
        end
        return nil
    end

    local function locateItemsByIdentifier(itemType, quantity)
        local results = {}
        local count = 0
        for item in getInventoryExcludingHands() do
            if item.HasTag("weapon") or item.HasTag("tool") then goto skip end

            if item.Prefab.Identifier.Equals(itemType) then
                table.insert(results, item)
                count = count + 1
                if count >= quantity then
                    return results
                end
            end
            if item.OwnInventory then
                for subItem in item.OwnInventory.AllItemsMod do
                    if subItem.Prefab.Identifier.Equals(itemType) then
                        table.insert(results, subItem)
                        count = count + 1
                        if count >= quantity then
                            return results
                        end
                    end
                end
            end
            ::skip::
        end
        return results
    end

    local function findStackableItems(itemType)
        local itemsFound = {}
        for item in getInventoryExcludingHands() do
            if item.HasTag("weapon") or item.HasTag("tool") then goto skip end
            if item.Prefab.Identifier.Equals(itemType) and item.ConditionPercentage > 0 then
                table.insert(itemsFound, item)
            end
            if item.OwnInventory then
                for subItem in item.OwnInventory.AllItemsMod do
                    if subItem.Prefab.Identifier.Equals(itemType) and subItem.ConditionPercentage > 0 then
                        table.insert(itemsFound, subItem)
                    end
                end
            end
            ::skip::
        end
        table.sort(itemsFound, function(a, b) return a.ConditionPercentage < b.ConditionPercentage end)
        return itemsFound
    end

    local function placeItem(item, slotIndex, stack, split)
        if item == nil or item.ConditionPercentage == 0 or item == mainHand or item == offHand then return end
        if not handInventory.TryPutItem(item, slotIndex, stack, split, character, true, true) then return false end
        return true
    end

    local container = handInventory.Container
    local currentSlot = math.max(container.ContainedStateIndicatorSlot + 1, 1)
    local initialPass = true

    while true do
        local slotRule = container.slotRestrictions[currentSlot-1]
        
        if #inventorySlots[currentSlot].items == 0 then
            for _, item in ipairs(locateItemsForSlot(currentSlot - 1, calculateSlotSpace(slotRule, inventorySlots[currentSlot]))) do
                placeItem(item, currentSlot - 1, false, false)
            end
        elseif #inventorySlots[currentSlot].items > 0 and calculateSlotSpace(slotRule, inventorySlots[currentSlot]) > 0 then
            for _, item in ipairs(locateItemsByIdentifier(inventorySlots[currentSlot].items[1].Prefab.Identifier, calculateSlotSpace(slotRule, inventorySlots[currentSlot]))) do
                placeItem(item, currentSlot - 1, false, false)
            end
        elseif calculateSlotSpace(slotRule, inventorySlots[currentSlot]) == 0 and #inventorySlots[currentSlot].items == 1 and inventorySlots[currentSlot].items[1].ConditionPercentage ~= 100 then
            local availableItems = findStackableItems(inventorySlots[currentSlot].items[1].Prefab.Identifier)
            local targetItem = availableItems[1]
            local currentMag = inventorySlots[currentSlot].items[1]
            
            if (#availableItems == 1 and inventorySlots[currentSlot].items[1].ConditionPercentage == 0) or (targetItem and targetItem.ConditionPercentage ~=100 and inventorySlots[currentSlot].items[1].ConditionPercentage == 0) then
                removeMagazine(currentSlot)
                placeItem(targetItem, currentSlot - 1, true, true)
            end
            
            if not placeItem(targetItem, currentSlot - 1, true, true) then
                if not (#availableItems == 0 and currentMag.ConditionPercentage > 0 )then
                    removeMagazine(currentSlot)
                end
                
                local equippedMain = character.Inventory.GetItemInLimbSlot(mainSlots[1])
                local equippedOff = character.Inventory.GetItemInLimbSlot(offSlots[1])
                if (equippedMain == mainHand and equippedOff == offHand) ~= true then
                    if mainHand and offHand and mainHand.ID == offHand.ID then
                        for _, slotType in ipairs { InvSlotType.LeftHand, InvSlotType.RightHand } do
                            local slotIndex = character.Inventory.FindLimbSlot(slotType)
                            if slotIndex >= 0 then
                                character.Inventory.TryPutItem(mainHand, slotIndex, true, false, character, true, true)
                            end
                        end
                    else
                        character.Inventory.TryPutItem(mainHand, character, mainSlots, true, true)
                        character.Inventory.TryPutItem(offHand, character, offSlots, true, true)
                    end
                end
                local newMag = findAlternativeMagazine(currentSlot-1, currentMag)
                placeItem(newMag, currentSlot - 1, true, true)
            end
            stackMagazine(targetItem)
            stackMagazine(currentMag)
        end
        
        if handInventory.CanBePut(inventorySlots[math.max(container.ContainedStateIndicatorSlot + 1, 1)].items[1]) and currentSlot < #container.slotRestrictions then
            if initialPass then
                if currentSlot ~= 1 then
                    currentSlot = 1
                else
                    currentSlot = currentSlot + 1
                end
                initialPass = false
            else
                currentSlot = currentSlot + 1
            end
        else
            break
        end
    end

    Hook.Add("think", "retryHandler", function()
        if not pendingRetries then return end
        local currentTime = Timer.GetTime()
    
        for itemId, entry in pairs(pendingRetries) do
            local magItem = entry.item
            local validGenerations = false

            if not magItem or magItem.ID ~= itemId then
                pendingRetries[itemId] = nil
                goto next_item
            end

            for genId, generation in pairs(entry.generations) do
                if currentTime > generation.expires then
                    entry.generations[genId] = nil
                    goto next_generation
                end

                if currentTime >= generation.nextTry then
                    local success = attemptMagazineStack(magItem)

                    if success then
                        pendingRetries[itemId] = nil
                        goto next_item
                    else
                        generation.tries = generation.tries + 1
                        generation.nextTry = currentTime + RETRY_SETTINGS.DELAY

                        if generation.tries >= RETRY_SETTINGS.MAX_TRIES then
                            entry.generations[genId] = nil
                        end
                    end
                end

                validGenerations = true
                ::next_generation::
            end
            
            if not validGenerations then
                pendingRetries[itemId] = nil
            end

            ::next_item::
        end
    end)
end

Hook.Patch("Barotrauma.Character", "ControlLocalPlayer", function(instance, ptable)
    if pendingRetries == nil then Hook.Remove("think", "retryHandler") end
    if(GUI.KeyboardDispatcher.Subscriber ~= nil) then return end
    if AAAC and AAAC.IsFeatureEnabled and not AAAC.IsFeatureEnabled("AutoReload") then return end
    local reloadPressed = false
    if AAAC and AAAC.IsConfiguredKeyHit then
        reloadPressed = AAAC.IsConfiguredKeyHit("AutoReload", {"CapsLock"})
    else
        reloadPressed = PlayerInput.KeyHit(Keys.CapsLock)
    end
    if not reloadPressed then return end

    local Character = instance
    if not Character then return end

    if Character.LockHands then return end

    local rightItem = Character.Inventory.GetItemInLimbSlot(InvSlotType.RightHand)
    local leftItem = Character.Inventory.GetItemInLimbSlot(InvSlotType.LeftHand)
    local rightSlots = {InvSlotType.RightHand}
    local leftSlots = {InvSlotType.LeftHand}

    if not rightItem and not leftItem then return end

    -- Проверяем weapon и tool теги для правой руки
    if rightItem and (rightItem.HasTag("weapon") or rightItem.HasTag("tool")) then
        if rightItem.NonInteractable then return end

        if not rightItem.IsInteractable(Character) then return end

        manageInventoryItems(Character, rightItem, leftItem, rightItem.OwnInventory, rightSlots, leftSlots)
    end

    -- Проверяем weapon и tool теги для левой руки
    if leftItem and not leftItem.Equals(rightItem) and (leftItem.HasTag("weapon") or leftItem.HasTag("tool")) then
        if leftItem.NonInteractable then return end

        if not leftItem.IsInteractable(Character) then return end

        manageInventoryItems(Character, leftItem, rightItem, leftItem.OwnInventory, leftSlots, rightSlots)
    end
end, Hook.HookMethodType.After)