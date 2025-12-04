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
        id: "Ikusabune IPF",
        extensions: new[] { "pak" },
        magics: new[] { "IPF " }
    )]
    internal sealed class IkusabuneIpfArchiveHandler : IArchiveHandler
    {
        private static readonly Lazy<IpfNameIndex> NameIndexLazy = new(() => IpfNameIndex.Load());
        private static IpfNameIndex NameIndex => NameIndexLazy.Value;

        private static readonly Encoding Cp932 = CreateCp932();
        private static Encoding CreateCp932()
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(932);
        }

        public OpenedArchive Open(string archivePath)
        {
            var fs = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.RandomAccess);
            var br = new BinaryReader(fs, Encoding.ASCII, true);
            try
            {
                fs.Position = 4;
                uint rawCount = br.ReadUInt32();
                bool multiPart = IsMultiPart(archivePath);
                uint entryCount = multiPart ? rawCount : (rawCount ^ 0x49u);
                if (entryCount > 1_000_000)
                    throw new InvalidDataException($"Unreasonable IPF entry count: {entryCount}");

                var entries = multiPart
                    ? ReadMultiPartEntries(br, entryCount)
                    : ReadSinglePakEntries(fs, br, entryCount);

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

        static bool IsMultiPart(string pakPath)
        {
            string dir = Path.GetDirectoryName(pakPath) ?? "";
            string baseName = Path.GetFileNameWithoutExtension(pakPath);
            string p00 = Path.Combine(dir, $"{baseName}.p00");
            return File.Exists(p00);
        }

        static List<ArchiveEntry> ReadMultiPartEntries(BinaryReader br, uint entryCount)
        {
            int count = (int)entryCount;
            var entries = new List<ArchiveEntry>(count);

            for (int i = 0; i < count; i++)
            {
                int hash = br.ReadInt32();
                int offset = br.ReadInt32();
                int size = br.ReadInt32();
                uint hashU = unchecked((uint)hash);

                string path = NameIndex.TryGet(hashU, out var p)
                    ? p
                    : $"${hashU:X8}";

                entries.Add(new ArchiveEntry
                {
                    Path = path.Replace('\\', '/'),
                    IsDirectory = false,
                    Offset = offset,
                    Size = size
                });
            }

            return entries;
        }

        static List<ArchiveEntry> ReadSinglePakEntries(FileStream fs, BinaryReader br, uint entryCount)
        {
            int count = (int)entryCount;
            var headers = new (int Hash, int Offset, int NameOffset, int Size)[count];

            for (int i = 0; i < count; i++)
            {
                headers[i].Hash = br.ReadInt32();
                headers[i].Offset = br.ReadInt32();
                headers[i].NameOffset = br.ReadInt32();
                headers[i].Size = br.ReadInt32();
            }

            long fileLen = fs.Length;
            var entries = new List<ArchiveEntry>(count);

            for (int i = 0; i < count; i++)
            {
                var h = headers[i];

                string path = string.Empty;
                if ((uint)h.NameOffset < fileLen)
                {
                    fs.Position = h.NameOffset;
                    path = fs.ReadNullTerminatedString(Cp932, 64).Trim();
                }

                if (string.IsNullOrWhiteSpace(path))
                {
                    uint hashU = unchecked((uint)h.Hash);
                    path = NameIndex.TryGet(hashU, out var p)
                        ? p
                        : $"${hashU:X8}";
                }

                entries.Add(new ArchiveEntry
                {
                    Path = path.Replace('\\', '/'),
                    IsDirectory = false,
                    Offset = h.Offset,
                    Size = h.Size
                });
            }

            return entries;
        }

        public Stream OpenEntryStream(OpenedArchive archive, ArchiveEntry entry)
        {
            if (entry.IsDirectory)
                throw new InvalidOperationException("Directory entries have no data stream.");

            if (entry.Size <= 0)
                return Stream.Null;

            string pakPath = archive.SourcePath;
            string dir = Path.GetDirectoryName(pakPath) ?? "";
            string baseName = Path.GetFileNameWithoutExtension(pakPath);
            bool multiPart = IsMultiPart(pakPath);

            string path;
            long innerOffset;
            long size = (long)(uint)entry.Size;

            if (multiPart)
            {
                uint encoded = unchecked((uint)entry.Offset);
                int partIndex = (int)(encoded >> 28);
                innerOffset = encoded & 0x0FFFFFFF;
                path = Path.Combine(dir, $"{baseName}.p{partIndex:00}");
            }
            else
            {
                innerOffset = (long)(uint)entry.Offset;
                path = pakPath;
            }

            var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.RandomAccess);

            if (innerOffset < 0 || innerOffset + size > fs.Length)
            {
                fs.Dispose();
                throw new InvalidDataException("IPF entry offset/size out of range.");
            }

            if (size <= 10)
                return new RangeStream(fs, innerOffset, size, leaveOpen: false);

            var header = new byte[10];
            fs.Position = innerOffset;
            int read = 0;
            while (read < 10)
            {
                int r = fs.Read(header, read, 10 - read);
                if (r <= 0) break;
                read += r;
            }

            if (read < 2 || header[0] != (byte)'Z' || header[1] != (byte)'0')
                return new RangeStream(fs, innerOffset, size, leaveOpen: false);

            long compOffset = innerOffset + 10;
            long compLen = size - 10;
            if (compLen < 0) compLen = 0;

            var seg = new RangeStream(fs, compOffset, compLen, leaveOpen: false);
            return new DeflateStream(seg, CompressionMode.Decompress);
        }

        sealed class IpfNameIndex
        {
            readonly byte[] data;
            readonly int count;
            readonly int entriesOffset;
            readonly int namesOffset;
            readonly Dictionary<uint, string> cache = new Dictionary<uint, string>();

            IpfNameIndex(byte[] data)
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

            public static IpfNameIndex Load()
            {
                try
                {
                    var asm = typeof(IkusabuneIpfArchiveHandler).Assembly;
                    const string resourceName = "Verviewer.Misc.IPF.IPF.zlib";
                    using var raw = asm.GetManifestResourceStream(resourceName);
                    if (raw == null) return new IpfNameIndex(Array.Empty<byte>());

                    using var ms = new MemoryStream();
                    using (var def = new DeflateStream(raw, CompressionMode.Decompress, leaveOpen: true))
                    {
                        def.CopyTo(ms);
                    }

                    return new IpfNameIndex(ms.ToArray());
                }
                catch
                {
                    return new IpfNameIndex(Array.Empty<byte>());
                }
            }

            public bool TryGet(uint hash, out string name)
            {
                if (count == 0)
                {
                    name = string.Empty;
                    return false;
                }

                if (cache.TryGetValue(hash, out name)) return true;

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