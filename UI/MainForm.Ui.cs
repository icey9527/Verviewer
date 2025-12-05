using System;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace Verviewer.UI
{
    internal partial class MainForm
    {
        void InitializeUi()
        {
            SuspendLayout();

            _split = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Vertical
            };
            _split.Panel1.Padding = new Padding(4, 4, 0, 4);
            _split.Panel2.Padding = new Padding(4, 4, 4, 4);

            _entryList = new ListView
            {
                Dock = DockStyle.Fill,
                View = View.Details,
                FullRowSelect = true,
                HideSelection = false,
                VirtualMode = true,
                MultiSelect = true,
                GridLines = true,
            };
            _entryList.Columns.Add("名称", 140);
            _entryList.Columns.Add("大小", 100);
            _entryList.Columns.Add("类型", 90);

            _entryList.SelectedIndexChanged += EntryList_SelectedIndexChanged;
            _entryList.MouseDoubleClick += EntryList_MouseDoubleClick;
            _entryList.RetrieveVirtualItem += EntryList_RetrieveVirtualItem;
            _entryList.MouseDown += EntryList_MouseDown;
            _entryList.ColumnClick += EntryList_ColumnClick;
            _entryList.KeyDown += EntryList_KeyDown;

            _entryContextMenu = new ContextMenuStrip();
            _entryExtractMenuItem = new ToolStripMenuItem("提取(&E)...", null, EntryContext_ExtractSelected_Click);
            _entryCopyImageMenuItem = new ToolStripMenuItem("复制图片(&C)", null, EntryContext_CopyImage_Click);
            _entryContextMenu.Items.AddRange(new ToolStripItem[]
            {
                _entryExtractMenuItem,
                _entryCopyImageMenuItem
            });
            _entryContextMenu.Opening += EntryContextMenu_Opening;
            _entryList.ContextMenuStrip = _entryContextMenu;

            _split.Panel1.Controls.Add(_entryList);

            _txtPreview = new RichTextBox
            {
                Dock = DockStyle.Fill,
                Font = new Font("Microsoft YaHei", 10),
                ReadOnly = true,
                HideSelection = false,
                DetectUrls = false,
                WordWrap = false,
                BackColor = Color.White
            };

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

            _imageContextMenu = new ContextMenuStrip();
            var copyImageItem = new ToolStripMenuItem("复制图片(&C)", null, ImageContext_Copy_Click);
            _imageContextMenu.Items.Add(copyImageItem);
            _imagePanel.ContextMenuStrip = _imageContextMenu;

            _split.Panel2.Controls.Add(_txtPreview);
            _split.Panel2.Controls.Add(_imagePanel);

            _statusStrip = new StatusStrip
            {
                Dock = DockStyle.Bottom
            };
            _statusLeft = new ToolStripStatusLabel
            {
                Spring = true,
                TextAlign = ContentAlignment.MiddleLeft
            };
            _statusRight = new ToolStripStatusLabel
            {
                AutoSize = false,
                Width = 160,
                TextAlign = ContentAlignment.MiddleRight
            };

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

            _numZoom = new NumericUpDown
            {
                Minimum = 10,
                Maximum = 400,
                Increment = 10,
                Value = 100,
                Width = 70,
                Visible = false
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

            _menu = new MenuStrip
            {
                Dock = DockStyle.Top
            };

            var menuOpen = new ToolStripMenuItem("打开");
            var openFileItem = new ToolStripMenuItem("打开文件(&F)...", null, BtnOpen_Click);
            var openFolderItem = new ToolStripMenuItem("打开文件夹(&D)...", null, OpenFolder_Click);
            menuOpen.DropDownItems.Add(openFileItem);
            menuOpen.DropDownItems.Add(openFolderItem);

            _menuExtractItem = new ToolStripMenuItem("提取(&E)...")
            {
                Visible = false
            };
            _menuExtractItem.Click += ExtractMenu_Click;

            // === 关于菜单 ===
            var menuAbout = new ToolStripMenuItem("关于");
            var checkUpdateItem = new ToolStripMenuItem("检查更新(&U)", null, CheckUpdate_Click);
            var githubItem = new ToolStripMenuItem("GitHub 主页(&G)", null, OpenGitHub_Click);
            menuAbout.DropDownItems.Add(checkUpdateItem);
            menuAbout.DropDownItems.Add(githubItem);

            _menu.Items.Add(menuOpen);
            _menu.Items.Add(_menuExtractItem);
            _menu.Items.Add(menuAbout);

            MainMenuStrip = _menu;

            Controls.Add(_split);
            Controls.Add(_statusStrip);
            Controls.Add(_menu);

            ResumeLayout(false);
            PerformLayout();
        }

        void CheckUpdate_Click(object? sender, EventArgs e)
        {
            const string releaseUrl = "https://github.com/icey9527/Verviewer/releases";
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = releaseUrl,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show(this,
                    $"无法打开浏览器：\n{ex.Message}",
                    "错误",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
        }

        void OpenGitHub_Click(object? sender, EventArgs e)
        {
            const string repoUrl = "https://github.com/icey9527/Verviewer";
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = repoUrl,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show(this,
                    $"无法打开浏览器：\n{ex.Message}",
                    "错误",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
            }
        }
    }
}