using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text;
using System.Windows.Forms;
using Verviewer.Core;
using Verviewer.Archives;

namespace Verviewer.UI
{
    internal partial class MainForm : Form
    {
        // UI 控件（在 MainForm.Ui.cs 初始化）
        private MenuStrip _menu = null!;
        private SplitContainer _split = null!;
        private TreeView _tree = null!;
        private RichTextBox _txtPreview = null!;
        private Panel _imagePanel = null!;
        private PictureBox _picPreview = null!;
        private NumericUpDown _numZoom = null!;

        private StatusStrip _statusStrip = null!;
        private ToolStripStatusLabel _statusLeft = null!;
        private ToolStripStatusLabel _statusRight = null!;
        private ToolStripControlHost _encodingHost = null!;
        private ToolStripControlHost _zoomHost = null!;
        private ComboBox _comboEncoding = null!; // 状态栏里的编码选择

        // 菜单 / 右键菜单
        private ContextMenuStrip? _treeContextMenu;
        private ContextMenuStrip? _imageContextMenu;
        private ToolStripMenuItem? _menuExtractItem;
        private ToolStripMenuItem? _menuBackItem;
        private ToolStripMenuItem? _treeExtractMenuItem;
        private ToolStripMenuItem? _treeCopyImageMenuItem;

        // 导航历史：支持从文件夹 → 封包 再“返回”
        private readonly Stack<ArchiveSnapshot> _archiveHistory = new();

        // 用于保存上一个 Archive 的上下文
        private sealed class ArchiveSnapshot
        {
            public OpenedArchive Archive { get; }
            public string Title { get; }
            public string? RuleName { get; }
            public bool MenuExtractEnabled { get; }

            public ArchiveSnapshot(OpenedArchive archive, string title, string? ruleName, bool menuExtractEnabled)
            {
                Archive = archive;
                Title = title;
                RuleName = ruleName;
                MenuExtractEnabled = menuExtractEnabled;
            }
        }

        // 按需打开的封包
        private OpenedArchive? _currentArchive;
        private string? _currentArchiveRuleName;     // 当前封包插件 Id
        private string? _currentImageHandlerName;    // 当前图片插件 Id（或 "builtin"/null）
        private string? _lastSelectedEntryPath;      // 当前选中的 ArchiveEntry.Path

        // 文本预览缓存（避免重复从封包读取）
        private byte[]? _lastPreviewTextData;
        private ArchiveEntry? _lastTextEntry;        

        // 图片缩放相关
        private Image? _originalImage;
        private float _imageZoom = 1.0f;
        private bool _updatingZoomControl;

        public MainForm()
        {
            var asm = typeof(MainForm).Assembly;
            const string iconResource = "Verviewer.Misc.ver.ico";
            this.Icon = asm.GetManifestResourceStream(iconResource) is var stream && stream != null 
                ? new Icon(stream) 
                : SystemIcons.Application;
            
            Text = "Verviewer";
            Width = 1200;
            Height = 800;
            StartPosition = FormStartPosition.CenterScreen;
            InitializeUi();

            this.MouseWheel += MainForm_MouseWheel;
        }

        private void Tree_AfterExpand(object? sender, TreeViewEventArgs e)
        {
            if (_currentArchive?.Handler is FolderArchiveHandler folderHandler)
            {
                folderHandler.LoadSubdirectories(_tree, e.Node, _currentArchive.SourcePath);
            }
        }


        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);

            // 窗口大小确定后再设置分割条，避免初始化宽度为0抛异常
            try
            {
                int width = _split.ClientSize.Width;
                if (width <= 0)
                    return;

                int panel1Min = 140;
                int panel2Min = 240;

                if (width < panel1Min + panel2Min)
                {
                    panel1Min = Math.Max(80, width / 3);
                    panel2Min = Math.Max(80, width - panel1Min);
                }

                _split.Panel1MinSize = panel1Min;
                _split.Panel2MinSize = panel2Min;

                int desired = 260;
                int min = _split.Panel1MinSize;
                int max = width - _split.Panel2MinSize;

                if (max > min)
                {
                    if (desired < min) desired = min;
                    if (desired > max) desired = max;
                    _split.SplitterDistance = desired;
                }
            }
            catch
            {
                // 忽略异常
            }

            UpdateStatus(CurrentPluginStatus, string.Empty);
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            base.OnFormClosed(e);

            _originalImage?.Dispose();
            _originalImage = null;

            _currentArchive?.Dispose();
            _currentArchive = null;
        }

        // 左下角显示
        private string CurrentPluginStatus
            => $"封包: {_currentArchiveRuleName ?? "-"}    图片: {_currentImageHandlerName ?? "-"}";

        // 更新底部状态栏（right 允许为 null，消除可空告警）
        private void UpdateStatus(string left, string? right)
        {
            if (_statusLeft != null) _statusLeft.Text = left ?? string.Empty;
            if (_statusRight != null) _statusRight.Text = right ?? string.Empty;
        }
    }
}