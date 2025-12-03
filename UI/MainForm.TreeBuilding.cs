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

            var srcPath = _currentArchive.SourcePath?
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) ?? string.Empty;

            var rootName = Path.GetFileName(srcPath);
            if (string.IsNullOrEmpty(rootName) || rootName == ".")
                rootName = srcPath;

            var rootNode = new TreeNode(rootName)
            {
                Tag = string.Empty
            };
            _tree.Nodes.Add(rootNode);

            UpdateStatus(CurrentPluginStatus, "正在构建目录树…");

            var dirNodes = new System.Collections.Generic.Dictionary<string, TreeNode>(StringComparer.OrdinalIgnoreCase)
            {
                [""] = rootNode
            };

            foreach (var entry in _currentArchive.Entries)
            {
                var normPath = NormalizePath(entry.Path);
                if (string.IsNullOrEmpty(normPath))
                    continue;

                if (entry.IsDirectory)
                {
                    EnsureDirectoryNode(normPath, dirNodes, rootNode);
                    continue;
                }

                int lastSlash = normPath.LastIndexOf('/');
                string dirPath, name;
                if (lastSlash >= 0)
                {
                    dirPath = normPath.Substring(0, lastSlash);
                    name = normPath.Substring(lastSlash + 1);
                }
                else
                {
                    dirPath = string.Empty;
                    name = normPath;
                }

                var parent = EnsureDirectoryNode(dirPath, dirNodes, rootNode);
                var node = new TreeNode(name)
                {
                    Tag = entry
                };
                parent.Nodes.Add(node);
            }

            SortTreeNode(rootNode);
            rootNode.Expand();
            _tree.EndUpdate();

            UpdateStatus(CurrentPluginStatus, string.Empty);
        }

        private static string NormalizePath(string path)
        {
            if (string.IsNullOrEmpty(path))
                return string.Empty;

            string p = path.Replace('\\', '/').Trim();
            if (p == "." || p == "./")
                return string.Empty;
            if (p.StartsWith("./", StringComparison.Ordinal))
                p = p.Substring(2);
            p = p.Trim('/');
            if (p.Length == 0)
                return string.Empty;
            var parts = p.Split('/', StringSplitOptions.RemoveEmptyEntries)
                         .Where(x => x != ".")
                         .ToArray();
            return parts.Length == 0 ? string.Empty : string.Join("/", parts);
        }

        private static TreeNode EnsureDirectoryNode(string dirPath,
            System.Collections.Generic.Dictionary<string, TreeNode> map,
            TreeNode rootNode)
        {
            if (map.TryGetValue(dirPath, out var node))
                return node;

            if (string.IsNullOrEmpty(dirPath))
                return rootNode;

            int lastSlash = dirPath.LastIndexOf('/');
            string parentPath;
            string name;
            if (lastSlash >= 0)
            {
                parentPath = dirPath.Substring(0, lastSlash);
                name = dirPath.Substring(lastSlash + 1);
            }
            else
            {
                parentPath = string.Empty;
                name = dirPath;
            }

            var parent = EnsureDirectoryNode(parentPath, map, rootNode);
            node = new TreeNode(name)
            {
                Tag = dirPath
            };
            parent.Nodes.Add(node);
            map[dirPath] = node;
            return node;
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

            if (aIsDir && !bIsDir) return -1;
            if (!aIsDir && bIsDir) return 1;

            if (a.Tag is ArchiveEntry ae && b.Tag is ArchiveEntry be)
            {
                string extA = Path.GetExtension(ae.Path);
                string extB = Path.GetExtension(be.Path);
                int cmpExt = string.Compare(extA, extB, StringComparison.OrdinalIgnoreCase);
                if (cmpExt != 0) return cmpExt;

                return string.Compare(Path.GetFileName(ae.Path),
                                      Path.GetFileName(be.Path),
                                      StringComparison.OrdinalIgnoreCase);
            }

            return string.Compare(a.Text, b.Text, StringComparison.OrdinalIgnoreCase);
        }

        #endregion
    }
}