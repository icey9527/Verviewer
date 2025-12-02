using System.Drawing;
using System.Windows.Forms;

namespace Verviewer.UI
{
    internal partial class MainForm
    {
        private void InitializeUi()
        {
            SuspendLayout();

            // ===== 左右分割面板（主体内容区域，先建它） =====
            _split = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Vertical
            };
            _split.Panel1.Padding = new Padding(4, 4, 0, 4);
            _split.Panel2.Padding = new Padding(4, 4, 4, 4);

            // ----- 左侧：树 -----
            _tree = new TreeView
            {
                Dock = DockStyle.Fill
            };
            _tree.AfterSelect += Tree_AfterSelect;
            _tree.NodeMouseDoubleClick += Tree_NodeMouseDoubleClick;
            _tree.NodeMouseClick += Tree_NodeMouseClick; // 右键选中节点

            // 树的右键菜单：提取 + 复制图片
            _treeContextMenu = new ContextMenuStrip();
            _treeExtractMenuItem = new ToolStripMenuItem("提取(&E)...", null, TreeContext_ExtractSelected_Click);
            _treeCopyImageMenuItem = new ToolStripMenuItem("复制图片(&C)", null, TreeContext_CopyImage_Click);

            _treeContextMenu.Items.AddRange(new ToolStripItem[]
            {
                _treeExtractMenuItem,
                _treeCopyImageMenuItem
            });
            _treeContextMenu.Opening += TreeContextMenu_Opening;
            _tree.ContextMenuStrip = _treeContextMenu;

            _split.Panel1.Controls.Add(_tree);

            // ----- 右侧：文本预览 -----
            _txtPreview = new RichTextBox
            {
                Dock = DockStyle.Fill,
                Font = new Font("Microsoft YaHei", 10),
                ReadOnly = true,
                HideSelection = false,
                DetectUrls = false,
                WordWrap = false
            };

            // ----- 右侧：图片预览 Panel + PictureBox -----
            _imagePanel = new Panel
            {
                Dock = DockStyle.Fill,
                Visible = false,    // 初始显示文本
                BackColor = Color.Black
            };
            _picPreview = new PictureBox
            {
                SizeMode = PictureBoxSizeMode.Normal,
                BackColor = Color.Black
            };
            _imagePanel.Controls.Add(_picPreview);
            _imagePanel.Resize += (s, e) => CenterImage();

            // 图片右键菜单：复制到剪贴板
            _imageContextMenu = new ContextMenuStrip();
            var copyImageItem = new ToolStripMenuItem("复制图片(&C)", null, ImageContext_Copy_Click);
            _imageContextMenu.Items.Add(copyImageItem);
            _imagePanel.ContextMenuStrip = _imageContextMenu;

            // Panel2 里同时放文本和图片，只是通过 Visible 来切换
            _split.Panel2.Controls.Add(_txtPreview);
            _split.Panel2.Controls.Add(_imagePanel);

            // ===== 底部状态栏 =====
            _statusStrip = new StatusStrip
            {
                Dock = DockStyle.Bottom
            };

            _statusLeft = new ToolStripStatusLabel
            {
                Spring = true,
                TextAlign = System.Drawing.ContentAlignment.MiddleLeft
            };

            _statusRight = new ToolStripStatusLabel
            {
                AutoSize = false,
                Width = 160,
                TextAlign = System.Drawing.ContentAlignment.MiddleRight
            };

            // 编码选择下拉框
            _comboEncoding = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Width = 120
            };
            _comboEncoding.Items.AddRange(new object[] { "cp932", "cp936", "utf-8" });
            _comboEncoding.SelectedIndex = 0;
            _comboEncoding.SelectedIndexChanged += ComboEncoding_SelectedIndexChanged;

            _encodingHost = new ToolStripControlHost(_comboEncoding)
            {
                AutoSize = false,
                Width = 130
            };

            // 图片缩放数值框，放在状态栏里
            _numZoom = new NumericUpDown
            {
                Minimum = 10,
                Maximum = 400,
                Increment = 10,
                Value = 100,
                Width = 70,
                Visible = false  // 只有图片预览时显示
            };
            _numZoom.ValueChanged += NumZoom_ValueChanged;

            _zoomHost = new ToolStripControlHost(_numZoom)
            {
                AutoSize = false,
                Width = 70,
                Visible = false
            };

            _statusStrip.Items.Add(_statusLeft);
            _statusStrip.Items.Add(_statusRight);
            _statusStrip.Items.Add(_zoomHost);
            _statusStrip.Items.Add(_encodingHost);

            // ===== 顶部菜单栏 =====
            _menu = new MenuStrip
            {
                Dock = DockStyle.Top
            };

            // 顶级：打开
            var menuOpen = new ToolStripMenuItem("打开");

            var openFileItem = new ToolStripMenuItem("打开文件(&F)...", null, BtnOpen_Click);
            var openFolderItem = new ToolStripMenuItem("打开文件夹(&D)...", null, OpenFolder_Click);

            menuOpen.DropDownItems.Add(openFileItem);
            menuOpen.DropDownItems.Add(openFolderItem);

            // 顶级：提取（是否显示由导航状态控制）
            _menuExtractItem = new ToolStripMenuItem("提取(&E)...")
            {
                Visible = false   // 初始隐藏
            };
            _menuExtractItem.Click += ExtractMenu_Click;

            // 顶级：返回（由导航状态控制）
            _menuBackItem = new ToolStripMenuItem("返回(&B)")
            {
                Visible = false   // 初始隐藏
            };
            _menuBackItem.Click += MenuBack_Click;

            _menu.Items.Add(menuOpen);
            _menu.Items.Add(_menuExtractItem);
            _menu.Items.Add(_menuBackItem);

            MainMenuStrip = _menu;

            // ===== 把控件加到窗体上 =====
            // 顺序：先 Fill 的 SplitContainer，再 Bottom 的 StatusStrip，最后 Top 的 MenuStrip
            Controls.Add(_split);
            Controls.Add(_statusStrip);
            Controls.Add(_menu);

            ResumeLayout(false);
            PerformLayout();
        }
    }
}