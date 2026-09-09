using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace Orbis
{
    internal sealed class LoopbackPkgServer : IDisposable
    {
        // App loopback uses 8743; the resident shell listener owns 8742.
        // Sharing one port caused BGFT HTTP 404 (0x80991404) when a task
        // fetched from the process that did not own its route.
        public const int Port = 8743;
        public const int LegacyResidentPort = 8742;

        sealed class Entry
        {
            public string QueueId;
            public string Route;
            public string FinalPath;
            public long Length;
            public volatile bool Complete;
            public volatile bool Failed;
            public volatile bool FullyServed;
            public readonly List<long[]> Served = new List<long[]>();
        }

        readonly object _gate = new object();
        readonly int _requestedPort;
        readonly int _waitMilliseconds;
        TcpListener _listener;
        Thread _thread;
        Entry _active;
        volatile bool _stop;
        volatile bool _running;
        string _lastError = "stopped";

        public LoopbackPkgServer(int port = Port, int waitMilliseconds = 4000)
        {
            if (port < 0 || port > 65535) throw new ArgumentOutOfRangeException("port");
            if (waitMilliseconds < 50) throw new ArgumentOutOfRangeException("waitMilliseconds");
            _requestedPort = port;
            _waitMilliseconds = waitMilliseconds;
        }

        public bool Running { get { return _running; } }
        public int BoundPort { get; private set; }
        public string LastError { get { lock (_gate) return _lastError; } }

        public bool TryRegister(string queueId, string finalPkgPath, long expectedLength,
            out string url, out string error)
        {
            bool busy;
            return TryRegister(queueId, finalPkgPath, expectedLength, out url, out error, out busy);
        }

        public bool TryRegister(string queueId, string finalPkgPath, long expectedLength,
            out string url, out string error, out bool busy)
        {
            url = null;
            error = null;
            busy = false;
            if (string.IsNullOrWhiteSpace(queueId) || queueId.Length > 128)
            {
                error = "Loopback PKG: invalid queue id";
                return false;
            }
            if (string.IsNullOrWhiteSpace(finalPkgPath) || expectedLength < 4)
            {
                error = "Loopback PKG: invalid file metadata";
                return false;
            }
            if (!TryStart(out error)) return false;

            string route = "/pkg/" + Uri.EscapeDataString(queueId) + ".pkg";
            lock (_gate)
            {
                if (_active != null)
                {
                    if (_active.QueueId == queueId &&
                        string.Equals(_active.FinalPath, finalPkgPath, StringComparison.OrdinalIgnoreCase) &&
                        _active.Length == expectedLength && !_active.Failed)
                    {
                        url = BuildUrl(route);
                        return true;
                    }
                    busy = true;
                    error = "Loopback PKG: another system download owns the feeder";
                    return false;
                }
                _active = new Entry
                {
                    QueueId = queueId,
                    Route = route,
                    FinalPath = finalPkgPath,
                    Length = expectedLength
                };
                url = BuildUrl(route);
                return true;
            }
        }

        public bool TryMarkComplete(string queueId, out string error)
        {
            error = null;
            Entry entry;
            lock (_gate)
            {
                entry = _active;
                if (entry == null || entry.QueueId != queueId)
                {
                    error = "Loopback PKG: queue does not own the feeder";
                    return false;
                }
            }

            FileStream file;
            if (!TryOpen(entry, entry.Length, out file))
            {
                error = "Loopback PKG: complete file is unavailable";
                return false;
            }
            using (file)
            {
                if (file.Length != entry.Length || !HasPkgMagic(file))
                {
                    error = "Loopback PKG: invalid complete PKG";
                    return false;
                }
            }
            entry.Complete = true;
            return true;
        }

        public void MarkFailed(string queueId)
        {
            lock (_gate)
                if (_active != null && _active.QueueId == queueId)
                    _active.Failed = true;
        }

        public bool IsAlive(string queueId)
        {
            lock (_gate)
                return _running && _active != null && _active.QueueId == queueId && !_active.Failed;
        }

        public bool IsOwnedByOther(string queueId)
        {
            lock (_gate)
                return _active != null && _active.QueueId != queueId && !_active.Failed;
        }

        public bool WasFullyServed(string queueId)
        {
            lock (_gate)
                return _active != null && _active.QueueId == queueId && _active.FullyServed;
        }

        public void Release(string queueId)
        {
            lock (_gate)
                if (_active != null && _active.QueueId == queueId)
                    _active = null;
        }

        bool TryStart(out string error)
        {
            error = null;
            lock (_gate)
            {
                if (_running) return true;
                try
                {
                    _stop = false;
                    _listener = new TcpListener(IPAddress.Loopback, _requestedPort);
                    _listener.Start();
                    BoundPort = ((IPEndPoint)_listener.LocalEndpoint).Port;
                    _thread = new Thread(ListenLoop) { IsBackground = true, Name = "PKG loopback" };
                    _running = true;
                    _lastError = "ok";
                    _thread.Start();
                    return true;
                }
                catch (Exception ex)
                {
                    _running = false;
                    _lastError = ex.GetType().Name + ": " + ex.Message;
                    error = "Loopback PKG: " + _lastError;
                    return false;
                }
            }
        }

        void ListenLoop()
        {
            try
            {
                while (!_stop)
                {
                    TcpClient client;
                    try { client = _listener.AcceptTcpClient(); }
                    catch (SocketException) { if (_stop) break; throw; }
                    catch (ObjectDisposedException) { break; }
                    ThreadPool.QueueUserWorkItem(HandleClient, client);
                }
            }
            catch (Exception ex)
            {
                lock (_gate) _lastError = ex.GetType().Name + ": " + ex.Message;
            }
            finally
            {
                _running = false;
                try { if (_listener != null) _listener.Stop(); } catch { }
            }
        }

        void HandleClient(object state)
        {
            using (var client = (TcpClient)state)
            {
                try
                {
                    client.ReceiveTimeout = 5000;
                    client.SendTimeout = 30000;
                    using (NetworkStream stream = client.GetStream()) Serve(stream);
                }
                catch (IOException) { }
                catch (SocketException) { }
                catch { }
            }
        }

        void Serve(NetworkStream stream)
        {
            string request = ReadHeaders(stream);
            if (string.IsNullOrEmpty(request)) return;
            string[] first = request.Split(new[] { "\r\n" }, StringSplitOptions.None)[0].Split(' ');
            if (first.Length < 2)
            {
                WriteEmpty(stream, 400, "Bad Request", null);
                return;
            }
            string method = first[0].ToUpperInvariant();
            if (method != "GET" && method != "HEAD")
            {
                WriteEmpty(stream, 405, "Method Not Allowed", "Allow: GET, HEAD\r\n");
                return;
            }

            string path = RequestPath(first[1]);
            Entry entry;
            lock (_gate) entry = _active != null && _active.Route == path ? _active : null;
            if (entry == null)
            {
                WriteEmpty(stream, 404, "Not Found", null);
                return;
            }

            long start = 0;
            long end = entry.Length - 1;
            bool partial = false;
            string range = Header(request, "Range");
            if (!string.IsNullOrEmpty(range))
            {
                partial = true;
                if (!TryParseRange(range, entry.Length, out start, out end))
                {
                    WriteEmpty(stream, 416, "Range Not Satisfiable",
                        "Content-Range: bytes */" + entry.Length + "\r\nAccept-Ranges: bytes\r\n");
                    return;
                }
            }

            string representation = RepresentationHeaders(entry, partial, start, end);
            if (method == "HEAD")
            {
                WriteHead(stream, partial ? 206 : 200, partial ? "Partial Content" : "OK", representation);
                stream.Flush();
                return;
            }

            bool needsComplete = end == entry.Length - 1;
            FileStream file = WaitForRange(entry, end + 1, needsComplete);
            if (file == null)
            {
                WriteEmpty(stream, 503, "Service Unavailable", "Retry-After: 1\r\n");
                return;
            }

            using (file)
            {
                file.Position = start;
                WriteHead(stream, partial ? 206 : 200, partial ? "Partial Content" : "OK", representation);
                byte[] buffer = new byte[256 * 1024];
                long remaining = end - start + 1;
                while (remaining > 0)
                {
                    int wanted = (int)Math.Min(buffer.Length, remaining);
                    int read = file.Read(buffer, 0, wanted);
                    if (read <= 0) return;
                    stream.Write(buffer, 0, read);
                    remaining -= read;
                }
                stream.Flush();
                RecordServed(entry, start, end);
            }
        }

        static void RecordServed(Entry entry, long start, long end)
        {
            lock (entry)
            {
                int at = 0;
                while (at < entry.Served.Count && entry.Served[at][1] < start - 1) at++;
                while (at < entry.Served.Count && entry.Served[at][0] <= end + 1)
                {
                    start = Math.Min(start, entry.Served[at][0]);
                    end = Math.Max(end, entry.Served[at][1]);
                    entry.Served.RemoveAt(at);
                }
                entry.Served.Insert(at, new[] { start, end });
                entry.FullyServed = entry.Served.Count == 1 && entry.Served[0][0] == 0 &&
                    entry.Served[0][1] >= entry.Length - 1;
            }
        }

        FileStream WaitForRange(Entry entry, long requiredLength, bool needsComplete)
        {
            DateTime until = DateTime.UtcNow.AddMilliseconds(_waitMilliseconds);
            do
            {
                if (entry.Failed) return null;
                if (!needsComplete || entry.Complete)
                {
                    FileStream file;
                    if (TryOpen(entry, requiredLength, out file))
                    {
                        if (file.Length <= entry.Length && HasPkgMagic(file)) return file;
                        file.Dispose();
                    }
                }
                Thread.Sleep(50);
            }
            while (DateTime.UtcNow < until && _running);
            return null;
        }

        static bool TryOpen(Entry entry, long requiredLength, out FileStream file)
        {
            file = null;
            string[] paths = { entry.FinalPath, entry.FinalPath + ".part" };
            foreach (string path in paths)
            {
                try
                {
                    if (!File.Exists(path)) continue;
                    var candidate = new FileStream(path, FileMode.Open, FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete, 256 * 1024);
                    if (candidate.Length >= requiredLength)
                    {
                        file = candidate;
                        return true;
                    }
                    candidate.Dispose();
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            return false;
        }

        static bool HasPkgMagic(FileStream file)
        {
            if (file == null || file.Length < 4) return false;
            long position = file.Position;
            file.Position = 0;
            int a = file.ReadByte();
            int b = file.ReadByte();
            int c = file.ReadByte();
            int d = file.ReadByte();
            file.Position = position;
            return a == 0x7F && b == 0x43 && c == 0x4E && d == 0x54;
        }

        static bool TryParseRange(string header, long length, out long start, out long end)
        {
            start = end = 0;
            if (length <= 0 || string.IsNullOrEmpty(header) ||
                !header.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase)) return false;
            string value = header.Substring(6).Trim();
            if (value.IndexOf(',') >= 0) return false;
            int dash = value.IndexOf('-');
            if (dash < 0) return false;
            string left = value.Substring(0, dash).Trim();
            string right = value.Substring(dash + 1).Trim();
            if (left.Length == 0)
            {
                long suffix;
                if (!long.TryParse(right, out suffix) || suffix <= 0) return false;
                if (suffix > length) suffix = length;
                start = length - suffix;
                end = length - 1;
                return true;
            }
            if (!long.TryParse(left, out start) || start < 0 || start >= length) return false;
            if (right.Length == 0) end = length - 1;
            else if (!long.TryParse(right, out end) || end < start) return false;
            if (end >= length) end = length - 1;
            return true;
        }

        static string ReadHeaders(NetworkStream stream)
        {
            var data = new MemoryStream();
            int match = 0;
            while (data.Length < 16384)
            {
                int value = stream.ReadByte();
                if (value < 0) return null;
                data.WriteByte((byte)value);
                match = match == 0 && value == '\r' ? 1 :
                    match == 1 && value == '\n' ? 2 :
                    match == 2 && value == '\r' ? 3 :
                    match == 3 && value == '\n' ? 4 : 0;
                if (match == 4) return Encoding.ASCII.GetString(data.ToArray());
            }
            return null;
        }

        static string Header(string request, string name)
        {
            string prefix = name + ":";
            foreach (string line in request.Split(new[] { "\r\n" }, StringSplitOptions.None))
                if (line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return line.Substring(prefix.Length).Trim();
            return null;
        }

        static string RequestPath(string target)
        {
            Uri absolute;
            if (Uri.TryCreate(target, UriKind.Absolute, out absolute)) return absolute.AbsolutePath;
            int query = target.IndexOf('?');
            return query < 0 ? target : target.Substring(0, query);
        }

        static string RepresentationHeaders(Entry entry, bool partial, long start, long end)
        {
            long count = end - start + 1;
            return "Content-Type: application/octet-stream\r\n" +
                "Content-Length: " + count + "\r\n" +
                "Accept-Ranges: bytes\r\n" +
                (partial ? "Content-Range: bytes " + start + "-" + end + "/" + entry.Length + "\r\n" : "") +
                "Cache-Control: no-transform\r\n";
        }

        static void WriteEmpty(NetworkStream stream, int code, string reason, string extra)
        {
            WriteHead(stream, code, reason, (extra ?? "") + "Content-Length: 0\r\n");
            stream.Flush();
        }

        static void WriteHead(NetworkStream stream, int code, string reason, string extra)
        {
            byte[] data = Encoding.ASCII.GetBytes("HTTP/1.1 " + code + " " + reason + "\r\n" +
                (extra ?? "") + "Connection: close\r\n\r\n");
            stream.Write(data, 0, data.Length);
        }

        string BuildUrl(string route)
        {
            return "http://127.0.0.1:" + BoundPort + route;
        }

        public void Stop()
        {
            _stop = true;
            _running = false;
            try { if (_listener != null) _listener.Stop(); } catch { }
            Thread thread = _thread;
            if (thread != null && thread != Thread.CurrentThread) thread.Join(2000);
            _listener = null;
            _thread = null;
        }

        public void Dispose()
        {
            Stop();
        }
    }
}
