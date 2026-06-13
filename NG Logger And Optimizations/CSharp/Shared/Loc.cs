using System;
using Barotrauma;

namespace NetEventLogger
{
    // ==========================================================================================
    //  Hybrid localization. Lives in Shared so BOTH the client (profiler/menu) and the server
    //  (event logger) can use it.
    //   • Static MENU/UI strings  -> XML/Text/{English,Russian}.xml via G(id)/TextManager
    //                                 (proper Barotrauma localization, community-translatable,
    //                                  auto English fallback for other languages).
    //   • Dynamic diagnostics/reports -> stay in code as T(ru, en), picked by the game language
    //                                 (GameSettings.CurrentConfig.Language). They are heavily
    //                                 interpolated and do not fit XML text packs well.
    //  RUS: Гибридная локализация. Лежит в Shared, чтобы И клиент (профайлер/меню), И сервер
    //  RUS: (логгер событий) могли ею пользоваться.
    //  RUS:  • Статичные строки МЕНЮ/UI  -> XML/Text/{English,Russian}.xml через G(id)/TextManager
    //  RUS:                                  (штатная локализация Barotrauma, можно переводить
    //  RUS:                                   силами сообщества, авто-фолбэк на английский).
    //  RUS:  • Динамическая диагностика/отчёты -> остаются в коде как T(ru, en) по языку игры —
    //  RUS:                                  они сильно интерполированы и плохо ложатся в XML.
    // ==========================================================================================
    internal static class Loc
    {
        // Unified console tag for this mod: every message starts with "[NG] [Logger And Optimizations] ".
        // RUS: Единый тег консоли для этого мода: каждое сообщение начинается с "[NG] [Logger And Optimizations] ".
        public const string Tag = "[NG] [Logger And Optimizations] ";

        private static int _ru = -1; // -1 unknown, 0 no, 1 yes   // RUS: -1 неизвестно, 0 нет, 1 да
        public static bool Ru
        {
            get
            {
                if (_ru < 0)
                {
                    try { _ru = GameSettings.CurrentConfig.Language.ToString().IndexOf("russ", StringComparison.OrdinalIgnoreCase) >= 0 ? 1 : 0; }
                    catch { _ru = 0; }
                }
                return _ru == 1;
            }
        }
        public static string T(string ru, string en) => Ru ? ru : en;

        // Static MENU/UI strings live in XML/Text/{English,Russian}.xml (TextManager). G(id) reads them by the
        // game language with automatic English fallback; if a tag is missing entirely it returns the id itself
        // (a visible marker that the text pack failed to load) instead of a blank label.
        // RUS: Статичные строки МЕНЮ/UI лежат в XML/Text/{English,Russian}.xml (TextManager). G(id) читает их по
        // RUS: языку игры с авто-фолбэком на английский; если тег вообще не найден — вернёт сам id (видимый
        // RUS: признак, что текст-пак не загрузился), а не пустую метку.
        // NOTE: Dynamic diagnostics/reports (benchmark prose, [STATUS] lines, log messages) deliberately stay
        //       in code as Loc.T(ru, en) — they are heavily interpolated and do not fit XML well.
        // RUS:  ПРИМ.: Динамическая диагностика/отчёты (текст бенча, [STATUS]-строки, лог-сообщения) намеренно
        // RUS:        остаются в коде как Loc.T(ru, en) — они сильно интерполированы и плохо ложатся в XML.
        public static string G(string id)
        {
            try { var v = TextManager.Get(id)?.Value; if (!string.IsNullOrEmpty(v)) return v; }
            catch { }
            return id;
        }

        // FIX 1 — container optimization (spent OnInserted effects no longer churn in Update).
        // RUS: ФИКС 1 — оптимизация контейнеров (отработавшие OnInserted-эффекты не крутятся в Update).
        public static string OptName  => G("ng.fix1.name");
        public static string OptShort => G("ng.fix1.short");
        public static string OptTip   => G("ng.fix1.tip");

        // FIX 2 — nearby-item search optimization (empty NearbyItems scans are skipped).
        // RUS: ФИКС 2 — оптимизация поиска предметов рядом (пустые NearbyItems-сканы пропускаются).
        public static string Opt2Name  => G("ng.fix2.name");
        public static string Opt2Short => G("ng.fix2.short");
        public static string Opt2Tip   => G("ng.fix2.tip");

        // FIX 3 (EXPERIMENTAL) — throttle auto-triggered RepairTool.Use (sprinklers etc.) to ~half the tickrate.
        // RUS: ФИКС 3 (ЭКСПЕРИМ.) — троттлинг авто-срабатывающего RepairTool.Use (спринклеры и т.п.) до ~половины тикрейта.
        public static string Opt3Name  => G("ng.fix3.name");
        public static string Opt3Short => G("ng.fix3.short");
        public static string Opt3Tip   => G("ng.fix3.tip");
        public static string Experimental => G("ng.experimental");

        // Short ON/OFF tokens stay in code: they are interpolated into dynamic report/status lines, not just the menu.
        // RUS: Короткие токены ВКЛ/ВЫКЛ остаются в коде: они интерполируются в динамические строки отчётов/статуса, не только в меню.
        public static string On  => T("ВКЛ", "ON");
        public static string Off => T("ВЫКЛ", "OFF");

        // Per-fix client/server toggle columns + hints (the menu shows two toggles per fix).
        // RUS: Колонки клиент/сервер у каждого фикса + подсказки (в меню по два тумблера на фикс).
        public static string ClientCol => G("ng.col.client");
        public static string ServerCol => G("ng.col.server");
        public static string Unknown   => "—"; // server state not yet known (identical RU/EN, kept in code)   // RUS: серверное состояние ещё неизвестно (одинаково RU/EN, в коде)
        public static string ClientTip => G("ng.tip.client");
        public static string ServerHostTip => G("ng.tip.serverhost");
        public static string ServerMPOnly => G("ng.tip.servermponly");
        public static string ServerBenchTip => G("ng.tip.serverbench");
        public static string ServerSnap    => G("ng.btn.serversnap");
        public static string ServerSnapTip => G("ng.tip.serversnap");
        public static string DurationTip => G("ng.tip.duration");
        public static string InfoTip => G("ng.tip.info");

        // Units used in report data rows (so EN shows ms / µs/call / calls).
        // RUS: Единицы в строках-данных отчётов (чтобы в EN было ms / µs/call / calls).
        public static string Ms        => T("мс", "ms");
        public static string UsPerCall => T("µs/вызов", "µs/call");
        public static string Calls     => T("выз.", "calls");
    }
}
