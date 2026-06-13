namespace NetEventLogger
{
    // ==========================================================================================
    //  Shared registry of the optimization fixes, addressed by index, so BOTH the client and the
    //  server can read/apply a fix's enabled-state uniformly (used by the menu, the net sync and the
    //  console commands). Each process has its OWN copy of the plugins' static Enabled flags:
    //  in the client assembly these are the CLIENT-side states, in the server assembly the SERVER-side.
    //  RUS: Общий реестр фиксов-оптимизаций по индексу, чтобы И клиент, И сервер единообразно читали/
    //  RUS: применяли состояние «включён» (используется меню, сетевой синхронизацией и командами). У каждого
    //  RUS: процесса СВОЯ копия статических флагов Enabled: в клиентской сборке это КЛИЕНТСКИЕ состояния,
    //  RUS: в серверной — СЕРВЕРНЫЕ.
    // ==========================================================================================
    internal static class OptFixes
    {
        public const int Count = 4;

        // Net message ids (LuaCs networking). Server->client: broadcast of the 3 server-side states.
        // Client->server: a request to set one server state (fix 0..2) OR to just resend the state (RequestState).
        // RUS: ID сетевых сообщений. Сервер->клиент: рассылка 3 серверных состояний. Клиент->сервер: запрос
        // RUS: установить одно серверное состояние (фикс 0..2) ЛИБО просто переслать состояние (RequestState).
        public const string MsgServerState = "ngopt_server_state";
        public const string MsgSetServer   = "ngopt_set_server";
        public const string MsgServerPerf  = "ngopt_server_perf"; // server->privileged clients: server frame/tick load   // RUS: сервер->привилегированные клиенты: серверная нагрузка по кадрам/тикам
        public const string MsgServerBench = "ngopt_server_bench"; // server->privileged client: server benchmark report   // RUS: сервер->привил.клиент: отчёт серверного бенчмарка
        public const byte   RequestState   = 255; // fix index meaning "just send me the current server state"   // RUS: индекс «просто пришли текущее серверное состояние»
        public const byte   RequestPerf    = 254; // fix index meaning "send me a server-perf snapshot"   // RUS: индекс «пришли снимок серверной нагрузки»
        public const byte   BenchStart     = 249; // fix index meaning "start the 60s server benchmark"   // RUS: индекс «запустить 60с серверный бенчмарк»
        public const byte   BenchCancel    = 248; // fix index meaning "cancel the running server benchmark"   // RUS: индекс «отменить идущий серверный бенчмарк»

        // Apply an enabled-state to fix #i in THIS process (client or server, depending on the assembly).
        // RUS: Применить состояние «включён» к фиксу #i в ЭТОМ процессе (клиент/сервер — смотря какая сборка).
        public static void SetEnabled(int i, bool on)
        {
            switch (i)
            {
                case 0: try { NGContainerOpt.ContainedEffectsOptPlugin.SetEnabled(on); } catch { } break;
                case 1: try { NGNearbyOpt.NearbyTargetsOptPlugin.SetEnabled(on); } catch { } break;
                case 2: try { NGRepairToolOpt.RepairToolThrottleOptPlugin.SetEnabled(on); } catch { } break;
                case 3: try { NGGearThrottleOpt.GearThrottleOptPlugin.SetEnabled(on); } catch { } break;
            }
        }

        public static bool GetEnabled(int i)
        {
            switch (i)
            {
                case 0: try { return NGContainerOpt.ContainedEffectsOptPlugin.Enabled; } catch { return false; }
                case 1: try { return NGNearbyOpt.NearbyTargetsOptPlugin.Enabled; } catch { return false; }
                case 2: try { return NGRepairToolOpt.RepairToolThrottleOptPlugin.Enabled; } catch { return false; }
                case 3: try { return NGGearThrottleOpt.GearThrottleOptPlugin.Enabled; } catch { return false; }
                default: return false;
            }
        }

        // Short display name of fix #i (localized).
        // RUS: Короткое отображаемое имя фикса #i (локализованное).
        public static string ShortName(int i)
        {
            switch (i)
            {
                case 0: return Loc.OptShort;
                case 1: return Loc.Opt2Short;
                case 2: return Loc.Opt3Short;
                case 3: return Loc.Opt4Short;
                default: return "?";
            }
        }

        // Tooltip of fix #i (localized).
        // RUS: Тултип фикса #i (локализованный).
        public static string Tip(int i)
        {
            switch (i)
            {
                case 0: return Loc.OptTip;
                case 1: return Loc.Opt2Tip;
                case 2: return Loc.Opt3Tip;
                case 3: return Loc.Opt4Tip;
                default: return "";
            }
        }

        public static bool IsExperimental(int i) => i == 2 || i == 3; // fixes 3 & 4 flagged experimental   // RUS: фиксы 3 и 4 помечены экспериментальными

        // Parse a fix selector from a console argument: accepts 1/2/3 or fix1/fix2/fix3. Returns -1 if invalid.
        // RUS: Разобрать селектор фикса из аргумента команды: принимает 1/2/3 или fix1/fix2/fix3. -1 если неверно.
        public static int ParseFix(string s)
        {
            if (string.IsNullOrEmpty(s)) { return -1; }
            s = s.Trim().ToLowerInvariant();
            if (s.StartsWith("fix")) { s = s.Substring(3); }
            if (s == "1") { return 0; }
            if (s == "2") { return 1; }
            if (s == "3") { return 2; }
            if (s == "4") { return 3; }
            return -1;
        }

        // Parse on/off from a console argument. Returns null if invalid.
        // RUS: Разобрать on/off из аргумента команды. null если неверно.
        public static bool? ParseOnOff(string s)
        {
            if (string.IsNullOrEmpty(s)) { return null; }
            s = s.Trim().ToLowerInvariant();
            if (s == "on" || s == "1" || s == "true" || s == "вкл") { return true; }
            if (s == "off" || s == "0" || s == "false" || s == "выкл") { return false; }
            return null;
        }
    }
}
