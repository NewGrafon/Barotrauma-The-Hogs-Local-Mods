using System;
using Barotrauma;

namespace NetEventLogger
{
    // ==========================================================================================
    //  RU/EN localization by the game's language (GameSettings.CurrentConfig.Language).
    //  Self-contained, without Content/Texts XML: simpler and more reliable for code/GUI strings.
    //  T(ru, en) picks the string by language. Lives in Shared so BOTH the client (profiler/menu)
    //  and the server (event logger) can use it.
    //  RUS: Локализация RU/EN по языку игры (GameSettings.CurrentConfig.Language). Самодостаточно,
    //  RUS: без Content/Texts XML: для строк кода/GUI это проще и надёжнее. T(ru, en) выбирает строку
    //  RUS: по языку. Лежит в Shared, чтобы И клиент (профайлер/меню), И сервер (логгер событий)
    //  RUS: могли ею пользоваться.
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

        // FIX 1 — container optimization (spent OnInserted effects no longer churn in Update).
        // RUS: ФИКС 1 — оптимизация контейнеров (отработавшие OnInserted-эффекты не крутятся в Update).
        public static string OptName  => T("Оптимизация контейнеров", "Container optimization");
        public static string OptShort => T("Оптим. контейнеров",      "Container opt.");
        public static string OptTip   => T(
            "Не обрабатывать каждый кадр уже отработавшие эффекты вставки у предметов внутри контейнеров (стволы с по-патронной зарядкой и т.п.). Выключи, чтобы сравнить нагрузку.",
            "Skip per-frame processing of already-spent insert-effects on items inside containers (per-round-reload guns etc.). Turn it off to compare the load.");

        // FIX 2 — nearby-item search optimization (empty NearbyItems scans are skipped).
        // RUS: ФИКС 2 — оптимизация поиска предметов рядом (пустые NearbyItems-сканы пропускаются).
        public static string Opt2Name  => T("Оптимизация поиска предметов", "Nearby-item search optimization");
        public static string Opt2Short => T("Оптим. поиска рядом",          "Nearby search opt.");
        public static string Opt2Tip   => T(
            "Не сканировать весь мир в поиске предметов рядом, если подходящих предметов в мире нет (PDA, разные пушки). Выключи, чтобы сравнить нагрузку.",
            "Skip scanning the whole world for nearby items when no matching item exists (PDA, various guns). Turn it off to compare the load.");

        public static string On  => T("ВКЛ", "ON");
        public static string Off => T("ВЫКЛ", "OFF");

        // Units used in report data rows (so EN shows ms / µs/call / calls).
        // RUS: Единицы в строках-данных отчётов (чтобы в EN было ms / µs/call / calls).
        public static string Ms        => T("мс", "ms");
        public static string UsPerCall => T("µs/вызов", "µs/call");
        public static string Calls     => T("выз.", "calls");
    }
}
