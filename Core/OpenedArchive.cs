using System;
using System.Collections.Generic;
using System.IO;

namespace Verviewer.Core
{
    /// <summary>
    /// 已打开的封包：持有一个 FileStream 和解析出的条目列表。
    /// </summary>
    internal sealed class OpenedArchive : IDisposable
    {
        public string SourcePath { get; }
        public FileStream Stream { get; }
        public IReadOnlyList<ArchiveEntry> Entries { get; }
        public IArchiveHandler Handler { get; }

        public OpenedArchive(
            string sourcePath,
            FileStream stream,
            IReadOnlyList<ArchiveEntry> entries,
            IArchiveHandler handler)
        {
            SourcePath = sourcePath;
            Stream = stream;
            Entries = entries;
            Handler = handler;
        }

        public void Dispose()
        {
            Stream.Dispose();
        }
    }
}