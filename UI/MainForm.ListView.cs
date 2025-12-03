using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using Verviewer.Archives;
using Verviewer.Core;

namespace Verviewer.UI
{
    internal partial class MainForm
    {
        readonly List<ArchiveEntry> _viewEntries = new();
        int _sortColumn;
        bool _sortAscending = true;

        void RebuildEntryList()
        {
            _entryList.BeginUpdate();
            try
            {
                _viewEntries.Clear();
                _entryList.VirtualListSize = 0;

                if (_currentArchive == null)
                    return;

                // 所有文件（目录一律由路径推导）
                var allFiles = new List<ArchiveEntry>();
                foreach (var src in _currentArchive.Entries)
                {
                    if (src.IsDirectory) continue;
                    var norm = NormalizePath(src.Path);
                    if (string.IsNullOrEmpty(norm)) continue;

                    allFiles.Add(new ArchiveEntry
                    {
                        Path = norm,
                        IsDirectory = false,
                        Offset = src.Offset,
                        Size = src.Size,
                        UncompressedSize = src.UncompressedSize
                    });
                }

                var items = new List<ArchiveEntry>();
                var dirSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var f in allFiles)
                {
                    var p = f.Path;
                    if (_currentDir.Length == 0)
                    {
                        int idx = p.IndexOf('/');
                        if (idx > 0)
                        {
                            string dirName = p.Substring(0, idx);
                            if (dirSet.Add(dirName))
                            {
                                items.Add(new ArchiveEntry
                                {
                                    Path = dirName,
                                    IsDirectory = true,
                                    Size = 0,
                                    UncompressedSize = 0
                                });
                            }
                        }
                        else
                        {
                            items.Add(f);
                        }
                    }
                    else
                    {
                        string prefix = _currentDir + "/";
                        if (!p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                            continue;

                        string rest = p.Substring(prefix.Length);
                        int idx = rest.IndexOf('/');
                        if (idx > 0)
                        {
                            string subName = rest.Substring(0, idx);
                            string fullDir = _currentDir + "/" + subName;
                            if (dirSet.Add(fullDir))
                            {
                                items.Add(new ArchiveEntry
                                {
                                    Path = fullDir,
                                    IsDirectory = true,
                                    Size = 0,
                                    UncompressedSize = 0
                                });
                            }
                        }
                        else
                        {
                            items.Add(f);
                        }
                    }
                }

                items.Sort(CompareEntries);

                // 根目录下：如果有嵌套历史，也增加 ".." 行用于“返回上一个封包”
                // 子目录下：增加 ".." 行用于返回上一级目录
                if (_currentDir.Length != 0 || _archiveHistory.Count > 0)
                {
                    _viewEntries.Add(new ArchiveEntry
                    {
                        Path = string.Empty,
                        IsDirectory = true,
                        Size = 0,
                        UncompressedSize = 0
                    });
                }

                _viewEntries.AddRange(items);
                _entryList.VirtualListSize = _viewEntries.Count;

                ( _entryList as NoHScrollListView )?.AdjustColumns();
                _entryList.Invalidate();
            }
            finally
            {
                _entryList.EndUpdate();
            }
        }

        static string NormalizePath(string path)
        {
            if (string.IsNullOrEmpty(path)) return string.Empty;
            string p = path.Replace('\\', '/').Trim();
            if (p == "." || p == "./") return string.Empty;
            if (p.StartsWith("./", StringComparison.Ordinal))
                p = p.Substring(2);
            p = p.Trim('/');
            if (p.Length == 0) return string.Empty;
            var parts = p.Split('/', StringSplitOptions.RemoveEmptyEntries)
                         .Where(x => x != ".")
                         .ToArray();
            return parts.Length == 0 ? string.Empty : string.Join("/", parts);
        }

        int CompareEntries(ArchiveEntry a, ArchiveEntry b)
        {
            bool aDir = a.IsDirectory;
            bool bDir = b.IsDirectory;

            if (aDir && !bDir) return -1;
            if (!aDir && bDir) return 1;

            string nameA = a.Path.Length == 0 ? ".." : Path.GetFileName(a.Path);
            string nameB = b.Path.Length == 0 ? ".." : Path.GetFileName(b.Path);

            switch (_sortColumn)
            {
                case 0:
                    return _sortAscending
                        ? string.Compare(nameA, nameB, StringComparison.CurrentCultureIgnoreCase)
                        : string.Compare(nameB, nameA, StringComparison.CurrentCultureIgnoreCase);

                case 1:
                    long sa = aDir ? 0 : a.Size;
                    long sb = bDir ? 0 : b.Size;
                    int c = sa.CompareTo(sb);
                    if (c != 0) return _sortAscending ? c : -c;
                    return _sortAscending
                        ? string.Compare(nameA, nameB, StringComparison.CurrentCultureIgnoreCase)
                        : string.Compare(nameB, nameA, StringComparison.CurrentCultureIgnoreCase);

                case 2:
                    string typeA = aDir ? "文件夹" : (Path.GetExtension(a.Path)?.Trim('.').ToLowerInvariant() ?? "");
                    string typeB = bDir ? "文件夹" : (Path.GetExtension(b.Path)?.Trim('.').ToLowerInvariant() ?? "");
                    int ct = string.Compare(typeA, typeB, StringComparison.CurrentCultureIgnoreCase);
                    if (ct != 0) return _sortAscending ? ct : -ct;
                    return _sortAscending
                        ? string.Compare(nameA, nameB, StringComparison.CurrentCultureIgnoreCase)
                        : string.Compare(nameB, nameA, StringComparison.CurrentCultureIgnoreCase);

                default:
                    return 0;
            }
        }

        bool TryGetSingleSelectedEntry(out ArchiveEntry entry)
        {
            entry = default;
            if (_entryList.SelectedIndices.Count != 1) return false;
            int idx = _entryList.SelectedIndices[0];
            if (idx < 0 || idx >= _viewEntries.Count) return false;
            entry = _viewEntries[idx];
            return true;
        }

        List<ArchiveEntry> GetSelectedEntries()
        {
            var list = new List<ArchiveEntry>(_entryList.SelectedIndices.Count);
            foreach (int idx in _entryList.SelectedIndices)
            {
                if (idx >= 0 && idx < _viewEntries.Count)
                    list.Add(_viewEntries[idx]);
            }
            return list;
        }

        void EntryList_RetrieveVirtualItem(object? sender, RetrieveVirtualItemEventArgs e)
        {
            if (e.ItemIndex < 0 || e.ItemIndex >= _viewEntries.Count)
            {
                e.Item = new ListViewItem(string.Empty);
                return;
            }

            var entry = _viewEntries[e.ItemIndex];

            string name;
            string size;
            string type;

            if (entry.Path.Length == 0 && entry.IsDirectory)
            {
                name = "..";
                size = "";
                type = "文件夹";
            }
            else
            {
                name = Path.GetFileName(entry.Path);
                size = entry.IsDirectory ? "" : (entry.Size >= 0 ? entry.Size.ToString() : "");
                type = entry.IsDirectory ? "<文件夹>" : (Path.GetExtension(entry.Path)?.Trim('.').ToLowerInvariant() ?? "");
            }

            var item = new ListViewItem(name);
            item.SubItems.Add(size);
            item.SubItems.Add(type);
            e.Item = item;
        }

        void EntryList_SelectedIndexChanged(object? sender, EventArgs e)
        {
            if (_currentArchive == null) return;

            if (!TryGetSingleSelectedEntry(out var entry))
            {
                _lastSelectedEntryPath = null;
                _lastPreviewTextData = null;
                _lastTextEntry = null;
                _currentImageHandlerName = null;
                _originalImage?.Dispose();
                _originalImage = null;
                _picPreview.Image?.Dispose();
                _picPreview.Image = null;
                _imagePanel.Visible = false;
                _txtPreview.Visible = true;
                _encodingHost.Visible = true;
                _numZoom.Visible = false;
                _txtPreview.Clear();
                UpdateStatus(CurrentPluginStatus, _statusRight.Text);
                return;
            }

            if (entry.Path.Length == 0 && entry.IsDirectory)
            {
                _lastSelectedEntryPath = null;
                _lastPreviewTextData = null;
                _lastTextEntry = null;
                _currentImageHandlerName = null;
                _originalImage?.Dispose();
                _originalImage = null;
                _picPreview.Image?.Dispose();
                _picPreview.Image = null;
                _imagePanel.Visible = false;
                _txtPreview.Visible = true;
                _encodingHost.Visible = true;
                _numZoom.Visible = false;
                _txtPreview.Clear();
                UpdateStatus(CurrentPluginStatus, _statusRight.Text);
                return;
            }

            _lastSelectedEntryPath = entry.Path;

            if (entry.IsDirectory)
            {
                _lastPreviewTextData = null;
                _lastTextEntry = null;
                _currentImageHandlerName = null;
                _originalImage?.Dispose();
                _originalImage = null;
                _picPreview.Image?.Dispose();
                _picPreview.Image = null;
                _imagePanel.Visible = false;
                _txtPreview.Visible = true;
                _encodingHost.Visible = true;
                _numZoom.Visible = false;
                _txtPreview.Clear();
                UpdateStatus(CurrentPluginStatus, _statusRight.Text);
                return;
            }

            PreviewEntry(entry);
        }

        void EntryList_MouseDoubleClick(object? sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Left) return;
            if (!TryGetSingleSelectedEntry(out var entry)) return;

            if (entry.IsDirectory)
            {
                if (entry.Path.Length == 0)
                {
                    if (_currentDir.Length > 0)
                    {
                        int idx = _currentDir.LastIndexOf('/');
                        _currentDir = idx > 0 ? _currentDir.Substring(0, idx) : string.Empty;
                        RebuildEntryList();
                    }
                    else if (_archiveHistory.Count > 0)
                    {
                        // 根目录下的 ".."：返回上一个封包
                        MenuBack_Click(this, EventArgs.Empty);
                    }
                }
                else
                {
                    _currentDir = entry.Path;
                    RebuildEntryList();
                }
                return;
            }

            if (_currentArchive?.Handler is FolderArchiveHandler)
                TryOpenEntryAsArchive(entry);
        }

        void EntryList_MouseDown(object? sender, MouseEventArgs e)
        {
            if (e.Button != MouseButtons.Right) return;
            var hit = _entryList.HitTest(e.Location);
            int idx = hit.Item?.Index ?? -1;
            if (idx < 0 || idx >= _viewEntries.Count) return;

            if (_entryList.SelectedIndices.Contains(idx))
                return;

            if ((Control.ModifierKeys & Keys.Control) != 0 ||
                (Control.ModifierKeys & Keys.Shift) != 0)
                return;

            _entryList.SelectedIndices.Clear();
            _entryList.SelectedIndices.Add(idx);
        }

        void EntryList_ColumnClick(object? sender, ColumnClickEventArgs e)
        {
            if (e.Column == _sortColumn)
                _sortAscending = !_sortAscending;
            else
            {
                _sortColumn = e.Column;
                _sortAscending = true;
            }
            ApplySort();
        }

        void ApplySort()
        {
            if (_viewEntries.Count <= 1) return;

            if (_currentDir.Length != 0 || _archiveHistory.Count > 0)
            {
                var up = _viewEntries[0];
                var rest = _viewEntries.Skip(1).ToList();
                rest.Sort(CompareEntries);
                _viewEntries.Clear();
                _viewEntries.Add(up);
                _viewEntries.AddRange(rest);
            }
            else
            {
                _viewEntries.Sort(CompareEntries);
            }

            (_entryList as NoHScrollListView)?.AdjustColumns();
            _entryList.Invalidate();
        }

        void EntryList_KeyDown(object? sender, KeyEventArgs e)
        {
            if (e.Control && e.KeyCode == Keys.A)
            {
                _entryList.BeginUpdate();
                _entryList.SelectedIndices.Clear();
                for (int i = 0; i < _entryList.VirtualListSize; i++)
                    _entryList.SelectedIndices.Add(i);
                _entryList.EndUpdate();
                e.Handled = true;
            }
        }

        void EntryContextMenu_Opening(object? sender, System.ComponentModel.CancelEventArgs e)
        {
            int selCount = _entryList.SelectedIndices.Count;
            bool hasEntry = selCount > 0;

            if (_entryExtractMenuItem != null)
                _entryExtractMenuItem.Enabled = _currentArchive != null && hasEntry;

            if (_entryCopyImageMenuItem != null)
            {
                bool isImagePreview = _imagePanel.Visible && _originalImage != null;
                bool canCopyImage = selCount == 1 && TryGetSingleSelectedEntry(out var entry) &&
                                    !entry.IsDirectory && entry.Path.Length != 0;
                _entryCopyImageMenuItem.Visible = isImagePreview && canCopyImage;
            }
        }

        void TryOpenEntryAsArchive(ArchiveEntry entry)
        {
            if (_currentArchive == null) return;
            if (!(_currentArchive.Handler is FolderArchiveHandler)) return;

            string rootFolder = _currentArchive.SourcePath;
            string relPath = entry.Path.Replace('/', Path.DirectorySeparatorChar);
            string fullPath = Path.Combine(rootFolder, relPath);
            if (!File.Exists(fullPath)) return;

            try
            {
                using var fsProbe = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                var types = PluginFactory.ResolveArchiveTypes(fullPath, fsProbe).ToList();
                if (types.Count == 0) return;
            }
            catch
            {
                return;
            }

            OpenArchive(fullPath, fromNested: true);
        }
    }
}