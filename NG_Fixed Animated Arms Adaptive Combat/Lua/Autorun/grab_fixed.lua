AAAC = AAAC or {}

-- Uses vanilla drag/grab input and flow.
-- This file only expands who can be dragged so G/H (whatever the player bound to Grab)
-- works on players, bots and creatures too.

local function grabAllEnabled()
    return true
end

local function isValidGrabPair(target, holder)
    if not target or not holder then return false end
    if target == holder then return false end
    if target.Removed or holder.Removed then return false end
    if target.Enabled == false or holder.Enabled == false then return false end
    return true
end

Hook.Patch(
    "AAAC_AllowDragAnyCharacter",
    "Barotrauma.Character",
    "CanBeDraggedBy",
    {"Barotrauma.Character"},
    function(instance, ptable)
        if not grabAllEnabled() then
            return
        end

        local holder = ptable["character"]
        if not isValidGrabPair(instance, holder) then
            ptable.PreventExecution = true
            return false
        end

        -- Keep vanilla self/removed checks, but otherwise allow dragging any character.
        ptable.PreventExecution = true
        return true
    end,
    Hook.HookMethodType.Before
)
