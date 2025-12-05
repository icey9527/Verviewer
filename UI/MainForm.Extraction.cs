using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using Verviewer.Archives;
using Verviewer.Core;

namespace Verviewer.UI
{
    internal partial class MainForm
    {
        async Task ExtractEntriesAsync(
            OpenedArchive archive,
            List<ExtractItem> items,
            string? nestedRootFolder = null)
        {
            if (items.Count == 0)
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
            bool imagesOnly = optForm.ImagesOnly;
            bool convertImages = optForm.ConvertImages;
            string imageFormat = optForm.ImageFormat;
            bool removeAlpha = optForm.RemoveAlpha;
            Color bgColor = optForm.BackgroundColor;

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

            bool PassesExtensionFilter(string path)
            {
                string ext = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
                if (extSet.Count == 0) return !excludeMode;
                bool inSet = extSet.Contains(ext);
                return excludeMode ? !inSet : inSet;
            }

            // 仅图像模式不过滤后缀
            var workItems = imagesOnly
                ? items.ToList()
                : items.Where(x => PassesExtensionFilter(x.Entry.Path)).ToList();

            if (workItems.Count == 0)
            {
                MessageBox.Show(this, "没有匹配筛选条件的文件。", "提示",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            int total = workItems.Count;
            int done = 0;
            int extracted = 0;
            int skipped = 0;

            _menu.Enabled = false;
            _entryList.Enabled = false;
            UpdateStatus(CurrentPluginStatus, $"0 / {total}");

            try
            {
                await Task.Run(() =>
                {
                    foreach (var item in workItems)
                    {
                        try
                        {
                            string relPath = item.OutputPath.Replace('/', Path.DirectorySeparatorChar);
                            if (!string.IsNullOrEmpty(nestedRootFolder))
                                relPath = Path.Combine(nestedRootFolder, relPath);

                            string destPath = Path.Combine(targetRoot, relPath);

                            if (imagesOnly)
                            {
                                // 仅图像模式：先快速判断是否可能是图像
                                if (!MightBeImage(archive, item.Entry))
                                {
                                    skipped++;
                                }
                                else if (TryConvertAndSaveImage(archive, item.Entry, destPath, imageFormat, removeAlpha, bgColor))
                                {
                                    extracted++;
                                }
                                else
                                {
                                    skipped++;
                                }
                            }
                            else if (convertImages)
                            {
                                // 普通模式+转换：尝试转换，失败则保存原始
                                if (!TryConvertAndSaveImage(archive, item.Entry, destPath, imageFormat, removeAlpha, bgColor))
                                    SaveOriginalFile(archive, item.Entry, destPath);
                                extracted++;
                            }
                            else
                            {
                                // 普通模式：直接保存原始
                                SaveOriginalFile(archive, item.Entry, destPath);
                                extracted++;
                            }
                        }
                        catch (Exception ex)
                        {
                            string pathCopy = item.OutputPath;
                            Invoke((Action)(() =>
                            {
                                MessageBox.Show(this,
                                    $"处理文件失败：\n{pathCopy}\n{ex.Message}",
                                    "提取错误",
                                    MessageBoxButtons.OK,
                                    MessageBoxIcon.Warning);
                            }));
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
                });

                string message = imagesOnly
                    ? $"提取完成。\n成功：{extracted} 个，跳过：{skipped} 个"
                    : $"提取完成。共 {extracted} 个文件。";

                MessageBox.Show(this, message, "完成",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            finally
            {
                _menu.Enabled = true;
                _entryList.Enabled = true;
                UpdateStatus(CurrentPluginStatus, string.Empty);
            }
        }

        /// <summary>
        /// 快速判断文件是否可能是图像（通过后缀和魔数匹配，不实际解码）
        /// </summary>
        bool MightBeImage(OpenedArchive archive, ArchiveEntry entry)
        {
            try
            {
                int headerLen = Math.Max(16, PluginFactory.MaxImageHeaderLength);
                byte[] header;

                using (var s = archive.Handler.OpenEntryStream(archive, entry))
                {
                    header = new byte[headerLen];
                    int read = s.Read(header, 0, header.Length);
                    if (read < header.Length)
                        Array.Resize(ref header, read);
                }

                var types = PluginFactory.ResolveImageTypes(entry.Path, header);
                return types.Count > 0;
            }
            catch
            {
                return false;
            }
        }

        bool TryConvertAndSaveImage(
            OpenedArchive archive,
            ArchiveEntry entry,
            string destPath,
            string format,
            bool removeAlpha,
            Color bgColor)
        {
            Image? img;
            try
            {
                img = TryDecodeImage(archive, entry, out _);
            }
            catch
            {
                return false;
            }

            if (img == null)
                return false;

            try
            {
                // 去除透明通道
                if (removeAlpha && img is Bitmap bmp && HasAlphaChannel(bmp))
                {
                    var flattened = FlattenAlpha(bmp, bgColor);
                    img.Dispose();
                    img = flattened;
                }

                // 确定输出路径：保留原后缀，追加图片后缀
                var (imgFormat, ext) = GetImageFormat(format);
                string outputPath = destPath + ext; // file.tlg -> file.tlg.png

                string? outputDir = Path.GetDirectoryName(outputPath);
                if (!string.IsNullOrEmpty(outputDir))
                    Directory.CreateDirectory(outputDir);

                // 保存
                if (format.Equals("jpg", StringComparison.OrdinalIgnoreCase) ||
                    format.Equals("jpeg", StringComparison.OrdinalIgnoreCase))
                {
                    SaveAsJpeg(img, outputPath, 95);
                }
                else
                {
                    img.Save(outputPath, imgFormat);
                }

                return true;
            }
            finally
            {
                img.Dispose();
            }
        }

        void SaveOriginalFile(OpenedArchive archive, ArchiveEntry entry, string destPath)
        {
            string? destDir = Path.GetDirectoryName(destPath);
            if (!string.IsNullOrEmpty(destDir))
                Directory.CreateDirectory(destDir);

            using var s = archive.Handler.OpenEntryStream(archive, entry);
            using var outFs = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536, FileOptions.SequentialScan);
            s.CopyTo(outFs);
        }

        static bool HasAlphaChannel(Bitmap bmp) => Image.IsAlphaPixelFormat(bmp.PixelFormat);

        static Bitmap FlattenAlpha(Bitmap source, Color bgColor)
        {
            var result = new Bitmap(source.Width, source.Height, PixelFormat.Format24bppRgb);
            using var g = Graphics.FromImage(result);
            g.Clear(bgColor);
            g.DrawImage(source, 0, 0, source.Width, source.Height);
            return result;
        }

        static (ImageFormat format, string ext) GetImageFormat(string name) => name.ToLowerInvariant() switch
        {
            "jpg" or "jpeg" => (ImageFormat.Jpeg, ".jpg"),
            "bmp" => (ImageFormat.Bmp, ".bmp"),
            "gif" => (ImageFormat.Gif, ".gif"),
            "tiff" or "tif" => (ImageFormat.Tiff, ".tiff"),
            _ => (ImageFormat.Png, ".png")
        };

        static void SaveAsJpeg(Image img, string path, int quality)
        {
            var encoder = ImageCodecInfo.GetImageEncoders()
                .FirstOrDefault(c => c.FormatID == ImageFormat.Jpeg.Guid);

            if (encoder == null)
            {
                img.Save(path, ImageFormat.Jpeg);
                return;
            }

            using var encoderParams = new EncoderParameters(1);
            encoderParams.Param[0] = new EncoderParameter(Encoder.Quality, quality);
            img.Save(path, encoder, encoderParams);
        }

        async void ExtractMenu_Click(object? sender, EventArgs e)
        {
            if (_currentArchive == null)
            {
                MessageBox.Show(this, "还没有打开任何封包。", "无法提取",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var items = _currentArchive.Entries
                .Where(x => !x.IsDirectory)
                .Select(x => new ExtractItem(x, x.Path))
                .ToList();

            await ExtractEntriesAsync(_currentArchive, items, null);
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

            var items = CollectExtractItems(_currentArchive, selected);

            if (items.Count == 0)
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

            await ExtractEntriesAsync(_currentArchive, items, null);
        }

        List<ExtractItem> CollectExtractItems(OpenedArchive archive, List<ArchiveEntry> selected)
        {
            var result = new List<ExtractItem>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var entry in selected)
            {
                if (entry.IsDirectory)
                {
                    string prefix = NormalizePath(entry.Path).TrimEnd('/');
                    if (prefix.Length == 0) continue;

                    foreach (var ent in archive.Entries.Where(x => !x.IsDirectory))
                    {
                        string fullPath = NormalizePath(ent.Path);
                        if (!IsUnderFolder(fullPath, prefix)) continue;
                        if (!seen.Add(fullPath)) continue;

                        string relativePath = GetRelativeFromParent(fullPath, prefix);
                        result.Add(new ExtractItem(ent, relativePath));
                    }
                }
                else
                {
                    string fullPath = NormalizePath(entry.Path);
                    if (!seen.Add(fullPath)) continue;

                    string fileName = Path.GetFileName(fullPath);
                    result.Add(new ExtractItem(entry, fileName));
                }
            }

            return result;
        }

        static bool IsUnderFolder(string path, string folder) =>
            path.Equals(folder, StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith(folder + "/", StringComparison.OrdinalIgnoreCase);

        static string GetRelativeFromParent(string fullPath, string folderPrefix)
        {
            int lastSlash = folderPrefix.LastIndexOf('/');
            if (lastSlash < 0) return fullPath;

            string parentPath = folderPrefix.Substring(0, lastSlash + 1);
            return fullPath.StartsWith(parentPath, StringComparison.OrdinalIgnoreCase)
                ? fullPath.Substring(parentPath.Length)
                : fullPath;
        }

        async Task<bool> TryExtractNestedArchiveForFileAsync(ArchiveEntry entry)
        {
            if (_currentArchive == null) return false;
            if (_currentArchive.Handler is not FolderArchiveHandler) return false;

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

                    foreach (var type in types)
                    {
                        try
                        {
                            if (Activator.CreateInstance(type) is IArchiveHandler handler)
                            {
                                nested = handler.Open(fullPath);
                                break;
                            }
                        }
                        catch { }
                    }

                    if (nested == null) return false;
                }

                var nestedItems = nested.Entries
                    .Where(x => !x.IsDirectory)
                    .Select(x => new ExtractItem(x, x.Path))
                    .ToList();

                if (nestedItems.Count == 0) return false;

                string folderName = Path.GetFileNameWithoutExtension(fullPath);
                await ExtractEntriesAsync(nested, nestedItems, folderName);

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

        readonly struct ExtractItem
        {
            public ArchiveEntry Entry { get; }
            public string OutputPath { get; }

            public ExtractItem(ArchiveEntry entry, string outputPath)
            {
                Entry = entry;
                OutputPath = outputPath;
            }
        }
    }
}