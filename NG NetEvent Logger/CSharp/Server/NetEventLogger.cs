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
        private static float  _timer        = 0f;
        private const  float  INTERVAL      = 15f;   // статус каждые 15 секунд
        private const  ushort DANGER_ZONE   = 30000; // предупреждение при накоплении > 30000 событий
        private static bool   _warnedDanger = false;

        public static void Update_Postfix(object __instance, List<Client> clients)
        {
            try
            {
                _timer += 0.016f;
                if (_timer < INTERVAL) return;
                _timer = 0f;

                var events = NetEventLoggerPlugin.GetEvents(__instance);
                if (events == null) { NetEventLoggerPlugin.Log("events == null!", Color.Red); return; }

                ushort currentID     = NetEventLoggerPlugin.GetID(__instance);
                ushort lastSentToAll = NetEventLoggerPlugin.GetLastSentToAll(__instance);
                int    count         = events.Count;
                ushort firstID       = count > 0 ? events[0].ID  : (ushort)0;
                ushort lastID        = count > 0 ? events[count - 1].ID : (ushort)0;

                // Расстояние между первым и текущим ID в круговой арифметике
                int span = (int)currentID - (int)firstID;
                if (span < 0) span += 65536;

                // --- Обычный статус ---
                NetEventLoggerPlugin.Log(
                    "[СТАТУС] count=" + count +
                    " ID=" + currentID +
                    " first=" + firstID +
                    " last=" + lastID +
                    " lastSentToAll=" + lastSentToAll +
                    " span=" + span + "/65535",
                    count > 20000 ? Color.Orange : Color.LightBlue
                );

                // --- Предупреждение об опасной зоне ---
                if (span > DANGER_ZONE && !_warnedDanger)
                {
                    _warnedDanger = true;
                    NetEventLoggerPlugin.Log(
                        "!!! ОПАСНО: span=" + span + " приближается к 32768! " +
                        "При достижении 32768 сервер начнёт кикать клиентов из-за UInt16 переполнения. " +
                        "RemoveAll не чистит список — lastSentToAll застрял на " + lastSentToAll,
                        Color.Red
                    );
                }
                if (span <= DANGER_ZONE) _warnedDanger = false;

                // --- Топ сущностей по количеству событий (каждые 15 сек) ---
                if (count > 5000)
                {
                    var top = events
                        .GroupBy(e => e.Entity != null ? e.Entity.GetType().Name + "|" + (e.Entity is MapEntity me ? me.Name ?? "?" : "?") : "null")
                        .Select(g => new { Key = g.Key, Count = g.Count(), Pkg = g.First().Entity?.ContentPackage?.Name ?? "?" })
                        .OrderByDescending(x => x.Count)
                        .Take(5);

                    NetEventLoggerPlugin.Log("Топ источников событий:", Color.Orange);
                    foreach (var entry in top)
                    {
                        NetEventLoggerPlugin.Log(
                            "  " + entry.Count + "x " + entry.Key + " [" + entry.Pkg + "]",
                            Color.Orange
                        );
                    }
                }
            }
            catch (Exception ex)
            {
                NetEventLoggerPlugin.Log("Update_Postfix ошибка: " + ex.Message, Color.Red);
            }
        }
    }
}