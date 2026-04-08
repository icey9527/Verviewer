using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Verviewer.Core;
using Utils;

namespace Verviewer.Archives
{
    [ArchivePlugin(
        id: "LEAF PAK",
        extensions: new[] { "pak" },
        magics: new[] { "LEAFPACK" }
    )]
    internal sealed class LeafPakArchiveHandler : IArchiveHandler
    {
        const int HeaderSize = 8;
        const int EntrySize = 0x18;

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
                if (fileSize < HeaderSize + 3)
                    throw new InvalidDataException("Archive is too small.");

                int keyLength = fs.ReadByteAt(fileSize - 1);
                if (keyLength <= 0)
                    throw new InvalidDataException("Key length is invalid.");

                long keyOffset = HeaderSize;
                long keyEnd = keyOffset + keyLength;
                if (keyEnd > fileSize - 3)
                    throw new InvalidDataException("Key exceeds archive bounds.");

                ushort entryCount = fs.ReadUInt16LEAt(fileSize - 3);
                long indexSize = (long)entryCount * EntrySize;
                long indexOffset = fileSize - 3 - indexSize;
                if (indexOffset < keyEnd)
                    throw new InvalidDataException("Index overlaps archive header or data.");

                byte[] key = fs.ReadBytesAt(keyOffset, keyLength);
                byte[] index = fs.ReadBytesAt(indexOffset, checked((int)indexSize));
                CryptInPlace(index, key, add: false);

                for (int i = 0; i < entryCount; i++)
                {
                    int baseOffset = i * EntrySize;
                    string name = DecodeName(index.AsSpan(baseOffset, 12));
                    if (string.IsNullOrWhiteSpace(name))
                        throw new InvalidDataException($"Entry {i} has an empty name.");

                    uint offset = ReadUInt32LE(index, baseOffset + 0x0C);
                    uint size = ReadUInt32LE(index, baseOffset + 0x10);
                    uint endOffset = ReadUInt32LE(index, baseOffset + 0x14);

                    if (offset + size != endOffset)
                        throw new InvalidDataException($"Entry {i} has an invalid end offset.");
                    if (endOffset > indexOffset)
                        throw new InvalidDataException($"Entry {i} data overlaps index.");

                    entries.Add(new ArchiveEntry
                    {
                        Path = name.Replace('\\', '/'),
                        Offset = offset,
                        Size = checked((int)size),
                        UncompressedSize = checked((int)size),
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
                throw new InvalidOperationException("Directory entries do not have data streams.");
            if (entry.Offset < 0 || entry.Size < 0 || entry.Offset + entry.Size > archive.Stream.Length)
                throw new InvalidDataException("Entry data is out of archive bounds.");

            byte[] key = ReadKey(archive.Stream);
            byte[] data = archive.Stream.ReadBytesAt(entry.Offset, entry.Size);
            CryptInPlace(data, key, add: false);
            return new MemoryStream(data, writable: false);
        }

        static byte[] ReadKey(FileStream fs)
        {
            long fileSize = fs.Length;
            int keyLength = fs.ReadByteAt(fileSize - 1);
            if (keyLength <= 0)
                throw new InvalidDataException("Key length is invalid.");
            return fs.ReadBytesAt(HeaderSize, keyLength);
        }

        static void CryptInPlace(byte[] data, byte[] key, bool add)
        {
            if (key.Length == 0)
                throw new InvalidDataException("Key length is zero.");

            for (int i = 0; i < data.Length; i++)
            {
                int value = data[i];
                int k = key[i % key.Length];
                data[i] = (byte)(add ? (value + k) & 0xFF : (value - k) & 0xFF);
            }
        }

        static string DecodeName(ReadOnlySpan<byte> raw)
        {
            Span<byte> nameRaw = stackalloc byte[8];
            Span<byte> extRaw = stackalloc byte[3];
            raw.Slice(0, 8).CopyTo(nameRaw);
            raw.Slice(8, 3).CopyTo(extRaw);

            string name = ShiftJis.GetString(TrimName(nameRaw));
            string ext = ShiftJis.GetString(TrimName(extRaw));
            return string.IsNullOrEmpty(ext) ? name : $"{name}.{ext}";
        }

        static byte[] TrimName(ReadOnlySpan<byte> src)
        {
            int end = src.Length;
            while (end > 0 && (src[end - 1] == 0 || src[end - 1] == 0x20))
                end--;

            if (end <= 0)
                return Array.Empty<byte>();

            var trimmed = new byte[end];
            src.Slice(0, end).CopyTo(trimmed);
            return trimmed;
        }

        static uint ReadUInt32LE(byte[] data, int offset)
        {
            return (uint)(
                data[offset]
                | (data[offset + 1] << 8)
                | (data[offset + 2] << 16)
                | (data[offset + 3] << 24));
        }
    }
}
