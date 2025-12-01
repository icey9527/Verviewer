namespace Verviewer.Core
{
    /// <summary>
    /// 外部封包格式规则，对应 config/archives.csv 一行。
    /// </summary>
    internal class ArchiveRule
    {
        /// <summary>封包扩展名，例如 "dat"，可以为空表示不按扩展名匹配。</summary>
        public string? Extension { get; set; }

        /// <summary>要匹配的魔数字节（从文件头开始），可以为空数组表示不按魔数匹配。</summary>
        public byte[] MagicBytes { get; set; } = System.Array.Empty<byte>();

        /// <summary>用哪个封包插件来解（IArchiveHandler.Id）。</summary>
        public string ArchiveId { get; set; } = string.Empty;

        /// <summary>该封包内部优先使用哪个图片插件（IImageHandler.Id），可选。</summary>
        public string[] PreferredImageIds { get; set; } = Array.Empty<string>();
    }
}