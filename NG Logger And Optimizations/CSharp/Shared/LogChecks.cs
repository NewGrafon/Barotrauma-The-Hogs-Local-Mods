namespace NetEventLogger
{
    // ==========================================================================================
    //  Shared metadata registry for the "Console logs" menu section: the logger's periodic diagnostic
    //  checks that print to a console, addressed by index. All are OFF by default.
    //  Unlike OptFixes, the actual toggle flags live in DIFFERENT assemblies — the client check is in
    //  the client assembly (ClientPerfLogger.AutoLog), the server checks in the server assembly
    //  (NetEventLoggerPlugin.LogEnabled, ServerPerfMonitor.AutoPush). So this Shared registry only holds
    //  metadata (names/tips/which side) + the net message ids; each side maps an index to its own flag.
    //  RUS: Общий реестр-метаданные раздела меню «Консольные логи»: периодические диагностические проверки
    //  RUS: логгера, печатающие в консоль, по индексу. По умолчанию все ВЫКЛ. В отличие от OptFixes, сами
    //  RUS: флаги-тумблеры лежат в РАЗНЫХ сборках — клиентская проверка в клиентской сборке
    //  RUS: (ClientPerfLogger.AutoLog), серверные — в серверной (NetEventLoggerPlugin.LogEnabled,
    //  RUS: ServerPerfMonitor.AutoPush). Поэтому здесь только метаданные (имена/тултипы/сторона) + id
    //  RUS: сетевых сообщений; каждая сторона сама сопоставляет индекс своему флагу.
    // ==========================================================================================
    internal static class LogChecks
    {
        public const int Count = 3;
        // 0 = client FPS auto-summary (AutoLog)      — CLIENT-side, toggled locally by the client (no net)
        // 1 = server net-event queue diagnostics     — SERVER-side, toggled via net (host/admin only)
        // 2 = server-load auto-snapshots (15s push)  — SERVER-side, toggled via net (host/admin only)
        public static bool IsServerSide(int i) => i == 1 || i == 2;

        // Net message ids (LuaCs networking). Server->client: broadcast of the server-side check states.
        // Client->server: a request to set one server check (index 1..2) OR to just resend (RequestLogState).
        // RUS: ID сетевых сообщений. Сервер->клиент: рассылка состояний серверных проверок. Клиент->сервер:
        // RUS: запрос установить одну серверную проверку (индекс 1..2) ЛИБО переслать состояние (RequestLogState).
        public const string MsgLogState     = "ngopt_log_state";
        public const string MsgSetLog       = "ngopt_set_log";
        public const byte   RequestLogState = 255; // index meaning "just send me the current server log states"   // RUS: индекс «пришли текущие состояния серверных проверок»

        // Localized display name of check #i.
        // RUS: Локализованное имя проверки #i.
        public static string Name(int i)
        {
            switch (i)
            {
                case 0: return Loc.LogClientPerf;
                case 1: return Loc.LogNetEvents;
                case 2: return Loc.LogServerPerf;
                default: return "?";
            }
        }

        // Tooltip of check #i.
        // RUS: Тултип проверки #i.
        public static string Tip(int i)
        {
            switch (i)
            {
                case 0: return Loc.LogClientPerfTip;
                case 1: return Loc.LogNetEventsTip;
                case 2: return Loc.LogServerPerfTip;
                default: return "";
            }
        }

        // Parse a check selector from a console argument: accepts 1/2/3 (1-based). Returns -1 if invalid.
        // RUS: Разобрать селектор проверки из аргумента команды: принимает 1/2/3 (с единицы). -1 если неверно.
        public static int Parse(string s)
        {
            if (string.IsNullOrEmpty(s)) { return -1; }
            s = s.Trim().ToLowerInvariant();
            if (s == "1") { return 0; }
            if (s == "2") { return 1; }
            if (s == "3") { return 2; }
            return -1;
        }
    }
}
