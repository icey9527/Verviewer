using System;
using System.Collections.Generic;
using System.IO;
using Verviewer.Core;
using Verviewer.Archives;
namespace Verviewer.Archives
{
    [ArchivePlugin(
        id: "FileSystemFolder",
        extensions: new[] { "dir" }
    )]
    internal sealed class FolderArchiveHandler : IArchiveHandler
    {
    public OpenedArchive Open(string folderPath)
    {
        // 确保文件夹存在
        if (!Directory.Exists(folderPath))
            throw new DirectoryNotFoundException($"文件夹不存在：{folderPath}");

        // 使用临时FileStream（满足接口要求），自动清理
        var tempFile = Path.GetTempFileName();
        var stream = new FileStream(tempFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None, 4096, FileOptions.DeleteOnClose);
        
        var entries = new List<ArchiveEntry>();
        // 初始只加载根目录下的直接子项（非递归）
        AddDirectoryEntries(folderPath, folderPath, entries, recursive: true);
        
        return new OpenedArchive(folderPath, stream, entries, this);
    }
        private void AddDirectoryEntries(string rootPath, string currentPath, List<ArchiveEntry> entries, bool recursive)
        {
            var relPath = Path.GetRelativePath(rootPath, currentPath).Replace('\\', '/');

            // 根目录的 relPath 通常是 "."，这里直接忽略掉，不加到 entries 里
            if (!string.IsNullOrEmpty(relPath) && relPath != ".")
            {
                entries.Add(new ArchiveEntry
                {
                    Path = relPath,
                    IsDirectory = true,
                    Size = 0,
                    UncompressedSize = 0
                });
            }

            foreach (var file in Directory.GetFiles(currentPath))
            {
                var fileInfo = new FileInfo(file);
                var fileRelPath = Path.GetRelativePath(rootPath, file).Replace('\\', '/');
                entries.Add(new ArchiveEntry
                {
                    Path = fileRelPath,
                    IsDirectory = false,
                    Size = (int)fileInfo.Length,
                    UncompressedSize = (int)fileInfo.Length
                });
            }

            if (recursive)
            {
                foreach (var dir in Directory.GetDirectories(currentPath))
                {
                    AddDirectoryEntries(rootPath, dir, entries, true);
                }
            }
        }

        public Stream OpenEntryStream(OpenedArchive archive, ArchiveEntry entry)
        {
            if (entry.IsDirectory)
                throw new InvalidOperationException("Cannot open stream for directory");

            var fullPath = Path.Combine(archive.SourcePath, entry.Path.Replace('/', Path.DirectorySeparatorChar));
            return new FileStream(fullPath, FileMode.Open, FileAccess.Read);
        }

        public void LoadSubdirectories(TreeView tree, TreeNode node, string rootPath)
        {
            if (node.Tag is not ArchiveEntry entry || !entry.IsDirectory)
                return;

            node.Nodes.Clear();
            var currentPath = Path.Combine(rootPath, entry.Path.Replace('/', Path.DirectorySeparatorChar));
            
            foreach (var dir in Directory.GetDirectories(currentPath))
            {
                var dirRelPath = Path.GetRelativePath(rootPath, dir).Replace('\\', '/');
                var dirNode = new TreeNode(Path.GetFileName(dir))
                {
                    Tag = new ArchiveEntry { Path = dirRelPath, IsDirectory = true },
                    ImageIndex = 0,
                    SelectedImageIndex = 0,
                    Nodes = { new TreeNode("Loading...") }
                };
                node.Nodes.Add(dirNode);
            }

            foreach (var file in Directory.GetFiles(currentPath))
            {
                var fileInfo = new FileInfo(file);
                var fileRelPath = Path.GetRelativePath(rootPath, file).Replace('\\', '/');
                var fileNode = new TreeNode(fileInfo.Name)
                {
                    Tag = new ArchiveEntry
                    {
                        Path = fileRelPath,
                        IsDirectory = false,
                        Size = (int)fileInfo.Length
                    },
                    ImageIndex = 1,
                    SelectedImageIndex = 1
                };
                node.Nodes.Add(fileNode);
            }
        }
    }
}