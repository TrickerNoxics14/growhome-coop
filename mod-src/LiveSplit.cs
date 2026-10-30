using System;
using System.Collections.Generic;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace CoopMod
{
    // Optional (off by default): drives LiveSplit through its own TCP server (LiveSplit: Control > Start TCP Server, port 16834).
    // Commands go to a queue; a background thread sends them, so the game never waits on the network. No server: they are dropped.
    public class LiveSplitClient
    {
        readonly object gate = new object();
        readonly Queue<string> queue = new Queue<string>();
        readonly string host; readonly int port;
        Thread thread; volatile bool stop;
        TcpClient client; NetworkStream stream;
        DateTime nextTry = DateTime.MinValue;
        public int Sent;
        public string LastProblem = "";

        public LiveSplitClient(string host, int port) { this.host = host; this.port = port; }

        public void Send(string command)
        {
            lock (gate)
            {
                if (queue.Count < 64) queue.Enqueue(command);
                Monitor.Pulse(gate);
            }
            if (thread == null) { thread = new Thread(Loop); thread.IsBackground = true; thread.Name = "CoopMod LiveSplit"; thread.Start(); }
        }

        public void Close() { stop = true; lock (gate) Monitor.Pulse(gate); Drop(); }

        void Drop() { try { if (client != null) client.Close(); } catch (Exception) { } client = null; stream = null; }

        void Loop()
        {
            while (!stop)
            {
                string cmd;
                lock (gate)
                {
                    while (queue.Count == 0 && !stop) Monitor.Wait(gate, 1000);
                    if (stop) break;
                    cmd = queue.Peek();
                }
                try
                {
                    if (stream == null)
                    {
                        if (DateTime.UtcNow < nextTry) { lock (gate) queue.Clear(); continue; }   // no server: what happened meanwhile is not sent late
                        var c = new TcpClient();
                        var ar = c.BeginConnect(host, port, null, null);
                        if (!ar.AsyncWaitHandle.WaitOne(200) || !c.Connected) { try { c.Close(); } catch (Exception) { } nextTry = DateTime.UtcNow.AddSeconds(10); LastProblem = "no LiveSplit server"; continue; }
                        c.EndConnect(ar);
                        client = c; stream = c.GetStream();
                    }
                    var bytes = Encoding.ASCII.GetBytes(cmd + "\r\n");
                    stream.Write(bytes, 0, bytes.Length);
                    lock (gate) { if (queue.Count > 0) queue.Dequeue(); }
                    Sent++;
                }
                catch (Exception e) { LastProblem = e.GetType().Name; Drop(); nextTry = DateTime.UtcNow.AddSeconds(10); }
            }
            Drop();
        }
    }

    public static class LiveSplit
    {
        public const int DefaultPort = 16834;
        static LiveSplitClient client;
        public static bool Enabled;

        public static LiveSplitClient Client { get { if (client == null) client = new LiveSplitClient("127.0.0.1", DefaultPort); return client; } }

        // start -> starttimer, a reached split -> split, a skipped one -> skipsplit, a reset or DNF -> reset
        public static string CommandFor(SrRun run, string what, int split)
        {
            if (what == "start") return "starttimer";
            if (what == "split") return "split";
            if (what == "skip") return "skipsplit";
            if (what == "end" && run != null && run.State != SrState.Finished) return "reset";
            return null;
        }

        public static void OnRun(SrRun run, string what, int split)
        {
            if (!Enabled) return;
            string c = CommandFor(run, what, split);
            if (c != null) Client.Send(c);
        }
    }
}
