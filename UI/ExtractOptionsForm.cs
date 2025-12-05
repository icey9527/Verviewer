using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace Verviewer.UI
{
    internal class ExtractOptionsForm : Form
    {
        private TextBox _txtExtensions = null!;
        private CheckBox _chkExclude = null!;
        private CheckBox _chkConvertImages = null!;
        private CheckBox _chkImagesOnly = null!;
        private ComboBox _cboImageFormat = null!;
        private CheckBox _chkRemoveAlpha = null!;
        private ComboBox _cboBgColor = null!;
        private Button _btnOk = null!;
        private Button _btnCancel = null!;
        private Label _lblHint = null!;

        public string[] Extensions { get; private set; } = Array.Empty<string>();
        public bool ExcludeMode { get; private set; }
        public bool ConvertImages { get; private set; }
        public bool ImagesOnly { get; private set; }
        public string ImageFormat { get; private set; } = "png";
        public bool RemoveAlpha { get; private set; }
        public Color BackgroundColor { get; private set; } = Color.Black;

        private Label _lblFormat = null!;

        public ExtractOptionsForm()
        {
            Text = "提取选项";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            Width = 480;
            Height = 280;

            InitializeComponents();
            UpdateControlStates();
        }

        private void InitializeComponents()
        {
            int y = 15;
            int leftMargin = 12;
            int indent = 32;

            // === 后缀过滤区域 ===
            var lblExt = new Label
            {
                Text = "文件后缀（不带点，逗号分隔；留空 = 全部）：",
                AutoSize = true,
                Left = leftMargin,
                Top = y
            };
            y = lblExt.Bottom + 6;

            _txtExtensions = new TextBox
            {
                Left = leftMargin,
                Top = y,
                Width = ClientSize.Width - 24,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };
            y = _txtExtensions.Bottom + 10;

            _chkExclude = new CheckBox
            {
                Text = "过滤模式（勾选 = 排除这些后缀）",
                Left = leftMargin,
                Top = y,
                AutoSize = true
            };
            y = _chkExclude.Bottom + 16;

            // === 分隔线 ===
            var separator = new Label
            {
                BorderStyle = BorderStyle.Fixed3D,
                Height = 2,
                Left = leftMargin,
                Top = y,
                Width = ClientSize.Width - 24,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };
            y = separator.Bottom + 12;

            // === 图像转换选项 ===
            _chkConvertImages = new CheckBox
            {
                Text = "转换图像格式",
                Left = leftMargin,
                Top = y,
                AutoSize = true
            };
            _chkConvertImages.CheckedChanged += (s, e) => UpdateControlStates();

            _chkImagesOnly = new CheckBox
            {
                Text = "仅提取图像",
                Left = _chkConvertImages.Right + 20,
                Top = y,
                AutoSize = true
            };
            _chkImagesOnly.CheckedChanged += (s, e) => UpdateControlStates();
            y = _chkConvertImages.Bottom + 8;

            // 输出格式
            _lblFormat = new Label
            {
                Text = "输出格式：",
                Left = leftMargin,
                Top = y,
                
                AutoSize = true
            };

            _cboImageFormat = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Left = _lblFormat.Right - 30,
                Top = y,
                Width = 70
            };
            _cboImageFormat.Items.AddRange(new object[] { "PNG", "JPG", "BMP", "GIF", "TIFF" });
            _cboImageFormat.SelectedIndex = 0;

            // 去除透明通道
            _chkRemoveAlpha = new CheckBox
            {
                Text = "去除透明通道：",
                Left = _cboImageFormat.Right + 20,
                Top = y,
                AutoSize = true
            };
            _chkRemoveAlpha.CheckedChanged += (s, e) => UpdateControlStates();

            _cboBgColor = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Left = _chkRemoveAlpha.Right + 8,
                Top = y,
                Width = 60
            };
            _cboBgColor.Items.AddRange(new object[] { "黑色", "白色" });
            _cboBgColor.SelectedIndex = 0; // 默认黑色
            y = _cboImageFormat.Bottom + 16;

            // === 提示 ===
            _lblHint = new Label
            {
                Text = "提示：勾选\"仅提取图像\"后，后缀过滤将被忽略。",
                Left = leftMargin,
                Top = y,
                AutoSize = true,
                ForeColor = Color.Gray
            };

            // === 按钮 ===
            _btnOk = new Button
            {
                Text = "确定",
                DialogResult = DialogResult.OK,
                Width = 80,
                Left = ClientSize.Width - 180,
                Top = ClientSize.Height - 45,
                Anchor = AnchorStyles.Bottom | AnchorStyles.Right
            };
            _btnOk.Click += BtnOk_Click;

            _btnCancel = new Button
            {
                Text = "取消",
                DialogResult = DialogResult.Cancel,
                Width = 80,
                Left = ClientSize.Width - 90,
                Top = ClientSize.Height - 45,
                Anchor = AnchorStyles.Bottom | AnchorStyles.Right
            };

            Controls.Add(lblExt);
            Controls.Add(_txtExtensions);
            Controls.Add(_chkExclude);
            Controls.Add(separator);
            Controls.Add(_chkConvertImages);
            Controls.Add(_chkImagesOnly);
            Controls.Add(_lblFormat);
            Controls.Add(_cboImageFormat);
            Controls.Add(_chkRemoveAlpha);
            Controls.Add(_cboBgColor);
            Controls.Add(_lblHint);
            Controls.Add(_btnOk);
            Controls.Add(_btnCancel);

            AcceptButton = _btnOk;
            CancelButton = _btnCancel;
        }

        private void UpdateControlStates()
        {
            bool convertImages = _chkConvertImages.Checked;
            bool imagesOnly = _chkImagesOnly.Checked;
            bool removeAlpha = _chkRemoveAlpha.Checked;

            // 仅提取图像需要先勾选转换图像
            _chkImagesOnly.Enabled = convertImages;
            if (!convertImages && _chkImagesOnly.Checked)
                _chkImagesOnly.Checked = false;

            // 仅提取图像时，后缀过滤不可用
            bool extFilterEnabled = !imagesOnly;
            _txtExtensions.Enabled = extFilterEnabled;
            _chkExclude.Enabled = extFilterEnabled;
            _txtExtensions.BackColor = extFilterEnabled ? SystemColors.Window : SystemColors.Control;

            _lblFormat.Enabled = convertImages;

            // 格式和去透明需要勾选转换图像
            _cboImageFormat.Enabled = convertImages;
            _chkRemoveAlpha.Enabled = convertImages;
            if (!convertImages && _chkRemoveAlpha.Checked)
                _chkRemoveAlpha.Checked = false;

            // 背景色需要勾选去透明
            _cboBgColor.Enabled = convertImages && removeAlpha;
        }

        private void BtnOk_Click(object? sender, EventArgs e)
        {
            bool imagesOnly = _chkImagesOnly.Checked;

            if (imagesOnly)
            {
                Extensions = Array.Empty<string>();
                ExcludeMode = false;
            }
            else
            {
                string raw = _txtExtensions.Text.Trim();

                if (string.IsNullOrEmpty(raw) && _chkExclude.Checked)
                {
                    MessageBox.Show(this,
                        "哈哈，你是不是拿我寻开心，啥也没选。",
                        "？？？",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information);
                    DialogResult = DialogResult.None;
                    return;
                }

                Extensions = raw
                    .Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(s => s.Trim().TrimStart('.').ToLowerInvariant())
                    .Where(s => s.Length > 0)
                    .Distinct()
                    .ToArray();
                ExcludeMode = _chkExclude.Checked;
            }

            ImagesOnly = imagesOnly;
            ConvertImages = _chkConvertImages.Checked;
            ImageFormat = _cboImageFormat.SelectedItem?.ToString()?.ToLowerInvariant() ?? "png";
            RemoveAlpha = _chkRemoveAlpha.Checked && ConvertImages;
            BackgroundColor = _cboBgColor.SelectedIndex == 1 ? Color.White : Color.Black;

            DialogResult = DialogResult.OK;
            Close();
        }
    }
}