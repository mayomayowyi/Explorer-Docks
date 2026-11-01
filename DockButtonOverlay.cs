using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using Microsoft.Win32;

namespace ExplorerDocks
{
    /// <summary>
    /// 各エクスプローラーウィンドウのタイトルバー上にDockボタン/Dock解除ボタンを重ねて表示する。
    /// エクスプローラー本体には手を加えず、追従する別ウィンドウとして描画する。
    /// </summary>
    internal sealed class DockButtonManager : IDisposable
    {
        private const int SyncIntervalMs = 1200;

        private readonly Dictionary<IntPtr, ExplorerDockButton> _buttons = new();
        private readonly System.Windows.Forms.Timer _timer = new();
        private readonly System.Windows.Forms.Timer _followTimer = new() { Interval = 16 };
        private readonly HashSet<IntPtr> _pendingFollow = new();
        private readonly HashSet<IntPtr> _moving = new();
        private bool _pendingSync;
        private bool _animationsEnabled = true;
        private bool _darkTheme = ReadDarkTheme();
        private readonly Func<IntPtr, bool> _canDock;
        private readonly Func<IntPtr, bool> _isDockedWindow;
        private readonly Func<IntPtr, bool> _isPinnedWindow;
        private readonly Action<IntPtr> _dockClick;
        private readonly Action<IntPtr> _pinClick;
        private readonly Action<IntPtr, Point> _contextMenu;
        private readonly Win32.WinEventDelegate _winEvent;
        private readonly IntPtr _locationHook;
        private readonly IntPtr _destroyHook;
        private readonly IntPtr _stateHook;

        private bool _disposed;

        public DockButtonManager(Action<IntPtr> dockClick, Func<IntPtr, bool> canDock,
            Func<IntPtr, bool> isDockedWindow, Func<IntPtr, bool> isPinnedWindow,
            Action<IntPtr> pinClick, Action<IntPtr, Point> contextMenu)
        {
            _dockClick = dockClick;
            _pinClick = pinClick;
            _contextMenu = contextMenu;
            _canDock = canDock;
            _isDockedWindow = isDockedWindow;
            _isPinnedWindow = isPinnedWindow;

            _winEvent = OnWinEvent;
            const uint flags = Win32.WINEVENT_OUTOFCONTEXT | Win32.WINEVENT_SKIPOWNPROCESS;
            _locationHook = Win32.SetWinEventHook(
                Win32.EVENT_OBJECT_LOCATIONCHANGE, Win32.EVENT_OBJECT_LOCATIONCHANGE,
                IntPtr.Zero, _winEvent, 0, 0, flags);
            _destroyHook = Win32.SetWinEventHook(
                Win32.EVENT_OBJECT_DESTROY, Win32.EVENT_OBJECT_DESTROY,
                IntPtr.Zero, _winEvent, 0, 0, flags);
            _stateHook = Win32.SetWinEventHook(
                Win32.EVENT_SYSTEM_FOREGROUND, Win32.EVENT_SYSTEM_MINIMIZEEND,
                IntPtr.Zero, _winEvent, 0, 0, flags);

            _timer.Interval = SyncIntervalMs;
            _timer.Tick += (_, _) => Sync();
            _timer.Start();
            _followTimer.Tick += (_, _) => FlushPending();

            Sync();
        }

        public bool AnimationsEnabled
        {
            set
            {
                if (_animationsEnabled == value) return;
                _animationsEnabled = value;
                foreach (var button in _buttons.Values) button.AnimationsEnabled = value;
            }
        }

        public void Refresh() => Sync();

        public void SetDockWindowVisible(IntPtr hWnd, bool visible)
        {
            if (_disposed) return;
            if (!visible)
            {
                if (_buttons.TryGetValue(hWnd, out var button)) button.HideForDockTransition();
                _pendingFollow.Remove(hWnd);
                return;
            }
            UpdateButton(hWnd);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _timer.Stop();
            _timer.Dispose();
            _followTimer.Stop();
            _followTimer.Dispose();
            if (_locationHook != IntPtr.Zero) Win32.UnhookWinEvent(_locationHook);
            if (_destroyHook != IntPtr.Zero) Win32.UnhookWinEvent(_destroyHook);
            if (_stateHook != IntPtr.Zero) Win32.UnhookWinEvent(_stateHook);

            foreach (var button in _buttons.Values)
            {
                button.Dispose();
            }
            _buttons.Clear();
        }

        private void Sync()
        {
            if (_disposed || _moving.Count != 0) return;

            // テーマはボタンごとではなく同期1回につき1度だけ読む
            _darkTheme = ReadDarkTheme();
            var alive = new HashSet<IntPtr>(DockService.FindExplorers());
            foreach (var hWnd in alive)
            {
                UpdateButton(hWnd);
            }

            foreach (var hwnd in _buttons.Keys.Where(h => !alive.Contains(h)).ToList())
            {
                // 収納中のExplorerは非表示になるが、Dock自体は生きている。
                // ボタンを破棄せず保持し、次の展開時に同じオーバーレイを再利用する。
                if (Win32.IsWindow(hwnd) && _isDockedWindow(hwnd))
                {
                    _buttons[hwnd].HideForDockTransition();
                    continue;
                }
                RemoveButton(hwnd);
            }
        }

        private static bool ReadDarkTheme()
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
        }

        private void UpdateButton(IntPtr hWnd)
        {
            bool docked = _isDockedWindow(hWnd);
            if (!docked && !_canDock(hWnd))
            {
                if (_buttons.TryGetValue(hWnd, out var hidden))
                {
                    hidden.Hide();
                }
                return;
            }

            if (!_buttons.TryGetValue(hWnd, out var button))
            {
                button = new ExplorerDockButton(hWnd, () => OnDockClicked(hWnd), () => OnPinClicked(hWnd))
                {
                    AnimationsEnabled = _animationsEnabled,
                };
                button.MouseUp += (_, e) =>
                {
                    if (e.Button == MouseButtons.Right && _isDockedWindow(hWnd))
                        _contextMenu(hWnd, Cursor.Position);
                };
                _buttons[hWnd] = button;
            }
            button.SetState(_darkTheme, docked, docked && _isPinnedWindow(hWnd));
            button.FollowExplorer();
        }

        private void OnDockClicked(IntPtr hWnd)
        {
            if (_canDock(hWnd) || _isDockedWindow(hWnd))
            {
                _dockClick(hWnd);
            }
        }

        private void OnPinClicked(IntPtr hWnd)
        {
            if (_isDockedWindow(hWnd)) _pinClick(hWnd);
        }

        private void RemoveButton(IntPtr hWnd)
        {
            if (!_buttons.TryGetValue(hWnd, out var button)) return;

            _buttons.Remove(hWnd);
            _pendingFollow.Remove(hWnd);
            _moving.Remove(hWnd);
            button.Dispose();
        }

        private void QueueFollow(IntPtr hWnd)
        {
            if (_moving.Contains(hWnd)) return;
            _pendingFollow.Add(hWnd);
            _followTimer.Start();
        }

        private void FollowAfterMove(IntPtr hWnd, ExplorerDockButton button)
        {
            // 移動終了後に1回だけ再配置する。16msタイマーによる追従は不要。
            if (!button.IsHandleCreated)
            {
                QueueFollow(hWnd);
                return;
            }
            button.BeginInvoke((Action)(() =>
            {
                if (!_disposed && !_moving.Contains(hWnd)
                    && _buttons.TryGetValue(hWnd, out var current)
                    && ReferenceEquals(current, button))
                {
                    button.FollowExplorer();
                }
            }));
        }

        private void FlushPending()
        {
            _followTimer.Stop();
            if (_disposed) return;

            if (_pendingSync)
            {
                _pendingSync = false;
                Sync();
            }

            foreach (var hWnd in _pendingFollow)
            {
                if (!_moving.Contains(hWnd) && _buttons.TryGetValue(hWnd, out var button))
                {
                    button.FollowExplorer(false);
                }
            }
            _pendingFollow.Clear();
        }

        private void OnWinEvent(IntPtr hook, uint eventType, IntPtr hWnd, int idObject, int idChild,
            uint threadId, uint eventTime)
        {
            if (_disposed) return;

            try
            {
                if (eventType == Win32.EVENT_SYSTEM_FOREGROUND
                    || eventType == Win32.EVENT_SYSTEM_MINIMIZEEND)
                {
                    _pendingSync = true;
                    _followTimer.Start();
                    return;
                }

                // LOCATIONCHANGEはキャレット等でも頻発するので、ウィンドウ自身以外は先に捨てる
                if (idObject != Win32.OBJID_WINDOW || idChild != 0) return;
                if (!_buttons.TryGetValue(hWnd, out var button)) return;

                if (eventType == Win32.EVENT_OBJECT_DESTROY)
                {
                    RemoveButton(hWnd);
                    return;
                }
                if (eventType == Win32.EVENT_SYSTEM_MINIMIZESTART)
                {
                    button.Hide();
                    return;
                }
                if (eventType == Win32.EVENT_SYSTEM_MOVESIZESTART)
                {
                    if (_moving.Add(hWnd))
                    {
                        _pendingFollow.Remove(hWnd);
                        button.SuspendFollowing();
                    }
                    return;
                }
                if (eventType == Win32.EVENT_SYSTEM_MOVESIZEEND)
                {
                    _moving.Remove(hWnd);
                    button.ResumeFollowing();
                    _pendingFollow.Remove(hWnd);
                    FollowAfterMove(hWnd, button);
                    return;
                }

                if (eventType == Win32.EVENT_OBJECT_LOCATIONCHANGE)
                {
                    QueueFollow(hWnd);
                }
            }
            catch
            {
            }
        }
    }

    /// <summary>
    /// Explorerのタイトルバーに追従する操作ボタン。
    /// 未Dockは「Dock」1つ、Dock中は「固定 | 解除」を1つの角丸にまとめて表示する。
    /// </summary>
    internal sealed class ExplorerDockButton : LayeredForm
    {
        private const int CaptionButtonWidth = 46;
        private const int ButtonWidth = 48;
        private const int PinButtonWidth = 56;
        private const int ButtonHeight = 28;
        private const int RightMargin = 4;
        private const int CornerRadius = 4;
        private const double FadeInMs = 120;
        private const double FadeOutMs = 80;
        private const double HoverMs = 110;
        private const double PressMs = 60;
        private const double PinStateMs = 160;
        private const string FontFamilyName = "Yu Gothic UI";
        private const float FontPixelSize = 13f;
        private const float IconPixelSize = 12f;
        private const string PinGlyph = "";

        // タイトルバーになじむ半透明の無彩色。固定中の区画だけアクセント色にする。
        private static readonly Palette LightPalette = new(
            Color.FromArgb(205, 252, 252, 252), Color.FromArgb(235, 234, 234, 234), Color.FromArgb(240, 220, 220, 220),
            Color.FromArgb(40, 0, 0, 0), Color.FromArgb(28, 28, 28));
        private static readonly Palette DarkPalette = new(
            Color.FromArgb(205, 50, 50, 50), Color.FromArgb(235, 70, 70, 70), Color.FromArgb(240, 60, 60, 60),
            Color.FromArgb(40, 255, 255, 255), Color.FromArgb(242, 242, 242));
        private static readonly Palette AccentPalette = new(
            DockColors.Accent, DockColors.AccentHover, DockColors.AccentPressed,
            Color.FromArgb(40, 0, 0, 0), Color.White);

        // Windows 11はSegoe Fluent Icons、Windows 10はSegoe MDL2 Assetsにピンのアイコンがある
        private static readonly string? IconFontFamily = new[] { "Segoe Fluent Icons", "Segoe MDL2 Assets" }
            .FirstOrDefault(name => FontFamily.Families.Any(f => f.Name == name));

        private readonly IntPtr _target;
        private readonly Action _click;
        private readonly Action _pinClick;
        private readonly System.Windows.Forms.Timer _animationTimer = new() { Interval = 15 };
        private readonly ToolTip _toolTip = new() { InitialDelay = 600, ShowAlways = true };
        private readonly StringFormat _centered = new(StringFormat.GenericTypographic)
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
            FormatFlags = StringFormatFlags.NoWrap,
        };
        private Font? _iconFont;
        private bool _docked;
        private bool _pinned;
        private bool _dark;
        private bool _hovered;
        private bool _pinHovered;
        private bool _pressed;
        private bool _pinPressed;
        private bool _followingSuspended;
        private bool _animationsEnabled = true;
        private Rectangle _bounds;
        private double _scale = 1;
        private double _opacity;
        private double _opacityTarget;
        private double _hover;
        private double _press;
        private double _pinHover;
        private double _pinPress;
        private double _pinnedAmount;
        private long _lastTick;

        public ExplorerDockButton(IntPtr target, Action click, Action pinClick)
        {
            _target = target;
            _click = click;
            _pinClick = pinClick;
            Text = "Dock";
            Font = new Font(FontFamilyName, FontPixelSize, GraphicsUnit.Pixel);
            Cursor = Cursors.Hand;
            _animationTimer.Tick += OnAnimationTick;
            UpdateToolTip();
        }

        public bool AnimationsEnabled
        {
            set
            {
                if (_animationsEnabled == value) return;
                _animationsEnabled = value;
                if (!value) SnapAnimations();
            }
        }

        private Palette Neutral => _dark ? DarkPalette : LightPalette;

        public void SetState(bool dark, bool docked, bool pinned)
        {
            if (_docked == docked && _pinned == pinned && _dark == dark) return;
            bool layoutChanged = _docked != docked;
            _docked = docked;
            _pinned = pinned;
            _dark = dark;
            Text = docked ? "解除" : "Dock";
            UpdateToolTip();
            // 区画の数が変わるときは幅も変わるので、色の補間はせずに切り替える
            if (layoutChanged) _pinnedAmount = pinned ? 1 : 0;
            StartAnimation();
            if (Visible) Render();
        }

        private void UpdateToolTip()
        {
            string text = _pinHovered
                ? (_pinned ? "固定を解除して自動収納を有効にします" : "固定して自動で収納されないようにします")
                : _docked ? "Dockを解除して元の位置・大きさに戻します"
                : "このウィンドウを画面端に収納します";
            _toolTip.SetToolTip(this, text);
        }

        public void SuspendFollowing()
        {
            _followingSuspended = true;
            if (Visible) FadeTo(0);
        }

        public void ResumeFollowing() => _followingSuspended = false;

        public void HideForDockTransition() => HideImmediately();

        // ドラッグ中はボタンを動かさず隠す。終了後は正しい位置に1回だけ表示する。
        public void FollowExplorer(bool updateZOrder = true)
        {
            if (_followingSuspended) return;
            if (!Win32.IsWindowVisible(_target) || Win32.IsIconic(_target)
                || !Win32.GetWindowRect(_target, out var rect))
            {
                HideImmediately();
                return;
            }

            uint dpi = Math.Max(96u, Win32.GetDpiForWindow(_target));
            double scale = dpi / 96.0;
            int width = (int)Math.Round((ButtonWidth + (_docked ? PinButtonWidth : 0)) * scale);
            int height = (int)Math.Round(ButtonHeight * scale);
            int caption = (int)Math.Round(CaptionButtonWidth * scale);
            int margin = (int)Math.Round(RightMargin * scale);
            int frame = Win32.GetSystemMetricsForDpi(Win32.SM_CYCAPTION, dpi)
                        + 2 * (Win32.GetSystemMetricsForDpi(Win32.SM_CYSIZEFRAME, dpi)
                               + Win32.GetSystemMetricsForDpi(Win32.SM_CXPADDEDBORDER, dpi));

            if (rect.Right - rect.Left < caption * 3 + width + margin * 2)
            {
                HideImmediately();
                return;
            }

            int x = rect.Right - caption * 3 - margin - width;
            int y = rect.Top + Math.Max(0, (frame - height) / 2);

            if (!IsHandleCreated)
            {
                _ = Handle;
                Win32.SetOwner(Handle, _target);
            }

            // 文字もボタンと同じくExplorer側のDPIに合わせる(モニター間移動で大きさがずれないように)
            float fontSize = (float)(FontPixelSize * scale);
            if (Font.Unit != GraphicsUnit.Pixel || Math.Abs(Font.Size - fontSize) > 0.1f)
            {
                var oldFont = Font;
                Font = new Font(FontFamilyName, fontSize, GraphicsUnit.Pixel);
                oldFont.Dispose();
            }
            float iconSize = (float)(IconPixelSize * scale);
            if (IconFontFamily is not null && (_iconFont is null || Math.Abs(_iconFont.Size - iconSize) > 0.1f))
            {
                _iconFont?.Dispose();
                _iconFont = new Font(IconFontFamily, iconSize, GraphicsUnit.Pixel);
            }

            var bounds = new Rectangle(x, y, width, height);
            if (_bounds != bounds || _scale != scale)
            {
                bool redraw = _bounds.Size != bounds.Size || _scale != scale;
                _bounds = bounds;
                _scale = scale;
                if (redraw) Render();
                else if (Visible) PresentSurface(_bounds.Location, _bounds.Size, EaseOutCubic(_opacity));
            }
            if (!Visible)
            {
                // 前回の内容が一瞬映らないよう、透明な状態を描いてから表示する
                _opacity = 0;
                Render();
                Show();
                FadeTo(1);
                updateZOrder = true;
            }
            else if (_opacityTarget < 1)
            {
                FadeTo(1);
            }

            if (!updateZOrder) return;
            const uint flags = Win32.SWP_NOMOVE | Win32.SWP_NOSIZE
                | Win32.SWP_NOACTIVATE | Win32.SWP_NOOWNERZORDER;

            // Explorerの直前に重ねる。無条件のHWND_TOPMOSTは他の画面に
            // 固定・解除ボタンだけが飛び出して見える原因になる。
            bool targetTopmost = (Win32.GetWindowLong(_target, Win32.GWL_EXSTYLE)
                & Win32.WS_EX_TOPMOST) != 0;
            bool buttonTopmost = (Win32.GetWindowLong(Handle, Win32.GWL_EXSTYLE)
                & Win32.WS_EX_TOPMOST) != 0;
            if (targetTopmost != buttonTopmost)
                _ = Win32.SetWindowPos(Handle,
                    targetTopmost ? Win32.HWND_TOPMOST : Win32.HWND_NOTOPMOST,
                    0, 0, 0, 0, flags);
            var previous = Win32.GetWindow(_target, Win32.GW_HWNDPREV);
            if (previous == Handle) return;
            _ = Win32.SetWindowPos(Handle,
                previous == IntPtr.Zero ? (targetTopmost ? Win32.HWND_TOPMOST : Win32.HWND_TOP) : previous,
                0, 0, 0, 0, flags);
        }

        private void FadeTo(double target)
        {
            _opacityTarget = target;
            StartAnimation();
        }

        private void StartAnimation()
        {
            if (!_animationsEnabled)
            {
                SnapAnimations();
                return;
            }
            if (_animationTimer.Enabled) return;
            _lastTick = Environment.TickCount64;
            _animationTimer.Start();
        }

        private void SnapAnimations()
        {
            _animationTimer.Stop();
            _opacity = _opacityTarget;
            _hover = _hovered ? 1 : 0;
            _press = _pressed ? 1 : 0;
            _pinHover = _pinHovered ? 1 : 0;
            _pinPress = _pinPressed ? 1 : 0;
            _pinnedAmount = _pinned ? 1 : 0;
            if (_opacity <= 0 && Visible) Hide();
            else if (Visible) Render();
        }

        private void OnAnimationTick(object? sender, EventArgs e)
        {
            long now = Environment.TickCount64;
            double elapsed = Math.Max(1, now - _lastTick);
            _lastTick = now;

            _opacity = Approach(_opacity, _opacityTarget, elapsed,
                _opacityTarget > _opacity ? FadeInMs : FadeOutMs);
            _hover = Approach(_hover, _hovered ? 1 : 0, elapsed, HoverMs);
            _press = Approach(_press, _pressed ? 1 : 0, elapsed, PressMs);
            _pinHover = Approach(_pinHover, _pinHovered ? 1 : 0, elapsed, HoverMs);
            _pinPress = Approach(_pinPress, _pinPressed ? 1 : 0, elapsed, PressMs);
            _pinnedAmount = Approach(_pinnedAmount, _pinned ? 1 : 0, elapsed, PinStateMs);

            if (_opacity <= 0 && _opacityTarget <= 0)
            {
                _animationTimer.Stop();
                if (Visible) Hide();
                return;
            }
            if (_opacity == _opacityTarget
                && _hover == (_hovered ? 1 : 0) && _press == (_pressed ? 1 : 0)
                && _pinHover == (_pinHovered ? 1 : 0) && _pinPress == (_pinPressed ? 1 : 0)
                && _pinnedAmount == (_pinned ? 1 : 0))
            {
                _animationTimer.Stop();
            }
            Render();
        }

        private void HideImmediately()
        {
            _opacityTarget = 0;
            if (Visible) Hide();
            ResetAnimationState();
        }

        private void ResetAnimationState()
        {
            _animationTimer.Stop();
            _opacity = 0;
            _hover = _press = _pinHover = _pinPress = 0;
            _pinnedAmount = _pinned ? 1 : 0;
            _hovered = _pinHovered = _pressed = _pinPressed = false;
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            if (!Visible) ResetAnimationState();
        }

        private void Render()
        {
            if (_bounds.IsEmpty) return;
            Present(_bounds.Location, _bounds.Size, EaseOutCubic(_opacity), Draw);
        }

        private static Color SegmentColor(Palette p, double hover, double press)
            => Lerp(Lerp(p.Base, p.Hover, hover), p.Pressed, press);

        private void Draw(Graphics g)
        {
            var neutral = Neutral;
            float w = _bounds.Width;
            float h = _bounds.Height;
            // 区画の境目を画素の境界にそろえ、塗りの継ぎ目が線にならないようにする
            float pinWidth = _docked ? MathF.Round((float)(PinButtonWidth * _scale)) : 0f;
            float radius = (float)(CornerRadius * _scale);
            var outer = new RectangleF(0.5f, 0.5f, w - 1f, h - 1f);

            var mainRect = RectangleF.FromLTRB(_docked ? pinWidth : outer.Left, outer.Top, outer.Right, outer.Bottom);
            using (var mainPath = SegmentPath(mainRect, radius, roundLeft: !_docked, roundRight: true))
            using (var main = new SolidBrush(SegmentColor(neutral, _hover, _press)))
                g.FillPath(main, mainPath);
            if (_docked)
            {
                var pinColor = Lerp(SegmentColor(neutral, _pinHover, _pinPress),
                    SegmentColor(AccentPalette, _pinHover, _pinPress), _pinnedAmount);
                var pinRect = RectangleF.FromLTRB(outer.Left, outer.Top, pinWidth, outer.Bottom);
                using var pinPath = SegmentPath(pinRect, radius, roundLeft: true, roundRight: false);
                using var pinFill = new SolidBrush(pinColor);
                g.FillPath(pinFill, pinPath);
            }

            using (var outline = RoundedRect(outer, radius))
            using (var border = new Pen(neutral.Border, 1f))
            {
                g.DrawPath(border, outline);
                if (_docked)
                {
                    float inset = (float)(6 * _scale);
                    g.DrawLine(border, pinWidth, inset, pinWidth, h - inset);
                }
            }

            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAlias;
            using (var text = new SolidBrush(neutral.Text))
                g.DrawString(Text, Font, text, new RectangleF(pinWidth, 0, w - pinWidth, h), _centered);

            if (_docked) DrawPinLabel(g, pinWidth, h, Lerp(neutral.Text, AccentPalette.Text, _pinnedAmount));
        }

        /// <summary>左右どちらかの角だけを丸めた区画の形。</summary>
        private static System.Drawing.Drawing2D.GraphicsPath SegmentPath(RectangleF r, float radius,
            bool roundLeft, bool roundRight)
        {
            var path = new System.Drawing.Drawing2D.GraphicsPath();
            float d = Math.Min(radius * 2f, Math.Min(r.Width, r.Height));
            if (roundLeft && d > 0.5f)
            {
                path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
                path.AddArc(r.X, r.Y, d, d, 180, 90);
            }
            else
            {
                path.AddLine(r.X, r.Bottom, r.X, r.Y);
            }
            if (roundRight && d > 0.5f)
            {
                path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
                path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            }
            else
            {
                path.AddLine(r.Right, r.Y, r.Right, r.Bottom);
            }
            path.CloseFigure();
            return path;
        }

        /// <summary>ピンのアイコンと「固定」を区画の中央にまとめて描く。</summary>
        private void DrawPinLabel(Graphics g, float width, float height, Color color)
        {
            const string label = "固定";
            using var brush = new SolidBrush(color);
            float textWidth = g.MeasureString(label, Font, PointF.Empty, _centered).Width;
            if (_iconFont is null)
            {
                g.DrawString(label, Font, brush, new RectangleF(0, 0, width, height), _centered);
                return;
            }
            float iconWidth = g.MeasureString(PinGlyph, _iconFont, PointF.Empty, _centered).Width;
            float gap = (float)(3 * _scale);
            float left = (width - (iconWidth + gap + textWidth)) / 2f;
            g.DrawString(PinGlyph, _iconFont, brush, new RectangleF(left, 0, iconWidth, height), _centered);
            g.DrawString(label, Font, brush, new RectangleF(left + iconWidth + gap, 0, textWidth, height), _centered);
        }

        private bool PinHit(Point point) => _docked && point.X < PinButtonWidth * _scale;

        private void UpdateHover(Point location)
        {
            bool pin = PinHit(location);
            if (_pinHovered == pin && _hovered == !pin) return;
            _pinHovered = pin;
            _hovered = !pin;
            UpdateToolTip();
            StartAnimation();
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            base.OnMouseEnter(e);
            UpdateHover(PointToClient(Cursor.Position));
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            UpdateHover(e.Location);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            _hovered = _pinHovered = _pressed = _pinPressed = false;
            StartAnimation();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            if (PinHit(e.Location)) _pinPressed = true;
            else _pressed = true;
            StartAnimation();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button != MouseButtons.Left) return;
            bool dockPressed = _pressed;
            bool pinPressed = _pinPressed;
            _pressed = _pinPressed = false;
            StartAnimation();
            // ボタンの外で離した場合は押さなかったことにする
            if (_followingSuspended || !new Rectangle(Point.Empty, _bounds.Size).Contains(e.Location)) return;
            if (pinPressed && PinHit(e.Location)) _pinClick();
            else if (dockPressed && !PinHit(e.Location)) _click();
        }

        protected override void Dispose(bool disposing)
        {
            var font = disposing ? Font : null;
            if (disposing)
            {
                _animationTimer.Dispose();
                _toolTip.Dispose();
                _centered.Dispose();
                _iconFont?.Dispose();
            }
            base.Dispose(disposing);
            font?.Dispose();
        }

        private sealed record Palette(Color Base, Color Hover, Color Pressed, Color Border, Color Text);
    }
}
