using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using Verviewer.Core;
using Verviewer.Images;

namespace Verviewer.UI
{
    internal partial class MainForm
    {
        #region 公共提取逻辑（菜单 / 右键共用）

        /// <summary>
        /// 对指定的 archive + entries 执行提取操作。
        /// nestedRootFolder 用于嵌套封包：把内容放到这个子文件夹下。
        /// </summary>
        private async Task ExtractEntriesFromArchiveWithOptionsAsync(
            OpenedArchive archive,
            List<ArchiveEntry> entries,
            string? nestedRootFolder = null)
        {
            if (entries.Count == 0)
            {
                MessageBox.Show(this, "没有可提取的文件。", "提示",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            using var optForm = new ExtractOptionsForm();
            if (optForm.ShowDialog(this) != DialogResult.OK)
                return;

            var exts = optForm.Extensions;
            bool excludeMode = optForm.ExcludeMode;
            bool convertImagesToPng = optForm.ConvertImagesToPng;

            using var fbd = new FolderBrowserDialog
            {
                Description = "选择提取文件的目标文件夹"
            };
            if (fbd.ShowDialog(this) != DialogResult.OK)
                return;

            string targetRoot = fbd.SelectedPath;

            int total = entries.Count;
            int done = 0;

            _menu.Enabled = false;
            _tree.Enabled = false;
            UpdateStatus(CurrentPluginStatus, $"0 / {total}");

            try
            {
                await Task.Run(() =>
                {
                    var extSet = new HashSet<string>(
                        exts.Select(s => s.ToLowerInvariant()),
                        StringComparer.OrdinalIgnoreCase);

                    foreach (var entry in entries)
                    {
                        string relPath = entry.Path.Replace('/', Path.DirectorySeparatorChar);
                        if (!string.IsNullOrEmpty(nestedRootFolder))
                        {
                            relPath = Path.Combine(nestedRootFolder, relPath);
                        }

                        string destPath = Path.Combine(targetRoot, relPath);
                        string? destDir = Path.GetDirectoryName(destPath);
                        if (!string.IsNullOrEmpty(destDir))
                            Directory.CreateDirectory(destDir);

                        string ext = Path.GetExtension(entry.Path).TrimStart('.').ToLowerInvariant();

                        bool match;
                        if (extSet.Count == 0)
                        {
                            match = !excludeMode;
                        }
                        else
                        {
                            bool inSet = extSet.Contains(ext);
                            match = excludeMode ? !inSet : inSet;
                        }

                        if (!match)
                        {
                            done++;
                            if (done % 50 == 0)
                            {
                                int d = done, t = total;
                                Invoke((Action)(() => UpdateStatus(CurrentPluginStatus, $"{d} / {t}")));
                            }
                            continue;
                        }

                        bool written = false;

                        if (convertImagesToPng)
                        {
                            try
                            {
                                written = TryConvertEntryToPng(archive, entry, destPath, ext);
                            }
                            catch
                            {
                                written = false;
                            }
                        }

                        if (!written)
                        {
                            try
                            {
                                using (var s = archive.Handler.OpenEntryStream(archive, entry))
                                using (var outFs = new FileStream(destPath, FileMode.Create, FileAccess.Write))
                                {
                                    s.CopyTo(outFs);
                                }
                            }
                            catch (Exception ex)
                            {
                                Invoke((Action)(() =>
                                {
                                    MessageBox.Show(this,
                                        $"写入文件失败：\n{destPath}\n{ex.Message}",
                                        "提取错误",
                                        MessageBoxButtons.OK,
                                        MessageBoxIcon.Warning);
                                }));
                            }
                        }

                        done++;
                        if (done % 50 == 0)
                        {
                            int d = done, t = total;
                            Invoke((Action)(() => UpdateStatus(CurrentPluginStatus, $"{d} / {t}")));
                        }
                    }

                    Invoke((Action)(() => UpdateStatus(CurrentPluginStatus, $"{done} / {total}")));
                });

                MessageBox.Show(this, "提取完成。", "完成",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            finally
            {
                _menu.Enabled = true;
                _tree.Enabled = true;
                UpdateStatus(CurrentPluginStatus, string.Empty);
            }
        }

        private bool TryConvertEntryToPng(
            OpenedArchive archive,
            ArchiveEntry entry,
            string destPath,
            string extWithoutDot)
        {
            Image? img = null;

            // 先用头部挑插件（只读少量头）
            byte[] head;
            int headLen = Math.Max(16, PluginFactory.MaxImageHeaderLength);
            using (var sh = archive.Handler.OpenEntryStream(archive, entry))
            {
                head = new byte[headLen];
                int r = sh.Read(head, 0, head.Length);
                if (r < head.Length) Array.Resize(ref head, r);
            }

            // 按权重排好的候选图片插件，逐个尝试
            var imgTypes = PluginFactory.ResolveImageTypes("." + extWithoutDot, head).ToList();
            foreach (var imgType in imgTypes)
            {
                object? obj;
                try
                {
                    obj = Activator.CreateInstance(imgType);
                }
                catch
                {
                    continue;
                }

                if (obj is not IImageHandler ih)
                    continue;

                try
                {
                    using var s = archive.Handler.OpenEntryStream(archive, entry);
                    img = ih.TryDecode(s, "." + extWithoutDot);
                }
                catch
                {
                    img = null;
                }

                if (img != null)
                    break;
            }

            if (img == null)
            {
                try
                {
                    using var s = archive.Handler.OpenEntryStream(archive, entry);
                    var gdi = Image.FromStream(s, useEmbeddedColorManagement: true, validateImageData: true);
                    img = gdi;
                }
                catch
                {
                    img = null;
                }
            }

            if (img == null)
                return false;

            string pngPath = Path.ChangeExtension(destPath, ".png");
            Directory.CreateDirectory(Path.GetDirectoryName(pngPath)!);
            img.Save(pngPath, System.Drawing.Imaging.ImageFormat.Png);
            img.Dispose();

            return true;
        }

        #endregion

        #region 顶部菜单：提取当前封包的所有文件（仅封包模式）

        private async void ExtractMenu_Click(object? sender, EventArgs e)
        {
            if (_currentArchive == null)
            {
                MessageBox.Show(this, "还没有打开任何封包。", "无法提取",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            // 文件夹模式下，这个菜单是禁用的（OpenFolderAsArchive 里控制）
            var entries = _currentArchive.Entries
                .Where(x => !x.IsDirectory)
                .ToList();

            await ExtractEntriesFromArchiveWithOptionsAsync(_currentArchive, entries, null);
        }

        #endregion

        #region 树右键：提取当前选中文件 / 文件夹（自动判断）

        private async void TreeContext_ExtractSelected_Click(object? sender, EventArgs e)
        {
            if (_currentArchive == null)
                return;

            var node = _tree.SelectedNode;
            if (node == null)
            {
                MessageBox.Show(this, "请先选中一个文件或文件夹。", "提示",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            // 1) 文件节点
            if (node.Tag is ArchiveEntry fileEntry && !fileEntry.IsDirectory)
            {
                // 如果是“文件夹模式”，并且这个文件本身是一个封包文件：
                //  → 优先尝试把它当作封包，提取里面的内容。
                if (await TryExtractNestedArchiveForFileAsync(fileEntry))
                    return;

                // 否则，就只提取这个文件本身
                await ExtractEntriesFromArchiveWithOptionsAsync(
                    _currentArchive,
                    new List<ArchiveEntry> { fileEntry },
                    null);
                return;
            }

            // 2) 文件夹节点：根据路径前缀找出所有子文件
            string? dirPath = null;

            if (node.Tag is string s)
                dirPath = s;
            else if (node.Tag is ArchiveEntry dirEntry && dirEntry.IsDirectory)
                dirPath = dirEntry.Path;

            if (dirPath == null)
            {
                MessageBox.Show(this, "请选择要提取的文件或文件夹。", "提示",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            List<ArchiveEntry> entries;
            if (string.IsNullOrEmpty(dirPath))
            {
                // 根节点：等价于“当前封包全部文件”
                entries = _currentArchive.Entries
                    .Where(x => !x.IsDirectory)
                    .ToList();
            }
            else
            {
                string prefix = dirPath.TrimEnd('/', '\\');
                entries = _currentArchive.Entries
                    .Where(x => !x.IsDirectory &&
                                (x.Path.Equals(prefix, StringComparison.OrdinalIgnoreCase) ||
                                 x.Path.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase) ||
                                 x.Path.StartsWith(prefix + "\\", StringComparison.OrdinalIgnoreCase)))
                    .ToList();
            }

            if (entries.Count == 0)
            {
                MessageBox.Show(this, "这个文件夹里没有可提取的文件。", "提示",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            await ExtractEntriesFromArchiveWithOptionsAsync(_currentArchive, entries, null);
        }

        /// <summary>
        /// 在“文件夹模式”下，尝试把这个文件当作封包打开并提取其中内容。
        /// 成功返回 true，失败（不是封包或打开失败）返回 false。
        /// </summary>
        private async Task<bool> TryExtractNestedArchiveForFileAsync(ArchiveEntry entry)
        {
            if (_currentArchive == null)
                return false;

            // 只有 FolderArchiveHandler 才有磁盘实际路径
            if (!(_currentArchive.Handler is Verviewer.Archives.FolderArchiveHandler))
                return false;

            string rootFolder = _currentArchive.SourcePath;
            string relPath = entry.Path.Replace('/', Path.DirectorySeparatorChar);
            string fullPath = Path.Combine(rootFolder, relPath);

            if (!File.Exists(fullPath))
                return false;

            OpenedArchive? nested = null;
            try
            {
                using (var fsProbe = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    var types = PluginFactory.ResolveArchiveTypes(fullPath, fsProbe).ToList();
                    if (types.Count == 0)
                        return false;

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
                            nested = handler.Open(fullPath);
                            break;
                        }
                        catch (Exception ex)
                        {
                            lastError = ex;
                        }
                    }

                    if (nested == null)
                        return false;
                }

                var nestedEntries = nested.Entries.Where(x => !x.IsDirectory).ToList();
                if (nestedEntries.Count == 0)
                    return false;

                string folderName = Path.GetFileNameWithoutExtension(fullPath);
                await ExtractEntriesFromArchiveWithOptionsAsync(
                    nested,
                    nestedEntries,
                    folderName);

                return true;
            }
            catch
            {
                return false;
            }
            finally
            {
                nested?.Dispose();
            }
        }

        #endregion
    }
}