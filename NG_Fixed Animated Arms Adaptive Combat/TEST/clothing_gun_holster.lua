print('clothing_gun_holster.lua')
local enabled = Game.GetEnabledContentPackages()
local isEnabled = false
for key, value in pairs(enabled) do
    if value.Name == "Security clothing plus" then
        isEnabled = true
        break
    end
end
if not isEnabled then return end

local config = dofile(SecurityClothingPlus.Path .. "/Lua/config.lua")

local list_ItemComponentType = LuaUserData.RegisterType("System.Collections.Generic.List`1[Barotrauma.Items.Components.ItemComponent]")
local xNameType = LuaUserData.RegisterType("System.Xml.Linq.XName")
local xElementType = LuaUserData.RegisterType("System.Xml.Linq.XElement")
local xAttributeType = LuaUserData.RegisterType("System.Xml.Linq.XAttribute")
local wearableSpriteType = LuaUserData.RegisterType("Barotrauma.WearableSprite")
local itemComponentType = LuaUserData.RegisterType("Barotrauma.Items.Components.ItemComponent")
local wearableType = LuaUserData.RegisterType("Barotrauma.Items.Components.Wearable")
local list_WearableSpriteType = LuaUserData.RegisterType("System.Collections.Generic.List`1[Barotrauma.WearableSprite]")
local ContentXElementType = LuaUserData.RegisterType("Barotrauma.ContentXElement")

local ItemComponent = LuaUserData.CreateStatic("Barotrauma.Items.Components.ItemComponent")
local Wearable = LuaUserData.CreateStatic("Barotrauma.Items.Components.Wearable")
local WearableSprite = LuaUserData.CreateStatic("Barotrauma.WearableSprite")
local XName = LuaUserData.CreateStatic("System.Xml.Linq.XName")
local XElement = LuaUserData.CreateStatic("System.Xml.Linq.XElement")
local XAttribute = LuaUserData.CreateStatic("System.Xml.Linq.XAttribute")
local List_ItemComponent = LuaUserData.CreateStatic("System.Collections.Generic.List`1[Barotrauma.Items.Components.ItemComponent]")
local List_WearableSprite = LuaUserData.CreateStatic("System.Collections.Generic.List`1[Barotrauma.WearableSprite]")
local ContentXElement = LuaUserData.CreateStatic("Barotrauma.ContentXElement")

local clothing_slot = 3
local gun_slot = 0
local hand_slots = { 6, 5 }

local sprite_names = {}
for gun,str in pairs(config.sprites) do
    local xElem = XElement.Parse(str)
    config.sprites[gun] = xElem
    sprite_names[gun] = xElem.Attribute(XName.Get("name")).Value
end

ctrl_itms = {
    items = {}
}

function _getGunType(self)
    local gun = self.item.OwnInventory.GetItemAt(gun_slot)
    local gunType = nil
    if gun ~= nil then gunType = gun.Prefab.Identifier
    else return nil end
    for i,gn in pairs(config.guns) do
        if gunType == gn then return gunType end
    end
    return nil
end

function ctrl_itms.inferState(char)
    local torso = getCharTorso(char)
    if torso ~= nil then
        local wSprites = torso.WearingItems
        for i = 0,#(wSprites)-1 do
            for gun,name in pairs(sprite_names) do
                if wSprites[i].Sprite.Name == name then return gun end
            end
        end
    end
    return 'default'
end

function ctrl_itms:get(char)
    if char.Inventory == nil then return nil end

    for i,itm in pairs(self.items) do
        if itm.char == char then return itm end
    end

    local cloth = char.Inventory.GetItemAt(clothing_slot)
    local itemEntry = {
        item = cloth,
        char = char,
        type = cloth.Prefab.Identifier,
        getGunType = _getGunType,
        state = ctrl_itms.inferState(char)
    }
    table.insert(self.items, itemEntry)
    return itemEntry
end

function ctrl_itms:contains(char)
    if char.Inventory == nil then return false end

    for i,itm in pairs(self.items) do
        if itm.char == char then return true end
    end

    return false
end

function ctrl_itms:remove(itemEntry)
    if itemEntry == nil then return end

    for i,itm in pairs(self.items) do
        if itm == itemEntry then
            table.remove(self.items, i)
            return
        end
    end
end

-- Добавляем переменные как в nadeequip.lua
local lastHolsterTime = 0
local HOLSTER_COOLDOWN = 0.3

-- Функция для переключения оружия как в nadeequip.lua
local function toggleWeapon(character)
    local currentTime = Timer.GetTime()
    if currentTime - lastHolsterTime < HOLSTER_COOLDOWN or GUI.KeyboardDispatcher.Subscriber then return end
    
    local itemEntry = ctrl_itms:get(character)
    if itemEntry == nil then return end

    local slotItem = itemEntry.item.OwnInventory.GetItemAt(gun_slot)
    local handItems = { 
        character.Inventory.GetItemInLimbSlot(InvSlotType.RightHand), 
        character.Inventory.GetItemInLimbSlot(InvSlotType.LeftHand) 
    }

    if (handItems[1] ~= nil or handItems[2] ~= nil) then
        -- Руки не пустые
        local handitem = handItems[1] or handItems[2]

        if slotItem ~= nil then
            -- В кобуре есть оружие и в руках тоже
            character.Inventory.TryPutItem(slotItem, character.Inventory.FindLimbSlot(InvSlotType.RightHand), false, false, character)
            itemEntry.item.OwnInventory.TryPutItem(handitem, gun_slot, false, false, character)
        else
            -- В кобуре пусто, но в руках есть оружие
            itemEntry.item.OwnInventory.TryPutItem(handitem, gun_slot, false, false, character)
        end
    else
        -- Руки пустые
        if slotItem ~= nil then
            -- В кобуре есть оружие
            character.Inventory.TryPutItem(slotItem, character.Inventory.FindLimbSlot(InvSlotType.RightHand), false, false, character)
        end
    end
    
    lastHolsterTime = currentTime
end

-- Хук для Mouse5 как в nadeequip.lua
Hook.Add("keyUpdate", "holster_weapon_toggle", function()
    if not PlayerInput.Mouse5ButtonClicked() or GUI.KeyboardDispatcher.Subscriber then return end
    local character = Character.Controlled
    if not character or not character.Inventory then return end
    
    local clothing = character.Inventory.GetItemAt(clothing_slot)
    if clothing == nil then return end
    
    local clothingType = clothing.Prefab.Identifier
    local isControlledClothing = false
    for i,cloth in pairs(config.clothes) do
        if clothingType == cloth then
            isControlledClothing = true
            break
        end
    end
    
    if isControlledClothing then
        toggleWeapon(character)
    end
end)

-- Остальной код для визуалов остается без изменений
local holsterUpdateDelegate = {func = nil}
if SERVER then
else
    Hook.Add("think", "holsterLoop", function()
        if holsterUpdateDelegate.func ~= nil then holsterUpdateDelegate.func() end
    end)

    local holsterInit = function ()
        Timer.Wait(function()
            holsterUpdateDelegate.func = holsterUpdate
        end, 1000)
    end
    if Game.GameSession ~= nil and Game.GameSession.IsRunning then holsterInit() end
    Hook.Add("roundStart", "holsterStart", holsterInit)
    Hook.Add("roundEnd", "holsterStop", function()
        holsterUpdateDelegate.func = nil
    end)
end

function holsterUpdate()
    local chars, remChars = updateChars()

    for i,ch in pairs(remChars) do
        local itemEntry = ctrl_itms:get(ch)
        itemEntry.state = 'default'
        setHolsterVisual(ch, itemEntry, nil)
        ctrl_itms:remove(itemEntry)
    end

    for i,ch in pairs(chars) do
        local itemEntry = ctrl_itms:get(ch)
        if itemEntry ~= nil then
            local gunType = itemEntry:getGunType()
            if gunType ~= nil and itemEntry.state ~= gunType then
                itemEntry.state = gunType
                setHolsterVisual(ch, itemEntry, gunType)
            elseif gunType == nil and itemEntry.state ~= 'default' then
                itemEntry.state = 'default'
                setHolsterVisual(ch, itemEntry, gunType)
            end
        end
    end
end

function updateChars()
    local remChars = {}
    local chars = {}

    for i,ch in pairs(Character.CharacterList) do
        if ch.Inventory ~= nil then
            local clothing = ch.Inventory.GetItemAt(clothing_slot)
            if ctrl_itms:contains(ch) and clothing ~= ctrl_itms:get(ch).item then
                table.insert(remChars, ch)
            elseif clothing ~= nil then
                local clothingType = clothing.Prefab.Identifier
                for i,cloth in pairs(config.clothes) do
                    if clothingType == cloth then
                        table.insert(chars, ch)
                        break
                    end
                end
            end
        end
    end

    return chars, remChars
end

function getCharTorso(char)
    for i,limb in pairs(char.AnimController.Limbs) do
        if limb.type == 12 then return limb end
    end
    return nil
end

function setHolsterVisual(char, itemEntry, gunType)
    local clothing = itemEntry.item
    local torso = getCharTorso(char)

    if torso ~= nil then
        local wSprites = torso.WearingItems

        local i = 0
        local maxIdx = #(wSprites)
        while i < maxIdx do
            for _,name in pairs(sprite_names) do
                if wSprites[i].Sprite.Name == name then
                    wSprites.RemoveAt(i)
                    maxIdx = #(wSprites)
                    i = i - 1
                    break
                end
            end
            i = i + 1
        end

        if gunType ~= nil then
            local wearable = nil
            for i = 0,#(clothing.Components)-1 do
                if clothing.Components[i].GetType() == Wearable then
                    wearable = clothing.Components[i]
                    break
                end
            end

            local sprite = nil
            for key,value in pairs(config.sprites) do
                if key == gunType then
                    sprite = value
                    break
                end
            end
            local cxelem = ContentXElement.__new(null, sprite)
            local gunSprite = WearableSprite.__new(cxelem, wearable, 0)
            gunSprite.Init(char)
            wSprites.Add(gunSprite)
        end
    end
end