using System.Drawing;

namespace Verviewer.Core
{
    /// <summary>
    /// 图片处理插件：把单个二进制文件解成 Image。
    /// </summary>
    internal interface IImageHandler
    {
        /// <summary>图片插件ID，可以在 archives.csv 里作为 PreferredImageId 使用。</summary>

        /// <summary>
        /// 尝试解码。data 是整个文件内容，extension 是文件扩展名（例如 ".agi"）。
        /// 成功返回 Image，失败返回 null。
        /// </summary>
        Image? TryDecode(byte[] data, string extension);
    }
}