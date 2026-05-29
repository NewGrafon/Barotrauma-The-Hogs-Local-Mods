AAAC = AAAC or {}
AAAC.Path = AAAC.Path or table.pack(...)[1]
dofile(AAAC.Path .. '/Lua/AAAC/Shared/bootstrap.lua')
if CLIENT then
    dofile(AAAC.Path .. '/Lua/AAAC/Client/init.lua')
end