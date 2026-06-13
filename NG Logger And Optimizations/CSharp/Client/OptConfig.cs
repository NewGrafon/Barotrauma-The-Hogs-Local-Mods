using System;
using System.Linq;
using System.Reflection;
using Barotrauma;
using HarmonyLib;
using Microsoft.Xna.Framework;

namespace NetEventLogger
{
    // The "init file": persists the fix toggle states (config) and (re)applies them — at mod load AND a
    // couple seconds after each round start (i.e. after Performance Enhancement has activated). If the
    // config is missing it is generated with both fixes ON by default. Client-only: this is the local
    // player's preference; the SERVER always keeps the fixes on (default).
    // RUS: «Файл инициализации»: хранит состояние тумблеров фиксов (конфиг) и (пере)применяет их — при
    // RUS: загрузке мода И через пару секунд после старта каждого раунда (т.е. после активации Performance
    // RUS: Enhancement). Если конфига нет — генерируется с обоими фиксами ВКЛ по умолчанию. Только клиент:
    // RUS: это локальная настройка игрока; СЕРВЕР всегда держит фиксы включёнными (по умолчанию).
    public static class OptConfig
    {
        public static bool Fix1 = true;  // container optimization          // RUS: оптимизация контейнеров
        public static bool Fix2 = true;  // nearby-item search optimization // RUS: оптимизация поиска предметов рядом
        public static bool Fix3 = true;  // auto-RepairTool throttling (on by default; UI still labels it "experimental") // RUS: троттлинг авто-RepairTool (по умолчанию вкл; в UI ещё помечен «эксперим.»)
        public static bool Fix4 = true;  // tactical-gear turret throttle + dead-wearer skip (on by default; UI "experimental") // RUS: троттлинг турелей снаряжения + скип на трупах (по умолч. вкл; в UI «эксперим.»)

        // Apply our fixes this long AFTER Performance Enhancement becomes active (it applies mid-round,
        // when its on-screen banner appears — detected via its C# bridge stats, not a fixed delay).
        // RUS: Применяем наши фиксы через столько ПОСЛЕ активации Performance Enhancement (он применяется
        // RUS: уже в раунде, когда появляется его экранная плашка — ловим через его C#-мост, а не фикс. задержку).
        private const double PostPEDelaySec  = 2.0;
        private const double MaxWaitSec      = 25.0; // fallback: apply anyway if PE never detected   // RUS: фоллбэк: применить всё равно, если PE так и не обнаружен
        private const double PollIntervalSec = 0.3;

        private static bool   _loaded;
        private static string _path;
        private static bool   _wasInRound;
        private static bool   _waiting;       // waiting (after round start) to apply   // RUS: ждём (после старта раунда) применения
        private static bool   _peSeen;        // PE detected active this round           // RUS: PE замечен активным в этом раунде
        private static double _waitElapsed, _postPe, _pollAcc;

        // --- Performance Enhancement bridge (reflection; PE's own Lua uses the same type) ---
        // RUS: --- мост Performance Enhancement (рефлексия; Lua самого PE использует тот же тип) ---
        private static bool       _peResolved;
        private static MethodInfo _peStats;
        private static bool       _peAbsent;

        // Called once from the client plugin's Initialize.
        // RUS: Зовётся один раз из Initialize клиентского плагина.
        public static void Init()
        {
            Load();
            ApplyToPlugins();
            try { ClientOptNet.Init(); } catch { } // client-side net (server-state sync) + `ngopt` command   // RUS: клиентская сеть (синхр. серверного состояния) + команда `ngopt`
            ClientPerf.Log(Loc.Ru
                ? $"Конфиг фиксов загружен: контейнеры={(Fix1 ? "ВКЛ" : "ВЫКЛ")}, поиск рядом={(Fix2 ? "ВКЛ" : "ВЫКЛ")}, авто-тулы(эксп.)={(Fix3 ? "ВКЛ" : "ВЫКЛ")}, снаряжение(эксп.)={(Fix4 ? "ВКЛ" : "ВЫКЛ")}."
                : $"Fix config loaded: containers={(Fix1 ? "ON" : "OFF")}, nearby-search={(Fix2 ? "ON" : "OFF")}, auto-tools(exp.)={(Fix3 ? "ON" : "OFF")}, gear(exp.)={(Fix4 ? "ON" : "OFF")}.", Color.Gray);
        }

        private static string ConfigPath()
        {
            if (_path != null) { return _path; }
            try
            {
                var pkg = ContentPackageManager.EnabledPackages.All.FirstOrDefault(p => p != null && p.Name == "NG Logger And Optimizations");
                string dir = pkg?.Dir;
                if (!string.IsNullOrEmpty(dir)) { _path = System.IO.Path.Combine(dir, "ngopt_config.txt"); }
            }
            catch { }
            return _path;
        }

        public static void Load()
        {
            _loaded = true;
            Fix1 = true; Fix2 = true; Fix3 = true; Fix4 = true; // defaults if anything fails   // RUS: значения по умолчанию, если что-то не так
            try
            {
                string path = ConfigPath();
                if (path == null) { return; }
                if (!Barotrauma.IO.File.Exists(path)) { Save(); return; } // no config -> generate default (1-2 ON, 3 experimental)   // RUS: конфига нет -> дефолт (1-2 ВКЛ, 3 эксперим.)
                foreach (string line in Barotrauma.IO.File.ReadAllLines(path))
                {
                    string s = line.Trim();
                    if (s.StartsWith("fix1=", StringComparison.OrdinalIgnoreCase)) { Fix1 = ParseBool(s.Substring(5), true); }
                    else if (s.StartsWith("fix2=", StringComparison.OrdinalIgnoreCase)) { Fix2 = ParseBool(s.Substring(5), true); }
                    else if (s.StartsWith("fix3=", StringComparison.OrdinalIgnoreCase)) { Fix3 = ParseBool(s.Substring(5), true); }
                    else if (s.StartsWith("fix4=", StringComparison.OrdinalIgnoreCase)) { Fix4 = ParseBool(s.Substring(5), true); }
                }
            }
            catch { Fix1 = true; Fix2 = true; Fix3 = true; Fix4 = true; }
        }

        private static bool ParseBool(string s, bool def)
        {
            s = s.Trim().ToLowerInvariant();
            if (s == "true" || s == "1" || s == "on")   { return true; }
            if (s == "false" || s == "0" || s == "off") { return false; }
            return def;
        }

        public static void Save()
        {
            try
            {
                string path = ConfigPath();
                if (path == null) { return; }
                Barotrauma.IO.File.WriteAllText(path,
                    "fix1=" + (Fix1 ? "true" : "false") + "\r\nfix2=" + (Fix2 ? "true" : "false") + "\r\nfix3=" + (Fix3 ? "true" : "false") + "\r\nfix4=" + (Fix4 ? "true" : "false") + "\r\n");
            }
            catch { }
        }

        // Apply the stored states to the (client) fix plugins. SetEnabled is a no-op if already in that state.
        // RUS: Применить сохранённые состояния к (клиентским) фикс-плагинам. SetEnabled — no-op, если уже в этом состоянии.
        public static void ApplyToPlugins()
        {
            try { NGContainerOpt.ContainedEffectsOptPlugin.SetEnabled(Fix1); } catch { }
            try { NGNearbyOpt.NearbyTargetsOptPlugin.SetEnabled(Fix2); } catch { }
            try { NGRepairToolOpt.RepairToolThrottleOptPlugin.SetEnabled(Fix3); } catch { }
            try { NGGearThrottleOpt.GearThrottleOptPlugin.SetEnabled(Fix4); } catch { }
        }

        // Round-start variant: ensure the enabled-state matches config AND re-process the loaded world
        // (idempotent). This is the "apply fixes at every round start" behaviour.
        // RUS: Вариант для старта раунда: привести флаги к конфигу И заново обработать загруженный мир
        // RUS: (идемпотентно). Это и есть «применять фиксы на каждом старте раунда».
        private static void ForceReapplyToWorld()
        {
            ApplyToPlugins();
            try { if (Fix1) { NGContainerOpt.TrimSpentEffectsPatch.TrimAllContainers(); } } catch { }
            try { if (Fix2) { NGNearbyOpt.NearbyTargetsIndex.PopulateExisting(); } } catch { }
        }

        // Called by the menu toggles -> change state, apply immediately, persist to config.
        // RUS: Зовётся тумблерами меню -> сменить состояние, применить сразу, сохранить в конфиг.
        public static void SetFix1(bool on) { if (!_loaded) { Load(); } Fix1 = on; try { NGContainerOpt.ContainedEffectsOptPlugin.SetEnabled(on); } catch { } Save(); }
        public static void SetFix2(bool on) { if (!_loaded) { Load(); } Fix2 = on; try { NGNearbyOpt.NearbyTargetsOptPlugin.SetEnabled(on); } catch { } Save(); }
        public static void SetFix3(bool on) { if (!_loaded) { Load(); } Fix3 = on; try { NGRepairToolOpt.RepairToolThrottleOptPlugin.SetEnabled(on); } catch { } Save(); }
        public static void SetFix4(bool on) { if (!_loaded) { Load(); } Fix4 = on; try { NGGearThrottleOpt.GearThrottleOptPlugin.SetEnabled(on); } catch { } Save(); }

        // Set a CLIENT fix by index (0..2) — applies + persists. Used by the menu and the `ngopt client` command.
        // RUS: Установить КЛИЕНТСКИЙ фикс по индексу (0..2) — применяет + сохраняет. Для меню и команды `ngopt client`.
        public static void SetFixByIndex(int i, bool on)
        {
            switch (i) { case 0: SetFix1(on); break; case 1: SetFix2(on); break; case 2: SetFix3(on); break; case 3: SetFix4(on); break; }
        }
        public static bool GetFixByIndex(int i) => OptFixes.GetEnabled(i);

        // Per-frame from ClientPerf.Tick: re-apply the config ~3s after each round start (after PE).
        // RUS: Покадрово из ClientPerf.Tick: переприменить конфиг через ~3с после старта раунда (после PE).
        public static void Tick(double delta, bool inRound)
        {
            try
            {
                if (inRound && !_wasInRound) // round just started -> begin waiting for PE to activate
                {
                    // RUS: раунд только начался -> начинаем ждать активации PE
                    _waiting = true; _peSeen = false; _waitElapsed = 0; _postPe = -1; _pollAcc = 0;
                    try { ClientOptNet.RequestServerState(); } catch { } // refresh the server-state display   // RUS: обновить показ серверного состояния
                }
                _wasInRound = inRound;

                if (!_waiting) { return; }
                if (!inRound) { _waiting = false; return; } // round ended before we applied   // RUS: раунд кончился раньше, чем применили

                _waitElapsed += delta;

                if (!_peSeen)
                {
                    _pollAcc += delta;
                    if (_pollAcc >= PollIntervalSec)
                    {
                        _pollAcc = 0;
                        if (IsPEActive()) { _peSeen = true; _postPe = PostPEDelaySec; } // PE activated -> wait a bit more   // RUS: PE активировался -> ждём ещё чуть-чуть
                    }
                    if (!_peSeen && _waitElapsed >= MaxWaitSec) // fallback: PE never detected
                    {
                        _waiting = false;
                        ForceReapplyToWorld();
                        ClientPerf.Log(Loc.T("Фиксы применены (PE не обнаружен, по таймауту).", "Fixes applied (PE not detected, by timeout)."), Color.Gray);
                    }
                }
                else
                {
                    _postPe -= delta;
                    if (_postPe <= 0)
                    {
                        _waiting = false;
                        ForceReapplyToWorld();
                        ClientPerf.Log(Loc.T("Фиксы применены (после активации Performance Enhancement).", "Fixes applied (after Performance Enhancement activated)."), Color.Gray);
                    }
                }
            }
            catch { _waiting = false; }
        }

        // Is Performance Enhancement actively throttling items right now? (i.e. its banner has appeared)
        // Detected via its C# bridge GetItemUpdateStatsJson -> scheduledActiveItems > 0. If PE isn't
        // installed at all, returns true so we don't wait for something that will never come.
        // RUS: Активно ли сейчас Performance Enhancement троттлит предметы? (т.е. плашка появилась)
        // RUS: Ловим через его C#-мост GetItemUpdateStatsJson -> scheduledActiveItems > 0. Если PE вообще
        // RUS: не установлен — возвращаем true, чтобы не ждать того, чего не будет.
        private static bool IsPEActive()
        {
            try
            {
                if (!_peResolved)
                {
                    _peResolved = true;
                    Type t = AccessTools.TypeByName("PerformanceEnhancement.PerformanceEnhancementBridge");
                    if (t != null) { _peStats = AccessTools.Method(t, "GetItemUpdateStatsJson"); }
                    _peAbsent = (_peStats == null);
                }
                if (_peAbsent) { return true; } // PE not present -> don't block   // RUS: PE нет -> не блокируем
                string json = _peStats.Invoke(null, null) as string;
                if (string.IsNullOrEmpty(json)) { return false; }
                return JsonIntPositive(json, "scheduledActiveItems") || JsonIntPositive(json, "actualUpdatedActiveItems");
            }
            catch { return true; } // on any error, don't block forever   // RUS: при любой ошибке не блокируем навсегда
        }

        // Tiny JSON helper: is integer field "key" > 0 ? (avoids pulling a JSON library).
        // RUS: Крошечный JSON-хелпер: целое поле "key" > 0 ? (чтобы не тащить JSON-библиотеку).
        private static bool JsonIntPositive(string json, string key)
        {
            try
            {
                int i = json.IndexOf("\"" + key + "\"", StringComparison.Ordinal);
                if (i < 0) { return false; }
                i = json.IndexOf(':', i);
                if (i < 0) { return false; }
                i++;
                while (i < json.Length && (json[i] == ' ' || json[i] == '\t')) { i++; }
                int start = i;
                while (i < json.Length && (char.IsDigit(json[i]) || json[i] == '-')) { i++; }
                return i > start && int.TryParse(json.Substring(start, i - start), out int v) && v > 0;
            }
            catch { return false; }
        }
    }
}
