using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Web.Script.Serialization;

namespace CodexUsageOverlay
{
    // One active conversation only. Keep numeric samples and byte offsets, never transcript text.
    internal sealed class CodexTurnTimingReader
    {
        private const int MaxRecordBytes = 256 * 1024;
        private readonly string sessionsRoot;
        private readonly Dictionary<string, Cursor> files = new Dictionary<string, Cursor>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, long> samples = new Dictionary<string, long>(StringComparer.Ordinal);
        private readonly byte[] buffer = new byte[32768];
        private string threadId;
        internal long BytesReadLastRefresh { get; private set; }

        private sealed class Cursor
        {
            internal long Offset;
            internal DateTime WriteUtc;
            internal int PrefixLength;
            internal ulong PrefixHash;
            internal byte[] Pending = new byte[512];
            internal int Count;
            internal bool Skip;
        }

        internal CodexTurnTimingReader(string root) { sessionsRoot = root; }

        internal CodexContextSignal Read(CodexThreadDescriptor thread, CodexContextSignal signal)
        {
            BytesReadLastRefresh = 0;
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string path in thread.RolloutPaths.Count > 0 ? (IList<string>)thread.RolloutPaths : new[] { thread.Path })
            {
                string resolved = CodexThreadContextMonitor.ResolveRolloutPath(path, sessionsRoot);
                if (resolved != null) paths.Add(resolved);
            }
            bool reset = threadId != thread.Id || files.Count != paths.Count;
            foreach (string path in paths)
            {
                Cursor cursor;
                if (!files.TryGetValue(path, out cursor)) { reset = true; continue; }
                try
                {
                    var info = new FileInfo(path);
                    if (info.Exists && (info.Length < cursor.Offset ||
                        (info.Length == cursor.Offset && info.LastWriteTimeUtc != cursor.WriteUtc))) reset = true;
                    else if (info.Exists && info.LastWriteTimeUtc != cursor.WriteUtc && cursor.PrefixLength > 0)
                        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                            if (PrefixHash(stream, cursor.PrefixLength) != cursor.PrefixHash) reset = true;
                }
                catch (IOException) { }
            }
            if (reset)
            {
                files.Clear(); samples.Clear(); threadId = thread.Id;
                foreach (string path in paths) files.Add(path, new Cursor());
            }
            foreach (string path in paths)
            {
                try { ReadFile(path, files[path]); }
                catch (IOException) { } // Keep completed samples while a live file is unavailable.
                catch (UnauthorizedAccessException) { }
            }
            double total = 0;
            foreach (long value in samples.Values) total += value;
            return signal.WithFirstTokenTiming(samples.Count, total);
        }

        private void ReadFile(string path, Cursor cursor)
        {
            var info = new FileInfo(path);
            if (!info.Exists || (info.Length == cursor.Offset && info.LastWriteTimeUtc == cursor.WriteUtc)) return;
            DateTime writeUtc = info.LastWriteTimeUtc;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                long end = stream.Length; // Do not chase a continuously growing log.
                cursor.PrefixLength = (int)Math.Min(512, end);
                cursor.PrefixHash = PrefixHash(stream, cursor.PrefixLength);
                stream.Position = cursor.Offset;
                while (stream.Position < end)
                {
                    int read = stream.Read(buffer, 0, (int)Math.Min(buffer.Length, end - stream.Position));
                    if (read == 0) break;
                    BytesReadLastRefresh += read;
                    int start = 0;
                    while (start < read)
                    {
                        int newline = Array.IndexOf(buffer, (byte)'\n', start, read - start);
                        int stop = newline < 0 ? read : newline;
                        Append(cursor, buffer, start, stop - start);
                        if (newline >= 0)
                        {
                            if (!cursor.Skip && cursor.Count > 0)
                                AddSample(Encoding.UTF8.GetString(cursor.Pending, 0, cursor.Count));
                            cursor.Count = 0; cursor.Skip = false;
                            start = newline + 1;
                        }
                        else start = read;
                    }
                    cursor.Offset = stream.Position;
                }
            }
            cursor.WriteUtc = writeUtc;
        }

        private static ulong PrefixHash(Stream stream, int count)
        {
            ulong hash = 14695981039346656037UL;
            for (int i = 0; i < count; i++) hash = unchecked((hash ^ (byte)stream.ReadByte()) * 1099511628211UL);
            return hash;
        }

        private static bool Candidate(byte[] bytes, int count)
        {
            string header = Encoding.UTF8.GetString(bytes, 0, Math.Min(count, 512));
            return header.IndexOf("\"event_msg\"", StringComparison.Ordinal) >= 0 &&
                header.IndexOf("\"task_complete\"", StringComparison.Ordinal) >= 0;
        }

        private static void Append(Cursor cursor, byte[] bytes, int start, int length)
        {
            if (cursor.Skip) return;
            // Classify using the record header before allocating for attachments/tool output.
            int prefix = Math.Min(length, Math.Max(0, 512 - cursor.Count));
            Buffer.BlockCopy(bytes, start, cursor.Pending, cursor.Count, prefix);
            cursor.Count += prefix; start += prefix; length -= prefix;
            if (cursor.Count >= 512 && !Candidate(cursor.Pending, cursor.Count)) { cursor.Skip = true; return; }
            if (cursor.Count + length > MaxRecordBytes) { cursor.Skip = true; return; }
            if (cursor.Count + length > cursor.Pending.Length)
                Array.Resize(ref cursor.Pending, Math.Min(MaxRecordBytes, Math.Max(cursor.Count + length, cursor.Pending.Length * 2)));
            Buffer.BlockCopy(bytes, start, cursor.Pending, cursor.Count, length);
            cursor.Count += length;
        }

        private void AddSample(string line)
        {
            if (line.IndexOf("\"task_complete\"", StringComparison.Ordinal) < 0) return;
            try
            {
                var root = new JavaScriptSerializer().DeserializeObject(line.TrimStart('\uFEFF')) as IDictionary<string, object>;
                if (Text(root, "type") != "event_msg") return;
                object raw;
                var payload = root.TryGetValue("payload", out raw) ? raw as IDictionary<string, object> : null;
                if (Text(payload, "type") != "task_complete") return;
                string id = Text(payload, "turn_id");
                long milliseconds, duration;
                if (String.IsNullOrWhiteSpace(id) || !Int64.TryParse(Text(payload, "time_to_first_token_ms"),
                    NumberStyles.None, CultureInfo.InvariantCulture, out milliseconds)) return;
                if (Int64.TryParse(Text(payload, "duration_ms"), NumberStyles.None, CultureInfo.InvariantCulture,
                    out duration) && milliseconds > duration) return;
                // Replayed/forked rollout segments contain the same turn. Count it once.
                if (!samples.ContainsKey(id)) samples.Add(id, milliseconds);
            }
            catch (ArgumentException) { }
            catch (InvalidOperationException) { }
        }

        private static string Text(IDictionary<string, object> value, string key)
        {
            object result;
            return value != null && value.TryGetValue(key, out result) && result != null
                ? Convert.ToString(result, CultureInfo.InvariantCulture) : String.Empty;
        }
    }
}
