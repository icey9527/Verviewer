using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using Verviewer.Core;
using Verviewer.Images;
using Verviewer.Archives;

namespace Verviewer.UI
{
    internal partial class MainForm
    {
        void BtnOpen_Click(object? sender, EventArgs e)
        {
            using var ofd = new OpenFileDialog
            {
                Filter = "所有文件|*.*",
                Title = "选择封包文件"
            };
            if (ofd.ShowDialog(this) != DialogResult.OK) return;
            OpenArchive(ofd.FileName, fromNested: false);
        }

        void OpenArchive(string archivePath, bool fromNested)
        {
            using var fsProbe = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var types = PluginFactory.ResolveArchiveTypes(archivePath, fsProbe).ToList();
            if (types.Count == 0)
            {
                MessageBox.Show(this, "没有找到可以处理这个封包的插件。", "无法打开",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

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

                if (handler == null) continue;

                try
                {
                    opened = handler.Open(archivePath);
                    var attr = type.GetCustomAttribute<ArchivePluginAttribute>();
                    usedRuleName = attr?.Id ?? type.Name;
                    break;
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

            var oldArchive = _currentArchive;
            var oldTitle = Text;
            var oldRuleName = _currentArchiveRuleName;
            var oldExtractVisible = _menuExtractItem?.Visible ?? false;

            if (fromNested && oldArchive != null)
            {
                _archiveHistory.Push(new ArchiveSnapshot(oldArchive, oldTitle, oldRuleName, oldExtractVisible));
            }
            else
            {
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
            _currentDir = string.Empty;

            _originalImage?.Dispose();
            _originalImage = null;
            _picPreview.Image?.Dispose();
            _picPreview.Image = null;
            _lastPreviewTextData = null;
            _lastTextEntry = null;

            Text = $"Verviewer - {Path.GetFileName(archivePath)}";
            UpdateStatus(CurrentPluginStatus, string.Empty);

            RebuildEntryList();
            UpdateNavigationMenu();
        }

        void OpenFolder_Click(object? sender, EventArgs e)
        {
            using var fbd = new FolderBrowserDialog
            {
                Description = "选择要打开的文件夹"
            };
            if (fbd.ShowDialog(this) != DialogResult.OK) return;
            OpenFolderAsArchive(fbd.SelectedPath);
        }

        void OpenFolderAsArchive(string folderPath)
        {
            var handler = new FolderArchiveHandler();
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
            _currentDir = string.Empty;

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

            RebuildEntryList();
            UpdateNavigationMenu();
        }

        void MenuBack_Click(object? sender, EventArgs e)
        {
            if (_archiveHistory.Count == 0) return;
            if (_currentArchive == null) return;

            _currentArchive.Dispose();

            var snap = _archiveHistory.Pop();
            _currentArchive = snap.Archive;
            _currentArchiveRuleName = snap.RuleName;
            _currentImageHandlerName = null;
            _lastSelectedEntryPath = null;
            _currentDir = string.Empty;

            _originalImage?.Dispose();
            _originalImage = null;
            _picPreview.Image?.Dispose();
            _picPreview.Image = null;
            _lastPreviewTextData = null;
            _lastTextEntry = null;

            Text = snap.Title;
            UpdateStatus(CurrentPluginStatus, string.Empty);

            RebuildEntryList();
            UpdateNavigationMenu();
        }

        void UpdateNavigationMenu()
        {
            bool hasArchive = _currentArchive != null;
            bool canExtract = false;
            bool canBack = false;

            if (hasArchive)
            {
                canExtract = !(_currentArchive!.Handler is FolderArchiveHandler);
                canBack = _archiveHistory.Count > 0;
            }

            if (_menuExtractItem != null)
                _menuExtractItem.Visible = canExtract;
        }
    }
}