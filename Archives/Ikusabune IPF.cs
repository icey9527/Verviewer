// Some parts of this implementation are based on the original C tools:
// - IPF unpacker
// - z0_unpack + zlib-based raw deflate decompressor

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Windows.Forms;
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
        private static readonly Dictionary<uint, string> NameByHash = LoadNameMap();

        public OpenedArchive Open(string archivePath)
        {
            var fs = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var br = new BinaryReader(fs);

            fs.Position = 0;
            byte[] magic = br.ReadBytes(4);
            if (magic.Length < 4 ||
                magic[0] != (byte)'I' ||
                magic[1] != (byte)'P' ||
                magic[2] != (byte)'F' ||
                magic[3] != (byte)' ')
            {
                br.Dispose();
                fs.Dispose();
                throw new InvalidDataException("Not an IPF PAK file (magic != 'IPF ').");
            }

            uint entryCount = br.ReadUInt32();
            if (entryCount > 1_000_000)
            {
                br.Dispose();
                fs.Dispose();
                throw new InvalidDataException($"Unreasonable IPF entry count: {entryCount}");
            }

            var entries = new List<ArchiveEntry>((int)entryCount);

            for (uint i = 0; i < entryCount; i++)
            {
                int hash   = br.ReadInt32();
                int offset = br.ReadInt32();
                int size   = br.ReadInt32();

                uint hashU = unchecked((uint)hash);

                if (!NameByHash.TryGetValue(hashU, out var path))
                    path = $"${hashU:X8}";

                entries.Add(new ArchiveEntry
                {
                    Path = path.Replace('\\', '/'),
                    IsDirectory = false,
                    Offset = offset,
                    Size = size
                });
            }

            br.Dispose();
            return new OpenedArchive(archivePath, fs, entries, this);
        }

        public Stream OpenEntryStream(OpenedArchive archive, ArchiveEntry entry)
        {
            if (entry.IsDirectory)
                throw new InvalidOperationException("Directory entries have no data stream.");

            long encoded    = entry.Offset;
            int  partIndex  = (int)(encoded >> 28);
            long innerOffset = encoded & 0xFFFFFFF;

            string pakPath  = archive.SourcePath;
            string dir      = Path.GetDirectoryName(pakPath) ?? "";
            string baseName = Path.GetFileNameWithoutExtension(pakPath);
            string partPath = Path.Combine(dir, $"{baseName}.p{partIndex:00}");

            if (!File.Exists(partPath))
            {
                MessageBox.Show(
                    $"Missing IPF data file:\n{partPath}\n\n" +
                    $"Some entries in \"{Path.GetFileName(pakPath)}\" cannot be read.",
                    "IPF data missing",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning
                );
                return new MemoryStream(Array.Empty<byte>(), writable: false);
            }

            byte[] raw = new byte[entry.Size];

            using (var partFs = new FileStream(partPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (innerOffset < 0 || innerOffset + entry.Size > partFs.Length)
                    throw new InvalidDataException("IPF entry offset/size out of range in part file.");

                partFs.Position = innerOffset;
                int read = partFs.Read(raw, 0, raw.Length);
                if (read < raw.Length)
                    Array.Resize(ref raw, read);
            }

            if (TryDecompressZ0(raw, out var dec))
                return new MemoryStream(dec, writable: false);

            return new MemoryStream(raw, writable: false);
        }

        private static bool TryDecompressZ0(byte[] src, out byte[] dst)
        {
            dst = src;

            if (src.Length < 10)
                return false;

            if (src[0] != (byte)'Z' || src[1] != (byte)'0')
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