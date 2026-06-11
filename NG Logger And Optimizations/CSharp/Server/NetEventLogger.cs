using System;
using System.Reflection;
using System.Collections.Generic;
using System.Linq;
using Barotrauma;
using Barotrauma.Networking;
using HarmonyLib;
using Microsoft.Xna.Framework;

namespace NetEventLogger
{
    // ==========================================================================================
    //  SERVER-side diagnostic: periodically logs the entity-event queue health (size/span/rate,
    //  per-client backlog, top event sources, probable cause). Helps find progressive net lag.
    //  RUS: СЕРВЕРНАЯ диагностика: периодически пишет здоровье очереди entity-событий (размер/span/
    //  RUS: скорость, отставание по клиентам, топ источников, вероятная причина). Ищет прогрессирующий лаг.
    // ==========================================================================================
    public sealed class NetEventLoggerPlugin : IAssemblyPlugin
    {
        private static Harmony _harmony;
        private static NetEventLoggerPlugin _activeInstance;

        private static readonly FieldInfo FieldEvents =
            typeof(ServerEntityEventManager).GetField("events",
                BindingFlags.NonPublic | BindingFlags.Instance);

        private static readonly FieldInfo FieldUniqueEvents =
            typeof(ServerEntityEventManager).GetField("uniqueEvents",
                BindingFlags.NonPublic | BindingFlags.Instance);

        private static readonly FieldInfo FieldID =
            typeof(ServerEntityEventManager).GetField("ID",
                BindingFlags.NonPublic | BindingFlags.Instance);

        private static readonly FieldInfo FieldLastSentToAll =
            typeof(ServerEntityEventManager).GetField("lastSentToAll",
                BindingFlags.NonPublic | BindingFlags.Instance);

        public void PreInitPatching() { }

        public void Initialize()
        {
            _activeInstance = this;
            Log(Loc.T("Initialize() вызван…", "Initialize() called…"), Color.Yellow);

            try
            {
                if (_harmony != null)
                {
                    Log(Loc.T("Уже инициализирован.", "Already initialized."), Color.Orange);
                    return;
                }

                _harmony = new Harmony("com.debug.neteventlogger");
                PatchMethod("Update", null, "Update_Postfix", "Update");
                Log(Loc.T("=== Логгер событий загружен ===", "=== Event logger loaded ==="), Color.LightGreen);
            }
            catch (Exception ex)
            {
                Log(Loc.T("ОШИБКА при инициализации: ", "Init ERROR: ") + ex.Message, Color.Red);
            }
        }

        private void PatchMethod(string methodName, string prefixName, string postfixName, string label)
        {
            try
            {
                var original = typeof(ServerEntityEventManager)
                    .GetMethod(methodName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

                if (original == null) { Log(Loc.T($"Метод {label} не найден!", $"Method {label} not found!"), Color.OrangeRed); return; }

                HarmonyMethod prefix  = prefixName  != null ? new HarmonyMethod(typeof(Patches).GetMethod(prefixName,  BindingFlags.Static | BindingFlags.Public)) : null;
                HarmonyMethod postfix = postfixName != null ? new HarmonyMethod(typeof(Patches).GetMethod(postfixName, BindingFlags.Static | BindingFlags.Public)) : null;

                _harmony.Patch(original, prefix: prefix, postfix: postfix);
                Log(Loc.T($"Патч {label} установлен.", $"Patch {label} installed."), Color.LightGreen);
            }
            catch (Exception ex)
            {
                Log(Loc.T($"Ошибка патча {label}: ", $"Patch error {label}: ") + ex.Message, Color.Red);
            }
        }

        public void OnLoadCompleted() { Log(Loc.T("OnLoadCompleted() — готов.", "OnLoadCompleted() — ready."), Color.LightGreen); }

        public void Dispose()
        {
            try { _harmony?.UnpatchSelf(); } catch { }
            _harmony = null;
            if (ReferenceEquals(_activeInstance, this)) _activeInstance = null;
            Log(Loc.T("Выгружен.", "Unloaded."), Color.Gray);
        }

        internal static List<ServerEntityEvent> GetEvents(object instance)
            => FieldEvents?.GetValue(instance) as List<ServerEntityEvent>;

        internal static ushort GetID(object instance)
            => FieldID != null ? Convert.ToUInt16(FieldID.GetValue(instance)) : (ushort)0;

        internal static ushort GetLastSentToAll(object instance)
            => FieldLastSentToAll != null ? Convert.ToUInt16(FieldLastSentToAll.GetValue(instance)) : (ushort)0;

        internal static void Log(string text, Color color)
        {
            string line = Loc.Tag + text;

            // 1) The server process's own console. A standalone DedicatedServer.exe shows this in its
            //    console window (stdout) immediately.
            //    RUS: Консоль самого серверного процесса. Отдельный DedicatedServer.exe пишет это в своё
            //    RUS: консольное окно (stdout) — видно сразу.
            try { DebugConsole.NewMessage(line, color); } catch { }

            // 2) When hosting "through the game", the server runs as a SEPARATE child process whose stdout
            //    is NOT shown in the host client's in-game console. So we also push the line straight to the
            //    server owner's (host's) console via the server channel.
            //    RUS: При хосте «через игру» сервер запускается ОТДЕЛЬНЫМ дочерним процессом, и его stdout
            //    RUS: не виден во внутриигровой консоли клиента-хоста. Поэтому дублируем сообщение прямо в
            //    RUS: консоль владельца сервера (хоста) через серверный канал.
            try
            {
                var server = GameMain.Server;
                if (server != null && server.OwnerConnection != null)
                {
                    foreach (var c in server.ConnectedClients)
                    {
                        if (c != null && c.Connection == server.OwnerConnection)
                        {
                            server.SendConsoleMessage(line, c, color);
                            break;
                        }
                    }
                }
            }
            catch { }
        }
    }

    public static class Patches
    {
        private const double INTERVAL    = 10.0;   // how often to print status, seconds   // RUS: как часто писать статус, сек
        private const int    DANGER_ZONE = 30000;  // span where UInt16 overflow is near    // RUS: span, при котором близко переполнение UInt16

        private static DateTime _lastLog = DateTime.MinValue;
        private static int      _prevID  = -1;
        private static bool     _warnedDanger = false;

        // --- access to Client fields via reflection (safe regardless of visibility) ---
        // RUS: --- доступ к полям Client через рефлексию (безопасно при любой видимости) ---
        private static bool _clientResolved = false;
        private static PropertyInfo _piLastRecv; private static FieldInfo _fiLastRecv;
        private static PropertyInfo _piInGame;   private static FieldInfo _fiInGame;

        private static void ResolveClientAccessors()
        {
            if (_clientResolved) { return; }
            _clientResolved = true;
            try
            {
                _piLastRecv = typeof(Client).GetProperty("LastRecvEntityEventID");
                if (_piLastRecv == null) { _fiLastRecv = typeof(Client).GetField("LastRecvEntityEventID"); }
                _piInGame = typeof(Client).GetProperty("InGame");
                if (_piInGame == null) { _fiInGame = typeof(Client).GetField("InGame"); }
            }
            catch { }
        }

        private static ushort GetLastRecv(Client c)
        {
            try
            {
                object v = _piLastRecv != null ? _piLastRecv.GetValue(c) : _fiLastRecv?.GetValue(c);
                return v != null ? Convert.ToUInt16(v) : (ushort)0;
            }
            catch { return 0; }
        }

        private static bool GetInGame(Client c)
        {
            try
            {
                object v = _piInGame != null ? _piInGame.GetValue(c) : _fiInGame?.GetValue(c);
                return !(v is bool b) || b; // if undeterminable — assume in-game   // RUS: если не смогли определить — считаем, что в игре
            }
            catch { return true; }
        }

        // circular ushort difference: how far 'ahead' leads 'behind' (0..65535)
        // RUS: круговая разница ushort: на сколько 'ahead' впереди 'behind' (0..65535)
        private static int Circular(ushort ahead, ushort behind)
        {
            int d = (int)ahead - (int)behind;
            if (d < 0) { d += 65536; }
            return d;
        }

        private static string SourceLabel(object entity)
        {
            try
            {
                if (entity == null) { return "null"; }
                if (entity is Item it && it.Prefab != null)
                {
                    string pkg = it.Prefab.ContentPackage != null ? it.Prefab.ContentPackage.Name : "?";
                    return "Item:" + it.Prefab.Identifier + " [" + pkg + "]";
                }
                if (entity is Character ch) { return "Character:" + ch.SpeciesName; }
                if (entity is MapEntity me) { return entity.GetType().Name + ":" + (me.Name ?? "?"); }
                return entity.GetType().Name;
            }
            catch { return "?"; }
        }

        public static void Update_Postfix(object __instance, List<Client> clients)
        {
            try
            {
                var now = DateTime.UtcNow;
                if (_lastLog != DateTime.MinValue && (now - _lastLog).TotalSeconds < INTERVAL) { return; }
                double elapsed = _lastLog == DateTime.MinValue ? INTERVAL : (now - _lastLog).TotalSeconds;
                _lastLog = now;

                ResolveClientAccessors();

                var events = NetEventLoggerPlugin.GetEvents(__instance);
                if (events == null) { NetEventLoggerPlugin.Log("events == null!", Color.Red); return; }

                ushort currentID     = NetEventLoggerPlugin.GetID(__instance);
                ushort lastSentToAll = NetEventLoggerPlugin.GetLastSentToAll(__instance);
                int    count         = events.Count;
                ushort firstID       = count > 0 ? events[0].ID : currentID;
                int    span          = Circular(currentID, firstID);

                // event generation rate (events/sec) from the ID increment
                // RUS: скорость генерации событий (соб/сек) по приросту ID
                double rate = 0;
                if (_prevID >= 0 && elapsed > 0) { rate = Circular(currentID, (ushort)_prevID) / elapsed; }
                _prevID = currentID;

                NetEventLoggerPlugin.Log(
                    Loc.Ru
                        ? "[СТАТУС] очередь=" + count + " | span=" + span + "/65535 | скорость~" + rate.ToString("F0") + " соб/с | ID=" + currentID + " lastSentToAll=" + lastSentToAll
                        : "[STATUS] queue=" + count + " | span=" + span + "/65535 | rate~" + rate.ToString("F0") + " ev/s | ID=" + currentID + " lastSentToAll=" + lastSentToAll,
                    span > 20000 ? Color.Orange : Color.LightBlue);

                // --- per-client backlog (who's holding the queue back) ---
                // RUS: --- отставание по клиентам (кто тормозит очередь) ---
                string worstName = null; int worstBehind = -1;
                if (clients != null)
                {
                    foreach (var c in clients)
                    {
                        if (c == null || !GetInGame(c)) { continue; }
                        int behind = Circular(currentID, GetLastRecv(c));
                        if (behind > worstBehind) { worstBehind = behind; worstName = c.Name; }
                        if (behind > 1000)
                        {
                            NetEventLoggerPlugin.Log(Loc.Ru ? $"  клиент '{c.Name}' отстаёт на {behind} событий" : $"  client '{c.Name}' is behind by {behind} events",
                                behind > 10000 ? Color.Red : Color.Orange);
                        }
                    }
                }

                // --- top sources in the queue ---
                // RUS: --- топ источников в очереди ---
                List<KeyValuePair<string, int>> top = null;
                if (count > 0)
                {
                    top = events
                        .Where(e => e != null && e.Entity != null)
                        .GroupBy(e => SourceLabel(e.Entity))
                        .Select(g => new KeyValuePair<string, int>(g.Key, g.Count()))
                        .OrderByDescending(x => x.Value)
                        .Take(6)
                        .ToList();

                    if (count > 2000 || span > 10000)
                    {
                        NetEventLoggerPlugin.Log(Loc.T("Топ источников в очереди:", "Top sources in queue:"), Color.Orange);
                        foreach (var entry in top)
                        {
                            NetEventLoggerPlugin.Log("  " + entry.Value + "x  " + entry.Key, Color.Orange);
                        }
                    }
                }

                // --- auto probable-cause line ---
                // RUS: --- авто-вывод вероятной причины ---
                if (worstBehind > 10000)
                {
                    NetEventLoggerPlugin.Log(Loc.Ru
                        ? $"ВЕРОЯТНО: клиент '{worstName}' (отстаёт на {worstBehind}) держит очередь -> откаты у всех. Проверь его пинг/соединение/ПК."
                        : $"LIKELY: client '{worstName}' (behind by {worstBehind}) holds the queue -> rollbacks for everyone. Check their ping/connection/PC.", Color.Yellow);
                }
                else if (top != null && top.Count > 0 && count > 2000 && (top[0].Value * 100 / Math.Max(count, 1)) >= 40)
                {
                    int pct = top[0].Value * 100 / Math.Max(count, 1);
                    NetEventLoggerPlugin.Log(Loc.Ru
                        ? $"ВЕРОЯТНО: источник '{top[0].Key}' даёт {pct}% событий — спамит сеть. Чинить этот мод/предмет."
                        : $"LIKELY: source '{top[0].Key}' produces {pct}% of events — spamming the network. Fix that mod/item.", Color.Yellow);
                }

                // --- span danger zone ---
                // RUS: --- опасная зона по span ---
                if (span > DANGER_ZONE && !_warnedDanger)
                {
                    _warnedDanger = true;
                    NetEventLoggerPlugin.Log(Loc.Ru
                        ? $"!!! ОПАСНО: span={span} близко к 32768 — скоро массовые кики/откаты (переполнение UInt16)."
                        : $"!!! DANGER: span={span} is near 32768 — mass kicks/rollbacks soon (UInt16 overflow).", Color.Red);
                }
                if (span <= DANGER_ZONE) { _warnedDanger = false; }
            }
            catch (Exception ex)
            {
                NetEventLoggerPlugin.Log("Update_Postfix error: " + ex.Message, Color.Red);
            }
        }
    }
}
