using System;
using System.Diagnostics;
using System.Reflection;
using Barotrauma;
using Barotrauma.Networking;
using HarmonyLib;
using Microsoft.Xna.Framework;

namespace NetEventLogger
{
    // ==========================================================================================
    //  SERVER-side frame/tick load monitor. Times the server's per-tick simulation (GameScreen.Update,
    //  the dominant cost) and counts ticks to derive the effective update rate (the engine warns when
    //  this drops below ~60 = "Running slowly"). Periodically (and on request) sends a snapshot of the
    //  server load to clients WITH CONSOLE-COMMAND PERMISSION (host/admins) — never to regular players.
    //  Snapshot = update rate, avg/worst ms-per-tick, and item/character/physics-body/client counts.
    //  Read-only metrics; collection is a cheap Stopwatch around GameScreen.Update.
    //  RUS: СЕРВЕРНЫЙ монитор нагрузки по кадрам/тикам. Замеряет посимуляционное время сервера за тик
    //  RUS: (GameScreen.Update — главная стоимость) и считает тики → эффективная частота апдейтов (движок
    //  RUS: ругается «Running slowly», когда она падает ниже ~60). Периодически (и по запросу) шлёт снимок
    //  RUS: серверной нагрузки клиентам С ПРАВОМ КОНСОЛЬНЫХ КОМАНД (хост/админы) — обычным игрокам никогда.
    //  RUS: Снимок = частота апдейтов, средн./худшее мс-на-тик, кол-во предметов/персонажей/физ-тел/клиентов.
    //  RUS: Метрики только на чтение; сбор = дешёвый Stopwatch вокруг GameScreen.Update.
    // ==========================================================================================
    public sealed class ServerPerfMonitorPlugin : IAssemblyPlugin
    {
        private static Harmony _h;

        public void PreInitPatching() { }

        public void Initialize()
        {
            try
            {
                if (_h != null) { return; }
                _h = new Harmony("ng.serverperfmonitor");
                BindingFlags sp = BindingFlags.Static | BindingFlags.Public;

                MethodInfo upd = AccessTools.Method(typeof(GameScreen), "Update", new[] { typeof(double) });
                if (upd == null)
                {
                    DebugConsole.NewMessage(Loc.Tag + Loc.T("Монитор сервера: GameScreen.Update не найден.", "Server monitor: GameScreen.Update not found."), Color.Orange);
                    return;
                }
                _h.Patch(upd,
                    prefix:  new HarmonyMethod(typeof(ServerPerfMonitor).GetMethod(nameof(ServerPerfMonitor.Prefix),  sp)),
                    postfix: new HarmonyMethod(typeof(ServerPerfMonitor).GetMethod(nameof(ServerPerfMonitor.Postfix), sp)));

                OptNet.Receive(OptFixes.MsgServerPerf, OptNet.NoOp);  // assign id so the server can SEND perf   // RUS: назначить id, чтобы сервер мог ОТПРАВЛЯТЬ нагрузку
                OptNet.Receive(OptFixes.MsgServerBench, OptNet.NoOp); // assign id so the server can SEND bench results   // RUS: назначить id, чтобы сервер мог ОТПРАВЛЯТЬ результаты бенчмарка
                DebugConsole.NewMessage(Loc.Tag + Loc.T("Монитор серверной нагрузки готов.", "Server load monitor ready."), Color.LightGreen);
            }
            catch (Exception ex)
            {
                DebugConsole.NewMessage(Loc.Tag + "ServerPerfMonitor init error: " + ex.Message, Color.Red);
            }
        }

        public void OnLoadCompleted() { }

        public void Dispose() { try { _h?.UnpatchSelf(); } catch { } _h = null; }
    }

    internal static class ServerPerfMonitor
    {
        private const double ReportIntervalSec = 15.0; // how often to push a snapshot to privileged clients   // RUS: как часто слать снимок привилегированным клиентам

        private static long     _tickStart;
        private static int      _winTicks;
        private static long     _winTotalTicks;   // accumulated Stopwatch ticks (time)   // RUS: накопленные тики Stopwatch (время)
        private static long     _winMaxTicks;     // worst single tick   // RUS: худший одиночный тик
        private static DateTime _winStart = DateTime.MinValue;

        public static void Prefix() { _tickStart = Stopwatch.GetTimestamp(); }

        public static void Postfix()
        {
            try
            {
                long el = Stopwatch.GetTimestamp() - _tickStart;
                if (el < 0) { el = 0; }
                ServerBenchmark.Tick(el); // drive the 60s server benchmark, if one is running   // RUS: гоним 60с серверный бенчмарк, если запущен
                if (_winStart == DateTime.MinValue) { _winStart = DateTime.UtcNow; }
                _winTicks++;
                _winTotalTicks += el;
                if (el > _winMaxTicks) { _winMaxTicks = el; }

                double winElapsed = (DateTime.UtcNow - _winStart).TotalSeconds;
                if (winElapsed >= ReportIntervalSec)
                {
                    PushToPrivileged(winElapsed);
                    _winTicks = 0; _winTotalTicks = 0; _winMaxTicks = 0; _winStart = DateTime.UtcNow;
                }
            }
            catch { }
        }

        // Build the perf message from the current window (or instantaneous if the window is empty).
        // RUS: Собрать сообщение нагрузки из текущего окна (или мгновенно, если окно пустое).
        private static IWriteMessage BuildMessage(double winElapsed)
        {
            IWriteMessage msg = OptNet.Start(OptFixes.MsgServerPerf);
            if (msg == null) { return null; }

            double freq      = Stopwatch.Frequency;
            float updateRate = winElapsed > 0 ? (float)(_winTicks / winElapsed) : 0f;
            float avgMs      = _winTicks > 0 ? (float)(_winTotalTicks * 1000.0 / freq / _winTicks) : 0f;
            float maxMs      = (float)(_winMaxTicks * 1000.0 / freq);

            msg.WriteSingle(updateRate);
            msg.WriteSingle(avgMs);
            msg.WriteSingle(maxMs);
            msg.WriteUInt16((ushort)Math.Min(SafeCount(() => Item.ItemList.Count), 65535));
            msg.WriteUInt16((ushort)Math.Min(SafeCount(() => Character.CharacterList.Count), 65535));
            msg.WriteUInt16((ushort)Math.Min(SafeCount(() => PhysicsBody.List.Count), 65535));
            msg.WriteByte((byte)Math.Min(SafeCount(() => GameMain.Server?.ConnectedClients.Count ?? 0), 255));
            return msg;
        }

        private static int SafeCount(Func<int> f) { try { return f(); } catch { return 0; } }

        // Push a snapshot to every client that has console-command permission (host/admins) only.
        // RUS: Разослать снимок только клиентам с правом консольных команд (хост/админы).
        private static void PushToPrivileged(double winElapsed)
        {
            try
            {
                var server = GameMain.Server;
                if (server == null) { return; }
                // a fresh message per recipient (don't reuse one IWriteMessage across multiple sends)
                // RUS: своё сообщение на каждого получателя (не переиспользуем один IWriteMessage на несколько отправок)
                foreach (Client c in server.ConnectedClients)
                {
                    if (!IsPrivileged(c)) { continue; }
                    IWriteMessage msg = BuildMessage(winElapsed);
                    if (msg == null) { return; }
                    OptNet.SendTo(msg, c.Connection);
                }
            }
            catch { }
        }

        // On-demand: send one snapshot to a specific (already permission-checked) connection.
        // RUS: По запросу: отправить один снимок конкретному (уже проверенному на права) соединению.
        public static void SendSnapshotTo(NetworkConnection conn)
        {
            try
            {
                if (conn == null) { return; }
                double winElapsed = _winStart == DateTime.MinValue ? 0 : (DateTime.UtcNow - _winStart).TotalSeconds;
                IWriteMessage msg = BuildMessage(winElapsed);
                if (msg != null) { OptNet.SendTo(msg, conn); }
            }
            catch { }
        }

        public static bool IsPrivileged(Client c)
        {
            try
            {
                if (c == null) { return false; }
                if (GameMain.Server?.OwnerConnection != null && c.Connection == GameMain.Server.OwnerConnection) { return true; }
                return c.HasPermission(ClientPermissions.ConsoleCommands);
            }
            catch { return false; }
        }
    }
}
