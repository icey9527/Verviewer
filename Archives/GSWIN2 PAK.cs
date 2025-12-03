using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Verviewer.Core;

namespace Verviewer.Archives
{
    [ArchivePlugin(
        id: "GSWIN2 PAK",
        extensions: new[] { "pak" },
        magics: new[] { "GswSys PACK 2.0" }
    )]
    internal sealed class GSWINArchiveHandler : IArchiveHandler
    {
        public OpenedArchive Open(string archivePath)
        {
            var fs = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.RandomAccess);
            var entries = new List<ArchiveEntry>();
            try
            {
                using var br = new BinaryReader(fs, Encoding.ASCII, true);
                fs.Position = 0;
                var header = br.ReadBytes(0x1C);
                if (header.Length < 0x1C) throw new InvalidDataException();
                int indexSize = BitConverter.ToInt32(header, 0x10);
                int fileCount = BitConverter.ToInt32(header, 0x14);
                int dataOffset = BitConverter.ToInt32(header, 0x18);
                if (indexSize <= 0 || fileCount < 0) throw new InvalidDataException();
                var compressedIndex = br.ReadBytes(indexSize);
                if (compressedIndex.Length < indexSize) throw new EndOfStreamException();
                XorDecrypt(compressedIndex);
                var indexData = DecompressLzss(compressedIndex, indexSize);
                const int entrySize = 0x28;
                if (indexData.Length < fileCount * entrySize) throw new InvalidDataException();
                var sjis = Encoding.GetEncoding(932);
                for (int i = 0; i < fileCount; i++)
                {
                    int baseOffset = i * entrySize;
                    var nameBytes = new byte[0x20];
                    Buffer.BlockCopy(indexData, baseOffset, nameBytes, 0, 0x20);
                    int zero = Array.IndexOf(nameBytes, (byte)0);
                    if (zero < 0) zero = nameBytes.Length;
                    string name = sjis.GetString(nameBytes, 0, zero).Replace('\\', '/');
                    int relOffset = BitConverter.ToInt32(indexData, baseOffset + 0x20);
                    int size = BitConverter.ToInt32(indexData, baseOffset + 0x24);
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

        public static byte[] DecompressLzss(byte[] data, int compressedSize)
        {
            int limit = Math.Min(compressedSize, data.Length);
            int index = 0;
            using var output = new MemoryStream();
            Lzss.Decompress(
                () => index < limit ? data[index++] : -1,
                b => output.WriteByte(b),
                limit
            );
            return output.ToArray();
        }

        public static void DecompressLzss(Stream input, int compressedSize, Stream output)
        {
            Lzss.Decompress(
                () => input.ReadByte(),
                b => output.WriteByte(b),
                compressedSize
            );
        }

        public static byte[] XorDecrypt(byte[] data)
        {
            for (int i = 0; i < data.Length; i++) data[i] = (byte)(data[i] ^ (byte)i);
            return data;
        }
    }
}