if CLIENT then
    return
end

-- ============================================================================================
--  NG Network Tweaks (server-only).
--  All intervals/limits are computed FROM the server TICKRATE (1 tick = 1/tickrate seconds), so
--  that on a tickrate change the behavior stays the same "per tick" rather than per absolute second.
--  Recomputed on mod load AND on round start (in case the tickrate changed between rounds).
--  RUS: NG Network Tweaks (только сервер).
--  RUS: Все интервалы/лимиты считаются ОТ ТИКРЕЙТА сервера (1 тик = 1/tickrate секунды), чтобы при
--  RUS: смене тикрейта поведение оставалось одинаковым по «тикам», а не по абсолютным секундам.
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

local function applyTweaks()
    local tickrate = getTickRate()
    pcall(function() print(TAG .. T("Тикрейт сервера: ", "Server tickrate: ") .. tostring(tickrate) .. T(" — пересчитываю поля под него.", " — recomputing fields for it.")) end)

    -- Intervals in seconds = (number of ticks) / tickrate:
    -- RUS: Интервалы в секундах = (сколько тиков) / тикрейт:
    forceSet(netConfigDesc, NetConfig, "MaxHealthUpdateInterval", 40 / tickrate)                -- once per 40 ticks   -- RUS: раз в 40 тиков
    forceSet(netConfigDesc, NetConfig, "LowPrioCharacterPositionUpdateInterval", 20 / tickrate) -- once per 20 ticks   -- RUS: раз в 20 тиков
    forceSet(netConfigDesc, NetConfig, "SparseHullUpdateInterval", 40 / tickrate)               -- once per 40 ticks   -- RUS: раз в 40 тиков
    forceSet(netConfigDesc, NetConfig, "HullUpdateInterval", 2 / tickrate)                      -- once per 2 ticks    -- RUS: раз в 2 тика
    forceSet(netConfigDesc, NetConfig, "ItemConditionUpdateInterval", 1 / tickrate)             -- once per 1 tick     -- RUS: раз в 1 тик

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
