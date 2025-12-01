using System;
using System.Text;
using System.Windows.Forms;
using Verviewer.UI;

namespace Verviewer
{
    internal static class Program
    {
        [STAThread]
        static void Main()
        {
            try
            {
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

                Application.Run(new MainForm());
            }
            catch (Exception ex)
            {
                // 任何在 MainForm 初始化或运行过程中的未捕获异常，都会走到这里
                MessageBox.Show(
                    ex.ToString(),
                    "程序发生未处理异常",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error
                );
            }
        }
    }
}