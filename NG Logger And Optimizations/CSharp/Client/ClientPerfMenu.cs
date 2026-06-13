using System;
using System.Linq;
using System.Reflection;
using Barotrauma;
using Microsoft.Xna.Framework;

namespace NetEventLogger
{
    // ==========================================================================================
    //  CLIENT window/menu (NG Logger And Optimizations).
    //  Opened with the `clientperf menu` (alias `ngperf menu`) command. A standalone window on
    //  GUI.Canvas — it does NOT pause the game (important: the benchmark must measure real load with
    //  the window open). Buttons: "Benchmark 60s", "Copy result" (to the clipboard, for sharing as
    //  text), and the fix toggles (split into Client/Server/Shared tabs).
    //  Client file (CSharp/Client) — compiled only into the client assembly (the GUI lives there).
    //
    //  RUS: КЛИЕНТСКОЕ окно-меню (NG Logger And Optimizations).
    //  RUS: Открывается командой `clientperf menu` (алиас `ngperf menu`). Отдельное окно на GUI.Canvas —
    //  RUS: НЕ ставит игру на паузу (важно: бенчмарк должен мерить реальную нагрузку с открытым окном).
    //  RUS: Кнопки: «Бенчмарк 60с», «Копировать результат» (в буфер обмена, чтобы кидать текстом),
    //  RUS: и тумблеры фиксов (вкладки Клиентские/Серверные/Общие).
    //  RUS: Клиентский файл (CSharp/Client) — компилируется только в клиентскую сборку (GUI там).
    // ==========================================================================================
    public static class ClientPerfMenu
    {
        private static GUIFrame      _frame;
        private static GUITextBlock  _status;
        private static GUIListBox    _resultList;
        private static GUITextBlock  _histLabel;   // "i/n • date" of the viewed history entry   // RUS: «i/n • дата» просматриваемой записи истории
        private static GUIButton     _histPrevBtn, _histNextBtn;

        // Two global sections under the title: Client and Server. The Server section + its selector are only
        // available to privileged clients (host/admin); everyone else just sees the Client section (no selector).
        // Each section holds its own benchmark + its own fix toggles (client-side or server-side).
        // RUS: Два глобальных раздела под title: Клиент и Сервер. Раздел Сервер и его селектор доступны только
        // RUS: привилегированным (хост/админ); остальные видят только раздел Клиент (без селектора). У каждого
        // RUS: раздела свой бенчмарк + свои тумблеры фиксов (клиентские или серверные).
        private enum Section { Client, Server }
        private static Section        _section = Section.Client;
        private static GUILayoutGroup _selectorRow;
        private static GUIButton      _secClientBtn, _secServerBtn;
        private static GUILayoutGroup _content;   // active section's content (rebuilt on switch)   // RUS: содержимое активного раздела (пересобирается при переключении)
        private static GUIButton      _benchBtn;   // active section's benchmark button   // RUS: кнопка бенчмарка активного раздела
        private static GUIButton      _durBtn;     // benchmark duration cycle button (shared 15/30/60/300s)   // RUS: кнопка переключения длительности (общая 15/30/60/300с)
        private static readonly GUIButton[] _fixBtns = new GUIButton[OptFixes.Count]; // active section's fix toggles   // RUS: тумблеры фиксов активного раздела

        public static bool IsOpen => _frame != null;

        public static void Toggle() { if (_frame == null) { Open(); } else { Close(); } }

        public static void Close()
        {
            try { if (_frame != null) { _frame.RectTransform.Parent = null; } } catch { }
            _frame = null; _status = null; _resultList = null;
            _selectorRow = null; _secClientBtn = null; _secServerBtn = null; _content = null; _benchBtn = null; _durBtn = null;
            _histLabel = null; _histPrevBtn = null; _histNextBtn = null;
            for (int i = 0; i < OptFixes.Count; i++) { _fixBtns[i] = null; }
        }

        public static void Open()
        {
            try
            {
                if (_frame != null) { return; }
                if (GUI.Canvas == null) { return; }

                // style:null + an explicit opaque color = a guaranteed-visible dark panel
                // (the default GUIFrame style can be transparent).
                // RUS: style:null + явный непрозрачный цвет = гарантированно видимая тёмная панель
                // RUS: (стиль по умолчанию у GUIFrame может быть прозрачным).
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

                bool privileged = ClientOptNet.InMP && ClientOptNet.CanControlServer();
                if (!privileged) { _section = Section.Client; }

                // section selector (Client / Server) right under the title — only for privileged clients
                // RUS: селектор разделов (Клиент / Сервер) сразу под title — только привилегированным
                if (privileged)
                {
                    _selectorRow = new GUILayoutGroup(new RectTransform(new Vector2(1f, 0.06f), col.RectTransform), isHorizontal: true) { Stretch = true, RelativeSpacing = 0.02f };
                    _secClientBtn = new GUIButton(new RectTransform(new Vector2(0.5f, 1f), _selectorRow.RectTransform), Loc.ClientCol, style: "GUIButtonSmall");
                    _secClientBtn.OnClicked = (b, o) => { SelectSection(Section.Client); return true; };
                    _secServerBtn = new GUIButton(new RectTransform(new Vector2(0.5f, 1f), _selectorRow.RectTransform), Loc.ServerCol, style: "GUIButtonSmall");
                    _secServerBtn.OnClicked = (b, o) => { SelectSection(Section.Server); return true; };
                }

                _status = new GUITextBlock(new RectTransform(new Vector2(1f, 0.06f), col.RectTransform),
                    Benchmark.StatusText, font: GUIStyle.SmallFont, textAlignment: Alignment.Center, wrap: true);

                // active section's content (a benchmark row + the fix-toggle rows), rebuilt on section switch
                // RUS: содержимое активного раздела (ряд бенчмарка + ряды тумблеров фиксов), пересобирается при смене раздела
                var contentFrame = new GUIFrame(new RectTransform(new Vector2(1f, 0.36f), col.RectTransform), style: null, color: new Color(0, 0, 0, 70));
                _content = new GUILayoutGroup(new RectTransform(new Vector2(0.97f, 0.94f), contentFrame.RectTransform, Anchor.Center)) { Stretch = true, RelativeSpacing = 0.03f };
                SelectSection(_section);

                // close
                // RUS: закрыть
                var row3 = new GUILayoutGroup(new RectTransform(new Vector2(1f, 0.06f), col.RectTransform), isHorizontal: true) { Stretch = true };
                var closeBtn = new GUIButton(new RectTransform(new Vector2(1f, 1f), row3.RectTransform),
                    Loc.T("Закрыть", "Close"), style: "GUIButtonSmall");
                closeBtn.OnClicked = (b, o) => { Close(); return true; };

                // history navigation row: [◀] [ i/n • date ] [▶] — steps through past benchmark results
                // RUS: ряд навигации по истории: [◀] [ i/n • дата ] [▶] — листает прошлые результаты бенчмарков
                var histRow = new GUILayoutGroup(new RectTransform(new Vector2(1f, 0.05f), col.RectTransform), isHorizontal: true) { Stretch = true, RelativeSpacing = 0.01f };
                _histPrevBtn = new GUIButton(new RectTransform(new Vector2(0.14f, 1f), histRow.RectTransform), "<---", style: "GUIButtonSmall");
                _histPrevBtn.OnClicked = (b, o) => { HistPrev(); return true; };
                _histLabel = new GUITextBlock(new RectTransform(new Vector2(0.72f, 1f), histRow.RectTransform), "", font: GUIStyle.SmallFont, textAlignment: Alignment.Center) { CanBeFocused = false };
                _histNextBtn = new GUIButton(new RectTransform(new Vector2(0.14f, 1f), histRow.RectTransform), "--->", style: "GUIButtonSmall");
                _histNextBtn.OnClicked = (b, o) => { HistNext(); return true; };

                // result area (scrollable, monospaced font for aligned columns)
                // RUS: область результата (скролл, моноширинный шрифт для ровных столбцов)
                _resultList = new GUIListBox(new RectTransform(new Vector2(1f, 0.39f), col.RectTransform));

                RefreshResult();
                try { ClientOptNet.RequestServerState(); } catch { } // pull current server states for the indicators   // RUS: подтянуть текущие серверные состояния для индикаторов
                ClientPerf.Log(Loc.T("Меню открыто.", "Menu opened."), Color.LightGreen);
            }
            catch (Exception ex) { ClientPerf.Log(Loc.T("Не удалось открыть меню: ", "Failed to open the menu: ") + ex, Color.Red); _frame = null; }
        }

        // Called every frame from ClientPerf.Tick: while the window is open, refresh status/buttons/result.
        // RUS: Зовётся каждый кадр из ClientPerf.Tick: пока окно открыто — обновляем статус/кнопки/результат.
        public static void Update()
        {
            if (_frame == null) { return; }
            try
            {
                // WITHOUT this the window isn't drawn: a component on GUI.Canvas must be added to the
                // GUI update list every frame (the engine clears that list each frame).
                // RUS: БЕЗ этого окно не рисуется: компонент на GUI.Canvas нужно каждый кадр заносить
                // RUS: в список отрисовки GUI (движок чистит его каждый кадр).
                _frame.AddToGUIUpdateList();
                ClientOptNet.ServerBenchTick(); // un-stick the server-bench button on timeout   // RUS: снять «зависшую» кнопку сервер-бенча по таймауту

                // privilege can change: hide the selector + drop to the Client section if it was lost
                // RUS: права могут измениться: прячем селектор + падаем в раздел Клиент, если их отобрали
                bool privileged = ClientOptNet.InMP && ClientOptNet.CanControlServer();
                if (_selectorRow != null) { _selectorRow.Visible = privileged; }
                if (!privileged && _section == Section.Server) { SelectSection(Section.Client); }

                if (_status != null)   { _status.Text = _section == Section.Client ? Benchmark.StatusText : ClientOptNet.ServerBenchStatus; }
                if (_benchBtn != null) { _benchBtn.Text = _section == Section.Client ? BenchLabel() : ServerBenchLabel(); }
                if (_durBtn != null)   { _durBtn.Text = DurationLabel(); }
                for (int i = 0; i < OptFixes.Count; i++)
                {
                    if (_fixBtns[i] != null) { _fixBtns[i].Text = _section == Section.Client ? ClientBtnLabel(i) : ServerBtnLabel(i); }
                }
                // refresh the result window when the active section's history changed (new entry / navigation)
                // RUS: обновляем окно результата, когда история активного раздела изменилась (новая запись / навигация)
                var hist = ActiveHistory();
                if (hist != null && hist.Dirty) { hist.Dirty = false; RefreshResult(); }
            }
            catch { }
        }

        private static string BenchLabel() => Benchmark.Running ? Loc.T("Стоп замер", "Stop") : Loc.T("Бенчмарк", "Benchmark");
        private static string ServerBenchLabel() => ClientOptNet.ServerBenchRunning ? Loc.T("Стоп сервер-бенч", "Stop server bench") : Loc.T("Серверный бенчмарк", "Server benchmark");
        private static string DurationLabel()
        {
            double d = _section == Section.Client ? Benchmark.DurationSec : ClientOptNet.ServerBenchDuration;
            return d >= 60 ? (d / 60).ToString("0.#") + (Loc.Ru ? "мин" : "min") : d.ToString("0") + (Loc.Ru ? "с" : "s");
        }
        private static string ClientBtnLabel(int fix) => Loc.ClientCol + ": " + (OptFixes.GetEnabled(fix) ? Loc.On : Loc.Off);
        private static string ServerBtnLabel(int fix)
        {
            string st = (!ClientOptNet.InMP || !ClientOptNet.ServerKnown(fix)) ? Loc.Unknown : (ClientOptNet.ServerState(fix) ? Loc.On : Loc.Off);
            return Loc.ServerCol + ": " + st;
        }

        // (Re)build the active section's content: a benchmark row + one toggle row per fix.
        // Client section -> client benchmark/copy + client toggles; Server section -> server benchmark/load + server toggles.
        // RUS: (Пере)собрать содержимое активного раздела: ряд бенчмарка + по ряду-тумблеру на фикс.
        // RUS: Раздел Клиент -> клиентский бенч/копир + клиентские тумблеры; Сервер -> серверный бенч/нагрузка + серверные тумблеры.
        private static void SelectSection(Section section)
        {
            _section = section;
            if (_content == null) { return; }
            _content.ClearChildren();
            _benchBtn = null;
            for (int i = 0; i < OptFixes.Count; i++) { _fixBtns[i] = null; }

            // benchmark row: [run benchmark] [duration cycle] [secondary: copy (client) / load snapshot (server)]
            // RUS: ряд бенчмарка: [запуск] [переключение длительности] [вторичная: копир. (клиент) / снимок нагрузки (сервер)]
            var benchRow = new GUILayoutGroup(new RectTransform(new Vector2(1f, 0.24f), _content.RectTransform), isHorizontal: true) { Stretch = true, RelativeSpacing = 0.015f };
            if (section == Section.Client)
            {
                _benchBtn = new GUIButton(new RectTransform(new Vector2(0.44f, 1f), benchRow.RectTransform), BenchLabel(), style: "GUIButtonSmall") { ToolTip = Loc.ClientTip };
                _benchBtn.OnClicked = (b, o) => { Benchmark.StartOrCancel(); SyncDynamic(); return true; };
                _durBtn = new GUIButton(new RectTransform(new Vector2(0.18f, 1f), benchRow.RectTransform), DurationLabel(), style: "GUIButtonSmall") { ToolTip = Loc.DurationTip };
                _durBtn.OnClicked = (b, o) => { if (_section == Section.Client) { Benchmark.CycleDuration(); } else { ClientOptNet.CycleServerBenchDuration(); } if (_durBtn != null) { _durBtn.Text = DurationLabel(); } return true; };
                var copyBtn = new GUIButton(new RectTransform(new Vector2(0.38f, 1f), benchRow.RectTransform), Loc.T("Копировать", "Copy"), style: "GUIButtonSmall");
                copyBtn.OnClicked = (b, o) => { CopyResults(); return true; };
            }
            else
            {
                _benchBtn = new GUIButton(new RectTransform(new Vector2(0.5f, 1f), benchRow.RectTransform), ServerBenchLabel(), style: "GUIButtonSmall") { ToolTip = Loc.ServerBenchTip };
                _benchBtn.OnClicked = (b, o) => { ClientOptNet.StartOrCancelServerBench(); return true; };
                _durBtn = new GUIButton(new RectTransform(new Vector2(0.18f, 1f), benchRow.RectTransform), DurationLabel(), style: "GUIButtonSmall") { ToolTip = Loc.DurationTip };
                _durBtn.OnClicked = (b, o) => { if (_section == Section.Client) { Benchmark.CycleDuration(); } else { ClientOptNet.CycleServerBenchDuration(); } if (_durBtn != null) { _durBtn.Text = DurationLabel(); } return true; };
                var loadBtn = new GUIButton(new RectTransform(new Vector2(0.32f, 1f), benchRow.RectTransform), Loc.ServerSnap, style: "GUIButtonSmall") { ToolTip = Loc.ServerSnapTip };
                loadBtn.OnClicked = (b, o) => { ClientOptNet.RequestServerPerf(); return true; };
            }

            // one toggle row per fix
            // RUS: по ряду-тумблеру на фикс
            for (int i = 0; i < OptFixes.Count; i++)
            {
                int fix = i; // capture for the lambdas   // RUS: захват для лямбд
                var row = new GUILayoutGroup(new RectTransform(new Vector2(1f, 0.25f), _content.RectTransform), isHorizontal: true) { Stretch = true, RelativeSpacing = 0.012f };
                string nm = OptFixes.ShortName(fix) + (OptFixes.IsExperimental(fix) ? " [" + Loc.Experimental + "]" : "");
                new GUITextBlock(new RectTransform(new Vector2(0.49f, 1f), row.RectTransform), nm,
                    font: GUIStyle.SmallFont, textAlignment: Alignment.CenterLeft, wrap: true) { ToolTip = OptFixes.Tip(fix), CanBeFocused = false };

                if (section == Section.Client)
                {
                    _fixBtns[fix] = new GUIButton(new RectTransform(new Vector2(0.37f, 1f), row.RectTransform), ClientBtnLabel(fix), style: "GUIButtonSmall") { ToolTip = Loc.ClientTip };
                    _fixBtns[fix].OnClicked = (b, o) => { ToggleClient(fix); return true; };
                }
                else
                {
                    _fixBtns[fix] = new GUIButton(new RectTransform(new Vector2(0.37f, 1f), row.RectTransform), ServerBtnLabel(fix), style: "GUIButtonSmall") { ToolTip = Loc.ServerHostTip };
                    _fixBtns[fix].OnClicked = (b, o) => { ToggleServer(fix); return true; };
                }

                // info stub: its only purpose is to show the detailed fix description on hover
                // RUS: инфо-заглушка: её единственная цель — показывать подробное описание фикса при наведении
                new GUIButton(new RectTransform(new Vector2(0.12f, 1f), row.RectTransform), "?", style: "GUIButtonSmall")
                { ToolTip = OptFixes.Tip(fix) };
            }
            SyncDynamic();
            // show the new section's benchmark history
            // RUS: показать историю бенчмарков нового раздела
            var h = ActiveHistory();
            if (h != null) { h.Dirty = false; }
            RefreshResult();
        }

        private static void SyncDynamic()
        {
            if (_benchBtn != null) { _benchBtn.Text = _section == Section.Client ? BenchLabel() : ServerBenchLabel(); }
            if (_status != null)   { _status.Text = _section == Section.Client ? Benchmark.StatusText : ClientOptNet.ServerBenchStatus; }
        }

        // Toggle a fix on THIS client (local, applies + persists).
        // RUS: Переключить фикс на ЭТОМ клиенте (локально, применяет + сохраняет).
        private static void ToggleClient(int fix)
        {
            bool now = !OptFixes.GetEnabled(fix);
            OptConfig.SetFixByIndex(fix, now);
            if (_section == Section.Client && _fixBtns[fix] != null) { _fixBtns[fix].Text = ClientBtnLabel(fix); }
            if (_status != null)
            {
                _status.Text = Loc.Ru
                    ? $"{OptFixes.ShortName(fix)} (клиент): {(now ? "ВКЛ" : "ВЫКЛ")}." + (now ? "" : " Прогони бенчмарк для сравнения.")
                    : $"{OptFixes.ShortName(fix)} (client): {(now ? "ON" : "OFF")}." + (now ? "" : " Run the benchmark to compare.");
            }
            ClientPerf.Log(OptFixes.ShortName(fix) + " [" + Loc.ClientCol + "]: " + (now ? Loc.On : Loc.Off), now ? Color.LightGreen : Color.Orange);
        }

        // Request toggling a fix on the SERVER (host/admin only; the server re-validates and broadcasts back).
        // RUS: Запросить переключение фикса на СЕРВЕРЕ (только хост/админ; сервер перепроверяет и рассылает обратно).
        private static void ToggleServer(int fix)
        {
            if (!ClientOptNet.InMP) { if (_status != null) { _status.Text = Loc.ServerMPOnly; } return; }
            if (!ClientOptNet.CanControlServer())
            {
                if (_status != null) { _status.Text = Loc.T("Серверный фикс может менять только хост/админ.", "Only the host/admin can change a server fix."); }
                return;
            }
            // flip the known server state (if unknown, assume it's currently ON -> turn it off)
            // RUS: переключаем известное серверное состояние (если неизвестно — считаем что сейчас ВКЛ -> выключаем)
            bool current = !ClientOptNet.ServerKnown(fix) || ClientOptNet.ServerState(fix);
            ClientOptNet.RequestSetServer(fix, !current);
            if (_status != null) { _status.Text = Loc.T("Запрос на сервер отправлен… (обновится по подтверждению).", "Request sent to the server… (updates on confirmation)."); }
        }

        // The history shown depends on the active section: Client -> client runs, Server -> server runs.
        // RUS: Показываемая история зависит от активного раздела: Клиент -> прогоны клиента, Сервер -> сервера.
        private static BenchHistory ActiveHistory() => _section == Section.Client ? Benchmark.History : ClientOptNet.ServerHistory;

        private static void HistPrev() { ActiveHistory().Prev(); RefreshResult(); }
        private static void HistNext() { ActiveHistory().Next(); RefreshResult(); }

        private static void UpdateHistLabel(BenchHistory hist)
        {
            if (_histLabel == null) { return; }
            if (hist == null || hist.Count == 0) { _histLabel.Text = Loc.T("история пуста", "history empty"); return; }
            var e = hist.Current;
            _histLabel.Text = $"{hist.ViewIndex + 1}/{hist.Count}" + (e != null && !string.IsNullOrEmpty(e.Stamp) ? "  |  " + e.Stamp : "");
        }

        // Absolute per-line height in px, from the monospaced font at our TextScale (+ small margin).
        // RUS: Абсолютная высота строки в px, по моноширинному шрифту с нашим TextScale (+ небольшой отступ).
        private static int LineHeightPx()
        {
            try { return Math.Max(10, (int)(GUIStyle.MonospacedFont.MeasureString("Ayj").Y * 0.8f) + 2); }
            catch { return 18; }
        }

        private static void RefreshResult()
        {
            if (_resultList == null) { return; }
            _resultList.Content.ClearChildren();
            var hist = ActiveHistory();
            UpdateHistLabel(hist);
            var entry = hist?.Current;
            if (entry == null || entry.Lines == null || entry.Lines.Count == 0)
            {
                new GUITextBlock(new RectTransform(new Vector2(1f, 0.06f), _resultList.Content.RectTransform),
                    _section == Section.Client
                        ? Loc.T("Результата пока нет. Нажми «Бенчмарк» возле нагрузки (станция с НПС).", "No result yet. Click «Benchmark» near the load (a station with NPCs).")
                        : Loc.T("Серверных бенчмарков пока нет. Нажми «Серверный бенчмарк».", "No server benchmarks yet. Click «Server benchmark»."),
                    font: GUIStyle.SmallFont, wrap: true)
                { CanBeFocused = false };
                return;
            }
            // each line in its own color (like the console), monospaced font ~20% smaller (TextScale 0.8).
            // line height is ABSOLUTE (matched to the font) so lines never overlap regardless of window size.
            // RUS: каждая строка своим цветом (как в консоли), моноширинный шрифт на ~20% мельче (TextScale 0.8).
            // RUS: высота строки АБСОЛЮТНАЯ (по шрифту), чтобы строки не налезали при любом размере окна.
            int lineH = LineHeightPx();
            foreach (var ln in entry.Lines)
            {
                new GUITextBlock(new RectTransform(new Vector2(1f, 0.05f), _resultList.Content.RectTransform)
                    { MinSize = new Point(0, lineH), MaxSize = new Point(int.MaxValue, lineH) },
                    string.IsNullOrEmpty(ln.Text) ? " " : ln.Text,
                    textColor: ln.Color, font: GUIStyle.MonospacedFont, textAlignment: Alignment.CenterLeft)
                { CanBeFocused = false, TextScale = 0.8f };
            }
            _resultList.BarScroll = 0f;
        }

        private static void CopyResults()
        {
            var entry = ActiveHistory()?.Current;
            string text = entry?.Lines != null ? string.Join("\n", entry.Lines.ConvertAll(l => l.Text)) : "";
            if (string.IsNullOrWhiteSpace(text))
            {
                if (_status != null) { _status.Text = Loc.T("Нечего копировать — сначала прогони бенчмарк.", "Nothing to copy — run the benchmark first."); }
                return;
            }
            bool ok = TrySetClipboard(text);
            if (_status != null) { _status.Text = ok ? Loc.T("Скопировано! Вставь текст в чат.", "Copied! Paste the text into chat.") : Loc.T("Не удалось скопировать (см. консоль).", "Copy failed (see console)."); }
            ClientPerf.Log(ok ? Loc.T("Результат бенчмарка скопирован в буфер обмена.", "Benchmark result copied to clipboard.") : Loc.T("Копирование не удалось.", "Copy failed."), ok ? Color.LightGreen : Color.Orange);
        }

        // --- clipboard (like GM Menu): Barotrauma.Clipboard.SetText via reflection + a hidden-GUITextBox fallback ---
        // RUS: --- буфер обмена (как в GM Menu): Barotrauma.Clipboard.SetText через рефлексию + фоллбэк скрытым GUITextBox ---
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
            // fallback: a hidden GUITextBox far off-screen + its internal CopySelectedText()
            // RUS: фоллбэк: скрытый GUITextBox далеко за экраном + его внутренний CopySelectedText()
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
