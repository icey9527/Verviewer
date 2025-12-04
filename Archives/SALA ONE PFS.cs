using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Verviewer.Core;
using Utils;

namespace Verviewer.Archives
{
    [ArchivePlugin(
        id: "SALA ONE PFS",
        extensions: new[] { "pfs", "ipd" },
        magics: new[] { "pf0" }
    )]
    internal sealed class SalaOnePfsHandler : IArchiveHandler
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
                // 读 "pf0" 魔数
                Span<byte> tag = stackalloc byte[3];
                if (fs.Read(tag) != 3)
                    throw new InvalidDataException("SALA ONE PFS: header too short.");

                if (tag[0] != (byte)'p' || tag[1] != (byte)'f' || tag[2] != (byte)'0')
                    throw new InvalidDataException("SALA ONE PFS: invalid magic.");

                // 文件数（小端 uint32）
                uint fileCount = ReadUInt32LE(fs);
                if (fileCount > 1_000_000)
                    throw new InvalidDataException("SALA ONE PFS: file count too large.");

                long fileSize = fs.Length;
                long headerSize = 7; // 3 字节 "pf0" + 4 字节 fileCount
                long tableSize = (long)fileCount * 268L; // 每项 256 名字 + 4 tag + 4 start + 4 length
                if (headerSize + tableSize > fileSize)
                    throw new InvalidDataException("SALA ONE PFS: index out of range.");

                for (uint i = 0; i < fileCount; i++)
                {
                    // 固定长度 256 字节名字，0 截断，其余填充
                    string path = fs.ReadFixedString(256, Encoding.UTF8)
                        .Replace('\\', '/');

                    uint fileTag = ReadUInt32LE(fs); // 目前没用到，但必须读掉保持对齐
                    uint start = ReadUInt32LE(fs);
                    uint length = ReadUInt32LE(fs);

                    long startL = start;
                    long lengthL = length;

                    if (startL < 0 || lengthL < 0 || startL + lengthL > fileSize)
                        throw new InvalidDataException("SALA ONE PFS: entry out of range.");

                    if (string.IsNullOrEmpty(path))
                        throw new InvalidDataException("SALA ONE PFS: empty entry name.");

                    if (startL > int.MaxValue || lengthL > int.MaxValue)
                        throw new InvalidDataException("SALA ONE PFS: offset/size exceeds 2GB.");

                    entries.Add(new ArchiveEntry
                    {
                        Path = path,
                        Offset = (int)startL,
                        Size = (int)lengthL,
                        IsDirectory = false
                    });
                }
            }
            catch
            {
                fs.Dispose();
                throw;
            }

            return new OpenedArchive(archivePath, fs, entries, this);
        }

        public Stream OpenEntryStream(OpenedArchive arc, ArchiveEntry entry)
        {
            if (entry.IsDirectory)
                throw new InvalidOperationException("Cannot open stream for directory entry.");

            return new RangeStream(arc.Stream, entry.Offset, entry.Size, leaveOpen: true);
        }

        static uint ReadUInt32LE(Stream s)
        {
            Span<byte> buf = stackalloc byte[4];
            int r = s.Read(buf);
            if (r != 4)
                throw new EndOfStreamException("SALA ONE PFS: unexpected EOF while reading UInt32.");

            return (uint)(
                buf[0]
                | (buf[1] << 8)
                | (buf[2] << 16)
                | (buf[3] << 24));
        }
    }
}