using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
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

            OpenArchive(ofd.FileName);
        }

        private void OpenArchive(string archivePath)
        {
            // 用解析器按“扩展名或魔术”挑选插件
            using var fsProbe = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            var type = PluginFactory.ResolveArchiveType(archivePath, fsProbe);
            if (type == null)
            {
                MessageBox.Show(this, "没有找到可以处理这个封包的插件。", "无法打开",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            var handler = (IArchiveHandler?)Activator.CreateInstance(type);
            if (handler == null)
            {
                MessageBox.Show(this, $"无法创建封包处理器：{type.FullName}", "无法打开",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            _currentArchive?.Dispose();
            _currentArchive = null;

            var attr = type.GetCustomAttribute<ArchivePluginAttribute>();
            _currentArchiveRuleName = attr?.Id ?? type.Name;
            _currentImageHandlerName = null;
            _lastSelectedEntryPath = null;

            try
            {
                _currentArchive = handler.Open(archivePath);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "打开封包失败：\n" + ex.Message, "错误",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            Text = $"Verviewer - {Path.GetFileName(archivePath)}";
            UpdateStatus(CurrentPluginStatus, string.Empty);

            BuildTreeFromEntries();
        }

        #endregion

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

            var rootText = Path.GetFileName(_currentArchive.SourcePath);
            var rootNode = new TreeNode(rootText)
            {
                Tag = string.Empty // 根路径用空字符串
            };
            _tree.Nodes.Add(rootNode);

            foreach (var entry in _currentArchive.Entries)
            {
                AddEntryToTree(rootNode, entry);
            }

            rootNode.Expand();
            _tree.EndUpdate();
        }

        private void AddEntryToTree(TreeNode rootNode, ArchiveEntry entry)
        {
            string fullPath = entry.Path.Replace('\\', '/').Trim('/');
            if (string.IsNullOrEmpty(fullPath))
                return;

            string[] parts = fullPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
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

        #endregion

        #region 树节点选择 & 预览（图片 / 文本）

        private void Tree_AfterSelect(object? sender, TreeViewEventArgs e)
        {
            if (_currentArchive == null)
                return;

            if (e.Node?.Tag is ArchiveEntry entry && !entry.IsDirectory)
            {
                _lastSelectedEntryPath = entry.Path;
                PreviewEntry(entry);
            }
            else
            {
                // 选中目录：清空预览
                _txtPreview.Clear();
                _imagePanel.Visible = false;
                _txtPreview.Visible = true;
                _encodingHost.Visible = true;
                _numZoom.Visible = false;
                _currentImageHandlerName = null;
                UpdateStatus(CurrentPluginStatus, _statusRight.Text);
            }
        }

        private void PreviewEntry(ArchiveEntry entry)
        {
            if (_currentArchive == null) return;

            var ext = Path.GetExtension(entry.Path)?.ToLowerInvariant();

            // 1) 只读头用于选择图片插件
            byte[] header;
            int headLen = Math.Max(16, PluginFactory.MaxImageHeaderLength);
            using (var s = _currentArchive.Handler.OpenEntryStream(_currentArchive, entry))
            {
                header = new byte[headLen];
                int read = s.Read(header, 0, header.Length);
                if (read < header.Length) Array.Resize(ref header, read);
            }

            // 2) 选择插件
            var imgType = PluginFactory.ResolveImageType(entry.Path, header);
            Image? decoded = null;
            string pluginName = "builtin";

            if (imgType != null)
            {
                var obj = Activator.CreateInstance(imgType);
                var attr = imgType.GetCustomAttribute<ImagePluginAttribute>();
                pluginName = attr?.Id ?? imgType.Name;

                // 2a) 流式解码（唯一接口）
                if (obj is IImageHandler ih)
                {
                    using var s = _currentArchive.Handler.OpenEntryStream(_currentArchive, entry);
                    decoded = ih.TryDecode(s, ext);
                }
            }

            if (decoded != null)
            {
                ShowImage(decoded, pluginName);
                return;
            }

            // 3) 再尝试 GDI（直接从流创建，再交给 ShowImage 克隆）
            try
            {
                using var s = _currentArchive.Handler.OpenEntryStream(_currentArchive, entry);
                var gdiImg = Image.FromStream(s, useEmbeddedColorManagement: true, validateImageData: true);
                ShowImage(gdiImg, "builtin");
                return;
            }
            catch
            {
            }

            // 4) 文本预览（读全）
            using (var s = _currentArchive.Handler.OpenEntryStream(_currentArchive, entry))
            using (var ms = new MemoryStream())
            {
                s.CopyTo(ms);
                ShowText(ms.ToArray());
            }
        }

        #endregion

        #region 图片显示 & 缩放

        private void ShowImage(Image img, string handlerName)
        {
            _txtPreview.Visible = false;
            _imagePanel.Visible = true;
            _encodingHost.Visible = false;
            _numZoom.Visible = true;

            _originalImage?.Dispose();
            _originalImage = new Bitmap(img);
            img.Dispose(); // 释放传入对象
            _currentImageHandlerName = handlerName;

            UpdateStatus(CurrentPluginStatus, _statusRight?.Text);

            _imageZoom = 1.0f;
            var panelSize = _imagePanel.ClientSize;
            if (panelSize.Width > 0 && panelSize.Height > 0 && _originalImage != null)
            {
                float zx = (float)panelSize.Width / _originalImage.Width;
                float zy = (float)panelSize.Height / _originalImage.Height;
                float fitZoom = Math.Min(zx, zy);
                if (fitZoom < 1.0f) _imageZoom = fitZoom;
            }

            SetImageZoom(_imageZoom, fromNumeric: false);
            _imagePanel.Focus();
        }

        private void MainForm_MouseWheel(object? sender, MouseEventArgs e)
        {
            if (!_imagePanel.Visible || _originalImage == null)
                return;

            var rectScreen = _imagePanel.RectangleToScreen(_imagePanel.ClientRectangle);
            if (!rectScreen.Contains(MousePosition))
                return;

            float delta = e.Delta > 0 ? 0.1f : -0.1f;
            float newZoom = _imageZoom + delta;
            SetImageZoom(newZoom, fromNumeric: false);
        }

        private void NumZoom_ValueChanged(object? sender, EventArgs e)
        {
            if (_updatingZoomControl) return;
            if (_originalImage == null) return;

            float zoom = (float)_numZoom.Value / 100f;
            SetImageZoom(zoom, fromNumeric: true);
        }

        private void SetImageZoom(float zoom, bool fromNumeric)
        {
            if (_originalImage == null)
                return;

            if (zoom < 0.1f) zoom = 0.1f;
            if (zoom > 4.0f) zoom = 4.0f;

            _imageZoom = zoom;

            if (!fromNumeric)
            {
                _updatingZoomControl = true;
                try
                {
                    decimal val = (decimal)(_imageZoom * 100f);
                    if (val < _numZoom.Minimum) val = _numZoom.Minimum;
                    if (val > _numZoom.Maximum) val = _numZoom.Maximum;
                    _numZoom.Value = val;
                }
                finally
                {
                    _updatingZoomControl = false;
                }
            }

            UpdateImageDisplay();
        }

        private void UpdateImageDisplay()
        {
            if (_originalImage == null)
                return;

            int targetW = (int)(_originalImage.Width * _imageZoom);
            int targetH = (int)(_originalImage.Height * _imageZoom);
            if (targetW < 1) targetW = 1;
            if (targetH < 1) targetH = 1;

            var bmp = new Bitmap(targetW, targetH);
            using (var g = Graphics.FromImage(bmp))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.Clear(Color.Black);
                g.DrawImage(_originalImage, new Rectangle(0, 0, targetW, targetH));
            }

            _picPreview.Image?.Dispose();
            _picPreview.Image = bmp;
            _picPreview.Size = bmp.Size;

            CenterImage();
        }

        private void CenterImage()
        {
            if (_picPreview.Image == null) return;

            var panelSize = _imagePanel.ClientSize;
            var imgSize = _picPreview.Size;

            int x = (panelSize.Width - imgSize.Width) / 2;
            int y = (panelSize.Height - imgSize.Height) / 2;
            if (x < 0) x = 0;
            if (y < 0) y = 0;

            _picPreview.Location = new Point(x, y);
        }

        #endregion

        #region 文本 & 编码

        private void ShowText(byte[] data)
        {
            _imagePanel.Visible = false;
            _txtPreview.Visible = true;
            _encodingHost.Visible = true;
            _numZoom.Visible = false;
            _currentImageHandlerName = null;

            UpdateStatus(CurrentPluginStatus, _statusRight.Text);

            Encoding enc = GetSelectedEncoding();
            string text;
            try
            {
                text = enc.GetString(data);
            }
            catch (Exception ex)
            {
                text = $"[文本解码失败: {ex.Message}]\r\n\r\n(尝试更换底部编码后再试)";
            }

            _txtPreview.Clear();
            _txtPreview.SelectionColor = Color.Black;
            _txtPreview.AppendText(text);
            _txtPreview.SelectionStart = 0;
        }

        private Encoding GetSelectedEncoding()
        {
            string? name = _comboEncoding.SelectedItem as string;
            name = name?.ToLowerInvariant();

            try
            {
                return name switch
                {
                    "utf-8" => Encoding.UTF8,
                    "gb18030" => Encoding.GetEncoding("gb18030"),
                    "cp936" => Encoding.GetEncoding(936),
                    "cp932" or "shift_jis" or null => Encoding.GetEncoding(932),
                    _ => Encoding.UTF8
                };
            }
            catch
            {
                return Encoding.UTF8;
            }
        }

        private void ComboEncoding_SelectedIndexChanged(object? sender, EventArgs e)
        {
            if (_currentArchive == null || _lastSelectedEntryPath == null)
                return;
            if (!_txtPreview.Visible)
                return;

            var entry = _currentArchive.Entries
                .FirstOrDefault(e => !e.IsDirectory &&
                    e.Path.Equals(_lastSelectedEntryPath, StringComparison.OrdinalIgnoreCase));
            if (entry == null)
                return;

            PreviewEntry(entry);
        }

        #endregion

        #region 提取功能（按需读取 + PNG 转换）

        private async void ExtractMenu_Click(object? sender, EventArgs e)
        {
            if (_currentArchive == null)
            {
                MessageBox.Show(this, "还没有打开任何封包。", "无法提取",
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
            var entries = _currentArchive.Entries.Where(e => !e.IsDirectory).ToList();
            int total = entries.Count;
            int done = 0;

            _menu.Enabled = false;
            _tree.Enabled = false;
            UpdateStatus(CurrentPluginStatus, $"0 / {total}");

            try
            {
                await Task.Run(() =>
                {
                    var extSet = new HashSet<string>(exts.Select(s => s.ToLowerInvariant()),
                        StringComparer.OrdinalIgnoreCase);

                    foreach (var entry in entries)
                    {
                        string relPath = entry.Path.Replace('/', Path.DirectorySeparatorChar);
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

                        if (convertImagesToPng)
                        {
                            try
                            {
                                Image? img = null;

                                // 先用头部挑插件（只读少量头）
                                byte[] head;
                                int headLen = Math.Max(16, PluginFactory.MaxImageHeaderLength);
                                using (var sh = _currentArchive.Handler.OpenEntryStream(_currentArchive, entry))
                                {
                                    head = new byte[headLen];
                                    int r = sh.Read(head, 0, head.Length);
                                    if (r < head.Length) Array.Resize(ref head, r);
                                }

                                var imgType = PluginFactory.ResolveImageType("." + ext, head);
                                if (imgType != null)
                                {
                                    var obj = Activator.CreateInstance(imgType);

                                    if (obj is IImageHandler ih)
                                    {
                                        using var s = _currentArchive.Handler.OpenEntryStream(_currentArchive, entry);
                                        img = ih.TryDecode(s, "." + ext);
                                    }
                                }

                                if (img == null)
                                {
                                    using var s = _currentArchive.Handler.OpenEntryStream(_currentArchive, entry);
                                    var gdi = Image.FromStream(s, useEmbeddedColorManagement: true, validateImageData: true);
                                    img = gdi; // 交给下方 ShowImage/保存时克隆与释放
                                }

                                if (img != null)
                                {
                                    string pngPath = Path.ChangeExtension(destPath, ".png");
                                    Directory.CreateDirectory(Path.GetDirectoryName(pngPath)!);
                                    img.Save(pngPath, System.Drawing.Imaging.ImageFormat.Png);
                                    img.Dispose();

                                    done++;
                                    if (done % 50 == 0)
                                    {
                                        int d = done, t = total;
                                        Invoke((Action)(() => UpdateStatus(CurrentPluginStatus, $"{d} / {t}")));
                                    }
                                    continue;
                                }
                            }
                            catch
                            {
                                // 转换失败则按原样复制
                            }
                        }

                        using (var s = _currentArchive.Handler.OpenEntryStream(_currentArchive, entry))
                        using (var outFs = new FileStream(destPath, FileMode.Create, FileAccess.Write))
                        {
                            s.CopyTo(outFs);
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

        #endregion
    }
}