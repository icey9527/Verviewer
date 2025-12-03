using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using Verviewer.Archives;
using Verviewer.Core;
using Verviewer.Images;

namespace Verviewer.UI
{
    internal partial class MainForm
    {
        async Task ExtractEntriesFromArchiveWithOptionsAsync(
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

            var extSet = new HashSet<string>(
                exts.Select(s => s.ToLowerInvariant()),
                StringComparer.OrdinalIgnoreCase);

            bool ShouldProcess(ArchiveEntry e)
            {
                string ext = Path.GetExtension(e.Path).TrimStart('.').ToLowerInvariant();
                if (extSet.Count == 0) return !excludeMode;
                bool inSet = extSet.Contains(ext);
                return excludeMode ? !inSet : inSet;
            }

            var workEntries = entries.Where(e => !e.IsDirectory && ShouldProcess(e)).ToList();
            if (workEntries.Count == 0)
            {
                MessageBox.Show(this, "没有匹配筛选条件的文件。", "提示",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            int total = workEntries.Count;
            int done = 0;

            _menu.Enabled = false;
            _entryList.Enabled = false;
            UpdateStatus(CurrentPluginStatus, $"0 / {total}");

            try
            {
                await Task.Run(() =>
                {
                    foreach (var entry in workEntries)
                    {
                        try
                        {
                            string relPath = entry.Path.Replace('/', Path.DirectorySeparatorChar);
                            if (!string.IsNullOrEmpty(nestedRootFolder))
                                relPath = Path.Combine(nestedRootFolder, relPath);

                            string destPath = Path.Combine(targetRoot, relPath);
                            string? destDir = Path.GetDirectoryName(destPath);
                            if (!string.IsNullOrEmpty(destDir))
                                Directory.CreateDirectory(destDir);

                            bool written = false;
                            if (convertImagesToPng)
                            {
                                try
                                {
                                    written = TryConvertEntryToPng(archive, entry, destPath);
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
                                    using var s = archive.Handler.OpenEntryStream(archive, entry);
                                    using var outFs = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536, FileOptions.SequentialScan);
                                    s.CopyTo(outFs);
                                }
                                catch (Exception ex)
                                {
                                    string pathCopy = destPath;
                                    Invoke((Action)(() =>
                                    {
                                        MessageBox.Show(this,
                                            $"写入文件失败：\n{pathCopy}\n{ex.Message}",
                                            "提取错误",
                                            MessageBoxButtons.OK,
                                            MessageBoxIcon.Warning);
                                    }));
                                }
                            }
                        }
                        finally
                        {
                            done++;
                            if (done % 50 == 0 || done == total)
                            {
                                int td = done, tt = total;
                                Invoke((Action)(() => UpdateStatus(CurrentPluginStatus, $"{td} / {tt}")));
                            }
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
                _entryList.Enabled = true;
                UpdateStatus(CurrentPluginStatus, string.Empty);
            }
        }

        bool TryConvertEntryToPng(OpenedArchive archive, ArchiveEntry entry, string destPath)
        {
            Image? img = null;
            try
            {
                img = TryDecodeImage(archive, entry, out _);
            }
            catch
            {
                img = null;
            }
            if (img == null) return false;

            string pngPath = Path.ChangeExtension(destPath, ".png");
            Directory.CreateDirectory(Path.GetDirectoryName(pngPath)!);
            img.Save(pngPath, System.Drawing.Imaging.ImageFormat.Png);
            img.Dispose();
            return true;
        }

        async void ExtractMenu_Click(object? sender, EventArgs e)
        {
            if (_currentArchive == null)
            {
                MessageBox.Show(this, "还没有打开任何封包。", "无法提取",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var entries = _currentArchive.Entries
                .Where(x => !x.IsDirectory)
                .ToList();

            await ExtractEntriesFromArchiveWithOptionsAsync(_currentArchive, entries, null);
        }

        async void EntryContext_ExtractSelected_Click(object? sender, EventArgs e)
        {
            if (_currentArchive == null) return;

            var selected = GetSelectedEntries();
            if (selected.Count == 0)
            {
                MessageBox.Show(this, "请先选中一个文件或文件夹。", "提示",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var files = new List<ArchiveEntry>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var entry in selected)
            {
                if (entry.IsDirectory)
                {
                    if (entry.Path.Length == 0)
                        continue;

                    string prefix = entry.Path.TrimEnd('/', '\\');
                    foreach (var ent in _currentArchive.Entries.Where(x => !x.IsDirectory))
                    {
                        string p = NormalizePath(ent.Path);
                        if (p.Equals(prefix, StringComparison.OrdinalIgnoreCase) ||
                            p.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase))
                        {
                            if (seen.Add(p))
                            {
                                files.Add(new ArchiveEntry
                                {
                                    Path = p,
                                    IsDirectory = false,
                                    Offset = ent.Offset,
                                    Size = ent.Size,
                                    UncompressedSize = ent.UncompressedSize
                                });
                            }
                        }
                    }
                }
                else
                {
                    string p = entry.Path;
                    if (seen.Add(p))
                        files.Add(entry);
                }
            }

            if (files.Count == 0)
            {
                MessageBox.Show(this, "没有可提取的文件。", "提示",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            if (_currentArchive.Handler is FolderArchiveHandler && selected.Count == 1 && !selected[0].IsDirectory)
            {
                if (await TryExtractNestedArchiveForFileAsync(selected[0]))
                    return;
            }

            await ExtractEntriesFromArchiveWithOptionsAsync(_currentArchive, files, null);
        }

        async Task<bool> TryExtractNestedArchiveForFileAsync(ArchiveEntry entry)
        {
            if (_currentArchive == null) return false;
            if (!(_currentArchive.Handler is FolderArchiveHandler)) return false;

            string rootFolder = _currentArchive.SourcePath;
            string relPath = entry.Path.Replace('/', Path.DirectorySeparatorChar);
            string fullPath = Path.Combine(rootFolder, relPath);
            if (!File.Exists(fullPath)) return false;

            OpenedArchive? nested = null;
            try
            {
                using (var fsProbe = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    var types = PluginFactory.ResolveArchiveTypes(fullPath, fsProbe).ToList();
                    if (types.Count == 0) return false;

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
                            nested = handler.Open(fullPath);
                            break;
                        }
                        catch (Exception ex)
                        {
                            lastError = ex;
                        }
                    }

                    if (nested == null) return false;
                }

                var nestedEntries = nested.Entries.Where(x => !x.IsDirectory).ToList();
                if (nestedEntries.Count == 0) return false;

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
    }
}