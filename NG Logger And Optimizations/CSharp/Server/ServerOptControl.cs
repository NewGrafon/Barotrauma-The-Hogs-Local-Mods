using System;
using System.Linq;
using System.Reflection;
using Barotrauma;
using Barotrauma.Networking;
using HarmonyLib;
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
        private static Harmony _h;

        public void PreInitPatching() { }

        public void Initialize()
        {
            try
            {
                ServerOptControl.Load();                                   // apply persisted server states   // RUS: применить сохранённые серверные состояния
                OptNet.Receive(OptFixes.MsgSetServer, ServerOptControl.OnSetServerMsg);  // client -> server (real handler)   // RUS: клиент -> сервер (реальный обработчик)
                OptNet.Receive(OptFixes.MsgServerState, OptNet.NoOp);      // assign the id so the server can SEND state   // RUS: назначить id, чтобы сервер мог ОТПРАВЛЯТЬ состояние
                OptNet.Receive(LogChecks.MsgSetLog, ServerOptControl.OnSetLogMsg);   // client -> server: toggle a server log-check   // RUS: клиент -> сервер: переключить серверную проверку логов
                OptNet.Receive(LogChecks.MsgLogState, OptNet.NoOp);        // assign the id so the server can SEND log states   // RUS: назначить id, чтобы сервер мог ОТПРАВЛЯТЬ состояния проверок
                ServerOptControl.RegisterCommand();

                // Re-apply enabled fixes to the world at EVERY round start. The server sets the fix flags at
                // init (empty world) and ContainedEffectsOptPlugin.SetEnabled early-returns when already
                // enabled, so pre-loaded containers/items would never be processed — mirrors the client's
                // round-start ForceReapplyToWorld. Patch every GameSession.StartRound overload (idempotent).
                // RUS: Переприменять включённые фиксы к миру на КАЖДОМ старте раунда. Сервер ставит флаги при
                // RUS: init (мир пуст), а SetEnabled рано выходит при уже включённом флаге — иначе загруженные
                // RUS: контейнеры/предметы не обрабатываются. Зеркалит клиентский ForceReapplyToWorld. Патчим
                // RUS: все перегрузки GameSession.StartRound (идемпотентно).
                try
                {
                    if (_h == null)
                    {
                        _h = new Harmony("ng.serveroptcontrol");
                        var post = new HarmonyMethod(typeof(ServerRoundStartPatch).GetMethod(
                            nameof(ServerRoundStartPatch.Postfix), BindingFlags.Static | BindingFlags.Public));
                        foreach (var m in typeof(GameSession).GetMethods(BindingFlags.Instance | BindingFlags.Public)
                                                             .Where(mi => mi.Name == "StartRound"))
                        {
                            try { _h.Patch(m, postfix: post); } catch { }
                        }
                    }
                }
                catch (Exception ex) { ServerOptControl.Log("round-start patch error: " + ex.Message, Color.Orange); }

                ServerOptControl.Log(Loc.T("Серверное управление фиксами готово (команда: ngopt).",
                                           "Server-side fix control ready (command: ngopt)."), Color.LightGreen);
            }
            catch (Exception ex)
            {
                ServerOptControl.Log("ServerOptControl init error: " + ex.Message, Color.Red);
            }
        }

        public void OnLoadCompleted() { }

        public void Dispose()
        {
            try { ServerOptControl.UnregisterCommand(); } catch { }
            try { _h?.UnpatchSelf(); } catch { }
            _h = null;
        }
    }

    // Postfix on GameSession.StartRound (all overloads): once the round's submarine + items are loaded,
    // re-apply the enabled server-side fixes to the world. Server assembly only -> server-side only.
    // RUS: Постфикс на GameSession.StartRound (все перегрузки): когда сабмарина + предметы раунда загружены —
    // RUS: переприменить включённые серверные фиксы к миру. Только серверная сборка -> только на сервере.
    public static class ServerRoundStartPatch
    {
        public static void Postfix() { try { ServerOptControl.ReapplyToWorld(); } catch { } }
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

                int level = msg.ReadByte();

                if (!HasControlPermission(client))
                {
                    Log(Loc.Ru
                        ? $"Отклонён запрос смены серверного фикса от '{(client != null ? client.Name : "?")}' — нет прав консольных команд."
                        : $"Rejected server-fix change from '{(client != null ? client.Name : "?")}' — no console-command permission.", Color.Orange);
                    return;
                }
                if (fix >= OptFixes.Count) { return; }

                OptFixes.SetLevel(fix, level);
                Save();
                BroadcastState(null); // to everyone   // RUS: всем
                string lbl = OptFixes.LevelLabel(fix, OptFixes.GetLevel(fix));
                Log(Loc.Ru
                    ? $"Серверный фикс #{fix + 1} ({OptFixes.ShortName(fix)}) -> {lbl} (от '{(client != null ? client.Name : "?")}')."
                    : $"Server fix #{fix + 1} ({OptFixes.ShortName(fix)}) -> {lbl} (by '{(client != null ? client.Name : "?")}').", Color.LightGreen);
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
                for (int i = 0; i < OptFixes.Count; i++) { msg.WriteByte((byte)OptFixes.GetLevel(i)); }
                if (conn != null) { OptNet.SendTo(msg, conn); } else { OptNet.Send(msg); }
            }
            catch { }
        }

        // Re-apply the enabled fixes to the CURRENTLY-loaded world. Needed because the server sets the fix
        // flags at init when the world is empty, and ContainedEffectsOptPlugin.SetEnabled early-returns when
        // the flag is already its default (ON) — so pre-loaded containers (e.g. AAAC reload guns holding
        // shells) are never trimmed until something forces it. Calls the world-processing DIRECTLY (not via
        // SetEnabled), so it runs regardless of the current flag state. Idempotent. Mirrors the client's
        // OptConfig.ForceReapplyToWorld. Invoked at round start and after a mid-round reloadcs.
        // RUS: Переприменить включённые фиксы к ТЕКУЩЕ загруженному миру. Нужно потому, что сервер ставит флаги
        // RUS: при init на пустом мире, а SetEnabled рано выходит, если флаг уже в дефолте (ВКЛ) — поэтому
        // RUS: загруженные контейнеры (напр. перезарядные стволы AAAC с патронами) не подрезаются, пока что-то
        // RUS: это не форсирует. Зовём обработку мира НАПРЯМУЮ (не через SetEnabled), поэтому работает при любом
        // RUS: состоянии флага. Идемпотентно. Зеркалит клиентский ForceReapplyToWorld. На старте раунда / reloadcs.
        public static void ReapplyToWorld()
        {
            try { if (OptFixes.GetEnabled(0)) { NGContainerOpt.TrimSpentEffectsPatch.TrimAllContainers(); } } catch { }
            try { if (OptFixes.GetEnabled(1)) { NGNearbyOpt.NearbyTargetsIndex.PopulateExisting(); } } catch { }
            // confirmation log only while a round is actually running (so the empty-world Load() call stays quiet)
            // RUS: лог-подтверждение только когда раунд реально идёт (чтобы вызов из Load() на пустом мире молчал)
            try
            {
                if (GameMain.GameSession?.IsRunning ?? false)
                {
                    Log(Loc.Ru
                        ? $"Серверные фиксы переприменены к миру: контейнеры={(OptFixes.GetEnabled(0) ? "ВКЛ" : "ВЫКЛ")}, поиск рядом={(OptFixes.GetEnabled(1) ? "ВКЛ" : "ВЫКЛ")}."
                        : $"Server fixes re-applied to the world: containers={(OptFixes.GetEnabled(0) ? "ON" : "OFF")}, nearby={(OptFixes.GetEnabled(1) ? "ON" : "OFF")}.", Color.Cyan);
                }
            }
            catch { }
        }

        // ---- "Console logs" server-side checks (net + flags live in the server assembly) ----
        // RUS: ---- серверные проверки «Консольные логи» (сеть + флаги в серверной сборке) ----

        // Map a check index to its SERVER-side toggle flag. Only checks 1,2 are server-side (0 is client).
        // RUS: Сопоставить индекс проверки её СЕРВЕРНОМУ флагу. Серверные только 1,2 (0 — клиентская).
        public static bool GetServerLog(int i)
        {
            switch (i)
            {
                case 1: try { return NetEventLoggerPlugin.LogEnabled; } catch { return false; }
                case 2: try { return ServerPerfMonitor.AutoPush;     } catch { return false; }
                default: return false;
            }
        }
        public static void SetServerLog(int i, bool on)
        {
            switch (i)
            {
                case 1: try { NetEventLoggerPlugin.LogEnabled = on; } catch { } break;
                case 2: try { ServerPerfMonitor.AutoPush     = on; } catch { } break;
            }
        }

        // Send the server-side check states (Count bytes; client-side index 0 always sent as 0).
        // RUS: Отправить состояния серверных проверок (Count байт; клиентский индекс 0 всегда как 0).
        public static void BroadcastLogState(NetworkConnection conn)
        {
            try
            {
                IWriteMessage msg = OptNet.Start(LogChecks.MsgLogState);
                if (msg == null) { return; }
                for (int i = 0; i < LogChecks.Count; i++)
                {
                    msg.WriteByte((byte)(LogChecks.IsServerSide(i) && GetServerLog(i) ? 1 : 0));
                }
                if (conn != null) { OptNet.SendTo(msg, conn); } else { OptNet.Send(msg); }
            }
            catch { }
        }

        // --- net handler for the "Console logs" server checks (args[0]=IReadMessage, args[1]=Client) ---
        // RUS: --- сетевой обработчик серверных проверок «Консольные логи» (args[0]=IReadMessage, args[1]=Client) ---
        public static void OnSetLogMsg(object[] args)
        {
            try
            {
                var msg    = args != null && args.Length > 0 ? args[0] as IReadMessage : null;
                var client = args != null && args.Length > 1 ? args[1] as Client      : null;
                if (msg == null) { return; }

                byte check = msg.ReadByte();
                if (check == LogChecks.RequestLogState)
                {
                    BroadcastLogState(client?.Connection); // read-only request, no permission needed   // RUS: запрос только на чтение, прав не надо
                    return;
                }
                byte on = msg.ReadByte();

                if (!HasControlPermission(client))
                {
                    Log(Loc.Ru
                        ? $"Отклонён запрос смены серверной проверки логов от '{(client != null ? client.Name : "?")}' — нет прав консольных команд."
                        : $"Rejected server log-check change from '{(client != null ? client.Name : "?")}' — no console-command permission.", Color.Orange);
                    return;
                }
                if (check >= LogChecks.Count || !LogChecks.IsServerSide(check)) { return; }

                SetServerLog(check, on != 0);
                Save();
                BroadcastLogState(null);
                Log(Loc.Ru
                    ? $"Серверная проверка логов «{LogChecks.Name(check)}» -> {(on != 0 ? "ВКЛ" : "ВЫКЛ")} (от '{(client != null ? client.Name : "?")}')."
                    : $"Server log-check '{LogChecks.Name(check)}' -> {(on != 0 ? "ON" : "OFF")} (by '{(client != null ? client.Name : "?")}').", Color.LightGreen);
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

        // Default SERVER fix profile: everything ON except the "weak" optimizations (fixes 3 & 5 — the
        // experimental throttles), which start OFF. Server checks ("Console logs") stay OFF (their own default).
        // RUS: Профиль серверных фиксов по умолчанию: всё ВКЛ, кроме «слабых» оптимизаций (фиксы 3 и 5 —
        // RUS: экспериментальные троттлы), они стартуют ВЫКЛ. Серверные проверки логов остаются ВЫКЛ (свой дефолт).
        private static void ApplyServerDefaults()
        {
            for (int i = 0; i < OptFixes.Count; i++)
            {
                bool weak = (i == 2 || i == 4); // the "weak optimization" group   // RUS: группа «слабая оптимизация»
                OptFixes.SetLevel(i, weak ? 0 : 1);
            }
        }

        public static void Load()
        {
            try
            {
                ApplyServerDefaults(); // baseline profile; a saved config (if any) overrides it per-key below   // RUS: базовый профиль; сохранённый конфиг (если есть) перекроет его по ключам ниже
                string path = ConfigPath();
                if (path == null) { return; }
                if (!Barotrauma.IO.File.Exists(path)) { Save(); return; } // no config -> write the default profile   // RUS: конфига нет -> записать профиль по умолчанию
                foreach (string line in Barotrauma.IO.File.ReadAllLines(path))
                {
                    string s = line.Trim();
                    for (int i = 0; i < OptFixes.Count; i++)
                    {
                        string key = "fix" + (i + 1) + "=";
                        if (s.StartsWith(key, StringComparison.OrdinalIgnoreCase))
                        {
                            OptFixes.SetLevel(i, OptFixes.ParseLevel(s.Substring(key.Length)));
                        }
                    }
                    for (int i = 0; i < LogChecks.Count; i++)
                    {
                        if (!LogChecks.IsServerSide(i)) { continue; }
                        string lkey = "logcheck" + (i + 1) + "=";
                        if (s.StartsWith(lkey, StringComparison.OrdinalIgnoreCase))
                        {
                            SetServerLog(i, OptFixes.ParseOnOff(s.Substring(lkey.Length)) ?? false);
                        }
                    }
                }
                Save(); // rewrite the file so keys missing from an older-version config get added (with their defaults)   // RUS: переписать файл, чтобы ключи, которых не было в конфиге старой версии, дописались (со своими дефолтами)
            }
            catch { }
            // covers a mid-round reloadcs (world already loaded); on a normal startup the world is empty -> no-op,
            // and the round itself is handled by the GameSession.StartRound postfix above.
            // RUS: покрывает reloadcs посреди раунда (мир уже загружен); на обычном старте мир пуст -> no-op,
            // RUS: а сам раунд обрабатывается постфиксом GameSession.StartRound выше.
            try { ReapplyToWorld(); } catch { }
        }

        public static void Save()
        {
            try
            {
                string path = ConfigPath();
                if (path == null) { return; }
                string text = "";
                for (int i = 0; i < OptFixes.Count; i++) { text += "fix" + (i + 1) + "=" + OptFixes.GetLevel(i) + "\r\n"; }
                for (int i = 0; i < LogChecks.Count; i++) { if (LogChecks.IsServerSide(i)) { text += "logcheck" + (i + 1) + "=" + (GetServerLog(i) ? 1 : 0) + "\r\n"; } }
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
                "NG Logger&Optimizations: ngopt status | ngopt server <fix> <on|off> | ngopt log <2|3> <on|off>",
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
                            for (int i = 0; i < LogChecks.Count; i++)
                            {
                                if (!LogChecks.IsServerSide(i)) { continue; }
                                Log("  log" + (i + 1) + " " + LogChecks.Name(i) + ": " + (GetServerLog(i) ? Loc.On : Loc.Off), Color.LightBlue);
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
                        if (sub == "log")
                        {
                            int chk  = LogChecks.Parse(args.Length > 1 ? args[1] : "");
                            bool? on = OptFixes.ParseOnOff(args.Length > 2 ? args[2] : "");
                            if (chk < 0 || !on.HasValue) { Log(Loc.T("Использование: ngopt log <2|3> <on|off>  (2=диагностика очереди, 3=снимки нагрузки; 1 — клиентская)", "Usage: ngopt log <2|3> <on|off>  (2=queue diag, 3=load snapshots; 1 is client-side)"), Color.Orange); return; }
                            if (!LogChecks.IsServerSide(chk)) { Log(Loc.T("Проверка #1 — клиентская, включается на клиенте (clientperf auto / меню).", "Check #1 is client-side; toggle it on the client (clientperf auto / menu)."), Color.Orange); return; }
                            SetServerLog(chk, on.Value);
                            Save();
                            BroadcastLogState(null);
                            Log(Loc.Ru ? $"Серверная проверка логов «{LogChecks.Name(chk)}» -> {(on.Value ? "ВКЛ" : "ВЫКЛ")}." : $"Server log-check '{LogChecks.Name(chk)}' -> {(on.Value ? "ON" : "OFF")}.", Color.LightGreen);
                            return;
                        }
                        Log(Loc.T("Использование: ngopt status | ngopt server <1|2|3> <on|off> | ngopt log <2|3> <on|off>", "Usage: ngopt status | ngopt server <1|2|3> <on|off> | ngopt log <2|3> <on|off>"), Color.Orange);
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
