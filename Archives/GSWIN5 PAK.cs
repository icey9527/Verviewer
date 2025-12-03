using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Verviewer.Core;

namespace Verviewer.Archives
{
    [ArchivePlugin(
        id: "GSWIN5 PAK",
        extensions: new[] { "pak" },
        magics: new[] { "DataPack5" }
    )]
    internal sealed class Gswin5PakArchiveHandler : IArchiveHandler
    {
        public OpenedArchive Open(string archivePath)
        {
            var fs = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.RandomAccess);
            var entries = new List<ArchiveEntry>();

            try
            {
                using var br = new BinaryReader(fs, Encoding.ASCII, leaveOpen: true);
                fs.Position = 0;
                var header = br.ReadBytes(0x48);
                if (header.Length < 0x48)
                    throw new InvalidDataException();

                int indexSize   = BitConverter.ToInt32(header, 0x34);
                int fileCount   = BitConverter.ToInt32(header, 0x3C);
                int dataOffset  = BitConverter.ToInt32(header, 0x40);
                int indexOffset = BitConverter.ToInt32(header, 0x44);

                if (indexSize <= 0 || fileCount < 0)
                    throw new InvalidDataException();

                fs.Position = indexOffset;
                var compressedIndex = br.ReadBytes(indexSize);
                if (compressedIndex.Length < indexSize)
                    throw new EndOfStreamException();

                var indexData = GSWINArchiveHandler.DecompressLzss(compressedIndex, indexSize);

                const int entrySize = 0x68;
                if (indexData.Length < fileCount * entrySize)
                    throw new InvalidDataException();

                var enc = Encoding.GetEncoding(936);

                for (int i = 0; i < fileCount; i++)
                {
                    int baseOffset = i * entrySize;

                    var nameBytes = new byte[0x40];
                    Buffer.BlockCopy(indexData, baseOffset, nameBytes, 0, 0x40);
                    int zero = Array.IndexOf(nameBytes, (byte)0);
                    if (zero < 0) zero = nameBytes.Length;
                    string name = enc.GetString(nameBytes, 0, zero).Replace('\\', '/');

                    int relOffset = BitConverter.ToInt32(indexData, baseOffset + 0x40);
                    int size = BitConverter.ToInt32(indexData, baseOffset + 0x44);
                    long offset = (uint)dataOffset + (uint)relOffset;

                    entries.Add(new ArchiveEntry
                    {
                        Path = name,
                        Offset = offset,
                        Size = size,
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

        public Stream OpenEntryStream(OpenedArchive arc, ArchiveEntry entry)
        {
            arc.Stream.Position = entry.Offset;
            return new SubReadStream(arc.Stream, entry.Offset, entry.Size);
        }
    }
}