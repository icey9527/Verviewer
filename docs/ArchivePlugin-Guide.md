# 一、封包插件（Archive）开发指南（无头部判断版）

## 0. 角色分工 & 原则

- 框架负责：
  - 根据文件扩展名 / 魔术（在 `ArchivePlugin` 上配置的）选出合适的封包插件；
  - 创建并调用对应的 `IArchiveHandler`。

- 封包插件只负责：
  - 按照该格式的**既定结构**解析索引、条目；
  - 按条目 `Offset` / `Size` 打开子流（`RangeStream` / `SubReadStream` 等）。

- **不要**在插件内部重复判断“是不是我”，例如：
  - 不要再读魔术后 `if (magic != "XYZ") return null;`
  - 不要通过“返回 0 条目/空列表”表达“不是我”。

解析失败 = 格式损坏或者数据不符，**直接抛异常**。

---

## 1. 注册规则：ArchivePlugin 特性

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using Verviewer.Core;
using Utils;

namespace Verviewer.Archives
{
    [ArchivePlugin(
        id: "My Archive Format",
        extensions: new[] { "dat", "pak" },      // 至少扩展或魔术一个非空
        magics:     new[] { "PIDX0" }           // 可以是 null
    )]
    internal sealed class MyArchiveHandler : IArchiveHandler
    {
        ...
    }
}
```

说明：

- `id`：封包格式名称。
- `extensions`：支持的扩展名数组（不含点），可以为 `null`。
- `magics`：文件头魔数字符串数组，可以为 `null`：
  - ASCII 写法：`"PIDX0"`, `"PACK"`;
  - 或十六进制写法：`"hex:50 49 44 58 30"`。
- 至少保证 `extensions` 或 `magics` 之一非空。

> 插件内部可以假定：**能走到这个插件，就已经被外层“判定为本格式”**。  
> 是否能解析成功，只取决于数据是否符合预期，而不是“是不是我”。

---

## 2. IArchiveHandler 标准骨架（推荐模板）

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using Verviewer.Core;
using Utils;

namespace Verviewer.Archives
{
    [ArchivePlugin(
        id: "My Archive Format",
        extensions: new[] { "pak" },
        magics:     new[] { "PIDX0" }
    )]
    internal sealed class MyArchiveHandler : IArchiveHandler
    {
        public OpenedArchive Open(string archivePath)
        {
            var fs = new FileStream(
                archivePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 65536,
                options: FileOptions.RandomAccess);

            var entries = new List<ArchiveEntry>();

            try
            {
                // 这里直接按“你定义的格式结构”解析，不再做“是不是我”的判断
                // 例如:
                //   - 读文件头部结构
                //   - 读取条目数量、索引表偏移等
                //   - 填充 entries 列表

                // 示例：假设前 4 字节是条目数，后面是每项 16 字节的索引
                fs.Position = 0;
                int count = fs.ReadInt32LEAt(0);    // 用 StreamUtils
                long indexOffset = 4;
                long fileSize = fs.Length;

                if (count < 0 || count > 1_000_000)
                    throw new InvalidDataException("Entry count out of range.");

                fs.Position = indexOffset;

                for (int i = 0; i < count; i++)
                {
                    // 假设索引结构: int offset; int size; 固定 256 字节名字
                    int off  = fs.ReadInt32LEAt(indexOffset + i * 264 + 0);
                    int size = fs.ReadInt32LEAt(indexOffset + i * 264 + 4);

                    // 固定 256 字节名字，0 截断
                    string name = fs.WithTemporarySeek(indexOffset + i * 264 + 8, st =>
                        st.ReadFixedString(256, System.Text.Encoding.UTF8));

                    if (string.IsNullOrEmpty(name))
                        throw new InvalidDataException("Empty entry name.");

                    if (off < 0 || size < 0 || (long)off + size > fileSize)
                        throw new InvalidDataException("Entry out of range.");

                    entries.Add(new ArchiveEntry
                    {
                        Path = name.Replace('\\', '/'),
                        Offset = off,
                        Size = size,
                        IsDirectory = false
                    });
                }

                return new OpenedArchive(archivePath, fs, entries, this);
            }
            catch
            {
                fs.Dispose();
                throw;
            }
        }

        public Stream OpenEntryStream(OpenedArchive arc, ArchiveEntry entry)
        {
            if (entry.IsDirectory)
                throw new InvalidOperationException("目录没有数据流。");

            // 最推荐：用 RangeStream / SubReadStream 返回子流，不一次性全部拷到内存
            return new RangeStream(arc.Stream, entry.Offset, entry.Size, leaveOpen: true);
        }
    }
}
```

---

## 3. 行为与异常约定（Archive）

- 解析失败 = 数据损坏 / 不符合结构：
  - **直接抛异常**（`InvalidDataException` 或其他），由上层统一捕获并给用户提示。
  - 不要通过“返回空 entries 列表/返回 null”来表达“不是我”。

- `ArchiveEntry.Path`：
  - 一律使用 `/` 作为路径分隔符：
    - 对：`"dir/file.bin"`
    - 错：`"dir\file.bin"`
  - 目录条目：
    - `IsDirectory = true`
    - `Size = 0`
    - 可设置 `UncompressedSize = 0`（如果你有用到）。

- 文件流生命周期：
  - `Open` 成功返回后，不要再手动关闭 `fs`；
  - 只在 `try` 中解析失败的 `catch` 分支里 `fs.Dispose(); throw;`。

---

## 4. 必用的公共工具

- `Utils.StreamUtils`：
  - `ReadExactly`, `ReadInt32LEAt`, `ReadUInt32LEAt`, `ReadFixedString`, `AlignPosition`, `WithTemporarySeek`, `EnsureSeekable` 等；
- `Utils.RangeStream` / `SubReadStream`：
  - 对应条目数据的子流，避免每次都 `MemoryStream` 复制大块数据；
- `Utils/compress` 下的公共解压（如 `LZSS`, `ArtdinkCompression` 等）：
  - 如果多个封包用同一套压缩算法，优先复用这里的核心，而不是在各插件里复制一份。

这些工具已经在现有封包插件中广泛使用（Artdink DAT, GSWIN PAK, Ikusabune IPF, SALA PFS 等），可以直接照着用。
