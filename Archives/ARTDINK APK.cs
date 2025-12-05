// 参考
//https://aluigi.altervista.org/bms/dragon_ball_z_boz.bms
//https://github.com/akio7624/ApkIdxTemplate

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using Verviewer.Core;
using Utils;

namespace Verviewer.Archives
{
    [ArchivePlugin(
        id: "Artdink APK",
        extensions: new[] { "apk" },
        magics: new[] { "ENDILTLE", "ENDIBIGE" }
    )]
    internal sealed class ArtdinkEndiPackHandler : IArchiveHandler
    {
        static readonly byte[] PackTocSignature = Encoding.ASCII.GetBytes("PACKTOC ");
        static readonly byte[] GenestrtSignature = Encoding.ASCII.GetBytes("GENESTRT");

        public OpenedArchive Open(string archivePath)
        {
            var fs = new FileStream(
                archivePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                65536,
                FileOptions.RandomAccess);

            var entries = new List<ArchiveEntry>();

            try
            {
                ParseArchive(fs, entries);
                return new OpenedArchive(archivePath, fs, entries, this);
            }
            catch
            {
                fs.Dispose();
                throw;
            }
        }

        public Stream OpenEntryStream(OpenedArchive arc, ArchiveEntry entry)
        {
            if (entry.IsDirectory)
                throw new InvalidOperationException("Directory has no data stream.");

            if (entry.Size <= 0)
                return Stream.Null;

            var s = arc.Stream;
            long offset = entry.Offset;
            long physSize = entry.Size;
            long uncomp = entry.UncompressedSize;

            bool isCompressed = uncomp > 0 && uncomp != physSize;

            if (!isCompressed)
                return new SubReadStream(s, offset, physSize);

            if (physSize <= 2)
                throw new InvalidDataException("Compressed entry too small.");

            long compOffset = offset + 2;
            long compLen = physSize - 2;

            var seg = new SubReadStream(s, compOffset, compLen);
            return new DeflateStream(seg, CompressionMode.Decompress);
        }

        static void ParseArchive(FileStream fs, List<ArchiveEntry> entries)
        {
            var r = new EndianReader(fs);

            string magic = r.ReadAscii(8);
            if (magic != "ENDILTLE" && magic != "ENDIBIGE")
                throw new InvalidDataException($"Invalid magic: {magic}");

            r.LittleEndian = magic == "ENDILTLE";

            long tocPos = FindPattern(fs, PackTocSignature);
            long strPos = FindPattern(fs, GenestrtSignature);

            if (tocPos < 0 || strPos < 0)
                throw new InvalidDataException("Required blocks not found");

            var names = ParseGenestrt(r, strPos);
            var tocEntries = ParsePacktoc(r, tocPos, names);

            BuildFileTree(tocEntries, entries);
        }

        static List<string> ParseGenestrt(EndianReader r, long strPos)
        {
            r.Position = strPos + 16;
            int count = r.ReadInt32();

            r.Position = strPos + 24;
            int namesOffset = r.ReadInt32();

            long offsetsBase = strPos + 32;
            long namesBase = strPos + 16 + namesOffset;

            var list = new List<string>(count);

            for (int i = 0; i < count; i++)
            {
                r.Position = offsetsBase + i * 4;
                int relOff = r.ReadInt32();
                r.Position = namesBase + relOff;
                list.Add(r.ReadCString());
            }

            return list;
        }

        static List<TocEntry> ParsePacktoc(EndianReader r, long tocPos, List<string> names)
        {
            r.Position = tocPos + 16;
            int segSize = r.ReadInt32();
            int segCount = r.ReadInt32();
            long basePos = tocPos + 32;

            var list = new List<TocEntry>(segCount);

            for (int i = 0; i < segCount; i++)
            {
                long pos = basePos + (long)i * segSize;

                r.Position = pos;
                int type = r.ReadInt32();
                int nameIndex = r.ReadInt32();

                string name = nameIndex >= 0 && nameIndex < names.Count
                    ? names[nameIndex]
                    : string.Empty;

                var e = new TocEntry
                {
                    Index = i,
                    Type = type,
                    Name = name,
                    IsFolder = type == 1
                };

                r.Position = pos + 16;

                if (e.IsFolder)
                {
                    e.ChildStart = r.ReadInt32();
                    e.ChildCount = r.ReadInt32();
                }
                else
                {
                    e.Offset = r.ReadInt64();
                }

                r.Position = pos + 24;
                e.Size = r.ReadInt64();
                e.ZSize = r.ReadInt64();

                list.Add(e);
            }

            return list;
        }

        static void BuildFileTree(List<TocEntry> tocEntries, List<ArchiveEntry> outEntries)
        {
            if (tocEntries.Count == 0)
                return;

            var stack = new Stack<(int Index, string ParentPath)>();
            stack.Push((0, string.Empty));

            while (stack.Count > 0)
            {
                var (idx, parentPath) = stack.Pop();
                if (idx < 0 || idx >= tocEntries.Count)
                    continue;

                var node = tocEntries[idx];

                string currentPath;
                if (idx == 0)
                {
                    currentPath = string.Empty;
                }
                else
                {
                    currentPath = string.IsNullOrEmpty(parentPath)
                        ? node.Name
                        : parentPath + "/" + node.Name;
                }

                if (node.IsFolder)
                {
                    for (int i = node.ChildCount - 1; i >= 0; i--)
                    {
                        int child = node.ChildStart + i;
                        stack.Push((child, currentPath));
                    }
                }
                else
                {
                    if (node.Size <= 0 || string.IsNullOrEmpty(currentPath))
                        continue;

                    long physSize = node.ZSize > 0 ? node.ZSize : node.Size;
                    long uncompSize = node.Size;

                    outEntries.Add(new ArchiveEntry
                    {
                        Path = currentPath.Replace('\\', '/'),
                        Offset = node.Offset,
                        Size = ToIntSize(physSize),
                        UncompressedSize = ToIntSize(uncompSize),
                        IsDirectory = false
                    });
                }
            }
        }

        static long FindPattern(Stream s, byte[] pattern)
        {
            long saved = s.Position;
            s.Position = 0;

            int bufSize = 65536;
            var buf = new byte[bufSize + pattern.Length - 1];
            long abs = 0;
            int overlap = 0;

            try
            {
                while (true)
                {
                    int read = s.Read(buf, overlap, bufSize);
                    if (read == 0)
                        break;

                    int total = overlap + read;
                    int limit = total - pattern.Length + 1;

                    for (int i = 0; i < limit; i++)
                    {
                        bool match = true;
                        for (int j = 0; j < pattern.Length; j++)
                        {
                            if (buf[i + j] != pattern[j])
                            {
                                match = false;
                                break;
                            }
                        }

                        if (match)
                        {
                            long pos = abs + i;
                            s.Position = saved;
                            return pos;
                        }
                    }

                    overlap = pattern.Length - 1;
                    Array.Copy(buf, total - overlap, buf, 0, overlap);
                    abs += total - overlap;
                }
            }
            finally
            {
                s.Position = saved;
            }

            return -1;
        }

        static int ToIntSize(long value)
        {
            if (value <= 0)
                return 0;
            if (value > int.MaxValue)
                return int.MaxValue;
            return (int)value;
        }

        sealed class TocEntry
        {
            public int Index;
            public int Type;
            public string Name = "";
            public bool IsFolder;
            public int ChildStart;
            public int ChildCount;
            public long Offset;
            public long Size;
            public long ZSize;
        }

        sealed class EndianReader
        {
            readonly Stream _s;
            readonly byte[] _buf = new byte[8];

            public EndianReader(Stream s)
            {
                _s = s;
                LittleEndian = true;
            }

            public bool LittleEndian { get; set; }

            public long Position
            {
                get => _s.Position;
                set => _s.Position = value;
            }

            public int ReadInt32()
            {
                ReadExact(4);
                if (LittleEndian)
                    return _buf[0] | (_buf[1] << 8) | (_buf[2] << 16) | (_buf[3] << 24);
                return (_buf[0] << 24) | (_buf[1] << 16) | (_buf[2] << 8) | _buf[3];
            }

            public long ReadInt64()
            {
                ReadExact(8);
                if (LittleEndian)
                {
                    ulong v =
                        (ulong)_buf[0]
                        | ((ulong)_buf[1] << 8)
                        | ((ulong)_buf[2] << 16)
                        | ((ulong)_buf[3] << 24)
                        | ((ulong)_buf[4] << 32)
                        | ((ulong)_buf[5] << 40)
                        | ((ulong)_buf[6] << 48)
                        | ((ulong)_buf[7] << 56);
                    return (long)v;
                }
                else
                {
                    ulong v =
                        (ulong)_buf[7]
                        | ((ulong)_buf[6] << 8)
                        | ((ulong)_buf[5] << 16)
                        | ((ulong)_buf[4] << 24)
                        | ((ulong)_buf[3] << 32)
                        | ((ulong)_buf[2] << 40)
                        | ((ulong)_buf[1] << 48)
                        | ((ulong)_buf[0] << 56);
                    return (long)v;
                }
            }

            public string ReadAscii(int count)
            {
                var buf = new byte[count];
                _s.ReadExactly(buf, 0, count);
                return Encoding.ASCII.GetString(buf, 0, count);
            }

            public string ReadCString()
            {
                var bytes = new List<byte>(32);
                while (true)
                {
                    int b = _s.ReadByte();
                    if (b < 0)
                        throw new EndOfStreamException();
                    if (b == 0)
                        break;
                    bytes.Add((byte)b);
                }
                return Encoding.UTF8.GetString(bytes.ToArray());
            }

            void ReadExact(int count)
            {
                int off = 0;
                while (off < count)
                {
                    int read = _s.Read(_buf, off, count - off);
                    if (read == 0)
                        throw new EndOfStreamException();
                    off += read;
                }
            }
        }
    }
}