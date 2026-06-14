using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using Barotrauma;
using Barotrauma.Items.Components;
using HarmonyLib;
using Microsoft.Xna.Framework;

namespace NGRepairToolOpt
{
    // ==========================================================================================
    //  FIX 3 (EXPERIMENTAL): throttle AUTO-triggered RepairTool.Use to ~half the server tickrate.
    //
    //  Problem (profiler: ekutility_portablespinkler Comp:LightComponent ~262µs/frame): an "auto"
    //  RepairTool (sprinkler, auto-welder, ...) is driven by a LightComponent OnActive <UseItem/>
    //  status effect that calls item.Use() EVERY frame (~60/s). RepairTool.Use does 2 physics raycasts
    //  (Submarine.PickBody obstacle check + Submarine.PickBodies repair ray) + hull lookups + fire-source
    //  search + particle emission — per frame, per placed tool. That's the ~262µs.
    //
    //  Solution (output-preserving): skip most Use calls and ACCUMULATE their deltaTime; once the
    //  accumulated time reaches the interval (= 2 / tickrate, i.e. half the tickrate), run ONE Use with
    //  the accumulated deltaTime. RepairTool's extinguish/water/fire all scale LINEARLY by deltaTime
    //  (FireSource.Extinguish(deltaTime,..), seed.Health += WaterAmount*deltaTime, FireProbability*deltaTime),
    //  so the TOTAL effect over time is unchanged — only the raycast/particle FREQUENCY drops (~3x fewer at
    //  tickrate 40). Manual use by a player (character != null) is NEVER throttled (must stay responsive).
    //  Interval clamped <= 0.09s so RepairTool.activeTimer (0.1, reset on each Use) never expires -> the
    //  looping spray sound stays alive.
    //
    //  MP-safe: Shared (client+server); each machine throttles its own local Use calls; the accumulated
    //  deltaTime preserves output; fire state is server-authoritative anyway. ON by default (client+server,
    //  validated without regressions); still labelled "experimental" in the menu (Shared tab) until proven in co-op.
    //
    //  RUS: ФИКС 3 (ЭКСПЕРИМЕНТАЛЬНЫЙ): троттлинг АВТО-срабатывающего RepairTool.Use до ~половины тикрейта.
    //  RUS: Проблема (профайлер: ekutility_portablespinkler Comp:LightComponent ~262µs/кадр): «авто»-RepairTool
    //  RUS: (спринклер, авто-сварщик, ...) гоняется через OnActive-эффект <UseItem/> у LightComponent, который
    //  RUS: зовёт item.Use() КАЖДЫЙ кадр (~60/с). RepairTool.Use делает 2 физ. рейкаста (PickBody проверка
    //  RUS: препятствий + PickBodies луч ремонта) + поиск халла + поиск источников огня + эмиссию партиклов —
    //  RUS: каждый кадр на каждый поставленный инструмент. Это и есть ~262µs.
    //  RUS: Решение (с сохранением вывода): пропускаем большинство вызовов Use и НАКАПЛИВАЕМ их deltaTime; когда
    //  RUS: накопленное достигнет интервала (= 2 / тикрейт, т.е. половина тикрейта) — выполняем ОДИН Use с
    //  RUS: накопленным deltaTime. Тушение/полив/огонь у RepairTool линейны по deltaTime, поэтому СУММАРНЫЙ
    //  RUS: эффект не меняется — падает лишь ЧАСТОТА рейкастов/партиклов (~×3 реже при тикрейте 40). Ручное
    //  RUS: использование игроком (character != null) НЕ троттлится. Интервал клампится <= 0.09с, чтобы
    //  RUS: activeTimer (0.1, сбрасывается каждым Use) не истёк -> луп-звук распыления остаётся живым.
    //  RUS: MP-safe: Shared (клиент+сервер); каждая машина троттлит свои локальные вызовы Use; накопленный
    //  RUS: deltaTime сохраняет вывод; состояние огня всё равно серверно-авторитетно. ЭКСПЕРИМ. + по умолчанию
    //  RUS: ВЫКЛ: включается вручную в меню мода (вкладка «Общие»).
    // ==========================================================================================
    public sealed class RepairToolThrottleOptPlugin : IAssemblyPlugin
    {
        // Validated (no audio/visual regression in testing) -> ON by default, like fix1/fix2. The SERVER also
        // uses this default (no menu there) -> the throttle now runs on the server too, saving server CPU on the
        // per-frame raycasts (relevant to net lag). Still labelled "experimental" in the UI until proven in real co-op.
        // RUS: Проверено (без аудио/визуальных регрессий) -> по умолчанию ВКЛ, как фикс1/фикс2. СЕРВЕР тоже
        // RUS: использует этот дефолт (меню там нет) -> троттлинг теперь работает и на сервере, экономя CPU на
        // RUS: покадровых рейкастах (важно для сетевого лага). В UI пока помечен «эксперим.» до проверки в реальном ко-опе.
        // Throttle level: 0=off, 1=50% (run Use every 2 ticks), 2=33% (every 3), 3=25% (every 4).
        // Default 1 (50%) = the original behaviour. Enabled mirrors level>0 for the registry/UI.
        // RUS: Уровень троттлинга: 0=выкл, 1=50% (Use раз в 2 тика), 2=33% (раз в 3), 3=25% (раз в 4).
        // RUS: По умолчанию 1 (50%) = прежнее поведение. Enabled = level>0 для реестра/UI.
        public static int Level = 1;
        public static bool Enabled => Level > 0;

        private static Harmony _h;

        public void PreInitPatching() { }

        public void Initialize()
        {
            try
            {
                if (_h != null) { return; }
                _h = new Harmony("ng.repairtoolthrottleopt");
                BindingFlags sp = BindingFlags.Static | BindingFlags.Public;

                MethodInfo use = AccessTools.Method(typeof(RepairTool), "Use", new[] { typeof(float), typeof(Character) });
                if (use == null)
                {
                    DebugConsole.NewMessage(NetEventLogger.Loc.Tag + NetEventLogger.Loc.T(
                        "ФИКС 3: RepairTool.Use не найден — троттлинг недоступен.",
                        "FIX 3: RepairTool.Use not found — throttling unavailable."), Color.Orange);
                    return;
                }
                _h.Patch(use, prefix: new HarmonyMethod(typeof(RepairToolThrottlePatch).GetMethod(nameof(RepairToolThrottlePatch.Prefix), sp)));
                DebugConsole.NewMessage(NetEventLogger.Loc.Tag + NetEventLogger.Loc.T(
                    "ФИКС 3 готов: троттлинг авто-RepairTool до ~половины тикрейта (по умолчанию ВКЛ; клиент+сервер).",
                    "FIX 3 ready: auto-RepairTool throttling to ~half the tickrate (ON by default; client+server)."), Color.LightGreen);
            }
            catch (Exception ex)
            {
                DebugConsole.NewMessage(NetEventLogger.Loc.Tag + "FIX 3 init error: " + ex.Message, Color.Red);
            }
        }

        public void OnLoadCompleted() { }

        public void Dispose()
        {
            try { _h?.UnpatchSelf(); } catch { }
            _h = null;
        }

        // Live toggle (called from the client menu). Instant — the prefix respects the flag immediately.
        // RUS: Переключение на лету (зовётся из клиентского меню). Мгновенно — префикс сразу уважает флаг.
        public static void SetEnabled(bool on) { Level = on ? 1 : 0; }
        public static void SetLevel(int lvl) { Level = lvl < 0 ? 0 : (lvl > 3 ? 3 : lvl); }
    }

    public static class RepairToolThrottlePatch
    {
        // per-RepairTool accumulated deltaTime of skipped frames (GC-friendly: auto-dropped when the component is collected)
        // RUS: накопленный deltaTime пропущенных кадров на каждый RepairTool (GC-friendly: исчезает при сборке компонента)
        private static readonly ConditionalWeakTable<RepairTool, StrongBox<float>> _acc = new ConditionalWeakTable<RepairTool, StrongBox<float>>();

        // keep < RepairTool.activeTimer (0.1) so the looping spray sound never cuts out
        // RUS: держим < activeTimer (0.1) у RepairTool, чтобы луп-звук распыления не оборвался
        private const float MaxInterval = 0.09f;

        // true -> run the original Use; false -> skip this frame's Use (accumulate its time for later).
        // On a skipped frame we set __result=true so Item.Use still marks the component WasUsed and keeps
        // the OnUse looping sound alive — Item.Update calls StopSounds(OnUse) every frame WasUsed is false
        // (Item.cs ~2495), so returning false here without __result would make the spray sound stutter.
        // The cheap OnUse side-effects (PlaySound/OnUse status effects) thus run every frame as in vanilla;
        // only the EXPENSIVE Use body (raycasts + FixBody + extinguish/water + particles) is throttled.
        // RUS: true -> выполнить оригинальный Use; false -> пропустить Use этого кадра (накопив его время).
        // RUS: На пропущенном кадре ставим __result=true, чтобы Item.Use отметил WasUsed и держал луп-звук
        // RUS: OnUse живым — Item.Update зовёт StopSounds(OnUse) каждый кадр, пока WasUsed=false (Item.cs ~2495),
        // RUS: так что без __result звук распыления заикался бы. Дешёвые OnUse-побочки (PlaySound/OnUse-эффекты)
        // RUS: при этом идут каждый кадр как в ванили; троттлится только ДОРОГОЕ тело Use (рейкасты + FixBody +
        // RUS: тушение/вода + партиклы).
        public static bool Prefix(RepairTool __instance, ref float deltaTime, Character character, ref bool __result)
        {
            if (RepairToolThrottleOptPlugin.Level <= 0) { return true; }
            try
            {
                if (character != null) { return true; }   // manual use by a player -> never throttle   // RUS: ручное использование игроком -> не троттлим
                float interval = GetInterval();
                if (interval <= 0f) { return true; }

                StrongBox<float> box = _acc.GetValue(__instance, static _ => new StrongBox<float>(0f));
                box.Value += deltaTime;
                if (box.Value < interval)
                {
                    __result = true;   // report success -> keep the looping sound alive (see note above)   // RUS: рапортуем успех -> держим луп-звук живым (см. примечание выше)
                    return false;      // skip the expensive Use body   // RUS: пропускаем дорогое тело Use
                }

                deltaTime = box.Value;   // run once with the accumulated time -> output preserved   // RUS: выполняем один раз с накопленным временем -> вывод сохранён
                box.Value = 0f;
                return true;             // run the real Use; it sets __result itself   // RUS: выполняем настоящий Use; он сам выставит __result
            }
            catch { return true; }   // fail-safe: any error -> behave like vanilla   // RUS: страховка: любая ошибка -> ведём себя как ваниль
        }

        // interval = half the server tickrate (period = 2 / tickrate), clamped so the spray sound stays alive.
        // Tickrate is read live (NG Network Tweaks may change it per round); falls back to 60 if unavailable.
        // RUS: интервал = половина тикрейта сервера (период = 2 / тикрейт), с клампом чтобы звук распыления не оборвался.
        // RUS: Тикрейт читается на лету (NG Network Tweaks может менять его по раундам); фоллбэк 60, если недоступен.
        private static float GetInterval()
        {
            int tr = 60;
            try
            {
                var ss = GameMain.NetworkMember?.ServerSettings;
                if (ss != null && ss.TickRate > 0) { tr = ss.TickRate; }
            }
            catch { }
            // period = (level+1) ticks: level1 -> 2 ticks (50%), level2 -> 3 (33%), level3 -> 4 (25%).
            // RUS: период = (уровень+1) тиков: 1->2 тика (50%), 2->3 (33%), 3->4 (25%).
            float interval = (RepairToolThrottleOptPlugin.Level + 1) / (float)tr;
            if (interval > MaxInterval) { interval = MaxInterval; }
            if (interval < 0f) { interval = 0f; }
            return interval;
        }
    }
}
