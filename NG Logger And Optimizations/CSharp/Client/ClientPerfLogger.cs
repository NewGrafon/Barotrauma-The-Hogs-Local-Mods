using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using Barotrauma;
using Barotrauma.Items.Components;
using HarmonyLib;
using Microsoft.Xna.Framework;

namespace NetEventLogger
{
    // ==========================================================================================
    //  CLIENT FPS profiler (loaded on EVERY participant: host + all clients).
    //  Data source — the built-in Barotrauma.GameMain.PerformanceCounter:
    //    * AverageFramesPerSecond — average FPS;
    //    * GetSavedIdentifiers / GetAverageElapsedMillisecs(id) — per-subsystem frame cost
    //      (Update:Character/Particles/Physics/StatusEffects/Ragdolls/MapEntity:Items/Power…,
    //       Draw:Map:Lighting/FrontParticles/BackCharactersItems/HUD/PostProcess…).
    //  Every 60s (of in-round time) it prints avg FPS + the top-10 operations and keeps every window
    //  in memory for the WHOLE SESSION. The `clientperf` command builds a session portrait.
    //
    //  RUS: КЛИЕНТСКИЙ профайлер ФПС (грузится у КАЖДОГО участника: хост + все клиенты).
    //  RUS: Источник данных — встроенный Barotrauma.GameMain.PerformanceCounter:
    //  RUS:   * AverageFramesPerSecond — средний FPS;
    //  RUS:   * GetSavedIdentifiers / GetAverageElapsedMillisecs(id) — стоимость подсистем кадра.
    //  RUS: Раз в 60с (игрового времени в раунде) пишет в консоль средний FPS + топ-10 операций
    //  RUS: и копит каждое окно в памяти ВСЕЙ СЕССИИ. Команда `clientperf` строит портрет сессии.
    // ==========================================================================================
    public sealed class ClientPerfLoggerPlugin : IAssemblyPlugin
    {
        private static Harmony _harmony;

        public void PreInitPatching() { }

        public void Initialize()
        {
            ClientPerf.Log(Loc.T("Инициализация клиентского профайлера…", "Initializing the client profiler…"), Color.Yellow);
            try
            {
                if (_harmony == null)
                {
                    _harmony = new Harmony("com.ng.clientperflogger");
                    // Per-frame tick: postfix on the client GameMain.Update(GameTime).
                    // RUS: Покадровый тик: постфикс на клиентский GameMain.Update(GameTime).
                    MethodInfo update = AccessTools.Method(typeof(GameMain), "Update", new[] { typeof(GameTime) });
                    if (update != null)
                    {
                        _harmony.Patch(update, postfix: new HarmonyMethod(typeof(ClientPerfPatch).GetMethod(
                            nameof(ClientPerfPatch.Update_Postfix), BindingFlags.Static | BindingFlags.Public)));
                    }
                    else
                    {
                        ClientPerf.Log(Loc.T("НЕ найден GameMain.Update — профайлер не сможет тикать!", "GameMain.Update NOT found — the profiler can't tick!"), Color.Red);
                    }

                    // Prefix on the client GUI.Update(float): add the menu window to the GUI list BEFORE
                    // input handling and drawing (without this the window isn't drawn or doesn't catch clicks).
                    // RUS: Префикс на клиентский GUI.Update(float): заносим окно меню в GUI-список ДО обработки
                    // RUS: ввода и отрисовки (без этого окно либо не рисуется, либо не ловит клики).
                    MethodInfo guiUpdate = AccessTools.Method(typeof(GUI), "Update", new[] { typeof(float) });
                    if (guiUpdate != null)
                    {
                        _harmony.Patch(guiUpdate, prefix: new HarmonyMethod(typeof(GuiUpdatePatch).GetMethod(
                            nameof(GuiUpdatePatch.Prefix), BindingFlags.Static | BindingFlags.Public)));
                    }
                }

                ClientPerf.RegisterCommands();
                OptConfig.Init(); // load saved fix states + apply   // RUS: загрузить сохранённые состояния фиксов + применить
                ClientPerf.Log(Loc.T("=== Клиентский профайлер загружен. Команда: clientperf (см. clientperf help) ===", "=== Client profiler loaded. Command: clientperf (see clientperf help) ==="), Color.LightGreen);
            }
            catch (Exception ex)
            {
                ClientPerf.Log(Loc.T("ОШИБКА инициализации: ", "Init ERROR: ") + ex, Color.Red);
            }
        }

        public void OnLoadCompleted() { }

        public void Dispose()
        {
            try { _harmony?.UnpatchSelf(); } catch { }
            _harmony = null;
            try { Benchmark.Cancel(); } catch { }
            try { ClientPerfMenu.Close(); } catch { }
            try { ItemProfiler.Disable(); } catch { }
            try { ClientPerf.UnregisterCommands(); } catch { }
        }
    }

    // (Loc moved to CSharp/Shared/Loc.cs so the server logger can localize too.)
    // RUS: (Loc перенесён в CSharp/Shared/Loc.cs, чтобы серверный логгер тоже мог локализоваться.)

    public static class ClientPerf
    {
        // --- settings ---   // RUS: настройки
        private const double SampleIntervalSec = 1.0;   // how often to sample (once a second)   // RUS: как часто снимать пробу
        private const double WindowSec         = 60.0;  // window length   // RUS: длина окна
        private const int    TopWindow         = 10;    // top ops in the auto window summary   // RUS: топ операций в авто-выводе окна
        private const int    TopSession        = 20;    // top ops in the session report   // RUS: топ операций в отчёте сессии
        private const int    MaxWindows        = 20000; // guard against unbounded memory growth   // RUS: страховка от безграничного роста памяти

        public static bool AutoLog = false;             // print a summary every 60s — OFF by default ("Console logs" / clientperf auto)   // RUS: сводка каждые 60с — по умолч. ВЫКЛ («Консольные логи» / clientperf auto)

        // --- clock and current-window accumulators ---   // RUS: часы и накопители текущего окна
        private static readonly Stopwatch _clock = Stopwatch.StartNew();
        private static double _lastTickSec = 0;
        private static double _sampleElapsed = 0;
        private static double _windowElapsed = 0;

        private static readonly Dictionary<string, double> _accMs = new Dictionary<string, double>();
        private static int    _sampleCount = 0;
        private static double _fpsSum = 0;
        private static double _fpsMin = double.MaxValue;
        private static int    _windowIndex = 0;

        // --- session memory ---   // RUS: память сессии
        public sealed class Window
        {
            public int    Index;
            public double AvgFps;
            public double MinFps;
            public double UpdateMs;
            public double DrawMs;
            public int    Samples;
            public Dictionary<string, double> OpMs; // average ms/frame per identifier   // RUS: средние мс/кадр по каждому идентификатору
        }
        private static readonly List<Window> _session = new List<Window>();

        // ----------------------------------------------------------------------------------
        //  Per-frame tick (called from the Harmony postfix of GameMain.Update).
        //  RUS: Покадровый тик (зовётся из Harmony-постфикса GameMain.Update).
        // ----------------------------------------------------------------------------------
        public static void Tick()
        {
            try
            {
                double now = _clock.Elapsed.TotalSeconds;
                double delta = now - _lastTickSec;
                _lastTickSec = now;
                // a normal frame? (discard the first frame and big gaps: loading/pause/minimized)
                // RUS: нормальный кадр? (отбрасываем первый кадр и большие провалы: загрузка/пауза/сворачивание)
                double safeDelta = (delta > 0 && delta <= 1.0) ? delta : 0;

                bool inRound = GameMain.GameScreen != null && Screen.Selected == GameMain.GameScreen;

                // benchmark — EVERY frame (not only in a round).
                // The menu window's draw/input (ClientPerfMenu.Update) is done in the GUI.Update prefix (GuiUpdatePatch).
                // RUS: бенчмарк — КАЖДЫЙ кадр (а не только в раунде). Отрисовку/ввод окна меню — в префиксе GUI.Update.
                Benchmark.Tick(safeDelta, inRound);
                OptConfig.Tick(safeDelta, inRound); // re-apply fixes after PE activates each round   // RUS: переприменить фиксы после активации PE на старте раунда

                // profiler sampling — only in a round and on a normal frame
                // RUS: сэмплинг профайлера — только в раунде и при нормальном кадре
                if (safeDelta <= 0 || !inRound) { return; }
                PerformanceCounter pc = GameMain.PerformanceCounter;
                if (pc == null) { return; }

                _sampleElapsed += safeDelta;
                _windowElapsed += safeDelta;

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
        //  Top LEAF operations (exclude aggregate parents to avoid double-counting:
        //  "Update" and "Draw" are sums of their own sub-branches, shown as a separate total line).
        //  RUS: Топ ЛИСТОВЫХ операций (исключаем родителей-агрегаты, чтобы не было двойного учёта:
        //  RUS: "Update" и "Draw" — это суммы своих под-веток, их показываем отдельной строкой-итогом).
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
            Log(Loc.Ru
                ? $"=== Окно #{w.Index} (60с) === средний FPS {w.AvgFps:F1} (мин {w.MinFps:F0}) | Update {w.UpdateMs:F2}мс + Draw {w.DrawMs:F2}мс"
                : $"=== Window #{w.Index} (60s) === avg FPS {w.AvgFps:F1} (min {w.MinFps:F0}) | Update {w.UpdateMs:F2}ms + Draw {w.DrawMs:F2}ms", FpsColor(w.AvgFps));
            var top = TopLeafOps(w.OpMs, TopWindow);
            Log(Loc.Ru ? $"  Топ-{top.Count} тяжёлых операций (средн. мс/кадр):"
                       : $"  Top {top.Count} heaviest operations (avg ms/frame):", Color.LightGray);
            int i = 1;
            foreach (var kvp in top) { Log($"    {i,2}. {Pad(kvp.Key, 34)} {kvp.Value,7:F3} {Loc.Ms}", MsColor(kvp.Value)); i++; }
        }

        // ----------------------------------------------------------------------------------
        //  Whole-session report: session top-20 + 3 snapshots (worst/typical/best FPS).
        //  RUS: Отчёт по всей сессии: топ-20 за сессию + 3 снапшота (худший/типичный/лучший FPS).
        // ----------------------------------------------------------------------------------
        public static void PrintSessionReport()
        {
            // the current window hasn't closed yet but already has data — count it "on the fly"
            // RUS: если текущее окно ещё не закрылось, но в нём уже есть данные — учтём его «на лету»
            FlushPartialWindowIfAny();

            if (_session.Count == 0)
            {
                Log(Loc.T("Данных пока нет: ни одно 60с-окно не завершилось в раунде. Поиграй немного и повтори.",
                          "No data yet: no 60s window finished in a round. Play a bit and try again."), Color.Orange);
                return;
            }

            double sessFpsMean = _session.Average(x => x.AvgFps);
            double sessFpsLow  = _session.Min(x => x.MinFps);
            Log(Loc.Ru ? $"===== ПОРТРЕТ ПРОИЗВОДИТЕЛЬНОСТИ СЕССИИ: {_session.Count} окон (~{_session.Count} мин в раундах) ====="
                       : $"===== SESSION PERFORMANCE PORTRAIT: {_session.Count} windows (~{_session.Count} min in rounds) =====", Color.Cyan);
            Log(Loc.Ru ? $"   Средний FPS по сессии: {sessFpsMean:F1} | худшая секунда: {sessFpsLow:F0} FPS"
                       : $"   Session average FPS: {sessFpsMean:F1} | worst second: {sessFpsLow:F0} FPS", Color.Cyan);

            // --- aggregated top operations over the whole session (mean of per-window means) ---
            // RUS: агрегированный топ операций за всю сессию (среднее от оконных средних)
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

            Log(Loc.Ru ? $"--- Топ-{topSess.Count} тяжёлых операций за СЕССИЮ (средн. мс/кадр) ---"
                       : $"--- Top {topSess.Count} heaviest operations over the SESSION (avg ms/frame) ---", Color.White);
            int i = 1;
            foreach (var kvp in topSess) { Log($"  {i,2}. {Pad(kvp.Key, 34)} {kvp.Value,7:F3} {Loc.Ms}", MsColor(kvp.Value)); i++; }

            // --- 3 representative snapshots ---
            // RUS: 3 характерных снапшота
            var byFps = _session.OrderBy(x => x.AvgFps).ToList();
            Window worst  = byFps.First();
            Window best   = byFps.Last();
            // the "most average average": the window whose avgFps is closest to the session mean
            // RUS: «самое среднее среднее»: окно с avgFps ближе всего к среднему по сессии
            Window typical = _session.OrderBy(x => Math.Abs(x.AvgFps - sessFpsMean)).First();

            PrintSnapshot(Loc.T("ХУДШИЙ FPS (тут искать виновника)", "WORST FPS (look for the culprit here)"), worst);
            PrintSnapshot(Loc.T("ТИПИЧНЫЙ FPS (среднее по сессии)", "TYPICAL FPS (session average)"), typical);
            PrintSnapshot(Loc.T("ЛУЧШИЙ FPS (для сравнения)", "BEST FPS (for comparison)"), best);

            Log(Loc.T("Подсказка: если в «худшем» окне резко выделяется Draw:Map:Lighting/FrontParticles — рендер (свет/частицы);",
                      "Hint: if in the 'worst' window Draw:Map:Lighting/FrontParticles stands out — it's rendering (lights/particles);"), Color.Gray);
            Log(Loc.T("           Update:StatusEffects/Character/MapEntity:Items — скрипты/моды/сущности; Update:Physics — физика/сабы.",
                      "           Update:StatusEffects/Character/MapEntity:Items — scripts/mods/entities; Update:Physics — physics/subs."), Color.Gray);
        }

        private static void PrintSnapshot(string label, Window w)
        {
            Log(Loc.Ru
                ? $"--- Снапшот: {label} — окно #{w.Index}: средний {w.AvgFps:F1} FPS (мин {w.MinFps:F0}) | Update {w.UpdateMs:F2}мс + Draw {w.DrawMs:F2}мс ---"
                : $"--- Snapshot: {label} — window #{w.Index}: avg {w.AvgFps:F1} FPS (min {w.MinFps:F0}) | Update {w.UpdateMs:F2}ms + Draw {w.DrawMs:F2}ms ---", FpsColor(w.AvgFps));
            var top = TopLeafOps(w.OpMs, TopWindow);
            int i = 1;
            foreach (var kvp in top) { Log($"       {i,2}. {Pad(kvp.Key, 34)} {kvp.Value,7:F3} {Loc.Ms}", MsColor(kvp.Value)); i++; }
        }

        // snapshot the current (not-yet-closed) window without clearing accumulators, and add it to the session temporarily
        // RUS: снять снимок текущего (ещё не закрытого) окна, не очищая накопители, и временно добавить в сессию
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
            // mark the current window as already counted: close it so the next close doesn't double it
            // RUS: помечаем, что текущее окно уже учтено: закрываем его, чтобы не задвоить при следующем закрытии
            _windowElapsed = 0;
            ResetWindow();
        }

        public static void PrintCurrentWindow()
        {
            if (_sampleCount <= 0) { Log(Loc.T("Текущее окно пустое (не в раунде или только началось).", "Current window is empty (not in a round, or it just started)."), Color.Orange); return; }
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
            Log(Loc.Ru ? $"=== ТЕКУЩЕЕ окно ({_sampleCount}с накоплено) ===" : $"=== CURRENT window ({_sampleCount}s accumulated) ===", FpsColor(w.AvgFps));
            PrintSnapshot(Loc.T("текущее", "current"), w);
        }

        public static void ResetSession()
        {
            _session.Clear();
            _windowIndex = 0;
            ResetWindow();
            _windowElapsed = 0;
            ItemProfiler.ResetStats();
            Log(Loc.T("Память сессии и пер-предметный замер очищены.", "Session memory and per-item profiling cleared."), Color.Yellow);
        }

        // ----------------------------------------------------------------------------------
        //  Console command
        //  RUS: Консольная команда
        // ----------------------------------------------------------------------------------
        public static void RegisterCommands()
        {
            UnregisterCommands();
            DebugConsole.Commands.Add(new DebugConsole.Command(
                "clientperf|ngperf",
                Loc.T("NG клиентский профайлер ФПС. Без аргументов — отчёт сессии. Аргументы: menu | now | items | reset | auto | help",
                      "NG client FPS profiler. No args — session report. Args: menu | now | items | reset | auto | help"),
                args =>
                {
                    string a = args != null && args.Length > 0 ? args[0].ToLowerInvariant() : "";
                    switch (a)
                    {
                        case "now":   PrintCurrentWindow(); break;
                        case "reset": ResetSession(); break;
                        case "auto":
                            OptConfig.SetAutoLog(!AutoLog); // toggle + persist (ngopt_config.txt)   // RUS: переключить + сохранить (ngopt_config.txt)
                            Log(Loc.T("Авто-вывод каждые 60с: ", "Auto-print every 60s: ") + (AutoLog ? Loc.On : Loc.Off), AutoLog ? Color.LightGreen : Color.Gray);
                            break;
                        case "items":
                        {
                            string sub  = args != null && args.Length > 1 ? args[1] : "";
                            string subl = sub.ToLowerInvariant();
                            if (subl == "on")         { ItemProfiler.Enable(); }
                            else if (subl == "off")   { ItemProfiler.Disable(); Log(Loc.T("Пер-предметный замер ВЫКЛ (патч снят, накладных нет).", "Per-item profiling OFF (patch removed, no overhead)."), Color.Gray); }
                            else if (subl == "reset") { ItemProfiler.ResetStats(); Log(Loc.T("Пер-предметный накопитель очищен.", "Per-item accumulator cleared."), Color.Yellow); }
                            else if (sub != "")       { ItemProfiler.ReportPrefab(sub); }   // clientperf items spas-13
                            else                      { ItemProfiler.Report(10); }
                            break;
                        }
                        case "menu":  ClientPerfMenu.Toggle(); break;
                        case "help":
                            Log(Loc.T("clientperf menu     — открыть/закрыть окно NG Logger&Optimizations",
                                      "clientperf menu     — open/close the NG Logger&Optimizations window"), Color.LightGreen);
                            Log(Loc.T("clientperf          — портрет сессии: топ-20 операций + 3 снапшота (худший/типичный/лучший FPS)",
                                      "clientperf          — session portrait: top-20 operations + 3 snapshots (worst/typical/best FPS)"), Color.White);
                            Log(Loc.T("clientperf now      — топ операций за текущее (ещё не закрытое) окно",
                                      "clientperf now      — top operations for the current (not-yet-closed) window"), Color.White);
                            Log(Loc.T("clientperf items    — топ ПРЕДМЕТОВ/МОДОВ по времени Item.Update (сначала: clientperf items on)",
                                      "clientperf items    — top ITEMS/MODS by Item.Update time (first: clientperf items on)"), Color.White);
                            Log(Loc.T("clientperf items <предмет> — РАЗБИВКА предмета по компонентам/эффектам (напр. clientperf items spas-13)",
                                      "clientperf items <item> — item BREAKDOWN by components/effects (e.g. clientperf items spas-13)"), Color.White);
                            Log(Loc.T("clientperf items on|off|reset — вкл/выкл/очистить пер-предметный замер",
                                      "clientperf items on|off|reset — enable/disable/clear per-item profiling"), Color.White);
                            Log(Loc.T("clientperf reset    — очистить память сессии (и предметов)",
                                      "clientperf reset    — clear session memory (and items)"), Color.White);
                            Log(Loc.T("clientperf auto     — вкл/выкл авто-вывод сводки каждые 60с",
                                      "clientperf auto     — toggle auto-printing the summary every 60s"), Color.White);
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
        //  Utilities
        //  RUS: Утилиты
        // ----------------------------------------------------------------------------------
        internal static string Pad(string s, int len) => s.Length >= len ? s : s + new string(' ', len - s.Length);

        private static Color FpsColor(double fps) => fps >= 50 ? Color.LightGreen : (fps >= 30 ? Color.Yellow : Color.OrangeRed);
        private static Color MsColor(double ms)   => ms >= 3.0 ? Color.OrangeRed : (ms >= 1.0 ? Color.Orange : Color.LightGray);

        public static void Log(string text, Color color)
        {
            try { DebugConsole.NewMessage(Loc.Tag + text, color); } catch { }
        }
    }

    public static class ClientPerfPatch
    {
        public static void Update_Postfix() => ClientPerf.Tick();
    }

    public static class GuiUpdatePatch
    {
        // GUI.Update prefix: insert the menu window into the GUI list right before input handling + drawing.
        // RUS: Префикс GUI.Update: заносим окно меню в GUI-список ровно перед обработкой ввода + отрисовкой.
        public static void Prefix() => ClientPerfMenu.Update();
    }

    // ==========================================================================================
    //  Per-item profiling: which items/mods eat time in Item.Update.
    //  Enabled via `clientperf items on` (then a Harmony patch is placed on Item.Update).
    //  While off — no patch, zero overhead (the baseline isn't distorted).
    //  RUS: Пер-предметный замер: кто из предметов/модов ест время в Item.Update.
    //  RUS: Включается командой `clientperf items on` (тогда ставится Harmony-патч на Item.Update).
    //  RUS: Пока выключено — патча нет, накладные нулевые (baseline не искажается).
    // ==========================================================================================
    public static class ItemProfiler
    {
        private static Harmony _h;
        public static bool Enabled => _h != null;

        private sealed class Stat { public long Ticks; public long Calls; public string Pkg; }
        private static readonly Dictionary<string, Stat> _stats = new Dictionary<string, Stat>();
        private static readonly Dictionary<string, Stat> _parts = new Dictionary<string, Stat>(); // prefab :: part (Comp:Type / [StatusEffects] / [Sounds])   // RUS: prefab :: часть (Comp:Тип / [StatusEffects] / [Sounds])

        public static void Enable()
        {
            if (_h != null) { return; }
            try
            {
                Harmony h = new Harmony("com.ng.clientperflogger.items");
                BindingFlags sp = BindingFlags.Static | BindingFlags.Public;

                // 1) Item.Update — total time of the item (for the top list and for "total" in the breakdown).
                // RUS: 1) Item.Update — суммарное время предмета (для топа и для "всего" в разбивке).
                MethodInfo m = AccessTools.Method(typeof(Item), "Update", new[] { typeof(float), typeof(Camera) });
                if (m == null) { ClientPerf.Log(Loc.T("Item.Update не найден — замер недоступен.", "Item.Update not found — profiling unavailable."), Color.Orange); return; }
                h.Patch(m,
                    prefix:  new HarmonyMethod(typeof(ItemUpdatePatch).GetMethod(nameof(ItemUpdatePatch.Prefix),  sp)),
                    postfix: new HarmonyMethod(typeof(ItemUpdatePatch).GetMethod(nameof(ItemUpdatePatch.Postfix), sp)));

                // 2) Item.ApplyStatusEffects — time of the item's status effects (the [StatusEffects] part).
                // RUS: 2) Item.ApplyStatusEffects — время статус-эффектов предмета (часть [StatusEffects]).
                MethodInfo se = AccessTools.Method(typeof(Item), "ApplyStatusEffects",
                    new[] { typeof(ActionType), typeof(float), typeof(Character), typeof(Limb), typeof(Entity), typeof(bool), typeof(Vector2?) });
                if (se != null)
                {
                    h.Patch(se,
                        prefix:  new HarmonyMethod(typeof(StatusFxPatch).GetMethod(nameof(StatusFxPatch.Prefix),  sp)),
                        postfix: new HarmonyMethod(typeof(StatusFxPatch).GetMethod(nameof(StatusFxPatch.Postfix), sp)));
                }

                // 3) ItemComponent.UpdateSounds — client-side sound update (called in the Item.Update loop; the [Sounds] part).
                // RUS: 3) ItemComponent.UpdateSounds — клиентское обновление звуков (вызов в цикле Item.Update; часть [Sounds]).
                MethodInfo us = AccessTools.Method(typeof(ItemComponent), "UpdateSounds");
                if (us != null)
                {
                    h.Patch(us,
                        prefix:  new HarmonyMethod(typeof(SoundsPatch).GetMethod(nameof(SoundsPatch.Prefix),  sp)),
                        postfix: new HarmonyMethod(typeof(SoundsPatch).GetMethod(nameof(SoundsPatch.Postfix), sp)));
                }

                // 4) All ItemComponent.Update(float,Camera) overrides — time per component TYPE (the Comp:Type parts).
                // RUS: 4) Все override-ы ItemComponent.Update(float,Camera) — время по ТИПУ компонента (части Comp:Тип).
                HarmonyMethod cpre  = new HarmonyMethod(typeof(CompUpdatePatch).GetMethod(nameof(CompUpdatePatch.Prefix),  sp));
                HarmonyMethod cpost = new HarmonyMethod(typeof(CompUpdatePatch).GetMethod(nameof(CompUpdatePatch.Postfix), sp));
                Type[] types;
                try { types = typeof(ItemComponent).Assembly.GetTypes(); }
                catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(x => x != null).ToArray(); }
                int patched = 0;
                foreach (Type t in types)
                {
                    if (t == null || t.IsAbstract || !typeof(ItemComponent).IsAssignableFrom(t)) { continue; }
                    MethodInfo upd;
                    try { upd = t.GetMethod("Update", BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly, null, new[] { typeof(float), typeof(Camera) }, null); }
                    catch { continue; }
                    if (upd == null || upd.IsAbstract || upd.DeclaringType != t) { continue; }
                    try { h.Patch(upd, prefix: cpre, postfix: cpost); patched++; } catch { }
                }

                _h = h;
                ClientPerf.Log(Loc.Ru
                    ? $"Пер-предметный замер ВКЛ (компонентов запатчено: {patched}). Поиграй, потом: clientperf items / clientperf items <предмет>"
                    : $"Per-item profiling ON (components patched: {patched}). Play, then: clientperf items / clientperf items <item>", Color.LightGreen);
            }
            catch (Exception ex) { ClientPerf.Log(Loc.T("Не удалось включить замер: ", "Failed to enable profiling: ") + ex.Message, Color.Red); _h = null; }
        }

        public static void Disable()
        {
            try { _h?.UnpatchSelf(); } catch { }
            _h = null;
        }

        public static void ResetStats() { _stats.Clear(); _parts.Clear(); }

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

        // Top items: builds lines (text + color) and also returns the ordered list of prefab-ids (for the benchmark).
        // RUS: Топ предметов: строит строки (текст + цвет) и заодно возвращает упорядоченный список prefab-id (для бенчмарка).
        public static List<(string Text, Color Color)> ItemsReportLines(int topN, out List<string> topPrefabs)
        {
            var lines = new List<(string Text, Color Color)>();
            topPrefabs = new List<string>();
            if (_stats.Count == 0)
            {
                lines.Add((Loc.T("Нет данных. Включи замер: clientperf items on — поиграй — потом clientperf items.",
                                 "No data. Enable: clientperf items on — play a bit — then clientperf items."), Color.Orange));
                return lines;
            }
            double freq = Stopwatch.Frequency;
            long grandTicks = _stats.Values.Sum(x => x.Ticks);
            long grandCalls = _stats.Values.Sum(x => x.Calls);
            double grandMs = grandTicks * 1000.0 / freq;

            lines.Add((Loc.Ru
                ? $"===== ТОП предметов по суммарному Item.Update (замерено {grandMs:F0} мс / {grandCalls:N0} вызовов; замер {(Enabled ? "ВКЛ" : "ВЫКЛ")}) ====="
                : $"===== TOP items by total Item.Update (measured {grandMs:F0} ms / {grandCalls:N0} calls; profiling {(Enabled ? "ON" : "OFF")}) =====", Color.Cyan));

            int i = 1;
            foreach (var kvp in _stats.OrderByDescending(k => k.Value.Ticks).Take(topN))
            {
                Stat s = kvp.Value;
                double ms  = s.Ticks * 1000.0 / freq;
                double us  = s.Calls > 0 ? s.Ticks * 1000000.0 / freq / s.Calls : 0;
                double pct = grandTicks > 0 ? s.Ticks * 100.0 / grandTicks : 0;
                lines.Add(($"  {i,2}. {ClientPerf.Pad(kvp.Key, 28)} {ms,8:F1}{Loc.Ms} {pct,5:F1}%  {us,7:F2}{Loc.UsPerCall}  [{s.Pkg}]",
                    pct >= 15 ? Color.OrangeRed : (pct >= 5 ? Color.Orange : Color.LightGray)));
                topPrefabs.Add(kvp.Key);
                i++;
            }

            lines.Add((Loc.T("--- По МОДАМ (суммарно Item.Update) ---", "--- By MOD (total Item.Update) ---"), Color.White));
            foreach (var mod in _stats.GroupBy(k => k.Value.Pkg)
                                      .Select(g => new { Pkg = g.Key, Ticks = g.Sum(x => x.Value.Ticks) })
                                      .OrderByDescending(x => x.Ticks).Take(5))
            {
                double ms  = mod.Ticks * 1000.0 / freq;
                double pct = grandTicks > 0 ? mod.Ticks * 100.0 / grandTicks : 0;
                lines.Add(($"   {ClientPerf.Pad(mod.Pkg, 34)} {ms,8:F1}{Loc.Ms} {pct,5:F1}%", pct >= 25 ? Color.Orange : Color.LightGray));
            }
            return lines;
        }

        public static void Report(int topN)
        {
            foreach (var ln in ItemsReportLines(topN, out _)) { ClientPerf.Log(ln.Text, ln.Color); }
            if (_stats.Count > 0) { ClientPerf.Log(Loc.T("Высокий µs/вызов у предмета = у него дорогой Update (тяжёлый Always-эффект/компонент) — чинить тот мод.", "High µs/call = expensive Update for that item (heavy Always effect/component) — fix that mod."), Color.Gray); }
        }

        // The same report as plain text (for the benchmark window / copying).
        // RUS: Тот же отчёт обычным текстом (для окна бенчмарка / копирования).
        public static string BuildItemsReportText(int topN, out List<string> topPrefabs)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var ln in ItemsReportLines(topN, out topPrefabs)) { sb.AppendLine(ln.Text); }
            return sb.ToString();
        }

        public static void RecordPart(Item item, string label, long ticks)
        {
            try
            {
                if (item?.Prefab == null) { return; }
                string key = item.Prefab.Identifier.Value + " :: " + label;
                if (!_parts.TryGetValue(key, out Stat s)) { s = new Stat(); _parts[key] = s; }
                s.Ticks += ticks; s.Calls++;
            }
            catch { }
        }

        public static void RecordComp(ItemComponent ic, long ticks)
        {
            if (ic == null) { return; }
            RecordPart(ic.Item, "Comp:" + ic.GetType().Name, ticks);
        }

        // Breakdown of a SPECIFIC item by parts (components / status effects / sounds / other).
        // RUS: Разбивка КОНКРЕТНОГО предмета по частям (компоненты / статус-эффекты / звуки / прочее).
        public static List<(string Text, Color Color)> PrefabReportLines(string filter)
        {
            var lines = new List<(string Text, Color Color)>();
            // exact match (the benchmark passes a ready prefab-id) -> otherwise substring (top-3 by time)
            // RUS: точное совпадение (бенчмарк передаёт готовый prefab-id) -> иначе подстрока (топ-3 по времени)
            string exact = _stats.Keys.FirstOrDefault(k => k.Equals(filter, StringComparison.OrdinalIgnoreCase));
            List<string> matches = exact != null
                ? new List<string> { exact }
                : _stats.Keys.Where(k => k.ToLowerInvariant().Contains(filter.ToLowerInvariant()))
                             .OrderByDescending(k => _stats[k].Ticks).Take(3).ToList();
            if (matches.Count == 0)
            {
                lines.Add((Loc.T($"Предмет '{filter}' не найден в замере. Сначала: clientperf items on, поиграй.",
                                 $"Item '{filter}' not found in profiling. First: clientperf items on, then play."), Color.Orange));
                return lines;
            }
            double freq = Stopwatch.Frequency;
            foreach (string prefab in matches)
            {
                Stat tot = _stats[prefab];
                double totMs = tot.Ticks * 1000.0 / freq;
                double totUs = tot.Calls > 0 ? tot.Ticks * 1000000.0 / freq / tot.Calls : 0;
                lines.Add((Loc.Ru
                    ? $"===== {prefab}: всего {totMs:F1}мс ({totUs:F1}µs/вызов, {tot.Calls:N0} выз.) — разбивка по частям ====="
                    : $"===== {prefab}: total {totMs:F1}ms ({totUs:F1}µs/call, {tot.Calls:N0} calls) — breakdown by part =====", Color.Cyan));

                string pre = prefab + " :: ";
                var parts = _parts.Where(kvp => kvp.Key.StartsWith(pre, StringComparison.Ordinal))
                                  .OrderByDescending(kvp => kvp.Value.Ticks).ToList();
                long sumParts = 0;
                foreach (var p in parts)
                {
                    Stat s = p.Value; sumParts += s.Ticks;
                    double ms  = s.Ticks * 1000.0 / freq;
                    double pct = tot.Ticks > 0 ? s.Ticks * 100.0 / tot.Ticks : 0;
                    string label = p.Key.Substring(pre.Length);
                    lines.Add(($"   {ClientPerf.Pad(label, 30)} {ms,8:F1}{Loc.Ms} {pct,5:F1}%",
                        pct >= 25 ? Color.OrangeRed : (pct >= 10 ? Color.Orange : Color.LightGray)));
                }
                long other = tot.Ticks - sumParts;
                if (other > 0 && parts.Count > 0)
                {
                    double ms  = other * 1000.0 / freq;
                    double pct = tot.Ticks > 0 ? other * 100.0 / tot.Ticks : 0;
                    lines.Add(($"   {ClientPerf.Pad(Loc.T("[прочее: loop/conditionals]", "[other: loop/conditionals]"), 30)} {ms,8:F1}{Loc.Ms} {pct,5:F1}%", Color.Gray));
                }
            }
            return lines;
        }

        public static void ReportPrefab(string filter)
        {
            foreach (var ln in PrefabReportLines(filter)) { ClientPerf.Log(ln.Text, ln.Color); }
        }

        public static string BuildPrefabReportText(string filter)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var ln in PrefabReportLines(filter)) { sb.AppendLine(ln.Text); }
            return sb.ToString();
        }
    }

    // ==========================================================================================
    //  BENCHMARK: one button runs the whole profiling sequence and collects the result into text.
    //  Sequence: items off -> reset -> on -> WAIT 60s (in a round) -> off ->
    //  overall report (top-10 + top-5 mods) -> breakdown of top-3 items -> reset. Driven frame-by-frame from ClientPerf.Tick.
    //  RUS: БЕНЧМАРК: одной кнопкой прогоняет последовательность замера и собирает результат в текст.
    //  RUS: Последовательность: items off -> reset -> on -> ЖДАТЬ 60с (в раунде) -> off ->
    //  RUS: общий отчёт (топ-10 + топ-5 модов) -> разбивка топ-3 предметов -> reset. Покадрово из ClientPerf.Tick.
    // ==========================================================================================
    public static class Benchmark
    {
        public enum Phase { Idle, Measuring, Done }
        public static Phase State { get; private set; } = Phase.Idle;
        public static string StatusText { get; private set; }
        // History of this CLIENT's benchmark results (the menu navigates it with prev/next).
        // RUS: История результатов бенчмарка ЭТОГО клиента (меню листает её кнопками назад/вперёд).
        public static readonly BenchHistory History = new BenchHistory();

        // Update ONLY the status line (does not touch the result history). Used for live progress updates.
        // RUS: Обновить ТОЛЬКО строку статуса (не трогая историю результатов). Для живых апдейтов прогресса.
        public static void SetStatus(string statusText)
        {
            if (statusText != null) { StatusText = statusText; }
        }

        // Local-time stamp for a benchmark run (used for the CLIENT benchmark; the server sends its own).
        // RUS: Метка локального времени прогона (для КЛИЕНТСКОГО бенчмарка; сервер шлёт свою).
        public static string NowStamp() { return System.DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"); }

        // Add a finished result (prefixed with a "Date:" line) to the given history.
        // RUS: Добавить готовый результат (с добавленной строкой «Дата:») в указанную историю.
        public static void AddToHistory(BenchHistory history, string stamp, List<(string Text, Color Color)> lines)
        {
            try
            {
                if (history == null) { return; }
                var withDate = new List<(string Text, Color Color)>();
                withDate.Add((Loc.Ru ? $"Дата: {stamp}" : $"Date: {stamp}", Color.Gray));
                if (lines != null) { withDate.AddRange(lines); }
                history.Add(stamp, withDate);
            }
            catch { }
        }

        static Benchmark() { StatusText = Loc.T("Готов. Нажми «Бенчмарк».", "Ready. Click «Benchmark»."); }

        // CLIENT benchmark duration (the server benchmark has its OWN duration in ClientOptNet). Presets are shared.
        // RUS: Длительность КЛИЕНТСКОГО бенчмарка (у серверного СВОЯ в ClientOptNet). Пресеты общие.
        public static readonly double[] DurationPresets = { 15.0, 30.0, 60.0, 300.0 };
        public static double DurationSec = 30.0;
        public static double NextPreset(double current)
        {
            int idx = 0;
            for (int i = 0; i < DurationPresets.Length; i++) { if (System.Math.Abs(DurationPresets[i] - current) < 0.5) { idx = i; break; } }
            return DurationPresets[(idx + 1) % DurationPresets.Length];
        }
        public static void CycleDuration() { DurationSec = NextPreset(DurationSec); }
        private static double _left;
        // times of EVERY frame during the measurement -> exact average FPS and 1% Low (mean of the worst 1% of frames)
        // RUS: времена КАЖДОГО кадра за замер -> точные средний FPS и 1% Low (среднее худшего 1% кадров)
        private static readonly List<double> _frameTimes = new List<double>(16384);

        // Per-subsystem cost sampled from the engine PerformanceCounter every frame (Update/Draw/Physics/
        // Lighting/Particles/…), so the report can explain FPS drops that are NOT caused by Item.Update.
        // _subAll = summed over every frame; _subSlow = summed only over slow frames (< 50 FPS) where the
        // drops actually happen. Reported as averages (sum / frame count).
        // RUS: Стоимость по подсистемам из движкового PerformanceCounter каждый кадр (Update/Draw/Physics/
        // RUS: Lighting/Particles/…), чтобы отчёт объяснял просадки FPS, которых НЕТ в Item.Update.
        // RUS: _subAll = сумма по всем кадрам; _subSlow = сумма только по медленным кадрам (<50 FPS), где
        // RUS: и случаются просадки. В отчёте — средние (сумма / число кадров).
        private static readonly Dictionary<string, double> _subAll  = new Dictionary<string, double>();
        private static readonly Dictionary<string, double> _subSlow = new Dictionary<string, double>();
        private static int _subFrames, _subSlowFrames;

        public static bool Running => State == Phase.Measuring;

        public static void StartOrCancel()
        {
            if (State == Phase.Measuring) { Cancel(); } else { Start(); }
        }

        public static void Start()
        {
            try
            {
                ItemProfiler.Disable();      // off
                ItemProfiler.ResetStats();   // reset
                ItemProfiler.Enable();       // on
                _left = DurationSec;
                _frameTimes.Clear();
                _subAll.Clear(); _subSlow.Clear(); _subFrames = 0; _subSlowFrames = 0;
                State = Phase.Measuring;
                StatusText = Loc.T("Замер… (стой возле нагрузки, не закрывай игру)", "Measuring… (stay near the load, keep the game running)");
                ClientPerf.Log(Loc.T("Бенчмарк начат: замер Item.Update.", "Benchmark started: Item.Update profiling."), Color.Cyan);
            }
            catch (Exception ex) { ClientPerf.Log("Benchmark start failed: " + ex.Message, Color.Red); State = Phase.Idle; }
        }

        public static void Cancel()
        {
            try { ItemProfiler.Disable(); } catch { }
            if (State == Phase.Measuring) { StatusText = Loc.T("Замер отменён.", "Benchmark cancelled."); }
            State = Phase.Idle;
        }

        public static void Tick(double delta, bool inRound)
        {
            if (State != Phase.Measuring) { return; }
            if (!inRound) { StatusText = Loc.T("Пауза замера (не в раунде)…", "Benchmark paused (not in a round)…"); return; }
            if (delta <= 0) { return; }

            _frameTimes.Add(delta); // this frame's time (for average FPS and 1% Low)   // RUS: время этого кадра (для среднего FPS и 1% Low)
            SampleSubsystems(delta); // engine per-subsystem cost (for the "what caused the drop" breakdown)   // RUS: стоимость подсистем движка (для разбивки «что вызвало просадку»)

            _left -= delta;
            if (_left <= 0) { Finish(); return; }
            StatusText = Loc.Ru ? $"Замер… осталось {Math.Ceiling(_left):F0}с" : $"Measuring… {Math.Ceiling(_left):F0}s left";
        }

        private static Color FpsColor(double fps) => fps >= 50 ? Color.LightGreen : (fps >= 30 ? Color.Yellow : Color.OrangeRed);

        private static void Finish()
        {
            try
            {
                ItemProfiler.Disable();   // off (data is kept)   // RUS: off (данные сохраняются)

                double avgFps = 0, lowFps = 0;
                if (_frameTimes.Count > 0)
                {
                    double total = 0;
                    foreach (double ft in _frameTimes) { total += ft; }
                    avgFps = total > 0 ? _frameTimes.Count / total : 0;
                    // 1% Low = average FPS of the worst 1% of frames (by the longest frame times)
                    // RUS: 1% Low = средний FPS худшего 1% кадров (по самым долгим временам кадра)
                    var slowest = _frameTimes.OrderByDescending(x => x).ToList();
                    int n = Math.Max(1, (int)(slowest.Count * 0.01));
                    double worst = 0;
                    for (int i = 0; i < n; i++) { worst += slowest[i]; }
                    lowFps = worst > 0 ? n / worst : 0;
                }
                bool fix1 = NGContainerOpt.ContainedEffectsOptPlugin.Enabled;
                bool fix2 = NGNearbyOpt.NearbyTargetsOptPlugin.Enabled;
                int  lvl3 = OptFixes.GetLevel(2);
                bool fix4 = NGGearThrottleOpt.GearThrottleOptPlugin.Enabled;
                int  lvl5 = OptFixes.GetLevel(4);

                var lines = new List<(string Text, Color Color)>
                {
                    (Loc.T("============ NG БЕНЧМАРК ============", "============ NG BENCHMARK ============"), Color.Cyan),
                    (Loc.Ru ? $"Средний FPS: {avgFps:F1}    |    1% Low: {lowFps:F0}" : $"Average FPS: {avgFps:F1}    |    1% Low: {lowFps:F0}", FpsColor(avgFps)),
                    ($"{Loc.OptName}: {(fix1 ? Loc.On : Loc.Off)}", fix1 ? Color.LightGreen : Color.Orange),
                    ($"{Loc.Opt2Name}: {(fix2 ? Loc.On : Loc.Off)}", fix2 ? Color.LightGreen : Color.Orange),
                    ($"{Loc.Opt3Name} [{Loc.Experimental}]: {OptFixes.LevelLabel(2, lvl3)}", lvl3 > 0 ? Color.LightGreen : Color.Gray),
                    ($"{Loc.Opt4Name} [{Loc.Experimental}]: {(fix4 ? Loc.On : Loc.Off)}", fix4 ? Color.LightGreen : Color.Gray),
                    ($"{Loc.Opt5Name} [{Loc.Experimental}]: {OptFixes.LevelLabel(4, lvl5)}", lvl5 > 0 ? Color.LightGreen : Color.Gray),
                    ("", Color.White)
                };

                lines.AddRange(ItemProfiler.ItemsReportLines(10, out List<string> top));
                foreach (string p in top.Take(3))
                {
                    lines.Add(("", Color.White));
                    lines.AddRange(ItemProfiler.PrefabReportLines(p));
                }

                // --- frame subsystems (engine PerformanceCounter): explains drops NOT caused by Item.Update ---
                // RUS: --- подсистемы кадра (движковый PerformanceCounter): объясняет просадки НЕ от Item.Update ---
                lines.Add(("", Color.White));
                lines.Add((Loc.T("===== Подсистемы кадра (средн. мс/кадр; рендер/физика/частицы и пр., не только Item.Update) =====",
                                 "===== Frame subsystems (avg ms/frame; render/physics/particles etc., not just Item.Update) ====="), Color.Cyan));
                var subAll = TopLeaf(_subAll, _subFrames, 8);
                if (subAll.Count == 0)
                {
                    lines.Add((Loc.T("   (нет данных PerformanceCounter)", "   (no PerformanceCounter data)"), Color.Gray));
                }
                else
                {
                    foreach (var kvp in subAll) { lines.Add(($"   {Pad(kvp.Key, 34)} {kvp.Value,7:F3} {Loc.Ms}", MsColor(kvp.Value))); }
                }

                lines.Add(("", Color.White));
                if (_subSlowFrames > 0)
                {
                    lines.Add((Loc.Ru ? $"===== Во время ПРОСАДОК (<50 FPS): {_subSlowFrames} кадр(ов) — что было тяжёлым ====="
                                       : $"===== During DROPS (<50 FPS): {_subSlowFrames} frame(s) — what was heavy =====", Color.Yellow));
                    foreach (var kvp in TopLeaf(_subSlow, _subSlowFrames, 8)) { lines.Add(($"   {Pad(kvp.Key, 34)} {kvp.Value,7:F3} {Loc.Ms}", MsColor(kvp.Value))); }
                    lines.Add((Loc.T("   ^ Draw:* — рендер (свет/частицы); Update:Physics — физика; Update:StatusEffects/Character — скрипты/сущности.",
                                     "   ^ Draw:* — rendering (lights/particles); Update:Physics — physics; Update:StatusEffects/Character — scripts/entities."), Color.Gray));
                }
                else
                {
                    lines.Add((Loc.T("Просадок <50 FPS за этот замер не было. Лови момент дропа и прогони бенчмарк тогда.",
                                     "No <50 FPS drops during this run. Catch a drop and run the benchmark while it happens."), Color.Gray));
                }

                ItemProfiler.ResetStats();   // reset
                AddToHistory(History, NowStamp(), lines); // store this run in the client history   // RUS: сохранить прогон в клиентскую историю
                State = Phase.Done;
                StatusText = Loc.Ru ? $"Готово! Средний FPS {avgFps:F1}. Жми «Копировать»." : $"Done! Average FPS {avgFps:F1}. Click «Copy».";
                ClientPerf.Log(Loc.T("Бенчмарк завершён — результат в окне.", "Benchmark finished — see the window."), Color.LightGreen);
            }
            catch (Exception ex)
            {
                ClientPerf.Log("Benchmark finish error: " + ex.Message, Color.Red);
                State = Phase.Idle; StatusText = Loc.T("Ошибка бенчмарка.", "Benchmark error.");
            }
        }

        // Sample the engine's per-subsystem cost this frame (Update/Draw/Physics/Lighting/Particles/…).
        // RUS: Снять покадровую стоимость подсистем движка (Update/Draw/Physics/Lighting/Particles/…).
        private static void SampleSubsystems(double delta)
        {
            try
            {
                var pc = GameMain.PerformanceCounter;
                if (pc == null) { return; }
                bool slow = delta > 0.02; // < 50 FPS this frame = a noticeable drop   // RUS: <50 FPS в этом кадре = заметная просадка
                _subFrames++;
                if (slow) { _subSlowFrames++; }
                foreach (string id in pc.GetSavedIdentifiers)
                {
                    double ms = pc.GetAverageElapsedMillisecs(id);
                    _subAll[id] = (_subAll.TryGetValue(id, out double a) ? a : 0) + ms;
                    if (slow) { _subSlow[id] = (_subSlow.TryGetValue(id, out double s) ? s : 0) + ms; }
                }
            }
            catch { }
        }

        // Top-N LEAF subsystems by avg ms/frame (excludes aggregate parents like "Update"/"Draw" to avoid
        // double-counting). Returned values are already averaged (sum / frames).
        // RUS: Топ-N ЛИСТОВЫХ подсистем по средн. мс/кадр (без агрегатов «Update»/«Draw», чтобы не дублировать).
        // RUS: Возвращаемые значения уже усреднены (сумма / кадры).
        private static List<KeyValuePair<string, double>> TopLeaf(Dictionary<string, double> acc, int frames, int n)
        {
            var result = new List<KeyValuePair<string, double>>();
            if (frames <= 0 || acc.Count == 0) { return result; }
            var keys = acc.Keys.ToList();
            bool IsLeaf(string k) => !keys.Any(o => o.Length > k.Length && o.StartsWith(k + ":", StringComparison.Ordinal));
            return acc.Where(kvp => IsLeaf(kvp.Key) && (kvp.Value / frames) > 0.001)
                      .OrderByDescending(kvp => kvp.Value)
                      .Take(n)
                      .Select(kvp => new KeyValuePair<string, double>(kvp.Key, kvp.Value / frames))
                      .ToList();
        }

        private static string Pad(string s, int w) => s == null ? new string(' ', w) : (s.Length >= w ? s.Substring(0, w) : s.PadRight(w));
        private static Color MsColor(double ms) => ms >= 4 ? Color.OrangeRed : (ms >= 1.5 ? Color.Yellow : Color.LightGray);
    }

    public static class ItemUpdatePatch
    {
        // out __state — measurement start. <=0 in the postfix => skip (if another mod prefix skipped the original, __state stays 0).
        // RUS: out __state — старт замера. <=0 в постфиксе => пропускаем (если другой мод-префикс пропустил оригинал, __state останется 0).
        public static void Prefix(out long __state) { __state = Stopwatch.GetTimestamp(); }

        public static void Postfix(Item __instance, long __state)
        {
            if (__state <= 0) { return; }
            ItemProfiler.Record(__instance, Stopwatch.GetTimestamp() - __state);
        }
    }

    public static class CompUpdatePatch
    {
        public static void Prefix(out long __state) { __state = Stopwatch.GetTimestamp(); }
        public static void Postfix(ItemComponent __instance, long __state)
        {
            if (__state <= 0) { return; }
            ItemProfiler.RecordComp(__instance, Stopwatch.GetTimestamp() - __state);
        }
    }

    public static class StatusFxPatch
    {
        public static void Prefix(out long __state) { __state = Stopwatch.GetTimestamp(); }
        public static void Postfix(Item __instance, long __state)
        {
            if (__state <= 0) { return; }
            ItemProfiler.RecordPart(__instance, "[StatusEffects]", Stopwatch.GetTimestamp() - __state);
        }
    }

    public static class SoundsPatch
    {
        public static void Prefix(out long __state) { __state = Stopwatch.GetTimestamp(); }
        public static void Postfix(ItemComponent __instance, long __state)
        {
            if (__state <= 0) { return; }
            ItemProfiler.RecordPart(__instance?.Item, "[Sounds]", Stopwatch.GetTimestamp() - __state);
        }
    }
}
