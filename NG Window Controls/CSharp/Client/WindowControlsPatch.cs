using System;
using System.Reflection;
using Barotrauma;
using Barotrauma.Items.Components;
using HarmonyLib;
using Microsoft.Xna.Framework;

namespace NGWindowControls
{
    // ============================================================================================
    //  NG Window Controls (client-only UI).
    //  Adds 6 buttons next to the gear icon on every draggable item HUD window (containers,
    //  fabricators, anything with a movable frame):
    //    +  / -  : raise / lower the window's draw layer (z-order) among overlapping windows.
    //    < ^ v > : nudge the window by 5% of the screen width/height in that direction.
    //  Move offsets reuse the vanilla GuiFrameOffset (synced to the server, so they persist for the
    //  round like a manual drag). Layer changes are purely local (each client orders its own UI).
    //  Implemented as a Harmony postfix on ItemComponent.TryCreateDragHandle so the buttons appear
    //  exactly when/where the gear does.
    //  RUS: Клиентский UI-мод. Добавляет 6 кнопок рядом с шестерёнкой у каждого перетаскиваемого
    //  RUS: HUD-окна предмета: +/- меняют слой отрисовки (что поверх при наложении), стрелки сдвигают
    //  RUS: окно на 5% ширины/высоты экрана. Сдвиг использует ванильный GuiFrameOffset (синхронится
    //  RUS: на сервер, держится весь раунд); слой — чисто локально.
    // ============================================================================================
    public sealed class WindowControlsPatch : IAssemblyPlugin
    {
        private Harmony harmony;

        private static bool _reflectionReady;
        private static FieldInfo _dragHandleField;     // private GUIDragHandle guiFrameDragHandle
        private static FieldInfo _updatePendingField;  // private bool guiFrameUpdatePending
        private static MethodInfo _hudLayerSetter;     // HudLayer { ... private set; }

        public void PreInitPatching() { }
        public void OnLoadCompleted() { }

        public void Initialize()
        {
            harmony = new Harmony("ng.windowcontrols");
            var method = typeof(ItemComponent).GetMethod("TryCreateDragHandle",
                BindingFlags.NonPublic | BindingFlags.Instance);
            if (method != null)
            {
                harmony.Patch(method, postfix: new HarmonyMethod(typeof(WindowControlsPatch), nameof(TryCreateDragHandle_Postfix)));
            }
            else
            {
                DebugConsole.Log("[NG] [Window Controls] TryCreateDragHandle not found — controls disabled.");
            }
        }

        public void Dispose()
        {
            harmony?.UnpatchSelf();
            harmony = null;
        }

        private static void EnsureReflection()
        {
            if (_reflectionReady) return;
            _reflectionReady = true;
            var t = typeof(ItemComponent);
            _dragHandleField    = t.GetField("guiFrameDragHandle",   BindingFlags.NonPublic | BindingFlags.Instance);
            _updatePendingField = t.GetField("guiFrameUpdatePending", BindingFlags.NonPublic | BindingFlags.Instance);
            _hudLayerSetter     = t.GetProperty("HudLayer", BindingFlags.Public | BindingFlags.Instance)?.GetSetMethod(nonPublic: true);
        }

        // -- localization (inline RU/EN by game language) --------------------------------------
        private static int _ru = -1;
        private static string T(string ru, string en)
        {
            if (_ru < 0)
            {
                try { _ru = GameSettings.CurrentConfig.Language.ToString().IndexOf("russ", StringComparison.OrdinalIgnoreCase) >= 0 ? 1 : 0; }
                catch { _ru = 0; }
            }
            return _ru == 1 ? ru : en;
        }

        // -- the patch ---------------------------------------------------------------------------
        private static void TryCreateDragHandle_Postfix(ItemComponent __instance)
        {
            try
            {
                EnsureReflection();
                if (!(_dragHandleField?.GetValue(__instance) is GUIDragHandle handle)) { return; }

                var ic = __instance;
                int bh = Math.Max(8, (int)(GUIStyle.ItemFrameMargin.Y * 0.4f)); // match the gear size
                int gap = Math.Max(2, bh / 6);
                int x = bh / 4 + bh + gap * 2;                                  // start just right of the gear

                AddButton(handle, ref x, bh, gap, "+", T("Окно вперёд (поверх остальных)", "Bring window forward"), () => ChangeLayer(ic, -1));
                AddButton(handle, ref x, bh, gap, "-", T("Окно назад (под остальные)",      "Send window back"),    () => ChangeLayer(ic, +1));
                AddButton(handle, ref x, bh, gap, "<", T("Сдвинуть влево на 5%",  "Move left 5%"),  () => Move(ic, -1, 0));
                AddButton(handle, ref x, bh, gap, "^", T("Сдвинуть вверх на 5%",  "Move up 5%"),    () => Move(ic, 0, -1));
                AddButton(handle, ref x, bh, gap, "v", T("Сдвинуть вниз на 5%",   "Move down 5%"),  () => Move(ic, 0, 1));
                AddButton(handle, ref x, bh, gap, ">", T("Сдвинуть вправо на 5%", "Move right 5%"), () => Move(ic, 1, 0));
            }
            catch (Exception e)
            {
                DebugConsole.Log("[NG] [Window Controls] error: " + e.Message);
            }
        }

        private static void AddButton(GUIDragHandle handle, ref int x, int bh, int gap, string label, string tip, Action onClick)
        {
            var btn = new GUIButton(
                new RectTransform(new Point(bh), handle.RectTransform, Anchor.TopLeft)
                {
                    AbsoluteOffset = new Point(x, bh / 4),
                    MinSize = new Point(bh)
                },
                label, textAlignment: Alignment.Center, style: "GUIButtonSmall")
            {
                ToolTip = tip
            };
            btn.OnClicked = (b, ud) => { try { onClick(); } catch { } return true; };
            x += bh + gap;
        }

        // Nudge the window by 5% of the screen, clamped on-screen; sync like a manual drag.
        // RUS: Сдвиг окна на 5% экрана с зажатием в границы; синхронизация как при перетаскивании.
        private static void Move(ItemComponent ic, int dx, int dy)
        {
            if (ic.GuiFrame == null) return;
            var rt = ic.GuiFrame.RectTransform;

            Point newOffset = ic.GuiFrameOffset + new Point(
                (int)(GameMain.GraphicsWidth  * 0.05f) * dx,
                (int)(GameMain.GraphicsHeight * 0.05f) * dy);

            // Clamp so the frame stays fully on screen (anchored rect = current rect minus current offset).
            Rectangle rect = ic.GuiFrame.Rect;
            int ax = rect.X - rt.ScreenSpaceOffset.X;
            int ay = rect.Y - rt.ScreenSpaceOffset.Y;
            int minX = -ax, maxX = GameMain.GraphicsWidth  - rect.Width  - ax;
            int minY = -ay, maxY = GameMain.GraphicsHeight - rect.Height - ay;
            if (minX <= maxX) { newOffset.X = Math.Clamp(newOffset.X, minX, maxX); }
            if (minY <= maxY) { newOffset.Y = Math.Clamp(newOffset.Y, minY, maxY); }

            ic.GuiFrameOffset = newOffset;
            rt.ScreenSpaceOffset = newOffset;
            _updatePendingField?.SetValue(ic, true); // triggers the vanilla position sync next UpdateHUD
        }

        // Raise/lower the window's draw layer. Lower HudLayer draws on top (Item.UpdateHUD sorts
        // descending and later-added frames render above), so "forward" = decrease HudLayer.
        // RUS: Меняет слой отрисовки. Меньший HudLayer рисуется поверх, поэтому «вперёд» = уменьшить.
        private static void ChangeLayer(ItemComponent ic, int delta)
        {
            if (_hudLayerSetter == null) return;
            _hudLayerSetter.Invoke(ic, new object[] { ic.HudLayer + delta });
        }
    }
}
