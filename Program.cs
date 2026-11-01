using System;
using System.Threading;
using System.Windows.Forms;

namespace ExplorerDocks
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            using var mutex = new Mutex(true, @"Local\ExplorerDocks.SingleInstance", out bool createdNew);
            if (!createdNew)
            {
                if (args.Length > 0)
                {
                    // トリミング画面などを常駐中のアプリが手前に出せるようにする
                    Win32.AllowSetForegroundWindow(-1);
                    LocalCommands.Send(args);
                }
                return;
            }

            ApplicationConfiguration.Initialize();
            Application.Run(new MainForm(args));
        }
    }
}
