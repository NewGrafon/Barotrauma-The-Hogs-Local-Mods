if CLIENT then
    return
end

-- ============================================================================================
--  NG Network Tweaks (server-only).
--  The four main hull/position/health intervals scale LINEARLY with the server tickrate: at tickrate 20
--  they equal the VANILLA values (in seconds), and at tickrate 60 they are HALVED (twice as frequent),
--  interpolated linearly in between and clamped outside [20, 60]. This is gentler than constant-ticks
--  scaling, which would shorten absolute intervals too aggressively at high tickrates.
--  ItemConditionUpdateInterval stays at a constant 2 ticks; everything else is left as-is.
--  Recomputed on mod load AND on round start (in case the tickrate changed between rounds).
--  RUS: NG Network Tweaks (только сервер).
--  RUS: Четыре основных интервала (трюмы/позиции/здоровье) масштабируются ЛИНЕЙНО по тикрейту: при
--  RUS: тикрейте 20 равны ВАНИЛЬНЫМ значениям (в секундах), при 60 — вдвое короче (вдвое чаще), линейно
--  RUS: между ними и зажаты вне диапазона [20, 60]. Это мягче, чем константа по тикам, которая на высоком
--  RUS: тикрейте укорачивала бы абсолютные интервалы слишком резко.
--  RUS: ItemConditionUpdateInterval остаётся константой 2 тика; всё остальное — без изменений.
--  RUS: Пересчёт делается при загрузке мода И на старте раунда (вдруг тикрейт сменили между раундами).
-- ============================================================================================

-- Console tag + best-effort RU/EN by game language (English fallback if undetectable from Lua).
-- RUS: Тег консоли + best-effort RU/EN по языку игры (фоллбэк на английский, если из Lua не определить).
local TAG = "[NG] [Network Tweaks] "
local IS_RU = false
pcall(function()
    local gs = LuaUserData.CreateStatic("Barotrauma.GameSettings")
    local lang = tostring(gs.CurrentConfig.Language)
    if lang and string.find(string.lower(lang), "russ") then IS_RU = true end
end)
local function T(ru, en) if IS_RU then return ru else return en end end

-- Universal setter: tries Field and Property, never crashes the script on failure.
-- RUS: Универсальная установка: пробует Field и Property, не крашит скрипт при неудаче.
local function forceSet(descriptor, obj, name, value)
    if not obj then
        return
    end

    pcall(function() print(TAG .. T("Текущее значение ", "Current value of ") .. name .. ": " .. tostring(obj[name])) end)
    pcall(function() LuaUserData.MakeFieldAccessible(descriptor, name) end)
    pcall(function() LuaUserData.MakePropertyAccessible(descriptor, name) end)

    local success, err = pcall(function() obj[name] = value end)
    if not success then
        pcall(function() print(TAG .. T("Не удалось изменить ", "Failed to set ") .. name .. ": " .. tostring(err)) end)
    else
        pcall(function() print(TAG .. T("Изменено ", "Set ") .. name .. " -> " .. tostring(obj[name])) end)
    end
end

local netConfigDesc = Descriptors["Barotrauma.Networking.NetConfig"]
local settingsDesc = Descriptors["Barotrauma.Networking.ServerSettings"]

-- Read the current server tickrate (with a fallback default if it can't be read).
-- RUS: Читаем текущий тикрейт сервера (с запасным дефолтом, если прочитать не удалось).
local function getTickRate()
    local tr = nil
    pcall(function() LuaUserData.MakePropertyAccessible(settingsDesc, "TickRate") end)
    pcall(function()
        if Game.ServerSettings then
            tr = Game.ServerSettings.TickRate
        end
    end)
    tr = tonumber(tr)
    if not tr or tr < 1 then
        tr = 40 -- fallback default (current server tickrate)   -- RUS: запасной дефолт (текущий тикрейт сервера)
    end
    return tr
end

local function round(x) return math.floor(x + 0.5) end
local function clamp(v, lo, hi) return math.max(lo, math.min(v, hi)) end

-- Linear interval scale by tickrate: 1.0 at tickrate 20 (vanilla seconds), 0.5 at tickrate 60 (half the
-- duration), linearly interpolated in between, clamped to [0.5, 1.0] outside the [20, 60] range.
-- RUS: Линейный масштаб интервалов по тикрейту: 1.0 при тикрейте 20 (ванильные секунды), 0.5 при 60 (вдвое
-- RUS: короче), линейно между ними, зажат в [0.5, 1.0] вне диапазона [20, 60].
local function scaleFactor(tickrate)
    return clamp(1.0 - (tickrate - 20) / 80.0, 0.5, 1.0)
end

local function applyTweaks()
    local tickrate = getTickRate()
    local f = scaleFactor(tickrate)
    pcall(function() print(TAG .. T("Тикрейт сервера: ", "Server tickrate: ") .. tostring(tickrate)
        .. T(" — множитель интервалов ", " — interval factor ") .. string.format("%.3f", f) .. ".") end)

    -- Tickrate-scaled intervals (seconds): vanilla at tickrate 20, half at 60, linear in between.
    -- RUS: Интервалы по тикрейту (в секундах): ваниль при 20, вдвое короче при 60, линейно между.
    forceSet(netConfigDesc, NetConfig, "MaxHealthUpdateInterval", 2.0 * f)                 -- vanilla 2s    -- RUS: ваниль 2с
    forceSet(netConfigDesc, NetConfig, "LowPrioCharacterPositionUpdateInterval", 1.0 * f)  -- vanilla 1s    -- RUS: ваниль 1с
    forceSet(netConfigDesc, NetConfig, "SparseHullUpdateInterval", 5.0 * f)                -- vanilla 5s    -- RUS: ваниль 5с
    forceSet(netConfigDesc, NetConfig, "HullUpdateInterval", 0.5 * f)                      -- vanilla 0.5s  -- RUS: ваниль 0.5с

    -- Always a constant 2 ticks, regardless of tickrate (unchanged).
    -- RUS: Всегда константа 2 тика, независимо от тикрейта (без изменений).
    forceSet(netConfigDesc, NetConfig, "ItemConditionUpdateInterval", 2 / tickrate)              -- once per 2 ticks    -- RUS: раз в 2 тика

    -- Event packet limit: up to 800/sec -> 800/tickrate, rounded, clamped to [5, 60]:
    -- RUS: Лимит пакетов событий: до 800/сек -> 800/тикрейт, округлить до целого, зажать в [5, 60]:
    local maxPkts = clamp(round(800 / tickrate), 5, 60)
    forceSet(netConfigDesc, NetConfig, "MaxEventPacketsPerUpdate", maxPkts)

    -- Independent of tickrate (left as-is):
    -- RUS: Не зависит от тикрейта (оставляем как было):
    forceSet(netConfigDesc, NetConfig, "HighPrioCharacterPositionUpdateDistance", 2000)

    if Game.ServerSettings then
        forceSet(settingsDesc, Game.ServerSettings, "MinimumMidRoundSyncTimeout", 120)
    end
end

applyTweaks() -- on mod load   -- RUS: при загрузке мода

-- Recompute on round start (tickrate may have changed between rounds).
-- RUS: Пересчёт на старте раунда (тикрейт мог смениться между раундами).
Hook.Add("roundStart", "NetworkTweaks.Apply", function()
    pcall(applyTweaks)
end)
