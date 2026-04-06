using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Verviewer.Core;
using Utils;

namespace Verviewer.Archives
{
    [ArchivePlugin(
        id: "Nine's fox PAC",
        extensions: new[] { "pac" },
        magics: new[] { "PAC" }
    )]
    internal sealed class NinesFox_PAC : IArchiveHandler
    {
        static readonly Encoding ShiftJis = Encoding.GetEncoding(932);

        public OpenedArchive Open(string archivePath)
        {
            var fs = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var entries = new List<ArchiveEntry>();

            try
            {
                uint nameTableOffset = fs.ReadUInt32LEAt(4);
                uint fileCount = fs.ReadUInt32LEAt(8);
                long fileSize = fs.Length;
                long fileTableOffset = 12;

                if (fileTableOffset + (long)fileCount * 8 > fileSize)
                    throw new InvalidDataException();

                if ((long)nameTableOffset + (long)fileCount * 0x40 > fileSize)
                    throw new InvalidDataException();

                for (uint i = 0; i < fileCount; i++)
                {
                    long p = fileTableOffset + i * 8;
                    uint offset = fs.ReadUInt32LEAt(p);
                    uint size = fs.ReadUInt32LEAt(p + 4);

                    if ((long)offset + size > fileSize)
                        throw new InvalidDataException();

                    string name = fs.WithTemporarySeek((long)nameTableOffset + i * 0x40, s =>
                        s.ReadFixedString(0x40, ShiftJis));

                    if (string.IsNullOrWhiteSpace(name))
                        throw new InvalidDataException();

                    entries.Add(new ArchiveEntry
                    {
                        Path = name.Replace('\\', '/'),
                        Offset = offset,
                        Size = (int)size,
                        IsDirectory = false
                    });
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
                throw new InvalidOperationException();

            var stream = new RangeStream(archive.Stream, entry.Offset, entry.Size, true);
            if (NinesFox.Decompress(stream, entry.Size, out var output))
                return new MemoryStream(output, false);

            stream.Position = 0;
            return stream;
        }
    }
}