using System;
using System.IO.Pipes;
using System.Text;
using System.Threading;

namespace OngekiDiscordRP
{
    /// <summary>
    /// Minimal, dependency-free Discord Rich Presence client.
    /// Talks to the local Discord desktop client over its IPC named pipe
    /// (\\.\pipe\discord-ipc-N). No internet, no external libraries, no JSON
    /// dependency - just the small subset of the protocol needed for SET_ACTIVITY.
    ///
    /// Single background thread does everything: connect, handshake, then for each
    /// activity it writes the command and reads back the reply, matching Discord's
    /// reply by our nonce (skipping any unsolicited frames). Keeping read and write
    /// on one thread avoids the frame desync / cross-thread pipe issues that a
    /// naive "write one / read one" or two-thread model runs into.
    /// </summary>
    internal sealed class DiscordIpcClient
    {
        private const int OpHandshake = 0;
        private const int OpFrame = 1;
        private const int OpClose = 2;

        private readonly string _clientId;
        private readonly int _pid;
        private readonly Action<string> _logInfo;
        private readonly Action<string> _logWarn;

        private Thread _thread;
        private volatile bool _running;
        private bool _connected;

        private NamedPipeClientStream _pipe;

        private readonly object _gate = new object();
        private string _pendingActivityJson;
        private bool _dirty;
        private bool _loggedFirstReply;

        public DiscordIpcClient(string clientId, Action<string> logInfo, Action<string> logWarn)
        {
            _clientId = clientId;
            _logInfo = logInfo;
            _logWarn = logWarn;
            _pid = System.Diagnostics.Process.GetCurrentProcess().Id;
        }

        public void Start()
        {
            if (_running) return;
            _running = true;
            _thread = new Thread(Loop) { IsBackground = true, Name = "OngekiDiscordRP" };
            _thread.Start();
        }

        public void Stop()
        {
            _running = false;
            try { ClosePipe(); } catch { }
            try { if (_thread != null) _thread.Join(750); } catch { }
        }

        /// <summary>Queue a new activity (raw "activity" JSON), or null to clear. Only sent when it changes.</summary>
        public void SetActivity(string activityJson)
        {
            lock (_gate)
            {
                if (_pendingActivityJson == activityJson && !_dirty) return;
                _pendingActivityJson = activityJson;
                _dirty = true;
            }
        }

        private void Loop()
        {
            while (_running)
            {
                try
                {
                    if (!_connected)
                    {
                        if (!TryConnect()) { Sleep(5000); continue; }
                    }

                    string toSend = null;
                    bool send = false;
                    lock (_gate)
                    {
                        if (_dirty) { toSend = _pendingActivityJson; _dirty = false; send = true; }
                    }

                    if (send) SendActivity(toSend);
                    Sleep(250);
                }
                catch (Exception e)
                {
                    if (_logWarn != null) _logWarn("Discord IPC error (" + e.Message + "), reconnecting.");
                    ClosePipe();
                    Sleep(5000);
                }
            }
            ClosePipe();
        }

        private bool TryConnect()
        {
            for (int i = 0; i < 10; i++)
            {
                NamedPipeClientStream pipe = null;
                try
                {
                    pipe = new NamedPipeClientStream(".", "discord-ipc-" + i,
                        PipeDirection.InOut, PipeOptions.None);
                    pipe.Connect(500);
                    _pipe = pipe;

                    WriteFrame(OpHandshake, "{\"v\":1,\"client_id\":\"" + _clientId + "\"}");
                    int op;
                    string ready = ReadFrame(out op); // READY dispatch
                    if (op == OpClose || IsError(ready))
                    {
                        if (_logWarn != null)
                            _logWarn("Discord rejected the handshake -> check ClientId. Reply: " + Trim(ready));
                        try { pipe.Dispose(); } catch { }
                        _pipe = null;
                        return false;
                    }

                    _connected = true;
                    _loggedFirstReply = false;
                    if (_logInfo != null) _logInfo("Connected to Discord (discord-ipc-" + i + "). Ready: " + Trim(ready));

                    lock (_gate) { _dirty = true; } // (re)send current activity
                    return true;
                }
                catch
                {
                    try { if (pipe != null) pipe.Dispose(); } catch { }
                    _pipe = null;
                }
            }
            return false;
        }

        private void SendActivity(string activityJson)
        {
            string nonce = Guid.NewGuid().ToString("N");
            string activity = activityJson ?? "null";
            string payload = "{\"cmd\":\"SET_ACTIVITY\",\"nonce\":\"" + nonce +
                             "\",\"args\":{\"pid\":" + _pid + ",\"activity\":" + activity + "}}";

            WriteFrame(OpFrame, payload);

            // Read frames until we see the reply carrying our nonce (or an error / close).
            string reply = ReadReplyForNonce(nonce);

            if (reply == null)
            {
                if (_logWarn != null) _logWarn("No reply from Discord for presence (nonce " + nonce + ").");
                return;
            }
            if (IsError(reply))
            {
                if (_logWarn != null) _logWarn("Discord REJECTED presence: " + Trim(reply) + "  | payload was: " + Trim(payload));
            }
            else if (!_loggedFirstReply)
            {
                _loggedFirstReply = true;
                if (_logInfo != null) _logInfo("Discord ACCEPTED presence. Reply: " + Trim(reply));
            }
        }

        private string ReadReplyForNonce(string nonce)
        {
            string needle = "\"nonce\":\"" + nonce + "\"";
            for (int k = 0; k < 12; k++)
            {
                int op;
                string body = ReadFrame(out op);
                if (op == OpClose)
                {
                    if (_logWarn != null) _logWarn("Discord closed the connection: " + Trim(body));
                    throw new Exception("closed by Discord");
                }
                if (body.IndexOf(needle, StringComparison.Ordinal) >= 0)
                    return body; // our reply (success or error)

                // Unsolicited frame (e.g. READY leftovers, events). Log errors, skip the rest.
                if (IsError(body) && _logWarn != null)
                    _logWarn("Discord sent an unsolicited error: " + Trim(body));
            }
            return null;
        }

        private void WriteFrame(int opcode, string json)
        {
            if (_pipe == null) throw new InvalidOperationException("pipe not open");
            byte[] data = Encoding.UTF8.GetBytes(json);
            byte[] header = new byte[8];
            WriteInt32LE(header, 0, opcode);
            WriteInt32LE(header, 4, data.Length);
            _pipe.Write(header, 0, header.Length);
            _pipe.Write(data, 0, data.Length);
            _pipe.Flush();
        }

        private string ReadFrame(out int opcode)
        {
            byte[] header = ReadExact(8);
            if (header == null) throw new Exception("pipe closed by Discord");
            opcode = ReadInt32LE(header, 0);
            int len = ReadInt32LE(header, 4);
            if (len <= 0 || len >= (1 << 20)) return "";
            byte[] body = ReadExact(len);
            if (body == null) throw new Exception("pipe closed mid-frame");
            return Encoding.UTF8.GetString(body);
        }

        private byte[] ReadExact(int count)
        {
            byte[] buf = new byte[count];
            int off = 0;
            while (off < count)
            {
                int r = _pipe.Read(buf, off, count - off);
                if (r <= 0) return null;
                off += r;
            }
            return buf;
        }

        private void ClosePipe()
        {
            _connected = false;
            try { if (_pipe != null) _pipe.Dispose(); } catch { }
            _pipe = null;
        }

        private void Sleep(int ms)
        {
            int slept = 0;
            while (_running && slept < ms) { Thread.Sleep(50); slept += 50; }
        }

        private static bool IsError(string json)
        {
            if (string.IsNullOrEmpty(json)) return false;
            return json.IndexOf("\"evt\":\"ERROR\"", StringComparison.Ordinal) >= 0;
        }

        private static string Trim(string s)
        {
            if (string.IsNullOrEmpty(s)) return "(empty)";
            return s.Length <= 400 ? s : s.Substring(0, 400) + "...";
        }

        private static void WriteInt32LE(byte[] b, int o, int v)
        {
            b[o] = (byte)v;
            b[o + 1] = (byte)(v >> 8);
            b[o + 2] = (byte)(v >> 16);
            b[o + 3] = (byte)(v >> 24);
        }

        private static int ReadInt32LE(byte[] b, int o)
        {
            return b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24);
        }
    }
}
