--if Game.IsMultiplayer and CLIENT then return end

NG = {} -- NeuroGuide
NG.Path = table.pack(...)[1]

-- server-side code (also run in singleplayer)
if (Game.IsMultiplayer and SERVER) or not Game.IsMultiplayer then
    dofile(NG.Path.."/Lua/Scripts/Server/TerminalButtons.lua")
end

-- client-side code
if CLIENT then
end