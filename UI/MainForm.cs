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
        MenuStrip _menu = null!;
        SplitContainer _split = null!;
        ListView _entryList = null!;
        RichTextBox _txtPreview = null!;
        Panel _imagePanel = null!;
        PictureBox _picPreview = null!;
        NumericUpDown _numZoom = null!;

        StatusStrip _statusStrip = null!;
        ToolStripStatusLabel _statusLeft = null!;
        ToolStripStatusLabel _statusRight = null!;
        ToolStripControlHost _encodingHost = null!;
        ToolStripControlHost _zoomHost = null!;
        ComboBox _comboEncoding = null!;

        ContextMenuStrip? _entryContextMenu;
        ContextMenuStrip? _imageContextMenu;
        ToolStripMenuItem? _menuExtractItem;
        ToolStripMenuItem? _entryExtractMenuItem;
        ToolStripMenuItem? _entryCopyImageMenuItem;

        readonly Stack<ArchiveSnapshot> _archiveHistory = new();

        sealed class ArchiveSnapshot
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

        OpenedArchive? _currentArchive;
        string? _currentArchiveRuleName;
        string? _currentImageHandlerName;
        string? _lastSelectedEntryPath;
        string _currentDir = string.Empty;

        byte[]? _lastPreviewTextData;
        ArchiveEntry? _lastTextEntry;

        Image? _originalImage;
        float _imageZoom = 1.0f;
        bool _updatingZoomControl;

        public MainForm()
        {
            var asm = typeof(MainForm).Assembly;
            const string iconResource = "Verviewer.Misc.ver.ico";
            this.Icon = asm.GetManifestResourceStream(iconResource) is var stream && stream != null
                ? new Icon(stream)
                : SystemIcons.Application;

            Text = "Verviewer";
            Width = 1400;
            Height = 800;
            StartPosition = FormStartPosition.CenterScreen;

            InitializeUi();
            InitDragDrop();
            this.MouseWheel += MainForm_MouseWheel;
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            try
            {
                int width = _split.ClientSize.Width;
                if (width <= 0) return;
                int panel1Min = 140;
                int panel2Min = 240;
                if (width < panel1Min + panel2Min)
                {
                    panel1Min = Math.Max(80, width / 3);
                    panel2Min = Math.Max(80, width - panel1Min);
                }
                _split.Panel1MinSize = panel1Min;
                _split.Panel2MinSize = panel2Min;
                int desired = 340;
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

        string CurrentPluginStatus
            => $"封包: {_currentArchiveRuleName ?? "-"}    图片: {_currentImageHandlerName ?? "-"}";

        void UpdateStatus(string left, string? right)
        {
            if (_statusLeft != null) _statusLeft.Text = left ?? string.Empty;
            if (_statusRight != null) _statusRight.Text = right ?? string.Empty;
        }
    }
}