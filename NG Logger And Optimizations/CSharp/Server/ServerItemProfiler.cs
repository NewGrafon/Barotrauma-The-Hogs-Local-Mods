using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using Barotrauma;
using HarmonyLib;

namespace NetEventLogger
{
    // ==========================================================================================
    //  SERVER-side per-item profiler — the server analog of the client's `clientperf items`.
    //  Opt-in (patches Item.Update only while enabled -> zero overhead when off). Aggregates total
    //  Item.Update time per prefab (+ owning content package) so the server benchmark can report the
    //  top items/mods eating the server's single-threaded item phase. Item.Update is a SHARED method,
    //  so it runs (and is measurable) on the server exactly as on the client.
    //  RUS: СЕРВЕРНЫЙ пер-предметный профайлер — серверный аналог клиентского `clientperf items`.
    //  RUS: Opt-in (патчит Item.Update только пока включён -> ноль накладных в выключенном состоянии).
    //  RUS: Агрегирует суммарное время Item.Update по префабу (+ контент-пакет), чтобы серверный бенчмарк
    //  RUS: показал топ предметов/модов, жрущих однопоточную предметную фазу сервера. Item.Update — общий
    //  RUS: метод, выполняется (и меряется) на сервере так же, как на клиенте.
    // ==========================================================================================
    internal static class ServerItemProfiler
    {
        private static Harmony _h;
        public static bool Enabled => _h != null;

        private sealed class Stat { public long Ticks; public long Calls; public string Pkg; }
        private static readonly Dictionary<string, Stat> _stats = new Dictionary<string, Stat>();

        public static void Enable()
        {
            if (_h != null) { return; }
            try
            {
                Harmony h = new Harmony("ng.serveritemprofiler");
                BindingFlags sp = BindingFlags.Static | BindingFlags.Public;
                MethodInfo m = AccessTools.Method(typeof(Item), "Update", new[] { typeof(float), typeof(Camera) });
                if (m == null) { return; }
                h.Patch(m,
                    prefix:  new HarmonyMethod(typeof(ServerItemPatch).GetMethod(nameof(ServerItemPatch.Prefix),  sp)),
                    postfix: new HarmonyMethod(typeof(ServerItemPatch).GetMethod(nameof(ServerItemPatch.Postfix), sp)));
                _h = h;
            }
            catch { _h = null; }
        }

        public static void Disable() { try { _h?.UnpatchSelf(); } catch { } _h = null; }
        public static void Reset()   { _stats.Clear(); }

        public static void Record(Item item, long ticks)
        {
            try
            {
                if (item?.Prefab == null) { return; }
                string id = item.Prefab.Identifier.Value;
                if (!_stats.TryGetValue(id, out Stat s))
                {
                    s = new Stat { Pkg = item.Prefab.ContentPackage?.Name ?? "?" };
                    _stats[id] = s;
                }
                s.Ticks += ticks;
                s.Calls++;
            }
            catch { }
        }

        // Produce language-neutral data: grand totals + the top-N item rows + by-mod rows (numbers/names
        // only, no localizable labels). The client adds localized section headers around these rows.
        // RUS: Выдаёт язык-нейтральные данные: общие итоги + строки топ-N предметов + строки по модам (только
        // RUS: числа/имена, без локализуемых подписей). Клиент добавляет локализованные заголовки разделов.
        public static void BuildData(int topN, out double grandMs, out long grandCalls, out List<string> itemRows, out List<string> modRows)
        {
            itemRows = new List<string>();
            modRows = new List<string>();
            grandMs = 0; grandCalls = 0;
            if (_stats.Count == 0) { return; }

            double freq = Stopwatch.Frequency;
            long grandTicks = _stats.Values.Sum(x => x.Ticks);
            grandCalls = _stats.Values.Sum(x => x.Calls);
            grandMs = grandTicks * 1000.0 / freq;

            int i = 1;
            foreach (var kvp in _stats.OrderByDescending(k => k.Value.Ticks).Take(topN))
            {
                Stat s = kvp.Value;
                double ms  = s.Ticks * 1000.0 / freq;
                double us  = s.Calls > 0 ? s.Ticks * 1000000.0 / freq / s.Calls : 0;
                double pct = grandTicks > 0 ? s.Ticks * 100.0 / grandTicks : 0;
                itemRows.Add($"  {i,2}. {Pad(kvp.Key, 26)} {ms,8:F1}ms {pct,5:F1}% {us,7:F2}µs [{s.Pkg}]");
                i++;
            }
            foreach (var mod in _stats.GroupBy(k => k.Value.Pkg)
                                      .Select(g => new { Pkg = g.Key, Ticks = g.Sum(x => x.Value.Ticks) })
                                      .OrderByDescending(x => x.Ticks).Take(5))
            {
                double ms  = mod.Ticks * 1000.0 / freq;
                double pct = grandTicks > 0 ? mod.Ticks * 100.0 / grandTicks : 0;
                modRows.Add($"   {Pad(mod.Pkg, 30)} {ms,8:F1}ms {pct,5:F1}%");
            }
        }

        private static string Pad(string s, int len) => s.Length >= len ? s : s + new string(' ', len - s.Length);
    }

    public static class ServerItemPatch
    {
        public static void Prefix(out long __state) { __state = Stopwatch.GetTimestamp(); }

        public static void Postfix(Item __instance, long __state)
        {
            if (__state <= 0) { return; }
            ServerItemProfiler.Record(__instance, Stopwatch.GetTimestamp() - __state);
        }
    }
}
