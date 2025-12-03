using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Verviewer.UI
{
    internal sealed class NoHScrollListView : ListView
    {
        const int WS_HSCROLL = 0x00100000;
        const int SB_HORZ = 0;
        const int GWL_STYLE = -16;

        [DllImport("user32.dll")]
        static extern bool ShowScrollBar(IntPtr hWnd, int wBar, bool bShow);

        [DllImport("user32.dll", SetLastError = true)]
        static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", SetLastError = true)]
        static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        bool _adjusting;

        public NoHScrollListView()
        {
            View = View.Details;
        }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                // 去掉水平滚动条样式
                cp.Style &= ~WS_HSCROLL;
                return cp;
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            DisableHorizontalScrollBar();
            AdjustColumns();
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            AdjustColumns();
            DisableHorizontalScrollBar();
        }

        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            DisableHorizontalScrollBar();
        }

        void DisableHorizontalScrollBar()
        {
            if (!IsHandleCreated) return;

            // 再保险：把样式里的 WS_HSCROLL 位抹掉一次
            int style = GetWindowLong(Handle, GWL_STYLE);
            if ((style & WS_HSCROLL) != 0)
                SetWindowLong(Handle, GWL_STYLE, style & ~WS_HSCROLL);

            // 强制隐藏水平滚动条
            ShowScrollBar(Handle, SB_HORZ, false);
        }

        public void AdjustColumns()
        {
            if (_adjusting) return;
            if (!IsHandleCreated) return;
            if (Columns.Count < 3) return;
            if (View != View.Details) return;

            try
            {
                _adjusting = true;

                int total = ClientSize.Width;
                if (total <= 0) return;

                int w0 = Columns[0].Width; // 名称
                int w1 = Columns[1].Width; // 大小

                int w2 = total - w0 - w1;
                if (w2 < 40) w2 = 40;

                Columns[2].Width = w2;
            }
            finally
            {
                _adjusting = false;
            }
        }
    }
}