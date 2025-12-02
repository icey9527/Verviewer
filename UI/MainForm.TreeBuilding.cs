using System;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using Verviewer.Core;

namespace Verviewer.UI
{
    internal partial class MainForm
    {
        #region 构建左侧树（基于 _currentArchive.Entries）

        private void BuildTreeFromEntries()
        {
            _tree.BeginUpdate();
            _tree.Nodes.Clear();

            if (_currentArchive == null || _currentArchive.Entries.Count == 0)
            {
                _tree.EndUpdate();
                return;
            }

            // 根节点名字：文件名或文件夹名
            var srcPath = _currentArchive.SourcePath?
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) ?? string.Empty;

            var rootName = Path.GetFileName(srcPath);
            if (string.IsNullOrEmpty(rootName) || rootName == ".")
                rootName = srcPath;

            var rootNode = new TreeNode(rootName)
            {
                Tag = string.Empty // 根路径用空字符串
            };
            _tree.Nodes.Add(rootNode);

            UpdateStatus(CurrentPluginStatus, "正在构建目录树…");

            foreach (var entry in _currentArchive.Entries)
            {
                AddEntryToTree(rootNode, entry);
            }

            // 构建完以后统一排序
            SortTreeNode(rootNode);

            rootNode.Expand();
            _tree.EndUpdate();

            UpdateStatus(CurrentPluginStatus, string.Empty);
        }

        private void AddEntryToTree(TreeNode rootNode, ArchiveEntry entry)
        {
            if (string.IsNullOrEmpty(entry.Path))
                return;

            // 统一分隔符并清理掉开头的 "./"、"." 之类
            string fullPath = entry.Path.Replace('\\', '/').Trim();

            // 完全就是 "." 的，直接忽略
            if (fullPath == "." || fullPath == "./")
                return;

            // 去掉前导 "./"
            if (fullPath.StartsWith("./", StringComparison.Ordinal))
                fullPath = fullPath.Substring(2);

            fullPath = fullPath.Trim('/');
            if (string.IsNullOrEmpty(fullPath))
                return;

            // 拆分路径，过滤掉中间出现的 "."
            string[] parts = fullPath
                .Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Where(p => p != ".")
                .ToArray();

            if (parts.Length == 0)
                return;

            TreeNode node = rootNode;
            string currentPath = string.Empty;

            for (int i = 0; i < parts.Length; i++)
            {
                string part = parts[i];
                bool isLast = (i == parts.Length - 1);
                currentPath = string.IsNullOrEmpty(currentPath) ? part : (currentPath + "/" + part);

                TreeNode? child = null;
                foreach (TreeNode c in node.Nodes)
                {
                    if (isLast && c.Tag is ArchiveEntry ae &&
                        ae.Path.Equals(currentPath, StringComparison.OrdinalIgnoreCase))
                    {
                        child = c;
                        break;
                    }
                    if (!isLast && c.Tag is string s &&
                        s.Equals(currentPath, StringComparison.OrdinalIgnoreCase))
                    {
                        child = c;
                        break;
                    }
                }

                if (child == null)
                {
                    child = new TreeNode(part);
                    if (isLast && !entry.IsDirectory)
                    {
                        // 文件节点：Tag = ArchiveEntry
                        child.Tag = entry;
                    }
                    else
                    {
                        // 目录节点：Tag = 该目录的虚拟路径
                        child.Tag = currentPath;
                    }
                    node.Nodes.Add(child);
                }

                node = child;
            }
        }

        /// <summary>
        /// 递归排序：目录在前，文件在后；目录按名称排，文件按扩展名 + 文件名排
        /// </summary>
        private void SortTreeNode(TreeNode node)
        {
            if (node.Nodes.Count == 0)
                return;

            var children = node.Nodes.Cast<TreeNode>().ToList();
            children.Sort(CompareTreeNodes);

            node.Nodes.Clear();
            node.Nodes.AddRange(children.ToArray());

            foreach (var child in children)
            {
                SortTreeNode(child);
            }
        }

        private int CompareTreeNodes(TreeNode a, TreeNode b)
        {
            bool aIsDir = a.Tag is string;
            bool bIsDir = b.Tag is string;

            // 目录在前，文件在后
            if (aIsDir && !bIsDir) return -1;
            if (!aIsDir && bIsDir) return 1;

            // 两个都是文件
            if (a.Tag is ArchiveEntry ae && b.Tag is ArchiveEntry be)
            {
                string extA = Path.GetExtension(ae.Path);
                string extB = Path.GetExtension(be.Path);
                int cmpExt = string.Compare(extA, extB, StringComparison.OrdinalIgnoreCase);
                if (cmpExt != 0) return cmpExt;

                // 如果将来 ArchiveEntry 有 Size，可以在这里加入按大小排序
                return string.Compare(Path.GetFileName(ae.Path),
                                      Path.GetFileName(be.Path),
                                      StringComparison.OrdinalIgnoreCase);
            }

            // 两个都是目录：按显示文本排
            return string.Compare(a.Text, b.Text, StringComparison.OrdinalIgnoreCase);
        }

        #endregion
    }
}