using Microsoft.Win32;
using System;
using System.IO;

namespace ExplorerDocks
{
    internal static class ShellIntegration
    {
        private const string ClassicKey =
            @"Software\Classes\CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}";
        private const string OwnedKey = @"Software\ExplorerDocks";
        private static readonly string[] ShellRoots =
        {
            @"Software\Classes\Directory\shell",
            @"Software\Classes\Directory\Background\shell",
            @"Software\Classes\DesktopBackground\Shell",
        };

        private const string OpenExplorerKey =
            @"Software\Classes\DesktopBackground\Shell\ExplorerDocksOpenExplorer";

        public static void EnsureExplorerLaunchMenu()
        {
            // Explorer Docksが起動していなくても利用できる、デスクトップ専用の項目。
            var explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
            using var key = Registry.CurrentUser.CreateSubKey(OpenExplorerKey)
                ?? throw new IOException("エクスプローラー起動メニューを登録できませんでした。");
            key.SetValue(null, "エクスプローラーを起動", RegistryValueKind.String);
            key.SetValue("Icon", $"{explorer},0", RegistryValueKind.String);
            using var command = key.CreateSubKey("command")
                ?? throw new IOException("エクスプローラー起動コマンドを登録できませんでした。");
            command.SetValue(null, $"\"{explorer}\"", RegistryValueKind.String);
        }

        public static bool IsClassicMenuEnabled
        {
            get
            {
                using var key = Registry.CurrentUser.OpenSubKey(ClassicKey + @"\InprocServer32");
                return key is not null && key.GetValue(null) is string value && value == "";
            }
        }

        public static void SetClassicMenuEnabled(bool enabled)
        {
            if (enabled)
            {
                if (IsClassicMenuEnabled) return;
                using (var key = Registry.CurrentUser.CreateSubKey(ClassicKey + @"\InprocServer32"))
                {
                    if (key is null) throw new IOException("右クリックメニューの設定を書き込めませんでした。");
                    key.SetValue(null, "", RegistryValueKind.String);
                }
                if (!IsClassicMenuEnabled)
                    throw new IOException("右クリックメニューの設定を確認できませんでした。");
                using var owned = Registry.CurrentUser.CreateSubKey(OwnedKey);
                owned?.SetValue("ClassicMenuManaged", 1, RegistryValueKind.DWord);
            }
            else
            {
                using var owned = Registry.CurrentUser.OpenSubKey(OwnedKey, writable: true);
                if (Convert.ToInt32(owned?.GetValue("ClassicMenuManaged") ?? 0) != 1)
                    throw new InvalidOperationException("Explorer Docks以外で設定された従来メニューは変更しません。");
                // 自分が作成したCLSID設定を元へ戻す。
                Registry.CurrentUser.DeleteSubKeyTree(ClassicKey, false);
                owned?.DeleteValue("ClassicMenuManaged", throwOnMissingValue: false);
            }
        }

        public static void RemoveLegacyLinkMenus()
        {
            foreach (var root in ShellRoots)
                Registry.CurrentUser.DeleteSubKeyTree(root + @"\ExplorerDocksLink", false);
        }

        // 画像として扱われる全形式(PerceivedType=image)の右クリックに出す
        private const string ImageCropKey =
            @"Software\Classes\SystemFileAssociations\image\shell\ExplorerDocksCrop";

        public static bool IsImageCropMenuEnabled
        {
            get
            {
                using var key = Registry.CurrentUser.OpenSubKey(ImageCropKey);
                return key is not null;
            }
        }

        public static void SetImageCropMenuEnabled(bool enabled)
        {
            if (!enabled)
            {
                Registry.CurrentUser.DeleteSubKeyTree(ImageCropKey, false);
                return;
            }
            string path = Environment.ProcessPath ?? "";
            if (!File.Exists(path) || !string.Equals(Path.GetFileName(path), "ExplorerDocks.exe",
                    StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("配布版のExplorerDocks.exeから設定してください。");
            using var item = Registry.CurrentUser.CreateSubKey(ImageCropKey)
                ?? throw new IOException("右クリックメニューを登録できませんでした。");
            item.SetValue(null, "画像をトリミング", RegistryValueKind.String);
            item.SetValue("Icon", path, RegistryValueKind.String);
            // 複数選択時に画面がたくさん開かないよう、1枚のときだけ表示する
            item.SetValue("MultiSelectModel", "Single", RegistryValueKind.String);
            using var command = item.CreateSubKey("command")
                ?? throw new IOException("右クリックメニューのコマンドを登録できませんでした。");
            command.SetValue(null, $"\"{path}\" --crop \"%1\"", RegistryValueKind.String);
        }

        public static bool IsShellMenuEnabled
        {
            get
            {
                using var key = Registry.CurrentUser.OpenSubKey(
                    ShellRoots[0] + @"\ExplorerDocksWork");
                return key is not null;
            }
        }

        public static void SetShellMenuEnabled(bool enabled)
        {
            string path = Environment.ProcessPath ?? "";
            if (enabled && (!File.Exists(path) ||
                !string.Equals(Path.GetFileName(path), "ExplorerDocks.exe",
                    StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("配布版のExplorerDocks.exeから設定してください。");

            foreach (var root in ShellRoots)
            {
                // 旧版の「リンクに追加」を確実に除去する。
                Registry.CurrentUser.DeleteSubKeyTree(root + @"\ExplorerDocksLink", false);
                string regPath = root + @"\ExplorerDocksWork";
                if (!enabled)
                {
                    Registry.CurrentUser.DeleteSubKeyTree(regPath, false);
                    continue;
                }
                using var item = Registry.CurrentUser.CreateSubKey(regPath);
                if (item is null) throw new IOException("右クリックメニューを登録できませんでした。");
                item.SetValue(null, "Workに追加", RegistryValueKind.String);
                item.SetValue("Icon", path, RegistryValueKind.String);
                using var command = item.CreateSubKey("command");
                string argument = root.Contains(@"\Directory\shell", StringComparison.Ordinal)
                    ? "%1" : "%V";
                command?.SetValue(null, $"\"{path}\" --work-add \"{argument}\"", RegistryValueKind.String);
            }
        }
    }
}
