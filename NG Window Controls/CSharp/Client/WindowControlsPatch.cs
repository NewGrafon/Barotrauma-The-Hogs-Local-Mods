using System;
using System.Reflection;
using System.Runtime.CompilerServices;
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

        // Per-component z-order (our own value). Injected into ItemComponent.AddToGUIUpdateList(order) so it
        // drives the REAL GUI draw order. >0 = drawn on top, <0 = drawn behind. ConditionalWeakTable so the
        // entry vanishes with the component (no leak). 0 / absent = vanilla behavior (untouched windows).
        // RUS: Наш per-компонентный z-order. Подставляется в ItemComponent.AddToGUIUpdateList(order), задавая
        // RUS: РЕАЛЬНЫЙ порядок отрисовки GUI. >0 = поверх, <0 = под низ. ConditionalWeakTable — без утечек.
        // RUS: 0 / нет записи = ванильное поведение (нетронутые окна).
        private static readonly ConditionalWeakTable<ItemComponent, StrongBox<int>> _z =
            new ConditionalWeakTable<ItemComponent, StrongBox<int>>();

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

            // Inject our per-window z-order into the single chokepoint the engine uses for BOTH selected-item
            // and equipped HUD windows. This is what actually changes the draw order (HudLayer alone doesn't).
            // RUS: Подставляем наш z-order в единую точку, через которую движок добавляет окна И выбранного
            // RUS: предмета, И надетого снаряжения. Именно это реально меняет порядок отрисовки (один HudLayer — нет).
            var addMethod = typeof(ItemComponent).GetMethod("AddToGUIUpdateList",
                BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(int) }, null);
            if (addMethod != null)
            {
                harmony.Patch(addMethod, prefix: new HarmonyMethod(typeof(WindowControlsPatch), nameof(AddToGUIUpdateList_Prefix)));
            }
            else
            {
                DebugConsole.Log("[NG] [Window Controls] AddToGUIUpdateList(int) not found — z-order disabled.");
            }

            // Mouse INPUT for inventory slots ignores window overlap (the engine's mouse-on-GUI check is
            // commented out in Inventory.UpdateSlot), so behind windows are clickable through a front window's
            // empty areas. Gate it: while the cursor is over a HIGHER of our windows, lock the lower container
            // inventory for this frame so its slots don't react. Per-inventory (cheap) + state restored after.
            // RUS: Ввод мышью по ячейкам игнорирует перекрытие окон (проверка в Inventory.UpdateSlot закомментирована
            // RUS: в движке) — через пустые места переднего окна кликается заднее. Гейтим: пока курсор над БОЛЕЕ
            // RUS: высоким нашим окном, на кадр блокируем (Locked) задний инвентарь-контейнер. Раз на инвентарь + возврат.
            var invUpdate = typeof(Inventory).GetMethod("Update",
                BindingFlags.Public | BindingFlags.Instance, null,
                new[] { typeof(float), typeof(Camera), typeof(bool) }, null);
            if (invUpdate != null)
            {
                harmony.Patch(invUpdate,
                    prefix:  new HarmonyMethod(typeof(WindowControlsPatch), nameof(InventoryUpdate_Prefix)),
                    postfix: new HarmonyMethod(typeof(WindowControlsPatch), nameof(InventoryUpdate_Postfix)));
            }
            else
            {
                DebugConsole.Log("[NG] [Window Controls] Inventory.Update not found — overlap input-blocking disabled.");
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
                _z.GetValue(ic, _ => new StrongBox<int>(0)); // register the window (z=0) so overlap-blocking can see it   // RUS: регистрируем окно (z=0), чтобы блокировка перекрытия его видела

                int bh = Math.Max(8, (int)(GUIStyle.ItemFrameMargin.Y * 0.4f)); // match the gear size
                int gap = Math.Max(2, bh / 6);
                int x = bh / 4 + bh + gap * 2;                                  // start just right of the gear

                AddButton(handle, ref x, bh, gap, "+", T("Окно вперёд (поверх остальных)", "Bring window forward"), () => ChangeLayer(ic, +1));
                AddButton(handle, ref x, bh, gap, "-", T("Окно назад (под остальные)",      "Send window back"),    () => ChangeLayer(ic, -1));
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

        // Raise/lower the window's draw layer by adjusting OUR per-component z value. The prefix below feeds
        // it into AddToGUIUpdateList as the GUI 'order' (>0 = on top, <0 = behind), which is the value the
        // engine actually draws by. delta +1 = forward/up, -1 = back/down. Clamped to a sane band.
        // RUS: Меняем слой окна через НАШ per-компонентный z. Префикс ниже отдаёт его как GUI-'order'
        // RUS: (>0 = поверх, <0 = под низ) — именно по нему движок рисует. +1 = вперёд, -1 = назад. С зажимом.
        private static void ChangeLayer(ItemComponent ic, int delta)
        {
            var box = _z.GetValue(ic, _ => new StrongBox<int>(0));
            box.Value = Math.Clamp(box.Value + delta, -20, 20);
        }

        // Prefix on ItemComponent.AddToGUIUpdateList(int order): override the 'order' with our stored z so the
        // window draws at the chosen depth. Only when set (non-zero) — untouched windows keep vanilla order 0.
        // Runs for BOTH paths: selected-item HUDs (Item.AddToGUIUpdateList) and equipped HUDs (CharacterHUD).
        // RUS: Префикс на ItemComponent.AddToGUIUpdateList(int order): подменяем 'order' нашим z, чтобы окно
        // RUS: рисовалось на нужной глубине. Только если задан (не 0) — нетронутые окна остаются на order 0.
        // RUS: Срабатывает для ОБОИХ путей: окна выбранного предмета и надетого снаряжения.
        private static void AddToGUIUpdateList_Prefix(ItemComponent __instance, ref int order)
        {
            try
            {
                if (_z.TryGetValue(__instance, out var box) && box.Value != 0) { order = box.Value; }
            }
            catch { }
        }

        // -- overlap input-blocking (Inventory.Update prefix/postfix) -----------------------------
        // While the cursor is over a HIGHER of our windows, lock this lower container inventory so its
        // slots don't react this frame (prevents click-through). __state restores the original Locked.
        // RUS: Пока курсор над БОЛЕЕ высоким нашим окном — блокируем этот нижний инвентарь-контейнер на
        // RUS: кадр, чтобы его ячейки не реагировали (нет клика «насквозь»). __state возвращает Locked.
        private static void InventoryUpdate_Prefix(Inventory __instance, out bool __state)
        {
            __state = false;
            try
            {
                if (__instance == null || __instance.Locked) { return; }
                if (IsCoveredByHigherWindow(__instance)) { __instance.Locked = true; __state = true; }
            }
            catch { }
        }

        private static void InventoryUpdate_Postfix(Inventory __instance, bool __state)
        {
            try { if (__state && __instance != null) { __instance.Locked = false; } }
            catch { }
        }

        // True if the cursor is over one of OUR windows that sits ABOVE this inventory's own window.
        // RUS: True, если курсор над одним из НАШИХ окон, что выше окна этого инвентаря.
        private static bool IsCoveredByHigherWindow(Inventory inv)
        {
            if (!TryGetInventoryWindowZ(inv, out int zThis, out GUIComponent ownFrame)) { return false; }
            Microsoft.Xna.Framework.Vector2 mouse = PlayerInput.MousePosition;
            foreach (var kv in _z)
            {
                GUIFrame frame = kv.Key?.GuiFrame;
                if (frame == null || !frame.Visible || frame == ownFrame) { continue; }
                if (kv.Value.Value > zThis && frame.Rect.Contains(mouse)) { return true; }
            }
            return false;
        }

        // Map a container inventory to its managed window's z + frame. Only ItemContainer windows are mapped
        // (the case the user hits); anything else (e.g. the hotbar) returns false and is never blocked.
        // RUS: Сопоставить инвентарь-контейнер его окну (z + фрейм). Маппим только окна ItemContainer; всё
        // RUS: прочее (напр. хотбар) -> false и никогда не блокируется.
        private static bool TryGetInventoryWindowZ(Inventory inv, out int z, out GUIComponent frame)
        {
            z = 0; frame = null;
            if (inv == null) { return false; }
            foreach (var kv in _z)
            {
                if (kv.Key is ItemContainer container && ReferenceEquals(container.Inventory, inv))
                {
                    GUIFrame f = kv.Key.GuiFrame;
                    if (f == null) { return false; }
                    z = kv.Value.Value; frame = f; return true;
                }
            }
            return false;
        }
    }
}
