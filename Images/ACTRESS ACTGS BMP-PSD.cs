using System;
using System.Drawing;
using System.IO;
using Verviewer.Core;
using Utils;

namespace Verviewer.Images
{
    /// <summary>
    /// ACTGS PC 版归档（arc.cg）解出的条目本身就是标准 BMP / 标准 PSD，
    /// 直接交给 GDI+ 解码即可，无需自定义像素解码器。
    ///
    /// 实测签名分布（WHITE CLARITY，1003 条）：
    ///   "8BPS"  × 900   标准 PSD（channels/height/width/depth/mode 均为标准布局）
    ///   "BM"    × 103   标准 24bit 无压缩 BMP（bfOffBits=54, biSize=40, planes=1）
    ///
    /// 与 "ACTRESS PB6" 插件不冲突：PB6 以魔术 "PB6" 命中，本插件以 "BM"/"8BPS" 命中。
    /// </summary>
    [ImagePlugin(
        id: "ACTRESS ACTGS BMP/PSD",
        extensions: new[] { "bmp", "psd" },
        magics: new[] { "BM", "hex:38 42 50 53" }
    )]
    internal sealed class ActgsBmpPsdImageHandler : IImageHandler
    {
        public Image? TryDecode(Stream stream, string? ext)
        {
            if (stream == null || !stream.CanRead) return null;

            try
            {
                Stream s = stream.EnsureSeekable();
                s.Position = 0;

                var img = new Bitmap(s);
                if (img.Width <= 0 || img.Height <= 0 || img.Width > 16384 || img.Height > 16384)
                {
                    img.Dispose();
                    return null;
                }
                return img;
            }
            catch
            {
                return null;
            }
        }
    }
}
