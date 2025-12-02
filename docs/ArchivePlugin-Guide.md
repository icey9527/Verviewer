# 封包插件（Archive）开发速记

目的

- 插件只“解析”，匹配由框架统一完成（扩展名列表 + 魔术列表，命中任一即可）。
- 插件内部不要再做“是不是我”的文件头判断。

注册规则

- 用特性 [ArchivePlugin(id, extensions, magics)]
- extensions: 扩展名数组（不带点），可空
- magics: 魔术数组（从文件开头前缀匹配），可空
- 至少确保 extensions 或 magics 有一个非空
- 魔术写法两种：
  - 直接写 ASCII，如 "PK\x03\x04"、"DDS "
  - 或 "hex:50 4B 03 04"

最小骨架



```
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
            var fs = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var entries = new List<ArchiveEntry>();

            // 这里直接解析结构，填充 entries
            // 不要再做魔术/扩展名判断（框架已完成选择）

            return new OpenedArchive(archivePath, fs, entries, this);
        }

        public Stream OpenEntryStream(OpenedArchive arc, ArchiveEntry entry)
        {
            arc.Stream.Position = entry.Offset;
            return new SubReadStream(arc.Stream, entry.Offset, entry.Size); // 你自己的子流实现
        }
    }
}
```

注意

- 解析失败表示数据坏了：抛异常即可（不要用“不是我”的返回值来分支）。
- ArchiveEntry.Path 用 “/” 作为分隔符；目录条目 IsDirectory=true。
- OpenedArchive 托管底层 FileStream，插件不要提前关闭它。
## 性能建议
- 解析索引走流式，只读必要字段；OpenEntryStream 返回子流，避免拷贝条目。
- 打开文件用 FileStream(..., bufferSize: 65536, options: FileOptions.RandomAccess)。