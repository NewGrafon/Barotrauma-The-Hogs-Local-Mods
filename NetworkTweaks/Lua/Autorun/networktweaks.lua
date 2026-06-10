if CLIENT then
    return
end

-- Универсальная функция: пробует все способы доступа, чтобы не крашнуть скрипт
local function forceSet(descriptor, obj, name, value)
    if not obj then
        return
    end

    -- 0. Выводим в лог текущее значение
    pcall(
        function()
            print("[NG] [NetworkTweaks] Текущее значение " .. name .. ": " .. tostring(obj[name]))
        end
    )

    -- 1. Пробуем сделать доступным как Field (Поле)
    pcall(
        function()
            LuaUserData.MakeFieldAccessible(descriptor, name)
        end
    )

    -- 2. Пробуем сделать доступным как Property (Свойство)
    pcall(
        function()
            LuaUserData.MakePropertyAccessible(descriptor, name)
        end
    )

    -- 3. Пытаемся записать значение
    local success, err =
        pcall(
        function()
            obj[name] = value
        end
    )

    -- 4. Выводим в лог результат
    if not success then
        pcall(
            function()
                print("[NG] [NetworkTweaks] Не удалось изменить " .. name .. ": " .. tostring(err))
            end
        )
    else
        pcall(
            function()
                print("[NG] [NetworkTweaks] Удалось изменить " .. name .. ": " .. tostring(obj[name]))
            end
        )
    end
end

-- Дескрипторы классов
local netConfigDesc = Descriptors["Barotrauma.Networking.NetConfig"]
local settingsDesc = Descriptors["Barotrauma.Networking.ServerSettings"]

-- Применяем настройки для NetConfig (через нашу защиту)
forceSet(netConfigDesc, NetConfig, "MaxHealthUpdateInterval", 1.515151)
forceSet(netConfigDesc, NetConfig, "LowPrioCharacterPositionUpdateInterval", 0.666666)
forceSet(netConfigDesc, NetConfig, "MaxEventPacketsPerUpdate", 24)
forceSet(netConfigDesc, NetConfig, "SparseHullUpdateInterval", 3.030303)
forceSet(netConfigDesc, NetConfig, "HullUpdateInterval", 0.333333)
forceSet(netConfigDesc, NetConfig, "HighPrioCharacterPositionUpdateDistance", 2000)
forceSet(netConfigDesc, NetConfig, "ItemConditionUpdateInterval", 0.111111)

-- Применяем настройки для ServerSettings
if Game.ServerSettings then
    forceSet(settingsDesc, Game.ServerSettings, "MinimumMidRoundSyncTimeout", 120)
end

-- Отчет в консоль при старте раунда
Hook.Add(
    "roundStart",
    "NetworkTweaks.Verify",
    function()
        pcall(
            function()
                print("[NG] [NetworkTweaks] Настройки применены. Проверьте консоль выше на наличие ошибок.")
            end
        )
    end
)

Hook.Add(
    "roundStartInitialization",
    "NetworkTweaks.Verify",
    function()
        pcall(
            function()
                print("[NG] [NetworkTweaks] Настройки применены. Проверьте консоль выше на наличие ошибок.")
            end
        )
    end
)
