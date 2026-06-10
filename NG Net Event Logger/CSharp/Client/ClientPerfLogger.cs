using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using Barotrauma;
using HarmonyLib;
using Microsoft.Xna.Framework;

namespace NetEventLogger
{
    // ==========================================================================================
    //  КЛИЕНТСКИЙ профайлер ФПС (грузится у КАЖДОГО участника: хост + все клиенты).
    //  Источник данных — встроенный Barotrauma.GameMain.PerformanceCounter:
    //    * AverageFramesPerSecond — средний FPS;
    //    * GetSavedIdentifiers / GetAverageElapsedMillisecs(id) — стоимость подсистем кадра
    //      (Update:Character/Particles/Physics/StatusEffects/Ragdolls/MapEntity:Items/Power…,
    //       Draw:Map:Lighting/FrontParticles/BackCharactersItems/HUD/PostProcess…).
    //  Раз в 60с (игрового времени в раунде) пишет в консоль средний FPS + топ-10 операций
    //  и копит каждое окно в памяти ВСЕЙ СЕССИИ. Команда `clientperf` строит портрет сессии.
    // ==========================================================================================
    public sealed class ClientPerfLoggerPlugin : IAssemblyPlugin
    {
        private static Harmony _harmony;

        public void PreInitPatching() { }

        public void Initialize()
        {
            ClientPerf.Log("Инициализация клиентского профайлера…", Color.Yellow);
            try
            {
                if (_harmony == null)
                {
                    _harmony = new Harmony("com.ng.clientperflogger");
                    // Покадровый тик: постфикс на клиентский GameMain.Update(GameTime).
                    MethodInfo update = AccessTools.Method(typeof(GameMain), "Update", new[] { typeof(GameTime) });
                    if (update != null)
                    {
                        _harmony.Patch(update, postfix: new HarmonyMethod(typeof(ClientPerfPatch).GetMethod(
                            nameof(ClientPerfPatch.Update_Postfix), BindingFlags.Static | BindingFlags.Public)));
                    }
                    else
                    {
                        ClientPerf.Log("НЕ найден GameMain.Update — профайлер не сможет тикать!", Color.Red);
                    }
                }

                ClientPerf.RegisterCommands();
                ClientPerf.Log("=== Клиентский профайлер загружен. Команда: clientperf (см. clientperf help) ===", Color.LightGreen);
            }
            catch (Exception ex)
            {
                ClientPerf.Log("ОШИБКА инициализации: " + ex, Color.Red);
            }
        }

        public void OnLoadCompleted() { }

        public void Dispose()
        {
            try { _harmony?.UnpatchSelf(); } catch { }
            _harmony = null;
            try { ItemProfiler.Disable(); } catch { }
            try { ClientPerf.UnregisterCommands(); } catch { }
        }
    }

    public static class ClientPerf
    {
        // --- настройки ---
        private const double SampleIntervalSec = 1.0;   // как часто снимать пробу (раз в секунду)
        private const double WindowSec         = 60.0;  // длина окна
        private const int    TopWindow         = 10;    // топ операций в авто-выводе окна
        private const int    TopSession        = 20;    // топ операций в отчёте сессии
        private const int    MaxWindows        = 20000; // страховка от безграничного роста памяти

        public static bool AutoLog = true;              // печатать сводку каждые 60с

        // --- часы и накопители текущего окна ---
        private static readonly Stopwatch _clock = Stopwatch.StartNew();
        private static double _lastTickSec = 0;
        private static double _sampleElapsed = 0;
        private static double _windowElapsed = 0;

        private static readonly Dictionary<string, double> _accMs = new Dictionary<string, double>();
        private static int    _sampleCount = 0;
        private static double _fpsSum = 0;
        private static double _fpsMin = double.MaxValue;
        private static int    _windowIndex = 0;

        // --- память сессии ---
        public sealed class Window
        {
            public int    Index;
            public double AvgFps;
            public double MinFps;
            public double UpdateMs;
            public double DrawMs;
            public int    Samples;
            public Dictionary<string, double> OpMs; // средние мс/кадр по каждому идентификатору
        }
        private static readonly List<Window> _session = new List<Window>();

        // ----------------------------------------------------------------------------------
        //  Покадровый тик (зовётся из Harmony-постфикса GameMain.Update).
        // ----------------------------------------------------------------------------------
        public static void Tick()
        {
            try
            {
                double now = _clock.Elapsed.TotalSeconds;
                double delta = now - _lastTickSec;
                _lastTickSec = now;
                // игнорируем первый кадр и большие провалы (загрузка/пауза/сворачивание окна)
                if (delta <= 0 || delta > 1.0) { return; }

                // считаем только пока идёт раунд (на игровом экране)
                if (GameMain.GameScreen == null || Screen.Selected != GameMain.GameScreen) { return; }
                PerformanceCounter pc = GameMain.PerformanceCounter;
                if (pc == null) { return; }

                _sampleElapsed += delta;
                _windowElapsed += delta;

                if (_sampleElapsed >= SampleIntervalSec) { _sampleElapsed = 0; Sample(pc); }
                if (_windowElapsed >= WindowSec)          { _windowElapsed = 0; CloseWindow(); }
            }
            catch { }
        }

        private static void Sample(PerformanceCounter pc)
        {
            _sampleCount++;

            double fps = pc.AverageFramesPerSecond;
            _fpsSum += fps;
            if (fps < _fpsMin) { _fpsMin = fps; }

            foreach (string id in pc.GetSavedIdentifiers)
            {
                double ms = pc.GetAverageElapsedMillisecs(id);
                _accMs[id] = (_accMs.TryGetValue(id, out double prev) ? prev : 0) + ms;
            }
        }

        private static void CloseWindow()
        {
            if (_sampleCount <= 0) { ResetWindow(); return; }

            var op = new Dictionary<string, double>(_accMs.Count);
            foreach (var kvp in _accMs) { op[kvp.Key] = kvp.Value / _sampleCount; }

            var w = new Window
            {
                Index    = ++_windowIndex,
                AvgFps   = _fpsSum / _sampleCount,
                MinFps   = _fpsMin == double.MaxValue ? 0 : _fpsMin,
                UpdateMs = op.TryGetValue("Update", out double u) ? u : 0,
                DrawMs   = op.TryGetValue("Draw", out double d) ? d : 0,
                Samples  = _sampleCount,
                OpMs     = op
            };
            _session.Add(w);
            if (_session.Count > MaxWindows) { _session.RemoveAt(0); }

            if (AutoLog) { PrintWindow(w); }
            ResetWindow();
        }

        private static void ResetWindow()
        {
            _accMs.Clear();
            _sampleCount = 0;
            _fpsSum = 0;
            _fpsMin = double.MaxValue;
        }

        // ----------------------------------------------------------------------------------
        //  Топ ЛИСТОВЫХ операций (исключаем родителей-агрегаты, чтобы не было двойного учёта:
        //  "Update" и "Draw" — это суммы своих под-веток, их показываем отдельной строкой-итогом).
        // ----------------------------------------------------------------------------------
        private static List<KeyValuePair<string, double>> TopLeafOps(Dictionary<string, double> op, int n)
        {
            var keys = op.Keys.ToList();
            bool IsLeaf(string k) => !keys.Any(o => o.Length > k.Length && o.StartsWith(k + ":", StringComparison.Ordinal));
            return op.Where(kvp => IsLeaf(kvp.Key) && kvp.Value > 0.0001)
                     .OrderByDescending(kvp => kvp.Value)
                     .Take(n)
                     .ToList();
        }

        private static void PrintWindow(Window w)
        {
            Log($"=== Окно #{w.Index} (60с) === средний FPS {w.AvgFps:F1} (мин {w.MinFps:F0}) | Update {w.UpdateMs:F2}мс + Draw {w.DrawMs:F2}мс", FpsColor(w.AvgFps));
            var top = TopLeafOps(w.OpMs, TopWindow);
            Log($"  Топ-{top.Count} тяжёлых операций (средн. мс/кадр):", Color.LightGray);
            int i = 1;
            foreach (var kvp in top) { Log($"    {i,2}. {Pad(kvp.Key, 34)} {kvp.Value,7:F3} мс", MsColor(kvp.Value)); i++; }
        }

        // ----------------------------------------------------------------------------------
        //  Отчёт по всей сессии: топ-20 за сессию + 3 снапшота (худший/типичный/лучший FPS).
        // ----------------------------------------------------------------------------------
        public static void PrintSessionReport()
        {
            // если текущее окно ещё не закрылось, но в нём уже есть данные — учтём его «на лету»
            FlushPartialWindowIfAny();

            if (_session.Count == 0)
            {
                Log("Данных пока нет: ни одно 60с-окно не завершилось в раунде. Поиграй немного и повтори.", Color.Orange);
                return;
            }

            double sessFpsMean = _session.Average(x => x.AvgFps);
            double sessFpsLow  = _session.Min(x => x.MinFps);
            Log($"===== ПОРТРЕТ ПРОИЗВОДИТЕЛЬНОСТИ СЕССИИ: {_session.Count} окон (~{_session.Count} мин в раундах) =====", Color.Cyan);
            Log($"   Средний FPS по сессии: {sessFpsMean:F1} | худшая секунда: {sessFpsLow:F0} FPS", Color.Cyan);

            // --- агрегированный топ операций за всю сессию (среднее от оконных средних) ---
            var sum = new Dictionary<string, double>();
            var cnt = new Dictionary<string, int>();
            foreach (var w in _session)
            {
                foreach (var kvp in w.OpMs)
                {
                    sum[kvp.Key] = (sum.TryGetValue(kvp.Key, out double s) ? s : 0) + kvp.Value;
                    cnt[kvp.Key] = (cnt.TryGetValue(kvp.Key, out int c) ? c : 0) + 1;
                }
            }
            var avg = sum.ToDictionary(k => k.Key, k => k.Value / Math.Max(1, cnt[k.Key]));
            var topSess = TopLeafOps(avg, TopSession);

            Log($"--- Топ-{topSess.Count} тяжёлых операций за СЕССИЮ (средн. мс/кадр) ---", Color.White);
            int i = 1;
            foreach (var kvp in topSess) { Log($"  {i,2}. {Pad(kvp.Key, 34)} {kvp.Value,7:F3} мс", MsColor(kvp.Value)); i++; }

            // --- 3 характерных снапшота ---
            var byFps = _session.OrderBy(x => x.AvgFps).ToList();
            Window worst  = byFps.First();
            Window best   = byFps.Last();
            // «самое среднее среднее»: окно с avgFps ближе всего к среднему по сессии
            Window typical = _session.OrderBy(x => Math.Abs(x.AvgFps - sessFpsMean)).First();

            PrintSnapshot("ХУДШИЙ FPS (тут искать виновника)", worst);
            PrintSnapshot("ТИПИЧНЫЙ FPS (среднее по сессии)", typical);
            PrintSnapshot("ЛУЧШИЙ FPS (для сравнения)", best);

            Log("Подсказка: если в «худшем» окне резко выделяется Draw:Map:Lighting/FrontParticles — рендер (свет/частицы);", Color.Gray);
            Log("           Update:StatusEffects/Character/MapEntity:Items — скрипты/моды/сущности; Update:Physics — физика/сабы.", Color.Gray);
        }

        private static void PrintSnapshot(string label, Window w)
        {
            Log($"--- Снапшот: {label} — окно #{w.Index}: средний {w.AvgFps:F1} FPS (мин {w.MinFps:F0}) | Update {w.UpdateMs:F2}мс + Draw {w.DrawMs:F2}мс ---", FpsColor(w.AvgFps));
            var top = TopLeafOps(w.OpMs, TopWindow);
            int i = 1;
            foreach (var kvp in top) { Log($"       {i,2}. {Pad(kvp.Key, 34)} {kvp.Value,7:F3} мс", MsColor(kvp.Value)); i++; }
        }

        // снять снимок текущего (ещё не закрытого) окна, не очищая накопители, и временно добавить в сессию
        private static void FlushPartialWindowIfAny()
        {
            if (_sampleCount <= 0) { return; }
            var op = new Dictionary<string, double>(_accMs.Count);
            foreach (var kvp in _accMs) { op[kvp.Key] = kvp.Value / _sampleCount; }
            _session.Add(new Window
            {
                Index    = _windowIndex + 1,
                AvgFps   = _fpsSum / _sampleCount,
                MinFps   = _fpsMin == double.MaxValue ? 0 : _fpsMin,
                UpdateMs = op.TryGetValue("Update", out double u) ? u : 0,
                DrawMs   = op.TryGetValue("Draw", out double d) ? d : 0,
                Samples  = _sampleCount,
                OpMs     = op
            });
            // помечаем, что текущее окно уже учтено: закрываем его, чтобы не задвоить при следующем закрытии
            _windowElapsed = 0;
            ResetWindow();
        }

        public static void PrintCurrentWindow()
        {
            if (_sampleCount <= 0) { Log("Текущее окно пустое (не в раунде или только началось).", Color.Orange); return; }
            var op = new Dictionary<string, double>(_accMs.Count);
            foreach (var kvp in _accMs) { op[kvp.Key] = kvp.Value / _sampleCount; }
            var w = new Window
            {
                Index    = _windowIndex + 1,
                AvgFps   = _fpsSum / _sampleCount,
                MinFps   = _fpsMin == double.MaxValue ? 0 : _fpsMin,
                UpdateMs = op.TryGetValue("Update", out double u) ? u : 0,
                DrawMs   = op.TryGetValue("Draw", out double d) ? d : 0,
                Samples  = _sampleCount,
                OpMs     = op
            };
            Log($"=== ТЕКУЩЕЕ окно ({_sampleCount}с накоплено) ===", FpsColor(w.AvgFps));
            PrintSnapshot("текущее", w);
        }

        public static void ResetSession()
        {
            _session.Clear();
            _windowIndex = 0;
            ResetWindow();
            _windowElapsed = 0;
            ItemProfiler.ResetStats();
            Log("Память сессии и пер-предметный замер очищены.", Color.Yellow);
        }

        // ----------------------------------------------------------------------------------
        //  Консольная команда
        // ----------------------------------------------------------------------------------
        public static void RegisterCommands()
        {
            UnregisterCommands();
            DebugConsole.Commands.Add(new DebugConsole.Command(
                "clientperf|ngperf",
                "NG клиентский профайлер ФПС. Без аргументов — отчёт сессии. Аргументы: now | items | reset | auto | help",
                args =>
                {
                    string a = args != null && args.Length > 0 ? args[0].ToLowerInvariant() : "";
                    switch (a)
                    {
                        case "now":   PrintCurrentWindow(); break;
                        case "reset": ResetSession(); break;
                        case "auto":
                            AutoLog = !AutoLog;
                            Log("Авто-вывод каждые 60с: " + (AutoLog ? "ВКЛ" : "ВЫКЛ"), AutoLog ? Color.LightGreen : Color.Gray);
                            break;
                        case "items":
                        {
                            string sub = args != null && args.Length > 1 ? args[1].ToLowerInvariant() : "";
                            if (sub == "on")         { ItemProfiler.Enable();  Log("Пер-предметный замер ВКЛ — накапливаю. Поиграй, потом: clientperf items", Color.LightGreen); }
                            else if (sub == "off")   { ItemProfiler.Disable(); Log("Пер-предметный замер ВЫКЛ (патч снят, накладных нет).", Color.Gray); }
                            else if (sub == "reset") { ItemProfiler.ResetStats(); Log("Пер-предметный накопитель очищен.", Color.Yellow); }
                            else                     { ItemProfiler.Report(20); }
                            break;
                        }
                        case "help":
                            Log("clientperf          — портрет сессии: топ-20 операций + 3 снапшота (худший/типичный/лучший FPS)", Color.White);
                            Log("clientperf now      — топ операций за текущее (ещё не закрытое) окно", Color.White);
                            Log("clientperf items    — топ ПРЕДМЕТОВ/МОДОВ по времени Item.Update (сначала: clientperf items on)", Color.White);
                            Log("clientperf items on|off|reset — вкл/выкл/очистить пер-предметный замер", Color.White);
                            Log("clientperf reset    — очистить память сессии (и предметов)", Color.White);
                            Log("clientperf auto     — вкл/выкл авто-вывод сводки каждые 60с", Color.White);
                            break;
                        default: PrintSessionReport(); break;
                    }
                }));
        }

        public static void UnregisterCommands()
        {
            var existing = DebugConsole.Commands.Find(c => c.Names.Any(n => n.Value.Equals("clientperf", StringComparison.OrdinalIgnoreCase)));
            if (existing != null) { DebugConsole.Commands.Remove(existing); }
        }

        // ----------------------------------------------------------------------------------
        //  Утилиты
        // ----------------------------------------------------------------------------------
        internal static string Pad(string s, int len) => s.Length >= len ? s : s + new string(' ', len - s.Length);

        private static Color FpsColor(double fps) => fps >= 50 ? Color.LightGreen : (fps >= 30 ? Color.Yellow : Color.OrangeRed);
        private static Color MsColor(double ms)   => ms >= 3.0 ? Color.OrangeRed : (ms >= 1.0 ? Color.Orange : Color.LightGray);

        public static void Log(string text, Color color)
        {
            try { DebugConsole.NewMessage("[ClientPerf] " + text, color); } catch { }
        }
    }

    public static class ClientPerfPatch
    {
        public static void Update_Postfix() => ClientPerf.Tick();
    }

    // ==========================================================================================
    //  Пер-предметный замер: кто из предметов/модов ест время в Item.Update.
    //  Включается командой `clientperf items on` (тогда ставится Harmony-патч на Item.Update).
    //  Пока выключено — патча нет, накладные нулевые (baseline не искажается).
    // ==========================================================================================
    public static class ItemProfiler
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
                Harmony h = new Harmony("com.ng.clientperflogger.items");
                MethodInfo m = AccessTools.Method(typeof(Item), "Update", new[] { typeof(float), typeof(Camera) });
                if (m == null) { ClientPerf.Log("Item.Update не найден — замер недоступен.", Color.Orange); return; }
                h.Patch(m,
                    prefix:  new HarmonyMethod(typeof(ItemUpdatePatch).GetMethod(nameof(ItemUpdatePatch.Prefix),  BindingFlags.Static | BindingFlags.Public)),
                    postfix: new HarmonyMethod(typeof(ItemUpdatePatch).GetMethod(nameof(ItemUpdatePatch.Postfix), BindingFlags.Static | BindingFlags.Public)));
                _h = h;
            }
            catch (Exception ex) { ClientPerf.Log("Не удалось включить замер: " + ex.Message, Color.Red); _h = null; }
        }

        public static void Disable()
        {
            try { _h?.UnpatchSelf(); } catch { }
            _h = null;
        }

        public static void ResetStats() { _stats.Clear(); }

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

        public static void Report(int topN)
        {
            if (_stats.Count == 0)
            {
                ClientPerf.Log("Нет данных. Включи замер: clientperf items on — поиграй — потом clientperf items.", Color.Orange);
                return;
            }
            double freq = Stopwatch.Frequency;
            long grandTicks = _stats.Values.Sum(x => x.Ticks);
            long grandCalls = _stats.Values.Sum(x => x.Calls);
            double grandMs = grandTicks * 1000.0 / freq;

            ClientPerf.Log($"===== ТОП предметов по суммарному Item.Update (замерено {grandMs:F0} мс / {grandCalls:N0} вызовов; замер {(Enabled ? "ВКЛ" : "ВЫКЛ")}) =====", Color.Cyan);

            int i = 1;
            foreach (var kvp in _stats.OrderByDescending(k => k.Value.Ticks).Take(topN))
            {
                Stat s = kvp.Value;
                double ms  = s.Ticks * 1000.0 / freq;
                double us  = s.Calls > 0 ? s.Ticks * 1000000.0 / freq / s.Calls : 0;
                double pct = grandTicks > 0 ? s.Ticks * 100.0 / grandTicks : 0;
                ClientPerf.Log($"  {i,2}. {ClientPerf.Pad(kvp.Key, 28)} {ms,8:F1}мс {pct,5:F1}%  {us,7:F2}µs/вызов  [{s.Pkg}]",
                    pct >= 15 ? Color.OrangeRed : (pct >= 5 ? Color.Orange : Color.LightGray));
                i++;
            }

            ClientPerf.Log("--- По МОДАМ (суммарно Item.Update) ---", Color.White);
            foreach (var mod in _stats.GroupBy(k => k.Value.Pkg)
                                      .Select(g => new { Pkg = g.Key, Ticks = g.Sum(x => x.Value.Ticks) })
                                      .OrderByDescending(x => x.Ticks).Take(12))
            {
                double ms  = mod.Ticks * 1000.0 / freq;
                double pct = grandTicks > 0 ? mod.Ticks * 100.0 / grandTicks : 0;
                ClientPerf.Log($"   {ClientPerf.Pad(mod.Pkg, 34)} {ms,8:F1}мс {pct,5:F1}%", pct >= 25 ? Color.Orange : Color.LightGray);
            }
            ClientPerf.Log("Высокий µs/вызов у предмета = у него дорогой Update (тяжёлый Always-эффект/компонент) — чинить тот мод.", Color.Gray);
        }
    }

    public static class ItemUpdatePatch
    {
        // out __state — старт замера. <=0 в постфиксе => пропускаем (если другой мод-префикс пропустил оригинал, __state останется 0).
        public static void Prefix(out long __state) { __state = Stopwatch.GetTimestamp(); }

        public static void Postfix(Item __instance, long __state)
        {
            if (__state <= 0) { return; }
            ItemProfiler.Record(__instance, Stopwatch.GetTimestamp() - __state);
        }
    }
}
