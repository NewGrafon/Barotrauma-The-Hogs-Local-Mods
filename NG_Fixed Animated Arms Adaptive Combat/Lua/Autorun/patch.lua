if SERVER then return end

DebugConsole = LuaUserData.CreateStatic("Barotrauma.DebugConsole")

local debugConsoleWasOpen = false

Hook.Patch("Barotrauma.DebugConsole", "ThrowError", {"System.String", "System.Exception", "Barotrauma.ContentPackage", "System.Boolean", "System.Boolean"}, function (instance, ptable)
    if ptable["createMessageBox"] then return end

    debugConsoleWasOpen = DebugConsole.IsOpen
end, Hook.HookMethodType.Before)

Hook.Patch("Barotrauma.DebugConsole", "ThrowError", {"System.String", "System.Exception", "Barotrauma.ContentPackage", "System.Boolean", "System.Boolean"}, function (instance, ptable)
    if ptable["createMessageBox"] then return end

    if not debugConsoleWasOpen then
        DebugConsole.IsOpen = false
    end
end, Hook.HookMethodType.After)