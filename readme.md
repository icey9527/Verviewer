```markdown
GPT5模仿 garbro写的玩意儿，将支持一些我感兴趣的格式。下面这个readme.md，也是它写的，我本人不会C#😃


---

### 1. 接口规格（不要改）

```csharp
// ArchiveEntry：封包中的一个条目（文件或目录）
namespace Verviewer.Core
{
    internal sealed class ArchiveEntry
    {
        public string Path { get; set; } = "";  // 统一用 '/' 分隔，例如 "folder/file.bin"
        public bool IsDirectory { get; set; }   // 目录=true，普通文件=false
        public long Offset { get; set; }        // 在封包文件中的偏移（字节）
        public int Size { get; set; }           // 实际存储大小（压缩后）
        public int UncompressedSize { get; set; } // 解压后大小；无压缩可等于 Size
    }
}
```

```csharp
// OpenedArchive：已经打开的封包
namespace Verviewer.Core
{
    internal sealed class OpenedArchive : IDisposable
    {
        public string SourcePath { get; }
        public FileStream Stream { get; }
        public IReadOnlyList<ArchiveEntry> Entries { get; }
        public IArchiveHandler Handler { get; }

        public OpenedArchive(
            string sourcePath,
            FileStream stream,
            IReadOnlyList<ArchiveEntry> entries,
            IArchiveHandler handler)
        {
            SourcePath = sourcePath;
            Stream = stream;
            Entries = entries;
            Handler = handler;
        }

        public void Dispose()
        {
            Stream.Dispose();
        }
    }
}
```

```csharp
// 封包处理插件接口（实现这个）
namespace Verviewer.Core
{
    internal interface IArchiveHandler
    {
        // 打开封包文件，解析出条目列表，返回 OpenedArchive。
        OpenedArchive Open(string archivePath);

        // 打开单个条目的数据流。
        // 对目录条目应该抛异常或直接不支持。
        System.IO.Stream OpenEntryStream(OpenedArchive archive, ArchiveEntry entry);
    }
}
```

```csharp
// 封包插件标记（已经内置，无需修改）
// 用法见后面的骨架。
namespace Verviewer.Core
{
    [AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
    internal sealed class ArchivePluginAttribute : Attribute
    {
        public string ArchiveId { get; }
        public string[] Extensions { get; }
        public byte[] MagicBytes { get; }
        public string[] PreferredImageIds { get; }

        public ArchivePluginAttribute(
            string archiveId,
            string[] extensions,
            string magic,
            string? preferredImageId = null)
        {
            ArchiveId = archiveId;
            Extensions = extensions ?? Array.Empty<string>();
            MagicBytes = ParseMagic(magic);
            PreferredImageId = preferredImageId;
        }

        // ParseMagic 实现略，工程里已有
    }
}
```

---

### 2. 封包插件骨架（照这个填）

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using Verviewer.Core;

namespace Verviewer.Archives
{
    [ArchivePlugin(
        archiveId: "My Archive Format",        // 例如 "ARTDINK DAT"
        extensions: new[] { "dat" },           // 不带点，比如 "dat"、"pak"
        magic: "PIDX0",                        // 头部魔数，可以是 "TEXT" 或 "\x50\x49\x44\x58\x30"
        preferredImageId: "agi"                // 可选：默认用哪个图片插件解图
    )]
    internal sealed class MyArchiveHandler : IArchiveHandler
    {
        public OpenedArchive Open(string archivePath)
        {
            // 1) 打开文件
            var fs = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var br = new BinaryReader(fs);

            // 2) 检查魔数（根据你的格式改）
            fs.Position = 0;
            byte[] magic = br.ReadBytes(5);
            if (magic.Length < 5 || magic[0] != 'P' || magic[1] != 'I' || magic[2] != 'D')
            {
                br.Dispose();
                fs.Dispose();
                throw new InvalidDataException("Not a valid MYARCH file.");
            }

            // 3) 解析索引，构造 entries 列表
            var entries = new List<ArchiveEntry>();

            // 示例：假设有 count 个固定大小索引，从某个位置开始
            // fs.Position = indexStart;
            // for (int i = 0; i < count; i++)
            // {
            //     long offset = br.ReadInt64();
            //     int size   = br.ReadInt32();
            //     string name = ...;
            //
            //     entries.Add(new ArchiveEntry
            //     {
            //         Path = name.Replace('\\','/'),
            //         IsDirectory = false,
            //         Offset = offset,
            //         Size = size,
            //         UncompressedSize = size
            //     });
            // }

            br.Dispose();

            // 4) 返回 OpenedArchive（fs 不要关，由 OpenedArchive 管）
            return new OpenedArchive(archivePath, fs, entries, this);
        }

        public Stream OpenEntryStream(OpenedArchive archive, ArchiveEntry entry)
        {
            if (entry.IsDirectory)
                throw new InvalidOperationException("Directory entries have no data stream.");

            var fs = archive.Stream;
            fs.Position = entry.Offset;

            byte[] buf = new byte[entry.Size];
            int read = fs.Read(buf, 0, buf.Length);
            if (read < buf.Length)
                Array.Resize(ref buf, read);

            // 如有压缩，可在这里解压；否则直接包装成 MemoryStream
            return new MemoryStream(buf, writable: false);
        }
    }
}
```

要点：

1. 封包插件类必须：
   - `internal sealed class XxxArchiveHandler : IArchiveHandler`
   - 带 `[ArchivePlugin(...)]` 标记，扩展名不带点。
2. `Open` 里：
   - 自己打开 `FileStream`；
   - 解析头、索引，填 `List<ArchiveEntry>`；
   - 返回 `new OpenedArchive(archivePath, fs, entries, this);`（`fs` 不要提前关闭）。
3. `OpenEntryStream` 里：
   - 用 `archive.Stream` + `entry.Offset` / `entry.Size` 读出原始数据；
   - 如需要解压，先解压再放进 `MemoryStream`；
   - 失败可以抛异常或让调用方处理，但**不要关 `archive.Stream`**。


图片插件写法（格式固定）

using System;
using System.Drawing;
using Verviewer.Core;

namespace Verviewer.Images
{
    [ImagePlugin(
        id: "任意唯一字符串ID",
        extensions: new[] { "扩展名（不带点）" } // 例如 "agi"、"tex"
    )]
    internal sealed class XxxImageHandler : IImageHandler
    {
        public Image? TryDecode(byte[] data, string extension)
        {
            // 1) 基本检查
            if (data == null || data.Length < 头最小长度) return null;
            if (!extension.EndsWith(".扩展名", StringComparison.OrdinalIgnoreCase)) return null;

            // 2) 检查魔数/头，不符合直接 return null;
            // if (!IsMyFormat(data)) return null;

            try
            {
                // 3) 解析宽/高/bpp/像素偏移...
                // 4) new Bitmap(...) 解码像素
                // return bmp;
            }
            catch
            {
                // 解析失败统一返回 null，让框架去试别的插件
                return null;
            }
        }
    }
}
```

约定（最重要的三条）：

1. 不是自己格式 / 解析失败：**返回 null，不抛异常**。  
2. `extension` 带点（比如 `".agi"`），`extensions` 里是不带点（比如 `"agi"`）。  
3. 只需要解出一张 `Bitmap` 即可，框架负责显示 / 保存。