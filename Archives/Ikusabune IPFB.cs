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
        id: "Ikusabune IPFB",
        extensions: new[] { "pak" },
        magics: null
    )]
    internal sealed class IkusabuneIpfbArchiveHandler : IArchiveHandler
    {
        static readonly Lazy<IpfbNameIndex> NameIndexLazy = new(() => IpfbNameIndex.Load());
        static IpfbNameIndex NameIndex => NameIndexLazy.Value;

        public OpenedArchive Open(string archivePath)
        {
            var fs = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.RandomAccess);
            var br = new BinaryReader(fs, Encoding.ASCII, true);
            try
            {
                fs.Position = 4;
                uint idxCount = ReadUInt32BE(br);

                fs.Position = 0x10;
                var entries = new List<ArchiveEntry>((int)idxCount);

                for (int i = 0; i < idxCount; i++)
                {
                    int hash = ReadInt32BE(br);
                    int offset = ReadInt32BE(br);
                    int size = ReadInt32BE(br);

                    if (hash == 0)
                        break;

                    uint h = unchecked((uint)hash);
                    string name = NameIndex.TryGet(h, out var p) ? p : "$" + h.ToString("X");

                    entries.Add(new ArchiveEntry
                    {
                        Path = name.Replace('\\', '/'),
                        IsDirectory = false,
                        Offset = offset,
                        Size = size
                    });
                }

                return new OpenedArchive(archivePath, fs, entries, this);
            }
            catch
            {
                br.Dispose();
                fs.Dispose();
                throw;
            }
            finally
            {
                br.Dispose();
            }
        }

        public Stream OpenEntryStream(OpenedArchive archive, ArchiveEntry entry)
        {
            if (entry.IsDirectory)
                throw new InvalidOperationException();

            if (entry.Size <= 0)
                return Stream.Null;

            string pakPath = archive.SourcePath;
            string dir = Path.GetDirectoryName(pakPath) ?? "";
            string baseName = Path.GetFileNameWithoutExtension(pakPath);

            uint encoded = unchecked((uint)entry.Offset);
            int partIndex = (int)(encoded >> 28);
            long innerOffset = encoded & 0x0FFFFFFF;
            long size = (long)(uint)entry.Size;

            string partPath = Path.Combine(dir, $"{baseName}.p{partIndex:00}");
            var fs = new FileStream(partPath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.RandomAccess);

            if (innerOffset < 0 || innerOffset + size > fs.Length)
            {
                fs.Dispose();
                throw new InvalidDataException();
            }

            if (size <= 12)
                return new RangeStream(fs, innerOffset, size, leaveOpen: false);

            byte[] header = new byte[12];
            fs.Position = innerOffset;
            int read = 0;
            while (read < 12)
            {
                int r = fs.Read(header, read, 12 - read);
                if (r <= 0) break;
                read += r;
            }

            if (read < 2 || header[0] != (byte)'Z' || header[1] != (byte)'1')
                return new RangeStream(fs, innerOffset, size, leaveOpen: false);

            long compOffset = innerOffset + 12;
            long compLen = size - 12;
            if (compLen < 0) compLen = 0;

            var seg = new RangeStream(fs, compOffset, compLen, leaveOpen: false);
            return new DeflateStream(seg, CompressionMode.Decompress);
        }

        static int ReadInt32BE(BinaryReader br)
        {
            Span<byte> b = stackalloc byte[4];
            int r = br.Read(b);
            if (r != 4) throw new EndOfStreamException();
            return (b[0] << 24) | (b[1] << 16) | (b[2] << 8) | b[3];
        }

        static uint ReadUInt32BE(BinaryReader br)
        {
            Span<byte> b = stackalloc byte[4];
            int r = br.Read(b);
            if (r != 4) throw new EndOfStreamException();
            return (uint)((b[0] << 24) | (b[1] << 16) | (b[2] << 8) | b[3]);
        }

        sealed class IpfbNameIndex
        {
            readonly byte[] data;
            readonly int count;
            readonly int entriesOffset;
            readonly int namesOffset;
            readonly Dictionary<uint, string> cache = new();

            IpfbNameIndex(byte[] data)
            {
                this.data = data;
                if (data.Length < 4) return;

                int c = BitConverter.ToInt32(data, 0);
                if (c <= 0) return;

                long entriesBytes = 4L + (long)c * 8L;
                if (entriesBytes > data.Length) return;

                count = c;
                entriesOffset = 4;
                namesOffset = entriesOffset + count * 8;
            }

            public static IpfbNameIndex Load()
            {
                try
                {
                    var asm = typeof(IkusabuneIpfbArchiveHandler).Assembly;
                    const string resourceName = "Verviewer.Misc.IPF.IPFB.zlib";
                    using var raw = asm.GetManifestResourceStream(resourceName);
                    if (raw == null) return new IpfbNameIndex(Array.Empty<byte>());

                    using var ms = new MemoryStream();
                    using (var def = new DeflateStream(raw, CompressionMode.Decompress, leaveOpen: true))
                    {
                        def.CopyTo(ms);
                    }

                    return new IpfbNameIndex(ms.ToArray());
                }
                catch
                {
                    return new IpfbNameIndex(Array.Empty<byte>());
                }
            }

            public bool TryGet(uint hash, out string name)
            {
                if (count == 0)
                {
                    name = string.Empty;
                    return false;
                }

                if (cache.TryGetValue(hash, out name))
                    return true;

                int lo = 0;
                int hi = count - 1;

                while (lo <= hi)
                {
                    int mid = (lo + hi) >> 1;
                    int entryOff = entriesOffset + mid * 8;
                    uint h = BitConverter.ToUInt32(data, entryOff);

                    if (hash == h)
                    {
                        uint rel = BitConverter.ToUInt32(data, entryOff + 4);
                        int pos = namesOffset + (int)rel;
                        if (pos < namesOffset || pos >= data.Length)
                        {
                            name = string.Empty;
                            return false;
                        }

                        int end = pos;
                        while (end < data.Length && data[end] != 0) end++;
                        if (end <= pos)
                        {
                            name = string.Empty;
                            return false;
                        }

                        name = Encoding.UTF8.GetString(data, pos, end - pos);
                        cache[hash] = name;
                        return true;
                    }

                    if (hash < h) hi = mid - 1;
                    else lo = mid + 1;
                }

                name = string.Empty;
                return false;
            }
        }
    }
}