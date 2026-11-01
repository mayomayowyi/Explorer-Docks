using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ExplorerDocks
{
    /// <summary>
    /// 画像のトリミング画面。回転・反転・角度の微調整をしたうえで範囲を選び、元画像は変えずにコピーを保存する。
    /// </summary>
    internal sealed class ImageCropForm : Form
    {
        private const int PreviewMaxSide = 1600;

        private static readonly (string Label, double? Ratio)[] Ratios =
        {
            ("自由", null), ("元の画像と同じ", 0), ("1:1（正方形）", 1.0),
            ("4:3", 4 / 3.0), ("3:4", 3 / 4.0), ("3:2", 3 / 2.0), ("2:3", 2 / 3.0),
            ("16:9", 16 / 9.0), ("9:16", 9 / 16.0),
        };

        private readonly string _path;
        private readonly CropCanvas _canvas = new() { Dock = DockStyle.Fill };
        private readonly TrackBar _angleSlider = new()
        {
            Minimum = -450, Maximum = 450, TickFrequency = 150, SmallChange = 1, LargeChange = 10,
            TickStyle = TickStyle.None, Dock = DockStyle.Fill, AutoSize = false, Height = 28,
        };
        private readonly NumericUpDown _angleValue = new()
        {
            Minimum = -45, Maximum = 45, DecimalPlaces = 1, Increment = 0.1M, Width = 72,
            TextAlign = HorizontalAlignment.Right,
        };
        private readonly ComboBox _ratio = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
        // よく使う比率はワンクリックで選べるようにする(Ratiosの添字)
        private static readonly (string Label, int Index)[] QuickRatios = { ("1:1", 2), ("4:3", 3), ("16:9", 7) };
        private readonly Button[] _quickRatioButtons = new Button[QuickRatios.Length];
        private readonly CheckBox _circle = new()
        {
            Text = "円形で確認（SNSアイコン）", AutoSize = true, Margin = new Padding(0, 4, 0, 0),
        };
        private readonly NumericUpDown _top = Trim(), _bottom = Trim(), _left = Trim(), _right = Trim();
        private readonly Label _outputSize = ManagementUi.Label("");
        private readonly Label _zoomLabel = new() { AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(6, 0, 0, 0) };
        private readonly Label _status = new()
        {
            AutoSize = false, Dock = DockStyle.Fill, AutoEllipsis = true,
            TextAlign = ContentAlignment.MiddleLeft, ForeColor = SystemColors.GrayText,
        };
        private readonly Button _save = ManagementUi.Button("コピーを保存");
        private readonly ToolTip _tips = new();
        private readonly Control[] _editors;

        private Bitmap? _source;   // 向きを補正した原寸画像
        private Bitmap? _preview;  // 表示用に縮小した画像(原寸が小さければ_sourceと同じ)
        private int _quarter;      // 90°回転の回数(時計回り)
        private bool _mirror;      // 左右反転
        private float _fine;       // 角度の微調整(度、時計回り)
        private bool _syncing;     // 入力欄を更新中は変更イベントを無視する

        public ImageCropForm(string path)
        {
            _path = path;
            Text = $"画像をトリミング - {Path.GetFileName(path)}";
            ManagementUi.ConfigureForm(this, resizable: true);
            ShowInTaskbar = true;
            MinimizeBox = true;
            KeyPreview = true;
            ClientSize = new Size(1120, 720);
            MinimumSize = new Size(780, 620);
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2, Margin = Padding.Empty };
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 272));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            var canvasFrame = new Panel { Dock = DockStyle.Fill, Margin = new Padding(0, 0, 12, 0), Padding = Padding.Empty };
            canvasFrame.Controls.Add(_canvas);
            root.Controls.Add(canvasFrame, 0, 0);
            root.Controls.Add(BuildSidePanel(), 1, 0);
            root.Controls.Add(BuildFooter(), 0, 1);
            root.SetColumnSpan(root.GetControlFromPosition(0, 1)!, 2);
            Controls.Add(root);

            _editors = new Control[] { _angleSlider, _angleValue, _ratio, _top, _bottom, _left, _right, _save };
            UpdateRatioControls();
            SetEditorsEnabled(false);
            _canvas.Message = "読み込み中...";
            _canvas.CropChanged += OnCanvasCropChanged;
            _canvas.ZoomChanged += () => _zoomLabel.Text = $"{_canvas.Zoom * 100:0}%";
        }

        public event Action<string>? Saved;

        private static NumericUpDown Trim() => new()
        {
            Minimum = 0, Maximum = 100000, Width = 84, ThousandsSeparator = true,
            TextAlign = HorizontalAlignment.Right, Anchor = AnchorStyles.Left | AnchorStyles.Right,
        };


        private Control BuildSidePanel()
        {
            var panel = ManagementUi.Column();
            panel.Dock = DockStyle.Fill;
            panel.AutoSize = false;

            panel.Controls.Add(Section("回転・反転"));
            var turns = Grid(2);
            turns.Controls.Add(ToolButton("左に90°回転", "Ctrl+L", () => Turn(-1)));
            turns.Controls.Add(ToolButton("右に90°回転", "Ctrl+R", () => Turn(1)));
            turns.Controls.Add(ToolButton("左右反転", null, () => Flip(horizontal: true)));
            turns.Controls.Add(ToolButton("上下反転", null, () => Flip(horizontal: false)));
            panel.Controls.Add(turns);

            panel.Controls.Add(Section("角度の微調整"));
            var angle = Grid(3);
            angle.ColumnStyles[0] = new ColumnStyle(SizeType.Percent, 100);
            angle.ColumnStyles[1] = new ColumnStyle(SizeType.AutoSize);
            angle.ColumnStyles[2] = new ColumnStyle(SizeType.AutoSize);
            angle.Controls.Add(_angleSlider);
            angle.Controls.Add(_angleValue);
            var zero = ToolButton("0°", null, () => SetFineAngle(0));
            zero.MinimumSize = new Size(40, 28);
            angle.Controls.Add(zero);
            _angleSlider.ValueChanged += (_, _) => { if (!_syncing) SetFineAngle(_angleSlider.Value / 10f); };
            _angleValue.ValueChanged += (_, _) => { if (!_syncing) SetFineAngle((float)_angleValue.Value); };
            _tips.SetToolTip(_angleSlider, "傾きを0.1°単位で直します。余白が出ないよう範囲も自動で合わせます。");
            panel.Controls.Add(angle);

            panel.Controls.Add(Section("縦横比"));
            var quick = Grid(QuickRatios.Length);
            for (int i = 0; i < QuickRatios.Length; i++)
            {
                var (label, index) = QuickRatios[i];
                var button = ToolButton(label, $"縦横比を{label}にします", () => _ratio.SelectedIndex = index);
                button.FlatStyle = FlatStyle.Flat;
                _quickRatioButtons[i] = button;
                quick.Controls.Add(button);
            }
            panel.Controls.Add(quick);
            foreach (var (label, _) in Ratios) _ratio.Items.Add(label);
            _ratio.SelectedIndex = 0;
            _ratio.SelectedIndexChanged += (_, _) => { ApplyRatio(); UpdateRatioControls(); };
            panel.Controls.Add(_ratio);
            _circle.CheckedChanged += (_, _) => _canvas.CircleGuide = _circle.Checked && _circle.Enabled;
            _tips.SetToolTip(_circle, "SNSのアイコンのように丸く切り抜かれたときの見え方を確認します。保存される画像は正方形のままです。");
            panel.Controls.Add(_circle);

            panel.Controls.Add(Section("切り取る幅（px）"));
            var trims = Grid(4);
            trims.ColumnStyles[0] = new ColumnStyle(SizeType.AutoSize);
            trims.ColumnStyles[1] = new ColumnStyle(SizeType.Percent, 50);
            trims.ColumnStyles[2] = new ColumnStyle(SizeType.AutoSize);
            trims.ColumnStyles[3] = new ColumnStyle(SizeType.Percent, 50);
            AddTrim(trims, "上", _top);
            AddTrim(trims, "下", _bottom);
            AddTrim(trims, "左", _left);
            AddTrim(trims, "右", _right);
            panel.Controls.Add(trims);
            _outputSize.Margin = new Padding(0, 6, 0, 0);
            panel.Controls.Add(_outputSize);

            panel.Controls.Add(Section("表示"));
            var zoom = new FlowLayoutPanel
            {
                AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false,
                Dock = DockStyle.Fill, Margin = Padding.Empty,
            };
            var zoomOut = ToolButton("−", "縮小（Ctrl+−）", () => _canvas.ZoomBy(1 / 1.25));
            var zoomIn = ToolButton("＋", "拡大（Ctrl+＋）", () => _canvas.ZoomBy(1.25));
            var fit = ToolButton("全体", "画面に合わせる（Ctrl+0）", () => _canvas.FitToView());
            var actual = ToolButton("100%", "実寸で表示（Ctrl+1）", () => _canvas.SetZoom(1));
            foreach (var b in new[] { zoomOut, zoomIn }) b.MinimumSize = new Size(36, 28);
            foreach (var b in new[] { fit, actual }) b.MinimumSize = new Size(52, 28);
            zoom.Controls.AddRange(new Control[] { zoomOut, zoomIn, fit, actual, _zoomLabel });
            panel.Controls.Add(zoom);
            var hint = ManagementUi.Label("ホイールで拡大縮小、右ドラッグで移動、枠の外をドラッグで範囲を作り直せます。", secondary: true);
            hint.Margin = new Padding(0, 6, 0, 0);
            hint.MaximumSize = new Size(260, 0);
            panel.Controls.Add(hint);
            return panel;
        }

        private Control BuildFooter()
        {
            var footer = new TableLayoutPanel
            {
                Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 3, Margin = new Padding(0, 12, 0, 0),
            };
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            footer.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            var reset = ManagementUi.Button("元に戻す");
            reset.Click += (_, _) => ResetAll();
            _tips.SetToolTip(reset, "回転・角度・範囲をすべて最初の状態に戻します。");
            _save.BackColor = DockColors.Accent;
            _save.ForeColor = Color.White;
            _save.FlatStyle = FlatStyle.Flat;
            _save.FlatAppearance.BorderSize = 0;
            _save.FlatAppearance.MouseOverBackColor = DockColors.AccentHover;
            _save.FlatAppearance.MouseDownBackColor = DockColors.AccentPressed;
            _save.Margin = Padding.Empty;
            _save.Click += (_, _) => SaveCopy();
            _tips.SetToolTip(_save, "元の画像はそのままに、同じフォルダーへ別名で保存して閉じます（Ctrl+S）。");
            footer.Controls.Add(reset, 0, 0);
            footer.Controls.Add(_status, 1, 0);
            footer.Controls.Add(_save, 2, 0);
            return footer;
        }

        private static Label Section(string text)
        {
            var label = ManagementUi.Header(text);
            label.Margin = new Padding(0, 10, 0, 6);
            return label;
        }

        private static TableLayoutPanel Grid(int columns)
        {
            var grid = new TableLayoutPanel
            {
                Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = columns, Margin = Padding.Empty,
            };
            for (int i = 0; i < columns; i++) grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f / columns));
            return grid;
        }

        private Button ToolButton(string text, string? tip, Action click)
        {
            var button = ManagementUi.Button(text);
            button.Dock = DockStyle.Fill;
            button.MinimumSize = new Size(0, 30);
            button.Margin = new Padding(0, 0, 6, 6);
            button.Click += (_, _) => click();
            if (tip is not null) _tips.SetToolTip(button, tip);
            return button;
        }

        private void AddTrim(TableLayoutPanel grid, string caption, NumericUpDown input)
        {
            var label = new Label { Text = caption, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 0, 6, 6) };
            input.Margin = new Padding(0, 0, 10, 6);
            input.AccessibleName = $"{caption}を切り取る幅";
            input.ValueChanged += (_, _) => OnTrimChanged();
            grid.Controls.Add(label);
            grid.Controls.Add(input);
        }

        private void SetEditorsEnabled(bool enabled)
        {
            foreach (var editor in _editors) editor.Enabled = enabled;
            UpdateRatioControls();
        }

        private void UpdateRatioControls()
        {
            bool ready = _source is not null;
            for (int i = 0; i < _quickRatioButtons.Length; i++)
            {
                var button = _quickRatioButtons[i];
                if (button is null) continue;
                bool selected = _ratio.SelectedIndex == QuickRatios[i].Index;
                button.Enabled = ready;
                button.BackColor = selected ? DockColors.Accent : SystemColors.Control;
                button.ForeColor = selected ? Color.White : SystemColors.ControlText;
                button.FlatAppearance.BorderColor = selected ? DockColors.Accent : SystemColors.ControlDark;
            }
            bool square = ready && _ratio.SelectedIndex == 2;
            _circle.Enabled = square;
            _canvas.CircleGuide = square && _circle.Checked;
        }


        protected override async void OnShown(EventArgs e)
        {
            base.OnShown(e);
            try
            {
                var source = await Task.Run(() => ImageCodec.Load(_path));
                if (IsDisposed) { source.Dispose(); return; }
                _source = source;
                _preview = CreatePreview(source);
                SetEditorsEnabled(true);
                _canvas.Message = null;
                RebuildWorkImage(resetCrop: true);
                _canvas.FitToView();
                _status.Text = $"元のサイズ: {source.Width:N0} × {source.Height:N0} px";
                _canvas.Focus();
            }
            catch (Exception ex)
            {
                if (IsDisposed) return;
                _canvas.Message = $"画像を読み込めませんでした。\n{ex.Message}";
            }
        }

        private static Bitmap CreatePreview(Bitmap source)
        {
            int longSide = Math.Max(source.Width, source.Height);
            if (longSide <= PreviewMaxSide) return source;
            double k = (double)PreviewMaxSide / longSide;
            var preview = new Bitmap(Math.Max(1, (int)Math.Round(source.Width * k)),
                Math.Max(1, (int)Math.Round(source.Height * k)), PixelFormat.Format32bppPArgb);
            using var g = Graphics.FromImage(preview);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.DrawImage(source, 0, 0, preview.Width, preview.Height);
            return preview;
        }


        // 画像への操作は「反転 → 90°回転 → 微調整の回転」の順に重ねる。
        // ボタンは常に見たままの向きで働くよう、反転中は回転の向きを逆にして保持する。
        private void Turn(int direction)
        {
            if (_source is null) return;
            _quarter = ((_quarter + (_mirror ? -direction : direction)) % 4 + 4) % 4;
            RebuildWorkImage(resetCrop: true);
        }

        private void Flip(bool horizontal)
        {
            if (_source is null) return;
            _mirror = !_mirror;
            if (!horizontal) _quarter = (_quarter + 2) % 4;
            _fine = -_fine;
            SyncAngleInputs();
            RebuildWorkImage(resetCrop: true);
        }

        private void SetFineAngle(float degrees)
        {
            if (_source is null) return;
            degrees = (float)Math.Round(Math.Clamp(degrees, -45f, 45f), 1);
            if (degrees == _fine) { SyncAngleInputs(); return; }
            _fine = degrees;
            SyncAngleInputs();
            // 画像は作り直さず、表示の傾きと範囲だけ更新する(スライダー操作を軽くする)
            ApplyFineAngle();
        }

        private void SyncAngleInputs()
        {
            _syncing = true;
            _angleSlider.Value = (int)Math.Round(_fine * 10);
            _angleValue.Value = (decimal)_fine;
            _syncing = false;
        }

        private void ResetAll()
        {
            if (_source is null) return;
            _quarter = 0;
            _mirror = false;
            _fine = 0;
            SyncAngleInputs();
            _ratio.SelectedIndex = 0;
            RebuildWorkImage(resetCrop: true);
            _canvas.FitToView();
        }

        /// <summary>原寸画像から回転・反転後の画像(作業画像)への変換と、その外接サイズ。</summary>
        private Matrix BuildTransform(Size source, out Size bounds, bool includeFine = true)
        {
            var turned = _quarter % 2 == 0 ? source : new Size(source.Height, source.Width);
            float fine = includeFine ? _fine : 0;
            double rad = fine * Math.PI / 180;
            double cos = Math.Abs(Math.Cos(rad)), sin = Math.Abs(Math.Sin(rad));
            bounds = fine == 0 ? turned : new Size(
                (int)Math.Ceiling(turned.Width * cos + turned.Height * sin - 0.01),
                (int)Math.Ceiling(turned.Width * sin + turned.Height * cos - 0.01));

            // GDI+の行列は後から追加したものが先に点へ適用される
            var m = new Matrix();
            m.Translate(bounds.Width / 2f, bounds.Height / 2f);
            m.Rotate(fine);
            m.Rotate(_quarter * 90f);
            if (_mirror) m.Scale(-1, 1);
            m.Translate(-source.Width / 2f, -source.Height / 2f);
            return m;
        }

        /// <summary>90°回転・反転を反映した表示用画像を作る。角度の微調整は表示時に傾けて描く。</summary>
        private void RebuildWorkImage(bool resetCrop)
        {
            if (_source is null || _preview is null) return;
            using var transform = BuildTransform(_source.Size, out var bounds, includeFine: false);
            float k = (float)_preview.Width / _source.Width;
            var image = new Bitmap(Math.Max(1, (int)Math.Ceiling(bounds.Width * k)),
                Math.Max(1, (int)Math.Ceiling(bounds.Height * k)), PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(image))
            using (var attributes = new ImageAttributes())
            {
                attributes.SetWrapMode(WrapMode.TileFlipXY); // 端の画素が半透明ににじむのを防ぐ
                g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                g.ScaleTransform(k, k);
                g.MultiplyTransform(transform);
                g.DrawImage(_preview, new Rectangle(0, 0, _source.Width, _source.Height),
                    0, 0, _preview.Width, _preview.Height, GraphicsUnit.Pixel, attributes);
            }
            _canvas.SetImage(image, k, bounds);
            ApplyFineAngle(resetCrop);
        }

        private void ApplyFineAngle(bool resetCrop = true)
        {
            if (_source is null) return;
            using var _ = BuildTransform(_source.Size, out var bounds);
            var crop = resetCrop ? DefaultCrop(bounds) : _canvas.Crop;
            _canvas.SetRotation(_fine, bounds, crop);
            _canvas.AspectRatio = CurrentRatio();
            if (_canvas.AspectRatio is double ratio) _canvas.Crop = FitRatio(_canvas.Crop, ratio);
            UpdateInputs();
        }

        /// <summary>角度を微調整したときは、余白(透明部分)が入らない最大の範囲にする。</summary>
        private Rectangle DefaultCrop(Size bounds)
        {
            if (_fine == 0 || _source is null) return new Rectangle(Point.Empty, bounds);
            var turned = _quarter % 2 == 0 ? _source.Size : new Size(_source.Height, _source.Width);
            var inner = LargestInnerRectangle(turned.Width, turned.Height, _fine * Math.PI / 180);
            int w = Math.Max(1, (int)Math.Floor(inner.Width));
            int h = Math.Max(1, (int)Math.Floor(inner.Height));
            return new Rectangle((bounds.Width - w) / 2, (bounds.Height - h) / 2, w, h);
        }

        /// <summary>w×hの画像をangleだけ回したとき、内側に収まる最大の(軸に沿った)長方形。</summary>
        internal static SizeF LargestInnerRectangle(double w, double h, double angle)
        {
            double sin = Math.Abs(Math.Sin(angle)), cos = Math.Abs(Math.Cos(angle));
            if (w <= 0 || h <= 0 || sin < 1e-9) return new SizeF((float)w, (float)h);
            bool widthLonger = w >= h;
            double longSide = widthLonger ? w : h, shortSide = widthLonger ? h : w;
            if (shortSide <= 2 * sin * cos * longSide || Math.Abs(sin - cos) < 1e-10)
            {
                double x = 0.5 * shortSide;
                return widthLonger ? new SizeF((float)(x / sin), (float)(x / cos))
                                   : new SizeF((float)(x / cos), (float)(x / sin));
            }
            double cos2 = cos * cos - sin * sin;
            return new SizeF((float)((w * cos - h * sin) / cos2), (float)((h * cos - w * sin) / cos2));
        }


        private double? CurrentRatio()
        {
            var ratio = Ratios[Math.Max(0, _ratio.SelectedIndex)].Ratio;
            if (ratio != 0 || _source is null) return ratio;
            var turned = _quarter % 2 == 0 ? _source.Size : new Size(_source.Height, _source.Width);
            return (double)turned.Width / turned.Height;
        }

        private void ApplyRatio()
        {
            if (_source is null) return;
            _canvas.AspectRatio = CurrentRatio();
            if (_canvas.AspectRatio is double ratio) _canvas.Crop = FitRatio(_canvas.Crop, ratio);
        }

        /// <summary>今の範囲の中心を保ったまま、指定の縦横比で収まる最大の範囲にする。</summary>
        private static Rectangle FitRatio(Rectangle crop, double ratio)
        {
            double w = crop.Width, h = crop.Height;
            if (w / h > ratio) w = h * ratio; else h = w / ratio;
            int iw = Math.Max(1, (int)Math.Round(w)), ih = Math.Max(1, (int)Math.Round(h));
            return new Rectangle(crop.Left + (crop.Width - iw) / 2, crop.Top + (crop.Height - ih) / 2, iw, ih);
        }

        private void OnCanvasCropChanged()
        {
            UpdateInputs();
        }

        private void UpdateInputs()
        {
            var crop = _canvas.Crop;
            var size = _canvas.WorkSize;
            _syncing = true;
            foreach (var input in new[] { _top, _bottom, _left, _right })
                input.Maximum = Math.Max(1, Math.Max(size.Width, size.Height));
            _top.Value = crop.Top;
            _bottom.Value = Math.Max(0, size.Height - crop.Bottom);
            _left.Value = crop.Left;
            _right.Value = Math.Max(0, size.Width - crop.Right);
            _syncing = false;
            _outputSize.Text = $"保存するサイズ: {crop.Width:N0} × {crop.Height:N0} px";
        }

        private void OnTrimChanged()
        {
            if (_syncing || _source is null) return;
            var size = _canvas.WorkSize;
            int left = (int)_left.Value, top = (int)_top.Value;
            int right = size.Width - (int)_right.Value, bottom = size.Height - (int)_bottom.Value;
            if (right - left < 1 || bottom - top < 1)
            {
                UpdateInputs(); // 範囲がなくなる値は受け付けず、元の値へ戻す
                return;
            }
            // 数値で直接決めたときは縦横比の固定を外す
            if (_ratio.SelectedIndex != 0)
            {
                _syncing = true;
                _ratio.SelectedIndex = 0;
                _syncing = false;
                _canvas.AspectRatio = null;
            }
            _canvas.Crop = Rectangle.FromLTRB(left, top, right, bottom);
        }


        private Bitmap RenderOutput()
        {
            var crop = _canvas.Crop;
            using var transform = BuildTransform(_source!.Size, out _);
            var output = new Bitmap(crop.Width, crop.Height, PixelFormat.Format32bppPArgb);
            using var g = Graphics.FromImage(output);
            using var attributes = new ImageAttributes();
            attributes.SetWrapMode(WrapMode.TileFlipXY);
            g.Clear(Color.Transparent);
            // 90°単位だけなら画素をそのまま写し、微調整の回転があるときだけ補間する
            bool exact = _fine == 0;
            g.InterpolationMode = exact ? InterpolationMode.NearestNeighbor : InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = exact ? PixelOffsetMode.Half : PixelOffsetMode.HighQuality;
            g.TranslateTransform(-crop.X, -crop.Y);
            g.MultiplyTransform(transform);
            g.DrawImage(_source, new Rectangle(0, 0, _source.Width, _source.Height),
                0, 0, _source.Width, _source.Height, GraphicsUnit.Pixel, attributes);
            return output;
        }

        private async void SaveCopy()
        {
            if (_source is null || !_save.Enabled) return;
            _save.Enabled = false;
            UseWaitCursor = true;
            string path = ImageCodec.CopyPathFor(_path);
            try
            {
                using var output = RenderOutput();
                try
                {
                    await Task.Run(() => ImageCodec.Save(output, path));
                }
                catch (UnauthorizedAccessException)
                {
                    // 書き込めないフォルダーなら保存先を選んでもらう
                    using var dialog = new SaveFileDialog
                    {
                        FileName = Path.GetFileName(path),
                        Filter = "画像|*" + Path.GetExtension(path),
                        InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
                    };
                    if (dialog.ShowDialog(this) != DialogResult.OK) return;
                    path = dialog.FileName;
                    if (File.Exists(path)) File.Delete(path);
                    await Task.Run(() => ImageCodec.Save(output, path));
                }
                Saved?.Invoke(path);
                Close();
                return;
            }
            catch (Exception ex)
            {
                _status.ForeColor = Color.Firebrick;
                _status.Text = $"保存できませんでした: {ex.Message}";
            }
            finally
            {
                UseWaitCursor = false;
                if (!IsDisposed) _save.Enabled = true;
            }
        }


        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (_source is not null)
            {
                switch (keyData)
                {
                    case Keys.Control | Keys.S: SaveCopy(); return true;
                    case Keys.Control | Keys.R: Turn(1); return true;
                    case Keys.Control | Keys.L: Turn(-1); return true;
                    case Keys.Control | Keys.D0: case Keys.Control | Keys.NumPad0: _canvas.FitToView(); return true;
                    case Keys.Control | Keys.D1: case Keys.Control | Keys.NumPad1: _canvas.SetZoom(1); return true;
                    case Keys.Control | Keys.Oemplus: case Keys.Control | Keys.Add: _canvas.ZoomBy(1.25); return true;
                    case Keys.Control | Keys.OemMinus: case Keys.Control | Keys.Subtract: _canvas.ZoomBy(1 / 1.25); return true;
                }
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (!ReferenceEquals(_preview, _source)) _preview?.Dispose();
                _source?.Dispose();
                _tips.Dispose();
            }
            base.Dispose(disposing);
        }
    }

    /// <summary>
    /// 作業画像の表示とトリミング範囲の操作。範囲は作業画像の画素座標で持つ。
    /// </summary>
    internal sealed class CropCanvas : Control
    {
        private enum Hit { None, Move, Left, Top, Right, Bottom, TopLeft, TopRight, BottomLeft, BottomRight, New }

        private static readonly Color Background = Color.FromArgb(36, 36, 36);
        private Bitmap? _image;
        private float _imageScale = 1;   // 表示画像の画素 / 作業画像の画素
        private Size _baseSize;          // 角度の微調整を入れる前の画像サイズ(作業画像の画素)
        private float _angle;            // 表示時に傾ける角度(度)
        private Size _workSize;          // 傾けた後の外接サイズ
        private Rectangle _crop;
        private double _zoom = 1;        // 画面の画素 / 作業画像の画素
        private PointF _offset;
        private bool _fit = true;
        private Hit _drag;
        private PointF _dragStartWork;
        private Rectangle _dragStartCrop;
        private Point _dragStartMouse;
        private bool _panning;
        private Point _panStart;
        private PointF _panStartOffset;
        private TextureBrush? _checker;

        public CropCanvas()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer
                     | ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            BackColor = Background;
            TabStop = true;
            AccessibleName = "トリミング範囲";
        }

        public event Action? CropChanged;
        public event Action? ZoomChanged;

        public string? Message
        {
            get => _message;
            set { _message = value; Invalidate(); }
        }
        private string? _message;

        public double? AspectRatio { get; set; }

        /// <summary>範囲の内側に円を重ね、丸く切り抜いたときの見え方を示す(保存には影響しない)。</summary>
        public bool CircleGuide
        {
            get => _circleGuide;
            set { if (_circleGuide == value) return; _circleGuide = value; Invalidate(); }
        }
        private bool _circleGuide;
        public Size WorkSize => _workSize;
        public double Zoom => _zoom;

        public Rectangle Crop
        {
            get => _crop;
            set
            {
                var bounds = new Rectangle(Point.Empty, _workSize);
                var next = Rectangle.Intersect(value, bounds);
                if (next.Width < 1 || next.Height < 1) next = bounds;
                if (next == _crop) return;
                _crop = next;
                Invalidate();
                CropChanged?.Invoke();
            }
        }

        private float Dpi => DeviceDpi / 96f;

        /// <summary>傾ける前の画像を差し替える(90°回転・反転のとき)。</summary>
        public void SetImage(Bitmap image, float imageScale, Size baseSize)
        {
            var old = _image;
            _image = image;
            old?.Dispose();
            _imageScale = imageScale;
            _baseSize = baseSize;
            _refit = true;
        }
        private bool _refit;

        /// <summary>傾きと範囲を変える。画像は作り直さないので、スライダー操作中も軽い。</summary>
        public void SetRotation(float angle, Size workSize, Rectangle crop)
        {
            bool first = _workSize.IsEmpty;
            _angle = angle;
            // 外接サイズが変わっても、画像の中心が同じ場所に見えるよう表示位置をずらす
            if (!first && workSize != _workSize)
            {
                _offset = new PointF(
                    (float)(_offset.X - (workSize.Width - _workSize.Width) / 2.0 * _zoom),
                    (float)(_offset.Y - (workSize.Height - _workSize.Height) / 2.0 * _zoom));
            }
            _workSize = workSize;
            _crop = Rectangle.Empty;
            Crop = crop;
            if (first || _refit)
            {
                _refit = false;
                FitToView();
            }
            Invalidate();
        }

        public void FitToView()
        {
            if (_workSize.IsEmpty || Width <= 0 || Height <= 0) return;
            float margin = 24 * Dpi;
            double z = Math.Min((Width - margin * 2) / _workSize.Width, (Height - margin * 2) / _workSize.Height);
            _zoom = Math.Clamp(z, 0.01, 32);
            _offset = new PointF((float)((Width - _workSize.Width * _zoom) / 2), (float)((Height - _workSize.Height * _zoom) / 2));
            _fit = true;
            Invalidate();
            ZoomChanged?.Invoke();
        }

        public void ZoomBy(double factor) => SetZoom(_zoom * factor);

        /// <summary>指定の位置(省略時は中央)を動かさずに倍率を変える。</summary>
        public void SetZoom(double zoom, PointF? anchor = null)
        {
            if (_workSize.IsEmpty) return;
            var a = anchor ?? new PointF(Width / 2f, Height / 2f);
            var work = ToWork(a);
            _zoom = Math.Clamp(zoom, 0.01, 32);
            _offset = new PointF((float)(a.X - work.X * _zoom), (float)(a.Y - work.Y * _zoom));
            _fit = false;
            Invalidate();
            ZoomChanged?.Invoke();
        }

        private RectangleF ToScreen(RectangleF r) => new(
            (float)(_offset.X + r.X * _zoom), (float)(_offset.Y + r.Y * _zoom),
            (float)(r.Width * _zoom), (float)(r.Height * _zoom));

        private PointF ToWork(PointF p) => new((float)((p.X - _offset.X) / _zoom), (float)((p.Y - _offset.Y) / _zoom));

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            if (_fit) FitToView();
        }

        // ==== 描画 ====

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(Background);
            if (_image is null || _message is not null)
            {
                TextRenderer.DrawText(g, _message ?? "", Font, ClientRectangle, Color.FromArgb(200, 200, 200),
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
                return;
            }

            var imageRect = ToScreen(new RectangleF(0, 0, _workSize.Width, _workSize.Height));
            _checker ??= CreateChecker((int)Math.Round(8 * Dpi));
            g.FillRectangle(_checker, imageRect);

            double pixelScale = _zoom / _imageScale;
            g.InterpolationMode = pixelScale >= 2 ? InterpolationMode.NearestNeighbor
                : _angle != 0 ? InterpolationMode.Bilinear : InterpolationMode.HighQualityBilinear;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            var state = g.Save();
            g.TranslateTransform(imageRect.Left + imageRect.Width / 2f, imageRect.Top + imageRect.Height / 2f);
            if (_angle != 0) g.RotateTransform(_angle);
            float bw = (float)(_baseSize.Width * _zoom), bh = (float)(_baseSize.Height * _zoom);
            g.DrawImage(_image, -bw / 2f, -bh / 2f, bw, bh);
            g.Restore(state);
            g.PixelOffsetMode = PixelOffsetMode.Default;

            var crop = ToScreen(_crop);
            using (var outside = new Region(imageRect))
            using (var dim = new SolidBrush(Color.FromArgb(150, 0, 0, 0)))
            {
                outside.Exclude(crop);
                g.FillRegion(dim, outside);
            }

            // 三分割の補助線は操作中だけ濃くする
            int gridAlpha = _drag != Hit.None ? 110 : 55;
            using (var grid = new Pen(Color.FromArgb(gridAlpha, 255, 255, 255)))
            {
                for (int i = 1; i <= 2; i++)
                {
                    float x = crop.Left + crop.Width * i / 3f;
                    float y = crop.Top + crop.Height * i / 3f;
                    g.DrawLine(grid, x, crop.Top, x, crop.Bottom);
                    g.DrawLine(grid, crop.Left, y, crop.Right, y);
                }
            }
            using (var shade = new Pen(Color.FromArgb(120, 0, 0, 0), 3))
                g.DrawRectangle(shade, crop.X, crop.Y, crop.Width, crop.Height);
            using (var border = new Pen(Color.White, 1))
                g.DrawRectangle(border, crop.X, crop.Y, crop.Width, crop.Height);

            if (_circleGuide) DrawCircleGuide(g, crop);
            DrawHandles(g, crop);
            if (_drag is not Hit.None) DrawSizeBadge(g, crop);
        }

        private static void DrawCircleGuide(Graphics g, RectangleF crop)
        {
            // 円の外側(四隅)を外と同じ暗さにして、アイコンとして見える部分だけを明るく残す
            var old = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var circle = new GraphicsPath())
            {
                circle.AddEllipse(crop);
                using var corners = new Region(crop);
                corners.Exclude(circle);
                using var dim = new SolidBrush(Color.FromArgb(150, 0, 0, 0));
                g.FillRegion(dim, corners);
            }
            using (var outline = new Pen(Color.FromArgb(230, 255, 255, 255), 1.5f))
                g.DrawEllipse(outline, crop);
            g.SmoothingMode = old;
        }

        private void DrawHandles(Graphics g, RectangleF r)
        {
            // 角はL字、辺は短い棒にして、つかめる場所をはっきり見せる
            float len = 14 * Dpi, thick = 3 * Dpi;
            using var brush = new SolidBrush(Color.White);
            using var shadow = new SolidBrush(Color.FromArgb(110, 0, 0, 0));
            void Bar(float x, float y, float w, float h)
            {
                g.FillRectangle(shadow, x - 1, y - 1, w + 2, h + 2);
                g.FillRectangle(brush, x, y, w, h);
            }
            float l = r.Left - thick, t = r.Top - thick, rr = r.Right, b = r.Bottom;
            float cl = Math.Min(len, r.Width / 2), ch = Math.Min(len, r.Height / 2);
            Bar(l, t, cl + thick, thick); Bar(l, t, thick, ch + thick);
            Bar(rr - cl, t, cl + thick, thick); Bar(rr, t, thick, ch + thick);
            Bar(l, b, cl + thick, thick); Bar(l, b - ch, thick, ch + thick);
            Bar(rr - cl, b, cl + thick, thick); Bar(rr, b - ch, thick, ch + thick);
            if (r.Width > len * 3) { Bar(r.Left + r.Width / 2 - len / 2, t, len, thick); Bar(r.Left + r.Width / 2 - len / 2, b, len, thick); }
            if (r.Height > len * 3) { Bar(l, r.Top + r.Height / 2 - len / 2, thick, len); Bar(rr, r.Top + r.Height / 2 - len / 2, thick, len); }
        }

        private void DrawSizeBadge(Graphics g, RectangleF crop)
        {
            string text = $"{_crop.Width:N0} × {_crop.Height:N0}";
            var size = TextRenderer.MeasureText(g, text, Font);
            float pad = 6 * Dpi;
            var box = new RectangleF(crop.Left + crop.Width / 2 - size.Width / 2f - pad,
                Math.Min(crop.Bottom + 8 * Dpi, Height - size.Height - pad * 2), size.Width + pad * 2, size.Height + pad);
            using var fill = new SolidBrush(Color.FromArgb(200, 20, 20, 20));
            g.FillRectangle(fill, box);
            TextRenderer.DrawText(g, text, Font, Rectangle.Round(box), Color.White,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        }

        private static TextureBrush CreateChecker(int cell)
        {
            cell = Math.Max(4, cell);
            using var tile = new Bitmap(cell * 2, cell * 2);
            using (var g = Graphics.FromImage(tile))
            {
                g.Clear(Color.FromArgb(70, 70, 70));
                using var dark = new SolidBrush(Color.FromArgb(56, 56, 56));
                g.FillRectangle(dark, 0, 0, cell, cell);
                g.FillRectangle(dark, cell, cell, cell, cell);
            }
            return new TextureBrush(tile);
        }

        // ==== マウス操作 ====

        private Hit HitTest(Point p)
        {
            var r = ToScreen(_crop);
            float t = 10 * Dpi;
            bool nearL = Math.Abs(p.X - r.Left) <= t, nearR = Math.Abs(p.X - r.Right) <= t;
            bool nearT = Math.Abs(p.Y - r.Top) <= t, nearB = Math.Abs(p.Y - r.Bottom) <= t;
            bool inX = p.X >= r.Left - t && p.X <= r.Right + t, inY = p.Y >= r.Top - t && p.Y <= r.Bottom + t;
            if (nearL && nearT) return Hit.TopLeft;
            if (nearR && nearT) return Hit.TopRight;
            if (nearL && nearB) return Hit.BottomLeft;
            if (nearR && nearB) return Hit.BottomRight;
            if (nearL && inY) return Hit.Left;
            if (nearR && inY) return Hit.Right;
            if (nearT && inX) return Hit.Top;
            if (nearB && inX) return Hit.Bottom;
            if (r.Contains(p)) return Hit.Move;
            return Hit.New;
        }

        private static Cursor CursorFor(Hit hit) => hit switch
        {
            Hit.TopLeft or Hit.BottomRight => Cursors.SizeNWSE,
            Hit.TopRight or Hit.BottomLeft => Cursors.SizeNESW,
            Hit.Left or Hit.Right => Cursors.SizeWE,
            Hit.Top or Hit.Bottom => Cursors.SizeNS,
            Hit.Move => Cursors.SizeAll,
            _ => Cursors.Cross,
        };

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            Focus();
            if (_image is null || _message is not null) return;
            if (e.Button is MouseButtons.Right or MouseButtons.Middle)
            {
                _panning = true;
                _panStart = e.Location;
                _panStartOffset = _offset;
                Cursor = Cursors.Hand;
                return;
            }
            if (e.Button != MouseButtons.Left) return;
            _drag = HitTest(e.Location);
            _dragStartMouse = e.Location;
            _dragStartCrop = _crop;
            var w = ToWork(e.Location);
            _dragStartWork = new PointF(Math.Clamp(w.X, 0, _workSize.Width), Math.Clamp(w.Y, 0, _workSize.Height));
            Capture = true;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (_image is null || _message is not null) return;
            if (_panning)
            {
                _offset = new PointF(_panStartOffset.X + e.X - _panStart.X, _panStartOffset.Y + e.Y - _panStart.Y);
                _fit = false;
                Invalidate();
                return;
            }
            if (_drag == Hit.None)
            {
                Cursor = CursorFor(HitTest(e.Location));
                return;
            }
            if (_drag == Hit.New && Math.Abs(e.X - _dragStartMouse.X) + Math.Abs(e.Y - _dragStartMouse.Y) < 4) return;
            var p = ToWork(e.Location);
            p = new PointF(Math.Clamp(p.X, 0, _workSize.Width), Math.Clamp(p.Y, 0, _workSize.Height));
            var next = _drag == Hit.Move ? Moved(p) : Resized(p);
            if (next.Width >= 1 && next.Height >= 1 && next != _crop)
            {
                _crop = next;
                Invalidate();
                CropChanged?.Invoke();
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (_panning)
            {
                _panning = false;
                Cursor = CursorFor(HitTest(e.Location));
                return;
            }
            if (_drag == Hit.None) return;
            _drag = Hit.None;
            Capture = false;
            Invalidate();
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            if (_image is null) return;
            SetZoom(_zoom * Math.Pow(1.15, e.Delta / 120.0), e.Location);
        }

        protected override void OnMouseDoubleClick(MouseEventArgs e)
        {
            base.OnMouseDoubleClick(e);
            if (e.Button == MouseButtons.Left && HitTest(e.Location) == Hit.New) FitToView();
        }

        protected override bool IsInputKey(Keys keyData)
            => (keyData & Keys.KeyCode) is Keys.Left or Keys.Right or Keys.Up or Keys.Down || base.IsInputKey(keyData);

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (_image is null) return;
            // 矢印キーで範囲を1px(Shiftで10px)ずつ動かす
            int step = e.Shift ? 10 : 1;
            var delta = e.KeyCode switch
            {
                Keys.Left => new Size(-step, 0), Keys.Right => new Size(step, 0),
                Keys.Up => new Size(0, -step), Keys.Down => new Size(0, step),
                _ => Size.Empty,
            };
            if (delta.IsEmpty) return;
            var next = _crop;
            next.X = Math.Clamp(next.X + delta.Width, 0, _workSize.Width - next.Width);
            next.Y = Math.Clamp(next.Y + delta.Height, 0, _workSize.Height - next.Height);
            Crop = next;
            e.Handled = true;
        }

        private Rectangle Moved(PointF p)
        {
            var s = _dragStartCrop;
            int x = (int)Math.Round(s.X + p.X - _dragStartWork.X);
            int y = (int)Math.Round(s.Y + p.Y - _dragStartWork.Y);
            return new Rectangle(Math.Clamp(x, 0, _workSize.Width - s.Width), Math.Clamp(y, 0, _workSize.Height - s.Height), s.Width, s.Height);
        }

        /// <summary>つかんだ辺・角を動かす。反対側は固定し、縦横比の指定があれば保つ。</summary>
        private Rectangle Resized(PointF p)
        {
            var s = _dragStartCrop;
            var bounds = new RectangleF(0, 0, _workSize.Width, _workSize.Height);
            float l = s.Left, t = s.Top, r = s.Right, b = s.Bottom;
            switch (_drag)
            {
                case Hit.New:
                    l = _dragStartWork.X; t = _dragStartWork.Y; r = p.X; b = p.Y;
                    break;
                case Hit.Left: l = p.X; break;
                case Hit.Right: r = p.X; break;
                case Hit.Top: t = p.Y; break;
                case Hit.Bottom: b = p.Y; break;
                case Hit.TopLeft: l = p.X; t = p.Y; break;
                case Hit.TopRight: r = p.X; t = p.Y; break;
                case Hit.BottomLeft: l = p.X; b = p.Y; break;
                case Hit.BottomRight: r = p.X; b = p.Y; break;
            }

            if (AspectRatio is not double ratio)
            {
                if (_drag == Hit.New) return Normalize(l, t, r, b);
                // 辺を反対側より先へは動かさない
                if (l > s.Right - 1) l = s.Right - 1;
                if (r < s.Left + 1) r = s.Left + 1;
                if (t > s.Bottom - 1) t = s.Bottom - 1;
                if (b < s.Top + 1) b = s.Top + 1;
                return Normalize(l, t, r, b);
            }

            if (_drag is Hit.Left or Hit.Right or Hit.Top or Hit.Bottom)
            {
                // 辺をつかんだときは、もう一方の向きを中心から均等に伸縮する
                bool horizontal = _drag is Hit.Left or Hit.Right;
                float cx = (s.Left + s.Right) / 2f, cy = (s.Top + s.Bottom) / 2f;
                float w = Math.Max(1, horizontal ? (_drag == Hit.Left ? s.Right - l : r - s.Left) : (b - t) * (float)ratio);
                float h = Math.Max(1, horizontal ? w / (float)ratio : (_drag == Hit.Top ? s.Bottom - t : b - s.Top));
                if (horizontal)
                {
                    float maxH = 2 * Math.Min(cy - bounds.Top, bounds.Bottom - cy);
                    float maxW = _drag == Hit.Left ? s.Right - bounds.Left : bounds.Right - s.Left;
                    Shrink(ref w, ref h, maxW, maxH, ratio);
                    return _drag == Hit.Left ? Normalize(s.Right - w, cy - h / 2, s.Right, cy + h / 2)
                                             : Normalize(s.Left, cy - h / 2, s.Left + w, cy + h / 2);
                }
                else
                {
                    float maxW = 2 * Math.Min(cx - bounds.Left, bounds.Right - cx);
                    float maxH = _drag == Hit.Top ? s.Bottom - bounds.Top : bounds.Bottom - s.Top;
                    Shrink(ref w, ref h, maxW, maxH, ratio);
                    return _drag == Hit.Top ? Normalize(cx - w / 2, s.Bottom - h, cx + w / 2, s.Bottom)
                                            : Normalize(cx - w / 2, s.Top, cx + w / 2, s.Top + h);
                }
            }

            // 角(または新規作成)は、つかんでいない側の角を固定する
            float ax, ay;
            switch (_drag)
            {
                case Hit.TopLeft: ax = s.Right; ay = s.Bottom; break;
                case Hit.TopRight: ax = s.Left; ay = s.Bottom; break;
                case Hit.BottomLeft: ax = s.Right; ay = s.Top; break;
                case Hit.BottomRight: ax = s.Left; ay = s.Top; break;
                default: ax = _dragStartWork.X; ay = _dragStartWork.Y; break;
            }
            float dirX = p.X >= ax ? 1 : -1, dirY = p.Y >= ay ? 1 : -1;
            float cw = Math.Max(1, Math.Abs(p.X - ax)), chh = Math.Max(1, Math.Abs(p.Y - ay));
            if (cw / chh > ratio) chh = cw / (float)ratio; else cw = chh * (float)ratio;
            float limitW = dirX > 0 ? bounds.Right - ax : ax - bounds.Left;
            float limitH = dirY > 0 ? bounds.Bottom - ay : ay - bounds.Top;
            Shrink(ref cw, ref chh, limitW, limitH, ratio);
            return Normalize(ax, ay, ax + dirX * cw, ay + dirY * chh);
        }

        private static void Shrink(ref float w, ref float h, float maxW, float maxH, double ratio)
        {
            if (w > maxW) { w = maxW; h = w / (float)ratio; }
            if (h > maxH) { h = maxH; w = h * (float)ratio; }
        }

        private Rectangle Normalize(float x1, float y1, float x2, float y2)
        {
            int l = (int)Math.Round(Math.Min(x1, x2)), r = (int)Math.Round(Math.Max(x1, x2));
            int t = (int)Math.Round(Math.Min(y1, y2)), b = (int)Math.Round(Math.Max(y1, y2));
            l = Math.Clamp(l, 0, _workSize.Width - 1); t = Math.Clamp(t, 0, _workSize.Height - 1);
            r = Math.Clamp(Math.Max(r, l + 1), 1, _workSize.Width); b = Math.Clamp(Math.Max(b, t + 1), 1, _workSize.Height);
            return Rectangle.FromLTRB(l, t, r, b);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _image?.Dispose();
                _checker?.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
