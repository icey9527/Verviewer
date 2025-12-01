using System.IO;

namespace Verviewer.Core
{
    /// <summary>
    /// 封包处理插件：负责解析索引 + 按需读取单个条目。
    /// </summary>
    internal interface IArchiveHandler
    {
        /// <summary>
        /// 插件ID，要和 config/archives.csv 里的 ArchiveId 一致。
        /// 你可以直接写 "ARTDINK DAT" 这种人类可读的名字。
        /// </summary>
        /// <summary>
        /// 打开封包：只解析索引表，不解压文件。
        /// </summary>
        OpenedArchive Open(string archivePath);

        /// <summary>
        /// 按需打开一个条目的数据流。
        /// 内部负责 Seek/解压/解密，返回解码后的数据流。
        /// 调用方负责使用完释放该流。
        /// </summary>
        Stream OpenEntryStream(OpenedArchive archive, ArchiveEntry entry);
    }
}