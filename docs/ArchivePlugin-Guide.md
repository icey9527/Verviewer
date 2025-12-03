# 封包插件（Archive）开发速记（性能优化版）

## 目标

- 插件只“解析封包结构并暴露条目”；文件匹配（扩展名 / 魔术）统一由框架完成。
- 插件内部不要再做“是不是我”的文件头判断。
- 索引解析走流式；条目访问尽量返回子流，避免额外拷贝和大块内存占用。

---

## 注册规则

- 使用特性：

  [ArchivePlugin(id, extensions, magics)]

- `extensions`：扩展名数组（不带点），可为 null
- `magics`：魔术数组（从文件开头前缀匹配），可为 null
- 至少保证 `extensions` 或 `magics` 其中之一非空
- 魔术写法两种：
  - 直接写 ASCII，如 `"PK\x03\x04"`、`"DDS "`
  - 或 `"hex:50 4B 03 04"`

---

## 最小骨架（推荐写法）

```csharp
using System.IO;
using System.Collections.Generic;
using Verviewer.Core;

namespace Verviewer.Archives
{
    [ArchivePlugin(
        id: "My Archive",
        extensions: new[] { "dat", "pak" },
        magics: new[] { "PIDX0" }
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
                options: FileOptions.RandomAccess
            );

            var entries = new List<ArchiveEntry>();

            try
            {
                // 这里直接解析封包结构，填充 entries
                // 不要再做扩展名 / 魔术判断（框架已完成选择）

                entries.Add(new ArchiveEntry
                {
                    Path = name.Replace('\\', '/'),
                    Offset = offset,
                    Size = size,
                    IsDirectory = isDir
                });

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
                throw new InvalidOperationException("目录没有数据流");

            arc.Stream.Position = entry.Offset;
            return new SubReadStream(arc.Stream, entry.Offset, entry.Size);
        }
    }
}
```

---

## 行为约定

- 解析失败 = 数据损坏：直接抛异常即可，不要通过“返回 null / 0 条目”来表达“不是我”。
- `ArchiveEntry.Path`：
  - 始终使用 `/` 作为路径分隔符；
  - 目录条目设置 `IsDirectory = true`，大小可为 0。
- `OpenedArchive` 托管底层 `FileStream`：
  - 在 `Open` 成功返回后，插件不要主动关闭 `fs`；
  - 在解析失败的 `catch` 里记得关闭后再抛出。

---

## 性能与内存建议

### 1. 文件打开

- 一律使用：

  `new FileStream(path, ..., bufferSize: 65536, options: FileOptions.RandomAccess)`

- 不要在插件内部再包一层 `BufferedStream`，如有需要交给调用方决定。

### 2. 索引解析

- 只在 `Open` 里做“索引级别”的读取和（必要时的）解压。
- 对索引数据：
  - 如果索引本身压缩（如 LZSS），只解压索引那一小块；
  - 解压逻辑可以调用公共的解压核心（例如某个 LZSS core），但不是强制要求：
    - 如果多种格式共享相同算法（比如标准 LZSS 变体），可以抽到公共 helper 里；
    - 如果是高度定制的变种，就留在各自插件中即可。

### 3. 条目访问（OpenEntryStream）

- 优先返回“子流”而不是 `MemoryStream`：
  - 标准做法：`new SubReadStream(arc.Stream, entry.Offset, entry.Size)`；
  - 这样只有在真正读取条目时才访问底层文件，不会提前把整个条目读入内存。
- 仅在以下情况考虑返回 `MemoryStream`：
  - 条目内容必须经过解密 / 解压，且上层需要对解压结果频繁 Seek；
  - 或者格式本身就是“小文件多”，一次性解压到内存对内存占用影响不大。
- 大文件策略（可选）：
  - “小文件”（比如 < 8–16 MB，具体看项目）：可以一次性解压到 `MemoryStream`；
  - “大文件”：可考虑自定义“仅支持顺序读取”的解压流，避免持有完整解压结果和压缩副本。

### 4. 压缩与解密的分层

- 封包级别的加密/压缩（例如：
  - PAK/DAT 头里的 XOR、checksum；
  - 容器级 LZSS 压缩块）
- 原则：
  - 在 Archive 插件中完成“还原出条目的原始文件内容”；
  - 在图片或其他上层插件中，只处理“文件本身的编码格式”（PNG/JPEG/自定义像素布局等）。
- 对压缩算法的组织方式：
  - 如果一个压缩算法在多个插件中复用（如同一套 GSWIN LZSS），可以抽出一个公共的核心函数（只负责 `readByte` / `writeByte`）；
  - 若某种压缩是为某一款游戏高度定制的（头、标志、特殊规则很多），可以把它完整留在该插件文件，不必强行公共化。

### 5. 禁止重复解压

- 一层只负责一层的解压：
  - Archive 插件：只解封包级压缩（得到原始文件）；
  - Image 插件：只解图像编码；
- 不要在 Archive 解过一次 LZSS，又在 Image 里对同一字节流再解一遍同样的 LZSS。

---