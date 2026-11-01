using System;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;

namespace ExplorerDocks
{
    // Workの作成、名前変更、削除と登録済みExplorerの確認。
    internal sealed class WorkManagerForm : Form
    {
        private readonly DockCoordinator _dock;
        private readonly ListBox _works = new();
        private readonly ListBox _members = new();
        private readonly Button _rename = ManagementUi.Button("名前を変更");
        private readonly Button _delete = ManagementUi.Button("削除");
        private readonly Button _addExplorer = ManagementUi.Button("フォルダーを追加...");
        private readonly Button _undock = ManagementUi.Button("Dock解除");
        private readonly Label _memberTitle = ManagementUi.Header("登録中のフォルダー");
        private readonly Label _empty = ManagementUi.Label("");

        public WorkManagerForm(DockCoordinator dock, string? initialWork = null)
        {
            _dock = dock;
            Text = "Workの管理";
            ManagementUi.ConfigureForm(this, resizable: true);
            ClientSize = new Size(680, 410);
            MinimumSize = new Size(600, 355);
            StartPosition = FormStartPosition.CenterScreen;
            TopMost = true;

            var layout = ManagementUi.Column();
            layout.RowCount = 1;
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

            var columns = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 4,
                Margin = Padding.Empty,
            };
            columns.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 34));
            columns.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 66));
            columns.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            columns.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            columns.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            columns.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            var workHeader = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoSize = true,
                ColumnCount = 2,
                Margin = new Padding(0, 0, 12, 8),
            };
            workHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            workHeader.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            var workTitle = ManagementUi.Header("Work");
            workTitle.TextAlign = ContentAlignment.MiddleLeft;
            workTitle.Margin = Padding.Empty;
            var create = ManagementUi.Button("新規作成...");
            create.Margin = Padding.Empty;
            workHeader.Controls.Add(workTitle, 0, 0);
            workHeader.Controls.Add(create, 1, 0);
            columns.Controls.Add(workHeader, 0, 0);
            _memberTitle.TextAlign = ContentAlignment.MiddleLeft;
            columns.Controls.Add(_memberTitle, 1, 0);
            _works.Dock = DockStyle.Fill;
            _works.Margin = new Padding(0, 0, 12, 0);
            _works.IntegralHeight = false;
            _works.BorderStyle = BorderStyle.FixedSingle;
            _works.HorizontalScrollbar = true;
            _works.AccessibleName = "Workの一覧";
            _works.SelectedIndexChanged += (_, _) => RefreshMembers();
            columns.Controls.Add(_works, 0, 1);
            var workActions = ManagementUi.Actions(_rename, _delete);
            workActions.Margin = new Padding(0, 8, 12, 0);
            columns.Controls.Add(workActions, 0, 3);
            _members.Dock = DockStyle.Fill;
            _members.Margin = Padding.Empty;
            _members.IntegralHeight = false;
            _members.BorderStyle = BorderStyle.FixedSingle;
            _members.HorizontalScrollbar = true;
            _members.AccessibleName = "Workに登録中のフォルダー";
            _members.SelectedIndexChanged += (_, _) => UpdateButtons();
            columns.Controls.Add(_members, 1, 1);
            _empty.ForeColor = SystemColors.GrayText;
            _empty.Margin = new Padding(0, 6, 0, 0);
            columns.Controls.Add(_empty, 1, 2);
            columns.Controls.Add(ManagementUi.Actions(_addExplorer, _undock), 1, 3);
            layout.Controls.Add(columns, 0, 0);
            create.Click += (_, _) => CreateWork();
            _rename.Click += (_, _) => RenameWork();
            _delete.Click += (_, _) => DeleteWork();
            _addExplorer.Click += (_, _) => AddExplorer();
            _undock.Click += (_, _) => UndockExplorer();
            Controls.Add(layout);
            LoadWorks(initialWork);
        }

        private string? SelectedWork => _works.SelectedItem as string;

        private void LoadWorks(string? selected = null)
        {
            selected ??= SelectedWork;
            _works.BeginUpdate();
            _works.Items.Clear();
            _works.Items.AddRange(_dock.WorkNames);
            _works.EndUpdate();
            if (selected is not null)
            {
                for (int i = 0; i < _works.Items.Count; i++)
                    if (string.Equals((string)_works.Items[i]!, selected, StringComparison.OrdinalIgnoreCase))
                    { _works.SelectedIndex = i; break; }
            }
            if (_works.SelectedIndex < 0 && _works.Items.Count > 0) _works.SelectedIndex = 0;
            RefreshMembers();
        }

        private void RefreshMembers()
        {
            _members.Items.Clear();
            if (SelectedWork is { } name)
                foreach (var (window, folder) in _dock.WorkMembers(name))
                    _members.Items.Add(new ExplorerItem(window, folder));
            _memberTitle.Text = $"登録中のフォルダー（{_members.Items.Count}）";
            _empty.Text = SelectedWork is null ? "新規作成からWorkを追加してください。"
                : _members.Items.Count == 0 ? "フォルダーは未登録です。「フォルダーを追加...」から登録できます。" : "";
            _empty.Visible = _members.Items.Count == 0;
            UpdateButtons();
        }

        private void UpdateButtons()
        {
            bool hasWork = SelectedWork is not null;
            _rename.Enabled = hasWork;
            _delete.Enabled = hasWork;
            _addExplorer.Enabled = hasWork;
            _undock.Enabled = hasWork && _members.SelectedItem is ExplorerItem;
        }

        private void CreateWork()
        {
            string? name = AskName("Workの新規作成", "Work名", "");
            if (name is null) return;
            try { _dock.CreateWork(name); LoadWorks(name.Trim()); }
            catch (Exception ex) { ShowError(ex); }
        }

        private void RenameWork()
        {
            if (SelectedWork is not { } oldName) return;
            string? name = AskName("Work名の変更", "新しいWork名", oldName);
            if (name is null) return;
            try { _dock.RenameWork(oldName, name); LoadWorks(name.Trim()); }
            catch (Exception ex) { ShowError(ex); }
        }

        private void DeleteWork()
        {
            if (SelectedWork is not { } name) return;
            if (MessageBox.Show(this,
                $"Work「{name}」を削除しますか？\n登録済みExplorerはDockとして残ります。",
                "Workの削除", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;
            try { _dock.DeleteWork(name); LoadWorks(); }
            catch (Exception ex) { ShowError(ex); }
        }

        private void AddExplorer()
        {
            if (SelectedWork is not { } name) return;
            var windows = DockService.FindExplorers()
                .Concat(_dock.DockedFolders.Select(d => d.Window)).Distinct()
                .Where(Win32.IsWindow).ToArray();
            if (windows.Length == 0)
            {
                MessageBox.Show(this, "開いているエクスプローラーがありません。", "Explorer Docks");
                return;
            }
            using var picker = new Form
            {
                Text = "Workに追加するフォルダーを選択",
                TopMost = TopMost,
            };
            ManagementUi.ConfigureForm(picker);
            var choices = new ListBox
            {
                MinimumSize = new Size(500, 260),
                IntegralHeight = false,
                HorizontalScrollbar = true,
            };
            foreach (var hwnd in windows)
                choices.Items.Add(new ExplorerItem(hwnd, Win32.GetTitleOf(hwnd)));
            choices.SelectedIndex = 0;
            ManagementUi.AddDialogContent(picker, "追加するフォルダーを選んでください。", choices,
                ManagementUi.Button("追加", DialogResult.OK));
            if (picker.ShowDialog(this) != DialogResult.OK || choices.SelectedItem is not ExplorerItem selected) return;
            try { _dock.AddToWork(selected.Window, name); RefreshMembers(); }
            catch (Exception ex) { ShowError(ex); }
        }

        private void UndockExplorer()
        {
            if (_members.SelectedItem is not ExplorerItem member) return;
            try { _dock.Undock(member.Window); RefreshMembers(); }
            catch (Exception ex) { ShowError(ex); }
        }

        private string? AskName(string title, string caption, string initial)
            => ManagementUi.AskName(this, title, caption, initial, "保存");

        private void ShowError(Exception ex) =>
            MessageBox.Show(this, ex.Message, "Explorer Docks", MessageBoxButtons.OK, MessageBoxIcon.Warning);

        private sealed record ExplorerItem(IntPtr Window, string Name)
        {
            public override string ToString() => string.IsNullOrWhiteSpace(Name) ? "フォルダー" : Name;
        }
    }
}
