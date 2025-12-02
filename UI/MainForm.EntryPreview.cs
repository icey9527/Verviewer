using System;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Windows.Forms;
using Verviewer.Core;
using Verviewer.Images;

namespace Verviewer.UI
{
    internal partial class MainForm
    {
        // 抽样长度：只看前 N 个字节判断是不是文本
        private const int TextSampleLength = 40;
        // 文本最大预览字节数，避免一次性把几百 MB 都读进来
        private const int MaxTextPreviewBytes = 1024 * 1024; // 1MB

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

                _lastPreviewTextData = null;
                _lastTextEntry = null;

                UpdateStatus(CurrentPluginStatus, _statusRight.Text);
            }
        }

        private void PreviewEntry(ArchiveEntry entry)
        {
            if (_currentArchive == null) return;

            _lastPreviewTextData = null;
            _lastTextEntry = null;

            // 尝试按图片方式解码
            string handlerName;
            var img = TryDecodeEntryImage(entry, out handlerName);
            if (img != null)
            {
                ShowImage(img, handlerName);
                return;
            }

            // 图片失败，再尝试文本预览：先抽样，再按最大大小读
            using (var s = _currentArchive.Handler.OpenEntryStream(_currentArchive, entry))
            using (var ms = new MemoryStream())
            {
                // 抽样
                byte[] sampleBuf = new byte[TextSampleLength];
                int sampleRead = s.Read(sampleBuf, 0, sampleBuf.Length);
                if (sampleRead > 0)
                {
                    ms.Write(sampleBuf, 0, sampleRead);
                }

                if (!IsProbablyText(sampleBuf, sampleRead))
                {
                    // 看起来不像文本，就不要整文件当文本解码
                    ShowNonTextHint();
                    return;
                }

                // 是文本，再继续读，但限制最大大小
                int remainingToRead = MaxTextPreviewBytes - sampleRead;
                if (remainingToRead > 0)
                {
                    var buffer = new byte[81920];
                    int read;
                    while (remainingToRead > 0 &&
                           (read = s.Read(buffer, 0, Math.Min(buffer.Length, remainingToRead))) > 0)
                    {
                        ms.Write(buffer, 0, read);
                        remainingToRead -= read;
                    }
                }

                var data = ms.ToArray();
                _lastPreviewTextData = data;
                _lastTextEntry = entry;

                ShowText(data);
            }
        }

        /// <summary>
        /// 尝试把条目当作图片解码，成功返回 Image，handlerName 表示使用的图片插件 ID。
        /// 失败返回 null。
        /// </summary>
        private Image? TryDecodeEntryImage(ArchiveEntry entry, out string handlerName)
        {
            handlerName = "builtin";
            if (_currentArchive == null)
                return null;

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

            // 2) 选择插件：按权重排好顺序，逐个尝试
            Image? decoded = null;

            var imgTypes = PluginFactory.ResolveImageTypes(entry.Path, header).ToList();
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
                    using var s = _currentArchive.Handler.OpenEntryStream(_currentArchive, entry);
                    decoded = ih.TryDecode(s, ext);
                }
                catch
                {
                    decoded = null;
                }

                if (decoded != null)
                {
                    var attr = imgType.GetCustomAttribute<ImagePluginAttribute>();
                    handlerName = attr?.Id ?? imgType.Name;
                    return decoded;
                }
            }

            // 3) 再尝试 GDI（直接从流创建）
            try
            {
                using var s = _currentArchive.Handler.OpenEntryStream(_currentArchive, entry);
                var gdiImg = Image.FromStream(s, useEmbeddedColorManagement: true, validateImageData: true);
                handlerName = "builtin";
                return gdiImg;
            }
            catch
            {
                return null;
            }
        }

        private static bool IsProbablyText(byte[] buffer, int length)
        {
            if (length == 0) return false;

            int controlCount = 0;
            for (int i = 0; i < length; i++)
            {
                byte b = buffer[i];
                if (b == 0)
                {
                    // 出现 NUL，几乎可以肯定是二进制
                    return false;
                }

                // 允许 \t \r \n，其它 < 0x20 的算控制字符
                if (b < 0x20 && (b != 0x09 && b != 0x0A && b != 0x0D))
                {
                    controlCount++;
                }
            }

            // 控制字符比例太高当作二进制
            return controlCount * 5 < length; // 控制字符 < 20%
        }

        private void ShowNonTextHint()
        {
            _imagePanel.Visible = false;
            _txtPreview.Visible = true;
            _encodingHost.Visible = false;
            _numZoom.Visible = false;
            _currentImageHandlerName = null;

            UpdateStatus(CurrentPluginStatus, _statusRight.Text);

            _txtPreview.Clear();
            _txtPreview.SelectionColor = Color.Gray;
            _txtPreview.AppendText("[看起来不是文本文件，已跳过文本预览]\r\n");
            _txtPreview.SelectionStart = 0;
        }

        #endregion

        #region 图片显示 & 缩放

        private void ShowImage(Image img, string handlerName)
        {
            _lastPreviewTextData = null;
            _lastTextEntry = null;

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

            if (data.Length >= MaxTextPreviewBytes)
            {
                _txtPreview.SelectionColor = Color.Gray;
                _txtPreview.AppendText($"\r\n\r\n[仅预览前 {MaxTextPreviewBytes} 字节，其余已省略]");
            }

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
            // 不要重新从封包里读取，只用缓存的字节重新按新编码解码
            if (_lastPreviewTextData == null)
                return;
            if (!_txtPreview.Visible)
                return;

            ShowText(_lastPreviewTextData);
        }

        #endregion

        #region 树右键 & 图片复制

        // 右键点击树节点时，让它成为 SelectedNode，方便上下文菜单操作
        private void Tree_NodeMouseClick(object? sender, TreeNodeMouseClickEventArgs e)
        {
            if (e.Button == MouseButtons.Right && e.Node != null)
            {
                _tree.SelectedNode = e.Node;
            }
        }

        // 树右键菜单打开前，根据当前选中节点调整菜单项可用状态
        private void TreeContextMenu_Opening(object? sender, CancelEventArgs e)
        {
            var node = _tree.SelectedNode;
            var entry = node?.Tag as ArchiveEntry;

            bool hasFile = entry != null && !entry.IsDirectory;

            // 提取按钮：只要当前有 Archive 就能用（这个逻辑保持不变）
            if (_treeExtractMenuItem != null)
                _treeExtractMenuItem.Enabled = _currentArchive != null;

            // 复制图片按钮：只有在“当前选中的是文件节点”且“预览窗口处于图片模式”时才显示
            if (_treeCopyImageMenuItem != null)
            {
                bool isImagePreview = _imagePanel.Visible && _originalImage != null;

                _treeCopyImageMenuItem.Visible = hasFile && isImagePreview;
            }
        }
        // 树右键：“复制图片”——尝试把选中的条目当作图片解码并复制到剪贴板
        private void TreeContext_CopyImage_Click(object? sender, EventArgs e)
        {
            if (_currentArchive == null)
                return;

            var node = _tree.SelectedNode;
            if (node?.Tag is not ArchiveEntry entry || entry.IsDirectory)
                return;

            string handlerName;
            var img = TryDecodeEntryImage(entry, out handlerName);
            if (img == null)
            {
                MessageBox.Show(this,
                    "无法将这个文件识别为图片。",
                    "无法复制",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                return;
            }

            try
            {
                Clipboard.SetImage(img);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this,
                    "复制图片到剪贴板失败：\n" + ex.Message,
                    "错误",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
            finally
            {
                img.Dispose();
            }
        }

        // 图片预览区域右键：“复制图片”——复制当前预览图
        private void ImageContext_Copy_Click(object? sender, EventArgs e)
        {
            if (_originalImage == null)
                return;

            try
            {
                Clipboard.SetImage(_originalImage);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this,
                    "复制图片到剪贴板失败：\n" + ex.Message,
                    "错误",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
        }

        #endregion
    }
}