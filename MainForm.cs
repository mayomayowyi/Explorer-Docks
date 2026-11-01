using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Diagnostics;
using System.Text.Json;
using System.Windows.Forms;

namespace ExplorerDocks
{
    /// <summary>
    /// 常駐専用の非表示ホスト。操作はエクスプローラー上のDockボタンとタスクトレイから行う。
    /// </summary>
    internal sealed class MainForm : Form
    {
        private readonly DockCoordinator _dock = new();
        private readonly NotifyIcon _notifyIcon = new();
        private readonly TrayContextMenuStrip _trayMenu = new();
        private readonly string _settingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ExplorerDocks", "settings.json");

        private DockButtonManager? _buttons;
        private Icon? _trayIcon;
        private bool _cleanupDone;
        private bool _checkingUpdates;
        private bool _startAtSignIn;
        private readonly System.Threading.CancellationTokenSource _ipcStop = new();
        private bool _restoreDocksOnStart = true;
        private bool _skipExplorerRestartConfirmation;
        private bool _restartingExplorer;
        private bool _imageCropMenu = true;

        public MainForm(string[] args)
        {
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            Text = "Explorer Docks";

            // 管理ウィンドウは表示しないが、メッセージループ用にハンドルは作っておく
            _ = Handle;

            var settings = ReadSettings();
            _dock.AnimationsEnabled = settings.AnimationsEnabled;
            _dock.CollapseDelayMs = settings.CollapseDelayMs;
            _dock.ShowLabels = settings.ShowLabels;
            _dock.PlacementMode = settings.PlacementMode;
            _restoreDocksOnStart = settings.RestoreDocksOnStart;
            _skipExplorerRestartConfirmation = settings.SkipExplorerRestartConfirmation;
            _startAtSignIn = settings.StartAtSignIn;
            _imageCropMenu = settings.ImageCropMenu;
            if (StartupRegistration.IsSupported)
            {
                try { ShellIntegration.EnsureExplorerLaunchMenu(); }
                catch { /* デスクトップメニューの登録に失敗しても常駐は継続する */ }
            }
            try { ShellIntegration.RemoveLegacyLinkMenus(); }
            catch { /* 旧メニューの削除に失敗しても起動する */ }
            if (ShellIntegration.IsShellMenuEnabled && StartupRegistration.IsSupported)
            {
                try { ShellIntegration.SetShellMenuEnabled(true); }
                catch { /* インストール先変更後の修復は失敗しても起動を継続 */ }
            }
            if (StartupRegistration.IsSupported)
            {
                try { StartupRegistration.SetEnabled(_startAtSignIn); }
                catch { /* 起動は継続し、トレイメニューから再設定できる */ }
            }
            // インストール先が変わっても正しいexeを指すよう、起動のたびに登録し直す。
            // 配布版でなくても、ExplorerDocks.exeから起動していれば登録する。
            try { ShellIntegration.SetImageCropMenuEnabled(_imageCropMenu); }
            catch { /* 登録できなくても常駐は継続する */ }
            _buttons = new DockButtonManager(
                DockButtonClicked,
                _ => true,
                hwnd => _dock.Contains(hwnd),
                hwnd => _dock.IsPinned(hwnd),
                hwnd =>
                {
                    _dock.TogglePin(hwnd);
                    _buttons?.Refresh();
                }, _dock.ShowContextMenu);
            _buttons.AnimationsEnabled = settings.AnimationsEnabled;
            _dock.StatusChanged += OnStatusChanged;
            _dock.DockVisibilityChanged += OnDockVisibilityChanged;
            _dock.WorkRequested += h => PromptWork(h);
            _dock.WorkCreateRequested += PromptCreateWorkAndAdd;
            _dock.RenameRequested += PromptRename;

            BuildTrayIcon();

            _dock.RestoreLeftoverWindows();
            _ = LocalCommands.ListenAsync(command =>
            {
                try
                {
                    if (IsHandleCreated && !IsDisposed)
                        BeginInvoke(new Action(() => _ = ProcessShellCommandAsync(command)));
                }
                catch (InvalidOperationException) { }
            }, _ipcStop.Token);

            if (_restoreDocksOnStart || args.Length > 0)
                BeginInvoke(new Action(() => _ = RestoreThenCommandAsync(args)));

            // メッセージループが開始してから、公開済みの場合だけ一度確認する。
            if (UpdateService.IsConfigured)
                BeginInvoke(new Action(() => _ = CheckForUpdatesAsync(false)));
        }

        protected override void SetVisibleCore(bool value)
        {
            base.SetVisibleCore(false);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && !_cleanupDone)
            {
                _cleanupDone = true;
                _ipcStop.Cancel();
                _ipcStop.Dispose();
                _dock.StatusChanged -= OnStatusChanged;
                _dock.DockVisibilityChanged -= OnDockVisibilityChanged;
                _buttons?.Dispose();
                _notifyIcon.Visible = false;
                _notifyIcon.Dispose();
                _trayMenu.Dispose();
                _trayIcon?.Dispose();
                // Dock状態は保存し、Explorer本体は元の状態に戻して終了する。
                if (_dock.IsDocked) _dock.SaveCurrentSessions();
                _dock.Dispose();
            }
            base.Dispose(disposing);
        }

        private void DockButtonClicked(IntPtr hWnd)
        {
            try
            {
                if (_dock.Contains(hWnd))
                {
                    _dock.Undock(hWnd);
                }
                else
                {
                    _dock.Dock(hWnd);
                }
            }
            catch (Exception ex)
            {
                OnStatusChanged(ex.Message);
            }
        }

        private void UndockAll()
        {
            if (!_dock.IsDocked)
            {
                OnStatusChanged("Dock中のエクスプローラーはありません。");
                return;
            }

            try
            {
                _dock.UndockAll();
            }
            catch (Exception ex)
            {
                OnStatusChanged($"Dock解除に失敗しました: {ex.Message}");
            }
        }

        private void OnDockVisibilityChanged(IntPtr hWnd, bool visible)
        {
            _buttons?.SetDockWindowVisible(hWnd, visible);
        }

        private void OnStatusChanged(string status)
        {
            _buttons?.Refresh();

            _notifyIcon.Text = status.Length <= 63 ? status : status[..63];
        }

        private sealed class AppSettings
        {
            public bool AnimationsEnabled { get; set; } = true;
            public int CollapseDelayMs { get; set; } = 280;
            public bool StartAtSignIn { get; set; } = true;
            public bool ShowLabels { get; set; } = true;
            public DockPlacementMode PlacementMode { get; set; } = DockPlacementMode.Aligned;
            public bool RestoreDocksOnStart { get; set; } = true;
            public bool SkipExplorerRestartConfirmation { get; set; }
            public bool ImageCropMenu { get; set; } = true;
        }

        private AppSettings ReadSettings()
        {
            var settings = new AppSettings();
            try
            {
                if (!File.Exists(_settingsPath)) return settings;
                using var json = JsonDocument.Parse(File.ReadAllText(_settingsPath));
                if (json.RootElement.TryGetProperty("AnimationsEnabled", out var enabled)
                    && enabled.ValueKind is JsonValueKind.True or JsonValueKind.False)
                {
                    settings.AnimationsEnabled = enabled.GetBoolean();
                }
                if (json.RootElement.TryGetProperty("StartAtSignIn", out var startup)
                    && startup.ValueKind is JsonValueKind.True or JsonValueKind.False)
                {
                    settings.StartAtSignIn = startup.GetBoolean();
                }
                if (json.RootElement.TryGetProperty("ShowLabels", out var labels)
                    && labels.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    settings.ShowLabels = labels.GetBoolean();
                if (json.RootElement.TryGetProperty("PlacementMode", out var mode)
                    && mode.ValueKind == JsonValueKind.Number
                    && mode.TryGetInt32(out int modeValue)
                    && Enum.IsDefined(typeof(DockPlacementMode), modeValue))
                    settings.PlacementMode = (DockPlacementMode)modeValue;
                if (json.RootElement.TryGetProperty("RestoreDocksOnStart", out var restore)
                    && restore.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    settings.RestoreDocksOnStart = restore.GetBoolean();
                if (json.RootElement.TryGetProperty("SkipExplorerRestartConfirmation", out var skip)
                    && skip.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    settings.SkipExplorerRestartConfirmation = skip.GetBoolean();
                if (json.RootElement.TryGetProperty("ImageCropMenu", out var crop)
                    && crop.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    settings.ImageCropMenu = crop.GetBoolean();
                if (json.RootElement.TryGetProperty("CollapseDelayMs", out var delay)
                    && delay.ValueKind == JsonValueKind.Number)
                {
                    settings.CollapseDelayMs = delay.GetInt32();
                }
            }
            catch { /* 読み込み失敗時は既定値を使う */ }
            if (settings.CollapseDelayMs < -1 || settings.CollapseDelayMs > 10000)
            {
                settings.CollapseDelayMs = 280;
            }
            return settings;
        }

        private bool SaveSettings()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
                File.WriteAllText(_settingsPath,
                    JsonSerializer.Serialize(new { _dock.AnimationsEnabled, _dock.CollapseDelayMs,
                        _dock.ShowLabels,
                        PlacementMode = (int)_dock.PlacementMode,
                        StartAtSignIn = _startAtSignIn,
                        RestoreDocksOnStart = _restoreDocksOnStart,
                        SkipExplorerRestartConfirmation = _skipExplorerRestartConfirmation,
                        ImageCropMenu = _imageCropMenu }));
                return true;
            }
            catch { return false; }
        }

        private void SetCollapseDelay(int ms)
        {
            _dock.CollapseDelayMs = ms;
            SaveSettings();
            OnStatusChanged($"収納までの時間を {FormatDelay(_dock.CollapseDelayMs)} に設定しました。");
        }

        private void PromptCollapseDelay()
        {
            using var form = new Form
            {
                Text = "収納までの時間",
                TopMost = true,
            };
            ManagementUi.ConfigureForm(form);
            var numeric = new NumericUpDown
            {
                Minimum = 0,
                Maximum = 10000,
                Increment = 50,
                ThousandsSeparator = true,
                Value = _dock.CollapseDelayMs < 0 ? 280 : _dock.CollapseDelayMs,
            };
            ManagementUi.AddDialogContent(form, "自動収納までの時間（ミリ秒）", numeric,
                ManagementUi.Button("保存", DialogResult.OK),
                "マウスが離れてから収納を始めるまでの待ち時間です。\n1,000ミリ秒 = 1秒 ／ 設定範囲：0～10,000ミリ秒");
            form.Shown += (_, _) => numeric.Select(0, numeric.Text.Length);
            if (form.ShowDialog() == DialogResult.OK)
            {
                SetCollapseDelay((int)numeric.Value);
            }
        }


        private async System.Threading.Tasks.Task RestoreThenCommandAsync(string[] args)
        {
            try
            {
                if (_restoreDocksOnStart) await _dock.RestoreSavedAsync();
                if (args.Length > 0) await ProcessShellCommandAsync(args);
            }
            catch (Exception ex) { OnStatusChanged(ex.Message); }
        }

        private void ShowWorkManager(string? work = null)
        {
            using var manager = new WorkManagerForm(_dock, work);
            manager.ShowDialog();
            _buttons?.Refresh();
        }

        private void PromptCreateWork()
        {
            string? name = ManagementUi.AskName(null, "Workの新規作成", "Work名", "", "作成");
            if (name is null) return;
            try { _dock.CreateWork(name); }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Explorer Docks",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void PromptCreateWorkAndAdd(IntPtr hwnd)
        {
            string? name = ManagementUi.AskName(null, "Workの新規作成", "Work名", "", "作成して追加");
            if (name is null) return;
            try
            {
                _dock.CreateWork(name);
                _dock.AddToWork(hwnd, name);
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Explorer Docks",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void PromptRename(IntPtr hwnd)
        {
            string? name = ManagementUi.AskName(null, "収納名の変更", "表示名", _dock.HandleName(hwnd), "保存");
            if (name is null) return;
            try { _dock.RenameHandle(hwnd, name); }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Explorer Docks",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void PromptWork(IntPtr hwnd)
        {
            string[] works = _dock.WorkNames;
            if (works.Length == 0)
            {
                MessageBox.Show("Workがありません。先に新規作成してください。",
                    "Workに追加", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            using var dialog = new Form { Text = "Workに追加", TopMost = true };
            ManagementUi.ConfigureForm(dialog);
            var choices = new ListBox
            {
                IntegralHeight = false,
                Height = Math.Min(208, Math.Max(90, works.Length * 27 + 8)),
                BorderStyle = BorderStyle.FixedSingle,
            };
            choices.Items.AddRange(works);
            string? current = _dock.CurrentWorkName(hwnd);
            if (current is not null && works.Contains(current, StringComparer.OrdinalIgnoreCase))
                choices.SelectedItem = works.First(name => string.Equals(name, current, StringComparison.OrdinalIgnoreCase));
            else if (works.Length == 1) choices.SelectedIndex = 0;

            var add = ManagementUi.Button("追加", DialogResult.OK);
            add.Enabled = choices.SelectedIndex >= 0;
            choices.SelectedIndexChanged += (_, _) => add.Enabled = choices.SelectedIndex >= 0;
            choices.DoubleClick += (_, _) =>
            {
                if (choices.SelectedIndex < 0) return;
                dialog.DialogResult = DialogResult.OK;
                dialog.Close();
            };
            ManagementUi.AddDialogContent(dialog, "追加先のWork", choices, add,
                current is null ? null : $"Work「{current}」のフォルダーも一緒に移動します。");
            if (dialog.ShowDialog() != DialogResult.OK || choices.SelectedItem is not string selected) return;
            try { _dock.AddToWork(hwnd, selected); }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Explorer Docks", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void OpenImageCropper(string path)
        {
            string file;
            try { file = Path.GetFullPath(path); }
            catch { return; }
            if (!File.Exists(file)) { OnStatusChanged("画像ファイルが見つかりません。"); return; }
            var form = new ImageCropForm(file);
            form.FormClosed += (_, _) => form.Dispose();
            form.Saved += saved => OnStatusChanged($"保存しました: {Path.GetFileName(saved)}");
            form.Show();
            form.Activate();
            Win32.SetForegroundWindow(form.Handle);
        }

        private async System.Threading.Tasks.Task ProcessShellCommandAsync(string[] args)
        {
            if (args.Length == 2 && args[0] == "--crop")
            {
                OpenImageCropper(args[1]);
                return;
            }
            if (args.Length != 2 || args[0] != "--work-add") return;
            string folder;
            try { folder = Path.GetFullPath(args[1]); }
            catch { return; }
            if (!Directory.Exists(folder)) return;
            IntPtr hwnd = IntPtr.Zero;
            var used = _dock.DockedFolders.Select(e => e.Window).ToHashSet();
            var open = ExplorerFolders.List();
            foreach (var (h, p) in open)
                if (string.Equals(folder, p, StringComparison.OrdinalIgnoreCase)
                    && !used.Contains(h)) { hwnd = h; break; }
            if (hwnd == IntPtr.Zero)
                foreach (var (h, p) in open)
                    if (string.Equals(folder, p, StringComparison.OrdinalIgnoreCase))
                    { hwnd = h; break; }
            if (hwnd == IntPtr.Zero)
            {
                try
                {
                    var start = new ProcessStartInfo("explorer.exe") { UseShellExecute = true };
                    start.Arguments = "/n,\"" + folder + "\"";
                    Process.Start(start);
                }
                catch { return; }
                for (int i = 0; i < 32 && hwnd == IntPtr.Zero; i++)
                {
                    await System.Threading.Tasks.Task.Delay(250);
                    if (IsDisposed) return;
                    foreach (var (h, p) in ExplorerFolders.List())
                        if (string.Equals(folder, p, StringComparison.OrdinalIgnoreCase)
                            && !used.Contains(h)) { hwnd = h; break; }
                }
            }
            if (hwnd == IntPtr.Zero) { OnStatusChanged("フォルダーを開けませんでした。"); return; }
            try
            {
                if (!_dock.Contains(hwnd)) _dock.Dock(hwnd);
                PromptWork(hwnd);
            }
            catch (Exception ex) { OnStatusChanged(ex.Message); }
        }

        private static string FormatDelay(int ms)
            => ms < 0 ? "無制限（クリックで収納）" : ms >= 1000 ? $"{ms / 1000.0:0.##}秒" : $"{ms}ms";

        /// <summary>画面端のハンドルと同じ配色で、ウィンドウが端に寄った形のアイコンを描く。</summary>
        private static Icon CreateTrayIcon()
        {
            int size = SystemInformation.SmallIconSize.Width;
            float s = size / 16f;
            using var bitmap = new Bitmap(size, size);
            using (var g = Graphics.FromImage(bitmap))
            {
                g.Clear(Color.Transparent);
                using var accent = new SolidBrush(Color.FromArgb(81, 137, 212));
                using var titleBar = new SolidBrush(Color.FromArgb(43, 58, 103));
                g.FillRectangle(accent, 1 * s, 2 * s, 10 * s, 12 * s);
                g.FillRectangle(titleBar, 1 * s, 2 * s, 10 * s, 3 * s);
                g.FillRectangle(accent, 12.5f * s, 4 * s, 2.5f * s, 8 * s);
            }
            IntPtr hIcon = bitmap.GetHicon();
            try
            {
                // FromHandleはハンドルを所有しないため、所有するコピーを作ってから元を破棄する
                using var borrowed = Icon.FromHandle(hIcon);
                return (Icon)borrowed.Clone();
            }
            finally
            {
                Win32.DestroyIcon(hIcon);
            }
        }

        private static Icon LoadTrayIcon()
        {
            using var stream = typeof(MainForm).Assembly.GetManifestResourceStream("ExplorerDocks.AppIcon");
            if (stream is null) return CreateTrayIcon();
            using var icon = new Icon(stream);
            return (Icon)icon.Clone();
        }

        private static void OpenExternalUrl(string url)
        {
            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show($"ブラウザを開けませんでした。\n{ex.Message}",
                    "Explorer Docks", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private async System.Threading.Tasks.Task CheckForUpdatesAsync(bool manual)
        {
            if (_checkingUpdates) return;
            if (!UpdateService.IsConfigured)
            {
                if (manual) MessageBox.Show("GitHubでの公開前です。リポジトリ設定後に更新機能が有効になります。",
                    "Explorer Docks", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            _checkingUpdates = true;
            try
            {
                var release = await UpdateService.FindNewReleaseAsync();
                if (release is null)
                {
                    if (manual) MessageBox.Show("現在のバージョンが最新です。",
                        "Explorer Docks", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                if (MessageBox.Show($"Explorer Docks v{release.Version} が公開されています。\n更新しますか？",
                    "Explorer Docks", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                    return;

                var result = await UpdateService.PrepareAsync(release);
                if (result is null)
                {
                    MessageBox.Show("この実行環境からは自動適用できません。配布用ZIPまたはインストーラーから起動してください。",
                        "Explorer Docks", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }

                if (!UpdateService.LaunchApply(result))
                    throw new IOException("更新プログラムを起動できませんでした。");
                Close();
            }
            catch (Exception ex)
            {
                if (manual) MessageBox.Show($"更新の確認または準備に失敗しました。\n{ex.Message}",
                    "Explorer Docks", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            finally { _checkingUpdates = false; }
        }

        private async System.Threading.Tasks.Task RestartExplorerForClassicMenuAsync()
        {
            if (_restartingExplorer) return;
            _restartingExplorer = true;
            try
            {
                bool hadDocks = _dock.IsDocked;
                _dock.PrepareForExplorerRestart();
                // Explorerの再起動でファイル操作が中断される場合がある。
                // 通常は確認し、省略設定がONの場合だけ確認を飛ばす。
                foreach (var process in Process.GetProcessesByName("explorer"))
                {
                    using (process)
                    {
                        if (!process.HasExited)
                        {
                            process.Kill();
                            process.WaitForExit(3000);
                        }
                    }
                }
                // Windows 11ではExplorerのシェルが自動復帰する。
                // 復帰済みなのにexplorer.exeを起動すると「PC」のフォルダーが余計に開く。
                bool shellReady = await WaitForExplorerShellAsync();
                if (!shellReady)
                {
                    // タスクバーが戻らない場合にだけシェルを起動する。
                    Process.Start(new ProcessStartInfo(
                        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"))
                    { UseShellExecute = false });
                    shellReady = await WaitForExplorerShellAsync();
                }
                if (!shellReady)
                    throw new InvalidOperationException("Windows Explorerのタスクバーが復帰しませんでした。");
                if (IsDisposed) return;
                // タスクバー再生成後はトレイアイコンも登録し直す。
                _notifyIcon.Visible = false;
                _notifyIcon.Visible = true;
                if (hadDocks) await _dock.RestoreSavedAsync();
                OnStatusChanged("Windows Explorerを再起動しました。");
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    "Windows Explorerの再起動を完了できませんでした。\n" +
                    "Windowsに再サインインすると設定が反映されます。\n" + ex.Message,
                    "Explorer Docks", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            finally { _restartingExplorer = false; }
        }

        private static async System.Threading.Tasks.Task<bool> WaitForExplorerShellAsync()
        {
            for (int attempt = 0; attempt < 30; attempt++)
            {
                if (Win32.FindWindow("Shell_TrayWnd", null) != IntPtr.Zero) return true;
                await System.Threading.Tasks.Task.Delay(200);
            }
            return false;
        }

        private bool ConfirmExplorerRestart(bool enable)
        {
            if (_skipExplorerRestartConfirmation) return true;

            using var form = new Form
            {
                Text = "Windows Explorerの再起動",
                TopMost = true,
            };
            ManagementUi.ConfigureForm(form);
            var skip = new CheckBox { AutoSize = true, Text = "次回からこの確認を表示しない" };
            ManagementUi.AddDialogContent(form,
                (enable ? "従来の右クリックメニューを有効にします。" : "従来の右クリックメニューを無効にします。") +
                    "\n\n反映のためWindows Explorerを再起動します。" +
                    "\n開いているフォルダーが一度閉じられます。" +
                    "\nファイルのコピー・移動中はキャンセルしてください。",
                skip, ManagementUi.Button("変更して再起動", DialogResult.OK));
            if (form.ShowDialog() != DialogResult.OK) return false;
            if (skip.Checked)
            {
                _skipExplorerRestartConfirmation = true;
                if (!SaveSettings())
                {
                    _skipExplorerRestartConfirmation = false;
                    MessageBox.Show("確認を省略する設定を保存できませんでした。",
                        "Explorer Docks", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
            return true;
        }

        private static void LaunchExplorer()
        {
            try
            {
                Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show($"エクスプローラーを起動できませんでした。\n{ex.Message}",
                    "Explorer Docks", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        // NotifyIconの標準メニューを維持し、上部タスクバーの場合だけ初期座標を補正する。
        private static Rectangle GetTrayMenuBounds(Rectangle requested)
        {
            var cursor = Cursor.Position;
            var screen = Screen.FromPoint(cursor);
            var bounds = screen.Bounds;
            int? taskbarBottom = screen.WorkingArea.Top > bounds.Top + 2
                ? screen.WorkingArea.Top : null;

            // 上下のマルチモニターでも、カーソル側のタスクバーを確認する。
            Win32.EnumWindows((hWnd, _) =>
            {
                var name = Win32.GetClassNameOf(hWnd);
                if (name is not ("Shell_TrayWnd" or "Shell_SecondaryTrayWnd")
                    || !Win32.IsWindowVisible(hWnd)
                    || !Win32.GetWindowRect(hWnd, out var r)) return true;

                int width = r.Right - r.Left;
                int height = r.Bottom - r.Top;
                if (Math.Abs(r.Top - bounds.Top) <= 4
                    && width >= bounds.Width / 2
                    && height > 0 && height <= bounds.Height / 4
                    && r.Left < bounds.Right && r.Right > bounds.Left)
                {
                    taskbarBottom = Math.Max(taskbarBottom ?? int.MinValue, r.Bottom);
                }
                return true;
            }, IntPtr.Zero);

            if (taskbarBottom is null) return requested;

            int menuWidth = Math.Max(1, requested.Width);
            int menuHeight = Math.Max(1, requested.Height);
            int x = Math.Clamp(requested.X, bounds.Left,
                Math.Max(bounds.Left, bounds.Right - menuWidth));
            int y = Math.Clamp(Math.Max(taskbarBottom.Value + 2, cursor.Y + 2),
                bounds.Top, Math.Max(bounds.Top, bounds.Bottom - menuHeight));
            return new Rectangle(x, y, requested.Width, requested.Height);
        }

        private void BuildTrayIcon()
        {
            ManagementUi.ConfigureMenu(_trayMenu);
            _trayMenu.Items.Add(new ToolStripMenuItem("エクスプローラーを起動", null,
                (_, _) => LaunchExplorer()));
            var dockSettings = new ToolStripMenuItem("Dockの設定");
            var windowsSettings = new ToolStripMenuItem("Windowsとの連携");
            var helpMenu = new ToolStripMenuItem("ヘルプ");

            var animationsItem = new ToolStripMenuItem("アニメーション")
            {
                CheckOnClick = true,
                Checked = _dock.AnimationsEnabled,
            };
            animationsItem.CheckedChanged += (_, _) =>
            {
                _dock.AnimationsEnabled = animationsItem.Checked;
                if (_buttons is not null) _buttons.AnimationsEnabled = animationsItem.Checked;
                SaveSettings();
            };
            dockSettings.DropDownItems.Add(animationsItem);

            var collapseMenu = new ToolStripMenuItem("自動収納までの時間");
            int[] presets = { -1, 0, 100, 200, 280, 500, 1000, 2000, 3000 };
            foreach (var ms in presets)
            {
                var item = new ToolStripMenuItem(ms == 280 ? $"{FormatDelay(ms)}（標準）" : FormatDelay(ms)) { Tag = ms };
                item.Click += (_, _) => SetCollapseDelay(ms);
                collapseMenu.DropDownItems.Add(item);
            }
            collapseMenu.DropDownItems.Add(new ToolStripSeparator());
            var customItem = new ToolStripMenuItem("時間を指定...");
            customItem.Click += (_, _) => PromptCollapseDelay();
            collapseMenu.DropDownItems.Add(customItem);
            collapseMenu.DropDownOpening += (_, _) =>
            {
                foreach (ToolStripItem item in collapseMenu.DropDownItems)
                {
                    if (item is ToolStripMenuItem menuItem && menuItem.Tag is int value)
                    {
                        menuItem.Checked = value == _dock.CollapseDelayMs;
                    }
                }
            };
            dockSettings.DropDownItems.Add(collapseMenu);

            var labelsItem = new ToolStripMenuItem("フォルダー名を表示")
            {
                CheckOnClick = true,
                Checked = _dock.ShowLabels,
            };
            labelsItem.CheckedChanged += (_, _) =>
            {
                _dock.ShowLabels = labelsItem.Checked;
                SaveSettings();
            };
            dockSettings.DropDownItems.Add(labelsItem);

            var placementMenu = new ToolStripMenuItem("収納位置");
            var alignedItem = new ToolStripMenuItem("整列（同じ画面端に並べる）");
            var independentItem = new ToolStripMenuItem("個別配置（位置を自由に設定）");
            alignedItem.Click += (_, _) =>
            {
                _dock.PlacementMode = DockPlacementMode.Aligned;
                SaveSettings();
                OnStatusChanged("Dockの収納位置: 整列");
            };
            independentItem.Click += (_, _) =>
            {
                _dock.PlacementMode = DockPlacementMode.Independent;
                SaveSettings();
                OnStatusChanged("Dockの収納位置: 個別配置");
            };
            placementMenu.DropDownItems.AddRange(new ToolStripItem[] { alignedItem, independentItem });
            placementMenu.DropDownOpening += (_, _) =>
            {
                alignedItem.Checked = _dock.PlacementMode == DockPlacementMode.Aligned;
                independentItem.Checked = _dock.PlacementMode == DockPlacementMode.Independent;
            };
            dockSettings.DropDownItems.Add(placementMenu);

            var workMenu = new ToolStripMenuItem("Work");
            workMenu.DropDownOpening += (_, _) =>
            {
                while (workMenu.DropDownItems.Count > 0) workMenu.DropDownItems[0].Dispose();
                workMenu.DropDownItems.Add(new ToolStripMenuItem("新規作成...", null,
                    (_, _) => PromptCreateWork()));
                workMenu.DropDownItems.Add(new ToolStripMenuItem("Workの管理...", null,
                    (_, _) => ShowWorkManager()));

                string[] works = _dock.WorkNames;
                if (works.Length == 0) return;
                workMenu.DropDownItems.Add(new ToolStripSeparator());
                foreach (string work in works)
                {
                    string name = work;
                    workMenu.DropDownItems.Add(new ToolStripMenuItem(name.Replace("&", "&&"), null,
                        async (_, _) =>
                        {
                            try { await _dock.SelectWorkAsync(name); }
                            catch (Exception ex) { MessageBox.Show(ex.Message, "Explorer Docks"); }
                        }));
                }
            };
            _trayMenu.Items.Add(workMenu);
            var undockItem = new ToolStripMenuItem("すべてのDockを解除", null, (_, _) => UndockAll());
            _trayMenu.Items.Add(undockItem);
            _trayMenu.Opening += (_, _) => undockItem.Enabled = _dock.IsDocked;
            _trayMenu.Items.Add(new ToolStripSeparator());
            _trayMenu.Items.Add(dockSettings);
            _trayMenu.Items.Add(windowsSettings);
            _trayMenu.Items.Add(helpMenu);
            _trayMenu.Items.Add(new ToolStripSeparator());
            _trayMenu.Items.Add(new ToolStripMenuItem("終了", null, (_, _) => Close()));

            var restoreItem = new ToolStripMenuItem("起動時にDockを復元")
            {
                CheckOnClick = true,
                Checked = _restoreDocksOnStart,
            };
            restoreItem.CheckedChanged += (_, _) =>
            {
                _restoreDocksOnStart = restoreItem.Checked;
                SaveSettings();
                if (_restoreDocksOnStart) _dock.SaveCurrentSessions();
            };
            dockSettings.DropDownItems.Add(new ToolStripSeparator());
            dockSettings.DropDownItems.Add(restoreItem);

            var shellMenuItem = new ToolStripMenuItem("右クリックにWorkを追加")
            {
                CheckOnClick = true,
                Checked = ShellIntegration.IsShellMenuEnabled,
                Enabled = StartupRegistration.IsSupported,
            };
            shellMenuItem.Click += (_, _) =>
            {
                try { ShellIntegration.SetShellMenuEnabled(shellMenuItem.Checked); }
                catch (Exception ex)
                {
                    shellMenuItem.Checked = ShellIntegration.IsShellMenuEnabled;
                    MessageBox.Show(ex.Message, "Explorer Docks", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            };
            windowsSettings.DropDownItems.Add(shellMenuItem);

            var cropMenuItem = new ToolStripMenuItem("画像の右クリックに「画像をトリミング」")
            {
                CheckOnClick = true,
                Checked = ShellIntegration.IsImageCropMenuEnabled,
            };
            cropMenuItem.Click += (_, _) =>
            {
                bool previous = _imageCropMenu;
                try
                {
                    ShellIntegration.SetImageCropMenuEnabled(cropMenuItem.Checked);
                    _imageCropMenu = cropMenuItem.Checked;
                    if (!SaveSettings())
                    {
                        _imageCropMenu = previous;
                        ShellIntegration.SetImageCropMenuEnabled(previous);
                        throw new IOException("設定ファイルを保存できませんでした。");
                    }
                }
                catch (Exception ex)
                {
                    cropMenuItem.Checked = ShellIntegration.IsImageCropMenuEnabled;
                    MessageBox.Show(ex.Message, "Explorer Docks", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            };
            windowsSettings.DropDownItems.Add(cropMenuItem);

            var classicItem = new ToolStripMenuItem("従来の右クリックメニューを優先")
            {
                CheckOnClick = true,
                Checked = ShellIntegration.IsClassicMenuEnabled,
            };
            classicItem.Click += (_, _) =>
            {
                if (_restartingExplorer || !ConfirmExplorerRestart(classicItem.Checked))
                {
                    classicItem.Checked = ShellIntegration.IsClassicMenuEnabled;
                    return;
                }
                try
                {
                    ShellIntegration.SetClassicMenuEnabled(classicItem.Checked);
                    _ = RestartExplorerForClassicMenuAsync();
                }
                catch (Exception ex)
                {
                    classicItem.Checked = ShellIntegration.IsClassicMenuEnabled;
                    MessageBox.Show(ex.Message, "Explorer Docks", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            };
            windowsSettings.DropDownItems.Add(classicItem);

            var skipRestartItem = new ToolStripMenuItem("Explorer再起動前の確認を表示しない")
            {
                CheckOnClick = true,
                Checked = _skipExplorerRestartConfirmation,
            };
            skipRestartItem.Click += (_, _) =>
            {
                bool previous = _skipExplorerRestartConfirmation;
                _skipExplorerRestartConfirmation = skipRestartItem.Checked;
                if (!SaveSettings())
                {
                    _skipExplorerRestartConfirmation = previous;
                    skipRestartItem.Checked = previous;
                    MessageBox.Show("設定を保存できませんでした。",
                        "Explorer Docks", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            };
            _trayMenu.Opening += (_, _) =>
                skipRestartItem.Checked = _skipExplorerRestartConfirmation;
            windowsSettings.DropDownItems.Add(skipRestartItem);
            windowsSettings.DropDownItems.Add(new ToolStripSeparator());

            var startupItem = new ToolStripMenuItem("サインイン時に自動起動")
            {
                CheckOnClick = true,
                Enabled = StartupRegistration.IsSupported,
                Checked = StartupRegistration.IsSupported && StartupRegistration.IsEnabled,
                ToolTipText = StartupRegistration.IsSupported ? "" : "配布版で利用できます",
            };
            startupItem.Click += (_, _) =>
            {
                try
                {
                    bool previous = _startAtSignIn;
                    StartupRegistration.SetEnabled(startupItem.Checked);
                    _startAtSignIn = startupItem.Checked;
                    if (!SaveSettings())
                    {
                        _startAtSignIn = previous;
                        StartupRegistration.SetEnabled(previous);
                        throw new IOException("設定ファイルを保存できませんでした。");
                    }
                }
                catch (Exception ex)
                {
                    startupItem.Checked = StartupRegistration.IsEnabled;
                    MessageBox.Show($"自動起動の設定に失敗しました。\n{ex.Message}",
                        "Explorer Docks", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            };
            windowsSettings.DropDownItems.Add(startupItem);
            helpMenu.DropDownItems.Add(new ToolStripMenuItem($"バージョン v{UpdateService.CurrentVersion.ToString(3)}") { Enabled = false });
            helpMenu.DropDownItems.Add(new ToolStripMenuItem("更新を確認", null,
                async (_, _) => await CheckForUpdatesAsync(true)));
            helpMenu.DropDownItems.Add(new ToolStripSeparator());
            helpMenu.DropDownItems.Add(new ToolStripMenuItem("お問い合わせ", null,
                (_, _) => OpenExternalUrl("https://mayoel.pages.dev/contact/")));
            helpMenu.DropDownItems.Add(new ToolStripMenuItem("開発支援", null,
                (_, _) => OpenExternalUrl("https://ko-fi.com/mayomayoi")));
            _trayIcon = LoadTrayIcon();
            _notifyIcon.Icon = _trayIcon;
            _notifyIcon.Text = "Explorer Docks";
            // WinFormsの通常のメニュー操作を保ち、表示位置だけを事前に決定する。
            _trayMenu.AutoClose = true;
            _trayMenu.AdjustInitialBounds = GetTrayMenuBounds;
            _notifyIcon.ContextMenuStrip = _trayMenu;
            _notifyIcon.Visible = true;
        }

    }

    internal sealed class TrayContextMenuStrip : ContextMenuStrip
    {
        public Func<Rectangle, Rectangle>? AdjustInitialBounds { get; set; }

        protected override void SetBoundsCore(int x, int y, int width, int height,
            BoundsSpecified specified)
        {
            // WinFormsは表示開始時にLocationを再設定する場合があるため、
            // メニューの座標変更はすべて画面内の正しい方向に補正する。
            if ((specified & BoundsSpecified.Location) != 0
                && AdjustInitialBounds is not null)
            {
                var bounds = AdjustInitialBounds(new Rectangle(x, y, width, height));
                x = bounds.X;
                y = bounds.Y;
            }
            base.SetBoundsCore(x, y, width, height, specified);
        }
    }
}
