-- public values
local EI = {
   UgcId = '2968896556',
   Name = "Enhanced Immersion", -- значение по умолчанию
   Version = '',
   Path = table.pack(...)[1],
   Config = require 'EnhancedImmersion.Config.init',
}

local hooks = {
   Client = CLIENT and require 'EnhancedImmersion.Scripts.Hooks.client',
   Server = (SERVER or Game.IsSingleplayer) and require 'EnhancedImmersion.Scripts.Hooks.server',
   Shared = require 'EnhancedImmersion.Scripts.Hooks.shared',
}

local incompatibleMods = {
   ['3123289876'] = true -- Enhanced Immersion (Zoom only) | has its own camera zoom
}

-- Функция для получения локализованного названия
local function getLocalizedName()
    local localizedName = TextManager.Get("EnhancedImmersion.ModName")
    if localizedName then
        return localizedName.Value
    end
    return "Enhanced Immersion"
end

-- Обновляем название после инициализации
EI.Name = getLocalizedName()

for package in ContentPackageManager.EnabledPackages.All do
   if tostring(package.UgcId) == EI.UgcId then
      -- Используем локализованное название вместо имени из пакета
      EI.Version = package.ModVersion
   elseif incompatibleMods[tostring(package.UgcId)] then
      print('EI camera zoom disabled')
      EI.Config.DisableCameraZoom = true
   end
end


-- shared networking hooks
if Game.IsMultiplayer then
   -- handle config sync messages
   Networking.Receive('EIConfigSync', hooks.Shared.HandleConfigSync)

   if CLIENT then
      -- request config from server when client reloads lua
      Hook.Add('loaded', 'requestConfigFromServer', hooks.Shared.RequestConfigFromServer)
   end

   if SERVER then
      -- send config when server reloads lua
      Hook.Add('loaded', 'sendConfigToAllClients', hooks.Shared.SendConfigToClient)

      -- send config to newly connected clients
      Hook.Add('client.connected', 'sendConfigToClient', hooks.Shared.SendConfigToClient)

      -- handle config requests from clients (e.g., when they reload lua)
      Networking.Receive('EIConfigRequest', hooks.Shared.SendConfigToClient)
   end
end

if CLIENT then
   -- gui
   Hook.HookMethod('Barotrauma.GUI', 'TogglePauseMenu', {}, hooks.Client.PatchTogglePauseMenu, Hook.HookMethodType.After)

   -- camera zoom
   Hook.Add('roundStart', 'setGlobalZoomScale', hooks.Client.SetGlobalZoomScaleOnRoundStart)
   Hook.HookMethod('Barotrauma.Camera', 'CreateMatrices', hooks.Client.PatchCreateMatrices, Hook.HookMethodType.After)

   -- periscope/aim offset
   Hook.HookMethod('Barotrauma.Character', 'ControlLocalPlayer', hooks.Client.PatchControlLocalPlayer, Hook.HookMethodType.After)
end

if not (CLIENT and Game.IsMultiplayer) then

   Hook.Add("signalReceived.enterable_doora", "ZDoors", hooks.Server.HandleReceive)
   Hook.Add("signalReceived.enterable_doorb", "ZDoors", hooks.Server.HandleReceive)
   Hook.Add("signalReceived.enterable_doorc", "ZDoors", hooks.Server.HandleReceive)
   Hook.Add("signalReceived.enterable_vent", "ZDoors", hooks.Server.HandleReceive)
end

return EI