using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace ExplorerDocks
{
    internal static class ManagementUi
    {
        internal static readonly Font Font = new("Yu Gothic UI", 9f);
        private static readonly Font HeaderFont = new("Yu Gothic UI", 9f, FontStyle.Bold);
        // SystemFonts.MenuFontは呼ぶたびに新しいFontを作るため、1つを全メニューで共有する
        private static readonly Font MenuFont = SystemFonts.MenuFont ?? Control.DefaultFont;

        internal static void ConfigureForm(Form form, bool resizable = false)
        {
            form.Font = Font;
            form.AutoScaleDimensions = new SizeF(96, 96);
            form.AutoScaleMode = AutoScaleMode.Dpi;
            form.BackColor = SystemColors.Window;
            form.ForeColor = SystemColors.WindowText;
            form.Padding = new Padding(12);
            form.FormBorderStyle = resizable ? FormBorderStyle.Sizable : FormBorderStyle.FixedDialog;
            form.MinimizeBox = false;
            form.MaximizeBox = resizable;
            form.ShowInTaskbar = false;
            form.StartPosition = FormStartPosition.CenterParent;
            if (!resizable)
            {
                form.AutoSize = true;
                form.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            }
            // 親は非表示の管理ウィンドウなので、CenterParentのままだと画面左上に出る。
            // 親がない場合はカーソルのあるモニターの中央に置く。
            form.Load += (_, _) =>
            {
                if (form.Owner is not null && form.Owner.Visible) return;
                var area = Screen.FromPoint(Cursor.Position).WorkingArea;
                form.Location = new Point(
                    area.Left + Math.Max(0, (area.Width - form.Width) / 2),
                    area.Top + Math.Max(0, (area.Height - form.Height) / 2));
            };
        }

        internal static Label Label(string text, bool secondary = false) => new()
        {
            Text = text,
            AutoSize = true,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 0, 0, 8),
            ForeColor = secondary ? SystemColors.GrayText : SystemColors.WindowText,
        };

        internal static Label Header(string text)
        {
            var label = Label(text);
            label.Font = HeaderFont;
            return label;
        }

        internal static Button Button(string text, DialogResult result = DialogResult.None) => new()
        {
            Text = text,
            DialogResult = result,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            MinimumSize = new Size(76, 28),
            Padding = new Padding(8, 3, 8, 3),
            Margin = new Padding(0, 0, 6, 0),
            UseVisualStyleBackColor = true,
        };

        internal static TableLayoutPanel Column() => new()
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            Margin = Padding.Empty,
            ColumnStyles = { new ColumnStyle(SizeType.Percent, 100) },
        };

        internal static FlowLayoutPanel Actions(params Button[] buttons)
        {
            var panel = new FlowLayoutPanel
            {
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Dock = DockStyle.Fill,
                WrapContents = false,
                Margin = new Padding(0, 8, 0, 0),
            };
            panel.Controls.AddRange(buttons);
            return panel;
        }

        internal static void AddDialogContent(Form form, string caption, Control input,
            Button accept, string? description = null)
        {
            var layout = Column();
            layout.MinimumSize = new Size(340, 0);
            layout.Controls.Add(Label(caption));
            input.Dock = DockStyle.Top;
            input.Margin = new Padding(0, 0, 0, 8);
            input.AccessibleName = caption;
            layout.Controls.Add(input);
            if (description is not null) layout.Controls.Add(Label(description, true));
            var cancel = Button("キャンセル", DialogResult.Cancel);
            var actions = Actions(cancel, accept);
            actions.FlowDirection = FlowDirection.RightToLeft;
            layout.Controls.Add(actions);
            form.Controls.Add(layout);
            form.AcceptButton = accept;
            form.CancelButton = cancel;
        }

        internal static string? AskName(IWin32Window? owner, string title, string caption,
            string initial, string acceptText)
        {
            using var form = new Form { Text = title, TopMost = owner is Form parent ? parent.TopMost : true };
            ConfigureForm(form);
            var name = new TextBox { Text = initial, MaxLength = 64 };
            AddDialogContent(form, caption, name, Button(acceptText, DialogResult.OK));
            form.Shown += (_, _) => { form.BringToFront(); form.Activate(); name.Focus(); name.SelectAll(); };
            return form.ShowDialog(owner) == DialogResult.OK ? name.Text : null;
        }

        internal static void ConfigureMenu(ToolStripDropDown menu)
        {
            menu.Font = MenuFont;
            menu.Renderer = new CenteredTextRenderer();
            menu.AutoSize = true;
            menu.ItemAdded += (_, e) => ConfigureMenuItem(e.Item!);
            foreach (ToolStripItem item in menu.Items) ConfigureMenuItem(item);
        }

        private static void ConfigureMenuItem(ToolStripItem item)
        {
            if (item is not ToolStripMenuItem entry) return;
            // 標準のTextRendererに任せ、チェック・矢印・文字の縦位置を揃える。
            // 上下に少し余裕を持たせ、Windows 11のメニューに近い行の高さにする。
            entry.Padding = new Padding(2, 4, 2, 4);
            entry.DropDownOpening += (_, _) => KeepSubmenuOnSameScreen(entry);
            ConfigureMenu(entry.DropDown);
        }

        /// <summary>
        /// サブメニューを親メニューと同じモニター内に開く。
        /// 標準では隣のモニターに入る余地があるとそちらへ出てしまう。
        /// </summary>
        private static void KeepSubmenuOnSameScreen(ToolStripMenuItem entry)
        {
            if (entry.Owner is not ToolStripDropDown parent || !parent.Visible) return;
            var area = Screen.FromRectangle(parent.Bounds).WorkingArea;
            var size = entry.DropDown.GetPreferredSize(Size.Empty);
            var itemTop = parent.PointToScreen(entry.Bounds.Location).Y;
            bool right = parent.Bounds.Right + size.Width <= area.Right
                         || parent.Bounds.Left - size.Width < area.Left;
            bool down = itemTop + size.Height <= area.Bottom;
            entry.DropDownDirection = (right, down) switch
            {
                (true, true) => ToolStripDropDownDirection.Right,
                (false, true) => ToolStripDropDownDirection.Left,
                (true, false) => ToolStripDropDownDirection.AboveRight,
                _ => ToolStripDropDownDirection.AboveLeft,
            };
        }

        /// <summary>
        /// 和文フォントは上下の余白が非対称で、標準の縦中央揃えだと文字が上に寄って見える。
        /// 実際に描いた文字の画素の上端・下端を測り、その中心が項目の中央に来るようずらす。
        /// </summary>
        private sealed class CenteredTextRenderer : ToolStripSystemRenderer
        {
            private readonly Dictionary<(string Text, Font Font, int Height), int> _offsets = new();

            protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
            {
                var font = e.TextFont ?? e.Item?.Font;
                if (font is not null && e.Item is not null && !string.IsNullOrEmpty(e.Text)
                    && e.TextRectangle.Width > 0 && e.TextRectangle.Height > 0)
                {
                    var r = e.TextRectangle;
                    var key = (e.Text, font, r.Height);
                    if (!_offsets.TryGetValue(key, out int offset))
                    {
                        offset = MeasureOffset(e.Text, font, r, e.TextFormat, e.Item.Height);
                        _offsets[key] = offset;
                    }
                    if (offset != 0)
                    {
                        r.Offset(0, offset);
                        e.TextRectangle = r;
                    }
                }
                base.OnRenderItemText(e);
            }

            private static int MeasureOffset(string text, Font font, Rectangle rect,
                TextFormatFlags flags, int itemHeight)
            {
                using var bitmap = new Bitmap(Math.Max(1, rect.Width), Math.Max(1, rect.Height));
                using (var g = Graphics.FromImage(bitmap))
                {
                    g.Clear(Color.White);
                    TextRenderer.DrawText(g, text, font, new Rectangle(Point.Empty, rect.Size), Color.Black, flags);
                }
                int top = -1, bottom = -1;
                for (int y = 0; y < bitmap.Height; y++)
                {
                    for (int x = 0; x < bitmap.Width; x++)
                    {
                        if (bitmap.GetPixel(x, y).R >= 160) continue;
                        if (top < 0) top = y;
                        bottom = y;
                        break;
                    }
                }
                if (top < 0) return 0;
                // 文字の中心(項目内の座標)を、項目の高さの中央へ合わせる
                float inkCenter = rect.Top + (top + bottom + 1) / 2f;
                int offset = (int)Math.Round(itemHeight / 2f - inkCenter);
                int limit = Math.Max(1, rect.Height / 4);
                return Math.Clamp(offset, -limit, limit);
            }
        }
    }
}
