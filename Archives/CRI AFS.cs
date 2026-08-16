using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Verviewer.Core;
using Utils;

namespace Verviewer.Archives
{
    [ArchivePlugin(
        id: "CRI AFS",
        extensions: new[] { "afs" },
        magics: new[] { "AFS" }
    )]
    internal sealed class CriAfsArchiveHandler : IArchiveHandler
    {
        const int HeaderSize = 8;
        const int EntryInfoSize = 8;
        const int AttributeInfoSize = 8;
        const int AttributeSize = 0x30;
        const int NameSize = 0x20;

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

            try
            {
                long fileSize = fs.Length;
                uint countU = fs.ReadUInt32LEAt(4);
                if (countU > 1_000_000)
                    throw new InvalidDataException("AFS entry count is out of range.");

                int count = (int)countU;
                long tableEnd = HeaderSize + (long)count * EntryInfoSize;
                if (tableEnd + AttributeInfoSize > fileSize)
                    throw new InvalidDataException("AFS entry table is truncated.");

                var offsets = new uint[count];
                var sizes = new uint[count];
                long firstDataOffset = fileSize;
                long dataEnd = 0;

                for (int i = 0; i < count; i++)
                {
                    long p = HeaderSize + (long)i * EntryInfoSize;
                    uint offset = fs.ReadUInt32LEAt(p);
                    uint size = fs.ReadUInt32LEAt(p + 4);
                    offsets[i] = offset;
                    sizes[i] = size;

                    if (offset == 0)
                        continue;
                    if (size > int.MaxValue || (long)offset + size > fileSize)
                        throw new InvalidDataException("AFS entry is outside the archive.");

                    firstDataOffset = Math.Min(firstDataOffset, offset);
                    dataEnd = Math.Max(dataEnd, (long)offset + size);
                }

                long attributeOffset = FindAttributeTable(fs, tableEnd, firstDataOffset, dataEnd, fileSize, count);
                var entries = new List<ArchiveEntry>(count);
                var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                for (int i = 0; i < count; i++)
                {
                    if (offsets[i] == 0)
                        continue;

                    string name = attributeOffset >= 0
                        ? fs.WithTemporarySeek(attributeOffset + (long)i * AttributeSize,
                            s => s.ReadFixedString(NameSize, ShiftJis))
                        : $"{i:00000000}";

                    name = NormalizeName(name, i, usedNames);
                    entries.Add(new ArchiveEntry
                    {
                        Path = name,
                        Offset = offsets[i],
                        Size = (int)sizes[i],
                        UncompressedSize = (int)sizes[i],
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

            return new RangeStream(archive.Stream, entry.Offset, entry.Size, leaveOpen: true);
        }

        static long FindAttributeTable(
            Stream stream,
            long tableEnd,
            long firstDataOffset,
            long dataEnd,
            long fileSize,
            int count)
        {
            if (TryReadAttributeInfo(stream, tableEnd, dataEnd, fileSize, count, out long offset))
                return offset;

            long trailingInfo = firstDataOffset - AttributeInfoSize;
            if (trailingInfo >= tableEnd &&
                TryReadAttributeInfo(stream, trailingInfo, dataEnd, fileSize, count, out offset))
                return offset;

            return -1;
        }

        static bool TryReadAttributeInfo(
            Stream stream,
            long infoOffset,
            long dataEnd,
            long fileSize,
            int count,
            out long attributeOffset)
        {
            attributeOffset = stream.ReadUInt32LEAt(infoOffset);
            uint attributeSize = stream.ReadUInt32LEAt(infoOffset + 4);
            long requiredSize = (long)count * AttributeSize;

            return attributeOffset != 0 &&
                   attributeSize >= requiredSize &&
                   attributeOffset >= dataEnd &&
                   attributeOffset <= fileSize - attributeSize;
        }

        static string NormalizeName(string name, int index, HashSet<string> usedNames)
        {
            string[] parts = name.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            name = string.Join('/', Array.FindAll(parts, p => p is not ("." or "..")));
            if (string.IsNullOrWhiteSpace(name))
                name = $"{index:00000000}";
            if (usedNames.Add(name))
                return name;

            string directory = Path.GetDirectoryName(name)?.Replace('\\', '/') ?? string.Empty;
            string stem = Path.GetFileNameWithoutExtension(name);
            string extension = Path.GetExtension(name);
            for (int duplicate = 1; ; duplicate++)
            {
                string uniqueName = $"{stem} ({duplicate}){extension}";
                string candidate = string.IsNullOrEmpty(directory) ? uniqueName : $"{directory}/{uniqueName}";
                if (usedNames.Add(candidate))
                    return candidate;
            }
        }
    }
}
