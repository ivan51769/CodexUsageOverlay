using System;
using System.IO;
using System.Text;

namespace CodexUsageOverlay
{
    // Incremental, bounded metadata reader for the hot status/context polling paths.
    internal sealed class CodexRolloutTailReader
    {
        private const int TailBytes = 2 * 1024 * 1024;
        private const int MaxLineBytes = 32768;
        private readonly string path;
        private long offset = -1;
        private DateTime writeUtc;
        private ulong prefixHash;
        private int prefixLength;
        private byte[] pending = new byte[512];
        private int count;
        private byte[] suffix;
        private int suffixCount;
        private bool skip;
        internal long BytesReadLastRefresh { get; private set; }

        internal CodexRolloutTailReader(string path) { this.path = path; }

        internal void Read(Action reset, Action<string> record)
        {
            BytesReadLastRefresh = 0;
            var file = new FileInfo(path);
            if (!file.Exists) return;
            if (file.Length == offset && file.LastWriteTimeUtc == writeUtc) return;
            DateTime observedWrite = file.LastWriteTimeUtc;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                long end = stream.Length;
                bool restart = offset < 0 || end < offset || (end == offset && observedWrite != writeUtc);
                if (!restart && prefixLength > 0) restart = HashPrefix(stream, prefixLength) != prefixHash;
                stream.Position = 0;
                prefixLength = (int)Math.Min(end, 512);
                prefixHash = HashPrefix(stream, prefixLength);
                if (restart)
                {
                    reset(); count = 0; suffixCount = 0;
                    offset = Math.Max(0, end - TailBytes);
                    skip = offset > 0;
                }
                stream.Position = offset;
                byte[] buffer = new byte[8192];
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
                        if (!skip)
                        {
                            int take = Math.Min(stop - start, MaxLineBytes - count);
                            if (count + take > pending.Length)
                                Array.Resize(ref pending, Math.Min(MaxLineBytes, Math.Max(count + take, pending.Length * 2)));
                            Buffer.BlockCopy(buffer, start, pending, count, take);
                            count += take;
                            // Legacy message records put phase/channel after a potentially
                            // large body. Keep a small suffix so completion is still recognized.
                            int remaining = stop - start - take;
                            if (remaining > 0)
                            {
                                if (suffix == null) suffix = new byte[512];
                                int tail = Math.Min(remaining, suffix.Length);
                                int keep = Math.Min(suffixCount, suffix.Length - tail);
                                Buffer.BlockCopy(suffix, suffixCount - keep, suffix, 0, keep);
                                Buffer.BlockCopy(buffer, stop - tail, suffix, keep, tail);
                                suffixCount = keep + tail;
                            }
                        }
                        if (newline >= 0)
                        {
                            if (!skip && count > 0) record(CurrentRecord());
                            count = 0; suffixCount = 0; skip = false;
                            start = newline + 1;
                        }
                        else start = read;
                    }
                    offset = stream.Position;
                }
                // token_count parsers reject incomplete JSON. Keep its prefix for the next append.
                if (!skip && count > 0) record(CurrentRecord());
            }
            writeUtc = observedWrite;
        }

        private string CurrentRecord()
        {
            string head = Encoding.UTF8.GetString(pending, 0, count).TrimStart('\uFEFF');
            return suffixCount == 0 ? head : head + Encoding.UTF8.GetString(suffix, 0, suffixCount);
        }

        private static ulong HashPrefix(Stream stream, int length)
        {
            ulong hash = 14695981039346656037UL;
            for (int i = 0; i < length; i++) hash = unchecked((hash ^ (byte)stream.ReadByte()) * 1099511628211UL);
            return hash;
        }
    }
}
