using System;
using System.Linq;
using System.Windows.Forms;

namespace Verviewer.UI
{
    /// <summary>
    /// 提取文件时的选项窗口：输入后缀过滤 + 反选模式 + 是否转换图片为 PNG。
    /// </summary>
    internal class ExtractOptionsForm : Form
    {
        private TextBox _txtExtensions = null!;
        private CheckBox _chkExclude = null!;
        private CheckBox _chkConvertImages = null!;
        private Button _btnOk = null!;
        private Button _btnCancel = null!;
        private Label _lblHint = null!;

        public string[] Extensions { get; private set; } = Array.Empty<string>();
        public bool ExcludeMode { get; private set; }
        public bool ConvertImagesToPng { get; private set; }

        public ExtractOptionsForm()
        {
            Text = "提取选项";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            Width = 460;
            Height = 260;

            InitializeComponents();
        }

        private void InitializeComponents()
        {
            var lbl = new Label
            {
                Text = "文件后缀（不带点，逗号分隔；留空 = 全部）：",
                AutoSize = true,
                Left = 12,
                Top = 15
            };

            _txtExtensions = new TextBox
            {
                Left = 12,
                Top = lbl.Bottom + 6,
                Width = ClientSize.Width - 24,
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right
            };

            _chkExclude = new CheckBox
            {
                Text = "过滤模式（勾选 = 排除这些后缀，其它全提取）",
                Left = 12,
                Top = _txtExtensions.Bottom + 10,
                AutoSize = true
            };

            _chkConvertImages = new CheckBox
            {
                Text = "将支持的图片转换为 PNG 输出",
                Left = 12,
                Top = _chkExclude.Bottom + 6,
                AutoSize = true
            };

            _lblHint = new Label
            {
                Text = "例如：只提取 txt,png ⇒ 输入 \"txt,png\"；\n排除 txt,png ⇒ 输入后勾选过滤模式。",
                Left = 12,
                Top = _chkConvertImages.Bottom + 8,
                AutoSize = true
            };

            _btnOk = new Button
            {
                Text = "确定",
                DialogResult = DialogResult.OK,
                Width = 80,
                Left = ClientSize.Width - 180,
                Top = ClientSize.Height - 40,
                Anchor = AnchorStyles.Bottom | AnchorStyles.Right
            };
            _btnOk.Click += BtnOk_Click;

            _btnCancel = new Button
            {
                Text = "取消",
                DialogResult = DialogResult.Cancel,
                Width = 80,
                Left = ClientSize.Width - 90,
                Top = ClientSize.Height - 40,
                Anchor = AnchorStyles.Bottom | AnchorStyles.Right
            };

            Controls.Add(lbl);
            Controls.Add(_txtExtensions);
            Controls.Add(_chkExclude);
            Controls.Add(_chkConvertImages);
            Controls.Add(_lblHint);
            Controls.Add(_btnOk);
            Controls.Add(_btnCancel);

            AcceptButton = _btnOk;
            CancelButton = _btnCancel;
        }

        private void BtnOk_Click(object? sender, EventArgs e)
        {
            string raw = _txtExtensions.Text.Trim();

            // 彩蛋：啥也没填还勾过滤
            if (string.IsNullOrEmpty(raw) && _chkExclude.Checked)
            {
                MessageBox.Show(this,
                    "哈哈，你是不是拿我寻开心，啥也没选。",
                    "？？？",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
                DialogResult = DialogResult.None; // 不关闭窗口
                return;
            }

            var exts = raw
                .Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim().TrimStart('.').ToLowerInvariant())
                .Where(s => s.Length > 0)
                .Distinct()
                .ToArray();

            Extensions = exts;
            ExcludeMode = _chkExclude.Checked;
            ConvertImagesToPng = _chkConvertImages.Checked;

            DialogResult = DialogResult.OK;
            Close();
        }
    }
}