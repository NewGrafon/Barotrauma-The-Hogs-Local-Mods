local menu = {}

local gui = require('EnhancedImmersion.Utils.gui')
local utils = require('EnhancedImmersion.Utils.utils')
local config = require('EnhancedImmersion.Config.init')


local tempConfig = config.ReadFromFile()


local function applyConfig()
   EI.Config.LoadConfig(tempConfig)

   -- send config to server for syncing to other clients
   if Game.IsMultiplayer and utils.IsPrivilegedClient(Game.Client) then
      local msg = utils.writeConfigNetworkMessage(tempConfig)

      print('CL HOST sending EIConfigSync')
      Networking.Send(msg)
   end

   if not EI.Config.Values.SyncConfig or utils.IsPrivilegedClient(Game.Client) or Game.IsSingleplayer then
      EI.Config.WriteToFile()
      utils.UpdateGlobalZoomScale()
      return true
   end
end

function menu.OnOpen(parent)
   local frame = gui.Frame(parent, Vector2(0.3, 0.5), "GUIFrame")
   local layout = gui.LayoutGroup(frame, Vector2(0.95, 0.9))
   local header = gui.Label(layout, EI.Name .. ' ' .. TextManager.Get('AA.config').Value, GUI.GUIStyle.LargeFont, Vector2(1, 0.16))

   local padding = gui.Frame(layout, Vector2(1, 0.7), "InnerFrame")

   local innerGroup = gui.LayoutGroup(padding, Vector2(0.95, 0.9))

   -- Config sliders
   local cGroup = gui.LayoutGroup(innerGroup, Vector2(1, 0.3))
   local cameraZoom = gui.Label(cGroup, TextManager.Get("AA.camera"), GUI.GUIStyle.SubHeadingFont)
   local cameraZoomSlider = gui.SliderLabel(cGroup, 0, 100, function(value)
      tempConfig.CameraZoom = math.floor(value)
   end, tempConfig.CameraZoom)
   local cameraZoomTooltip = TextManager.Get("AA.camera.description")
   if EI.Config.DisableCameraZoom then
      cameraZoomTooltip = TextManager.Get("AA.incompatibility.nteyes")
      cameraZoomSlider[1].Enabled = false
   end
   cameraZoom.ToolTip = cameraZoomTooltip
   cameraZoomSlider[1].ToolTip = cameraZoomTooltip

   local pGroup = gui.LayoutGroup(innerGroup, Vector2(1, 0.3))
   local periscopeZoom = gui.Label(pGroup, TextManager.Get("AA.periscope"), GUI.GUIStyle.SubHeadingFont)
   local periscopeZoomSlider = gui.SliderLabel(pGroup, 0, 100, function(value)
      tempConfig.PeriscopeOffset = math.floor(value)
   end, tempConfig.PeriscopeOffset)
   periscopeZoom.ToolTip = TextManager.Get("AA.periscope.description")
   periscopeZoomSlider[1].ToolTip = TextManager.Get("AA.periscope.description")

   local gGroup = gui.LayoutGroup(innerGroup, Vector2(1, 0.3))
   local gunZoom = gui.Label(gGroup, TextManager.Get("AA.aiming"), GUI.GUIStyle.SubHeadingFont)
   local gunZoomSlider = gui.SliderLabel(gGroup, 0, 100, function(value)
      tempConfig.AimOffset = math.floor(value)
   end, tempConfig.AimOffset)
   gunZoom.ToolTip = TextManager.Get("AA.aiming.description")
   gunZoomSlider[1].ToolTip = TextManager.Get("AA.aiming.description")

   -- Server Sync button
   if Game.IsMultiplayer and utils.IsPrivilegedClient(Game.Client) then
      local syncButton = gui.TickBox(innerGroup, TextManager.Get("AA.sync"), function(state)
         tempConfig.SyncConfig = state
      end, tempConfig.SyncConfig, TextManager.Get("AA.sync.description"))
   else
      tempConfig.SyncConfig = false
   end

   -- Cancel and Apply buttons
   local bottom = gui.LayoutGroup(layout, Vector2(1, 1), true)
   bottom.Stretch = true
   bottom.RelativeSpacing = 0.01
   local cancelButton = gui.Button(bottom, TextManager.Get("cancel"), function()
      tempConfig = config.ReadFromFile()
      GUI.GUI.TogglePauseMenu()
   end)
   local applyButton = gui.Button(bottom, TextManager.Get("applysettingsbutton"), function()
      if applyConfig() then
         frame.Flash(Color(154, 213, 163, 255))
      end
      return false
   end, nil)
end

function menu.ShowMenu(parent)
   local button = gui.Button(parent, EI.Name, function() menu.OnOpen(GUI.GUI.PauseMenu) end, 'GUIButtonSmall')
   if Game.IsMultiplayer and not utils.IsPrivilegedClient(Game.Client) then
      button.Enabled = not EI.Config.Values.SyncConfig
      if not button.Enabled then
         button.ToolTip = TextManager.Get("AA.sync.enabled")
      else
         button.ToolTip = nil
      end
   end
   if EI.Config.Button == nil then
      EI.Config.Button = button
   end
end


return menu