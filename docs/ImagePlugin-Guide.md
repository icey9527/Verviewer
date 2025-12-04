# 二、图片插件（Image）开发指南（无头部判断版）

## 0. 原则与行为约定

- 框架根据扩展名 / 魔术选择 `IImageHandler`；
- 图片插件不需要也不应该再判断“是不是我”：
  - 不要因为“文件头魔术不对”而返回 null；
  - 只能按**既定的数据结构**去读，如果读失败就认为“数据坏了”。

- `TryDecode`：
  - 成功 → 返回 `Image`；
  - 失败（数据损坏、不符合预期等）→ 返回 `null`；
  - 不向外抛异常。

- 不要关闭原始 `stream`，只关闭你自己创建的辅助流（例如 `EnsureSeekable` 返回的 `MemoryStream`）。

---

## 1. ImagePlugin 注册

```csharp
using System;
using System.Drawing;
using System.IO;
using Verviewer.Core;
using Utils;

namespace Verviewer.Images
{
    [ImagePlugin(
        id: "My Image Format",
        extensions: new[] { "img" },   // 可以是 null
        magics:     new[] { "IMGF" }   // 可以是 null
    )]
    internal sealed class MyImageHandler : IImageHandler
    {
        public Image? TryDecode(Stream stream, string? ext)
        {
            // 解码
        }
    }
}
```

同样：`extensions`/`magics` 用于框架选择插件，插件内部无需再做头部判断。

---

## 2. 推荐骨架（无头部判断版）

```csharp
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using Verviewer.Core;
using Utils;

namespace Verviewer.Images
{
    [ImagePlugin(
        id: "My Image Format",
        extensions: new[] { "img" }
    )]
    internal sealed class MyImageHandler : IImageHandler
    {
        public Image? TryDecode(Stream stream, string? ext)
        {
            // 1) 确保可 Seek
            Stream s = stream.EnsureSeekable();

            try
            {
                if (!s.CanSeek || s.Length < 8) // 至少要能读宽高
                    return null;

                // 2) 按照“格式约定”读取结构（不判断是否是本格式，只做合理性检查）
                s.Position = 0;

                // 例如：假设前 4 字节是 width，后 4 字节是 height（小端）
                int width  = s.ReadInt32LEAt(0);
                int height = s.ReadInt32LEAt(4);

                if (width <= 0 || height <= 0 || width > 16384 || height > 16384)
                    return null;

                // 3) 创建并锁定位图
                var bmp = ImageUtils.CreateArgbBitmap(width, height, out var bd, out int stride);

                try
                {
                    // 4) 一行一行读取像素，并用 ImageUtils 写入位图
                    // 假设后面就是 width*height 个 RGBA 像素:
                    long pixelBytes = (long)width * height * 4;
                    if (s.Position + pixelBytes > s.Length)
                    {
                        bmp.Dispose();
                        return null;
                    }

                    var srcRow = new byte[width * 4];
                    var row    = new byte[width * 4];

                    for (int y = 0; y < height; y++)
                    {
                        s.ReadExactly(srcRow, 0, srcRow.Length);
                        ImageUtils.ConvertRowRgba32ToBgra(srcRow, row, width);
                        ImageUtils.CopyRowToBitmap(bd, y, row, stride);
                    }

                    return bmp;
                }
                catch
                {
                    bmp.Dispose();
                    return null;
                }
                finally
                {
                    ImageUtils.UnlockBitmap(bd, bmp);
                }
            }
            catch
            {
                // 任何异常都视为“解码失败”，返回 null
                return null;
            }
            finally
            {
                if (!ReferenceEquals(s, stream))
                    s.Dispose();
            }
        }
    }
}
```

关键点：

- 不再有 `if (magic != "XXX") return null;`；
- 只做**结构合理性检查**（宽高范围、像素总大小不超出文件长度等）。

---

## 3. 常见模式整理（给 AI 的“套路库”）

### 3.1 索引色图片（4bpp / 8bpp）

- 固定或指向调色板偏移；
- 调色板是 `N * 4` 字节的 RGBA；
- 像素区按索引排列：

```csharp
// 例: 8bpp, 256 色, 调色板在 palOffset
s.Position = palOffset;
byte[] palRaw = s.ReadExactly(256 * 4);        // RGBA

byte[] paletteBgra = ImageUtils.BuildPs2Palette256Bgra_Block32(palRaw); // PS2 样式
// 或: paletteBgra = ImageUtils.BuildPaletteBgraFromRgba(palRaw, 256, applyPs2AlphaFix: true);

s.Position = pixelDataOffset;

var bmp = ImageUtils.CreateArgbBitmap(width, height, out var bd, out int stride);
var idxRow = new byte[width];
var row    = new byte[width * 4];

try
{
    for (int y = 0; y < height; y++)
    {
        s.ReadExactly(idxRow, 0, idxRow.Length);
        ImageUtils.ConvertRowIndexed8ToBgra(idxRow, row, width, paletteBgra);
        ImageUtils.CopyRowToBitmap(bd, y, row, stride);
    }
    return bmp;
}
catch
{
    bmp.Dispose();
    return null;
}
finally
{
    ImageUtils.UnlockBitmap(bd, bmp);
}
```

4bpp 同理，用 `ConvertRowIndexed4ToBgra` 和 `(width+1)/2` 的 packed 行长度。

### 3.2 16bpp（RGB555 / 1555 / 4444）

直接用对应的行转换函数即可：

```csharp
// 假设像素起点在 pixelDataOffset, 格式为 RGB555
s.Position = pixelDataOffset;
var bmp = ImageUtils.CreateArgbBitmap(width, height, out var bd, out int stride);
var rowSrc = new byte[width * 2];
var row    = new byte[width * 4];

try
{
    for (int y = 0; y < height; y++)
    {
        s.ReadExactly(rowSrc, 0, rowSrc.Length);
        ImageUtils.ConvertRowRgb555ToBgra(rowSrc, row, width);
        ImageUtils.CopyRowToBitmap(bd, y, row, stride);
    }
    return bmp;
}
catch
{
    bmp.Dispose();
    return null;
}
finally
{
    ImageUtils.UnlockBitmap(bd, bmp);
}
```

1555 / 4444 同理使用 `ConvertRowArgb1555ToBgra` / `ConvertRowArgb4444ToBgra`。

### 3.3 24bpp / 32bpp 直读

- 24bpp RGB / BGR：
  - `ConvertRowRgb24ToBgra`
  - `ConvertRowBgr24ToBgra`
- 32bpp RGBA：
  - `ConvertRowRgba32ToBgra`
  - 或带 PS2 Alpha：`ConvertRowRgba32ToBgraWithPs2Alpha`

---

## 4. 总结给“0 基础 AI”的一句话

> 写图片插件 =「按已知结构读出宽高和像素数据 → 创建 32bpp 位图 → 用 ImageUtils 把每行源像素转成 BGRA 写进去」。  
> 
> 不要再判断“是不是这个格式”，那是插件工厂的工作；  
> 所有流操作用 StreamUtils，所有像素和调色板转换用 ImageUtils，  
> 解码失败就返回 null，不要关掉原始流。