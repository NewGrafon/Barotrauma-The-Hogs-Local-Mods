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

                    // Префикс на клиентский GUI.Update(float): заносим окно меню в GUI-список ДО обработки
                    // ввода и отрисовки (без этого окно либо не рисуется, либо не ловит клики).
                    MethodInfo guiUpdate = AccessTools.Method(typeof(GUI), "Update", new[] { typeof(float) });
                    if (guiUpdate != null)
                    {
                        _harmony.Patch(guiUpdate, prefix: new HarmonyMethod(typeof(GuiUpdatePatch).GetMethod(
                            nameof(GuiUpdatePatch.Prefix), BindingFlags.Static | BindingFlags.Public)));
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
                // нормальный кадр? (отбрасываем первый кадр и большие провалы: загрузка/пауза/сворачивание)
                double safeDelta = (delta > 0 && delta <= 1.0) ? delta : 0;

                bool inRound = GameMain.GameScreen != null && Screen.Selected == GameMain.GameScreen;

                // бенчмарк — КАЖДЫЙ кадр (а не только в раунде).
                // Отрисовку/ввод окна меню (ClientPerfMenu.Update) делаем в префиксе GUI.Update (GuiUpdatePatch).
                Benchmark.Tick(safeDelta, inRound);

                // сэмплинг профайлера — только в раунде и при нормальном кадре
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
        //  Отчёт по всей сессии: топ-20 за сессию + 3 снапшота (худший/типичный/лучший FPS).
        // ----------------------------------------------------------------------------------
        public static void PrintSessionReport()
        {
            // если текущее окно ещё не закрылось, но в нём уже есть данные — учтём его «на лету»
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

            Log(Loc.Ru ? $"--- Топ-{topSess.Count} тяжёлых операций за СЕССИЮ (средн. мс/кадр) ---"
                       : $"--- Top {topSess.Count} heaviest operations over the SESSION (avg ms/frame) ---", Color.White);
            int i = 1;
            foreach (var kvp in topSess) { Log($"  {i,2}. {Pad(kvp.Key, 34)} {kvp.Value,7:F3} {Loc.Ms}", MsColor(kvp.Value)); i++; }

            // --- 3 характерных снапшота ---
            var byFps = _session.OrderBy(x => x.AvgFps).ToList();
            Window worst  = byFps.First();
            Window best   = byFps.Last();
            // «самое среднее среднее»: окно с avgFps ближе всего к среднему по сессии
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
                "NG клиентский профайлер ФПС. Без аргументов — отчёт сессии. Аргументы: menu | now | items | reset | auto | help",
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
                            string sub  = args != null && args.Length > 1 ? args[1] : "";
                            string subl = sub.ToLowerInvariant();
                            if (subl == "on")         { ItemProfiler.Enable(); }
                            else if (subl == "off")   { ItemProfiler.Disable(); Log("Пер-предметный замер ВЫКЛ (патч снят, накладных нет).", Color.Gray); }
                            else if (subl == "reset") { ItemProfiler.ResetStats(); Log("Пер-предметный накопитель очищен.", Color.Yellow); }
                            else if (sub != "")       { ItemProfiler.ReportPrefab(sub); }   // clientperf items spas-13
                            else                      { ItemProfiler.Report(10); }
                            break;
                        }
                        case "menu":  ClientPerfMenu.Toggle(); break;
                        case "help":
                            Log("clientperf menu     — открыть/закрыть окно NG Logger&Optimizations", Color.LightGreen);
                            Log("clientperf          — портрет сессии: топ-20 операций + 3 снапшота (худший/типичный/лучший FPS)", Color.White);
                            Log("clientperf now      — топ операций за текущее (ещё не закрытое) окно", Color.White);
                            Log("clientperf items    — топ ПРЕДМЕТОВ/МОДОВ по времени Item.Update (сначала: clientperf items on)", Color.White);
                            Log("clientperf items <предмет> — РАЗБИВКА предмета по компонентам/эффектам (напр. clientperf items spas-13)", Color.White);
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
            try { DebugConsole.NewMessage(Loc.Tag + text, color); } catch { }
        }
    }

    public static class ClientPerfPatch
    {
        public static void Update_Postfix() => ClientPerf.Tick();
    }

    public static class GuiUpdatePatch
    {
        // Префикс GUI.Update: заносим окно меню в GUI-список ровно перед обработкой ввода + отрисовкой.
        public static void Prefix() => ClientPerfMenu.Update();
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
        private static readonly Dictionary<string, Stat> _parts = new Dictionary<string, Stat>(); // prefab :: часть (Comp:Тип / [StatusEffects] / [Sounds])

        public static void Enable()
        {
            if (_h != null) { return; }
            try
            {
                Harmony h = new Harmony("com.ng.clientperflogger.items");
                BindingFlags sp = BindingFlags.Static | BindingFlags.Public;

                // 1) Item.Update — суммарное время предмета (для топа и для "всего" в разбивке).
                MethodInfo m = AccessTools.Method(typeof(Item), "Update", new[] { typeof(float), typeof(Camera) });
                if (m == null) { ClientPerf.Log(Loc.T("Item.Update не найден — замер недоступен.", "Item.Update not found — profiling unavailable."), Color.Orange); return; }
                h.Patch(m,
                    prefix:  new HarmonyMethod(typeof(ItemUpdatePatch).GetMethod(nameof(ItemUpdatePatch.Prefix),  sp)),
                    postfix: new HarmonyMethod(typeof(ItemUpdatePatch).GetMethod(nameof(ItemUpdatePatch.Postfix), sp)));

                // 2) Item.ApplyStatusEffects — время статус-эффектов предмета (часть [StatusEffects]).
                MethodInfo se = AccessTools.Method(typeof(Item), "ApplyStatusEffects",
                    new[] { typeof(ActionType), typeof(float), typeof(Character), typeof(Limb), typeof(Entity), typeof(bool), typeof(Vector2?) });
                if (se != null)
                {
                    h.Patch(se,
                        prefix:  new HarmonyMethod(typeof(StatusFxPatch).GetMethod(nameof(StatusFxPatch.Prefix),  sp)),
                        postfix: new HarmonyMethod(typeof(StatusFxPatch).GetMethod(nameof(StatusFxPatch.Postfix), sp)));
                }

                // 3) ItemComponent.UpdateSounds — клиентское обновление звуков (вызов в цикле Item.Update; часть [Sounds]).
                MethodInfo us = AccessTools.Method(typeof(ItemComponent), "UpdateSounds");
                if (us != null)
                {
                    h.Patch(us,
                        prefix:  new HarmonyMethod(typeof(SoundsPatch).GetMethod(nameof(SoundsPatch.Prefix),  sp)),
                        postfix: new HarmonyMethod(typeof(SoundsPatch).GetMethod(nameof(SoundsPatch.Postfix), sp)));
                }

                // 4) Все override-ы ItemComponent.Update(float,Camera) — время по ТИПУ компонента (части Comp:Тип).
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
            catch (Exception ex) { ClientPerf.Log("Не удалось включить замер: " + ex.Message, Color.Red); _h = null; }
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

        // Топ предметов: строит строки (текст + цвет) и заодно возвращает упорядоченный список prefab-id (для бенчмарка).
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

        // Тот же отчёт обычным текстом (для окна бенчмарка / копирования).
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

        // Разбивка КОНКРЕТНОГО предмета по частям (компоненты / статус-эффекты / звуки / прочее).
        public static List<(string Text, Color Color)> PrefabReportLines(string filter)
        {
            var lines = new List<(string Text, Color Color)>();
            // точное совпадение (бенчмарк передаёт готовый prefab-id) -> иначе подстрока (топ-3 по времени)
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
    //  БЕНЧМАРК: одной кнопкой прогоняет последовательность замера и собирает результат в текст.
    //  Последовательность: items off -> reset -> on -> ЖДАТЬ 60с (в раунде) -> off ->
    //  общий отчёт (топ-10 + топ-5 модов) -> разбивка топ-3 предметов -> reset. Покадрово из ClientPerf.Tick.
    // ==========================================================================================
    public static class Benchmark
    {
        public enum Phase { Idle, Measuring, Done }
        public static Phase State { get; private set; } = Phase.Idle;
        public static string StatusText { get; private set; }
        public static string ResultText { get; private set; } = "";                  // plain-text для копирования
        public static List<(string Text, Color Color)> ResultLines { get; private set; } = new List<(string Text, Color Color)>(); // цветные строки для окна
        public static bool ResultIsNew;   // флаг для GUI: появился свежий результат — пора перерисовать

        static Benchmark() { StatusText = Loc.T("Готов. Нажми «Бенчмарк 60с».", "Ready. Click «Benchmark 60s»."); }

        private const double DurationSec = 60.0;
        private static double _left;
        // времена КАЖДОГО кадра за замер -> точные средний FPS и 1% Low (среднее худшего 1% кадров)
        private static readonly List<double> _frameTimes = new List<double>(16384);

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
                State = Phase.Measuring;
                StatusText = Loc.T("Замер… 60с (стой возле нагрузки, не закрывай игру)", "Measuring… 60s (stay near the load, keep the game running)");
                ClientPerf.Log(Loc.T("Бенчмарк начат: 60с замера Item.Update.", "Benchmark started: 60s of Item.Update profiling."), Color.Cyan);
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

            _frameTimes.Add(delta); // время этого кадра (для среднего FPS и 1% Low)

            _left -= delta;
            if (_left <= 0) { Finish(); return; }
            StatusText = Loc.Ru ? $"Замер… осталось {Math.Ceiling(_left):F0}с" : $"Measuring… {Math.Ceiling(_left):F0}s left";
        }

        private static Color FpsColor(double fps) => fps >= 50 ? Color.LightGreen : (fps >= 30 ? Color.Yellow : Color.OrangeRed);

        private static void Finish()
        {
            try
            {
                ItemProfiler.Disable();   // off (данные сохраняются)

                double avgFps = 0, lowFps = 0;
                if (_frameTimes.Count > 0)
                {
                    double total = 0;
                    foreach (double ft in _frameTimes) { total += ft; }
                    avgFps = total > 0 ? _frameTimes.Count / total : 0;
                    // 1% Low = средний FPS худшего 1% кадров (по самым долгим временам кадра)
                    var slowest = _frameTimes.OrderByDescending(x => x).ToList();
                    int n = Math.Max(1, (int)(slowest.Count * 0.01));
                    double worst = 0;
                    for (int i = 0; i < n; i++) { worst += slowest[i]; }
                    lowFps = worst > 0 ? n / worst : 0;
                }
                bool fix1 = NGContainerOpt.ContainedEffectsOptPlugin.Enabled;
                bool fix2 = NGNearbyOpt.NearbyTargetsOptPlugin.Enabled;

                var lines = new List<(string Text, Color Color)>
                {
                    (Loc.T("============ NG БЕНЧМАРК (60 секунд) ============", "============ NG BENCHMARK (60 seconds) ============"), Color.Cyan),
                    (Loc.Ru ? $"Средний FPS: {avgFps:F1}    |    1% Low: {lowFps:F0}" : $"Average FPS: {avgFps:F1}    |    1% Low: {lowFps:F0}", FpsColor(avgFps)),
                    ($"{Loc.OptName}: {(fix1 ? Loc.On : Loc.Off)}", fix1 ? Color.LightGreen : Color.Orange),
                    ($"{Loc.Opt2Name}: {(fix2 ? Loc.On : Loc.Off)}", fix2 ? Color.LightGreen : Color.Orange),
                    ("", Color.White)
                };

                lines.AddRange(ItemProfiler.ItemsReportLines(10, out List<string> top));
                foreach (string p in top.Take(3))
                {
                    lines.Add(("", Color.White));
                    lines.AddRange(ItemProfiler.PrefabReportLines(p));
                }

                ItemProfiler.ResetStats();   // reset
                ResultLines = lines;
                ResultText  = string.Join("\n", lines.Select(l => l.Text));
                ResultIsNew = true;
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
