using System;
using System.Linq;
using Barotrauma;
using Barotrauma.Networking;
using Microsoft.Xna.Framework;

namespace NetEventLogger
{
    // ==========================================================================================
    //  SERVER-side control of the fix enabled-states (the plugins' Enabled flags in the server process).
    //  - Persists them in a server-side config file (ngopt_server_config.txt in the mod folder).
    //  - Receives client requests (OptFixes.MsgSetServer): RequestState -> resend; set -> apply (only if the
    //    sender has console-command permission / is the owner), persist, broadcast the new state to everyone.
    //  - Broadcasts the 3 server states (OptFixes.MsgServerState) so every client's menu shows them.
    //  - Registers a server console command `ngopt` (for dedicated-server admins typing directly).
    //  RUS: СЕРВЕРНОЕ управление состояниями фиксов (флаги Enabled плагинов в серверном процессе).
    //  RUS: - Персист в серверный конфиг (ngopt_server_config.txt в папке мода).
    //  RUS: - Приём запросов клиента (MsgSetServer): RequestState -> переслать; set -> применить (только если у
    //  RUS:   отправителя есть право консольных команд / он владелец), сохранить, разослать новое состояние всем.
    //  RUS: - Рассылка 3 серверных состояний (MsgServerState), чтобы меню каждого клиента их показывало.
    //  RUS: - Регистрирует серверную консольную команду `ngopt` (для админов выделенного сервера).
    // ==========================================================================================
    public sealed class ServerOptControlPlugin : IAssemblyPlugin
    {
        public void PreInitPatching() { }

        public void Initialize()
        {
            try
            {
                ServerOptControl.Load();                                   // apply persisted server states   // RUS: применить сохранённые серверные состояния
                OptNet.Receive(OptFixes.MsgSetServer, ServerOptControl.OnSetServerMsg);  // client -> server (real handler)   // RUS: клиент -> сервер (реальный обработчик)
                OptNet.Receive(OptFixes.MsgServerState, OptNet.NoOp);      // assign the id so the server can SEND state   // RUS: назначить id, чтобы сервер мог ОТПРАВЛЯТЬ состояние
                ServerOptControl.RegisterCommand();
                ServerOptControl.Log(Loc.T("Серверное управление фиксами готово (команда: ngopt).",
                                           "Server-side fix control ready (command: ngopt)."), Color.LightGreen);
            }
            catch (Exception ex)
            {
                ServerOptControl.Log("ServerOptControl init error: " + ex.Message, Color.Red);
            }
        }

        public void OnLoadCompleted() { }

        public void Dispose() { try { ServerOptControl.UnregisterCommand(); } catch { } }
    }

    internal static class ServerOptControl
    {
        private static string _path;

        // --- net handler (LuaCsAction: args[0]=IReadMessage, args[1]=sending Client) ---
        // RUS: --- сетевой обработчик (LuaCsAction: args[0]=IReadMessage, args[1]=клиент-отправитель) ---
        public static void OnSetServerMsg(object[] args)
        {
            try
            {
                var msg    = args != null && args.Length > 0 ? args[0] as IReadMessage : null;
                var client = args != null && args.Length > 1 ? args[1] as Client      : null;
                if (msg == null) { return; }

                byte fix = msg.ReadByte();
                if (fix == OptFixes.RequestState)
                {
                    // read-only request: just send the current state back to the asker (no permission needed)
                    // RUS: запрос только на чтение: просто шлём текущее состояние спросившему (прав не надо)
                    BroadcastState(client?.Connection);
                    return;
                }
                if (fix == OptFixes.RequestPerf)
                {
                    // server-load snapshot: privileged clients only (host/admins)
                    // RUS: снимок серверной нагрузки: только привилегированным клиентам (хост/админы)
                    if (HasControlPermission(client)) { ServerPerfMonitor.SendSnapshotTo(client?.Connection); }
                    return;
                }
                if (fix == OptFixes.BenchStart)
                {
                    // start the server benchmark (duration sent by the client) for this (privileged) client
                    // RUS: запустить серверный бенчмарк (длительность прислал клиент) для этого (привилегированного) клиента
                    float dur = msg.ReadSingle();
                    if (HasControlPermission(client))
                    {
                        if (ServerBenchmark.Start(client?.Connection, dur))
                        {
                            Log(Loc.Ru ? $"Серверный бенчмарк запущен на {dur:F0}с (запросил '{(client != null ? client.Name : "?")}')." : $"Server benchmark started for {dur:F0}s (by '{(client != null ? client.Name : "?")}').", Color.Cyan);
                        }
                        else
                        {
                            ServerBenchmark.SendBusy(client?.Connection); // already running -> tell the requester   // RUS: уже идёт -> сообщить запросившему
                            Log(Loc.Ru ? $"Серверный бенчмарк УЖЕ идёт — запрос от '{(client != null ? client.Name : "?")}' отклонён." : $"Server benchmark ALREADY running — request from '{(client != null ? client.Name : "?")}' refused.", Color.Orange);
                        }
                    }
                    return;
                }
                if (fix == OptFixes.BenchCancel)
                {
                    if (HasControlPermission(client)) { ServerBenchmark.Cancel(); }
                    return;
                }

                byte onByte = msg.ReadByte();
                bool on = onByte != 0;

                if (!HasControlPermission(client))
                {
                    Log(Loc.Ru
                        ? $"Отклонён запрос смены серверного фикса от '{(client != null ? client.Name : "?")}' — нет прав консольных команд."
                        : $"Rejected server-fix change from '{(client != null ? client.Name : "?")}' — no console-command permission.", Color.Orange);
                    return;
                }
                if (fix >= OptFixes.Count) { return; }

                OptFixes.SetEnabled(fix, on);
                Save();
                BroadcastState(null); // to everyone   // RUS: всем
                Log(Loc.Ru
                    ? $"Серверный фикс #{fix + 1} ({OptFixes.ShortName(fix)}) -> {(on ? "ВКЛ" : "ВЫКЛ")} (от '{(client != null ? client.Name : "?")}')."
                    : $"Server fix #{fix + 1} ({OptFixes.ShortName(fix)}) -> {(on ? "ON" : "OFF")} (by '{(client != null ? client.Name : "?")}').", Color.LightGreen);
            }
            catch { }
        }

        private static bool HasControlPermission(Client c)
        {
            try
            {
                if (c == null) { return false; }
                if (GameMain.Server?.OwnerConnection != null && c.Connection == GameMain.Server.OwnerConnection) { return true; }
                return c.HasPermission(ClientPermissions.ConsoleCommands);
            }
            catch { return false; }
        }

        // Send the 3 server states. conn == null -> broadcast to all; otherwise to that one client.
        // RUS: Отправить 3 серверных состояния. conn == null -> всем; иначе одному клиенту.
        public static void BroadcastState(NetworkConnection conn)
        {
            try
            {
                IWriteMessage msg = OptNet.Start(OptFixes.MsgServerState);
                if (msg == null) { return; }
                for (int i = 0; i < OptFixes.Count; i++) { msg.WriteByte((byte)(OptFixes.GetEnabled(i) ? 1 : 0)); }
                if (conn != null) { OptNet.SendTo(msg, conn); } else { OptNet.Send(msg); }
            }
            catch { }
        }

        // --- persistence (server-side config file in the mod folder) ---
        // RUS: --- персист (серверный конфиг-файл в папке мода) ---
        private static string ConfigPath()
        {
            if (_path != null) { return _path; }
            try
            {
                var pkg = ContentPackageManager.EnabledPackages.All.FirstOrDefault(p => p != null && p.Name == "NG Logger And Optimizations");
                string dir = pkg?.Dir;
                if (!string.IsNullOrEmpty(dir)) { _path = System.IO.Path.Combine(dir, "ngopt_server_config.txt"); }
            }
            catch { }
            return _path;
        }

        public static void Load()
        {
            try
            {
                string path = ConfigPath();
                if (path == null) { return; }
                if (!Barotrauma.IO.File.Exists(path)) { Save(); return; } // no config -> keep current defaults, write them   // RUS: конфига нет -> оставить дефолты, записать их
                foreach (string line in Barotrauma.IO.File.ReadAllLines(path))
                {
                    string s = line.Trim();
                    for (int i = 0; i < OptFixes.Count; i++)
                    {
                        string key = "fix" + (i + 1) + "=";
                        if (s.StartsWith(key, StringComparison.OrdinalIgnoreCase))
                        {
                            bool? v = OptFixes.ParseOnOff(s.Substring(key.Length));
                            if (v.HasValue) { OptFixes.SetEnabled(i, v.Value); }
                        }
                    }
                }
            }
            catch { }
        }

        public static void Save()
        {
            try
            {
                string path = ConfigPath();
                if (path == null) { return; }
                string text = "";
                for (int i = 0; i < OptFixes.Count; i++) { text += "fix" + (i + 1) + "=" + (OptFixes.GetEnabled(i) ? "true" : "false") + "\r\n"; }
                Barotrauma.IO.File.WriteAllText(path, text);
            }
            catch { }
        }

        // --- server console command (dedicated-server admin console) ---
        // RUS: --- серверная консольная команда (консоль админа выделенного сервера) ---
        public static void RegisterCommand()
        {
            UnregisterCommand();
            DebugConsole.Commands.Add(new DebugConsole.Command(
                "ngopt",
                "NG Logger&Optimizations: ngopt status | ngopt server <fix> <on|off>",
                args =>
                {
                    try
                    {
                        string sub = args != null && args.Length > 0 ? args[0].ToLowerInvariant() : "";
                        if (sub == "" || sub == "status")
                        {
                            for (int i = 0; i < OptFixes.Count; i++)
                            {
                                Log("  " + OptFixes.ShortName(i) + " [" + Loc.ServerCol + "]: " + (OptFixes.GetEnabled(i) ? Loc.On : Loc.Off), Color.LightBlue);
                            }
                            return;
                        }
                        if (sub == "server")
                        {
                            int fix = OptFixes.ParseFix(args.Length > 1 ? args[1] : "");
                            bool? on = OptFixes.ParseOnOff(args.Length > 2 ? args[2] : "");
                            if (fix < 0 || !on.HasValue) { Log(Loc.T("Использование: ngopt server <1|2|3> <on|off>", "Usage: ngopt server <1|2|3> <on|off>"), Color.Orange); return; }
                            OptFixes.SetEnabled(fix, on.Value);
                            Save();
                            BroadcastState(null);
                            Log(Loc.Ru ? $"Серверный фикс #{fix + 1} -> {(on.Value ? "ВКЛ" : "ВЫКЛ")}." : $"Server fix #{fix + 1} -> {(on.Value ? "ON" : "OFF")}.", Color.LightGreen);
                            return;
                        }
                        Log(Loc.T("Использование: ngopt status | ngopt server <1|2|3> <on|off>", "Usage: ngopt status | ngopt server <1|2|3> <on|off>"), Color.Orange);
                    }
                    catch (Exception ex) { Log("ngopt error: " + ex.Message, Color.Red); }
                }));
        }

        public static void UnregisterCommand()
        {
            try
            {
                var existing = DebugConsole.Commands.Find(c => c.Names.Any(n => n.Value.Equals("ngopt", StringComparison.OrdinalIgnoreCase)));
                if (existing != null) { DebugConsole.Commands.Remove(existing); }
            }
            catch { }
        }

        public static void Log(string text, Color color)
        {
            string line = Loc.Tag + text;
            try { DebugConsole.NewMessage(line, color); } catch { }
            // also push to the host's in-game console (server runs as a child process when hosting through the game)
            // RUS: также дублируем в консоль хоста (сервер — дочерний процесс при хосте «через игру»)
            try
            {
                var server = GameMain.Server;
                if (server != null && server.OwnerConnection != null)
                {
                    foreach (var c in server.ConnectedClients)
                    {
                        if (c != null && c.Connection == server.OwnerConnection) { server.SendConsoleMessage(line, c, color); break; }
                    }
                }
            }
            catch { }
        }
    }
}
