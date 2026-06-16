-- ek_switch_offdelay.lua
-- EK heavy power switch — optional TURN-OFF DELAY driven by a wired connection.
--
-- The switch has an extra input "set_offdelay" (see ekutility_placeablemachines.xml).
-- Wire a Memory/Constant component (its signal_out) into it with a number of SECONDS.
--   * Nothing wired (or value <= 0) -> the switch behaves 100% like vanilla.
--   * value > 0  -> after the LAST set_state signal that is > 0, the switch stays ON for that many
--                   seconds. Any new > 0 re-arms the timer; set_state = 0 is ignored while armed,
--                   so brief signal drops / a reactor ramping after refuel no longer cut power.
--
-- MP-safe & SP-safe: turning off goes through RelayComponent.SetState (server-authoritative, raises
-- a network event so clients sync). The client build runs this harmlessly.
--
-- RUS: EK тяжёлый переключатель — задержка отключения через вход "set_offdelay" (память/константа =
-- RUS: число секунд). Пусто/<=0 — ваниль. >0 — держит ВКЛ N сек после последнего set_state>0.
-- RUS: Диагностика: команда ekswitchhold_status.

local SWITCH_ID     = "ekutility_heavypowerswitch"
local OFFDELAY_CONN = "set_offdelay"
local INTERVAL      = 0.1   -- timer tick, seconds

-- Make sure MoonSharp can read the Signal struct's fields and the Connection members.
pcall(function() LuaUserData.RegisterType("Barotrauma.Items.Components.Signal") end)
pcall(function() LuaUserData.RegisterType("Barotrauma.Items.Components.Connection") end)
pcall(function() LuaUserData.RegisterType("Barotrauma.Items.Components.RelayComponent") end)

-- tracked[itemID] = { relay, delay, armed, countdown }
local tracked = {}

-- diagnostics (shown by ekswitchhold_status)
local diag = { installed = false, fires = 0, switchFires = 0,
               lastOffdelayRaw = "<none>", lastSetstateRaw = "<none>", lastErr = "<none>" }

-- Robust numeric parse: handles strings, surrounding whitespace, comma decimals, and values like
-- "10 sec" by extracting the first numeric token.
local function parseSeconds(raw)
    if raw == nil then return nil end
    local s = tostring(raw):gsub("%s+", ""):gsub(",", ".")
    local n = tonumber(s)
    if n == nil then
        local m = string.match(s, "%-?%d+%.?%d*")
        if m ~= nil then n = tonumber(m) end
    end
    return n
end

-- Read the incoming signal value robustly (struct marshalling can be finicky). Falls back to the
-- connection's LastReceivedSignal if the ptable copy is empty/unreadable.
local function sigValue(p)
    local v = nil
    pcall(function() v = p["signal"].value end)
    if v == nil or v == "" then
        local v2 = nil
        pcall(function() v2 = p["connection"].LastReceivedSignal.value end)
        if v2 ~= nil and v2 ~= "" then v = v2 end
    end
    return v
end

-- ---------------------------------------------------------------------------
-- Intercept set_state / cache the wired delay value.
-- ---------------------------------------------------------------------------
local ok, err = pcall(function()
    Hook.Patch(
        "ek.switch.offdelay.receivesignal",
        "Barotrauma.Items.Components.RelayComponent",
        "ReceiveSignal",
        function(instance, p)
            local handled = pcall(function()
                diag.fires = diag.fires + 1

                local item = instance.Item
                if item == nil or item.Prefab == nil then return end
                if string.lower(tostring(item.Prefab.Identifier)) ~= SWITCH_ID then return end

                local conn = p["connection"]
                if conn == nil then return end
                local cname = tostring(conn.Name)
                if cname ~= OFFDELAY_CONN and cname ~= "set_state" then return end

                diag.switchFires = diag.switchFires + 1

                local id = item.ID
                local st = tracked[id]
                if st == nil then
                    st = { delay = 0, armed = false, countdown = 0 }
                    tracked[id] = st
                end
                st.relay = instance

                local raw = sigValue(p)
                local val = parseSeconds(raw)

                if cname == OFFDELAY_CONN then
                    diag.lastOffdelayRaw = "'" .. tostring(raw) .. "' len=" .. tostring(#tostring(raw)) .. " parsed=" .. tostring(val)
                    -- Latch the configured delay. A successful parse (incl. 0 = disable) updates it;
                    -- an empty/garbled read keeps the last value, so a non-continuous source (a
                    -- one-shot terminal input or a memory) still arms the feature permanently.
                    if val ~= nil then st.delay = val end
                    return
                end

                -- cname == "set_state"
                diag.lastSetstateRaw = "'" .. tostring(raw) .. "' parsed=" .. tostring(val)
                local active = (st.delay or 0) > 0
                if not active then
                    st.armed = false
                    return -- vanilla behaviour
                end

                if val ~= nil and val > 0 then
                    st.armed = true
                    st.countdown = st.delay
                    -- let original run -> switch turns ON
                else
                    if st.armed then
                        p.PreventExecution = true -- swallow the 0; timer turns it off later
                    end
                end
            end)
            if not handled then diag.lastErr = "receivesignal handler error" end
        end,
        Hook.HookMethodType.Before
    )
end)
diag.installed = ok
if ok then
    print("[EK] [Switch Off-delay] patch installed OK.")
else
    print("[EK] [Switch Off-delay] PATCH FAILED: " .. tostring(err))
    diag.lastErr = "patch install: " .. tostring(err)
end

-- ---------------------------------------------------------------------------
-- Timer: count down and turn the switch off `delay` s after the last > 0.
-- ---------------------------------------------------------------------------
local function tick()
    pcall(function()
        for id, st in pairs(tracked) do
            local alive = false
            pcall(function() alive = st.relay ~= nil and st.relay.Item ~= nil and not st.relay.Item.Removed end)
            if not alive then
                tracked[id] = nil
            else
                if st.armed then
                    st.countdown = (st.countdown or 0) - INTERVAL
                    if st.countdown <= 0 then
                        pcall(function()
                            if st.relay.IsOn then st.relay.SetState(false, false) end
                        end)
                        st.armed = false
                    end
                end
            end
        end
    end)
    Timer.Wait(tick, math.floor(INTERVAL * 1000))
end
Timer.Wait(tick, math.floor(INTERVAL * 1000))

Hook.Add("roundStart", "ek.switch.offdelay.reset", function()
    tracked = {}
    diag.fires = 0; diag.switchFires = 0
    diag.lastOffdelayRaw = "<none>"; diag.lastSetstateRaw = "<none>"
end)

-- ---------------------------------------------------------------------------
-- Diagnostics command.
-- ---------------------------------------------------------------------------
_G.ekswitchhold_status = function()
    print("[EK] [Switch Off-delay] === STATUS ===")
    print(string.format("  patch installed : %s", tostring(diag.installed)))
    print(string.format("  ReceiveSignal fires (all relays): %d", diag.fires))
    print(string.format("  fires for EK heavy switch       : %d", diag.switchFires))
    print(string.format("  last set_offdelay value seen    : %s", diag.lastOffdelayRaw))
    print(string.format("  last set_state value seen       : %s", diag.lastSetstateRaw))
    print(string.format("  last error                      : %s", diag.lastErr))
    local n = 0
    for id, st in pairs(tracked) do
        n = n + 1
        local active = (st.delay or 0) > 0
        local ison = "?"
        pcall(function() ison = tostring(st.relay.IsOn) end)
        print(string.format("  switch [%d]: delay=%.1fs active=%s armed=%s countdown=%.1f IsOn=%s",
            id, st.delay or 0, tostring(active), tostring(st.armed), st.countdown or 0, ison))
    end
    if n == 0 then print("  (no EK heavy switch has received a signal yet)") end
end
