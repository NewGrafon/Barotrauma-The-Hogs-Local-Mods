
local utils = {}

-- check if client has All permissions
function utils.IsPrivilegedClient(client)
   if client == nil then return false end
   return client.IsServerOwner or client.HasPermission(ClientPermissions.All)
end

function utils.UpdateGlobalZoomScale()
   if EI.Config.DisableCameraZoom then return end
   if not Screen.Selected.Cam or not CLIENT then return end

   local scale = Vector2(GUI.UIWidth, GameSettings.CurrentConfig.Graphics.Height).Length() / GUI.ReferenceResolution.Length()
   Screen.Selected.Cam.globalZoomScale = scale * EI.Config.Calculated.CameraZoom
end

-- convert EI's config Values object into a network message
function utils.writeConfigNetworkMessage(values)
   local msg = Networking.Start('EIConfigSync')
   msg.WriteBoolean(values.SyncConfig)

   -- if we're not syncing the config, there's no need to send it
   if values.SyncConfig then
      msg.WriteRangedInteger(values.CameraZoom, 0, 100)
      msg.WriteRangedInteger(values.PeriscopeOffset, 0, 100)
      msg.WriteRangedInteger(values.AimOffset, 0, 100)
   end
 
   return msg
end

-- converts a network message into EI's config Values object
function utils.readConfigNetworkMessage(msg)
   local config = {}
   config.SyncConfig = msg.ReadBoolean()

   if config.SyncConfig then
      config.CameraZoom = msg.ReadRangedInteger(0, 100)
      config.PeriscopeOffset = msg.ReadRangedInteger(0, 100)
      config.AimOffset = msg.ReadRangedInteger(0, 100)
   end

   return config
end

return utils
