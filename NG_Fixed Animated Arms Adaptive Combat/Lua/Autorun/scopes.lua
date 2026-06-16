if SERVER then return end -- Только для клиента

-- Настройки зума
local ZoomManager = {
    States = {}, -- Состояния для каждого предмета
    LastWheelTime = 0, -- Время последней прокрутки
    WheelCooldown = 0, -- Задержка между прокрутками
    ZoomSpeed = 0.07 -- Скорость изменения зума
}

-- Плавное изменение значения
function ZoomManager.Lerp(a, b, t)
    return a + (b - a) * t
end

-- Ограничение значения в диапазоне
function ZoomManager.Clamp(value, min, max)
    return math.min(math.max(value, min), max)
end

-- Настройки уровней зума
ZoomManager.Levels = {
    -- Фиксированные zoom уровни
    Fixed = {
        ["zoom2x"] = {offset = 900, multiplier = 0.2},
        ["zoom2_5x"] = {offset = 900, multiplier = 0.25},
        ["zoom3x"] = {offset = 900, multiplier = 0.3},
        ["zoom6x"] = {offset = 900, multiplier = 0.6},
        ["zoom8x"] = {offset = 900, multiplier = 0.8}
    },
    
    -- Переменные zoom уровни
    Variable = {
        ["zoom1-3x"] = {offset = 900, min = 0, max = 0.3},
        ["zoom1-6x"] = {offset = 900, min = 0, max = 0.7},
        ["zoom1-8x"] = {offset = 900, min = 0, max = 0.95}
    }
}

-- Проверяет есть ли у предмета нужный тег
function ZoomManager.HasZoomTag(item, tag)
    return item and (item.HasTag(tag) or 
           (item.ownInventory and item.ownInventory.FindItemByTag(tag, true)))
end

-- Получает тип зума для предмета
function ZoomManager.GetZoomType(item)
    if not item then return nil end
    
    -- Сначала проверяем переменный зум
    for tag, settings in pairs(ZoomManager.Levels.Variable) do
        if ZoomManager.HasZoomTag(item, tag) then
            return "variable", tag, settings
        end
    end
    
    -- Затем проверяем фиксированный зум
    for tag, settings in pairs(ZoomManager.Levels.Fixed) do
        if ZoomManager.HasZoomTag(item, tag) then
            return "fixed", tag, settings
        end
    end
    
    return nil
end

-- Обрабатывает изменение зума
function ZoomManager.HandleZoom(item, delta)
    if not item then return Screen.Selected.Cam.OffsetAmount end
    
    local zoomType, tag, settings = ZoomManager.GetZoomType(item)
    
    if zoomType == "variable" then
        -- Обработка переменного зума
        local itemID = item.Prefab.Identifier.value
        
        if not ZoomManager.States[itemID] then
            ZoomManager.States[itemID] = {
                currentMultiplier = settings.min,
                baseOffset = settings.offset
            }
        end
        
        local state = ZoomManager.States[itemID]
        state.currentMultiplier = ZoomManager.Clamp(
            state.currentMultiplier + delta,
            settings.min,
            settings.max
        )
        
        return ZoomManager.Lerp(
            Screen.Selected.Cam.OffsetAmount,
            state.baseOffset,
            state.currentMultiplier
        )
        
    elseif zoomType == "fixed" then
        -- Обработка фиксированного зума
        return ZoomManager.Lerp(
            Screen.Selected.Cam.OffsetAmount,
            settings.offset,
            settings.multiplier
        )
    end
    
    -- Если зум не найден
    return Screen.Selected.Cam.OffsetAmount
end

-- Применяет zoom к камере
function ZoomManager.ApplyZoom(offset)
    if Screen.Selected and Screen.Selected.Cam then
        Screen.Selected.Cam.OffsetAmount = offset
    end
end

-- Основная функция обработки
function ZoomManager.ProcessZoom(character)
    if not character or not character.Inventory then return end
    
    -- Проверяем задержку
    local currentTime = os.clock()
    if currentTime - ZoomManager.LastWheelTime < ZoomManager.WheelCooldown then return end
    
    -- Получаем предмет в руках
    local item = character.Inventory.GetItemInLimbSlot(InvSlotType.RightHand) or
                 character.Inventory.GetItemInLimbSlot(InvSlotType.LeftHand)
    
    -- Проверяем условия для зума
    if not item or not character.AnimController.IsAiming then return end
    
    -- Обрабатываем ввод
    local newOffset = ZoomManager.HandleZoom(item, 0)
    
    if PlayerInput.MouseWheelUpClicked() then
        ZoomManager.LastWheelTime = currentTime
        newOffset = ZoomManager.HandleZoom(item, -ZoomManager.ZoomSpeed)
    elseif PlayerInput.MouseWheelDownClicked() then
        ZoomManager.LastWheelTime = currentTime
        newOffset = ZoomManager.HandleZoom(item, ZoomManager.ZoomSpeed)
    end
    
    -- Применяем изменения
    ZoomManager.ApplyZoom(newOffset)
end

-- Регистрируем хук
Hook.Patch("Barotrauma.Character", "ControlLocalPlayer", function(instance, ptable)
    ZoomManager.ProcessZoom(instance)
end, Hook.HookMethodType.After)