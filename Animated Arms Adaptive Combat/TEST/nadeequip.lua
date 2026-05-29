if not CLIENT then return end
local lastGrenadeTime = 0
local GRENADE_COOLDOWN = 0.3

local function findItemInInventory(character, itemIdentifier)
    if not character or not character.Inventory then return nil end
    for item in character.Inventory.AllItemsMod do
        if item.Prefab.Identifier.Equals(itemIdentifier) then return item end
        if item.OwnInventory then
            for subItem in item.OwnInventory.AllItemsMod do
                if subItem.Prefab.Identifier.Equals(itemIdentifier) then return subItem end
            end
        end
    end
    return nil
end

local function equipGrenade(character, grenade)
    local rightHandSlot = character.Inventory.FindLimbSlot(InvSlotType.RightHand)
    if rightHandSlot >= 0 and character.Inventory.TryPutItem(grenade, rightHandSlot, false, false, character) then return true end
    local leftHandSlot = character.Inventory.FindLimbSlot(InvSlotType.LeftHand)
    if leftHandSlot >= 0 and character.Inventory.TryPutItem(grenade, leftHandSlot, false, false, character) then return true end
    return false
end

Hook.Add("keyUpdate", "grenade_equip", function()
    local currentTime = Timer.GetTime()
    if currentTime - lastGrenadeTime < GRENADE_COOLDOWN or not PlayerInput.Mouse4ButtonClicked() or GUI.KeyboardDispatcher.Subscriber then return end
    local character = Character.Controlled
    if not character or not character.Inventory then return end
    local inventory = character.Inventory
    if inventory.GetItemInLimbSlot(InvSlotType.RightHand) or inventory.GetItemInLimbSlot(InvSlotType.LeftHand) then return end
    local grenade = findItemInInventory(character, "heavyfraggrenade") or findItemInInventory(character, "fraggrenade") or findItemInInventory(character, "fixfoamgrenade") or findItemInInventory(character, "empgrenade") or findItemInInventory(character, "incendiumgrenade") or findItemInInventory(character, "chemgrenade") or findItemInInventory(character, "stungrenade") or findItemInInventory(character, "stinggrenade")
    if grenade and equipGrenade(character, grenade) then lastGrenadeTime = currentTime end
end)

if not SERVER then return end
local lastGrenadeTimeServer = 0

Hook.Add("keyUpdate", "grenade_equip_server", function()
    local currentTime = Timer.GetTime()
    if currentTime - lastGrenadeTimeServer < GRENADE_COOLDOWN or not PlayerInput.Mouse4ButtonClicked() or GUI.KeyboardDispatcher.Subscriber then return end
    local character = Character.Controlled
    if not character or not character.Inventory then return end
    local inventory = character.Inventory
    if inventory.GetItemInLimbSlot(InvSlotType.RightHand) or inventory.GetItemInLimbSlot(InvSlotType.LeftHand) then return end
    local grenade = findItemInInventory(character, "heavyfraggrenade") or findItemInInventory(character, "fraggrenade") or findItemInInventory(character, "fixfoamgrenade") or findItemInInventory(character, "empgrenade") or findItemInInventory(character, "incendiumgrenade") or findItemInInventory(character, "chemgrenade") or findItemInInventory(character, "stungrenade") or findItemInInventory(character, "stinggrenade")
    if grenade and equipGrenade(character, grenade) then lastGrenadeTimeServer = currentTime end
end)