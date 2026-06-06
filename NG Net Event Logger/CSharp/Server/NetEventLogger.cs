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
            Log("Initialize() вызван...", Color.Yellow);

            try
            {
                if (_harmony != null)
                {
                    Log("Уже инициализирован.", Color.Orange);
                    return;
                }

                _harmony = new Harmony("com.debug.neteventlogger");
                PatchMethod("Update", null, "Update_Postfix", "Update");
                Log("=== NetEventLogger загружен ===", Color.LightGreen);
            }
            catch (Exception ex)
            {
                Log("ОШИБКА при инициализации: " + ex.Message, Color.Red);
            }
        }

        private void PatchMethod(string methodName, string prefixName, string postfixName, string label)
        {
            try
            {
                var original = typeof(ServerEntityEventManager)
                    .GetMethod(methodName, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

                if (original == null) { Log("Метод " + label + " не найден!", Color.OrangeRed); return; }

                HarmonyMethod prefix  = prefixName  != null ? new HarmonyMethod(typeof(Patches).GetMethod(prefixName,  BindingFlags.Static | BindingFlags.Public)) : null;
                HarmonyMethod postfix = postfixName != null ? new HarmonyMethod(typeof(Patches).GetMethod(postfixName, BindingFlags.Static | BindingFlags.Public)) : null;

                _harmony.Patch(original, prefix: prefix, postfix: postfix);
                Log("Патч " + label + " установлен.", Color.LightGreen);
            }
            catch (Exception ex)
            {
                Log("Ошибка патча " + label + ": " + ex.Message, Color.Red);
            }
        }

        public void OnLoadCompleted() { Log("OnLoadCompleted() — готов.", Color.LightGreen); }

        public void Dispose()
        {
            try { _harmony?.UnpatchSelf(); } catch { }
            _harmony = null;
            if (ReferenceEquals(_activeInstance, this)) _activeInstance = null;
            Log("Выгружен.", Color.Gray);
        }

        internal static List<ServerEntityEvent> GetEvents(object instance)
            => FieldEvents?.GetValue(instance) as List<ServerEntityEvent>;

        internal static ushort GetID(object instance)
            => FieldID != null ? Convert.ToUInt16(FieldID.GetValue(instance)) : (ushort)0;

        internal static ushort GetLastSentToAll(object instance)
            => FieldLastSentToAll != null ? Convert.ToUInt16(FieldLastSentToAll.GetValue(instance)) : (ushort)0;

        internal static void Log(string text, Color color)
        {
            try { DebugConsole.NewMessage("[NetEventLogger] " + text, color); } catch { }
        }
    }

    public static class Patches
    {
        private const double INTERVAL    = 10.0;   // как часто писать статус, сек
        private const int    DANGER_ZONE = 30000;  // span, при котором близко переполнение UInt16

        private static DateTime _lastLog = DateTime.MinValue;
        private static int      _prevID  = -1;
        private static bool     _warnedDanger = false;

        // --- доступ к полям Client через рефлексию (безопасно при любой видимости) ---
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
                return !(v is bool b) || b; // если не смогли определить — считаем, что в игре
            }
            catch { return true; }
        }

        // круговая разница ushort: на сколько ahead впереди behind (0..65535)
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

                // скорость генерации событий (соб/сек) по приросту ID
                double rate = 0;
                if (_prevID >= 0 && elapsed > 0) { rate = Circular(currentID, (ushort)_prevID) / elapsed; }
                _prevID = currentID;

                NetEventLoggerPlugin.Log(
                    "[СТАТУС] очередь=" + count + " | span=" + span + "/65535 | скорость~" + rate.ToString("F0") +
                    " соб/с | ID=" + currentID + " lastSentToAll=" + lastSentToAll,
                    span > 20000 ? Color.Orange : Color.LightBlue);

                // --- отставание по клиентам (кто тормозит очередь) ---
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
                            NetEventLoggerPlugin.Log("  клиент '" + c.Name + "' отстаёт на " + behind + " событий",
                                behind > 10000 ? Color.Red : Color.Orange);
                        }
                    }
                }

                // --- топ источников в очереди ---
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
                        NetEventLoggerPlugin.Log("Топ источников в очереди:", Color.Orange);
                        foreach (var entry in top)
                        {
                            NetEventLoggerPlugin.Log("  " + entry.Value + "x  " + entry.Key, Color.Orange);
                        }
                    }
                }

                // --- авто-вывод вероятной причины ---
                if (worstBehind > 10000)
                {
                    NetEventLoggerPlugin.Log("ВЕРОЯТНО: клиент '" + worstName + "' (отстаёт на " + worstBehind +
                        ") держит очередь -> откаты у всех. Проверь его пинг/соединение/ПК.", Color.Yellow);
                }
                else if (top != null && top.Count > 0 && count > 2000 && (top[0].Value * 100 / Math.Max(count, 1)) >= 40)
                {
                    NetEventLoggerPlugin.Log("ВЕРОЯТНО: источник '" + top[0].Key + "' даёт " +
                        (top[0].Value * 100 / Math.Max(count, 1)) + "% событий — спамит сеть. Чинить этот мод/предмет.", Color.Yellow);
                }

                // --- опасная зона по span ---
                if (span > DANGER_ZONE && !_warnedDanger)
                {
                    _warnedDanger = true;
                    NetEventLoggerPlugin.Log("!!! ОПАСНО: span=" + span +
                        " близко к 32768 — скоро массовые кики/откаты (переполнение UInt16).", Color.Red);
                }
                if (span <= DANGER_ZONE) { _warnedDanger = false; }
            }
            catch (Exception ex)
            {
                NetEventLoggerPlugin.Log("Update_Postfix ошибка: " + ex.Message, Color.Red);
            }
        }
    }
}
