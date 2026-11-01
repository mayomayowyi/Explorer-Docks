using Microsoft.Win32;
using System;
using System.IO;

namespace ExplorerDocks
{
    internal static class StartupRegistration
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "ExplorerDocks";

        private static string? ExecutablePath
        {
            get
            {
                var path = Environment.ProcessPath;
                if (path is null || !Path.GetFileName(path).Equals("ExplorerDocks.exe", StringComparison.OrdinalIgnoreCase))
                    return null;
                var dir = AppContext.BaseDirectory;
                return File.Exists(Path.Combine(dir, "explorerdocks-portable.marker")) ||
                       File.Exists(Path.Combine(dir, "explorerdocks-installed.marker")) ? path : null;
            }
        }

        public static bool IsSupported => ExecutablePath is not null;

        public static bool IsEnabled
        {
            get
            {
                var path = ExecutablePath;
                if (path is null) return false;
                try
                {
                    using var key = Registry.CurrentUser.OpenSubKey(RunKey);
                    return string.Equals(key?.GetValue(ValueName) as string, $"\"{path}\"",
                        StringComparison.OrdinalIgnoreCase);
                }
                catch (UnauthorizedAccessException) { return false; }
                catch (System.Security.SecurityException) { return false; }
            }
        }

        public static void SetEnabled(bool enabled)
        {
            var path = ExecutablePath ?? throw new InvalidOperationException("配布版から起動してください。");
            using var key = Registry.CurrentUser.CreateSubKey(RunKey)
                ?? throw new IOException("自動起動の設定を開けません。");
            if (enabled)
            {
                string command = $"\"{path}\"";
                if (!string.Equals(key.GetValue(ValueName) as string, command, StringComparison.OrdinalIgnoreCase))
                    key.SetValue(ValueName, command, RegistryValueKind.String);
            }
            else if (key.GetValue(ValueName) is not null)
            {
                key.DeleteValue(ValueName, false);
            }
        }
    }
}
