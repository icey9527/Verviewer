using System.Drawing;
using System.IO;

namespace Verviewer.Core
{
    internal interface IImageHandler
    {
        Image? TryDecode(Stream stream, string? ext);
    }
}