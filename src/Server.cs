using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Newtonsoft.Json.Linq;

namespace TerraInvictaMCP
{
    public class Request
    {
        public long id;
        public string cmd;
        public JObject args;
        public string parseError;
    }

    class WorkItem
    {
        public Connection conn;
        public Request req;
    }

    // One socket, one reader thread, one writer thread. The writer owns all blocking
    // sends so a stuck client can never stall the game's frame.
    class Connection
    {
        // A client that never sends a newline is a client we stop talking to.
        public const int MaxLineBytes = 1024 * 1024;

        readonly TcpClient client;
        readonly Queue<string> outbox = new Queue<string>();
        readonly object gate = new object();
        bool closed;
        bool readerDone;    // peer half-closed; no further requests will arrive
        int outstanding;    // requests handed to the queue but not yet answered

        public Connection(TcpClient client)
        {
            this.client = client;
        }

        public void Start()
        {
            var reader = new Thread(ReadLoop);
            reader.IsBackground = true;
            reader.Name = "TerraInvictaMCP reader";
            reader.Start();
            var writer = new Thread(WriteLoop);
            writer.IsBackground = true;
            writer.Name = "TerraInvictaMCP writer";
            writer.Start();
        }

        public void Send(string line)
        {
            lock (gate)
            {
                if (closed) return;
                if (outstanding > 0) outstanding--;
                outbox.Enqueue(line);
                Monitor.Pulse(gate);
            }
        }

        public void Close()
        {
            lock (gate)
            {
                if (closed) return;
                closed = true;
                outbox.Clear();
                Monitor.PulseAll(gate);
            }
            try { client.Close(); } catch (Exception) { }
            Server.Remove(this);
        }

        // Peer sent EOF. Keep the socket alive so answers to requests it already sent
        // can still be written; the writer closes once the last one is out.
        void HalfClose()
        {
            lock (gate)
            {
                readerDone = true;
                Monitor.PulseAll(gate);
            }
        }

        // Close() disposes the socket under the reader and the writer, so both loops
        // fault as part of routine teardown. Only a failure that arrives while the
        // connection is still open says anything; reporting the rest would keep
        // overwriting the single LastError slot with normal shutdown.
        void ReportUnlessClosed(string where, Exception e)
        {
            lock (gate)
            {
                if (closed) return;
                Server.LastError = where + ": " + Verbs.Note(e);
            }
        }

        void ReadLoop()
        {
            bool eof = false;
            try
            {
                var stream = client.GetStream();
                var encoding = new UTF8Encoding(false);
                byte[] chunk = new byte[4096];
                byte[] pending = new byte[1024];
                int pendingLen = 0;
                while (true)
                {
                    int n = stream.Read(chunk, 0, chunk.Length);
                    if (n <= 0) { eof = true; break; }
                    for (int i = 0; i < n; i++)
                    {
                        byte b = chunk[i];
                        if (b == 10)
                        {
                            int len = pendingLen;
                            if (len > 0 && pending[len - 1] == 13) len--;
                            string line = encoding.GetString(pending, 0, len);
                            pendingLen = 0;
                            if (line.Trim().Length == 0) continue;
                            lock (gate)
                            {
                                if (closed) return;
                                outstanding++;
                            }
                            Server.Enqueue(this, Parse(line));
                            continue;
                        }
                        if (pendingLen >= MaxLineBytes)
                        {
                            Server.LastError = "line exceeded " + MaxLineBytes + " bytes, connection closed";
                            Close();
                            return;
                        }
                        if (pendingLen == pending.Length)
                        {
                            int grow = pending.Length * 2;
                            if (grow > MaxLineBytes) grow = MaxLineBytes;
                            byte[] bigger = new byte[grow];
                            Buffer.BlockCopy(pending, 0, bigger, 0, pendingLen);
                            pending = bigger;
                        }
                        pending[pendingLen++] = b;
                    }
                }
            }
            catch (Exception e) { ReportUnlessClosed("read loop", e); }
            if (eof) HalfClose();
            else Close();
        }

        void WriteLoop()
        {
            try
            {
                var stream = client.GetStream();
                var encoding = new UTF8Encoding(false);
                while (true)
                {
                    string line;
                    lock (gate)
                    {
                        while (!closed && outbox.Count == 0)
                        {
                            if (readerDone && outstanding == 0) return;
                            Monitor.Wait(gate);
                        }
                        if (closed) return;
                        line = outbox.Dequeue();
                    }
                    byte[] bytes = encoding.GetBytes(line + "\n");
                    stream.Write(bytes, 0, bytes.Length);
                    stream.Flush();
                }
            }
            catch (Exception e) { ReportUnlessClosed("write loop", e); }
            finally { Close(); }
        }

        // Parsing happens off the main thread; failures ride the same queue so a client
        // still gets its frames in the order it sent them.
        static Request Parse(string line)
        {
            var req = new Request();
            try
            {
                JObject o = JObject.Parse(line);
                JToken idToken = o["id"];
                if (idToken != null && idToken.Type == JTokenType.Integer)
                    req.id = (long)idToken;
                JToken cmdToken = o["cmd"];
                if (cmdToken != null && cmdToken.Type == JTokenType.String) req.cmd = (string)cmdToken;
                req.args = o["args"] as JObject;
                if (string.IsNullOrEmpty(req.cmd)) req.parseError = "missing or non-string 'cmd'";
            }
            catch (Exception e)
            {
                req.id = 0;
                req.parseError = "malformed request: " + e.Message;
            }
            return req;
        }
    }

    public static class Server
    {
        public const int Port = 17470;

        // Frame budget: a burst of queued work is spread across frames instead of
        // freezing the game for the length of the burst.
        public const int MaxCommandsPerDrain = 64;

        static readonly ConcurrentQueue<WorkItem> queue = new ConcurrentQueue<WorkItem>();
        static readonly List<Connection> connections = new List<Connection>();
        static readonly object connGate = new object();
        static TcpListener listener;
        static volatile bool stopped;

        public static string LastError = "";

        // The listener is the running state: Start sets it, Stop and an accept
        // failure clear it.
        public static bool Running
        {
            get { return listener != null; }
        }

        public static int ConnectionCount
        {
            get { lock (connGate) return connections.Count; }
        }

        // Returns true when this call actually opened the listener.
        public static bool Start()
        {
            if (listener != null) return false;
            var l = new TcpListener(IPAddress.Loopback, Port);
            l.Start();
            stopped = false;
            listener = l;
            var accept = new Thread(AcceptLoop);
            accept.IsBackground = true;
            accept.Name = "TerraInvictaMCP accept";
            accept.Start();
            return true;
        }

        public static void Stop()
        {
            stopped = true;
            var l = listener;
            listener = null;
            if (l != null)
            {
                try { l.Stop(); } catch (Exception) { }
            }
            // Work queued before the toggle must not execute on the next enable.
            WorkItem drop;
            while (queue.TryDequeue(out drop)) { }
            Connection[] live;
            lock (connGate) live = connections.ToArray();
            for (int i = 0; i < live.Length; i++)
            {
                try { live[i].Close(); } catch (Exception) { }
            }
        }

        static void AcceptLoop()
        {
            TcpListener mine = listener;
            while (true)
            {
                TcpClient client;
                try { client = mine.AcceptTcpClient(); }
                catch (Exception)
                {
                    Fail(mine);
                    return;
                }
                try
                {
                    client.NoDelay = true;
                    var conn = new Connection(client);
                    bool late;
                    // Stop() flips the flag before it snapshots the list, so a connection
                    // registered on either side of that snapshot still gets closed.
                    lock (connGate)
                    {
                        connections.Add(conn);
                        late = stopped;
                    }
                    if (late) conn.Close();
                    else conn.Start();
                }
                catch (Exception e)
                {
                    LastError = e.Message;
                    try { client.Close(); } catch (Exception) { }
                }
            }
        }

        // An accept failure on a listener Stop() has not replaced means the server
        // died on its own; clearing the field is what reports it down.
        static void Fail(TcpListener mine)
        {
            if (ReferenceEquals(mine, listener)) listener = null;
        }

        internal static void Enqueue(Connection conn, Request req)
        {
            var item = new WorkItem();
            item.conn = conn;
            item.req = req;
            queue.Enqueue(item);
        }

        internal static void Remove(Connection conn)
        {
            lock (connGate) connections.Remove(conn);
        }

        // Main thread only. Every game API call in the mod happens under this.
        public static void Drain()
        {
            WorkItem item;
            int done = 0;
            while (done < MaxCommandsPerDrain && queue.TryDequeue(out item))
            {
                done++;
                string line;
                try
                {
                    line = Verbs.Execute(item.req);
                }
                catch (Exception e)
                {
                    // Verbs.Execute answers its own failures; reaching here means the
                    // response itself could not be built, so frame a minimal reply
                    // rather than leave the client waiting.
                    LastError = Verbs.Note(e);
                    line = Verbs.Error(item.req.id, "internal error");
                }
                try { item.conn.Send(line); }
                catch (Exception) { item.conn.Close(); }
            }
        }
    }
}
