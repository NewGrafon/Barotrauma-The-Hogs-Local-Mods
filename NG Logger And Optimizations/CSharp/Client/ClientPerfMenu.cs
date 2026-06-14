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
        private enum Section { Client, Server, Logs }
        private static Section        _section = Section.Client;
        private static GUILayoutGroup _selectorRow;
        private static GUIButton      _secClientBtn, _secServerBtn, _secLogsBtn;
        private static GUILayoutGroup _content;   // active section's content (rebuilt on switch)   // RUS: содержимое активного раздела (пересобирается при переключении)
        private static GUIButton      _benchBtn;   // active section's benchmark button   // RUS: кнопка бенчмарка активного раздела
        private static GUIButton      _durBtn;     // benchmark duration cycle button (shared 15/30/60/300s)   // RUS: кнопка переключения длительности (общая 15/30/60/300с)
        private static readonly GUIButton[] _fixBtns = new GUIButton[OptFixes.Count]; // active section's fix toggles   // RUS: тумблеры фиксов активного раздела
        private static readonly GUIButton[] _logBtns = new GUIButton[LogChecks.Count]; // "Console logs" section toggles   // RUS: тумблеры раздела «Конс. логи»
        private static GUILayoutGroup _histRow;   // benchmark history nav row (hidden in the Logs section)   // RUS: ряд навигации по истории бенча (скрыт в разделе «Конс. логи»)

        public static bool IsOpen => _frame != null;

        public static void Toggle() { if (_frame == null) { Open(); } else { Close(); } }

        public static void Close()
        {
            try { if (_frame != null) { _frame.RectTransform.Parent = null; } } catch { }
            _frame = null; _status = null; _resultList = null;
            _selectorRow = null; _secClientBtn = null; _secServerBtn = null; _secLogsBtn = null; _content = null; _benchBtn = null; _durBtn = null;
            _histLabel = null; _histPrevBtn = null; _histNextBtn = null; _histRow = null;
            for (int i = 0; i < OptFixes.Count; i++) { _fixBtns[i] = null; }
            for (int i = 0; i < LogChecks.Count; i++) { _logBtns[i] = null; }
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
                if (!privileged && _section == Section.Server) { _section = Section.Client; } // Server needs privilege; Client/Logs are open to all   // RUS: Сервер требует прав; Клиент/Логи доступны всем

                // section selector under the title: [Client] [Server (privileged only)] [Console logs (everyone)]
                // RUS: селектор разделов под title: [Клиент] [Сервер (только привилегир.)] [Конс. логи (всем)]
                _selectorRow = new GUILayoutGroup(new RectTransform(new Vector2(1f, 0.06f), col.RectTransform), isHorizontal: true) { Stretch = true, RelativeSpacing = 0.02f };
                float bw = privileged ? 0.333f : 0.5f;
                _secClientBtn = new GUIButton(new RectTransform(new Vector2(bw, 1f), _selectorRow.RectTransform), Loc.ClientCol, style: "GUIButtonSmall");
                _secClientBtn.OnClicked = (b, o) => { SelectSection(Section.Client); return true; };
                if (privileged)
                {
                    _secServerBtn = new GUIButton(new RectTransform(new Vector2(bw, 1f), _selectorRow.RectTransform), Loc.ServerCol, style: "GUIButtonSmall");
                    _secServerBtn.OnClicked = (b, o) => { SelectSection(Section.Server); return true; };
                }
                _secLogsBtn = new GUIButton(new RectTransform(new Vector2(bw, 1f), _selectorRow.RectTransform), Loc.LogsCol, style: "GUIButtonSmall") { ToolTip = Loc.LogsTip };
                _secLogsBtn.OnClicked = (b, o) => { SelectSection(Section.Logs); return true; };

                _status = new GUITextBlock(new RectTransform(new Vector2(1f, 0.06f), col.RectTransform),
                    Benchmark.StatusText, font: GUIStyle.SmallFont, textAlignment: Alignment.Center, wrap: true);

                // active section's content (a benchmark row + the fix-toggle rows), rebuilt on section switch
                // RUS: содержимое активного раздела (ряд бенчмарка + ряды тумблеров фиксов), пересобирается при смене раздела
                var contentFrame = new GUIFrame(new RectTransform(new Vector2(1f, 0.46f), col.RectTransform), style: null, color: new Color(0, 0, 0, 70));
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
                _histRow = new GUILayoutGroup(new RectTransform(new Vector2(1f, 0.05f), col.RectTransform), isHorizontal: true) { Stretch = true, RelativeSpacing = 0.01f };
                var histRow = _histRow;
                _histPrevBtn = new GUIButton(new RectTransform(new Vector2(0.12f, 1f), histRow.RectTransform), "<---", style: "GUIButtonSmall");
                _histPrevBtn.OnClicked = (b, o) => { HistPrev(); return true; };
                _histLabel = new GUITextBlock(new RectTransform(new Vector2(0.50f, 1f), histRow.RectTransform), "", font: GUIStyle.SmallFont, textAlignment: Alignment.Center) { CanBeFocused = false };
                _histNextBtn = new GUIButton(new RectTransform(new Vector2(0.12f, 1f), histRow.RectTransform), "--->", style: "GUIButtonSmall");
                _histNextBtn.OnClicked = (b, o) => { HistNext(); return true; };
                // Copy the currently-shown benchmark result to the clipboard. Lives in the (shared)
                // history row so BOTH the client AND server sections have it (CopyResults reads the
                // active section's history via ActiveHistory()).
                // RUS: Копирование показанного результата в буфер. В ОБЩЕМ ряду истории -> есть И в
                // RUS: клиентском, И в серверном разделе (CopyResults берёт историю активного раздела).
                var copyHistBtn = new GUIButton(new RectTransform(new Vector2(0.24f, 1f), histRow.RectTransform), Loc.T("Копир.", "Copy"), style: "GUIButtonSmall")
                { ToolTip = Loc.T("Скопировать показанный результат бенчмарка в буфер обмена.", "Copy the shown benchmark result to the clipboard.") };
                copyHistBtn.OnClicked = (b, o) => { CopyResults(); return true; };

                // result area (scrollable, monospaced font for aligned columns)
                // RUS: область результата (скролл, моноширинный шрифт для ровных столбцов)
                _resultList = new GUIListBox(new RectTransform(new Vector2(1f, 0.29f), col.RectTransform));

                RefreshResult();
                try { ClientOptNet.RequestServerState(); } catch { } // pull current server states for the indicators   // RUS: подтянуть текущие серверные состояния для индикаторов
                try { ClientOptNet.RequestLogState(); } catch { }   // pull current server log-check states for the Logs section   // RUS: подтянуть состояния серверных проверок для раздела «Конс. логи»
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

                // privilege can change: drop out of the Server section if it was lost (Client/Logs stay open to all)
                // RUS: права могут измениться: уходим из раздела Сервер, если их отобрали (Клиент/Логи доступны всем)
                bool privileged = ClientOptNet.InMP && ClientOptNet.CanControlServer();
                if (!privileged && _section == Section.Server) { SelectSection(Section.Client); }

                if (_status != null && _section != Section.Logs) { _status.Text = _section == Section.Client ? Benchmark.StatusText : ClientOptNet.ServerBenchStatus; }
                if (_benchBtn != null) { _benchBtn.Text = _section == Section.Client ? BenchLabel() : ServerBenchLabel(); }
                if (_durBtn != null)   { _durBtn.Text = DurationLabel(); }
                for (int i = 0; i < OptFixes.Count; i++)
                {
                    if (_fixBtns[i] != null) { _fixBtns[i].Text = _section == Section.Client ? ClientBtnLabel(i) : ServerBtnLabel(i); }
                }
                for (int i = 0; i < LogChecks.Count; i++)
                {
                    if (_logBtns[i] != null) { _logBtns[i].Text = LogBtnLabel(i); }
                }
                // refresh the result window when the active section's history changed (new entry / navigation)
                // RUS: обновляем окно результата, когда история активного раздела изменилась (новая запись / навигация)
                if (_section != Section.Logs)
                {
                    var hist = ActiveHistory();
                    if (hist != null && hist.Dirty) { hist.Dirty = false; RefreshResult(); }
                }
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
        private static string ClientBtnLabel(int fix) => Loc.ClientCol + ": " + OptFixes.LevelLabel(fix, OptFixes.GetLevel(fix));
        private static string ServerBtnLabel(int fix)
        {
            string st = (!ClientOptNet.InMP || !ClientOptNet.ServerKnown(fix)) ? Loc.Unknown : OptFixes.LevelLabel(fix, ClientOptNet.ServerLevel(fix));
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
            _benchBtn = null; _durBtn = null;
            for (int i = 0; i < OptFixes.Count; i++) { _fixBtns[i] = null; }
            for (int i = 0; i < LogChecks.Count; i++) { _logBtns[i] = null; }

            // the Logs section has no benchmark: hide the history nav + result area while it's active
            // RUS: в разделе «Конс. логи» нет бенчмарка: пока он активен — прячем навигацию по истории + область результата
            bool bench = section != Section.Logs;
            if (_resultList != null) { _resultList.Visible = bench; }
            if (_histRow != null)    { _histRow.Visible = bench; }

            if (section == Section.Logs)
            {
                AddLogChecks();
                if (_status != null) { _status.Text = Loc.LogsTip; }
                return;
            }

            // benchmark row: [run benchmark] [duration cycle] [secondary: copy (client) / load snapshot (server)]
            // RUS: ряд бенчмарка: [запуск] [переключение длительности] [вторичная: копир. (клиент) / снимок нагрузки (сервер)]
            var benchRow = new GUILayoutGroup(new RectTransform(new Vector2(1f, 0.24f), _content.RectTransform), isHorizontal: true) { Stretch = true, RelativeSpacing = 0.015f };
            if (section == Section.Client)
            {
                _benchBtn = new GUIButton(new RectTransform(new Vector2(0.44f, 1f), benchRow.RectTransform), BenchLabel(), style: "GUIButtonSmall") { ToolTip = Loc.ClientTip };
                _benchBtn.OnClicked = (b, o) => { Benchmark.StartOrCancel(); SyncDynamic(); return true; };
                _durBtn = new GUIButton(new RectTransform(new Vector2(0.24f, 1f), benchRow.RectTransform), DurationLabel(), style: "GUIButtonSmall") { ToolTip = Loc.DurationTip };
                _durBtn.OnClicked = (b, o) => { if (_section == Section.Client) { Benchmark.CycleDuration(); } else { ClientOptNet.CycleServerBenchDuration(); } if (_durBtn != null) { _durBtn.Text = DurationLabel(); } return true; };
                // (Copy moved to the shared history row so it's available in the server section too.)
                // RUS: (Кнопка копирования перенесена в общий ряд истории — теперь она и в серверном разделе.)
            }
            else
            {
                _benchBtn = new GUIButton(new RectTransform(new Vector2(0.6f, 1f), benchRow.RectTransform), ServerBenchLabel(), style: "GUIButtonSmall") { ToolTip = Loc.ServerBenchTip };
                _benchBtn.OnClicked = (b, o) => { ClientOptNet.StartOrCancelServerBench(); return true; };
                _durBtn = new GUIButton(new RectTransform(new Vector2(0.38f, 1f), benchRow.RectTransform), DurationLabel(), style: "GUIButtonSmall") { ToolTip = Loc.DurationTip };
                _durBtn.OnClicked = (b, o) => { if (_section == Section.Client) { Benchmark.CycleDuration(); } else { ClientOptNet.CycleServerBenchDuration(); } if (_durBtn != null) { _durBtn.Text = DurationLabel(); } return true; };
                // (the "Load snapshot" button moved to the "Console logs" section — it prints a console log line.)
                // RUS: (кнопка «Снимок нагрузки» переехала в раздел «Конс. логи» — она печатает строку лога в консоль.)
            }

            // Fixes grouped into named subsections (small centered header + its fix rows). The display
            // order is GROUP-driven (not 0..Count), so fix 5 sits next to fix 3 under "weak optimization".
            // RUS: Фиксы по именованным подразделам (мелкий заголовок по центру + ряды фиксов). Порядок —
            // RUS: по ГРУППАМ (а не 0..Count), чтобы фикс 5 шёл рядом с фиксом 3 в «слабой оптимизации».
            AddFixGroup(section, Loc.T("— Сильная оптимизация —", "— Strong optimization —"), 0, 1);
            AddFixGroup(section, Loc.T("— Слабая оптимизация —",  "— Weak optimization —"),   2, 4);
            AddFixGroup(section, Loc.T("— Фиксы —",               "— Fixes —"),               3);
            SyncDynamic();
            // show the new section's benchmark history
            // RUS: показать историю бенчмарков нового раздела
            var h = ActiveHistory();
            if (h != null) { h.Dirty = false; }
            RefreshResult();
        }

        // Adds a subsection: a small centered header label, then a toggle row for each given fix index.
        // RUS: Добавляет подраздел: мелкий заголовок по центру, затем ряд-тумблер на каждый из фиксов.
        private static void AddFixGroup(Section section, string header, params int[] fixes)
        {
            if (_content == null) { return; }
            new GUITextBlock(new RectTransform(new Vector2(1f, 0.10f), _content.RectTransform), header,
                font: GUIStyle.SmallFont, textAlignment: Alignment.Center) { CanBeFocused = false };

            foreach (int idx in fixes)
            {
                int fix = idx; // capture for the lambdas   // RUS: захват для лямбд
                var row = new GUILayoutGroup(new RectTransform(new Vector2(1f, 0.22f), _content.RectTransform), isHorizontal: true) { Stretch = true, RelativeSpacing = 0.012f };
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
        }

        private static void SyncDynamic()
        {
            if (_benchBtn != null) { _benchBtn.Text = _section == Section.Client ? BenchLabel() : ServerBenchLabel(); }
            if (_status != null)   { _status.Text = _section == Section.Client ? Benchmark.StatusText : ClientOptNet.ServerBenchStatus; }
        }

        // Builds the "Console logs" section: one toggle row per periodic logger check. The client check (0)
        // toggles a LOCAL flag; the server checks (1,2) are net-synced and changeable by the host/admin only.
        // RUS: Строит раздел «Конс. логи»: ряд-тумблер на каждую периодическую проверку логгера. Клиентская
        // RUS: проверка (0) — ЛОКАЛЬНЫЙ флаг; серверные (1,2) — синхронизируются по сети, меняет только хост/админ.
        private static void AddLogChecks()
        {
            if (_content == null) { return; }
            new GUITextBlock(new RectTransform(new Vector2(1f, 0.16f), _content.RectTransform),
                Loc.T("— Периодические проверки —", "— Periodic checks —"),
                font: GUIStyle.SmallFont, textAlignment: Alignment.Center) { CanBeFocused = false };

            for (int idx = 0; idx < LogChecks.Count; idx++)
            {
                int chk = idx; // capture for the lambda   // RUS: захват для лямбды
                var row = new GUILayoutGroup(new RectTransform(new Vector2(1f, 0.22f), _content.RectTransform), isHorizontal: true) { Stretch = true, RelativeSpacing = 0.012f };
                string side = LogChecks.IsServerSide(chk) ? " [" + Loc.ServerCol + "]" : " [" + Loc.ClientCol + "]";
                new GUITextBlock(new RectTransform(new Vector2(0.55f, 1f), row.RectTransform), LogChecks.Name(chk) + side,
                    font: GUIStyle.SmallFont, textAlignment: Alignment.CenterLeft, wrap: true) { ToolTip = LogChecks.Tip(chk), CanBeFocused = false };

                _logBtns[chk] = new GUIButton(new RectTransform(new Vector2(0.33f, 1f), row.RectTransform), LogBtnLabel(chk), style: "GUIButtonSmall")
                { ToolTip = LogChecks.IsServerSide(chk) ? Loc.ServerHostTip : Loc.ClientTip };
                _logBtns[chk].OnClicked = (b, o) => { ToggleLog(chk); return true; };

                new GUIButton(new RectTransform(new Vector2(0.12f, 1f), row.RectTransform), "?", style: "GUIButtonSmall")
                { ToolTip = LogChecks.Tip(chk) };
            }

            // manual one-shot: request an immediate server-load snapshot (privileged only). Moved here from the
            // Server section — it prints a console log line, so it belongs with the periodic console checks.
            // RUS: ручное действие: запросить мгновенный снимок нагрузки сервера (только привилегир.). Переехало из
            // RUS: раздела Сервер — печатает строку лога в консоль, поэтому ему место среди консольных проверок.
            if (ClientOptNet.InMP && ClientOptNet.CanControlServer())
            {
                var srow = new GUILayoutGroup(new RectTransform(new Vector2(1f, 0.22f), _content.RectTransform), isHorizontal: true) { Stretch = true, RelativeSpacing = 0.012f };
                new GUITextBlock(new RectTransform(new Vector2(0.55f, 1f), srow.RectTransform),
                    Loc.T("Снимок нагрузки сейчас (сервер)", "Load snapshot now (server)"),
                    font: GUIStyle.SmallFont, textAlignment: Alignment.CenterLeft, wrap: true) { ToolTip = Loc.ServerSnapTip, CanBeFocused = false };
                var snapBtn = new GUIButton(new RectTransform(new Vector2(0.33f, 1f), srow.RectTransform), Loc.ServerSnap, style: "GUIButtonSmall") { ToolTip = Loc.ServerSnapTip };
                snapBtn.OnClicked = (b, o) => { ClientOptNet.RequestServerPerf(); if (_status != null) { _status.Text = Loc.T("Запрос снимка нагрузки отправлен…", "Load-snapshot request sent…"); } return true; };
                new GUIFrame(new RectTransform(new Vector2(0.12f, 1f), srow.RectTransform), style: null) { CanBeFocused = false }; // keep the button column aligned   // RUS: держим колонку кнопок выровненной
            }

            // trailing spacer: keeps the rows at a compact height (the content group stretches its children
            // to fill, so without this filler the few rows would blow up tall and the buttons sit above the text).
            // RUS: спейсер снизу: держит строки компактной высоты (контейнер растягивает детей на всю высоту,
            // RUS: без этой заглушки немногочисленные строки раздувались бы, и кнопки оказывались выше текста).
            new GUIFrame(new RectTransform(new Vector2(1f, 0.9f), _content.RectTransform), style: null) { CanBeFocused = false };
        }

        // Toggle label for a "Console logs" check: ON/OFF locally for the client check; ON/OFF/— for server
        // checks (— = the server state isn't known yet, e.g. single-player or before the first broadcast).
        // RUS: Метка тумблера проверки «Конс. логи»: ВКЛ/ВЫКЛ локально для клиентской; ВКЛ/ВЫКЛ/— для серверных
        // RUS: (— = серверное состояние ещё неизвестно, напр. одиночка или до первой рассылки).
        private static string LogBtnLabel(int chk)
        {
            if (LogChecks.IsServerSide(chk))
            {
                if (!ClientOptNet.InMP || !ClientOptNet.ServerLogKnown(chk)) { return Loc.Unknown; }
                return ClientOptNet.ServerLogState(chk) ? Loc.On : Loc.Off;
            }
            return ClientOptNet.LogCheckEnabled(chk) ? Loc.On : Loc.Off;
        }

        // Toggle a "Console logs" check: the client check flips locally; server checks are requested over the
        // net (host/admin only — the server re-validates and broadcasts the new state back).
        // RUS: Переключить проверку «Конс. логи»: клиентская — локально; серверные — запросом по сети (только
        // RUS: хост/админ — сервер перепроверяет и рассылает новое состояние обратно).
        private static void ToggleLog(int chk)
        {
            if (LogChecks.IsServerSide(chk))
            {
                if (!ClientOptNet.InMP) { if (_status != null) { _status.Text = Loc.ServerMPOnly; } return; }
                if (!ClientOptNet.CanControlServer())
                {
                    if (_status != null) { _status.Text = Loc.T("Серверную проверку логов может менять только хост/админ.", "Only the host/admin can change a server log-check."); }
                    return;
                }
                ClientOptNet.ToggleLogCheck(chk); // sends a net request; the broadcast updates the label   // RUS: шлёт сетевой запрос; метку обновит рассылка
                if (_status != null) { _status.Text = Loc.T("Запрос на сервер отправлен… (обновится по подтверждению).", "Request sent to the server… (updates on confirmation)."); }
                return;
            }
            // client-side check: flip the LOCAL flag and reflect it immediately
            // RUS: клиентская проверка: переключаем ЛОКАЛЬНЫЙ флаг и сразу отражаем
            ClientOptNet.ToggleLogCheck(chk);
            bool on = ClientOptNet.LogCheckEnabled(chk);
            if (_logBtns[chk] != null) { _logBtns[chk].Text = LogBtnLabel(chk); }
            if (_status != null) { _status.Text = LogChecks.Name(chk) + ": " + (on ? Loc.On : Loc.Off); }
            ClientPerf.Log(LogChecks.Name(chk) + ": " + (on ? Loc.On : Loc.Off), on ? Color.LightGreen : Color.Orange);
        }

        // Toggle a fix on THIS client (local, applies + persists).
        // RUS: Переключить фикс на ЭТОМ клиенте (локально, применяет + сохраняет).
        private static void ToggleClient(int fix)
        {
            int lvl = OptConfig.CycleClient(fix);   // cycle this client's level (off -> ... -> off), apply + persist
            string label = OptFixes.LevelLabel(fix, lvl);
            if (_section == Section.Client && _fixBtns[fix] != null) { _fixBtns[fix].Text = ClientBtnLabel(fix); }
            if (_status != null)
            {
                _status.Text = Loc.Ru
                    ? $"{OptFixes.ShortName(fix)} (клиент): {label}." + (lvl > 0 ? "" : " Прогони бенчмарк для сравнения.")
                    : $"{OptFixes.ShortName(fix)} (client): {label}." + (lvl > 0 ? "" : " Run the benchmark to compare.");
            }
            ClientPerf.Log(OptFixes.ShortName(fix) + " [" + Loc.ClientCol + "]: " + label, lvl > 0 ? Color.LightGreen : Color.Orange);
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
            // cycle the known server level (off -> ... -> max -> off); if unknown, start from 0
            // RUS: циклим известный серверный уровень (выкл -> ... -> макс -> выкл); если неизвестно — с 0
            int cur = ClientOptNet.ServerKnown(fix) ? ClientOptNet.ServerLevel(fix) : 0;
            int next = cur + 1;
            if (next > OptFixes.MaxLevel(fix)) { next = 0; }
            ClientOptNet.RequestSetServer(fix, next);
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
