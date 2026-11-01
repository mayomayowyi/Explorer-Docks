using System;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace ExplorerDocks
{
    /// <summary>フォーカスを取らない透過ウィンドウ。</summary>
    internal abstract class LayeredForm : Form
    {
        private const int WS_EX_LAYERED = 0x00080000;
        private const int WS_EX_NOACTIVATE = 0x08000000;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int WM_DPICHANGED = 0x02E0;

        private IntPtr _memDc;
        private IntPtr _dib;
        private IntPtr _oldBitmap;
        private Bitmap? _surface;
        private Size _surfaceSize;

        protected LayeredForm()
        {
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            ControlBox = false;
            MinimizeBox = false;
            MaximizeBox = false;
            AutoScaleMode = AutoScaleMode.None;
        }

        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= WS_EX_LAYERED | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW;
                return cp;
            }
        }

        protected override void WndProc(ref Message m)
        {
            // 大きさは呼び出し側が対象のDPIから決めるので、WinFormsによる自動拡縮を止める
            if (m.Msg == WM_DPICHANGED)
            {
                m.Result = IntPtr.Zero;
                return;
            }
            base.WndProc(ref m);
        }

        // 表示内容はPresentだけで決まる
        protected override void OnPaintBackground(PaintEventArgs e) { }
        protected override void OnPaint(PaintEventArgs e) { }

        /// <summary>乗算済みアルファで描画し、必要なら位置も更新する。</summary>
        protected void Present(Point? location, Size size, double opacity, Action<Graphics> draw)
        {
            if (!IsHandleCreated || size.Width <= 0 || size.Height <= 0) return;
            EnsureSurface(size);

            using (var g = Graphics.FromImage(_surface!))
            {
                var area = new Rectangle(Point.Empty, size);
                g.SetClip(area);
                g.CompositingMode = CompositingMode.SourceCopy;
                using (var clear = new SolidBrush(Color.Transparent)) g.FillRectangle(clear, area);
                g.CompositingMode = CompositingMode.SourceOver;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                draw(g);
            }

            PresentSurface(location, size, opacity);
        }

        protected void PresentSurface(Point? location, Size size, double opacity)
        {
            if (!IsHandleCreated || _surface is null || size.Width <= 0 || size.Height <= 0) return;

            var blend = new Win32.BLENDFUNCTION
            {
                BlendOp = 0, // AC_SRC_OVER
                SourceConstantAlpha = (byte)Math.Round(Math.Clamp(opacity, 0, 1) * 255),
                AlphaFormat = 1, // AC_SRC_ALPHA
            };
            var sz = new Win32.SIZE { Width = size.Width, Height = size.Height };
            var src = new Win32.POINT();
            if (location is { } p)
            {
                var dst = new Win32.POINT { X = p.X, Y = p.Y };
                _ = Win32.UpdateLayeredWindow(Handle, IntPtr.Zero, ref dst, ref sz, _memDc, ref src,
                    0, ref blend, Win32.ULW_ALPHA);
            }
            else
            {
                _ = Win32.UpdateLayeredWindow(Handle, IntPtr.Zero, IntPtr.Zero, ref sz, _memDc, ref src,
                    0, ref blend, Win32.ULW_ALPHA);
            }
        }

        /// <summary>アニメーション中の描画バッファの再確保を避ける。</summary>
        protected void ReserveSurface(Size size) => EnsureSurface(size);

        /// <summary>大きな描画バッファを使い終えたときに解放する。</summary>
        protected void ReleaseSurface()
        {
            _surface?.Dispose();
            _surface = null;
            if (_memDc != IntPtr.Zero)
            {
                if (_oldBitmap != IntPtr.Zero) Win32.SelectObject(_memDc, _oldBitmap);
                Win32.DeleteDC(_memDc);
            }
            if (_dib != IntPtr.Zero) Win32.DeleteObject(_dib);
            _memDc = _dib = _oldBitmap = IntPtr.Zero;
            _surfaceSize = Size.Empty;
        }

        private void EnsureSurface(Size size)
        {
            if (_surface is not null && size.Width <= _surfaceSize.Width && size.Height <= _surfaceSize.Height)
                return;

            var newSize = new Size(Math.Max(size.Width, _surfaceSize.Width), Math.Max(size.Height, _surfaceSize.Height));
            ReleaseSurface();

            var header = new Win32.BITMAPINFOHEADER
            {
                biSize = Marshal.SizeOf<Win32.BITMAPINFOHEADER>(),
                biWidth = newSize.Width,
                biHeight = -newSize.Height, // 上から下へ並ぶDIB
                biPlanes = 1,
                biBitCount = 32,
            };
            _memDc = Win32.CreateCompatibleDC(IntPtr.Zero);
            _dib = Win32.CreateDIBSection(_memDc, ref header, 0, out IntPtr bits, IntPtr.Zero, 0);
            if (_memDc == IntPtr.Zero || _dib == IntPtr.Zero || bits == IntPtr.Zero)
            {
                ReleaseSurface();
                throw new Win32Exception(Marshal.GetLastWin32Error(), "描画バッファを作成できませんでした。");
            }
            _oldBitmap = Win32.SelectObject(_memDc, _dib);
            _surface = new Bitmap(newSize.Width, newSize.Height, newSize.Width * 4,
                PixelFormat.Format32bppPArgb, bits);
            _surfaceSize = newSize;
        }

        protected override void Dispose(bool disposing)
        {
            ReleaseSurface();
            base.Dispose(disposing);
        }

        protected static GraphicsPath RoundedRect(RectangleF r, float radius)
        {
            var path = new GraphicsPath();
            float d = Math.Min(radius * 2f, Math.Min(r.Width, r.Height));
            if (d <= 0.5f)
            {
                path.AddRectangle(r);
                return path;
            }
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        protected static Color Lerp(Color a, Color b, double t)
        {
            t = Math.Clamp(t, 0, 1);
            return Color.FromArgb(
                (int)Math.Round(a.A + (b.A - a.A) * t),
                (int)Math.Round(a.R + (b.R - a.R) * t),
                (int)Math.Round(a.G + (b.G - a.G) * t),
                (int)Math.Round(a.B + (b.B - a.B) * t));
        }

        /// <summary>現在値を目標へ一定速度で近づける(durationMsで0→1を移動する速さ)。</summary>
        protected static double Approach(double current, double target, double elapsedMs, double durationMs)
        {
            double step = durationMs <= 0 ? 1 : elapsedMs / durationMs;
            return current < target ? Math.Min(target, current + step) : Math.Max(target, current - step);
        }

        protected static double EaseOutCubic(double t)
        {
            t = 1 - Math.Clamp(t, 0, 1);
            return 1 - t * t * t;
        }
    }

    /// <summary>ボタン・ハンドル・アニメーションで共通に使う配色。</summary>
    internal static class DockColors
    {
        public static readonly Color Accent = Color.FromArgb(81, 137, 212);
        public static readonly Color AccentHover = Color.FromArgb(102, 158, 232);
        public static readonly Color AccentPressed = Color.FromArgb(66, 118, 190);
        public static readonly Color AccentLight = Color.FromArgb(171, 210, 250);
        public static readonly Color Navy = Color.FromArgb(43, 58, 103);
    }
}
