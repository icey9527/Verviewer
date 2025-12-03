# 文件：ImagePlugin_DevGuide.md

# 图片插件（Image）开发速记（流式 + 性能优化版）

## 目标

- 匹配由框架完成：扩展名 / 魔术命中其一即可。
- 插件只做“图像解码”，不关心封包、容器压缩或加密。
- 解码过程尽量：
  - 仅按需读取；
  - 避免中间大数组；
  - 直接写入最终 `Bitmap` 像素缓冲。

---

## 注册

- 使用特性：

  [ImagePlugin(id, extensions, magics)]

- `extensions`：扩展名数组（不带点），可为 null
- `magics`：文件头前缀数组（从偏移 0 匹配），可为 null
- 魔术写法：
  - ASCII 文本，如 `"DDS "`、`"IPG"`、`"BM"`、`"\x20\x32"`；
  - 或 `"hex:44 44 53 20"`。

---

## 最小骨架（推荐写法）

```csharp
using System.Drawing;
using System.IO;
using Verviewer.Core;

namespace Verviewer.Images
{
    [ImagePlugin(
        id: "dds",
        extensions: new[] { "dds" },
        magics: new[] { "DDS " }
    )]
    internal sealed class DdsImageHandler : IImageHandler
    {
        public Image? TryDecode(Stream stream, string? ext)
        {
            if (!stream.CanRead) return null;
            if (stream.CanSeek) stream.Position = 0;

            // 这里直接解析 DDS 结构并生成 Bitmap
            // 不要再做扩展名 / 魔术判断（框架已完成选择）

            return null;
        }
    }
}
```

---

## 行为约定

- 不要在插件内部再做“是不是我”的判断：
  - 不要再读取头部比对魔术或扩展名来分支。
- 解析失败时：
  - 可以返回 `null`（框架或 UI 会尝试其他插件/回退到 GDI 等）；
  - 或抛出异常表示“数据损坏”。
- 不要关闭传入的 `stream`：
  - 如需从头读取：`if (stream.CanSeek) stream.Position = 0;`
  - 只读取你需要的部分。

---

## 性能与内存建议

### 1. 输出格式统一

- 推荐统一输出 `Bitmap` 的 `PixelFormat.Format32bppArgb`：
  - 便于使用 `LockBits` + `Marshal.Copy`；
  - 在大多数绘制管线里兼容性最好。

### 2. 避免逐像素 API

- 禁用 `Bitmap.SetPixel` 等逐像素 GDI+ API（性能极差）。
- 推荐模式：
  - `var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);`
  - `var data = bmp.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);`
  - 每行维护一个 `byte[] row = new byte[width * 4];`
  - 填满一行后：

    `Marshal.Copy(row, 0, destPtr, row.Length);`

  - 最后 `bmp.UnlockBits(data);`

### 3. 行缓冲 / 按需读取

- 尽量使用“行缓冲 + 直接写入 Bitmap”的方式：
  - 未压缩格式（例如简单 RGBA、IPG 这类）：
    - 每次从 `stream` 读一行原始数据；
    - 做必要的通道转换（如 R,G,B,A → B,G,R,A）；
    - 写入 Bitmap 当前行；
  - RLE / LZSS 等格式：
    - 优先设计为“边解码边吐像素”，直接写入行缓冲，而不是先解压到整块大数组再二次遍历。
- 只有在格式设计本身要求完整缓冲时（比如强依赖随机访问压缩数据），才考虑整块读取。

### 4. 与封包 / 压缩层的分工

- 图片插件只关心“条目文件的内容是什么图像格式”：
  - 例如 GRP 自定义像素结构、TGA 头、某种自定义调色板等；
  - 不负责 PAK/DAT/GRP 容器级压缩或 XOR；
- 封包插件负责：
  - 针对条目做完 XOR / LZSS / 其它容器级解压；
  - 交给图片插件的 `Stream` 应该已经是“原始图像文件”。

### 5. 与标准格式的关系

- 标准格式（PNG/BMP/JPEG/GIF/TIFF/ICO 等）：
  - 可以统一由一个 `StandardImageHandler` 插件处理，内部直接用 `Image.FromStream`；
  - 其他图片插件只需关心游戏自定义格式。
- 如果某个封包里条目本身就是标准格式（例如 dat 里是 PNG 文件）：
  - Archive 插件只需要还原出这份 PNG 内容；
  - 由标准图片插件解码，不要在 Archive 插件/自定义图片插件中重复做 PNG 解码。

### 6. 解压算法位置

- 对“通用、标准、在多处重用”的图像级压缩（例如同一套 LZSS 变体被多个图像格式共用）：
  - 可以考虑抽出一个公共的解压核心（例如 `LzssCore`），只关心 `readByte` / `writeByte`；
  - 各图片插件在外层处理自己的头部、标志位、XOR 等，然后调用核心。
- 对“高度自定义、只用在一个格式上的”压缩：
  - 可以直接留在该图片插件文件中，不强制抽取到公共目录。

---

## 故障处理

- 常规解析失败（格式不符 / 数据长度不对等）：
  - 返回 `null` 即可，框架会尝试其他插件或回退到通用处理。
- 严重数据损坏（明显越界、结构严重不合理）：
  - 可以抛出 `InvalidDataException` 或类似异常，方便调试问题资源。

---