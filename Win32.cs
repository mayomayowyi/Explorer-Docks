using System;
using System.Runtime.InteropServices;
using System.Text;

namespace ExplorerDocks
{
    internal static class Win32
    {
        public const int SW_HIDE = 0;
        public const int SW_SHOWNORMAL = 1;
        public const int SW_SHOWMINIMIZED = 2;
        public const int SW_RESTORE = 9;

        public const int VK_LBUTTON = 0x01;
        public const int VK_RBUTTON = 0x02;
        public const int VK_MBUTTON = 0x04;

        public const uint SWP_NOSIZE = 0x0001;
        public const uint SWP_NOMOVE = 0x0002;
        public const uint SWP_NOZORDER = 0x0004;
        public const uint SWP_NOACTIVATE = 0x0010;
        public const uint SWP_NOOWNERZORDER = 0x0200;
        public const uint SWP_SHOWWINDOW = 0x0040;

        public const uint GW_OWNER = 4;
        public const uint GW_HWNDPREV = 3;
        public const int GWL_EXSTYLE = -20;
        public const int WS_EX_TOPMOST = 0x00000008;

        public const int SM_CYCAPTION = 4;
        public const int SM_CYSIZEFRAME = 33;
        public const int SM_CXPADDEDBORDER = 92;

        public const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
        public const uint EVENT_SYSTEM_MOVESIZESTART = 0x000A;
        public const uint EVENT_SYSTEM_MOVESIZEEND = 0x000B;
        public const uint EVENT_SYSTEM_MINIMIZESTART = 0x0016;
        public const uint EVENT_SYSTEM_MINIMIZEEND = 0x0017;
        public const uint EVENT_OBJECT_DESTROY = 0x8001;
        public const uint EVENT_OBJECT_LOCATIONCHANGE = 0x800B;

        public const uint WINEVENT_OUTOFCONTEXT = 0x0000;
        public const uint WINEVENT_SKIPOWNPROCESS = 0x0002;

        public const int OBJID_WINDOW = 0;
        public const int GWLP_HWNDPARENT = -8;

        public static readonly IntPtr HWND_TOP = IntPtr.Zero;
        public static readonly IntPtr HWND_TOPMOST = new(-1);
        public static readonly IntPtr HWND_NOTOPMOST = new(-2);

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct WINDOWPLACEMENT
        {
            public int Length;
            public int Flags;
            public int ShowCmd;
            public POINT MinPosition;
            public POINT MaxPosition;
            public RECT NormalPosition;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct SIZE
        {
            public int Width;
            public int Height;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct BLENDFUNCTION
        {
            public byte BlendOp;
            public byte BlendFlags;
            public byte SourceConstantAlpha;
            public byte AlphaFormat;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct BITMAPINFOHEADER
        {
            public int biSize;
            public int biWidth;
            public int biHeight;
            public short biPlanes;
            public short biBitCount;
            public int biCompression;
            public int biSizeImage;
            public int biXPelsPerMeter;
            public int biYPelsPerMeter;
            public int biClrUsed;
            public int biClrImportant;
        }

        public const uint ULW_ALPHA = 0x00000002;

        public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        public delegate void WinEventDelegate(
            IntPtr hook, uint eventType, IntPtr hWnd, int idObject, int idChild, uint threadId, uint eventTime);

        [DllImport("user32.dll")]
        public static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetClassName(IntPtr hWnd, StringBuilder buffer, int count);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetWindowText(IntPtr hWnd, StringBuilder buffer, int count);

        [DllImport("user32.dll")]
        public static extern bool IsWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool IsIconic(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool IsZoomed(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        [DllImport("user32.dll")]
        public static extern IntPtr GetWindow(IntPtr hWnd, uint cmd);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
        public static extern int GetWindowLong(IntPtr hWnd, int index);

        [DllImport("user32.dll")]
        public static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr FindWindow(string className, string? windowName);

        [DllImport("user32.dll")]
        public static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

        [DllImport("user32.dll")]
        public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

        [DllImport("user32.dll")]
        public static extern bool GetWindowPlacement(IntPtr hWnd, ref WINDOWPLACEMENT placement);

        [DllImport("user32.dll")]
        public static extern bool SetWindowPlacement(IntPtr hWnd, ref WINDOWPLACEMENT placement);

        [DllImport("user32.dll")]
        public static extern bool ShowWindow(IntPtr hWnd, int cmdShow);

        [DllImport("user32.dll")]
        public static extern short GetAsyncKeyState(int key);

        [DllImport("user32.dll")]
        public static extern bool GetCursorPos(out POINT point);

        [DllImport("user32.dll")]
        public static extern uint GetDpiForWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern int GetSystemMetricsForDpi(int index, uint dpi);

        [DllImport("user32.dll")]
        public static extern IntPtr MonitorFromPoint(POINT point, uint flags);

        [DllImport("shcore.dll")]
        public static extern int GetDpiForMonitor(IntPtr monitor, int dpiType, out uint dpiX, out uint dpiY);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool UpdateLayeredWindow(IntPtr hWnd, IntPtr hdcDst, ref POINT pptDst,
            ref SIZE psize, IntPtr hdcSrc, ref POINT pptSrc, int crKey, ref BLENDFUNCTION pblend, uint flags);

        /// <summary>位置を変えずに内容だけ更新する版(pptDst = NULL)。</summary>
        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool UpdateLayeredWindow(IntPtr hWnd, IntPtr hdcDst, IntPtr pptDst,
            ref SIZE psize, IntPtr hdcSrc, ref POINT pptSrc, int crKey, ref BLENDFUNCTION pblend, uint flags);

        [DllImport("gdi32.dll", SetLastError = true)]
        public static extern IntPtr CreateCompatibleDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        public static extern bool DeleteDC(IntPtr hdc);

        [DllImport("gdi32.dll")]
        public static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);

        [DllImport("gdi32.dll")]
        public static extern bool DeleteObject(IntPtr obj);

        [DllImport("gdi32.dll", SetLastError = true)]
        public static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFOHEADER bmi, uint usage,
            out IntPtr bits, IntPtr section, uint offset);

        /// <summary>次の画面合成(垂直同期)まで待つ。</summary>
        [DllImport("dwmapi.dll")]
        public static extern int DwmFlush();

        [DllImport("user32.dll")]
        public static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr module,
            WinEventDelegate callback, uint processId, uint threadId, uint flags);

        [DllImport("user32.dll")]
        public static extern bool UnhookWinEvent(IntPtr hook);

        [DllImport("user32.dll")]
        public static extern bool DestroyIcon(IntPtr hIcon);

        /// <summary>右クリックから起動された側が、常駐中のアプリに前面表示を許可する。</summary>
        [DllImport("user32.dll")]
        public static extern bool AllowSetForegroundWindow(int processId);

        [DllImport("user32.dll")]
        public static extern bool SetForegroundWindow(IntPtr hWnd);

        /// <summary>影や透明なリサイズ枠を除いた、見た目どおりのウィンドウ矩形。</summary>
        public const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;

        [DllImport("dwmapi.dll")]
        public static extern int DwmGetWindowAttribute(IntPtr hWnd, int attribute, out RECT value, int size);

        public const uint SPI_GETWINARRANGING = 0x0082;
        public const uint SPI_SETWINARRANGING = 0x0083;
        public const uint SPIF_SENDCHANGE = 0x0002;

        [DllImport("user32.dll")]
        public static extern bool SystemParametersInfo(uint action, uint param, out int value, uint winIni);

        [DllImport("user32.dll")]
        public static extern bool SystemParametersInfo(uint action, uint param, IntPtr value, uint winIni);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
        private static extern IntPtr SetWindowLongPtrW(IntPtr hWnd, int nIndex, IntPtr value);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
        private static extern int SetWindowLongW(IntPtr hWnd, int nIndex, int value);

        /// <summary>トップレベルウィンドウの所有者を設定する(64bit/32bit両対応)。</summary>
        public static void SetOwner(IntPtr hWnd, IntPtr owner)
        {
            if (IntPtr.Size == 8)
            {
                _ = SetWindowLongPtrW(hWnd, GWLP_HWNDPARENT, owner);
            }
            else
            {
                _ = SetWindowLongW(hWnd, GWLP_HWNDPARENT, owner.ToInt32());
            }
        }

        public static string GetClassNameOf(IntPtr hWnd)
        {
            var sb = new StringBuilder(64);
            return GetClassName(hWnd, sb, sb.Capacity) > 0 ? sb.ToString() : "";
        }

        public static string GetTitleOf(IntPtr hWnd, int maxLength = 256)
        {
            var sb = new StringBuilder(maxLength);
            _ = GetWindowText(hWnd, sb, maxLength);
            return sb.ToString();
        }

        public static uint GetProcessIdOf(IntPtr hWnd)
        {
            GetWindowThreadProcessId(hWnd, out uint pid);
            return pid;
        }

        public static bool IsExplorerWindow(IntPtr hWnd)
            => IsWindow(hWnd)
               && string.Equals(GetClassNameOf(hWnd), "CabinetWClass", StringComparison.Ordinal);

        public static bool IsMouseButtonPressed()
            => (GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0
               || (GetAsyncKeyState(VK_RBUTTON) & 0x8000) != 0
               || (GetAsyncKeyState(VK_MBUTTON) & 0x8000) != 0;
    }
}
