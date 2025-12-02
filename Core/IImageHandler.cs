using System.Drawing;

namespace Verviewer.Core
{
    /// <summary>
    /// 图片处理插件：把单个二进制文件解成 Image。
    /// </summary>
    internal interface IImageHandler
    {
        Image? TryDecode(byte[] data, string extension);
    }
}