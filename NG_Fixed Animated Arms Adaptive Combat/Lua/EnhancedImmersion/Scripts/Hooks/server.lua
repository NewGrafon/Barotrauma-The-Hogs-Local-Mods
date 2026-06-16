
local server = {}

if not SERVER and Game.IsMultiplayer then return {} end

local delay = {}

local sounds = {
    enterable_door1 = "sfx_doorsounda",
    enterable_door2 = "sfx_doorsoundb",
}

function server.HandleReceive(signal, connection)
   if signal.sender == nil then return end

   local minDelay = 0

   if connection.Item.HasTag("smalldelay") then
      minDelay = 0.2
   elseif connection.Item.HasTag("mediumdelay") then
      minDelay = 0.5
   elseif connection.Item.HasTag("bigdelay") then
      minDelay = 1
   elseif connection.Item.HasTag("hugedelay") then
      minDelay = 2
   end

   if delay[signal.sender] and Timer.GetTime() - delay[signal.sender] < minDelay then
      return
   end

   signal.sender.TeleportTo(connection.Item.WorldPosition)

   if signal.sender.SelectedCharacter then
      signal.sender.SelectedCharacter.TeleportTo(connection.Item.WorldPosition)
   end

   local sound = sounds[connection.Item.Prefab.Identifier.Value]
   if sound then
      Entity.Spawner.AddItemToSpawnQueue(ItemPrefab.GetItemPrefab(sound), connection.Item.WorldPosition)
   end

   delay[signal.sender] = Timer.GetTime()
end


return server
