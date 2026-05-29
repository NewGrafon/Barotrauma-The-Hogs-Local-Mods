
local config = {}

config.ConfigPath = Game.SaveFolder .. '/ModConfigs/'
config.ConfigFile = 'EnhancedImmersion.json'

-- default configuration values
config.Default = require 'EnhancedImmersion.Config.default'

-- values read from config file / edited in-game via gui
config.Values = {}
-- values automatically derived from config.Values, used for calculations
config.Calculated = {}

config.DisableCameraZoom = false


-- calculate values to be used in the scripts
function config.RecalculateValues(values)
   if values == nil then values = config.Values end

   local calc = {}
   calc.CameraZoom = 1 + (values.CameraZoom / 100)
   calc.PeriscopeOffset = values.PeriscopeOffset * -0.002
   calc.AimOffset = math.clamp(values.AimOffset / 100, 0, 1)

   config.Calculated = calc
   return calc
end

function config.WriteToFile(values, path)
   if values == nil then values = config.Values end
   if path == nil then path = config.ConfigPath .. config.ConfigFile end

   File.Write(path, json.serialize(values))
end

function config.ReadFromFile(path)
   if path == nil then path = config.ConfigPath .. config.ConfigFile end

   if not File.Exists(path) then
      return config.Default
   end

   return json.parse(File.Read(path))
end

function config.LoadConfig(values)
   config.Values = values
   config.RecalculateValues(values)
end


-- initialisation
if not File.DirectoryExists(config.ConfigPath) then
   File.CreateDirectory(config.ConfigPath)
end

if next(config.Values) == nil then
   local values = config.ReadFromFile()

   for key, value in pairs(config.Default) do
      if values[key] == nil then
         values[key] = value
      end
   end

   config.LoadConfig(values)
   config.WriteToFile(values)
end


return config
