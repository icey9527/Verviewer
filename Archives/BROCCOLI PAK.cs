using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Verviewer.Core;
using Utils;

namespace Verviewer.Archives
{
    [ArchivePlugin(
        id: "BROCCOLI PAK",
        extensions: new[] { "pak" },
        magics: new[] { "DATA$TOP" }
    )]
    internal sealed class BroccoliPakArchiveHandler : IArchiveHandler
    {
        const int EntrySize = 0x40;
        const int NameSize = 0x30;
        const int OffsetField = 0x34;
        const int SizeField = 0x38;

        static readonly Encoding ShiftJis = Encoding.GetEncoding(932);

        public OpenedArchive Open(string archivePath)
        {
            var fs = new FileStream(
                archivePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 65536,
                options: FileOptions.RandomAccess);

            var entries = new List<ArchiveEntry>();

            try
            {
                long fileSize = fs.Length;
                if (fileSize < EntrySize * 2)
                    throw new InvalidDataException();

                int count = fs.ReadInt32LEAt(SizeField);
                if (count <= 1)
                    throw new InvalidDataException();

                long dataOffset = (long)count * EntrySize;
                if (dataOffset > fileSize)
                    throw new InvalidDataException();

                for (int i = 1; i < count; i++)
                {
                    long entryOffset = (long)i * EntrySize;
                    string name = fs.ReadFixedStringAt(entryOffset, NameSize, ShiftJis).Trim();
                    uint offset = fs.ReadUInt32LEAt(entryOffset + OffsetField);
                    uint size = fs.ReadUInt32LEAt(entryOffset + SizeField);

                    if (string.IsNullOrWhiteSpace(name) || size == 0)
                        continue;

                    long absoluteOffset = dataOffset + offset;
                    if (absoluteOffset + size > fileSize)
                        continue;

                    entries.Add(new ArchiveEntry
                    {
                        Path = name.Replace('\\', '/'),
                        Offset = absoluteOffset,
                        Size = checked((int)size),
                        UncompressedSize = checked((int)size),
                        IsDirectory = false
                    });
                }

                if (entries.Count == 0)
                    throw new InvalidDataException();

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
                throw new InvalidOperationException();
            if (entry.Offset < 0 || entry.Size < 0 || entry.Offset + entry.Size > archive.Stream.Length)
                throw new InvalidDataException();

            return new RangeStream(archive.Stream, entry.Offset, entry.Size, leaveOpen: true);
        }
    }
}
