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
        public static int  Fix3Level = 1; // auto-RepairTool throttle level: 0=off,1=50%,2=33%,3=25% (default 50%) // RUS: уровень троттлинга авто-RepairTool: 0=выкл,1=50%,2=33%,3=25% (по умолч. 50%)
        public static bool Fix4 = true;  // tactical-gear turret throttle + dead-wearer skip (on by default; UI "experimental") // RUS: троттлинг турелей снаряжения + скип на трупах (по умолч. вкл; в UI «эксперим.»)
        public static int  Fix5Level = 0; // trigger-light throttle level: 0=off,1=50%,2=33%,3=25% (default off) // RUS: уровень троттлинга ламп-тикалок: 0=выкл,1=50%,2=33%,3=25% (по умолч. выкл)
        public static bool AutoLog = false; // "Console logs": client FPS auto-summary every 60s (off by default) // RUS: «Конс. логи»: авто-сводка FPS клиента каждые 60с (по умолч. выкл)

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
                ? $"Конфиг фиксов: контейнеры={(Fix1 ? "ВКЛ" : "ВЫКЛ")}, поиск={(Fix2 ? "ВКЛ" : "ВЫКЛ")}, авто-тулы={OptFixes.LevelLabel(2, Fix3Level)}, снаряжение={(Fix4 ? "ВКЛ" : "ВЫКЛ")}, лампы-тикалки={OptFixes.LevelLabel(4, Fix5Level)}."
                : $"Fix config: containers={(Fix1 ? "ON" : "OFF")}, nearby={(Fix2 ? "ON" : "OFF")}, auto-tools={OptFixes.LevelLabel(2, Fix3Level)}, gear={(Fix4 ? "ON" : "OFF")}, trigger-lights={OptFixes.LevelLabel(4, Fix5Level)}.", Color.Gray);
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
            // Baseline defaults — used for any key NOT found in the file. The WEAK optimizations (fixes 3 & 5)
            // default OFF when Performance Enhancement is installed (it already throttles item updates — see the
            // fix tooltips). Applied PER-KEY: a config that has only one of the weak fixes still gets the correct
            // PE-aware default for the missing one. A key PRESENT in the file overrides this during parsing below.
            // RUS: Базовые дефолты — для любого ключа, которого НЕТ в файле. «Слабые» оптимизации (фиксы 3 и 5)
            // RUS: по умолчанию ВЫКЛ, если установлен Performance Enhancement (он уже троттлит апдейты предметов —
            // RUS: см. тултипы). Применяется ПОКЛЮЧЕВО: конфиг с одним из слабых фиксов всё равно получит верный
            // RUS: PE-дефолт для отсутствующего. Ключ, ПРИСУТСТВУЮЩИЙ в файле, перекроет это при разборе ниже.
            bool pe = IsPEInstalled();
            Fix1 = true; Fix2 = true; Fix4 = true; AutoLog = false;
            Fix3Level = pe ? 0 : 1; // weak: 50% normally, OFF under PE   // RUS: слабый: обычно 50%, под PE — ВЫКЛ
            Fix5Level = 0;          // weak: OFF by default regardless of PE   // RUS: слабый: по умолчанию ВЫКЛ независимо от PE
            try
            {
                string path = ConfigPath();
                if (path == null) { return; }
                if (!Barotrauma.IO.File.Exists(path)) { Save(); return; } // no config -> write the PE-aware defaults   // RUS: конфига нет -> записать дефолты с учётом PE
                foreach (string line in Barotrauma.IO.File.ReadAllLines(path))
                {
                    string s = line.Trim();
                    if (s.StartsWith("fix1=", StringComparison.OrdinalIgnoreCase)) { Fix1 = ParseBool(s.Substring(5), true); }
                    else if (s.StartsWith("fix2=", StringComparison.OrdinalIgnoreCase)) { Fix2 = ParseBool(s.Substring(5), true); }
                    else if (s.StartsWith("fix3=", StringComparison.OrdinalIgnoreCase)) { Fix3Level = ParseLevel(s.Substring(5), 1); }
                    else if (s.StartsWith("fix4=", StringComparison.OrdinalIgnoreCase)) { Fix4 = ParseBool(s.Substring(5), true); }
                    else if (s.StartsWith("fix5=", StringComparison.OrdinalIgnoreCase)) { Fix5Level = ParseLevel(s.Substring(5), 0); }
                    else if (s.StartsWith("autolog=", StringComparison.OrdinalIgnoreCase)) { AutoLog = ParseBool(s.Substring(8), false); }
                }
                Save(); // rewrite the file so keys missing from an older-version config get added (with their defaults)   // RUS: переписать файл, чтобы ключи, которых не было в конфиге старой версии, дописались (со своими дефолтами)
            }
            catch { Fix1 = true; Fix2 = true; Fix4 = true; Fix5Level = 0; AutoLog = false; Fix3Level = pe ? 0 : 1; }
        }

        private static bool ParseBool(string s, bool def)
        {
            s = s.Trim().ToLowerInvariant();
            if (s == "true" || s == "1" || s == "on")   { return true; }
            if (s == "false" || s == "0" || s == "off") { return false; }
            return def;
        }

        // Parse a throttle level (0..3). Accepts true/on -> 1, false/off -> 0, or an integer.
        // RUS: Разобрать уровень троттлинга (0..3). true/on -> 1, false/off -> 0, либо целое.
        private static int ParseLevel(string s, int def)
        {
            s = s.Trim().ToLowerInvariant();
            if (s == "true" || s == "on")  { return 1; }
            if (s == "false" || s == "off") { return 0; }
            if (int.TryParse(s, out int v)) { return v < 0 ? 0 : (v > 3 ? 3 : v); }
            return def;
        }

        public static void Save()
        {
            try
            {
                string path = ConfigPath();
                if (path == null) { return; }
                Barotrauma.IO.File.WriteAllText(path,
                    "fix1=" + (Fix1 ? "true" : "false") + "\r\nfix2=" + (Fix2 ? "true" : "false") + "\r\nfix3=" + Fix3Level + "\r\nfix4=" + (Fix4 ? "true" : "false") + "\r\nfix5=" + Fix5Level + "\r\nautolog=" + (AutoLog ? "true" : "false") + "\r\n");
            }
            catch { }
        }

        // Apply the stored states to the (client) fix plugins. SetEnabled is a no-op if already in that state.
        // RUS: Применить сохранённые состояния к (клиентским) фикс-плагинам. SetEnabled — no-op, если уже в этом состоянии.
        public static void ApplyToPlugins()
        {
            try { NGContainerOpt.ContainedEffectsOptPlugin.SetEnabled(Fix1); } catch { }
            try { NGNearbyOpt.NearbyTargetsOptPlugin.SetEnabled(Fix2); } catch { }
            try { NGRepairToolOpt.RepairToolThrottleOptPlugin.SetLevel(Fix3Level); } catch { }
            try { NGGearThrottleOpt.GearThrottleOptPlugin.SetEnabled(Fix4); } catch { }
            try { NGLightThrottleOpt.LightTriggerThrottleOptPlugin.SetLevel(Fix5Level); } catch { }
            try { ClientPerf.AutoLog = AutoLog; } catch { } // "Console logs": client FPS auto-summary   // RUS: «Конс. логи»: авто-сводка FPS клиента
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
        public static void SetFix3(bool on) { if (!_loaded) { Load(); } Fix3Level = on ? 1 : 0; try { NGRepairToolOpt.RepairToolThrottleOptPlugin.SetLevel(Fix3Level); } catch { } Save(); }
        public static void SetFix4(bool on) { if (!_loaded) { Load(); } Fix4 = on; try { NGGearThrottleOpt.GearThrottleOptPlugin.SetEnabled(on); } catch { } Save(); }
        public static void SetFix5(bool on) { if (!_loaded) { Load(); } Fix5Level = on ? 1 : 0; try { NGLightThrottleOpt.LightTriggerThrottleOptPlugin.SetLevel(Fix5Level); } catch { } Save(); }

        // "Console logs" client check: client FPS auto-summary every 60s. Applies + persists (ngopt_config.txt).
        // RUS: Клиентская проверка «Конс. логи»: авто-сводка FPS каждые 60с. Применяет + сохраняет (ngopt_config.txt).
        public static void SetAutoLog(bool on) { if (!_loaded) { Load(); } AutoLog = on; try { ClientPerf.AutoLog = AutoLog; } catch { } Save(); }

        // Cycle fix #i to its next level on THIS client (off -> ... -> max -> off), apply + persist.
        // RUS: Прокрутить фикс #i на следующий уровень на ЭТОМ клиенте (выкл -> ... -> макс -> выкл), применить + сохранить.
        public static int CycleClient(int i)
        {
            if (!_loaded) { Load(); }
            int n = OptFixes.CycleLevel(i); // applies to the plugin in this process
            StoreLevel(i, n);
            Save();
            return n;
        }

        private static void StoreLevel(int i, int lvl)
        {
            switch (i)
            {
                case 0: Fix1 = lvl > 0; break;
                case 1: Fix2 = lvl > 0; break;
                case 2: Fix3Level = lvl; break;
                case 3: Fix4 = lvl > 0; break;
                case 4: Fix5Level = lvl; break;
            }
        }

        // Set a CLIENT fix by index (0..2) — applies + persists. Used by the menu and the `ngopt client` command.
        // RUS: Установить КЛИЕНТСКИЙ фикс по индексу (0..2) — применяет + сохраняет. Для меню и команды `ngopt client`.
        public static void SetFixByIndex(int i, bool on)
        {
            switch (i) { case 0: SetFix1(on); break; case 1: SetFix2(on); break; case 2: SetFix3(on); break; case 3: SetFix4(on); break; case 4: SetFix5(on); break; }
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
        // Is the Performance Enhancement mod installed (its C# bridge type is present)? Resolved once and
        // cached. Used at first-run config generation to avoid defaulting the weak optimizations ON.
        // RUS: Установлен ли мод Performance Enhancement (есть его C#-мост)? Резолвится один раз и кэшируется.
        // RUS: Нужно при генерации первого конфига, чтобы не включать «слабые» оптимизации по умолчанию.
        private static bool IsPEInstalled()
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
                return !_peAbsent;
            }
            catch { return false; }
        }

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
