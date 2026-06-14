using System;
using System.Linq;
using Barotrauma;
using Barotrauma.Networking;
using Microsoft.Xna.Framework;

namespace NetEventLogger
{
    // ==========================================================================================
    //  CLIENT-side networking + console command for the per-fix CLIENT/SERVER toggles.
    //  - Receives the server's broadcast of its 3 fix states (OptFixes.MsgServerState) and caches them
    //    so the menu can show them (read-only for non-privileged clients).
    //  - RequestServerState(): asks the server to (re)send its state (sent on menu open / round start).
    //  - RequestSetServer(): a host/admin asks the server to flip a server fix (the server re-validates).
    //  - Console command `ngopt`: status | client <fix> <on|off> | server <fix> <on|off>.
    //  Everything is best-effort: if networking is unavailable, the server column is just "unknown".
    //  RUS: КЛИЕНТСКАЯ сеть + консольная команда для тумблеров КЛИЕНТ/СЕРВЕР каждого фикса.
    //  RUS: - Принимает рассылку 3 серверных состояний (MsgServerState) и кэширует их для меню (read-only
    //  RUS:   у непривилегированных клиентов).
    //  RUS: - RequestServerState(): просит сервер (пере)слать состояние (при открытии меню / старте раунда).
    //  RUS: - RequestSetServer(): хост/админ просит сервер переключить серверный фикс (сервер перепроверяет).
    //  RUS: - Команда `ngopt`: status | client <fix> <on|off> | server <fix> <on|off>.
    //  RUS: Всё best-effort: если сеть недоступна — серверная колонка просто «неизвестно».
    // ==========================================================================================
    internal static class ClientOptNet
    {
        private static bool _inited;
        private static readonly bool[] _serverState = new bool[OptFixes.Count];
        private static readonly bool[] _serverKnown = new bool[OptFixes.Count];
        private static readonly int[]  _serverLevel = new int[OptFixes.Count]; // 0..3 throttle level per server fix   // RUS: уровень троттлинга 0..3 по каждому серверному фиксу
        private static readonly bool[] _serverLogState = new bool[LogChecks.Count]; // server-side "Console logs" check states (net)   // RUS: состояния серверных проверок «Консольные логи» (по сети)
        private static readonly bool[] _serverLogKnown = new bool[LogChecks.Count];

        public static void Init()
        {
            if (_inited) { return; }
            _inited = true;
            ServerBenchStatus = Loc.T("Готов. «Серверный бенчмарк» — топ нагрузки сервера.", "Ready. «Server benchmark» — top server load.");
            try { OptNet.Receive(OptFixes.MsgServerState, OnServerStateMsg); } catch { } // real handler (server -> client)   // RUS: реальный обработчик (сервер -> клиент)
            try { OptNet.Receive(OptFixes.MsgServerPerf, OnServerPerfMsg); } catch { }    // server load snapshots (privileged only)   // RUS: снимки серверной нагрузки (только привилегированным)
            try { OptNet.Receive(OptFixes.MsgServerBench, OnServerBenchMsg); } catch { }  // server benchmark report   // RUS: отчёт серверного бенчмарка
            try { OptNet.Receive(OptFixes.MsgSetServer, OptNet.NoOp); } catch { }         // request the id so the client can SEND set/request   // RUS: запросить id, чтобы клиент мог ОТПРАВЛЯТЬ set/запрос
            try { OptNet.Receive(LogChecks.MsgLogState, OnLogStateMsg); } catch { }       // server -> client: server log-check states   // RUS: сервер -> клиент: состояния серверных проверок логов
            try { OptNet.Receive(LogChecks.MsgSetLog, OptNet.NoOp); } catch { }           // request the id so the client can SEND set/request   // RUS: запросить id, чтобы клиент мог ОТПРАВЛЯТЬ set/запрос
            try { RegisterCommand(); } catch { }
        }

        // True only in multiplayer (a server connection exists).
        // RUS: True только в сетевой игре (есть соединение с сервером).
        public static bool InMP { get { try { return GameMain.Client != null; } catch { return false; } } }

        public static bool ServerKnown(int i) => i >= 0 && i < OptFixes.Count && _serverKnown[i];
        public static bool ServerState(int i) => i >= 0 && i < OptFixes.Count && _serverState[i];
        public static int  ServerLevel(int i) => (i >= 0 && i < OptFixes.Count) ? _serverLevel[i] : 0;

        // "Console logs" checks. Client-side check 0 reads the LOCAL flag; server-side checks (1,2) read the
        // net-synced state (ServerLogKnown tells whether the server has reported it yet).
        // RUS: Проверки «Консольные логи». Клиентская 0 — ЛОКАЛЬНЫЙ флаг; серверные (1,2) — синхронизированное
        // RUS: по сети состояние (ServerLogKnown — сообщил ли уже сервер).
        public static bool ServerLogKnown(int i) => i >= 0 && i < LogChecks.Count && _serverLogKnown[i];
        public static bool ServerLogState(int i) => i >= 0 && i < LogChecks.Count && _serverLogState[i];
        public static bool LogCheckEnabled(int i)
        {
            if (i == 0) { try { return ClientPerf.AutoLog; } catch { return false; } }
            return ServerLogState(i);
        }

        // Can the local client change SERVER fixes? (server owner / has console-command permission)
        // RUS: Может ли локальный клиент менять СЕРВЕРНЫЕ фиксы? (владелец сервера / есть право консольных команд)
        public static bool CanControlServer()
        {
            try
            {
                var c = GameMain.Client;
                if (c == null) { return false; }
                if (c.IsServerOwner) { return true; }
                return c.HasPermission(ClientPermissions.ConsoleCommands);
            }
            catch { return false; }
        }

        // --- receive: server broadcast of its 3 states (args[0] = IReadMessage) ---
        // RUS: --- приём: рассылка сервером его 3 состояний (args[0] = IReadMessage) ---
        private static void OnServerStateMsg(object[] args)
        {
            try
            {
                var msg = args != null && args.Length > 0 ? args[0] as IReadMessage : null;
                if (msg == null) { return; }
                for (int i = 0; i < OptFixes.Count; i++)
                {
                    int lvl = msg.ReadByte();
                    _serverLevel[i] = lvl;
                    _serverState[i] = lvl > 0;
                    _serverKnown[i] = true;
                }
            }
            catch { }
        }

        // --- receive: server broadcast of its log-check states (args[0] = IReadMessage) ---
        // RUS: --- приём: рассылка сервером состояний его проверок логов (args[0] = IReadMessage) ---
        private static void OnLogStateMsg(object[] args)
        {
            try
            {
                var msg = args != null && args.Length > 0 ? args[0] as IReadMessage : null;
                if (msg == null) { return; }
                for (int i = 0; i < LogChecks.Count; i++)
                {
                    bool on = msg.ReadByte() != 0;
                    _serverLogState[i] = on;
                    _serverLogKnown[i] = true;
                }
            }
            catch { }
        }

        // --- receive: server load snapshot (only privileged clients ever get this) ---
        // RUS: --- приём: снимок серверной нагрузки (приходит только привилегированным клиентам) ---
        private static void OnServerPerfMsg(object[] args)
        {
            try
            {
                var msg = args != null && args.Length > 0 ? args[0] as IReadMessage : null;
                if (msg == null) { return; }
                float updateRate = msg.ReadSingle();
                float avgMs      = msg.ReadSingle();
                float maxMs      = msg.ReadSingle();
                int items   = msg.ReadUInt16();
                int chars   = msg.ReadUInt16();
                int bodies  = msg.ReadUInt16();
                int clients = msg.ReadByte();

                // target sim rate is 60 (Timing.FixedUpdateRate); colour by how well the server keeps up
                // RUS: целевая частота симуляции — 60 (Timing.FixedUpdateRate); цвет по тому, как сервер успевает
                Color col = updateRate >= 58f ? Color.LightGreen : (updateRate >= 50f ? Color.Yellow : Color.OrangeRed);
                ClientPerf.Log(Loc.Ru
                    ? $"[СЕРВЕР] {updateRate:F0} апд/с (цель 60) | тик {avgMs:F1}мс (макс {maxMs:F1}) | предметы {items}, перс {chars}, тела {bodies}, клиентов {clients}"
                    : $"[SERVER] {updateRate:F0} upd/s (target 60) | tick {avgMs:F1}ms (max {maxMs:F1}) | items {items}, chars {chars}, bodies {bodies}, clients {clients}",
                    col);
                if (updateRate > 0 && updateRate < 50f)
                {
                    ClientPerf.Log(Loc.T("  ВНИМАНИЕ: сервер не успевает за симуляцией (60/с) — источник лага/откатов.",
                                         "  NOTE: the server can't keep up with the sim (60/s) — source of lag/rollbacks."), Color.Orange);
                }
            }
            catch { }
        }

        // --- server benchmark coordinator (privileged) ---
        // RUS: --- координатор серверного бенчмарка (привилегированный) ---
        private static bool     _benchRunning;
        private static DateTime _benchStart;
        private static double   _benchDuration = 30;
        public static bool ServerBenchRunning => _benchRunning;

        // SERVER benchmark duration (separate from the client one). Cycled via the menu (Server section).
        // RUS: Длительность СЕРВЕРНОГО бенчмарка (отдельно от клиентской). Циклится из меню (раздел Сервер).
        public static double ServerBenchDuration = 30.0;
        public static void CycleServerBenchDuration() { ServerBenchDuration = Benchmark.NextPreset(ServerBenchDuration); }

        // SERVER benchmark status line — kept SEPARATE from the client's Benchmark.StatusText so the two
        // benchmarks' countdowns don't overwrite each other. The menu shows the active section's status.
        // RUS: Строка статуса СЕРВЕРНОГО бенчмарка — ОТДЕЛЬНО от клиентской Benchmark.StatusText, чтобы отсчёты
        // RUS: двух бенчмарков не перекрывали друг друга. Меню показывает статус активного раздела.
        public static string ServerBenchStatus = "";
        private static void SetServerStatus(string s) { if (s != null) { ServerBenchStatus = s; } }

        // History of SERVER benchmark results — the server broadcasts each result to ALL privileged clients,
        // so every privileged client accumulates the same history. The menu navigates it (Server section).
        // RUS: История результатов СЕРВЕРНОГО бенчмарка — сервер рассылает каждый результат ВСЕМ привилегированным
        // RUS: клиентам, так что у всех накапливается одна история. Меню листает её (раздел Сервер).
        public static readonly BenchHistory ServerHistory = new BenchHistory();

        // Start the 60s server benchmark, or cancel the running one. Privileged + MP only.
        // RUS: Запустить 60с серверный бенчмарк или отменить идущий. Только привилегированный + сеть.
        public static void StartOrCancelServerBench()
        {
            try
            {
                if (!InMP) { SetServerStatus(Loc.ServerMPOnly); return; }
                if (!CanControlServer()) { SetServerStatus(Loc.T("Серверный бенчмарк может запускать только хост/админ.", "Only the host/admin can run the server benchmark.")); return; }

                if (_benchRunning)
                {
                    SendCode(OptFixes.BenchCancel);
                    _benchRunning = false;
                    SetServerStatus(Loc.T("Серверный бенчмарк отменён.", "Server benchmark cancelled."));
                    return;
                }
                // start with the shared benchmark duration
                // RUS: запуск с общей длительностью бенчмарка
                try
                {
                    IWriteMessage m = OptNet.Start(OptFixes.MsgSetServer);
                    if (m != null) { m.WriteByte(OptFixes.BenchStart); m.WriteSingle((float)ServerBenchDuration); OptNet.Send(m); }
                }
                catch { }
                _benchRunning = true;
                _benchStart = DateTime.UtcNow;
                _benchDuration = ServerBenchDuration;
                SetServerStatus(Loc.T("Серверный бенчмарк идёт… жди отчёт.", "Server benchmark running… wait for the report."));
            }
            catch { }
        }

        // Called each frame while the menu is open: un-stick the button if the report never arrives.
        // RUS: Зовётся каждый кадр при открытом меню: снять «зависший» бенчмарк, если отчёт так и не пришёл.
        public static void ServerBenchTick()
        {
            if (!_benchRunning) { return; }
            try
            {
                if ((DateTime.UtcNow - _benchStart).TotalSeconds > _benchDuration + 15)
                {
                    _benchRunning = false;
                    SetServerStatus(Loc.T("Серверный бенчмарк: отчёт не пришёл (таймаут). Сеть недоступна?", "Server benchmark: no report (timeout). Networking unavailable?"));
                }
            }
            catch { }
        }

        private static void SendCode(byte code)
        {
            try
            {
                if (GameMain.Client == null) { return; }
                IWriteMessage msg = OptNet.Start(OptFixes.MsgSetServer);
                if (msg == null) { return; }
                msg.WriteByte(code);
                OptNet.Send(msg);
            }
            catch { }
        }

        // --- receive: server benchmark report (text) -> show in the menu result window ---
        // RUS: --- приём: отчёт серверного бенчмарка (текст) -> показать в окне результатов меню ---
        private static void OnServerBenchMsg(object[] args)
        {
            try
            {
                var msg = args != null && args.Length > 0 ? args[0] as IReadMessage : null;
                if (msg == null) { return; }

                byte phase = msg.ReadByte(); // 0 = progress (remaining seconds), 1 = final report, 2 = busy   // RUS: 0 = прогресс, 1 = финал, 2 = занято
                if (phase == 0)
                {
                    float remaining = msg.ReadSingle();
                    if (_benchRunning)
                    {
                        SetServerStatus(Loc.Ru ? $"Серверный бенчмарк… осталось ~{remaining:F0}с"
                                                                  : $"Server benchmark… ~{remaining:F0}s left");
                    }
                    return;
                }
                if (phase == 2)
                {
                    _benchRunning = false; // our start was refused — another benchmark is already running   // RUS: наш старт отклонён — другой бенчмарк уже идёт
                    SetServerStatus(Loc.T("Сервер занят: серверный бенчмарк уже идёт (запущен другим админом).",
                                              "Server busy: a server benchmark is already running (started by another admin)."));
                    return;
                }

                string stamp = msg.ReadString() ?? ""; // server's local-time stamp   // RUS: метка локального времени сервера
                float updateRate = msg.ReadSingle();
                float avgMs      = msg.ReadSingle();
                float worstMs    = msg.ReadSingle();
                float slow1pctMs = msg.ReadSingle();
                int items   = msg.ReadUInt16();
                int chars   = msg.ReadUInt16();
                int bodies  = msg.ReadUInt16();
                int clients = msg.ReadByte();
                float grandMs    = msg.ReadSingle();
                float grandCalls = msg.ReadSingle();
                int itemRowCount = msg.ReadByte();
                var itemRows = new System.Collections.Generic.List<string>();
                for (int i = 0; i < itemRowCount; i++) { itemRows.Add(msg.ReadString() ?? ""); }
                int modRowCount = msg.ReadByte();
                var modRows = new System.Collections.Generic.List<string>();
                for (int i = 0; i < modRowCount; i++) { modRows.Add(msg.ReadString() ?? ""); }

                _benchRunning = false;

                // server sim runs at a fixed 60 Hz (16.67 ms budget); tick load = avg tick / budget
                // RUS: серверная симуляция идёт на фикс. 60 Гц (бюджет 16.67 мс); загрузка тика = средн.тик / бюджет
                float load = avgMs / (1000f / 60f) * 100f;
                Color rateCol = updateRate >= 58f ? Color.LightGreen : (updateRate >= 50f ? Color.Yellow : Color.OrangeRed);

                var lines = new System.Collections.Generic.List<(string Text, Color Color)>
                {
                    (Loc.T("===== СЕРВЕРНЫЙ БЕНЧМАРК (60с) =====", "===== SERVER BENCHMARK (60s) ====="), Color.Cyan),
                    (Loc.Ru ? $"Ср. частота апдейтов: {updateRate:F1}/с (цель 60) | загрузка тика: {load:F0}%"
                            : $"Avg update rate: {updateRate:F1}/s (target 60) | tick load: {load:F0}%", rateCol),
                    (Loc.Ru ? $"Средний тик: {avgMs:F2}мс из 16.67 | худший: {worstMs:F2}мс | 1% медленных: {slow1pctMs:F2}мс"
                            : $"Avg tick: {avgMs:F2}ms of 16.67 | worst: {worstMs:F2}ms | 1% slow: {slow1pctMs:F2}ms", Color.LightBlue),
                    (Loc.Ru ? $"Предметы: {items} | Персонажи: {chars} | Физ-тела: {bodies} | Клиентов: {clients}"
                            : $"Items: {items} | Chars: {chars} | Bodies: {bodies} | Clients: {clients}", Color.LightGray),
                    ("", Color.White),
                    (Loc.Ru ? $"ТОП предметов по серверному Item.Update ({grandMs:F0}мс / {grandCalls:N0} вызовов):"
                            : $"TOP items by server Item.Update ({grandMs:F0}ms / {grandCalls:N0} calls):", Color.White),
                };
                foreach (string r in itemRows) { lines.Add((r, Color.LightGray)); }
                lines.Add((Loc.T("По модам (суммарно серверный Item.Update):", "By MOD (total server Item.Update):"), Color.White));
                foreach (string r in modRows) { lines.Add((r, Color.LightGray)); }

                if (updateRate > 0 && updateRate < 50f)
                {
                    lines.Add((Loc.T("ВНИМАНИЕ: сервер не успевает за симуляцией (60/с) — источник лага/откатов.",
                                     "NOTE: the server can't keep up with the sim (60/s) — source of lag/rollbacks."), Color.Orange));
                }

                Benchmark.AddToHistory(ServerHistory, stamp, lines); // store in the server-bench history   // RUS: сохранить в историю серверного бенча
                SetServerStatus(Loc.T("Серверный бенчмарк готов.", "Server benchmark done."));
                ClientPerf.Log(Loc.T("Получен отчёт серверного бенчмарка (см. окно меню).", "Server benchmark report received (see the menu window)."), Color.LightGreen);
            }
            catch { }
        }

        // Ask the server for a server-load snapshot (privileged only; server re-validates).
        // RUS: Запросить у сервера снимок серверной нагрузки (только привилегированным; сервер перепроверяет).
        public static void RequestServerPerf()
        {
            try
            {
                if (GameMain.Client == null) { return; }
                IWriteMessage msg = OptNet.Start(OptFixes.MsgSetServer);
                if (msg == null) { return; }
                msg.WriteByte(OptFixes.RequestPerf);
                OptNet.Send(msg);
            }
            catch { }
        }

        // Ask the server to (re)send its current state.
        // RUS: Попросить сервер (пере)слать текущее состояние.
        public static void RequestServerState()
        {
            try
            {
                if (GameMain.Client == null) { return; }
                IWriteMessage msg = OptNet.Start(OptFixes.MsgSetServer);
                if (msg == null) { return; }
                msg.WriteByte(OptFixes.RequestState);
                OptNet.Send(msg);
            }
            catch { }
        }

        // Ask the server to set a server fix (host/admin only; the server re-validates permission).
        // RUS: Попросить сервер установить серверный фикс (только хост/админ; сервер перепроверяет права).
        public static void RequestSetServer(int fix, int level)
        {
            try
            {
                if (GameMain.Client == null || fix < 0 || fix >= OptFixes.Count) { return; }
                int mx = OptFixes.MaxLevel(fix);
                if (level < 0) { level = 0; } else if (level > mx) { level = mx; }
                IWriteMessage msg = OptNet.Start(OptFixes.MsgSetServer);
                if (msg == null) { return; }
                msg.WriteByte((byte)fix);
                msg.WriteByte((byte)level);
                OptNet.Send(msg);
            }
            catch { }
        }

        // Ask the server to (re)send the current server-side log-check states (sent on menu open / round start).
        // RUS: Попросить сервер (пере)слать текущие состояния серверных проверок логов (при открытии меню / старте раунда).
        public static void RequestLogState()
        {
            try
            {
                if (GameMain.Client == null) { return; }
                IWriteMessage msg = OptNet.Start(LogChecks.MsgSetLog);
                if (msg == null) { return; }
                msg.WriteByte(LogChecks.RequestLogState);
                OptNet.Send(msg);
            }
            catch { }
        }

        // Ask the server to set a server-side log-check (host/admin only; the server re-validates permission).
        // RUS: Попросить сервер установить серверную проверку логов (только хост/админ; сервер перепроверяет права).
        public static void RequestSetLog(int check, bool on)
        {
            try
            {
                if (GameMain.Client == null || check < 0 || check >= LogChecks.Count || !LogChecks.IsServerSide(check)) { return; }
                IWriteMessage msg = OptNet.Start(LogChecks.MsgSetLog);
                if (msg == null) { return; }
                msg.WriteByte((byte)check);
                msg.WriteByte((byte)(on ? 1 : 0));
                OptNet.Send(msg);
            }
            catch { }
        }

        // Toggle a "Console logs" check from the menu: client-side check 0 flips the LOCAL flag immediately;
        // server-side checks go through the server (host/admin only, re-validated server-side).
        // RUS: Переключить проверку «Консольные логи» из меню: клиентская 0 — сразу ЛОКАЛЬНЫЙ флаг; серверные —
        // RUS: через сервер (только хост/админ, перепроверка на сервере).
        public static void ToggleLogCheck(int i)
        {
            if (i == 0) { try { OptConfig.SetAutoLog(!ClientPerf.AutoLog); } catch { } return; } // local flag, persisted   // RUS: локальный флаг, сохраняется
            if (LogChecks.IsServerSide(i)) { RequestSetLog(i, !ServerLogState(i)); }
        }

        // --- client console command ---
        // RUS: --- клиентская консольная команда ---
        private static void RegisterCommand()
        {
            UnregisterCommand();
            DebugConsole.Commands.Add(new DebugConsole.Command(
                "ngopt",
                "NG Logger&Optimizations: ngopt status | ngopt client <fix> <on|off> | ngopt server <fix> <on|off> | ngopt serverperf",
                args =>
                {
                    try
                    {
                        string sub = args != null && args.Length > 0 ? args[0].ToLowerInvariant() : "";
                        if (sub == "" || sub == "status")
                        {
                            for (int i = 0; i < OptFixes.Count; i++)
                            {
                                string srv = _serverKnown[i] ? (_serverState[i] ? Loc.On : Loc.Off) : Loc.Unknown;
                                ClientPerf.Log("  " + OptFixes.ShortName(i) + " — " + Loc.ClientCol + ": " + (OptFixes.GetEnabled(i) ? Loc.On : Loc.Off) + " | " + Loc.ServerCol + ": " + srv, Color.LightBlue);
                            }
                            return;
                        }
                        if (sub == "serverperf")
                        {
                            if (!InMP) { ClientPerf.Log(Loc.ServerMPOnly, Color.Orange); return; }
                            if (!CanControlServer()) { ClientPerf.Log(Loc.T("Серверную нагрузку могут запрашивать только хост/админ.", "Only the host/admin can request server load."), Color.Orange); return; }
                            RequestServerPerf();
                            ClientPerf.Log(Loc.T("Запрос серверной нагрузки отправлен…", "Server-load request sent…"), Color.LightBlue);
                            return;
                        }
                        if (sub == "serverbench")
                        {
                            StartOrCancelServerBench();
                            ClientPerf.Log(Loc.T("Серверный бенчмарк: см. меню/статус.", "Server benchmark: see the menu/status."), Color.LightBlue);
                            return;
                        }
                        int fix = OptFixes.ParseFix(args.Length > 1 ? args[1] : "");
                        bool? on = OptFixes.ParseOnOff(args.Length > 2 ? args[2] : "");
                        if (sub == "client")
                        {
                            if (fix < 0 || !on.HasValue) { ClientPerf.Log(Loc.T("Использование: ngopt client <1|2|3> <on|off>", "Usage: ngopt client <1|2|3> <on|off>"), Color.Orange); return; }
                            OptConfig.SetFixByIndex(fix, on.Value);
                            ClientPerf.Log(OptFixes.ShortName(fix) + " [" + Loc.ClientCol + "]: " + (on.Value ? Loc.On : Loc.Off), on.Value ? Color.LightGreen : Color.Orange);
                            return;
                        }
                        if (sub == "server")
                        {
                            if (fix < 0 || !on.HasValue) { ClientPerf.Log(Loc.T("Использование: ngopt server <1|2|3> <on|off>", "Usage: ngopt server <1|2|3> <on|off>"), Color.Orange); return; }
                            if (!InMP) { ClientPerf.Log(Loc.ServerMPOnly, Color.Orange); return; }
                            if (!CanControlServer()) { ClientPerf.Log(Loc.T("Нет прав менять серверные фиксы (нужен хост/админ).", "No permission to change server fixes (host/admin required)."), Color.Orange); return; }
                            RequestSetServer(fix, on.Value ? 1 : 0);
                            ClientPerf.Log(Loc.T("Запрос отправлен серверу…", "Request sent to the server…"), Color.LightBlue);
                            return;
                        }
                        ClientPerf.Log(Loc.T("Использование: ngopt status | ngopt client <1|2|3> <on|off> | ngopt server <1|2|3> <on|off> | ngopt serverperf",
                                             "Usage: ngopt status | ngopt client <1|2|3> <on|off> | ngopt server <1|2|3> <on|off> | ngopt serverperf"), Color.Orange);
                    }
                    catch (Exception ex) { ClientPerf.Log("ngopt error: " + ex.Message, Color.Red); }
                }));
        }

        private static void UnregisterCommand()
        {
            try
            {
                var existing = DebugConsole.Commands.Find(c => c.Names.Any(n => n.Value.Equals("ngopt", StringComparison.OrdinalIgnoreCase)));
                if (existing != null) { DebugConsole.Commands.Remove(existing); }
            }
            catch { }
        }
    }
}
