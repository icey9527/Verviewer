using System;
using System.IO;
using System.Windows.Forms;

namespace Verviewer.UI
{
    internal partial class MainForm
    {
        void InitDragDrop()
        {
            AllowDrop = true;
            DragEnter += MainForm_DragEnter;
            DragDrop += MainForm_DragDrop;
            DragLeave += MainForm_DragLeave;
        }

        void MainForm_DragEnter(object? sender, DragEventArgs e)
        {
            if (e.Data?.GetDataPresent(DataFormats.FileDrop) == true)
            {
                var files = e.Data.GetData(DataFormats.FileDrop) as string[];
                if (files != null && files.Length > 0)
                {
                    string path = files[0];
                    if (File.Exists(path) || Directory.Exists(path))
                    {
                        e.Effect = DragDropEffects.Copy;
                        UpdateStatus(CurrentPluginStatus, $"释放以打开: {Path.GetFileName(path)}");
                        return;
                    }
                }
            }
            e.Effect = DragDropEffects.None;
        }

        void MainForm_DragLeave(object? sender, EventArgs e)
        {
            UpdateStatus(CurrentPluginStatus, string.Empty);
        }

        void MainForm_DragDrop(object? sender, DragEventArgs e)
        {
            UpdateStatus(CurrentPluginStatus, string.Empty);
            if (e.Data?.GetDataPresent(DataFormats.FileDrop) != true) return;

            var files = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (files == null || files.Length == 0) return;

            string path = files[0];
            if (Directory.Exists(path))
                OpenFolderAsArchive(path);
            else if (File.Exists(path))
                OpenArchive(path, fromNested: false);
        }
    }
}