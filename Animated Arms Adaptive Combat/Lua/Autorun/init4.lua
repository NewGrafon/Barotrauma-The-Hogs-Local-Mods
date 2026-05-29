if SERVER then
    PerformanceFix = {}
    PerformanceFix.Path = ...

    -- Определение языка сервера (по умолчанию английский)
    local function GetServerLanguage()
        -- Сервер может использовать системную локаль или настройки
        -- По умолчанию оставляем английский для совместимости
        return "English"
    end

    local SERVER_LANG = GetServerLanguage()

    -- Функция перевода для серверных сообщений (если понадобятся)
    local function T(rus, eng, chn)
        if SERVER_LANG == "Russian" then
            return rus
        elseif SERVER_LANG == "Chinese" then
            return chn
        else
            return eng
        end
    end

    if not File.Exists(PerformanceFix.Path .. "/config.json") then
        File.Write(PerformanceFix.Path .. "/config.json", json.serialize(dofile(PerformanceFix.Path .. "/Lua/defaultconfig.lua")))
        
        -- Опционально: сообщение о создании конфига
        print(T(
            "Создан новый файл конфигурации: " .. PerformanceFix.Path .. "/config.json",
            "Created new configuration file: " .. PerformanceFix.Path .. "/config.json",
            "已创建新的配置文件: " .. PerformanceFix.Path .. "/config.json"
        ))
    end

    PerformanceFix.Config = json.parse(File.Read(PerformanceFix.Path .. "/config.json"))
    AAAC = dofile(PerformanceFix.Path .. "/Lua/aaac_settings.lua")

    dofile(PerformanceFix.Path .. "/Lua/performancefix.lua")
    
    print(T(
        "Улучшенная оптимизация (Performance Fix) загружено",
        "Performance Fix loaded",
        "性能优化已加载"
    ))
end