using System;
using System.IO;
using System.Text;
using System.Collections.Generic;
using Verviewer.Core;

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
                using var br = new BinaryReader(fs, Encoding.UTF8, leaveOpen: true);

                Span<byte> tag = stackalloc byte[3];
                if (fs.Read(tag) != 3)
                    throw new InvalidDataException("SALA ONE PFS: header too short.");

                if (tag[0] != (byte)'p' || tag[1] != (byte)'f' || tag[2] != (byte)'0')
                    throw new InvalidDataException("SALA ONE PFS: invalid magic.");

                uint fileCount = br.ReadUInt32();
                if (fileCount > 1_000_000)
                    throw new InvalidDataException("SALA ONE PFS: file count too large.");

                long fileSize = fs.Length;
                long headerSize = 7;
                long tableSize = (long)fileCount * 268L;
                if (headerSize + tableSize > fileSize)
                    throw new InvalidDataException("SALA ONE PFS: index out of range.");

                for (uint i = 0; i < fileCount; i++)
                {
                    byte[] nameBytes = br.ReadBytes(256);
                    if (nameBytes.Length != 256)
                        throw new EndOfStreamException("SALA ONE PFS: unexpected EOF in name.");

                    int zeroIndex = Array.IndexOf(nameBytes, (byte)0);
                    if (zeroIndex < 0)
                        zeroIndex = 256;

                    string path = Encoding.UTF8.GetString(nameBytes, 0, zeroIndex)
                        .Replace('\\', '/');

                    uint fileTag = br.ReadUInt32();
                    uint start = br.ReadUInt32();
                    uint length = br.ReadUInt32();

                    long startL = start;
                    long lengthL = length;

                    if (startL < 0 || lengthL < 0 || startL + lengthL > fileSize)
                        throw new InvalidDataException("SALA ONE PFS: entry out of range.");

                    if (string.IsNullOrEmpty(path))
                        throw new InvalidDataException("SALA ONE PFS: empty entry name.");

                    if (startL > int.MaxValue || lengthL > int.MaxValue)
                        throw new InvalidDataException("SALA ONE PFS: offset/size exceeds 2GB.");

                    var entry = new ArchiveEntry
                    {
                        Path = path,
                        Offset = (int)startL,
                        Size = (int)lengthL,
                        IsDirectory = false
                    };

                    entries.Add(entry);
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
            return new SubReadStream(arc.Stream, entry.Offset, entry.Size);
        }
    }

    internal sealed class SubReadStream : Stream
    {
        private readonly Stream _baseStream;
        private readonly long _start;
        private readonly long _length;
        private long _position;

        public SubReadStream(Stream baseStream, long start, long length)
        {
            _baseStream = baseStream ?? throw new ArgumentNullException(nameof(baseStream));
            _start = start;
            _length = length;
            _position = 0;

            if (!_baseStream.CanRead)
                throw new ArgumentException("Base stream must be readable.", nameof(baseStream));
        }

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => _length;

        public override long Position
        {
            get => _position;
            set => Seek(value, SeekOrigin.Begin);
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (buffer == null)
                throw new ArgumentNullException(nameof(buffer));
            if (offset < 0 || count < 0 || offset + count > buffer.Length)
                throw new ArgumentOutOfRangeException();

            if (_position >= _length)
                return 0;

            long remaining = _length - _position;
            if (count > remaining)
                count = (int)remaining;

            lock (_baseStream)
            {
                _baseStream.Position = _start + _position;
                int read = _baseStream.Read(buffer, offset, count);
                _position += read;
                return read;
            }
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            long newPos;
            switch (origin)
            {
                case SeekOrigin.Begin:
                    newPos = offset;
                    break;
                case SeekOrigin.Current:
                    newPos = _position + offset;
                    break;
                case SeekOrigin.End:
                    newPos = _length + offset;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(origin), origin, null);
            }

            if (newPos < 0 || newPos > _length)
                throw new IOException("Seek out of range.");

            _position = newPos;
            return _position;
        }

        public override void SetLength(long value)
        {
            throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            throw new NotSupportedException();
        }
    }
}