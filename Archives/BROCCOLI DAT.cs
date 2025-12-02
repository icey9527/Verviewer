using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Verviewer.Core;

namespace Verviewer.Archives
{
    [ArchivePlugin(
        id: "Broccoli DAT",
        extensions: new[] { "dat" },
        magics: null
    )]
    internal sealed class DatArchiveHandler : IArchiveHandler
    {
        const uint XorUncomp = 0x1f84c9af;
        const uint XorComp = 0x9ed835ab;
        const int RingSize = 4096;
        const int RingMask = 0x0fff;
        const int RingInit = 0xFEE;

        static readonly Encoding Cp932 = CreateCp932();

        static Encoding CreateCp932()
        {
            try { Encoding.RegisterProvider(CodePagesEncodingProvider.Instance); }
            catch { }
            return Encoding.GetEncoding(932);
        }

        public OpenedArchive Open(string archivePath)
        {
            var fs = new FileStream(
                archivePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 65536,
                options: FileOptions.RandomAccess
            );

            try
            {
                var entries = Parse(fs);
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
            if (entry.IsDirectory) throw new InvalidOperationException();
            var block = ReadBlock(arc.Stream, entry.Offset);
            if (block.Data.Length != entry.Size) throw new InvalidDataException();
            return new MemoryStream(block.Data, writable: false);
        }

        static List<ArchiveEntry> Parse(FileStream fs)
        {
            if (fs.Length < 12) throw new InvalidDataException();
            var indexBlock = ReadBlock(fs, 0);
            var index = indexBlock.Data;
            long dataStart = indexBlock.BlockSize;
            long fileSize = fs.Length;

            var entries =
                TryParseLayout(fs, index, fileSize, dataStart, headerSize: 8, addrOffset: 4) ??
                TryParseLayout(fs, index, fileSize, dataStart, headerSize: 12, addrOffset: 8);

            if (entries == null || entries.Count == 0) throw new InvalidDataException();
            return entries;
        }

        static List<ArchiveEntry>? TryParseLayout(
            FileStream fs,
            byte[] index,
            long fileSize,
            long dataStart,
            int headerSize,
            int addrOffset
        )
        {
            var list = new List<ArchiveEntry>();
            int pos = 0;

            while (pos + headerSize <= index.Length)
            {
                uint nameLen = BitConverter.ToUInt32(index, pos);
                uint addr = BitConverter.ToUInt32(index, pos + addrOffset);
                int namePos = pos + headerSize;

                if (nameLen == 0 || namePos + nameLen + 1 > index.Length)
                    return null;

                long headerOffset = dataStart + addr;
                if (headerOffset < dataStart || headerOffset + 12 > fileSize)
                    return null;

                int size;
                try
                {
                    (size, _) = ReadEntrySizes(fs, headerOffset);
                }
                catch
                {
                    return null;
                }

                var nameBytes = new byte[nameLen];
                Buffer.BlockCopy(index, namePos, nameBytes, 0, (int)nameLen);

                string rawName;
                try
                {
                    rawName = Cp932.GetString(nameBytes);
                }
                catch
                {
                    return null;
                }

                list.Add(new ArchiveEntry
                {
                    Path = rawName.Replace('\\', '/'),
                    Offset = headerOffset,
                    Size = size,
                    IsDirectory = false
                });

                pos = namePos + (int)nameLen + 1;
            }

            return list;
        }

        readonly struct Block
        {
            public Block(byte[] data, long nextOffset, int blockSize)
            {
                Data = data;
                NextOffset = nextOffset;
                BlockSize = blockSize;
            }

            public byte[] Data { get; }
            public long NextOffset { get; }
            public int BlockSize { get; }
        }

        static Block ReadBlock(Stream s, long offset)
        {
            var header = new byte[12];
            s.Position = offset;
            ReadExactly(s, header, 0, 12);

            uint uncomp = BitConverter.ToUInt32(header, 0) ^ XorUncomp;
            uint comp = BitConverter.ToUInt32(header, 4) ^ XorComp;
            uint checksum = BitConverter.ToUInt32(header, 8);

            if (uncomp > int.MaxValue || comp > int.MaxValue)
                throw new InvalidDataException();

            byte[] data;

            if (comp != 0)
            {
                var enc = new byte[comp];
                ReadExactly(s, enc, 0, enc.Length);
                Xor(enc, 0, enc.Length, CalcKey(checksum));
                data = new byte[uncomp];
                if (!Lzss(enc, enc.Length, data, data.Length))
                    throw new InvalidDataException();
            }
            else
            {
                data = new byte[uncomp];
                ReadExactly(s, data, 0, data.Length);
            }

            int blockSize = 12 + (int)(comp != 0 ? comp : uncomp);
            return new Block(data, offset + blockSize, blockSize);
        }

        static (int Uncomp, int Comp) ReadEntrySizes(Stream s, long offset)
        {
            var header = new byte[12];
            s.Position = offset;
            ReadExactly(s, header, 0, 12);

            uint uncomp = BitConverter.ToUInt32(header, 0) ^ XorUncomp;
            uint comp = BitConverter.ToUInt32(header, 4) ^ XorComp;

            if (uncomp > int.MaxValue || comp > int.MaxValue)
                throw new InvalidDataException();

            return ((int)uncomp, (int)comp);
        }

        static void ReadExactly(Stream s, byte[] buffer, int offset, int count)
        {
            int readTotal = 0;
            while (readTotal < count)
            {
                int r = s.Read(buffer, offset + readTotal, count - readTotal);
                if (r <= 0) throw new EndOfStreamException();
                readTotal += r;
            }
        }

        static byte CalcKey(uint checksum)
        {
            int sum =
                (int)(checksum & 0xFF) +
                (int)((checksum >> 8) & 0xFF) +
                (int)((checksum >> 16) & 0xFF) +
                (int)((checksum >> 24) & 0xFF);
            byte k = (byte)(sum & 0xFF);
            return k == 0 ? (byte)0xAA : k;
        }

        static void Xor(byte[] data, int offset, int count, byte key)
        {
            for (int i = 0; i < count; i++)
                data[offset + i] ^= key;
        }

        static bool Lzss(byte[] src, int srcLen, byte[] dst, int dstLen)
        {
            var ring = new byte[RingSize];
            int ringPos = RingInit;
            int flags = 0;
            int sp = 0;
            int dp = 0;

            while (dp < dstLen && sp < srcLen)
            {
                flags >>= 1;
                if ((flags & 0x100) == 0)
                {
                    if (sp >= srcLen) break;
                    flags = src[sp++] | 0xFF00;
                }

                if ((flags & 1) != 0)
                {
                    if (sp >= srcLen) break;
                    byte b = src[sp++];
                    dst[dp++] = b;
                    ring[ringPos] = b;
                    ringPos = (ringPos + 1) & RingMask;
                }
                else
                {
                    if (sp + 1 >= srcLen) break;
                    byte lo = src[sp++];
                    byte hi = src[sp++];
                    int off = ((hi & 0xF0) << 4) | lo;
                    int len = (hi & 0x0F) + 3;
                    for (int i = 0; i < len && dp < dstLen; i++)
                    {
                        byte b = ring[(off + i) & RingMask];
                        dst[dp++] = b;
                        ring[ringPos] = b;
                        ringPos = (ringPos + 1) & RingMask;
                    }
                }
            }

            return dp == dstLen;
        }
    }
}