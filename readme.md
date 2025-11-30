```markdown
GPT5模仿 garbro写的玩意儿，将支持一些我感兴趣的格式。下面这个readme.md，也是它写的，我本人不会C#😃

# Verviewer 开发指南

本项目由 GPT‑5 开发，核心目标是：**用尽量简单的代码实现可扩展的封包 / 图片解析插件系统**。  
下面只介绍如何开发自己的封包插件和图片插件。

---

## 目录结构简要

```text
Core/
  ArchiveRule.cs
  ArchiveConfigLoader.cs
  ArchiveHandler.cs       // IArchiveHandler 接口
  ArchiveEntry.cs
  OpenedArchive.cs
  IImageHandler.cs        // 图片插件接口
  PluginFactory.cs        // 插件工厂（按名字创建插件）

Archives/
  ARTDINK DAT.cs          // DatArchiveHandler（含 FSTS 嵌套）

Images/
  AgiImageHandler.cs      // AGI 图片解码插件

config/
  archives.csv            // 封包规则表
```

---

## 1. 封包规则表：`config/archives.csv`

每一行定义**如何识别一个封包，并用哪个插件解析**：

```csv
Extension,Magic,ArchiveId,PreferredImageId
dat,PIDX0,ARTDINK DAT,agi
```

字段含义：

- `Extension`：封包文件扩展名（不带点），例如 `dat`  
- `Magic`：魔数（文件头）
  - 支持 ASCII 文本，比如 `PIDX0`
  - 或十六进制：`\x50\x49\x44\x58\x30`
- `ArchiveId`：封包插件名字（任意字符串），例如 `ARTDINK DAT`
- `PreferredImageId`：图片插件名字（可选），例如 `agi`

当用户打开一个文件时，程序会：

1. 根据扩展名和文件头，匹配到某一行规则 `rule`；
2. 用 `rule.ArchiveId` 调用 `PluginFactory.CreateArchiveHandler(rule.ArchiveId)` 创建封包插件；
3. 打开封包，得到 `OpenedArchive` 和 `ArchiveEntry` 列表；
4. 左侧树用这些 Entry 的 `Path` 构建目录结构；
5. 右侧预览时，通过图片插件链解码单个文件。

---

## 2. 封包插件开发（Archives）

封包插件实现 `IArchiveHandler` 接口：

```csharp
// Core/ArchiveHandler.cs
namespace Verviewer.Core
{
    internal interface IArchiveHandler
    {
        OpenedArchive Open(string archivePath);
        Stream OpenEntryStream(OpenedArchive archive, ArchiveEntry entry);
    }
}
```

### 2.1 示例：自定义封包插件 `MyGameDatHandler`

1. 在 `config/archives.csv` 添加一行（例）：

```csv
dat2,MYHD,MYGAME DAT,agi
```

2. 在 `Archives/` 下新建 `MYGAME DAT.cs`：

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Verviewer.Core;

namespace Verviewer.Archives
{
    internal class MyGameDatHandler : IArchiveHandler
    {
        public OpenedArchive Open(string archivePath)
        {
            var fs = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var br = new BinaryReader(fs, Encoding.ASCII, leaveOpen: true);

            // TODO: 检查魔数、解析索引
            // 例如：读取 entryCount，然后循环读取每条：
            //   - name/path
            //   - offset
            //   - size
            //   - uncompressedSize

            var entries = new List<ArchiveEntry>();

            // 示例：构造一个假的条目（实际请按格式解析）
            /*
            entries.Add(new ArchiveEntry
            {
                Path = "foo/bar.bin",
                IsDirectory = false,
                Offset = 0x1234,
                Size = 0x1000,
                UncompressedSize = 0x2000
            });
            */

            br.Dispose();
            return new OpenedArchive(archivePath, fs, entries, this);
        }

        public Stream OpenEntryStream(OpenedArchive archive, ArchiveEntry entry)
        {
            if (entry.IsDirectory)
                throw new InvalidOperationException("目录没有数据流。");

            var fs = archive.Stream;
            fs.Position = entry.Offset;

            byte[] data = new byte[entry.Size];
            int read = fs.Read(data, 0, data.Length);
            if (read < data.Length)
                Array.Resize(ref data, read);

            // TODO: 若有压缩/加密，在这里做解码，然后返回 MemoryStream
            return new MemoryStream(data, writable: false);
        }
    }
}
```

3. 在 `Core/PluginFactory.cs` 中让工厂认识这个插件：

```csharp
public static IArchiveHandler? CreateArchiveHandler(string name)
    => name switch
    {
        "ARTDINK DAT" => new DatArchiveHandler(),
        "MYGAME DAT"  => new MyGameDatHandler(),
        _             => null
    };
```

**注意**：  
- 匹配用的就是 `ArchiveId` 字符串（如 `MYGAME DAT`），和源码文件名是否相同由你自己约定。  
- UI 不关心插件类名，只通过工厂得到 `IArchiveHandler` 实例。

---

## 3. 图片插件开发（Images）

图片插件实现 `IImageHandler` 接口：

```csharp
using System.Drawing;

namespace Verviewer.Core
{
    internal interface IImageHandler
    {
        string Id { get; }
        Image? TryDecode(byte[] data, string extension);
    }
}
```

UI 在预览时的策略非常简单：

1. 将文件数据 `data` 依次传给 `_imageHandlers` 里的每个插件调用 `TryDecode`
2. 任意插件返回非 null，即认为该文件是图片，直接显示为图片
3. 若所有插件失败，再由 GDI (`Image.FromStream`) 尝试识别常规格式（png/jpg/bmp 等）
4. 仍失败，则当文本显示

### 3.1 示例：AGI 图片插件（已有）

```csharp
using System;
using System.Drawing;
using Verviewer.Core;

namespace Verviewer.Images
{
    internal class AgiImageHandler : IImageHandler
    {
        public string Id => "agi";

        public Image? TryDecode(byte[] data, string extension)
        {
            // 简单示例：只处理 .agi
            if (!extension.Equals(".agi", StringComparison.OrdinalIgnoreCase))
                return null;

            // TODO: 按你之前的 Python/C 逻辑解析 header、bpp、palette、像素数据
            // 例如：
            // int width = ...
            // int height = ...
            // var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            // 填充 bmp 的像素...
            // return bmp;

            return null; // 解码失败返回 null
        }
    }
}
```

### 3.2 注册图片插件（工厂）

`Core/PluginFactory.cs` 示例：

```csharp
using System.Collections.Generic;
using Verviewer.Images;

namespace Verviewer.Core
{
    internal static class PluginFactory
    {
        public static IReadOnlyList<IImageHandler> CreateAllImageHandlers()
            => new IImageHandler[]
            {
                new AgiImageHandler()
                // 以后你有别的图片插件，就在这里多 new 一个
            };

        public static string? GetImagePluginName(IImageHandler handler)
            => handler switch
            {
                AgiImageHandler => "agi",
                _               => null
            };
    }
}
```

UI 会自动：

- 在预览时遍历 `IImageHandler` 列表，将文件数据依次交给每个插件尝试解码  
- 用 `GetImagePluginName` 将插件实例映射为字符串（例如 `"agi"`），在状态栏中显示

如果你以后加一个 `TexImageHandler`，只需：

- 在 `Images/` 下创建类实现 `IImageHandler`  
- 在 `CreateAllImageHandlers` 中返回它  
- 在 `GetImagePluginName` 中为它返回对应字符串（例如 `"tex"`）  

UI 无需任何改动。

---

## 4. 注意事项

- 所有封包插件必须是**按需读取**：  
  - `Open()` 中不要解压所有文件到磁盘，只解析索引即可；  
  - `OpenEntryStream()` 中才根据 `ArchiveEntry` 信息读取实际数据，必要时边读边解压。  
- 所有图片插件必须是**静默失败**：  
  - 不能抛异常或 MessageBox，解码失败时返回 null 即可；  
  - 由 UI 自动 fall-back 到其它插件或文本预览。  
- `archives.csv` 是 Verviewer 的“指令总表”，任何新封包格式都应该在其中增加一行，并在 `PluginFactory` 中映射到具体插件类。

---

```