- # 图片插件（Image）开发速记（流式版）

  目标

  - 匹配交给框架：extensions[] 或 magics[] 命中其一即可（列表可多项）。
  - 插件只做解码，统一流式接口：IImageHandler.TryDecode(Stream, ext)。

  注册

  - [ImagePlugin(id, extensions, magics)]
  - extensions: 扩展名数组（不带点），可空
  - magics: 文件头前缀数组（从偏移0匹配），可空
  - 至少保证二者之一非空
  - 魔术写法：ASCII（如 "DDS "、"PK\x03\x04"）或 "hex:50 4B 03 04"

  最小骨架

  ```
  using System.Drawing;
  using System.IO;
  using System.Text;
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
              // 直接解析；不要在这里做“是不是我”的魔术/扩展名判断
              // 解析失败：返回 null 或抛异常（数据损坏）
              // TODO: 用 BinaryReader 读结构并生成 Bitmap
              return null;
          }
      }
  }
  ```

  注意

  - 不要做二次文件头验证；匹配已在框架层完成。
  - 解码成功返回 Image；失败返回 null（UI 会回退到 GDI 或文本）或抛异常。
  - 如需从任意位置读：设置 stream.Position 再读；不要关闭传入的 stream。
  ## 性能建议
- 解码时优先使用 LockBits + Marshal.Copy，一次按行/整图拷贝像素；避免 Bitmap.SetPixel。
- 推荐统一输出 32bpp ARGB（BGRA 顺序），便于一次性拷贝。
- 仅按需读取：优先行缓冲（row buffer），不要把整图二次复制。