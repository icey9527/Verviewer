using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Verviewer.Core;
using Utils;

namespace Verviewer.Archives
{
    [ArchivePlugin(
        id: "Artdink DAT",
        extensions: new[] { "dat" },
        magics: new[] { "PIDX0" }
    )]
    internal class Artdink_DAT : IArchiveHandler
    {
        static readonly Encoding ShiftJis = Encoding.GetEncoding(932);

        public OpenedArchive Open(string archivePath)
        {
            var fs = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var br = new BinaryReader(fs, Encoding.ASCII, leaveOpen: true);
            var header = ReadHeader(br);

            var entries = new List<ArchiveEntry>();
            var dirs = new HashSet<string>();
            var fileMap = new Dictionary<string, ArchiveEntry>();

            if (header.Table2Count > 0)
            {
                var table2 = ParseTable2(fs, br, header);
                CollectTable2Entries(entries, dirs, fileMap, table2, header);
            }

            if (header.Table3Size > 0)
                CollectTable3Entries(entries, dirs, fileMap, fs, br, header);

            br.Dispose();
            return new OpenedArchive(archivePath, fs, entries, this);
        }

        public Stream OpenEntryStream(OpenedArchive archive, ArchiveEntry entry)
        {
            if (entry.IsDirectory)
                throw new InvalidOperationException();

            var fs = archive.Stream;
            if (entry.Offset > fs.Length)
                return Stream.Null;

            fs.Position = entry.Offset;

            if (entry.Size > 0 && Artdink.Decompress(fs, entry.Size, out var dec))
                return new MemoryStream(dec, false);

            int rawSize = entry.UncompressedSize > 0 ? entry.UncompressedSize : entry.Size;
            if (rawSize <= 0)
                return Stream.Null;

            fs.Position = entry.Offset;
            var raw = new byte[rawSize];
            int read = fs.Read(raw, 0, raw.Length);
            if (read < raw.Length)
                Array.Resize(ref raw, read);
            return new MemoryStream(raw, false);
        }

        PidxHeader ReadHeader(BinaryReader br)
        {
            br.BaseStream.Position = 0;
            return new PidxHeader
            {
                Magic = Encoding.ASCII.GetString(br.ReadBytes(4)),
                Table1Offset = br.ReadUInt32(),
                Table1Count = br.ReadUInt32(),
                Table2Offset = br.ReadUInt32(),
                Table2Count = br.ReadUInt32(),
                RootDirectoryChildCount = br.ReadUInt32(),
                Table3Offset = br.ReadUInt32(),
                Table3Size = br.ReadUInt32(),
                StringPoolOffset = br.ReadUInt32(),
                StringPoolSize = br.ReadUInt32()
            };
        }

        List<Table2Entry> ParseTable2(FileStream fs, BinaryReader br, PidxHeader header)
        {
            var list = new List<Table2Entry>((int)header.Table2Count);
            for (uint i = 0; i < header.Table2Count; i++)
            {
                fs.Position = header.Table2Offset + i * 24;
                uint type = br.ReadUInt32();
                uint nameOffset = br.ReadUInt32();
                uint field08 = br.ReadUInt32();
                uint field0C = br.ReadUInt32();
                uint field10 = br.ReadUInt32();
                uint field14 = br.ReadUInt32();

                var entry = new Table2Entry
                {
                    Name = ReadStringAt(fs, header.StringPoolOffset + nameOffset),
                    IsDirectory = type == 1
                };

                if (entry.IsDirectory)
                {
                    entry.ChildCount = field08;
                    entry.ChildStart = field0C;
                }
                else
                {
                    entry.DataOffset = field0C;
                    entry.DecompressedSize = field10;
                    entry.CompressedSize = field14;
                }

                list.Add(entry);
            }

            return list;
        }

        void CollectTable2Entries(List<ArchiveEntry> entries, HashSet<string> dirs,
            Dictionary<string, ArchiveEntry> fileMap, List<Table2Entry> table2, PidxHeader header)
        {
            var visited = new HashSet<int>();
            var paths = new Dictionary<int, string>();

            int rootCount = (int)Math.Min(header.RootDirectoryChildCount, (uint)table2.Count);
            if (rootCount > 0)
            {
                for (int i = 0; i < rootCount; i++)
                    BuildPaths(table2, i, string.Empty, paths, visited);
            }
            else if (table2.Count > 0)
            {
                BuildPaths(table2, 0, string.Empty, paths, visited);
            }

            foreach (var kvp in paths)
            {
                var entry = table2[kvp.Key];
                if (entry.IsDirectory)
                {
                    if (dirs.Add(kvp.Value))
                        entries.Add(new ArchiveEntry { Path = kvp.Value, IsDirectory = true });
                }
                else
                {
                    AddFile(entries, fileMap, kvp.Value, entry.DataOffset,
                        (int)entry.CompressedSize, (int)entry.DecompressedSize);
                }
            }

            for (int i = 0; i < table2.Count; i++)
            {
                if (visited.Contains(i) || table2[i].IsDirectory)
                    continue;

                var entry = table2[i];
                AddFile(entries, fileMap, entry.Name, entry.DataOffset,
                    (int)entry.CompressedSize, (int)entry.DecompressedSize);
            }
        }

        void BuildPaths(List<Table2Entry> table2, int index, string parentPath,
            Dictionary<int, string> paths, HashSet<int> visited)
        {
            if (index < 0 || index >= table2.Count || visited.Contains(index))
                return;

            visited.Add(index);
            var entry = table2[index];
            string path = string.IsNullOrEmpty(parentPath) ? entry.Name : parentPath + "/" + entry.Name;
            paths[index] = path;

            if (!entry.IsDirectory)
                return;

            for (uint i = 0; i < entry.ChildCount; i++)
                BuildPaths(table2, (int)(entry.ChildStart + i), path, paths, visited);
        }

        void CollectTable3Entries(List<ArchiveEntry> entries, HashSet<string> dirs,
            Dictionary<string, ArchiveEntry> fileMap, FileStream fs, BinaryReader br, PidxHeader header)
        {
            fs.Position = header.Table3Offset;
            uint count = br.ReadUInt32();

            var pointers = new uint[count];
            for (uint i = 0; i < count; i++)
                pointers[i] = br.ReadUInt32() + header.Table3Offset;

            for (uint i = 0; i < count; i++)
            {
                fs.Position = pointers[i] + 8;
                uint fstsOffset = br.ReadUInt32();

                if (fstsOffset + 16 > fs.Length)
                    continue;

                fs.Position = fstsOffset;
                if (Encoding.ASCII.GetString(br.ReadBytes(4)) != "FSTS")
                    continue;

                uint entryCount = br.ReadUInt32();
                uint entriesOffset = br.ReadUInt32();
                uint stringPoolOffset = br.ReadUInt32();

                for (uint j = 0; j < entryCount; j++)
                {
                    fs.Position = fstsOffset + entriesOffset + j * 16;
                    uint nameOff = br.ReadUInt32();
                    uint dataOff = br.ReadUInt32();
                    uint decompSize = br.ReadUInt32();
                    uint compSize = br.ReadUInt32();

                    string name = ReadStringAt(fs, fstsOffset + stringPoolOffset + nameOff);
                    string path = name.Replace('\\', '/').TrimStart('/');

                    AddDirectories(entries, dirs, path);
                    AddFile(entries, fileMap, path, fstsOffset + dataOff, (int)compSize, (int)decompSize);
                }
            }
        }

        void AddFile(List<ArchiveEntry> entries, Dictionary<string, ArchiveEntry> fileMap,
            string path, long offset, int compSize, int decompSize)
        {
            if (fileMap.ContainsKey(path))
                return;

            var entry = new ArchiveEntry
            {
                Path = path,
                IsDirectory = false,
                Offset = offset,
                Size = compSize,
                UncompressedSize = decompSize
            };

            entries.Add(entry);
            fileMap[path] = entry;
        }

        void AddDirectories(List<ArchiveEntry> entries, HashSet<string> dirs, string filePath)
        {
            var parts = filePath.Split('/');
            if (parts.Length <= 1)
                return;

            var sb = new StringBuilder();
            for (int i = 0; i < parts.Length - 1; i++)
            {
                if (i > 0)
                    sb.Append('/');
                sb.Append(parts[i]);
                string dirPath = sb.ToString();
                if (dirs.Add(dirPath))
                    entries.Add(new ArchiveEntry { Path = dirPath, IsDirectory = true });
            }
        }

        string ReadStringAt(FileStream fs, long offset)
        {
            if (offset < 0 || offset >= fs.Length)
                return string.Empty;

            long saved = fs.Position;
            fs.Position = offset;

            var bytes = new List<byte>();
            int b;
            while ((b = fs.ReadByte()) > 0)
                bytes.Add((byte)b);

            fs.Position = saved;
            return bytes.Count == 0 ? string.Empty : ShiftJis.GetString(bytes.ToArray());
        }

        class PidxHeader
        {
            public string Magic;
            public uint Table1Offset;
            public uint Table1Count;
            public uint Table2Offset;
            public uint Table2Count;
            public uint RootDirectoryChildCount;
            public uint Table3Offset;
            public uint Table3Size;
            public uint StringPoolOffset;
            public uint StringPoolSize;
        }

        class Table2Entry
        {
            public string Name;
            public bool IsDirectory;
            public uint ChildCount;
            public uint ChildStart;
            public uint DataOffset;
            public uint DecompressedSize;
            public uint CompressedSize;
        }
    }
}