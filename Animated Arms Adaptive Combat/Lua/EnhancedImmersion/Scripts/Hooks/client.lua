local client = {}

local utils = require('EnhancedImmersion.Utils.utils')
local menu = require('EnhancedImmersion.Scripts.menu')
local DebugConsole = LuaUserData.CreateStatic("Barotrauma.DebugConsole")
LuaUserData.MakeFieldAccessible(Descriptors["Barotrauma.Camera"],"globalZoomScale")
LuaUserData.MakeMethodAccessible(Descriptors["Barotrauma.Camera"],"CreateMatrices")


local function lerp(a,b,t)
   return a* (1- t) + b * t
end

local function getChildren(component)
   local tbl = {}
   for value in component.GetAllChildren() do
       table.insert(tbl, value)
   end
   return tbl
end

-- set camera globalZoomScale on round start
-- :BaroDev:
function client.SetGlobalZoomScaleOnRoundStart()
   local function attemptSetGlobalZoomScale()
      if Screen.Selected.Cam then
         utils.UpdateGlobalZoomScale()
         return
      else
         Timer.NextFrame(attemptSetGlobalZoomScale)
      end
   end

   Timer.NextFrame(attemptSetGlobalZoomScale)
end

-- update global zoom scale when client changes game resolution
function client.PatchCreateMatrices()
   utils.UpdateGlobalZoomScale()
end

-- set periscope and aiming offsets
-- cannot be done anywhere outside of patching Barotrauma.Character.ControlLocalPlayer
-- because Cam.OffsetAmount gets written to inside this vanilla method
-- https://github.com/FakeFishGames/Barotrauma/blob/master/Barotrauma/BarotraumaClient/ClientSource/Characters/Character.cs#L467
function client.PatchControlLocalPlayer(instance, ptable)
   if GUI.GUI.PauseMenuOpen or DebugConsole.IsOpen == true then return end

   if instance.SelectedItem and instance.SelectedItem.HasTag("periscope") then
      Screen.Selected.Cam.OffsetAmount = lerp(Screen.Selected.Cam.OffsetAmount, 0, EI.Config.Calculated.PeriscopeOffset)
   elseif PlayerInput.SecondaryMouseButtonHeld() and (instance.HasEquippedItem("weapon") or instance.HasEquippedItem("handgrenade")) then
      if not Character.DisableControls and Character.Controlled then
         local startvalue = Screen.Selected.Cam.OffsetAmount
         local endvalue = startvalue * 1.3  -- Умеренное увеличение
         local multiplier = 1.2  -- Небольшой базовый множитель

         if not GameSettings.CurrentConfig.EnableMouseLook then
            startvalue = 0
            endvalue = 450  -- Умеренное увеличение максимального отдаления
         end

         -- Небольшое дополнительное отдаление для огнестрельного оружия
         if instance.HasEquippedItem("gun") then
            multiplier = multiplier * 1.3
         end

         Screen.Selected.Cam.OffsetAmount = lerp(startvalue, endvalue * multiplier, EI.Config.Calculated.AimOffset)
      end
   end
end

-- add EI configuration menu to pause menu
function client.PatchTogglePauseMenu()
   return
end


return client