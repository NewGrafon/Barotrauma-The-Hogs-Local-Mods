using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using Barotrauma;
using Barotrauma.Items.Components;
using HarmonyLib;
using Microsoft.Xna.Framework;

namespace NGGearThrottleOpt
{
    // ==========================================================================================
    //  FIX 4 (EXPERIMENTAL): cut the per-frame cost of heavy WEARABLE gear that also carries a Turret
    //  component — AAAC tactical helmets / NVG (ballistichelmet4/7, nvg-anim, ...).
    //
    //  Profiler: one such helmet costs ~560-900µs/frame — ~half in its Turret (a COSMETIC HUD/thermal
    //  overlay: HudTint, crosshair, light rotation; AI auto-fire is OFF so it scans nothing), ~half in
    //  its ~10 OnWearing StatusEffects (oxygen supply, armor, vision). Several worn at once tank FPS.
    //
    //  Two SAFE measures (a LIVING wearer's oxygen/armor are NOT touched):
    //   (1) Throttle the gear's Turret.Update to 1 of every N frames, ACCUMULATING deltaTime. It's pure
    //       cosmetics, so running it ~1/3 as often only makes the overlay slightly less smooth. Real
    //       weapon turrets (not wearable) are NEVER touched.
    //   (2) Skip Item.Update ENTIRELY for such gear worn/held by a DEAD character — a corpse needs no
    //       oxygen/HUD, so this removes the cost of accumulated looted/corpse helmets at zero gameplay
    //       cost. (When Item.Update is skipped, the Turret isn't updated either — no double work.)
    //
    //  We deliberately do NOT throttle a LIVING wearer's Item.Update: its oxygen-supply StatusEffect
    //  (OxygenAvailable) must run every frame or breathing could flicker.
    //
    //  MP-safe: Shared (client+server); deltaTime accumulation preserves rates; the dead-skip is
    //  deterministic and changes no network serialization. ON by default; labelled experimental in the UI.
    //  RUS: ФИКС 4 (ЭКСПЕРИМ.): срезает покадровую стоимость тяжёлого НОСИМОГО снаряжения с компонентом
    //  RUS: Turret — тактические шлемы/NVG из AAAC. Один шлем ~560-900µs/кадр: ~половина — Turret (косметика:
    //  RUS: HUD/тепловизор, прицел, поворот ламп; AI-стрельба ВЫКЛ), ~половина — ~10 эффектов OnWearing
    //  RUS: (кислород/броня/обзор). (1) Троттлим Turret.Update до 1 из N кадров с накоплением deltaTime —
    //  RUS: чистая косметика. (2) Полностью пропускаем Item.Update для такого снаряжения на МЁРТВОМ носителе
    //  RUS: (трупу не нужны кислород/HUD). Item.Update ЖИВОГО носителя НЕ троттлим (кислород подаётся каждый
    //  RUS: кадр). MP-safe; deltaTime сохраняет rate; скип на трупах детерминирован.
    // ==========================================================================================
    public sealed class GearThrottleOptPlugin : IAssemblyPlugin
    {
        public static bool Enabled = true;

        private static Harmony _h;

        public void PreInitPatching() { }

        public void Initialize()
        {
            try
            {
                if (_h != null) { return; }
                _h = new Harmony("ng.gearthrottleopt");
                BindingFlags sp = BindingFlags.Static | BindingFlags.Public;

                MethodInfo turretUpd = AccessTools.Method(typeof(Turret), "Update", new[] { typeof(float), typeof(Camera) });
                MethodInfo itemUpd   = AccessTools.Method(typeof(Item), "Update", new[] { typeof(float), typeof(Camera) });

                if (turretUpd != null)
                {
                    _h.Patch(turretUpd, prefix: new HarmonyMethod(typeof(GearThrottlePatch).GetMethod(nameof(GearThrottlePatch.TurretPrefix), sp)));
                }
                if (itemUpd != null)
                {
                    _h.Patch(itemUpd, prefix: new HarmonyMethod(typeof(GearThrottlePatch).GetMethod(nameof(GearThrottlePatch.ItemPrefix), sp)));
                }

                DebugConsole.NewMessage(NetEventLogger.Loc.Tag + NetEventLogger.Loc.T(
                    "ФИКС 4 готов: троттлинг турелей тактического снаряжения + скип на трупах (по умолч. ВКЛ).",
                    "FIX 4 ready: tactical-gear turret throttling + dead-wearer skip (ON by default)."), Color.LightGreen);
            }
            catch (Exception ex)
            {
                DebugConsole.NewMessage(NetEventLogger.Loc.Tag + "FIX 4 init error: " + ex.Message, Color.Red);
            }
        }

        public void OnLoadCompleted() { }

        public void Dispose()
        {
            try { _h?.UnpatchSelf(); } catch { }
            _h = null;
        }

        public static void SetEnabled(bool on) { Enabled = on; }
    }

    public static class GearThrottlePatch
    {
        private sealed class Acc { public float Time; public int Count; }

        // per-turret accumulated time (GC-friendly: dropped when the component is collected)
        private static readonly ConditionalWeakTable<Turret, Acc> _acc = new ConditionalWeakTable<Turret, Acc>();

        // per-prefab cache of "is wearable + turret tactical gear" (component scan done once)
        private static readonly HashSet<Identifier> _gear    = new HashSet<Identifier>();
        private static readonly HashSet<Identifier> _notGear = new HashSet<Identifier>();

        private const int RunEvery = 3; // run the turret 1 of every 3 updates (~66% fewer)

        private static bool IsGear(Item item)
        {
            if (item?.Prefab == null) { return false; }
            Identifier id = item.Prefab.Identifier;
            if (_gear.Contains(id)) { return true; }
            if (_notGear.Contains(id)) { return false; }
            bool g = false;
            try { g = item.GetComponent<Wearable>() != null && item.GetComponent<Turret>() != null; } catch { }
            if (g) { _gear.Add(id); } else { _notGear.Add(id); }
            return g;
        }

        // (2) Skip the whole Item.Update for tactical gear on a DEAD wearer (corpse needs nothing).
        // RUS: (2) Полностью пропускаем Item.Update для тактического снаряжения на МЁРТВОМ носителе.
        public static bool ItemPrefix(Item __instance)
        {
            if (!GearThrottleOptPlugin.Enabled) { return true; }
            try
            {
                if (!IsGear(__instance)) { return true; }
                Character owner = __instance.ParentInventory?.Owner as Character;
                if (owner != null && owner.IsDead) { return false; } // corpse gear -> skip
                return true;
            }
            catch { return true; }
        }

        // (1) Throttle / skip the COSMETIC Turret.Update of wearable gear.
        //  - Not worn by a LIVING character (dropped on the floor, sitting in a container, on a
        //    corpse) -> the HUD is never drawn, so the turret is pure waste -> SKIP it entirely.
        //    This kills the cost of looted/idle helmets that pile up over a long round even when no
        //    new wearers appear (the likely cause of the cost "climbing" in long sessions).
        //  - Worn by a living wearer -> throttle to 1 of every N frames (cosmetic, deltaTime kept).
        // Real weapon turrets (not wearable gear) are never touched.
        // RUS: (1) Троттлим / пропускаем КОСМЕТИЧЕСКИЙ Turret.Update носимого снаряжения.
        // RUS:  - НЕ надет живым (выпал на пол / в контейнере / на трупе) -> HUD не рисуется, турель
        // RUS:    бесполезна -> ПОЛНОСТЬЮ пропускаем. Это убирает стоимость лута/лежащих шлемов,
        // RUS:    копящихся за долгий раунд даже без новых носителей (вероятная причина «роста»).
        // RUS:  - Надет живым -> троттлим до 1 из N кадров (косметика, deltaTime сохранён).
        public static bool TurretPrefix(Turret __instance, ref float deltaTime)
        {
            if (!GearThrottleOptPlugin.Enabled) { return true; }
            try
            {
                Item item = __instance.Item;
                if (!IsGear(item)) { return true; } // only wearable-gear turrets; weapon turrets untouched

                Character owner = item.ParentInventory?.Owner as Character;
                if (owner == null || owner.IsDead) { return false; } // not worn by a living character -> skip the turret entirely

                Acc a = _acc.GetValue(__instance, static _ => new Acc());
                a.Time += deltaTime;
                a.Count++;
                if (a.Count < RunEvery) { return false; } // skip this frame's (cosmetic) turret update
                deltaTime = a.Time;   // run once with accumulated time
                a.Time = 0f;
                a.Count = 0;
                return true;
            }
            catch { return true; }
        }
    }
}
