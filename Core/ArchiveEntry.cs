namespace Verviewer.Core
{
    /// <summary>
    /// 封包里的一个条目（文件或目录）。
    /// </summary>
    internal sealed class ArchiveEntry
    {
        /// <summary>封包内部路径，例如 "folder/sub/file.txt"</summary>
        public string Path { get; init; } = string.Empty;

        /// <summary>是否目录</summary>
        public bool IsDirectory { get; init; }

        /// <summary>数据在封包文件中的偏移（字节）</summary>
        public long Offset { get; init; }

        /// <summary>压缩或原始数据大小（字节）</summary>
        public int Size { get; init; }

        /// <summary>解压后的大小（字节），如果未知可等于 Size</summary>
        public int UncompressedSize { get; init; }
    }
}