using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using Verviewer.Core;

namespace Verviewer.Archives
{
    [ArchivePlugin(
        id: "Ikusabune IPF",
        extensions: new[] { "pak" },
        magics: new[] { "IPF " }
    )]
    internal sealed class IkusabuneIpfArchiveHandler : IArchiveHandler
    {
        private static readonly Lazy<Dictionary<uint, string>> NameByHashLazy =
            new(() => LoadNameMap());
        private static Dictionary<uint, string> NameByHash => NameByHashLazy.Value;

        private static readonly Encoding Cp932 = CreateCp932();
        private static Encoding CreateCp932()
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(932);
        }

        // index & names
        public OpenedArchive Open(string archivePath)
        {
            var fs = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var br = new BinaryReader(fs);

            fs.Position = 4;

            uint rawCount = br.ReadUInt32();
            bool multiPart = IsMultiPart(archivePath);

            // 多分卷直接用原始次数；单文件需要异或 0x49
            uint entryCount = multiPart ? rawCount : (rawCount ^ 0x49u);

            if (entryCount > 1_000_000)
            {
                br.Dispose();
                fs.Dispose();
                throw new InvalidDataException($"Unreasonable IPF entry count: {entryCount}");
            }

            var entries = multiPart
                ? ReadMultiPartEntries(br, entryCount)
                : ReadSinglePakEntries(fs, br, entryCount);

            br.Dispose();
            return new OpenedArchive(archivePath, fs, entries, this);
        }

        private static bool IsMultiPart(string pakPath)
        {
            string dir      = Path.GetDirectoryName(pakPath) ?? "";
            string baseName = Path.GetFileNameWithoutExtension(pakPath);
            string p00      = Path.Combine(dir, $"{baseName}.p00");
            return File.Exists(p00);
        }

        private static List<ArchiveEntry> ReadMultiPartEntries(BinaryReader br, uint entryCount)
        {
            int count = (int)entryCount;
            var entries = new List<ArchiveEntry>(count);

            for (int i = 0; i < count; i++)
            {
                int  hash   = br.ReadInt32();
                int  offset = br.ReadInt32();
                int  size   = br.ReadInt32();
                uint hashU  = unchecked((uint)hash);

                string path = NameByHash.TryGetValue(hashU, out var p)
                    ? p
                    : $"${hashU:X8}";

                entries.Add(new ArchiveEntry
                {
                    Path        = path.Replace('\\', '/'),
                    IsDirectory = false,
                    Offset      = offset,
                    Size        = size
                });
            }

            return entries;
        }

        private static List<ArchiveEntry> ReadSinglePakEntries(FileStream fs, BinaryReader br, uint entryCount)
        {
            int count = (int)entryCount;
            var headers = new (int Hash, int Offset, int NameOffset, int Size)[count];

            for (int i = 0; i < count; i++)
            {
                headers[i].Hash       = br.ReadInt32();
                headers[i].Offset     = br.ReadInt32();
                headers[i].NameOffset = br.ReadInt32();
                headers[i].Size       = br.ReadInt32();
            }

            long fileLen = fs.Length;
            var entries  = new List<ArchiveEntry>(count);

            for (int i = 0; i < count; i++)
            {
                var h = headers[i];

                string path = string.Empty;
                if ((uint)h.NameOffset < fileLen)
                {
                    fs.Position = h.NameOffset;
                    path = ReadNullTerminatedString(fs, 64);
                }

                // 单文件有自己带的名字，不用哈希表；名字为空就直接用 hash 占位
                if (string.IsNullOrWhiteSpace(path))
                {
                    uint hashU = unchecked((uint)h.Hash);
                    path = $"${hashU:X8}";
                }

                entries.Add(new ArchiveEntry
                {
                    Path        = path.Replace('\\', '/'),
                    IsDirectory = false,
                    Offset      = h.Offset,
                    Size        = h.Size
                });
            }

            return entries;
        }

        private static string ReadNullTerminatedString(Stream stream, int maxBytes)
        {
            var buffer = new byte[maxBytes];
            int count  = 0;

            for (; count < maxBytes; count++)
            {
                int b = stream.ReadByte();
                if (b <= 0)
                    break;
                buffer[count] = (byte)b;
            }

            return count == 0
                ? string.Empty
                : Cp932.GetString(buffer, 0, count).Trim();
        }

        public Stream OpenEntryStream(OpenedArchive archive, ArchiveEntry entry)
        {
            if (entry.IsDirectory)
                throw new InvalidOperationException("Directory entries have no data stream.");

            if (entry.Size <= 0)
                return Stream.Null;

            string pakPath  = archive.SourcePath;
            string dir      = Path.GetDirectoryName(pakPath) ?? "";
            string baseName = Path.GetFileNameWithoutExtension(pakPath);
            bool   multiPart = IsMultiPart(pakPath);

            byte[] raw = new byte[entry.Size];

            if (multiPart)
            {
                uint encoded     = unchecked((uint)entry.Offset);
                int  partIndex   = (int)(encoded >> 28);
                long innerOffset = encoded & 0x0FFFFFFF;
                long size        = (long)(uint)entry.Size;

                string partPath = Path.Combine(dir, $"{baseName}.p{partIndex:00}");
                using var partFs = new FileStream(partPath, FileMode.Open, FileAccess.Read, FileShare.Read);

                if (innerOffset < 0 || innerOffset + size > partFs.Length)
                    throw new InvalidDataException("IPF entry offset/size out of range in part file.");

                partFs.Position = innerOffset;
                int read = partFs.Read(raw, 0, raw.Length);
                if (read < raw.Length)
                    Array.Resize(ref raw, read);
            }
            else
            {
                long innerOffset = (long)(uint)entry.Offset;
                long size        = (long)(uint)entry.Size;

                using var fs = new FileStream(pakPath, FileMode.Open, FileAccess.Read, FileShare.Read);

                if (innerOffset < 0 || innerOffset + size > fs.Length)
                    throw new InvalidDataException("IPF entry offset/size out of range in archive.");

                fs.Position = innerOffset;
                int read = fs.Read(raw, 0, raw.Length);
                if (read < raw.Length)
                    Array.Resize(ref raw, read);
            }

            if (TryDecompressZ0(raw, out var dec))
                return new MemoryStream(dec, writable: false);

            return new MemoryStream(raw, writable: false);
        }

        // data & helpers
        private static bool TryDecompressZ0(byte[] src, out byte[] dst)
        {
            dst = src;

            if (src.Length < 10 || src[0] != (byte)'Z' || src[1] != (byte)'0')
                return false;

            uint uncompressedLen =
                (uint)(src[2] << 24 | src[3] << 16 | src[4] << 8 | src[5]);

            int compOffset = 10;
            int compLen    = src.Length - compOffset;
            if (compLen <= 0)
                return false;

            try
            {
                using var msIn    = new MemoryStream(src, compOffset, compLen);
                using var deflate = new DeflateStream(msIn, CompressionMode.Decompress);
                using var msOut   = new MemoryStream(
                    uncompressedLen > 0 && uncompressedLen <= int.MaxValue
                        ? (int)uncompressedLen
                        : 4096);

                deflate.CopyTo(msOut);
                dst = msOut.ToArray();
                return true;
            }
            catch
            {
                dst = src;
                return false;
            }
        }

        private static Dictionary<uint, string> LoadNameMap()
        {
            var dict = new Dictionary<uint, string>();

            try
            {
                var asm = typeof(IkusabuneIpfArchiveHandler).Assembly;
                const string resourceName = "Verviewer.Misc.IPF.list.zlib";

                using var raw = asm.GetManifestResourceStream(resourceName);
                if (raw == null)
                    return dict;

                using var ms = new MemoryStream();
                try
                {
                    using var def = new DeflateStream(raw, CompressionMode.Decompress, leaveOpen: true);
                    def.CopyTo(ms);
                }
                catch
                {
                    raw.Position = 0;
                    raw.CopyTo(ms);
                }

                ms.Position = 0;
                using var reader = new StreamReader(ms, Encoding.UTF8);
                string? line;
                while ((line = reader.ReadLine()) != null)
                {
                    line = line.Trim();
                    if (line.Length == 0 || line.StartsWith("#"))
                        continue;

                    uint id = MakeFileId(line);
                    if (!dict.ContainsKey(id))
                        dict[id] = line;
                }
            }
            catch
            {
            }

            return dict;
        }

        private static uint MakeFileId(string path)
        {
            string s = path.Replace('\\', '/').ToLowerInvariant();

            uint v2 = 0;
            uint v3 = 0;

            foreach (char ch in s)
            {
                uint v6 = ch;
                v3 += v6;
                v2 = v6 + (v2 << 8);
                if ((v2 & 0xFF800000u) != 0)
                    v2 %= 0xFFF9D7u;
            }

            return v2 | (v3 << 24);
        }
    }
}