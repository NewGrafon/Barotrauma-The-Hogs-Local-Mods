using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using Barotrauma;
using Barotrauma.Networking;

namespace NetEventLogger
{
    // ==========================================================================================
    //  SERVER-side 60-second benchmark — the server analog of the client menu's "Benchmark 60s".
    //  Triggered by a privileged client; runs entirely on the server: enables the per-item profiler,
    //  measures every server sim tick (via ServerPerfMonitor's GameScreen.Update hook) for 60s, then
    //  builds a report (avg/1%-low server update rate, avg/worst ms-per-tick, entity counts, top items
    //  + by-mod) and sends it back as text to the client that started it. Only one run at a time.
    //  RUS: СЕРВЕРНЫЙ 60-секундный бенчмарк — серверный аналог кнопки «Бенчмарк 60с» в клиентском меню.
    //  RUS: Запускается привилегированным клиентом; целиком на сервере: включает пер-предметный профайлер,
    //  RUS: меряет каждый серверный тик симуляции (через хук ServerPerfMonitor на GameScreen.Update) 60с,
    //  RUS: затем строит отчёт (средн./1%-low частота апдейтов, средн./худший мс-тик, счётчики сущностей,
    //  RUS: топ предметов + по модам) и шлёт текстом клиенту, который запустил. За раз — один прогон.
    // ==========================================================================================
    internal static class ServerBenchmark
    {
        private const int    TopN              = 10;
        private const int    ProgressEveryTicks = 20; // send "X seconds left" to the requester every N ticks   // RUS: слать «осталось X сек» запросившему каждые N тиков

        private static bool              _running;
        private static NetworkConnection _conn;
        private static DateTime          _start;
        private static double            _durationSec = 60.0;
        private static int               _progressCounter;
        private static readonly List<long> _tickTimes = new List<long>(8192);

        public static bool Running => _running;

        // Start the benchmark for the requester. Returns false if one is ALREADY running (server busy) —
        // net handlers run single-threaded on the main thread, so this guard fully serialises concurrent
        // start requests from multiple privileged clients (no data race; the 2nd one just gets "busy").
        // RUS: Запустить бенчмарк для запросившего. Возвращает false, если один УЖЕ идёт (сервер занят) —
        // RUS: сетевые обработчики однопоточны на главном потоке, так что этот guard полностью сериализует
        // RUS: конкурентные запросы от нескольких привилегированных клиентов (без data race; второй получит «занято»).
        public static bool Start(NetworkConnection conn, double durationSec)
        {
            if (_running) { return false; }
            try
            {
                _conn = conn;
                _durationSec = Math.Max(5.0, Math.Min(600.0, durationSec)); // clamp 5s..10min   // RUS: кламп 5с..10мин
                _progressCounter = 0;
                _tickTimes.Clear();
                ServerItemProfiler.Reset();
                ServerItemProfiler.Enable();
                _start = DateTime.UtcNow;
                _running = true;
                return true;
            }
            catch { _running = false; return false; }
        }

        // Tell a client its start request was refused because a benchmark is already running.
        // RUS: Сообщить клиенту, что его запрос отклонён — бенчмарк уже идёт.
        public static void SendBusy(NetworkConnection conn)
        {
            try
            {
                if (conn == null) { return; }
                IWriteMessage msg = OptNet.Start(OptFixes.MsgServerBench);
                if (msg == null) { return; }
                msg.WriteByte(2); // phase 2 = busy   // RUS: фаза 2 = занято
                OptNet.SendTo(msg, conn);
            }
            catch { }
        }

        public static void Cancel()
        {
            _running = false;
            try { ServerItemProfiler.Disable(); } catch { }
        }

        // Per server sim tick (called from ServerPerfMonitor.Postfix with the tick's Stopwatch ticks).
        // RUS: На каждый серверный тик симуляции (зовётся из ServerPerfMonitor.Postfix со Stopwatch-тиками тика).
        public static void Tick(long elapsedTicks)
        {
            if (!_running) { return; }
            try
            {
                if (elapsedTicks > 0) { _tickTimes.Add(elapsedTicks); }
                double elapsed = (DateTime.UtcNow - _start).TotalSeconds;
                if (elapsed >= _durationSec) { Finish(); return; }

                // periodic progress (remaining seconds) to the requester
                // RUS: периодический прогресс (осталось секунд) запросившему
                if (++_progressCounter >= ProgressEveryTicks)
                {
                    _progressCounter = 0;
                    SendProgress((float)Math.Max(0.0, _durationSec - elapsed));
                }
            }
            catch { _running = false; try { ServerItemProfiler.Disable(); } catch { } }
        }

        private static void SendProgress(float remainingSec)
        {
            try
            {
                if (_conn == null) { return; }
                IWriteMessage msg = OptNet.Start(OptFixes.MsgServerBench);
                if (msg == null) { return; }
                msg.WriteByte(0); // phase 0 = progress   // RUS: фаза 0 = прогресс
                msg.WriteSingle(remainingSec);
                OptNet.SendTo(msg, _conn);
            }
            catch { }
        }

        private static void Finish()
        {
            _running = false;
            try
            {
                ServerItemProfiler.Disable();

                double freq = Stopwatch.Frequency;
                double durationSec = Math.Max(0.001, (DateTime.UtcNow - _start).TotalSeconds);
                int count = _tickTimes.Count;

                float updateRate = (float)(count / durationSec);
                float avgMs = 0, worstMs = 0, slow1pctMs = 0;
                if (count > 0)
                {
                    long total = 0, worst = 0;
                    foreach (long t in _tickTimes) { total += t; if (t > worst) { worst = t; } }
                    avgMs   = (float)(total * 1000.0 / freq / count);
                    worstMs = (float)(worst * 1000.0 / freq);
                    // avg ms of the slowest 1% of ticks (a "1% low" expressed as a duration, not a rate)
                    // RUS: средн. мс самых медленных 1% тиков («1% low» как длительность, а не как частота)
                    var slowest = _tickTimes.OrderByDescending(x => x).ToList();
                    int n = Math.Max(1, (int)(count * 0.01));
                    long slowSum = 0;
                    for (int i = 0; i < n; i++) { slowSum += slowest[i]; }
                    slow1pctMs = (float)(slowSum * 1000.0 / freq / n);
                }

                int items   = Safe(() => Item.ItemList.Count);
                int chars   = Safe(() => Character.CharacterList.Count);
                int bodies  = Safe(() => PhysicsBody.List.Count);
                int clients = Safe(() => GameMain.Server?.ConnectedClients.Count ?? 0);

                ServerItemProfiler.BuildData(TopN, out double grandMs, out long grandCalls, out List<string> itemRows, out List<string> modRows);
                string stamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"); // server's LOCAL time   // RUS: ЛОКАЛЬНОЕ время сервера

                // a fresh message per recipient (can't reuse one IWriteMessage across sends)
                // RUS: своё сообщение на каждого получателя (один IWriteMessage переиспользовать нельзя)
                IWriteMessage Build()
                {
                    IWriteMessage m = OptNet.Start(OptFixes.MsgServerBench);
                    if (m == null) { return null; }
                    m.WriteByte(1); // phase 1 = final report   // RUS: фаза 1 = финальный отчёт
                    m.WriteString(stamp);
                    m.WriteSingle(updateRate); m.WriteSingle(avgMs); m.WriteSingle(worstMs); m.WriteSingle(slow1pctMs);
                    m.WriteUInt16((ushort)Math.Min(items, 65535));
                    m.WriteUInt16((ushort)Math.Min(chars, 65535));
                    m.WriteUInt16((ushort)Math.Min(bodies, 65535));
                    m.WriteByte((byte)Math.Min(clients, 255));
                    m.WriteSingle((float)grandMs);
                    m.WriteSingle((float)grandCalls);
                    m.WriteByte((byte)Math.Min(itemRows.Count, 255));
                    foreach (string r in itemRows) { m.WriteString(r); }
                    m.WriteByte((byte)Math.Min(modRows.Count, 255));
                    foreach (string r in modRows) { m.WriteString(r); }
                    return m;
                }

                // broadcast the result to ALL privileged clients so everyone's server-bench history stays in sync
                // RUS: рассылаем результат ВСЕМ привилегированным клиентам, чтобы история серверного бенча была у всех одна
                var server = GameMain.Server;
                if (server != null)
                {
                    foreach (Client c in server.ConnectedClients)
                    {
                        if (!ServerPerfMonitor.IsPrivileged(c)) { continue; }
                        IWriteMessage m = Build();
                        if (m != null) { OptNet.SendTo(m, c.Connection); }
                    }
                }
            }
            catch { }
        }

        private static int Safe(Func<int> f) { try { return f(); } catch { return 0; } }
    }
}
