using System;
using System.Windows.Forms;

namespace Rp1PrPicker
{
    internal static class Program
    {
        [STAThread]
        static void Main()
        {
            AppConfig.Load();
            Logger.Init();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }
    }
}
