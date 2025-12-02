using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using Verviewer.Core;
using Verviewer.Images;

namespace Verviewer.UI
{
    internal partial class MainForm
    {
        #region 打开封包（按需架构）

        private void BtnOpen_Click(object? sender, EventArgs e)
        {
            using var ofd = new OpenFileDialog
            {
                Filter = "所有文件|*.*",
                Title = "选择封包文件"
            };

            if (ofd.ShowDialog(this) != DialogResult.OK)
                return;

            // 顶层打开：不压入历史
            OpenArchive(ofd.FileName, fromNested: false);
        }

        private void OpenArchive(string archivePath, bool fromNested)
        {
            // 先解析候选插件
            using var fsProbe = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var types = PluginFactory.ResolveArchiveTypes(archivePath, fsProbe).ToList();

            if (types.Count == 0)
            {
                MessageBox.Show(this, "没有找到可以处理这个封包的插件。", "无法打开",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            // 先尝试打开新封包，成功后再替换当前
            OpenedArchive? opened = null;
            string? usedRuleName = null;
            Exception? lastError = null;

            foreach (var type in types)
            {
                IArchiveHandler? handler;
                try
                {
                    handler = Activator.CreateInstance(type) as IArchiveHandler;
                }
                catch
                {
                    continue;
                }

                if (handler == null)
                    continue;

                try
                {
                    opened = handler.Open(archivePath);

                    var attr = type.GetCustomAttribute<ArchivePluginAttribute>();
                    usedRuleName = attr?.Id ?? type.Name;

                    break; // 成功
                }
                catch (Exception ex)
                {
                    lastError = ex;
                }
            }

            if (opened == null)
            {
                var msg = "所有候选插件都无法打开这个封包。";
                if (lastError != null)
                    msg += "\n最后一个错误信息：\n" + lastError.Message;

                MessageBox.Show(this, msg, "无法打开",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            // 成功：根据 fromNested 决定是否把旧封包压入历史
            var oldArchive = _currentArchive;
            var oldTitle = Text;
            var oldRuleName = _currentArchiveRuleName;
            var oldExtractVisible = _menuExtractItem?.Visible ?? false;

            if (fromNested && oldArchive != null)
            {
                // 压入历史，但不 Dispose，方便“返回”
                _archiveHistory.Push(new ArchiveSnapshot(oldArchive, oldTitle, oldRuleName, oldExtractVisible));
            }
            else
            {
                // 顶层打开：清空历史，并释放之前所有上下文
                while (_archiveHistory.Count > 0)
                {
                    var snap = _archiveHistory.Pop();
                    snap.Archive.Dispose();
                }

                oldArchive?.Dispose();
            }

            _currentArchive = opened;
            _currentArchiveRuleName = usedRuleName ?? "Unknown";
            _currentImageHandlerName = null;
            _lastSelectedEntryPath = null;

            // 清理预览状态
            _originalImage?.Dispose();
            _originalImage = null;
            _picPreview.Image?.Dispose();
            _picPreview.Image = null;
            _lastPreviewTextData = null;
            _lastTextEntry = null;

            Text = $"Verviewer - {Path.GetFileName(archivePath)}";
            UpdateStatus(CurrentPluginStatus, string.Empty);

            BuildTreeFromEntries();
            UpdateNavigationMenu();
        }

        #endregion

        #region 打开文件夹作为“封包”

        private void OpenFolder_Click(object? sender, EventArgs e)
        {
            using var fbd = new FolderBrowserDialog
            {
                Description = "选择要打开的文件夹"
            };

            if (fbd.ShowDialog(this) != DialogResult.OK)
                return;

            OpenFolderAsArchive(fbd.SelectedPath);
        }

        private void OpenFolderAsArchive(string folderPath)
        {
            var handler = new Verviewer.Archives.FolderArchiveHandler();

            OpenedArchive? opened;
            try
            {
                opened = handler.Open(folderPath);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "打开文件夹失败：\n" + ex.Message, "错误",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            // 顶层打开：清空历史，并释放之前所有上下文
            var oldArchive = _currentArchive;
            while (_archiveHistory.Count > 0)
            {
                var snap = _archiveHistory.Pop();
                snap.Archive.Dispose();
            }
            oldArchive?.Dispose();

            _currentArchive = opened;
            _currentArchiveRuleName = "FileSystemFolder";
            _currentImageHandlerName = null;
            _lastSelectedEntryPath = null;

            // 清理预览状态
            _originalImage?.Dispose();
            _originalImage = null;
            _picPreview.Image?.Dispose();
            _picPreview.Image = null;
            _lastPreviewTextData = null;
            _lastTextEntry = null;

            var name = Path.GetFileName(folderPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (string.IsNullOrEmpty(name))
                name = folderPath;

            Text = $"Verviewer - [文件夹] {name}";
            UpdateStatus(CurrentPluginStatus, string.Empty);

            BuildTreeFromEntries();
            UpdateNavigationMenu();
        }

        #endregion

        #region 返回（只处理嵌套封包）

        private void MenuBack_Click(object? sender, EventArgs e)
        {
            if (_archiveHistory.Count == 0)
                return;
            if (_currentArchive == null)
                return;

            // 返回到上一个 Archive 快照
            _currentArchive.Dispose();

            var snap = _archiveHistory.Pop();
            _currentArchive = snap.Archive;
            _currentArchiveRuleName = snap.RuleName;
            _currentImageHandlerName = null;
            _lastSelectedEntryPath = null;

            // 清理预览状态
            _originalImage?.Dispose();
            _originalImage = null;
            _picPreview.Image?.Dispose();
            _picPreview.Image = null;
            _lastPreviewTextData = null;
            _lastTextEntry = null;

            Text = snap.Title;
            UpdateStatus(CurrentPluginStatus, string.Empty);

            BuildTreeFromEntries();
            UpdateNavigationMenu();
        }

        #endregion

        #region 在树中双击文件，尝试当封包再次打开（仅文件夹模式）

        private void Tree_NodeMouseDoubleClick(object? sender, TreeNodeMouseClickEventArgs e)
        {
            if (e.Node?.Tag is ArchiveEntry entry && !entry.IsDirectory)
            {
                TryOpenEntryAsArchive(entry);
            }
        }

        private void TryOpenEntryAsArchive(ArchiveEntry entry)
        {
            if (_currentArchive == null)
                return;

            // 只有在“文件夹作为封包”模式下才支持这种行为
            if (!(_currentArchive.Handler is Verviewer.Archives.FolderArchiveHandler))
                return;

            string rootFolder = _currentArchive.SourcePath;
            string relPath = entry.Path.Replace('/', Path.DirectorySeparatorChar);
            string fullPath = Path.Combine(rootFolder, relPath);

            if (!File.Exists(fullPath))
                return;

            // 先看一下有没有插件能处理这个文件
            try
            {
                using var fsProbe = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                var types = PluginFactory.ResolveArchiveTypes(fullPath, fsProbe).ToList();
                if (types.Count == 0)
                    return;
            }
            catch
            {
                return;
            }

            // 有插件能处理，作为“嵌套封包”打开：压入当前上下文
            OpenArchive(fullPath, fromNested: true);
        }

        #endregion

        #region 导航菜单状态更新

        /// <summary>
        /// 根据当前 _currentArchive / _archiveHistory 更新“提取 / 返回”菜单是否显示。
        /// </summary>
        private void UpdateNavigationMenu()
        {
            bool hasArchive = _currentArchive != null;
            bool canExtract = false;
            bool canBack = false;

            if (hasArchive)
            {
                // 提取：有封包且 Handler 不是文件夹
                canExtract = !(_currentArchive!.Handler is Verviewer.Archives.FolderArchiveHandler);

                // 返回：只要有嵌套历史就能返回
                canBack = _archiveHistory.Count > 0;
            }

            if (_menuExtractItem != null)
                _menuExtractItem.Visible = canExtract;

            if (_menuBackItem != null)
                _menuBackItem.Visible = canBack;
        }

        #endregion
    }
}