using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.IO.Compression;
using Verviewer.Core;
using Utils;

namespace Verviewer.Archives
{
    [ArchivePlugin(
        id: "Artdink ENDI PACK",
        extensions: new[] { "apk" },
        magics: new[] { "ENDILTLE", "ENDIBIGE" }
    )]
    internal sealed class ArtdinkEndiPackHandler : IArchiveHandler
    {
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
            long offset    = entry.Offset;
            long physSize  = entry.Size;
            long uncomp    = entry.UncompressedSize;

            bool isCompressed = uncomp > 0 && uncomp != physSize;

            if (!isCompressed)
                return new SubReadStream(s, offset, physSize);

            if (physSize <= 2)
                throw new InvalidDataException("Compressed entry too small.");

            long compOffset = offset + 2;
            long compLen    = physSize - 2;

            var seg = new SubReadStream(s, compOffset, compLen);
            return new DeflateStream(seg, CompressionMode.Decompress);
        }

        static void ParseArchive(FileStream fs, List<ArchiveEntry> entries)
        {
            var tmp = new byte[8];

            fs.ReadExactly(tmp, 0, 8);
            string endianStr = Encoding.ASCII.GetString(tmp, 4, 4);
            bool little = string.Equals(endianStr, "LTLE", StringComparison.OrdinalIgnoreCase);

            fs.ReadExactly(tmp, 0, 8);

            var r = new EndianReader(fs, little);

            string h = r.ReadAscii(8);
            if (h != "PACKHEDR")
                throw new InvalidDataException("Missing PACKHEDR");

            long headerSizeMain = r.ReadInt64();
            r.Skip(4 * 4 + 16);

            string t = r.ReadAscii(8);
            if (t != "PACKTOC ")
                throw new InvalidDataException("Missing PACKTOC");

            long tocHeaderSize   = r.ReadInt64();
            long tocHeaderOffset = r.Position;
            int  entrySize       = r.ReadInt32();
            int  filesCount      = r.ReadInt32();
            int  foldersCount    = r.ReadInt32();
            r.ReadInt32(); // unused

            var tocNames = ReadGenestrt(r, tocHeaderOffset, tocHeaderSize);

            for (int i = 0; i < foldersCount; i++)
                r.Skip(entrySize);

            int fileRecordCount = filesCount - foldersCount;
            for (int i = 0; i < fileRecordCount; i++)
            {
                r.Skip(4);
                int  nameIndex = r.ReadInt32();
                r.Skip(8);
                long offset = r.ReadInt64();
                long size   = r.ReadInt64();
                long zsize  = r.ReadInt64();

                if (size == 0)
                    continue;

                string name = GetName(tocNames, nameIndex);
                if (string.IsNullOrEmpty(name))
                    continue;

                AddEntry(entries, name, offset, size, zsize);
            }

            r.Position = tocHeaderOffset + tocHeaderSize;
            if (r.Position >= fs.Length)
                return;

            string fsls = r.ReadAscii(8);
            if (fsls != "PACKFSLS")
                throw new InvalidDataException("Missing PACKFSLS");

            long fslsHeaderSize   = r.ReadInt64();
            long fslsHeaderOffset = r.Position;
            int  archivesCount    = r.ReadInt32();
            r.Skip(4);
            r.Skip(4);
            r.ReadInt32();

            var archiveNames = ReadGenestrt(r, fslsHeaderOffset, fslsHeaderSize);

            for (int i = 0; i < archivesCount; i++)
            {
                int  nameIndex     = r.ReadInt32();
                r.ReadInt32();
                long archiveOffset = r.ReadInt64();
                long archiveSize   = r.ReadInt64();
                r.Skip(16);

                string archiveName = GetName(archiveNames, nameIndex);
                if (string.IsNullOrEmpty(archiveName))
                    archiveName = $"archive_{i:D4}";

                long ret = r.Position;
                ParseSubArchive(r, archiveName, archiveOffset, entries);
                r.Position = ret;
            }
        }

        static void ParseSubArchive(EndianReader parent, string archiveName, long archiveOffset, List<ArchiveEntry> entries)
        {
            var s = parent.BaseStream;
            parent.Position = archiveOffset;

            s.Seek(4 + 4 + 8, SeekOrigin.Current);

            var r = new EndianReader(s, parent.LittleEndian);

            r.Skip(8);
            long headerSize   = r.ReadInt64();
            long headerOffset = r.Position;
            r.ReadInt32();
            int entrySize   = r.ReadInt32();
            int filesCount  = r.ReadInt32();
            int entriesSize = r.ReadInt32();
            r.Skip(16);

            var fileNames = ReadGenestrt(r, headerOffset, headerSize);

            for (int i = 0; i < filesCount; i++)
            {
                int  nameIndex = r.ReadInt32();
                int  zip       = r.ReadInt32();
                long offset    = r.ReadInt64();
                long size      = r.ReadInt64();
                long zsize     = r.ReadInt64();

                if (size == 0)
                    continue;

                string name = GetName(fileNames, nameIndex);
                if (string.IsNullOrEmpty(name))
                    name = $"file_{i:D5}";

                long absoluteOffset = archiveOffset + offset;
                AddEntry(entries, archiveName + "/" + name, absoluteOffset, size, zsize);
            }
        }

        static void AddEntry(List<ArchiveEntry> entries, string path, long offset, long size, long zsize)
        {
            int physSize   = ToIntSize(zsize != 0 ? zsize : size);
            int uncompSize = ToIntSize(size);

            entries.Add(new ArchiveEntry
            {
                Path             = NormalizePath(path),
                Offset           = offset,
                Size             = physSize,
                UncompressedSize = uncompSize,
                IsDirectory      = false
            });
        }

        static List<string> ReadGenestrt(EndianReader r, long headerOffset, long headerSize)
        {
            var s   = r.BaseStream;
            long ret   = r.Position;
            long start = headerOffset + headerSize;

            long pos = FindSignature(s, start, GenestrtSignature);
            r.Position = pos;

            string magic = r.ReadAscii(8);
            if (magic != "GENESTRT")
                throw new InvalidDataException("Invalid GENESTRT");

            long dummy = r.ReadInt64();
            int  count = r.ReadInt32();
            r.Skip(0x0C);

            var offs = new int[count];
            for (int i = 0; i < count; i++)
                offs[i] = r.ReadInt32();

            s.AlignPosition(0x10);
            long namesBase = s.Position;

            var list = new List<string>(count);
            for (int i = 0; i < count; i++)
            {
                s.Position = namesBase + offs[i];
                list.Add(ReadCString(s));
            }

            r.Position = ret;
            return list;
        }

        static long FindSignature(Stream s, long start, byte[] sig)
        {
            const int bufSize = 65536;
            var buf = new byte[bufSize + 8];

            s.Position = start;
            long abs    = start;
            int  filled = 0;

            while (true)
            {
                int read = s.Read(buf, filled, bufSize);
                if (read == 0)
                    break;

                filled += read;
                int limit = filled - sig.Length + 1;

                for (int i = 0; i < limit; i++)
                {
                    bool match = true;
                    for (int j = 0; j < sig.Length; j++)
                    {
                        if (buf[i + j] != sig[j])
                        {
                            match = false;
                            break;
                        }
                    }

                    if (match)
                    {
                        long pos = abs + i;
                        s.Position = pos;
                        return pos;
                    }
                }

                if (filled > sig.Length - 1)
                {
                    int keep = sig.Length - 1;
                    Array.Copy(buf, filled - keep, buf, 0, keep);
                    abs    += filled - keep;
                    filled  = keep;
                }
            }

            throw new InvalidDataException("GENESTRT not found");
        }

        static string ReadCString(Stream s)
        {
            var bytes = new List<byte>(32);
            while (true)
            {
                int b = s.ReadByte();
                if (b < 0)
                    throw new EndOfStreamException();
                if (b == 0)
                    break;
                bytes.Add((byte)b);
            }
            return Encoding.ASCII.GetString(bytes.ToArray());
        }

        static string GetName(IList<string> list, int index)
        {
            if (index < 0 || index >= list.Count)
                return string.Empty;
            return list[index];
        }

        static string NormalizePath(string path)
        {
            if (string.IsNullOrEmpty(path))
                return string.Empty;
            return path.Replace('\\', '/');
        }

        static int ToIntSize(long value)
        {
            if (value < 0 || value > int.MaxValue)
                throw new InvalidDataException("Size too large for int");
            return (int)value;
        }

        sealed class EndianReader
        {
            readonly Stream _s;
            readonly bool   _little;
            readonly byte[] _buf = new byte[8];

            public EndianReader(Stream s, bool little)
            {
                _s      = s;
                _little = little;
            }

            public Stream BaseStream => _s;
            public bool   LittleEndian => _little;

            public long Position
            {
                get => _s.Position;
                set => _s.Position = value;
            }

            public void Skip(int count)
            {
                if (count != 0)
                    _s.Seek(count, SeekOrigin.Current);
            }

            public int ReadInt32()
            {
                Read(4);
                if (_little)
                {
                    return _buf[0]
                         | (_buf[1] << 8)
                         | (_buf[2] << 16)
                         | (_buf[3] << 24);
                }
                else
                {
                    return (_buf[0] << 24)
                         | (_buf[1] << 16)
                         | (_buf[2] << 8)
                         | _buf[3];
                }
            }

            public long ReadInt64()
            {
                Read(8);
                if (_little)
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

            void Read(int count)
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