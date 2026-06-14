using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using Barotrauma;
using Barotrauma.Items.Components;
using HarmonyLib;
using Microsoft.Xna.Framework;

namespace NGLightThrottleOpt
{
    // ==========================================================================================
    //  FIX 5 (EXPERIMENTAL): throttle the per-frame Update of invisible "trigger lights".
    //
    //  Many auto-tools (EK sprinkler ekutility_portablespinkler etc.) have no real light — they
    //  abuse an INVISIBLE LightComponent (lightcolor alpha=0, range≈10) purely as a power-gated,
    //  signal-toggleable, every-frame ticker: its <StatusEffect type="OnActive"><UseItem/></> calls
    //  item.Use() each active frame to drive the spray. Profiler attributes the whole spray cost to
    //  Comp:LightComponent (Use is called synchronously inside LightComponent.Update).
    //
    //  Fix#3 already throttles the RepairTool.Use BODY (raycasts). This goes one level UP and throttles
    //  the WHOLE LightComponent.Update of such trigger-lights — skipping N-1 frames and ACCUMULATING
    //  deltaTime, then running once with the accumulated time. Because LightComponent.Update passes its
    //  deltaTime down to UpdateOnActiveEffects -> UseItem -> item.Use(deltaTime), the accumulated time
    //  flows into Use, so spray OUTPUT is preserved; only the per-frame light/dispatch overhead AND the
    //  raycast frequency drop. Composes safely with Fix#3 (no double output). The light is invisible so
    //  throttling its visuals changes nothing. Power/state gating is untouched (an off/unpowered light
    //  is IsActive=false and isn't Updated at all, so the prefix is never reached).
    //
    //  Target = a LightComponent whose light is invisible (color alpha 0) on an item that has a
    //  RepairTool (the auto-tool pattern). Real (visible) lights are never touched.
    //  Levels: 0=off, 1=50% (run every 2 frames), 2=33% (every 3), 3=25% (every 4). Default OFF (so it
    //  doesn't double up with Fix#3 unless the user opts in). MP-safe: Shared, deltaTime preserved.
    //  RUS: ФИКС 5 (ЭКСПЕРИМ.): троттлинг покадрового апдейта невидимых «ламп-тикалок». Авто-инструменты
    //  RUS: (спринклер EK и т.п.) вешают НЕВИДИМЫЙ LightComponent (alpha=0) как запитываемую «тикалку»:
    //  RUS: его OnActive <UseItem/> зовёт Use() каждый активный кадр. Fix#3 троттлит тело Use; этот фикс
    //  RUS: троттлит ВЕСЬ LightComponent.Update таких ламп (накопление deltaTime -> мощность сохраняется,
    //  RUS: падает покадровый каркас и частота рейкастов). Цель: невидимый свет (alpha 0) у предмета с
    //  RUS: RepairTool. Уровни: 0=выкл,1=50%,2=33%,3=25%. По умолчанию ВЫКЛ (чтобы не дублировать Fix#3).
    // ==========================================================================================
    public sealed class LightTriggerThrottleOptPlugin : IAssemblyPlugin
    {
        public static int Level = 0;            // 0=off, 1=50%, 2=33%, 3=25%
        public static bool Enabled => Level > 0;

        private static Harmony _h;

        public void PreInitPatching() { }

        public void Initialize()
        {
            try
            {
                if (_h != null) { return; }
                _h = new Harmony("ng.lighttriggerthrottleopt");
                BindingFlags sp = BindingFlags.Static | BindingFlags.Public;

                MethodInfo upd = AccessTools.Method(typeof(LightComponent), "Update", new[] { typeof(float), typeof(Camera) });
                if (upd == null)
                {
                    DebugConsole.NewMessage(NetEventLogger.Loc.Tag + NetEventLogger.Loc.T(
                        "ФИКС 5: LightComponent.Update не найден — троттлинг недоступен.",
                        "FIX 5: LightComponent.Update not found — throttling unavailable."), Color.Orange);
                    return;
                }
                _h.Patch(upd, prefix: new HarmonyMethod(typeof(LightTriggerThrottlePatch).GetMethod(nameof(LightTriggerThrottlePatch.Prefix), sp)));
                DebugConsole.NewMessage(NetEventLogger.Loc.Tag + NetEventLogger.Loc.T(
                    "ФИКС 5 готов: троттлинг невидимых ламп-тикалок авто-инструментов (по умолч. ВЫКЛ).",
                    "FIX 5 ready: invisible auto-tool trigger-light throttling (OFF by default)."), Color.LightGreen);
            }
            catch (Exception ex)
            {
                DebugConsole.NewMessage(NetEventLogger.Loc.Tag + "FIX 5 init error: " + ex.Message, Color.Red);
            }
        }

        public void OnLoadCompleted() { }

        public void Dispose()
        {
            try { _h?.UnpatchSelf(); } catch { }
            _h = null;
        }

        public static void SetEnabled(bool on) { Level = on ? 1 : 0; }
        public static void SetLevel(int lvl) { Level = lvl < 0 ? 0 : (lvl > 3 ? 3 : lvl); }
    }

    public static class LightTriggerThrottlePatch
    {
        private sealed class Acc { public float Time; public int Count; }

        private static readonly ConditionalWeakTable<LightComponent, Acc> _acc = new ConditionalWeakTable<LightComponent, Acc>();
        private static readonly HashSet<Identifier> _trigger    = new HashSet<Identifier>();
        private static readonly HashSet<Identifier> _notTrigger = new HashSet<Identifier>();

        // Invisible (alpha 0) light on an item that drives a RepairTool = an auto-tool ticker. Cached per prefab.
        // RUS: Невидимый (alpha 0) свет у предмета с RepairTool = «тикалка» авто-инструмента. Кэш по prefab.
        private static bool IsTriggerLight(LightComponent lc)
        {
            var item = lc.Item;
            if (item?.Prefab == null) { return false; }
            Identifier id = item.Prefab.Identifier;
            if (_trigger.Contains(id)) { return true; }
            if (_notTrigger.Contains(id)) { return false; }
            bool t = false;
            try { t = lc.LightColor.A == 0 && item.GetComponent<RepairTool>() != null; } catch { }
            if (t) { _trigger.Add(id); } else { _notTrigger.Add(id); }
            return t;
        }

        public static bool Prefix(LightComponent __instance, ref float deltaTime)
        {
            if (LightTriggerThrottleOptPlugin.Level <= 0) { return true; }
            try
            {
                if (!IsTriggerLight(__instance)) { return true; }
                int every = LightTriggerThrottleOptPlugin.Level + 1; // level1->2 (50%), level2->3 (33%), level3->4 (25%)
                Acc a = _acc.GetValue(__instance, static _ => new Acc());
                a.Time += deltaTime;
                a.Count++;
                if (a.Count < every)
                {
                    // Keep the auto-tool's looping OnUse sound alive while we skip the update. Item.Update
                    // calls StopSounds(OnUse) on every frame the component's WasUsed is false (Item.cs ~2495);
                    // since we skip the LightComponent.Update -> UseItem -> Use chain (which would set WasUsed),
                    // we set it ourselves so the spray sound doesn't stutter. The expensive raycasts/light
                    // work are still skipped; deltaTime is accumulated, so the actual spray output is unchanged.
                    // RUS: Держим луп-звук OnUse живым на пропущенных кадрах. Item.Update глушит OnUse, когда
                    // RUS: WasUsed=false (Item.cs ~2495); раз мы пропускаем цепочку Use (которая ставит WasUsed),
                    // RUS: ставим его сами, чтобы звук распыления не дёргался. Рейкасты/свет всё равно пропущены.
                    try { var rt = __instance.Item.GetComponent<RepairTool>(); if (rt != null) { rt.WasUsed = true; } } catch { }
                    return false; // skip this frame's whole update (incl. the UseItem -> spray)
                }
                deltaTime = a.Time;   // run once with the accumulated time -> spray output preserved
                a.Time = 0f;
                a.Count = 0;
                return true;
            }
            catch { return true; }
        }
    }
}
