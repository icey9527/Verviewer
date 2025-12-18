using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Verviewer.Core;
using Utils;

namespace Verviewer.Archives
{
    [ArchivePlugin(
        id: "ISO9660 (PS2/PSP ISO)",
        extensions: new[] { "iso" },
        magics: null)]
    internal sealed class IsoArchiveHandler : IArchiveHandler
    {
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
                long fileSize = fs.Length;

                foreach (var e in Iso9660Reader.EnumerateEntries(fs))
                {
                    if (e.IsDirectory)
                    {
                        entries.Add(new ArchiveEntry
                        {
                            Path = e.Path,
                            IsDirectory = true,
                            Size = 0,
                            UncompressedSize = 0
                        });
                    }
                    else
                    {
                        long offset = (long)e.Lba * Iso9660Reader.SectorSize;
                        int size = checked((int)e.Size);

                        if (offset < 0 || offset + size > fileSize)
                            throw new InvalidDataException("ISO entry out of range: " + e.Path);

                        entries.Add(new ArchiveEntry
                        {
                            Path = e.Path,
                            IsDirectory = false,
                            Offset = offset,
                            Size = size,
                            UncompressedSize = size
                        });
                    }
                }

                return new OpenedArchive(archivePath, fs, entries, this);
            }
            catch
            {
                fs.Dispose();
                throw;
            }
        }

        public Stream OpenEntryStream(OpenedArchive archive, ArchiveEntry entry)
        {
            if (entry.IsDirectory)
                throw new InvalidOperationException("Cannot open stream for directory");

            return new RangeStream(archive.Stream, entry.Offset, entry.Size, true);
        }
    }

    internal static class Iso9660Reader
    {
        public const int SectorSize = 2048;

        internal sealed class IsoEntry
        {
            public string Path { get; set; } = string.Empty;
            public bool IsDirectory { get; set; }
            public uint Lba { get; set; }
            public uint Size { get; set; }
        }

        public static IEnumerable<IsoEntry> EnumerateEntries(Stream iso)
        {
            if (iso == null) throw new ArgumentNullException(nameof(iso));
            if (!iso.CanSeek) throw new ArgumentException("Stream must be seekable.", nameof(iso));

            var sector = new byte[SectorSize];
            uint lba = 16;

            while (true)
            {
                iso.Position = (long)lba * SectorSize;
                int read = iso.Read(sector, 0, SectorSize);
                if (read != SectorSize)
                    throw new InvalidDataException("Cannot read Primary Volume Descriptor.");

                byte type = sector[0];
                if (Encoding.ASCII.GetString(sector, 1, 5) != "CD001")
                    throw new InvalidDataException("Not a valid ISO9660 image.");

                if (type == 1) break;
                if (type == 255) throw new InvalidDataException("Primary Volume Descriptor not found.");

                lba++;
            }

            const int rootOffset = 156;
            byte lenDr = sector[rootOffset];
            if (lenDr <= 0)
                throw new InvalidDataException("Root Directory Record invalid.");

            uint rootLba = ReadUInt32LE(sector, rootOffset + 2);
            uint rootSize = ReadUInt32LE(sector, rootOffset + 10);

            foreach (var e in EnumerateDirectory(iso, rootLba, rootSize, string.Empty))
                yield return e;
        }

        private static IEnumerable<IsoEntry> EnumerateDirectory(Stream iso, uint dirLba, uint dirSize, string parentPath)
        {
            if (dirSize == 0)
                yield break;

            long dirStart = (long)dirLba * SectorSize;
            long remaining = dirSize;
            var buffer = new byte[SectorSize];

            int sectorIndex = 0;
            while (remaining > 0)
            {
                iso.Position = dirStart + (long)sectorIndex * SectorSize;
                int read = iso.Read(buffer, 0, SectorSize);
                if (read != SectorSize)
                    throw new InvalidDataException("Unexpected EOF while reading directory sector.");

                int offset = 0;
                while (offset < SectorSize)
                {
                    byte lenDr = buffer[offset];
                    if (lenDr == 0)
                        break;

                    if (offset + lenDr > SectorSize)
                        throw new InvalidDataException("Directory record crosses sector boundary.");

                    uint lba = ReadUInt32LE(buffer, offset + 2);
                    uint dataLength = ReadUInt32LE(buffer, offset + 10);
                    byte flags = buffer[offset + 25];
                    bool isDir = (flags & 0x02) != 0;
                    byte nameLen = buffer[offset + 32];

                    if (33 + nameLen > offset + lenDr)
                        throw new InvalidDataException("File identifier out of range.");

                    string name;
                    if (nameLen == 1)
                    {
                        byte id = buffer[offset + 33];
                        if (id == 0) name = ".";
                        else if (id == 1) name = "..";
                        else name = ((char)id).ToString();
                    }
                    else
                    {
                        name = Encoding.ASCII.GetString(buffer, offset + 33, nameLen);
                    }

                    offset += lenDr;

                    if (name == "." || name == "..")
                        continue;

                    if (!isDir)
                    {
                        int semicolon = name.IndexOf(';');
                        if (semicolon >= 0)
                            name = name.Substring(0, semicolon);
                        name = name.TrimEnd('.');
                    }

                    string path = string.IsNullOrEmpty(parentPath) ? name : parentPath + "/" + name;

                    if (isDir)
                    {
                        yield return new IsoEntry
                        {
                            Path = path,
                            IsDirectory = true,
                            Lba = lba,
                            Size = 0
                        };

                        foreach (var child in EnumerateDirectory(iso, lba, dataLength, path))
                            yield return child;
                    }
                    else
                    {
                        yield return new IsoEntry
                        {
                            Path = path,
                            IsDirectory = false,
                            Lba = lba,
                            Size = dataLength
                        };
                    }
                }

                sectorIndex++;
                remaining -= SectorSize;
            }
        }

        private static uint ReadUInt32LE(byte[] buffer, int offset)
        {
            return (uint)(
                buffer[offset]
                | (buffer[offset + 1] << 8)
                | (buffer[offset + 2] << 16)
                | (buffer[offset + 3] << 24));
        }
    }
}