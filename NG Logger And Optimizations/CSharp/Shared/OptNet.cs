using System;
using System.Reflection;
using Barotrauma;
using Barotrauma.Networking;
using HarmonyLib;

namespace NetEventLogger
{
    // ==========================================================================================
    //  Thin, REFLECTION-ISOLATED wrapper around LuaCs networking (LuaCsSetup.Instance.Networking).
    //  Why reflection: the networking service type is internal; calling it via reflection means this
    //  file always COMPILES regardless of access levels, and any runtime failure is swallowed -> the
    //  rest of the mod (client fixes, menu, profiler) can never be broken by a networking problem.
    //  If networking is unavailable, the menu simply shows the server state as unknown ("—") and the
    //  always-available server console command remains the fallback for controlling server fixes.
    //  RUS: Тонкая обёртка над сетью LuaCs (LuaCsSetup.Instance.Networking) ЧЕРЕЗ РЕФЛЕКСИЮ. Зачем
    //  RUS: рефлексия: тип сетевого сервиса internal; вызов через рефлексию => файл ВСЕГДА компилируется
    //  RUS: при любой видимости, а любой сбой в рантайме проглатывается -> остальной мод (клиентские
    //  RUS: фиксы, меню, профайлер) сеть сломать не может. Если сеть недоступна — меню показывает
    //  RUS: серверное состояние как «—», а всегда-доступная серверная консольная команда остаётся
    //  RUS: запасным способом управления серверными фиксами.
    // ==========================================================================================
    internal static class OptNet
    {
        private static bool       _tried;
        private static object     _net;          // INetworkingService (internal) held as object   // RUS: INetworkingService (internal) как object
        private static MethodInfo _start;        // IWriteMessage Start(string)
        private static MethodInfo _receive;      // void Receive(string, LuaCsAction)
        private static MethodInfo _send;         // void Send(...) — client/server signature
        private static bool       _serverSend;   // true if _send takes a NetworkConnection (server)   // RUS: true если _send принимает NetworkConnection (сервер)

        private static void Resolve()
        {
            if (_tried) { return; }
            _tried = true;
            try
            {
                Type setupT = AccessTools.TypeByName("Barotrauma.LuaCsSetup");
                if (setupT == null) { return; }
                object inst = AccessTools.Property(setupT, "Instance")?.GetValue(null);
                _net = AccessTools.Property(setupT, "Networking")?.GetValue(inst);
                if (_net == null) { return; }
                Type nt = _net.GetType();
                _start   = AccessTools.Method(nt, "Start",   new[] { typeof(string) });
                _receive = AccessTools.Method(nt, "Receive", new[] { typeof(string), typeof(LuaCsAction) });
                // server Send has a NetworkConnection param; client Send does not
                // RUS: серверный Send имеет параметр NetworkConnection; клиентский — нет
                _send = AccessTools.Method(nt, "Send", new[] { typeof(IWriteMessage), typeof(NetworkConnection), typeof(DeliveryMethod) });
                if (_send != null) { _serverSend = true; }
                else { _send = AccessTools.Method(nt, "Send", new[] { typeof(IWriteMessage), typeof(DeliveryMethod) }); _serverSend = false; }
            }
            catch { }
        }

        public static bool Available
        {
            get { Resolve(); return _net != null && _start != null && _receive != null && _send != null; }
        }

        public static bool IsServer { get { Resolve(); return _serverSend; } }

        public static IWriteMessage Start(string id)
        {
            Resolve();
            try { return _start?.Invoke(_net, new object[] { id }) as IWriteMessage; }
            catch { return null; }
        }

        public static void Receive(string id, LuaCsAction callback)
        {
            Resolve();
            try { _receive?.Invoke(_net, new object[] { id, callback }); }
            catch { }
        }

        // A no-op receiver: register it for a message this side only SENDS, so its net id gets assigned
        // (server) / requested (client) and Start() can write it. Never actually fires.
        // RUS: Пустой приёмник: регистрируем для сообщения, которое эта сторона только ОТПРАВЛЯЕТ, чтобы его
        // RUS: net id был назначен (сервер) / запрошен (клиент) и Start() мог его записать. Никогда не срабатывает.
        public static void NoOp(object[] args) { }

        // Client: send to server (the message already carries its net id). Server: broadcast to ALL clients.
        // RUS: Клиент: отправить серверу (сообщение уже несёт свой net id). Сервер: broadcast ВСЕМ клиентам.
        public static void Send(IWriteMessage msg)
        {
            Resolve();
            if (msg == null || _send == null) { return; }
            try
            {
                if (_serverSend) { _send.Invoke(_net, new object[] { msg, null, DeliveryMethod.Reliable }); }
                else             { _send.Invoke(_net, new object[] { msg, DeliveryMethod.Reliable }); }
            }
            catch { }
        }

        // Server only: send to ONE client connection (e.g. answering a state request).
        // RUS: Только сервер: отправить ОДНОМУ клиентскому соединению (напр. ответ на запрос состояния).
        public static void SendTo(IWriteMessage msg, NetworkConnection conn)
        {
            Resolve();
            if (msg == null || _send == null || !_serverSend) { return; }
            try { _send.Invoke(_net, new object[] { msg, conn, DeliveryMethod.Reliable }); }
            catch { }
        }
    }
}
