using System.Drawing;
using System.Windows.Forms;

namespace Verviewer.UI
{
    internal partial class MainForm
    {
        private void InitializeUi()
        {
            // 顶部菜单栏
            _menu = new MenuStrip();
            var menuFile = new ToolStripMenuItem("文件");
            var openItem = new ToolStripMenuItem("打开...");
            openItem.Click += BtnOpen_Click;
            var extractItem = new ToolStripMenuItem("提取...");
            extractItem.Click += ExtractMenu_Click;
            menuFile.DropDownItems.Add(openItem);
            menuFile.DropDownItems.Add(extractItem);
            _menu.Items.Add(menuFile);
            MainMenuStrip = _menu;
            Controls.Add(_menu);

            // 左右分割面板
            _split = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Vertical
            };
            _split.Panel1.Padding = new Padding(4, 4, 0, 4);
            _split.Panel2.Padding = new Padding(4, 4, 4, 4);

            // 左侧：树
            _tree = new TreeView
            {
                Dock = DockStyle.Fill
            };
            _tree.AfterSelect += Tree_AfterSelect;
            _split.Panel1.Controls.Add(_tree);

            // 右侧：文本预览
            _txtPreview = new RichTextBox
            {
                Dock = DockStyle.Fill,
                Font = new Font("Microsoft YaHei", 10),
                ReadOnly = true,
                HideSelection = false,
                DetectUrls = false,
                WordWrap = false
            };

            // 右侧：图片预览 Panel + PictureBox
            _imagePanel = new Panel
            {
                Dock = DockStyle.Fill,
                Visible = false,
                BackColor = Color.Black
            };
            _picPreview = new PictureBox
            {
                SizeMode = PictureBoxSizeMode.Normal,
                BackColor = Color.Black
            };
            _imagePanel.Controls.Add(_picPreview);
            _imagePanel.Resize += (s, e) => CenterImage();

            // 图片缩放比例（右上角数值框）
            _numZoom = new NumericUpDown
            {
                Minimum = 10,
                Maximum = 400,
                Increment = 10,
                Value = 100,
                Width = 70,
                Visible = false,
                Anchor = AnchorStyles.Top | AnchorStyles.Right
            };
            _numZoom.ValueChanged += NumZoom_ValueChanged;
            _numZoom.Location = new Point(20, 28);

            _split.Panel2.Controls.Add(_txtPreview);
            _split.Panel2.Controls.Add(_imagePanel);

            // 底部状态栏：左边插件信息，中间编码选择，右边提取进度/状态
            _statusStrip = new StatusStrip();

            _statusLeft = new ToolStripStatusLabel
            {
                Spring = true,
                TextAlign = System.Drawing.ContentAlignment.MiddleLeft
            };

            _comboEncoding = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Width = 120
            };
            // 默认 cp932
            _comboEncoding.Items.AddRange(new object[] { "cp932", "cp936", "utf-8" });
            _comboEncoding.SelectedIndex = 0;
            _comboEncoding.SelectedIndexChanged += ComboEncoding_SelectedIndexChanged;

            _encodingHost = new ToolStripControlHost(_comboEncoding)
            {
                AutoSize = false,
                Width = 130
            };

            _statusRight = new ToolStripStatusLabel
            {
                AutoSize = false,
                Width = 160,
                TextAlign = System.Drawing.ContentAlignment.MiddleRight
            };

            _zoomHost = new ToolStripControlHost(_numZoom)
            {
                AutoSize = false,
                Width = 70,
                Visible = false // 初始隐藏
            };


            _statusStrip.Items.Add(_statusLeft);
            _statusStrip.Items.Add(_statusRight);
            _statusStrip.Items.Add(_zoomHost);
            _statusStrip.Items.Add(_encodingHost);

            Controls.Add(_split);
            Controls.Add(_statusStrip);
        }
    }
}