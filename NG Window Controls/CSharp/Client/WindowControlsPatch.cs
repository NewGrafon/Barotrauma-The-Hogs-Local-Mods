using System;
using System.Linq;
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
            Settings.Load();
            Settings.RegisterCommand();

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

            // Scale linked containers shown side-by-side under a fabricator (postfix on the container HUD update).
            // RUS: Масштаб связанных контейнеров, показанных под фабрикатором (постфикс апдейта HUD контейнера).
            var containerHud = typeof(ItemContainer).GetMethod("UpdateHUDComponentSpecific",
                BindingFlags.Public | BindingFlags.Instance, null,
                new[] { typeof(Character), typeof(float), typeof(Camera) }, null);
            if (containerHud != null)
            {
                harmony.Patch(containerHud, postfix: new HarmonyMethod(typeof(WindowControlsPatch), nameof(ContainerHUD_Postfix)));
            }
            else
            {
                DebugConsole.Log("[NG] [Window Controls] ItemContainer.UpdateHUDComponentSpecific not found — linked-container scaling disabled.");
            }

            // Keep the settings menu on the GUI update list each frame (postfix on the per-frame HUD add).
            // RUS: Держим меню настроек в GUI-списке каждый кадр (постфикс на покадровом добавлении HUD).
            var charHud = typeof(CharacterHUD).GetMethod("AddToGUIUpdateList",
                BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(Character) }, null);
            if (charHud != null)
            {
                harmony.Patch(charHud, postfix: new HarmonyMethod(typeof(WindowControlsPatch), nameof(MenuUpdate_Postfix)));
            }
            else
            {
                DebugConsole.Log("[NG] [Window Controls] CharacterHUD.AddToGUIUpdateList not found — menu auto-draw disabled.");
            }

            // The gear's context menu ("Reset/Lock position") is added by the engine at a tiny order (2), so our
            // higher-order windows cover it and its buttons become unclickable. Force it far above everything.
            // RUS: Контекстное меню шестерёнки («Сброс/Закрепить позицию») движок добавляет с крошечным order (2),
            // RUS: и наши окна с большим z его перекрывают — кнопки не нажать. Принудительно поднимаем поверх всего.
            var ctxAdd = typeof(GUIContextMenu).GetMethod("AddToGUIUpdateList",
                BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(bool), typeof(int) }, null);
            if (ctxAdd != null)
            {
                harmony.Patch(ctxAdd, prefix: new HarmonyMethod(typeof(WindowControlsPatch), nameof(ContextMenu_AddToGUIUpdateList_Prefix)));
            }
            else
            {
                DebugConsole.Log("[NG] [Window Controls] GUIContextMenu.AddToGUIUpdateList not found — context-menu z-fix disabled.");
            }
        }

        public void Dispose()
        {
            try { Settings.UnregisterCommand(); } catch { }
            try { Menu.Close(); } catch { }
            harmony?.UnpatchSelf();
            harmony = null;
        }

        private static void MenuUpdate_Postfix() { try { Menu.AddToGUIUpdateList(); } catch { } }

        // Force the gear context menu (and its submenus) far above our window orders so it's always on top
        // and clickable, regardless of the z chosen for the window behind it.
        // RUS: Поднимаем контекстное меню шестерёнки (и подменю) намного выше order наших окон, чтобы оно было
        // RUS: всегда сверху и кликабельно, какой бы z ни стоял у окна за ним.
        private static void ContextMenu_AddToGUIUpdateList_Prefix(ref int order)
        {
            if (order < 1000) { order = 1000; }
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

                // Register the window with a per-window MANUAL offset (delta), default 0. The fabricator base
                // order is NOT stored here — it's computed live in EffectiveOrder(), so it never depends on
                // component-load timing during registration. Effective order = base(item) + delta.
                // RUS: Регистрируем окно с РУЧНЫМ смещением (delta), по умолчанию 0. Базовый order фабрикатора
                // RUS: здесь НЕ хранится — считается на лету в EffectiveOrder(), поэтому не зависит от порядка
                // RUS: загрузки компонентов при регистрации. Итоговый order = base(предмет) + delta.
                _z.GetValue(ic, _ => new StrongBox<int>(0));

                // Movement-restriction toggle. Vanilla rejects a drop whose window overlaps another (red flash +
                // revert via ValidatePosition). When our setting is OFF (default) accept any position but keep the
                // useful side-effects (persist offset, refresh slots). Read live -> the toggle works at runtime.
                // RUS: Тумблер ограничения перемещения. Ваниль отклоняет дроп с пересечением окна (красная вспышка +
                // RUS: откат). Если тумблер ВЫКЛ (по умолч.) — принимаем любую позицию, но сохраняем полезные
                // RUS: side-effects (персист оффсета, рефреш слотов). Чтение live -> тумблер работает на лету.
                var vanillaValidate = handle.ValidatePosition;
                handle.ValidatePosition = (rectT) =>
                {
                    if (Settings.MoveRestriction) { return vanillaValidate == null || vanillaValidate(rectT); }
                    try
                    {
                        var sel = Character.Controlled?.SelectedItem;
                        var huds = sel?.ActiveHUDs ?? ic.Item.ActiveHUDs;
                        foreach (ItemComponent c in huds) { (c as ItemContainer)?.Inventory?.CreateSlots(); }
                        ic.GuiFrameOffset = ic.GuiFrame.RectTransform.ScreenSpaceOffset;
                        _updatePendingField?.SetValue(ic, true);
                    }
                    catch { }
                    return true;
                };

                int bh = Math.Max(8, (int)(GUIStyle.ItemFrameMargin.Y * 0.4f)); // match the gear size
                int gap = Math.Max(2, bh / 6);
                int x = bh / 4 + bh + gap * 2;                                  // start just right of the gear

                AddButton(handle, ref x, bh, gap, "+", T("Окно вперёд (поверх остальных)", "Bring window forward"), () => ChangeLayer(ic, +1));
                AddButton(handle, ref x, bh, gap, "-", T("Окно назад (под остальные)",      "Send window back"),    () => ChangeLayer(ic, -1));
                if (Settings.ShowMoveArrows) // 5%-move arrows are hidden by default (toggle in the menu)   // RUS: стрелки сдвига на 5% по умолчанию скрыты (тумблер в меню)
                {
                    AddButton(handle, ref x, bh, gap, "<", T("Сдвинуть влево на 5%",  "Move left 5%"),  () => Move(ic, -1, 0));
                    AddButton(handle, ref x, bh, gap, "^", T("Сдвинуть вверх на 5%",  "Move up 5%"),    () => Move(ic, 0, -1));
                    AddButton(handle, ref x, bh, gap, "v", T("Сдвинуть вниз на 5%",   "Move down 5%"),  () => Move(ic, 0, 1));
                    AddButton(handle, ref x, bh, gap, ">", T("Сдвинуть вправо на 5%", "Move right 5%"), () => Move(ic, 1, 0));
                }
                AddOrderLabel(handle, ref x, bh, gap, ic); // live "z:N" readout (effective order)   // RUS: живой показ «z:N» (итоговый order)
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

        // Live "z:N" readout right after the buttons — shows the window's current order (updates every frame
        // via TextGetter, so it also reflects changes made on other windows). Purely for testing/visibility.
        // RUS: Живой показ «z:N» сразу за кнопками — текущий order окна (обновляется каждый кадр через TextGetter,
        // RUS: так что отражает и изменения на других окнах). Чисто для теста/наглядности.
        private static void AddOrderLabel(GUIDragHandle handle, ref int x, int bh, int gap, ItemComponent ic)
        {
            int w = bh * 5 / 2;
            new GUITextBlock(
                new RectTransform(new Point(w, bh), handle.RectTransform, Anchor.TopLeft)
                {
                    AbsoluteOffset = new Point(x, bh / 4),
                    MinSize = new Point(w, bh)
                },
                "z:0", font: GUIStyle.SmallFont, textAlignment: Alignment.Center)
            {
                CanBeFocused = false,
                ToolTip = T("Текущий слой окна (z). + поверх, − под низ.", "Current window layer (z). + on top, − behind."),
                TextGetter = () => "z:" + GetZ(ic)
            };
            x += w + gap;
        }

        // Base draw order from the item TYPE (computed live): fabricators sit above plain windows.
        // RUS: Базовый order по ТИПУ предмета (считается live): фабрикаторы выше обычных окон.
        private static int BaseOrder(ItemComponent ic) => (ic != null && IsFabricatorItem(ic.Item)) ? Settings.FabricatorOrder : 0;

        // Effective order actually used for drawing/blocking = base(type) + per-window manual delta (_z).
        // RUS: Итоговый order для отрисовки/блокировки = base(тип) + ручная delta окна (_z).
        private static int EffectiveOrder(ItemComponent ic) => Math.Max(0, BaseOrder(ic) + (_z.TryGetValue(ic, out var b) ? b.Value : 0)); // never below 0   // RUS: никогда ниже 0

        private static int GetZ(ItemComponent ic) => EffectiveOrder(ic); // shown in the on-window "z:N" readout   // RUS: показывается в ярлыке «z:N» на окне

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
            // lower bound = -BaseOrder so the effective order never drops below 0 (negative z makes a window's
            // inventory slots unselectable — so we forbid it).
            // RUS: нижняя граница = -BaseOrder, чтобы итоговый order не падал ниже 0 (отрицательный z делает
            // RUS: ячейки окна невыбираемыми — поэтому запрещаем).
            box.Value = Math.Clamp(box.Value + delta, -BaseOrder(ic), 20);
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
                // Effective order = fabricator base (live, never depends on registration timing) + manual delta.
                // Only override when non-zero, so unrelated components (MiniMap order+1, RemoteController -1, …)
                // keep the order their own code passes.
                // RUS: Итоговый order = база фабрикатора (live, не зависит от тайминга регистрации) + ручная delta.
                // RUS: Подменяем только при non-zero, чтобы посторонние компоненты (MiniMap +1, RemoteController -1, …)
                // RUS: сохраняли свой передаваемый order.
                int eff = EffectiveOrder(__instance);
                if (eff != 0) { order = eff; }
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

        // True if the cursor is over a CURRENTLY-SHOWN window that sits ABOVE this inventory's own window.
        // IMPORTANT: only the windows displayed right now are considered (the selected item's HUDs + equipped
        // gear), NOT every entry ever registered in _z — a closed item's GuiFrame keeps Visible=true and its
        // stale Rect would otherwise block containers it no longer covers (the "can't select slots" bug).
        // RUS: True, если курсор над ПОКАЗАННЫМ СЕЙЧАС окном, что выше окна этого инвентаря. ВАЖНО: учитываем
        // RUS: только отображаемые сейчас окна (HUD выбранного предмета + надетое), а НЕ все записи _z — у
        // RUS: закрытого окна GuiFrame.Visible остаётся true, и его устаревший Rect иначе блокирует контейнеры,
        // RUS: которые он уже не перекрывает (баг «нельзя выбрать ячейки»).
        private static bool IsCoveredByHigherWindow(Inventory inv)
        {
            if (!TryGetInventoryWindowZ(inv, out int zThis, out GUIComponent ownFrame)) { return false; }
            var c = Character.Controlled;
            if (c == null) { return false; }
            Vector2 mouse = PlayerInput.MousePosition;

            if (CoveredBySelected(c.SelectedItem, zThis, ownFrame, mouse)) { return true; }
            if (CoveredBySelected(c.SelectedSecondaryItem, zThis, ownFrame, mouse)) { return true; }

            var pInv = c.Inventory;
            if (pInv != null)
            {
                for (int i = 0; i < pInv.Capacity; i++)
                {
                    Item item = pInv.GetItemAt(i);
                    if (item == null) { continue; }
                    foreach (ItemComponent ic in item.Components)
                    {
                        if (ic.DrawHudWhenEquipped && IsHigherCover(ic, zThis, ownFrame, mouse)) { return true; }
                    }
                }
            }
            return false;
        }

        // Any of a shown item's active HUD windows above zThis under the cursor?
        // RUS: Есть ли среди показанных HUD-окон предмета окно выше zThis под курсором?
        private static bool CoveredBySelected(Item shown, int zThis, GUIComponent ownFrame, Vector2 mouse)
        {
            if (shown == null) { return false; }
            foreach (ItemComponent ic in shown.ActiveHUDs)
            {
                if (IsHigherCover(ic, zThis, ownFrame, mouse)) { return true; }
            }
            return false;
        }

        // A single window covers us if it's visible, not our own, higher order, and under the cursor.
        // RUS: Одно окно перекрывает нас, если видимо, не наше, выше по order и под курсором.
        private static bool IsHigherCover(ItemComponent ic, int zThis, GUIComponent ownFrame, Vector2 mouse)
        {
            // Only OTHER container windows gate input: their inventory slots ignore window overlap (the
            // click-through bug we fix). Fabricators/other machines use real GUI components that consume input
            // themselves, and their large window Rect would otherwise block the linked containers shown beside
            // them — so machines must NOT count as a covering window.
            // RUS: Перекрывать ввод могут только ДРУГИЕ окна-контейнеры: их ячейки игнорируют перекрытие (это и
            // RUS: есть баг клика «насквозь»). Фабрикаторы/прочие машины сами ловят ввод реальными GUI-компонентами,
            // RUS: а их большой Rect иначе блокировал бы связанные контейнеры рядом — поэтому машины НЕ считаем
            // RUS: перекрывающим окном.
            if (!(ic is ItemContainer) || IsFabricatorItem(ic.Item)) { return false; }
            GUIFrame frame = ic.GuiFrame;
            if (frame == null || !frame.Visible || frame == ownFrame) { return false; }
            return EffectiveOrder(ic) > zThis && frame.Rect.Contains(mouse);
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
                    z = EffectiveOrder(kv.Key); frame = f; return true;
                }
            }
            return false;
        }

        // A fabricator-like machine, detected by COMPONENT (so modded fabricators/deconstructors work too — no
        // identifier list). Used for the default window order and the linked-container scaling.
        // RUS: Машина типа фабрикатора, определяется по КОМПОНЕНТУ (работают и модовые — без списка id).
        // RUS: Нужно для дефолтного order окна и масштаба связанных контейнеров.
        private static bool IsFabricatorItem(Item it)
            => it != null && (it.GetComponent<Fabricator>() != null || it.GetComponent<Deconstructor>() != null);

        // Postfix on ItemContainer.UpdateHUDComponentSpecific: scale a container window down while it is shown as
        // a side-by-side LINKED container under a SELECTED fabricator/deconstructor. A container opened on its own
        // (or the machine's own container) stays at 1.0. Runs each HUD frame, so it self-corrects on context change.
        // RUS: Постфикс ItemContainer.UpdateHUDComponentSpecific: уменьшаем окно контейнера, пока он показан как
        // RUS: связанный side-by-side под ВЫБРАННЫМ фабрикатором/деструктором. Открытый сам по себе (или собственный
        // RUS: контейнер машины) — масштаб 1.0. Зовётся каждый кадр HUD -> само корректируется при смене контекста.
        private static void ContainerHUD_Postfix(ItemContainer __instance)
        {
            try
            {
                GUIFrame frame = __instance?.GuiFrame;
                if (frame == null) { return; }
                Item sel = Character.Controlled?.SelectedItem;
                bool linkedUnderFab = sel != null && sel != __instance.Item
                    && IsFabricatorItem(sel) && sel.linkedTo.Contains(__instance.Item);
                float target = linkedUnderFab ? Math.Clamp(Settings.LinkedScale, 0.2f, 1f) : 1f;
                if (Math.Abs(frame.RectTransform.LocalScale.X - target) > 0.001f)
                {
                    frame.RectTransform.LocalScale = new Vector2(target);
                }
            }
            catch { }
        }
    }

    // ============================================================================================
    //  Mod settings — persisted to ngwindowcontrols_config.txt in the mod folder; tweakable live via the
    //  `ngwindow` console command. All read live by the patches, so changes apply without a restart.
    //  RUS: Настройки мода — персист в ngwindowcontrols_config.txt в папке мода; меняются на лету командой
    //  RUS: `ngwindow`. Патчи читают их live, так что изменения применяются без перезапуска.
    // ============================================================================================
    internal static class Settings
    {
        public static int   FabricatorOrder = 10;    // default z for fabricator/deconstructor windows   // RUS: дефолтный z окон фабрикаторов/деструкторов
        public static float LinkedScale     = 0.75f; // scale of containers shown under a fabricator (0.75 = −25%)   // RUS: масштаб контейнеров под фабрикатором (0.75 = −25%)
        public static bool  MoveRestriction = false; // false = allow overlapping moves (no red-flash reject)   // RUS: false = разрешить перемещение с перекрытием (без красного отката)
        public static bool  ShowMoveArrows  = false; // false = hide the 5%-move arrow buttons on windows   // RUS: false = скрыть кнопки-стрелки сдвига на 5% у окон

        private static int _ru = -1;
        private static string T(string ru, string en)
        {
            if (_ru < 0) { try { _ru = GameSettings.CurrentConfig.Language.ToString().IndexOf("russ", StringComparison.OrdinalIgnoreCase) >= 0 ? 1 : 0; } catch { _ru = 0; } }
            return _ru == 1 ? ru : en;
        }

        private static string _path;
        private static string ConfigPath()
        {
            if (_path != null) { return _path; }
            try
            {
                var pkg = ContentPackageManager.EnabledPackages.All.FirstOrDefault(p => p != null && p.Name == "NG Window Controls");
                string dir = pkg?.Dir;
                if (!string.IsNullOrEmpty(dir)) { _path = System.IO.Path.Combine(dir, "ngwindowcontrols_config.txt"); }
            }
            catch { }
            return _path;
        }

        public static void Load()
        {
            FabricatorOrder = 10; LinkedScale = 0.75f; MoveRestriction = false; ShowMoveArrows = false; // defaults
            try
            {
                string path = ConfigPath();
                if (path == null) { return; }
                if (!Barotrauma.IO.File.Exists(path)) { Save(); return; } // no config -> write defaults
                foreach (string line in Barotrauma.IO.File.ReadAllLines(path))
                {
                    string s = line.Trim();
                    int eq = s.IndexOf('=');
                    if (eq <= 0) { continue; }
                    string key = s.Substring(0, eq).Trim().ToLowerInvariant();
                    string val = s.Substring(eq + 1).Trim();
                    if (key == "fabricatororder" && int.TryParse(val, out int o)) { FabricatorOrder = Math.Clamp(o, 0, 20); }
                    else if (key == "linkedscale" && float.TryParse(val, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float sc)) { LinkedScale = Math.Clamp(sc, 0.2f, 1f); }
                    else if (key == "moverestriction") { MoveRestriction = val == "1" || val.Equals("true", StringComparison.OrdinalIgnoreCase) || val.Equals("on", StringComparison.OrdinalIgnoreCase); }
                    else if (key == "showmovearrows") { ShowMoveArrows = val == "1" || val.Equals("true", StringComparison.OrdinalIgnoreCase) || val.Equals("on", StringComparison.OrdinalIgnoreCase); }
                }
            }
            catch { }
        }

        public static void Save()
        {
            try
            {
                string path = ConfigPath();
                if (path == null) { return; }
                string text =
                    "fabricatororder=" + FabricatorOrder + "\r\n" +
                    "linkedscale=" + LinkedScale.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + "\r\n" +
                    "moverestriction=" + (MoveRestriction ? "true" : "false") + "\r\n" +
                    "showmovearrows=" + (ShowMoveArrows ? "true" : "false") + "\r\n";
                Barotrauma.IO.File.WriteAllText(path, text);
            }
            catch { }
        }

        // Console command: ngwindow menu | status | order <n> | scale <0.2..1 | %> | restrict <on|off>
        // RUS: Команда: ngwindow menu | status | order <n> | scale <0.2..1 | %> | restrict <on|off>
        public static void RegisterCommand()
        {
            UnregisterCommand();
            DebugConsole.Commands.Add(new DebugConsole.Command(
                "ngwindow",
                "NG Window Controls: ngwindow menu | status | order <n> | scale <0.2..1 | %> | restrict <on|off>",
                args =>
                {
                    try
                    {
                        string sub = args != null && args.Length > 0 ? args[0].ToLowerInvariant() : "status";
                        if (sub == "menu") { Menu.Toggle(); return; }
                        if (sub == "" || sub == "status") { Print(); return; }
                        if (sub == "order" && args.Length > 1 && int.TryParse(args[1], out int o))
                        {
                            FabricatorOrder = Math.Clamp(o, 0, 20); Save(); Print(); return;
                        }
                        if (sub == "scale" && args.Length > 1)
                        {
                            string v = args[1].Replace("%", "").Replace(",", ".");
                            if (float.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float sc))
                            {
                                if (sc > 1f) { sc /= 100f; } // accept "75" as 0.75   // RUS: «75» = 0.75
                                LinkedScale = Math.Clamp(sc, 0.2f, 1f); Save(); Print(); return;
                            }
                        }
                        if (sub == "restrict" && args.Length > 1)
                        {
                            string v = args[1].ToLowerInvariant();
                            MoveRestriction = v == "on" || v == "1" || v == "true"; Save(); Print(); return;
                        }
                        if (sub == "arrows" && args.Length > 1)
                        {
                            string v = args[1].ToLowerInvariant();
                            ShowMoveArrows = v == "on" || v == "1" || v == "true"; Save(); Print(); return;
                        }
                        DebugConsole.NewMessage(T("Использование: ngwindow menu | status | order <n> | scale <0.2..1 | %> | restrict <on|off> | arrows <on|off>",
                                                  "Usage: ngwindow menu | status | order <n> | scale <0.2..1 | %> | restrict <on|off> | arrows <on|off>"), Color.Orange);
                    }
                    catch (Exception ex) { DebugConsole.NewMessage("ngwindow error: " + ex.Message, Color.Red); }
                }));
        }

        private static void Print()
        {
            DebugConsole.NewMessage("[NG] [Window Controls] " + T("Настройки: ", "Settings: ")
                + "order=" + FabricatorOrder
                + ", scale=" + LinkedScale.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)
                + ", restrict=" + (MoveRestriction ? "ON" : "OFF")
                + ", arrows=" + (ShowMoveArrows ? "ON" : "OFF"), Color.LightCyan);
        }

        public static void UnregisterCommand()
        {
            try
            {
                var existing = DebugConsole.Commands.Find(c => c.Names.Any(n => n.Value.Equals("ngwindow", StringComparison.OrdinalIgnoreCase)));
                if (existing != null) { DebugConsole.Commands.Remove(existing); }
            }
            catch { }
        }
    }

    // ============================================================================================
    //  Simple read-only settings window (like NG Logger's menu), centered on screen. Lists the current
    //  settings + their status; values are changed via the `ngwindow` console command. Open: `ngwindow menu`.
    //  RUS: Простое read-only окно настроек (как меню NG Logger), по центру экрана. Перечисляет текущие
    //  RUS: настройки + их статус; значения меняются командой `ngwindow`. Открыть: `ngwindow menu`.
    // ============================================================================================
    internal static class Menu
    {
        private static GUIFrame _frame;
        public static bool IsOpen => _frame != null;
        public static void Toggle() { if (_frame == null) { Open(); } else { Close(); } }
        public static void Close() { try { if (_frame != null) { _frame.RectTransform.Parent = null; } } catch { } _frame = null; }

        private static int _ru = -1;
        private static string T(string ru, string en)
        {
            if (_ru < 0) { try { _ru = GameSettings.CurrentConfig.Language.ToString().IndexOf("russ", StringComparison.OrdinalIgnoreCase) >= 0 ? 1 : 0; } catch { _ru = 0; } }
            return _ru == 1 ? ru : en;
        }

        public static void Open()
        {
            try
            {
                if (_frame != null) { return; }
                if (GUI.Canvas == null) { return; }

                _frame = new GUIFrame(new RectTransform(new Vector2(0.28f, 0.46f), GUI.Canvas, Anchor.Center, minSize: new Point(400, 390)),
                    style: null, color: new Color(14, 17, 24, 245));
                var col = new GUILayoutGroup(new RectTransform(new Vector2(0.92f, 0.9f), _frame.RectTransform, Anchor.Center)) { Stretch = true, RelativeSpacing = 0.03f };

                new GUITextBlock(new RectTransform(new Vector2(1f, 0.13f), col.RectTransform), "NG Window Controls",
                    font: GUIStyle.SubHeadingFont, textAlignment: Alignment.Center) { CanBeFocused = false };
                new GUITextBlock(new RectTransform(new Vector2(1f, 0.08f), col.RectTransform), T("Настройки (наведи на строку — описание)", "Settings (hover a row for a description)"),
                    font: GUIStyle.SmallFont, textAlignment: Alignment.Center) { TextColor = Color.LightCyan, CanBeFocused = false };

                AddStepper(col,
                    () => T("Слой фабрикаторов", "Fabricator order"),
                    T("Слой отрисовки окна фабрикатора по умолчанию. Чем больше — тем выше окно над связанными контейнерами.",
                      "Default draw layer of fabricator windows. Higher = the window sits above its linked containers."),
                    () => Settings.FabricatorOrder.ToString(),
                    () => Settings.FabricatorOrder = Math.Clamp(Settings.FabricatorOrder - 1, 0, 20),
                    () => Settings.FabricatorOrder = Math.Clamp(Settings.FabricatorOrder + 1, 0, 20));

                AddStepper(col,
                    () => T("Масштаб контейнеров", "Container scale"),
                    T("Во сколько уменьшать окна контейнеров, открытых вместе с фабрикатором (привязанных). Контейнер, открытый сам по себе, не уменьшается.",
                      "How much to shrink container windows opened with a fabricator (linked ones). A container opened on its own is not shrunk."),
                    () => (int)Math.Round(Settings.LinkedScale * 100) + "%",
                    () => Settings.LinkedScale = Math.Clamp((float)Math.Round(Settings.LinkedScale - 0.05f, 2), 0.2f, 1f),
                    () => Settings.LinkedScale = Math.Clamp((float)Math.Round(Settings.LinkedScale + 0.05f, 2), 0.2f, 1f));

                AddToggle(col,
                    () => T("Ограничение перемещения", "Move restriction"),
                    T("ВКЛ — нельзя поставить окно с перекрытием другого (как в ванили, красный откат). ВЫКЛ — перемещение свободное.",
                      "ON — you can't drop a window overlapping another (vanilla red-revert). OFF — free movement."),
                    () => Settings.MoveRestriction ? T("ВКЛ", "ON") : T("ВЫКЛ", "OFF"),
                    () => Settings.MoveRestriction = !Settings.MoveRestriction);

                AddToggle(col,
                    () => T("Стрелки сдвига (5%)", "Move arrows (5%)"),
                    T("Показывать на окнах кнопки-стрелки сдвига на 5%. По умолчанию скрыты. Применяется при следующем открытии окна.",
                      "Show the 5%-move arrow buttons on windows. Hidden by default. Applies the next time a window is opened."),
                    () => Settings.ShowMoveArrows ? T("ВКЛ", "ON") : T("ВЫКЛ", "OFF"),
                    () => Settings.ShowMoveArrows = !Settings.ShowMoveArrows);

                var close = new GUIButton(new RectTransform(new Vector2(1f, 0.13f), col.RectTransform), T("Закрыть", "Close"), style: "GUIButtonSmall");
                close.OnClicked = (b, o) => { Close(); return true; };
            }
            catch { _frame = null; }
        }

        // Row: name (hover shows the hint) + [-] value [+]. Each button changes the setting + saves; values
        // are read live by the patches so changes apply immediately. Labels live-update via TextGetter.
        // RUS: Строка: имя (наведение — подсказка) + [-] значение [+]. Кнопки меняют настройку + сохраняют;
        // RUS: значения читаются патчами live, так что применяются сразу. Метки обновляются через TextGetter.
        private static void AddStepper(GUILayoutGroup col, Func<string> name, string tip, Func<string> value, Action minus, Action plus)
        {
            var row = new GUILayoutGroup(new RectTransform(new Vector2(1f, 0.14f), col.RectTransform), isHorizontal: true) { Stretch = true, RelativeSpacing = 0.015f };
            new GUITextBlock(new RectTransform(new Vector2(0.52f, 1f), row.RectTransform), name(),
                font: GUIStyle.SmallFont, textAlignment: Alignment.CenterLeft, wrap: true) { CanBeFocused = true, ToolTip = tip, TextGetter = () => name() };
            var minusBtn = new GUIButton(new RectTransform(new Vector2(0.13f, 1f), row.RectTransform), "-", style: "GUIButtonSmall") { ToolTip = tip };
            minusBtn.OnClicked = (b, o) => { try { minus(); Settings.Save(); } catch { } return true; };
            new GUITextBlock(new RectTransform(new Vector2(0.22f, 1f), row.RectTransform), value(),
                font: GUIStyle.SmallFont, textAlignment: Alignment.Center) { CanBeFocused = false, TextGetter = () => value() };
            var plusBtn = new GUIButton(new RectTransform(new Vector2(0.13f, 1f), row.RectTransform), "+", style: "GUIButtonSmall") { ToolTip = tip };
            plusBtn.OnClicked = (b, o) => { try { plus(); Settings.Save(); } catch { } return true; };
        }

        // Row: name (hover shows the hint) + [ON/OFF] toggle button.
        // RUS: Строка: имя (наведение — подсказка) + кнопка-тумблер [ВКЛ/ВЫКЛ].
        private static void AddToggle(GUILayoutGroup col, Func<string> name, string tip, Func<string> value, Action toggle)
        {
            var row = new GUILayoutGroup(new RectTransform(new Vector2(1f, 0.14f), col.RectTransform), isHorizontal: true) { Stretch = true, RelativeSpacing = 0.015f };
            new GUITextBlock(new RectTransform(new Vector2(0.6f, 1f), row.RectTransform), name(),
                font: GUIStyle.SmallFont, textAlignment: Alignment.CenterLeft, wrap: true) { CanBeFocused = true, ToolTip = tip, TextGetter = () => name() };
            var btn = new GUIButton(new RectTransform(new Vector2(0.38f, 1f), row.RectTransform), value(), style: "GUIButtonSmall") { ToolTip = tip };
            if (btn.TextBlock != null) { btn.TextBlock.TextGetter = () => value(); }
            btn.OnClicked = (b, o) => { try { toggle(); Settings.Save(); } catch { } return true; };
        }

        // Added to the GUI update list every frame (from the CharacterHUD postfix) with a high order so it
        // stays on top. Without a per-frame AddToGUIUpdateList a GUI.Canvas child isn't drawn.
        // RUS: Каждый кадр добавляется в GUI-список (из постфикса CharacterHUD) с высоким order, чтобы быть
        // RUS: поверх. Без покадрового AddToGUIUpdateList ребёнок GUI.Canvas не рисуется.
        public static void AddToGUIUpdateList()
        {
            if (_frame == null) { return; }
            try { _frame.AddToGUIUpdateList(order: 100); } catch { }
        }
    }
}
