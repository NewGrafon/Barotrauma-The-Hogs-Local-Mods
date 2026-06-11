using System;
using System.Linq;
using System.Reflection;
using Barotrauma;
using Microsoft.Xna.Framework;

namespace NetEventLogger
{
    // ==========================================================================================
    //  КЛИЕНТСКОЕ окно-меню (NG Logger & Optimizations).
    //  Открывается хоткеем F7 или командой `clientperf menu`. Отдельное окно на GUI.Canvas — НЕ
    //  ставит игру на паузу (важно: бенчмарк должен мерить реальную нагрузку, окно при этом открыто).
    //  Кнопки: «Бенчмарк 60с» (off->reset->on->60с->off->отчёт->reset), «Копировать результат»
    //  (в буфер обмена, чтобы кидать текстом), тумблер «Fix #3» (оптимизация контейнеров, на лету).
    //  Клиентский файл (CSharp/Client) — компилируется только в клиентскую сборку (GUI там).
    // ==========================================================================================
    public static class ClientPerfMenu
    {
        private static GUIFrame      _frame;
        private static GUITextBlock  _status;
        private static GUIButton     _benchBtn;
        private static GUIButton     _fixBtn;   // фикс 1 — оптимизация контейнеров   // RUS: fix 1 — container optimization
        private static GUIButton     _fix2Btn;  // фикс 2 — оптимизация поиска предметов рядом   // RUS: fix 2 — nearby-item search optimization
        private static GUIListBox    _resultList;

        // Fix categories shown as 3 tabs. The current fixes are all Shared.
        // RUS: Категории фиксов как 3 вкладки. Сейчас все фиксы — Shared.
        private enum Tab { Client, Server, Shared }
        private static Tab           _activeTab = Tab.Shared;
        private static GUILayoutGroup _tabContent;
        private static GUIButton     _tabClientBtn, _tabServerBtn, _tabSharedBtn;

        public static bool IsOpen => _frame != null;

        public static void Toggle() { if (_frame == null) { Open(); } else { Close(); } }

        public static void Close()
        {
            try { if (_frame != null) { _frame.RectTransform.Parent = null; } } catch { }
            _frame = null; _status = null; _benchBtn = null; _fixBtn = null; _fix2Btn = null; _resultList = null;
            _tabContent = null; _tabClientBtn = null; _tabServerBtn = null; _tabSharedBtn = null;
        }

        public static void Open()
        {
            try
            {
                if (_frame != null) { return; }
                if (GUI.Canvas == null) { return; }

                // style:null + явный непрозрачный цвет = гарантированно видимая тёмная панель
                // (стиль по умолчанию у GUIFrame может быть прозрачным).
                _frame = new GUIFrame(new RectTransform(new Vector2(0.34f, 0.66f), GUI.Canvas, Anchor.CenterLeft,
                    minSize: new Point(380, 430)) { RelativeOffset = new Vector2(0.012f, 0f) },
                    style: null, color: new Color(14, 17, 24, 245));

                var col = new GUILayoutGroup(new RectTransform(new Vector2(0.95f, 0.95f), _frame.RectTransform, Anchor.Center))
                {
                    Stretch = true,
                    RelativeSpacing = 0.012f
                };

                new GUITextBlock(new RectTransform(new Vector2(1f, 0.06f), col.RectTransform),
                    "NG Logger And Optimizations", font: GUIStyle.SubHeadingFont, textAlignment: Alignment.Center);

                _status = new GUITextBlock(new RectTransform(new Vector2(1f, 0.07f), col.RectTransform),
                    Benchmark.StatusText, font: GUIStyle.SmallFont, textAlignment: Alignment.Center, wrap: true);

                // ряд 1: бенчмарк + копировать
                var row1 = new GUILayoutGroup(new RectTransform(new Vector2(1f, 0.07f), col.RectTransform), isHorizontal: true)
                { Stretch = true, RelativeSpacing = 0.02f };
                _benchBtn = new GUIButton(new RectTransform(new Vector2(0.5f, 1f), row1.RectTransform), BenchLabel(), style: "GUIButtonSmall");
                _benchBtn.OnClicked = (b, o) => { Benchmark.StartOrCancel(); SyncDynamic(); return true; };
                var copyBtn = new GUIButton(new RectTransform(new Vector2(0.5f, 1f), row1.RectTransform),
                    Loc.T("Копировать результат", "Copy result"), style: "GUIButtonSmall");
                copyBtn.OnClicked = (b, o) => { CopyResults(); return true; };

                // ряд 2: ВКЛАДКИ фиксов (Клиентские / Серверные / Общие)
                // RUS: row 2: fix TABS (Client / Server / Shared)
                var tabRow = new GUILayoutGroup(new RectTransform(new Vector2(1f, 0.06f), col.RectTransform), isHorizontal: true)
                { Stretch = true, RelativeSpacing = 0.01f };
                _tabClientBtn = new GUIButton(new RectTransform(new Vector2(0.34f, 1f), tabRow.RectTransform), Loc.T("Клиентские", "Client"), style: "GUIButtonSmall");
                _tabClientBtn.OnClicked = (b, o) => { SelectTab(Tab.Client); return true; };
                _tabServerBtn = new GUIButton(new RectTransform(new Vector2(0.33f, 1f), tabRow.RectTransform), Loc.T("Серверные", "Server"), style: "GUIButtonSmall");
                _tabServerBtn.OnClicked = (b, o) => { SelectTab(Tab.Server); return true; };
                _tabSharedBtn = new GUIButton(new RectTransform(new Vector2(0.33f, 1f), tabRow.RectTransform), Loc.T("Общие", "Shared"), style: "GUIButtonSmall");
                _tabSharedBtn.OnClicked = (b, o) => { SelectTab(Tab.Shared); return true; };

                // контейнер тумблеров активной вкладки   // RUS: container for the active tab's toggles
                var tabFrame = new GUIFrame(new RectTransform(new Vector2(1f, 0.12f), col.RectTransform), style: null, color: new Color(0, 0, 0, 70));
                _tabContent = new GUILayoutGroup(new RectTransform(new Vector2(0.98f, 0.9f), tabFrame.RectTransform, Anchor.Center))
                { Stretch = true, RelativeSpacing = 0.05f };
                SelectTab(_activeTab);

                // ряд 3: закрыть
                var row3 = new GUILayoutGroup(new RectTransform(new Vector2(1f, 0.06f), col.RectTransform), isHorizontal: true)
                { Stretch = true };
                var closeBtn = new GUIButton(new RectTransform(new Vector2(1f, 1f), row3.RectTransform),
                    Loc.T("Закрыть", "Close"), style: "GUIButtonSmall");
                closeBtn.OnClicked = (b, o) => { Close(); return true; };

                // область результата (скролл, моноширинный шрифт для ровных столбцов)
                _resultList = new GUIListBox(new RectTransform(new Vector2(1f, 0.6f), col.RectTransform));

                RefreshResult();
                ClientPerf.Log(Loc.T("Меню открыто.", "Menu opened."), Color.LightGreen);
            }
            catch (Exception ex) { ClientPerf.Log("Не удалось открыть меню: " + ex, Color.Red); _frame = null; }
        }

        // Зовётся каждый кадр из ClientPerf.Tick: пока окно открыто — обновляем статус/кнопки/результат.
        public static void Update()
        {
            if (_frame == null) { return; }
            try
            {
                // БЕЗ этого окно не рисуется: компонент на GUI.Canvas нужно каждый кадр заносить
                // в список отрисовки GUI (движок чистит его каждый кадр).
                _frame.AddToGUIUpdateList();

                if (_status != null)   { _status.Text = Benchmark.StatusText; }
                if (_benchBtn != null) { _benchBtn.Text = BenchLabel(); }
                if (Benchmark.ResultIsNew)
                {
                    Benchmark.ResultIsNew = false;
                    RefreshResult();
                }
            }
            catch { }
        }

        private static string BenchLabel() => Benchmark.Running ? Loc.T("Стоп замер", "Stop") : Loc.T("Бенчмарк 60с", "Benchmark 60s");
        private static string FixLabel()   => Loc.OptShort  + ": " + (NGContainerOpt.ContainedEffectsOptPlugin.Enabled ? Loc.On : Loc.Off);
        private static string Fix2Label()  => Loc.Opt2Short + ": " + (NGNearbyOpt.NearbyTargetsOptPlugin.Enabled  ? Loc.On : Loc.Off);

        // Switch fix tab and (re)build its toggles. Empty categories show "empty".
        // RUS: Переключить вкладку фиксов и пересобрать её тумблеры. Пустые категории показывают «пусто».
        private static void SelectTab(Tab tab)
        {
            _activeTab = tab;
            if (_tabContent == null) { return; }
            _tabContent.ClearChildren();
            _fixBtn = null; _fix2Btn = null;

            if (tab == Tab.Shared)
            {
                // both current fixes are Shared (run on client+server)
                // RUS: оба текущих фикса — Shared (работают на клиенте+сервере)
                _fixBtn  = AddFixToggle(FixLabel(),  Loc.OptTip,  () => ToggleFix());
                _fix2Btn = AddFixToggle(Fix2Label(), Loc.Opt2Tip, () => ToggleFix2());
            }
            else
            {
                // Client / Server: no fixes yet   // RUS: Клиентские / Серверные: фиксов пока нет
                new GUITextBlock(new RectTransform(new Vector2(1f, 1f), _tabContent.RectTransform),
                    Loc.T("пусто", "empty"), textAlignment: Alignment.Center) { CanBeFocused = false };
            }
        }

        private static GUIButton AddFixToggle(string label, string tip, Action onClick)
        {
            var btn = new GUIButton(new RectTransform(new Vector2(1f, 0.45f), _tabContent.RectTransform), label, style: "GUIButtonSmall") { ToolTip = tip };
            btn.OnClicked = (b, o) => { onClick(); return true; };
            return btn;
        }

        private static void SyncDynamic()
        {
            if (_benchBtn != null) { _benchBtn.Text = BenchLabel(); }
            if (_status != null)   { _status.Text = Benchmark.StatusText; }
        }

        private static void ToggleFix()
        {
            bool now = !NGContainerOpt.ContainedEffectsOptPlugin.Enabled;
            NGContainerOpt.ContainedEffectsOptPlugin.SetEnabled(now);
            if (_fixBtn != null) { _fixBtn.Text = FixLabel(); }
            if (_status != null)
            {
                _status.Text = now
                    ? Loc.T("Оптимизация ВКЛ — контейнеры подрезаны.", "Optimization ON — containers trimmed.")
                    : Loc.T("Оптимизация ВЫКЛ — нагрузка вернулась (для сравнения). Прогони бенчмарк.",
                            "Optimization OFF — load is back (for comparison). Run the benchmark.");
            }
            ClientPerf.Log(Loc.OptName + ": " + (now ? Loc.On : Loc.Off), now ? Color.LightGreen : Color.Orange);
        }

        private static void ToggleFix2()
        {
            bool now = !NGNearbyOpt.NearbyTargetsOptPlugin.Enabled;
            NGNearbyOpt.NearbyTargetsOptPlugin.SetEnabled(now);
            if (_fix2Btn != null) { _fix2Btn.Text = Fix2Label(); }
            if (_status != null)
            {
                _status.Text = now
                    ? Loc.T("Оптимизация поиска предметов ВКЛ.", "Nearby-item search optimization ON.")
                    : Loc.T("Оптимизация поиска предметов ВЫКЛ (для сравнения). Прогони бенчмарк.",
                            "Nearby-item search optimization OFF (for comparison). Run the benchmark.");
            }
            ClientPerf.Log(Loc.Opt2Name + ": " + (now ? Loc.On : Loc.Off), now ? Color.LightGreen : Color.Orange);
        }

        private static void RefreshResult()
        {
            if (_resultList == null) { return; }
            _resultList.Content.ClearChildren();
            var lines = Benchmark.ResultLines;
            if (lines == null || lines.Count == 0)
            {
                new GUITextBlock(new RectTransform(new Vector2(1f, 0.06f), _resultList.Content.RectTransform),
                    Loc.T("Результата пока нет.\nНажми «Бенчмарк 60с» возле нагрузки (станция с НПС).",
                          "No result yet.\nClick «Benchmark 60s» near the load (a station with NPCs)."),
                    font: GUIStyle.SmallFont, wrap: true)
                { CanBeFocused = false };
                return;
            }
            // каждая строка своим цветом (как в консоли), шрифт моноширинный и на ~20% мельче (TextScale 0.8)
            foreach (var ln in lines)
            {
                new GUITextBlock(new RectTransform(new Vector2(1f, 0.031f), _resultList.Content.RectTransform),
                    string.IsNullOrEmpty(ln.Text) ? " " : ln.Text,
                    textColor: ln.Color, font: GUIStyle.MonospacedFont, textAlignment: Alignment.CenterLeft)
                { CanBeFocused = false, TextScale = 0.8f };
            }
            _resultList.BarScroll = 0f;
        }

        private static void CopyResults()
        {
            string text = Benchmark.ResultText ?? "";
            if (string.IsNullOrWhiteSpace(text))
            {
                if (_status != null) { _status.Text = Loc.T("Нечего копировать — сначала прогони бенчмарк.", "Nothing to copy — run the benchmark first."); }
                return;
            }
            bool ok = TrySetClipboard(text);
            if (_status != null) { _status.Text = ok ? Loc.T("Скопировано! Вставь текст в чат.", "Copied! Paste the text into chat.") : Loc.T("Не удалось скопировать (см. консоль).", "Copy failed (see console)."); }
            ClientPerf.Log(ok ? Loc.T("Результат бенчмарка скопирован в буфер обмена.", "Benchmark result copied to clipboard.") : Loc.T("Копирование не удалось.", "Copy failed."), ok ? Color.LightGreen : Color.Orange);
        }

        // --- буфер обмена (как в GM Menu): Barotrauma.Clipboard.SetText через рефлексию + фоллбэк скрытым GUITextBox ---
        private static MethodInfo _clipboardSetText;
        private static bool       _clipboardSearched;

        private static bool TrySetClipboard(string text)
        {
            try
            {
                MethodInfo m = FindClipboardSetText();
                if (m != null) { m.Invoke(null, new object[] { text }); return true; }
            }
            catch { }
            // фоллбэк: скрытый GUITextBox далеко за экраном + его внутренний CopySelectedText()
            try
            {
                if (GUI.Canvas != null)
                {
                    var rt = new RectTransform(new Vector2(0.01f, 0.01f), GUI.Canvas, Anchor.TopLeft) { AbsoluteOffset = new Point(-10000, -10000) };
                    var tb = new GUITextBox(rt, text) { Visible = false };
                    tb.Text = text;
                    tb.SelectAll();
                    MethodInfo copy = typeof(GUITextBox).GetMethod("CopySelectedText", BindingFlags.Instance | BindingFlags.NonPublic);
                    copy?.Invoke(tb, null);
                    tb.RectTransform.Parent = null;
                    return copy != null;
                }
            }
            catch { }
            return false;
        }

        private static MethodInfo FindClipboardSetText()
        {
            if (_clipboardSearched) { return _clipboardSetText; }
            _clipboardSearched = true;
            BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;
            foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type[] types;
                try { types = asm.GetTypes(); }
                catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(t => t != null).ToArray(); }
                catch { continue; }
                foreach (Type t in types)
                {
                    if (t == null || t.Name != "Clipboard") { continue; }
                    MethodInfo m = t.GetMethod("SetText", flags, null, new[] { typeof(string) }, null);
                    if (m != null) { _clipboardSetText = m; return m; }
                }
            }
            return null;
        }
    }
}
