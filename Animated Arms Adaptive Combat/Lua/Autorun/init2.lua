AnalogCom = {}

if Game.IsMultiplayer and CLIENT then return end

function AnalogCom.SetChannel(item,channel)
    local wifi = item.GetComponentString("WifiComponent")
    -- NG fix: не шлём лишнее сетевое событие, если канал уже нужный.
    -- Хук SetNumber.xmlhook звал SetChannel ~4 раза/сек на каждом лежащем PDA,
    -- и CreateEntityEvent ниже срабатывал вхолостую -> спам сети. Теперь только при реальной смене канала.
    if wifi == nil or wifi.Channel == channel then return end
    wifi.Channel = channel

    if SERVER then
        local prop = wifi.SerializableProperties[Identifier("Channel")]
        Networking.CreateEntityEvent(item, Item.ChangePropertyEventData(prop, wifi))
    end
end

function AnalogCom.GiveAfflictionCharacter(character, identifier, amount, limbType)
    local limb = character.AnimController.GetLimb(limbType or LimbType.Torso,nil,nil,nil)
    character.CharacterHealth.ApplyAffliction(limb, AfflictionPrefab.Prefabs[identifier].Instantiate(amount))
end

function AnalogCom.DelayFuncBy(func,delay)
    return function(...)
        local args = {...}
        Timer.Wait(function()
            func(table.unpack(args))
        end,delay or 1000)
    end
end

function AnalogCom.GetTagValue(s,key)
    if not s then return end
    local _, _, _, val = string.find(s, "("..key.."):([^,]+)")
    return val
end

AnalogCom.Tick = 0
function AnalogCom.TickFunc()
    if not Game.RoundStarted then return end
    AnalogCom.Tick = AnalogCom.Tick + 1
end

function AnalogCom.ResetTickFunc()
    if not Game.RoundStarted then return end
    AnalogCom.Tick = 0
end

Hook.Add("think", "analogcom.Tick", AnalogCom.TickFunc)
Hook.Add("roundEnd", "analogcom.ResetTick", AnalogCom.ResetTickFunc)

function AnalogCom.CallFunc(effect, deltaTime, item, targets, worldPosition, element)
    local wifi = item.GetComponentString("WifiComponent")
    if not wifi then return end
    
    for radio in wifi.GetReceiversInRange() do
        if radio and radio.Item then
            local identifier = radio.Item.Prefab.Identifier
            
            -- Преобразуем Identifier в строку если это userdata
            local identifierStr
            if type(identifier) == "string" then
                identifierStr = identifier
            elseif identifier and identifier.value then
                identifierStr = identifier.value
            elseif identifier then
                identifierStr = tostring(identifier)
            else
                identifierStr = ""
            end
            
            -- Проверяем все PDA и телефоны
            if identifierStr == "analogcom_phonemobile" or
               identifierStr == "analogcom_phonestand" or
               string.find(identifierStr, "PDA") then
                
                local targetItem = radio.Item
                
                -- Устанавливаем condition в 0
                targetItem.Condition = 0
                
                -- Запускаем таймер на 10 секунд для сброса condition
                Timer.Wait(function()
                    if targetItem and not targetItem.Removed then
                        targetItem.Condition = 100
                        print("[DEBUG] Condition reset to 100 after 10 seconds")
                    end
                end, 10000) -- 10000 мс = 10 секунд
            end
        end
    end
end

function AnalogCom.SetNumberFunc(effect, deltaTime, item, targets, worldPosition, element)
    local num = tonumber(AnalogCom.GetTagValue(item.Tags,"number")) or 1111
    AnalogCom.SetChannel(targets[1],num)
end

function AnalogCom.RandomNumberFunc(effect, deltaTime, item, targets, worldPosition, element)
    if not Game.RoundStarted then return end
    local random = math.random(1111,9999)
    local tag = "number:" .. random
    item.AddTag(tag)
end

AnalogCom.RandomNumberFunc = AnalogCom.DelayFuncBy(AnalogCom.RandomNumberFunc)

function AnalogCom.GetItemsCharsOnSameChannel(wifi,getSelf)
    local chars = {}
    if getSelf then
        local owner = wifi.Item.GetRootInventoryOwner()
        if LuaUserData.IsTargetType(owner, "Barotrauma.Character") then
            chars[wifi.Item] = owner
        end
    end
    for radio in wifi.GetReceiversInRange() do
        local owner = radio.Item.GetRootInventoryOwner()
        if LuaUserData.IsTargetType(owner, "Barotrauma.Character") then
            chars[radio.Item] = owner
        end
    end
    return chars
end

function AnalogCom.PickUpFunc(effect, deltaTime, item, targets, worldPosition, element)
    local radio = item.GetComponentString("WifiComponent")
    for otherItem,char in pairs(AnalogCom.GetItemsCharsOnSameChannel(radio,true)) do
        if char.HasEquippedItem(otherItem, InvSlotType.RightHand) then
            AnalogCom.GiveAfflictionCharacter(char, "pickup", 100)
        end
    end
end

function AnalogCom.HangUpFunc(effect, deltaTime, item, targets, worldPosition, element)
    local radio = item.GetComponentString("WifiComponent")
    for otherItem,char in pairs(AnalogCom.GetItemsCharsOnSameChannel(radio,true)) do
        if char.HasEquippedItem(otherItem, InvSlotType.RightHand) then
            AnalogCom.GiveAfflictionCharacter(char, "hangup", 100)
        end
    end
end

function AnalogCom.CancelToneFunc(effect, deltaTime, item, targets, worldPosition, element)
    local radio = item.GetComponentString("WifiComponent")
    for otherItem,char in pairs(AnalogCom.GetItemsCharsOnSameChannel(radio,false)) do
        local aff = char.CharacterHealth.GetAffliction("call")
        if aff then
            aff.Strength = 0
        end
    end
end

Hook.Add("SetNumber.xmlhook", "analogcom.SetNumber", AnalogCom.SetNumberFunc)
Hook.Add("Call.xmlhook", "analogcom.Call", AnalogCom.CallFunc)
Hook.Add("Cancel.xmlhook", "analogcom.CancelTone", AnalogCom.CancelToneFunc)
Hook.Add("analogcom.PickUp.xmlhook", "analogcom.PickUp", AnalogCom.PickUpFunc)
Hook.Add("analogcom.HangUp.xmlhook", "analogcom.HangUp", AnalogCom.HangUpFunc)
Hook.Add("RandomNumber.xmlhook", "analogcom.RandomNumber", AnalogCom.RandomNumberFunc)