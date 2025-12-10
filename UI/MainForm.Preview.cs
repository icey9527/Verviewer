using System;
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
        void PreviewEntry(ArchiveEntry entry)
        {
            if (_currentArchive == null) return;

            _lastPreviewTextData = null;
            _lastTextEntry = null;

            string handlerName;
            var img = TryDecodeImage(_currentArchive, entry, out handlerName);
            if (img != null)
            {
                ShowImage(img, handlerName);
                return;
            }

            using var s = _currentArchive.Handler.OpenEntryStream(_currentArchive, entry);
            using var ms = new MemoryStream();
            var sampleBuf = new byte[TextSampleLength];
            int sampleRead = s.Read(sampleBuf, 0, sampleBuf.Length);
            if (sampleRead > 0) ms.Write(sampleBuf, 0, sampleRead);
            if (!IsProbablyText(sampleBuf, sampleRead))
            {
                ShowNonTextHint();
                return;
            }

            int remainingToRead = MaxTextPreviewBytes - sampleRead;
            if (remainingToRead > 0)
            {
                var buf = new byte[81920];
                int read;
                while (remainingToRead > 0 &&
                       (read = s.Read(buf, 0, Math.Min(buf.Length, remainingToRead))) > 0)
                {
                    ms.Write(buf, 0, read);
                    remainingToRead -= read;
                }
            }

            var data = ms.ToArray();
            _lastPreviewTextData = data;
            _lastTextEntry = entry;
            ShowText(data);
        }

        Image? TryDecodeImage(OpenedArchive archive, ArchiveEntry entry, out string handlerName)
        {
            handlerName = "builtin";
            var ext = Path.GetExtension(entry.Path)?.ToLowerInvariant();
            byte[] header;
            int headLen = Math.Max(16, PluginFactory.MaxImageHeaderLength);
            using (var s = archive.Handler.OpenEntryStream(archive, entry))
            {
                header = new byte[headLen];
                int read = s.Read(header, 0, header.Length);
                if (read < header.Length) Array.Resize(ref header, read);
            }

            Image? decoded = null;
            var imgTypes = PluginFactory.ResolveImageTypes(entry.Path, header).ToList();
            foreach (var imgType in imgTypes)
            {
                IImageHandler? ih;
                try
                {
                    ih = Activator.CreateInstance(imgType) as IImageHandler;
                }
                catch
                {
                    continue;
                }
                if (ih == null) continue;

                try
                {
                    using var s = archive.Handler.OpenEntryStream(archive, entry);
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

            try
            {
                using var s = archive.Handler.OpenEntryStream(archive, entry);
                decoded = Image.FromStream(s, true, true);
                handlerName = "builtin";
                return decoded;
            }
            catch
            {
                return null;
            }
        }

        const int TextSampleLength = 40;
        const int MaxTextPreviewBytes = 1024 * 1024;

        static bool IsProbablyText(byte[] buffer, int length)
        {
            if (length == 0) return false;
            int controlCount = 0;
            for (int i = 0; i < length; i++)
            {
                byte b = buffer[i];
                if (b == 0) return false;
                if (b < 0x20 && b != 0x09 && b != 0x0A && b != 0x0D) controlCount++;
            }
            return controlCount * 5 < length;
        }

        void ShowNonTextHint()
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

        void ShowImage(Image img, string handlerName)
        {
            _lastPreviewTextData = null;
            _lastTextEntry = null;
            _txtPreview.Visible = false;
            _imagePanel.Visible = true;
            _encodingHost.Visible = false;
            _numZoom.Visible = true;
            _picPreview.Image?.Dispose();
            _originalImage?.Dispose();
            _originalImage = img;
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

            SetImageZoom(_imageZoom, false);
            _imagePanel.Focus();
        }

        void MainForm_MouseWheel(object? sender, MouseEventArgs e)
        {
            if (!_imagePanel.Visible || _originalImage == null) return;
            var rectScreen = _imagePanel.RectangleToScreen(_imagePanel.ClientRectangle);
            if (!rectScreen.Contains(MousePosition)) return;
            float delta = e.Delta > 0 ? 0.1f : -0.1f;
            float newZoom = _imageZoom + delta;
            SetImageZoom(newZoom, false);
        }

        void NumZoom_ValueChanged(object? sender, EventArgs e)
        {
            if (_updatingZoomControl) return;
            if (_originalImage == null) return;
            float zoom = (float)_numZoom.Value / 100f;
            SetImageZoom(zoom, true);
        }

        void SetImageZoom(float zoom, bool fromNumeric)
        {
            if (_originalImage == null) return;
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

        void UpdateImageDisplay()
        {
            if (_originalImage == null) return;
            int targetW = (int)(_originalImage.Width * _imageZoom);
            int targetH = (int)(_originalImage.Height * _imageZoom);
            if (targetW < 1) targetW = 1;
            if (targetH < 1) targetH = 1;

            var bmp = new Bitmap(targetW, targetH);
            using (var g = Graphics.FromImage(bmp))
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                using (var gridBrush = new System.Drawing.Drawing2D.HatchBrush(
                    System.Drawing.Drawing2D.HatchStyle.LargeCheckerBoard, 
                    Color.LightGray, 
                    Color.White))
                {
                    g.FillRectangle(gridBrush, 0, 0, targetW, targetH);
                }
                g.DrawImage(_originalImage, new Rectangle(0, 0, targetW, targetH));
            }

            _picPreview.Image?.Dispose();
            _picPreview.Image = bmp;
            _picPreview.Size = bmp.Size;
            CenterImage();
        }

        void CenterImage()
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

        void ShowText(byte[] data)
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

        Encoding GetSelectedEncoding()
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

        void ComboEncoding_SelectedIndexChanged(object? sender, EventArgs e)
        {
            if (_lastPreviewTextData == null) return;
            if (!_txtPreview.Visible) return;
            ShowText(_lastPreviewTextData);
        }

        void EntryContext_CopyImage_Click(object? sender, EventArgs e)
        {
            if (_currentArchive == null) return;
            if (!TryGetSingleSelectedEntry(out var entry) || entry.IsDirectory || entry.Path.Length == 0) return;

            var img = TryDecodeImage(_currentArchive, entry, out _);
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

        void ImageContext_Copy_Click(object? sender, EventArgs e)
        {
            if (_originalImage == null) return;
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
    }
}