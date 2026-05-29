
local shared = {}

if not Game.IsMultiplayer then return {} end

local utils = require('EnhancedImmersion.Utils.utils')


-- on the server: check if received message comes from privileged client, and propagate new config if they are
-- on the client: read the received message and optionally sync config (if server is set to sync)
function shared.HandleConfigSync(msg, client)
   if SERVER then
      if client.Connection == Game.Server.OwnerConnection or client.HasPermission(ClientPermissions.All) then
         local newConfig = utils.readConfigNetworkMessage(msg)

         -- only send new config if:
         -- incoming config is set to sync
         -- incoming config sync is not the same as current config sync
         if newConfig.SyncConfig or (newConfig.SyncConfig ~= EI.Config.Values.SyncConfig) then
            print('SV sending EIConfigSync to all clients (syncing is ' .. (newConfig.SyncConfig and "ON" or "OFF") .. ")")
            Networking.Send(utils.writeConfigNetworkMessage(newConfig))
         end

         if newConfig.SyncConfig then
            EI.Config.LoadConfig(newConfig)
         else
            EI.Config.Values.SyncConfig = false
         end
      end
   elseif CLIENT then
      print('CL received EIConfigSync')
      local serverConfig = utils.readConfigNetworkMessage(msg)

      EI.Config.Values.SyncConfig = serverConfig.SyncConfig
      if EI.Config.Values.SyncConfig then
         EI.Config.RecalculateValues(serverConfig)
         utils.UpdateGlobalZoomScale()
      else
         print('CL syncing disabled by server')
      end
      if EI.Config.Button ~= nil then
         EI.Config.Button.Enabled = not EI.Config.Values.SyncConfig
         -- reload the pause menu so the config button state gets updated
         if GUI.GUI.PauseMenuOpen then
            EI.Config.Button.AddToGUIUpdateList()
         end
      end
   end
end

-- send a config request network message to the server
-- the server should respond with the current config for EI
function shared.RequestConfigFromServer()
   if not Game.Client.IsServerOwner then
      print('CL sending EIConfigRequest')
      Networking.Send(Networking.Start('EIConfigRequest'))
   end
end

-- send the config to the specified client
-- if client is nil, sends to all connected clients
function shared.SendConfigToClient(msg, client)
   print('SV sending EIConfigSync to ' .. (client ~= nil and 'single client' or 'all clients'))
   local response = utils.writeConfigNetworkMessage(EI.Config.Values)
   Networking.Send(response, client and client.Connection)
end


return shared
