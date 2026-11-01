using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace ExplorerDocks
{
    internal static class ExplorerFolders
    {
        public const string ThisPc = "shell:MyComputerFolder";

        public static bool CanRestore(string path) =>
            string.Equals(path, ThisPc, StringComparison.OrdinalIgnoreCase) ||
            (Path.IsPathFullyQualified(path) && Directory.Exists(path));

        public static Dictionary<IntPtr, string> List()
        {
            var result = new Dictionary<IntPtr, string>();
            object? shell = null;
            object? windows = null;
            try
            {
                var type = Type.GetTypeFromProgID("Shell.Application");
                if (type is null) return result;
                shell = Activator.CreateInstance(type);
                if (shell is null) return result;
                dynamic app = shell;
                windows = app.Windows();
                dynamic collection = windows;
                int count = collection.Count;
                for (int i = 0; i < count; i++)
                {
                    object? item = null;
                    try
                    {
                        item = collection.Item(i);
                        if (item is null) continue;
                        dynamic window = item;
                        var hwnd = new IntPtr(Convert.ToInt64(window.HWND));
                        if (!Win32.IsExplorerWindow(hwnd)) continue;
                        string? url = Convert.ToString((object?)window.LocationURL);
                        if (!string.IsNullOrWhiteSpace(url) &&
                            Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) && uri.IsFile)
                        {
                            string path = Path.GetFullPath(uri.LocalPath);
                            if (Directory.Exists(path)) result[hwnd] = path;
                        }
                        else
                        {
                            // PCは実フォルダーではない。明示的な識別子で保存し、
                            // 保存対象に存在するときだけ復元する。
                            string title = Convert.ToString((object?)window.LocationName) ?? "";
                            if (title is "PC" or "This PC" or "Computer")
                                result[hwnd] = ThisPc;
                        }
                    }
                    catch { /* 仮想フォルダーや閉じかけのウィンドウは対象外 */ }
                    finally { Release(item); }
                }
            }
            catch { /* Shell COMが取得できなければ復元をスキップ */ }
            finally
            {
                Release(windows);
                Release(shell);
            }
            return result;
        }

        public static string? ForWindow(IntPtr hwnd)
        {
            var windows = List();
            return windows.TryGetValue(hwnd, out string? path) ? path : null;
        }

        private static void Release(object? value)
        {
            if (value is not null && Marshal.IsComObject(value))
            {
                try { Marshal.ReleaseComObject(value); } catch { }
            }
        }
    }
}
