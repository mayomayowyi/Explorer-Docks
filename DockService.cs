using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Forms;

namespace ExplorerDocks
{
    internal enum DockPhase
    {
        None,
        Collapsed,
        Expanded,
    }

    internal enum DockEdge
    {
        Left,
        Top,
        Right,
        Bottom,
    }

    internal sealed class DockService : IDisposable
    {
        private const int HandleThickness = 8;
        private const int HandleLength = 72;
        private const int HandleGap = 2;
        private const int ExpandedHandleMargin = 4;
        private const int LabeledHandleThickness = 20;
        private const int LabeledHandleLength = 112;
        private const int IdleIntervalMs = 80;
        private const int DefaultCollapseDelayMs = 280;
        private const int MinExpandedMs = 450;
        private const int EdgeHoverDelayMs = 85;
        private const int CollapseAnimationMs = 220;
        private const int ExpandAnimationMs = 170;
        private const string MenuWindowClass = "#32768";

        private readonly DockHandleForm _handle = new();
        private readonly ContextMenuStrip _handleMenu = new();
        private readonly System.Windows.Forms.Timer _timer = new();
        private readonly Stopwatch _animationClock = new();
        private CancellationTokenSource? _animationCancellation;
        private readonly DockTransitionForm _transition = new();
        private string _statePath;
        private string _positionPath;
        private readonly string _sharedPositionPath;
        private string? _individualPositionPath;
        private bool _independentPosition;
        private bool _persistSharedPosition;
        private DockPositionFile? _lastDockPosition;
        private bool _animating;
        private DockPhase _animationTarget;
        private Rectangle _animationStart;
        private Rectangle _animationEnd;
        private int _animationDurationMs;
        private bool _animationsEnabled = true;

        private IntPtr _explorer;
        private DockPhase _phase = DockPhase.None;
        private Rectangle _workRect;
        private Rectangle _handleRect;
        private double _scale = 1.0;
        private DockEdge _dockEdge = DockEdge.Right;
        private int _dockLeft;
        private int _dockWidth;
        private int _dockHeight;
        private int _dockTop;
        private int? _handleCenter;
        private long _expandedTick;
        private long _outsideTick = -1;
        private long _edgeHoverSince = -1;
        private bool _suppressEdgeHoverUntilExit;
        private bool _fullscreenSuppressed;
        private bool _showLabels = true;
        private bool _pinned;
        private bool _collapseRequestedAfterUnpin;
        private bool _collapseRequestedAfterWorkDrop;
        private bool _collapseRequestedByHandle;
        private bool _expandRequestedByHandle;
        private string _folderNameFromPath = "";
        private long _lastFolderNameCheck;
        private string? _groupLabel;
        private bool _handleEnabled = true;
        private Rectangle? _workBounds;
        private int _maxHandleSpan = int.MaxValue;
        private long _lastTitleCheck;
        private int _collapseDelayMs = DefaultCollapseDelayMs;

        private bool _hasSavedState;
        private Win32.WINDOWPLACEMENT _savedPlacement;
        private bool _savedWasTopmost;

        public DockService(bool useSavedPosition = true)
        {
            _statePath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ExplorerDocks",
                "state.json");
            _sharedPositionPath = Path.Combine(Path.GetDirectoryName(_statePath)!, "dock-position.json");
            _positionPath = _sharedPositionPath;
            _persistSharedPosition = useSavedPosition;
            _lastDockPosition = useSavedPosition ? ReadDockPosition() : null;
            _timer.Tick += OnTick;
            // ウィンドウ移動中は展開せず、ファイルなどのドラッグ中だけ許可する。
            _handle.Hover += () =>
            {
                if (DockHandleForm.IsDataDragActive || !Win32.IsMouseButtonPressed()) StartExpansion();
            };
            _handle.ToggleClicked += () =>
            {
                if (HandleToggleRequested is { } handler) handler(this);
                else ToggleExpansion();
            };
            _handle.DisplayChanged += OnDisplayChanged;
            _handle.RepositionRequested += point =>
            {
                HandleDragMoved?.Invoke(this, point);
                MoveHandleTo(point);
            };
            _handle.RepositionFinished += () =>
            {
                HandleDragFinished?.Invoke(this, Cursor.Position);
                SaveDockPosition();
                HandlePositionChanged?.Invoke(this);
            };

            ManagementUi.ConfigureMenu(_handleMenu);
            var pinItem = new ToolStripMenuItem("ピン留め") { CheckOnClick = true };
            pinItem.CheckedChanged += (_, _) => Pinned = pinItem.Checked;
            _handleMenu.Opening += (_, _) => pinItem.Checked = Pinned;
            _handleMenu.Items.Add(pinItem);
            _handleMenu.Items.Add("名前を変更...", null, (_, _) => RenameRequested?.Invoke(this));
            var destinations = new ToolStripMenuItem("収納先モニター");
            _handleMenu.Items.Add(destinations);
            _handleMenu.Opening += (_, _) =>
            {
                foreach (ToolStripItem oldItem in destinations.DropDownItems.Cast<ToolStripItem>().ToArray())
                    oldItem.Dispose();
                destinations.DropDownItems.Clear();
                var screens = Screen.AllScreens;
                for (int i = 0; i < screens.Length; i++)
                {
                    var targetScreen = screens[i];
                    var screenMenu = new ToolStripMenuItem(
                        $"モニター {i + 1} ({targetScreen.Bounds.Width}×{targetScreen.Bounds.Height})");
                    foreach (var (label, edge) in new[]
                    {
                        ("左", DockEdge.Left), ("右", DockEdge.Right),
                        ("上", DockEdge.Top), ("下", DockEdge.Bottom),
                    })
                    {
                        var destinationEdge = edge;
                        var item = new ToolStripMenuItem(label)
                        {
                            Checked = targetScreen.WorkingArea == _workRect && _dockEdge == edge,
                        };
                        item.Click += (_, _) =>
                        {
                            var destination = targetScreen.WorkingArea;
                            int center = MapHandleCenter(_workRect, _dockEdge, HandleCenter,
                                destination, destinationEdge);
                            DockTargetRequested?.Invoke(this, destination, destinationEdge, center);
                        };
                        screenMenu.DropDownItems.Add(item);
                    }
                    destinations.DropDownItems.Add(screenMenu);
                }
            };
            var addWork = new ToolStripMenuItem("Workに追加");
            addWork.DropDownItems.Add("既存のWorkへ...", null, (_, _) => WorkRequested?.Invoke(this));
            addWork.DropDownItems.Add("新規作成...", null, (_, _) => WorkCreateRequested?.Invoke(this));
            _handleMenu.Items.Add(addWork);
            var leaveWork = new ToolStripMenuItem("Workを解除", null,
                (_, _) => WorkDissolveRequested?.Invoke(this));
            _handleMenu.Items.Add(leaveWork);
            _handleMenu.Opening += (_, _) => leaveWork.Visible = IsWork;
            var arrange = new ToolStripMenuItem("Workの並べ方");
            var rowItem = new ToolStripMenuItem("横に並べる");
            var columnItem = new ToolStripMenuItem("縦に並べる");
            rowItem.Click += (_, _) => WorkArrangementRequested?.Invoke(this, false);
            columnItem.Click += (_, _) => WorkArrangementRequested?.Invoke(this, true);
            arrange.DropDownItems.AddRange(new ToolStripItem[] { rowItem, columnItem });
            _handleMenu.Items.Add(arrange);
            var syncSize = new ToolStripMenuItem("ウィンドウサイズを同期", null,
                (_, _) => WorkSizeSyncRequested?.Invoke(this));
            _handleMenu.Items.Add(syncSize);
            _handleMenu.Opening += (_, _) =>
            {
                arrange.Visible = IsWork;
                syncSize.Visible = IsWork;
                syncSize.Checked = WorkSizeSyncEnabled;
                rowItem.Checked = !WorkStacked;
                columnItem.Checked = WorkStacked;
            };
            _handleMenu.Items.Add(new ToolStripSeparator());
            _handleMenu.Items.Add("すべてのDockを解除", null, (_, _) =>
            {
                if (_handle.IsHandleCreated)
                    _handle.BeginInvoke((Action)(() => UndockAllRequested?.Invoke(this)));
            });
            // 画面端の内側へメニューを開く
            _handle.MouseUp += (_, e) =>
            {
                if (e.Button != MouseButtons.Right || !_handle.Visible) return;
                var direction = _dockEdge switch
                {
                    DockEdge.Left => ToolStripDropDownDirection.Right,
                    DockEdge.Top => ToolStripDropDownDirection.BelowLeft,
                    DockEdge.Bottom => ToolStripDropDownDirection.AboveLeft,
                    _ => ToolStripDropDownDirection.Left,
                };
                _handleMenu.Show(_handle,
                    new Point(_handle.Width / 2, _handle.Height / 2),
                    direction);
            };
        }

        public bool IsDocked => _phase != DockPhase.None;

        public void ShowContextMenu(Point location) => _handleMenu.Show(location);
        public IntPtr Window => _explorer;
        public bool Pinned
        {
            get => _pinned;
            set
            {
                if (_pinned == value) return;
                _pinned = value;
                if (value) _collapseRequestedAfterUnpin = false;
                _outsideTick = -1;
                PinnedChanged?.Invoke(this);
            }
        }
        public bool HandleEnabled
        {
            get => _handleEnabled;
            set
            {
                if (_handleEnabled == value) return;
                _handleEnabled = value;
                if (!IsDocked) return;
                if (value && !_fullscreenSuppressed) _ = ShowHandle(false);
                else _handle.Visible = false;
            }
        }
        // Workの各ウィンドウは個別の矩形を使い、互いに重ならないようにする。
        public Size PreferredDockSize => new(_dockWidth, _dockHeight);

        /// <summary>Work内で同期した大きさを、このウィンドウの保存サイズに反映する。</summary>
        public void SetPreferredDockSize(Size size)
        {
            _dockWidth = Math.Max(1, size.Width);
            _dockHeight = Math.Max(1, size.Height);
        }

        /// <summary>Workを縦一列に並べているか(右クリックメニューの表示用)。</summary>
        public bool WorkStacked { get; set; }
        public bool WorkSizeSyncEnabled { get; set; }
        public event Action<DockService, bool>? WorkArrangementRequested;
        public event Action<DockService>? WorkSizeSyncRequested;
        public bool ResizingWorkWindow => _resizingWorkWindow;
        public bool MovingByTitleBar => _titleBarDrag;
        private bool _resizingWorkWindow;
        public event Action<DockService>? WorkSizeChanged;
        public event Action<DockService>? WorkResizeCompleted;

        // WinEventの開始通知が届く環境ではそのまま使い、届かない場合はOnTickでも検出する。
        public void BeginWorkResize()
        {
            _resizingWorkWindow = true;
            _timer.Interval = 35;
        }

        public bool CaptureWorkResize()
        {
            if (_titleBarDrag || !_resizingWorkWindow || _workBounds is null || !IsExpanded ||
                !Win32.GetWindowRect(_explorer, out var rect)) return false;
            int width = Math.Max(1, rect.Right - rect.Left);
            int height = Math.Max(1, rect.Bottom - rect.Top);
            if (width == _dockWidth && height == _dockHeight) return false;
            _dockWidth = width;
            _dockHeight = height;
            return true;
        }

        public void EndWorkResize()
        {
            _resizingWorkWindow = false;
            _timer.Interval = IdleIntervalMs;
        }

        private void PollWorkResize()
        {
            if (_titleBarDrag || _workBounds is not Rectangle layout || !IsExpanded || _animating) return;

            bool dragging = (Win32.GetAsyncKeyState(Win32.VK_LBUTTON) & 0x8000) != 0;
            if (!dragging)
            {
                if (!_resizingWorkWindow) return;
                if (WorkResizeCompleted is { } completed) completed(this);
                else EndWorkResize();
                return;
            }

            if (_resizingWorkWindow)
            {
                WorkSizeChanged?.Invoke(this);
                return;
            }

            if (Win32.GetForegroundWindow() != _explorer ||
                !Win32.GetWindowRect(_explorer, out var rect)) return;

            // WinEventの開始通知が欠けた場合も、実際の枠のドラッグだけを検出する。
            int width = rect.Right - rect.Left;
            int height = rect.Bottom - rect.Top;
            if (width == layout.Width && height == layout.Height) return;
            var cursor = Cursor.Position;
            int margin = Math.Max(16, (int)Math.Round(20 * _scale));
            bool nearFrame = cursor.X >= rect.Left - margin && cursor.X <= rect.Right + margin &&
                cursor.Y >= rect.Top - margin && cursor.Y <= rect.Bottom + margin &&
                (Math.Abs(cursor.X - rect.Left) <= margin ||
                 Math.Abs(cursor.X - rect.Right) <= margin ||
                 Math.Abs(cursor.Y - rect.Top) <= margin ||
                 Math.Abs(cursor.Y - rect.Bottom) <= margin);
            if (!nearFrame) return;
            BeginWorkResize();
            WorkSizeChanged?.Invoke(this);
        }

        public void SetWorkBounds(Rectangle? bounds, bool apply = true)
        {
            if (_workBounds == bounds)
            {
                if (bounds is not Rectangle expected || !apply || !IsExpanded
                    || _animating || _resizingWorkWindow) return;
                if (Win32.GetWindowRect(_explorer, out var current)
                    && Rectangle.FromLTRB(current.Left, current.Top, current.Right, current.Bottom) == expected)
                    return;
            }
            _workBounds = bounds;
            if (_animating && _animationTarget == DockPhase.Expanded)
                _animationEnd = ExpandedDockBounds();
            if (apply && IsExpanded && !_animating && !_resizingWorkWindow)
                _ = ApplyDockGeometry();
        }
        public Rectangle HandleBounds => _handleRect;
        public void SetDropTarget(bool highlighted) => _handle.DropTarget = highlighted;
        public bool IsWork { get; set; }
        public void SetHandleLabel(string? text)
        {
            _groupLabel = text;
            RefreshWindowTitle();
        }
        public bool IsExpanded => _phase == DockPhase.Expanded;
        public bool IsTransitioningToExpanded => _animating && _animationTarget == DockPhase.Expanded;
        public bool IsTransitioningToCollapsed => _animating && _animationTarget == DockPhase.Collapsed;
        public Rectangle DockWorkArea => _workRect;
        public DockEdge Edge => _dockEdge;
        public int HandleCenter => _dockEdge is DockEdge.Left or DockEdge.Right
            ? _handleRect.Top + _handleRect.Height / 2 : _handleRect.Left + _handleRect.Width / 2;
        public int HandleSpan => _dockEdge is DockEdge.Left or DockEdge.Right
            ? _handleRect.Height : _handleRect.Width;
        public int LabeledHandleSpan => (int)Math.Round(LabeledHandleLength * _scale);

        /// <summary>整列時の共通保存先と、個別配置時のウィンドウ別保存先を切り替える。</summary>
        public bool UseIndependentPosition
        {
            get => _independentPosition;
            set
            {
                if (_independentPosition == value) return;
                _independentPosition = value;
                if (!IsDocked) return;
                _positionPath = value ? _individualPositionPath ?? _sharedPositionPath : _sharedPositionPath;
                if (value && ReadDockPosition() is { } saved)
                    ApplySavedDockPosition(saved);
            }
        }

        public bool PersistSharedPosition
        {
            set => _persistSharedPosition = value;
        }

        public void SaveCurrentDockPosition() => SaveDockPosition();

        internal static double PositionFraction(Rectangle work, DockEdge edge, int center)
        {
            int span = edge is DockEdge.Left or DockEdge.Right ? work.Height : work.Width;
            int start = edge is DockEdge.Left or DockEdge.Right ? work.Top : work.Left;
            return Math.Clamp((center - start) / (double)Math.Max(1, span), 0, 1);
        }

        internal static int MapHandleCenter(Rectangle from, DockEdge fromEdge, int fromCenter,
            Rectangle to, DockEdge toEdge)
        {
            double fraction = PositionFraction(from, fromEdge, fromCenter);
            return toEdge is DockEdge.Left or DockEdge.Right
                ? to.Top + (int)Math.Round(to.Height * fraction)
                : to.Left + (int)Math.Round(to.Width * fraction);
        }

        public (string Screen, DockEdge Edge, double Position) PositionSnapshot()
        {
            return (Screen.FromRectangle(_workRect).DeviceName, _dockEdge,
                PositionFraction(_workRect, _dockEdge, HandleCenter));
        }

        public void RestorePosition(string screenName, DockEdge edge, double position)
        {
            if (!IsDocked || !Enum.IsDefined(edge) || !double.IsFinite(position)) return;
            var screen = Array.Find(Screen.AllScreens,
                s => string.Equals(s.DeviceName, screenName, StringComparison.OrdinalIgnoreCase));
            if (screen is null) return;
            var work = screen.WorkingArea;
            int center = edge is DockEdge.Left or DockEdge.Right
                ? work.Top + (int)Math.Round(Math.Clamp(position, 0, 1) * work.Height)
                : work.Left + (int)Math.Round(Math.Clamp(position, 0, 1) * work.Width);
            SetDockTarget(work, edge, center);
        }

        public bool ShowLabels
        {
            get => _showLabels;
            set
            {
                if (_showLabels == value) return;
                _showLabels = value;
                _handle.ShowLabel = value;
                if (!IsDocked) return;
                UpdateHandleRect();
                if (_animating) _animationEnd = _animationTarget == DockPhase.Collapsed
                    ? _handleRect : ExpandedDockBounds();
                if (_phase == DockPhase.Expanded) _ = ApplyDockGeometry();
                if (!_fullscreenSuppressed) _ = ShowHandle(false);
            }
        }

        public event Action<DockService>? Expanded;
        public event Action<DockService>? Expanding;
        public event Action<DockService>? Collapsing;
        public Func<Point, bool>? GroupCursorInside { get; set; }
        public event Action<DockService>? PinnedChanged;
        public event Action<DockService>? HandleToggleRequested;
        public event Action<DockService>? WorkRequested;
        public event Action<DockService>? WorkCreateRequested;
        public event Action<DockService>? RenameRequested;
        public event Action<DockService>? UndockAllRequested;
        public event Action<DockService>? HandlePositionChanged;
        public event Action<DockService, Rectangle, DockEdge, int>? DockTargetRequested;
        public event Action<DockService, Point>? HandleDragMoved;
        public event Action<DockService, Point>? HandleDragFinished;
        public event Action<DockService>? WorkDissolveRequested;
        public event Action<DockService>? LayoutChanged;
        /// <summary>Work内のウィンドウがタイトルバーで動かされた(大きさは変わっていない)。</summary>
        public event Action<DockService, Rectangle>? WorkWindowMoved;
        public Rectangle? WorkBounds => _workBounds;

        /// <summary>Workの配置位置へウィンドウを戻す。</summary>
        public void ReapplyWorkBounds()
        {
            if (IsExpanded && !_animating && _workBounds.HasValue) _ = ApplyDockGeometry();
        }

        public bool AnimationsEnabled
        {
            get => _animationsEnabled;
            set
            {
                if (_animationsEnabled == value) return;
                _animationsEnabled = value;
                _handle.AnimationsEnabled = value;
                if (!value && _animating) FinishTransition();
            }
        }

        /// <summary>展開中にマウスが離れてから収納するまでの時間。-1は手動収納。</summary>
        public int CollapseDelayMs
        {
            get => _collapseDelayMs;
            set
            {
                _collapseDelayMs = Math.Clamp(value, -1, 10000);
                _outsideTick = -1;
            }
        }

        public event Action<string>? StatusChanged;
        public event Action<IntPtr, bool>? DockVisibilityChanged;

        public static List<IntPtr> FindExplorers()
        {
            var list = new List<IntPtr>();
            Win32.EnumWindows((hWnd, _) =>
            {
                if (Win32.IsWindowVisible(hWnd) && Win32.IsExplorerWindow(hWnd))
                {
                    list.Add(hWnd);
                }
                return true;
            }, IntPtr.Zero);
            return list;
        }

        public void Dock(IntPtr hWnd)
        {
            if (IsDocked) throw new InvalidOperationException("すでにDock化されています。先に解除してください。");
            if (!Win32.IsExplorerWindow(hWnd)) throw new InvalidOperationException("対象はエクスプローラーのウィンドウではありません。");

            SavePlacement(hWnd);
            // 同じタイトルのフォルダーは前回の個別配置を再利用する。
            // 整列モードの共通位置ファイルとは完全に分離する。
            var title = Win32.GetTitleOf(hWnd).Trim();
            if (string.IsNullOrEmpty(title)) title = "Explorer";
            var key = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(title)))[..20];
            _individualPositionPath = Path.Combine(Path.GetDirectoryName(_sharedPositionPath)!,
                $"dock-position-{key}.json");
            if (_independentPosition)
            {
                _positionPath = _individualPositionPath;
                _lastDockPosition = ReadDockPosition();
            }
            ComputeDockGeometry(hWnd);

            if (!ShowHandle(false))
            {
                _handle.Visible = false;
                throw new InvalidOperationException("Dockハンドルを表示できませんでした。");
            }

            _explorer = hWnd;
            _statePath = Path.Combine(Path.GetDirectoryName(_statePath)!, $"state-{hWnd.ToInt64():x}.json");
            _handle.ShowLabel = _showLabels;
            RefreshWindowTitle();
            _phase = DockPhase.Collapsed;
            _collapseRequestedAfterWorkDrop = false;
            _handle.Expanded = false;
            _outsideTick = -1;
            _edgeHoverSince = -1;
            _suppressEdgeHoverUntilExit = false;
            _fullscreenSuppressed = false;
            _timer.Interval = IdleIntervalMs;
            _timer.Start();
            SaveDockPosition();
            WriteStateFile();
            // 収納開始時点で解除ボタンを消す。Explorerの非表示イベントを待たない。
            var sourceRect = GetDockRect(hWnd);
            DockVisibilityChanged?.Invoke(hWnd, false);
            _ = Win32.ShowWindow(hWnd, Win32.SW_HIDE);
            BeginTransition(sourceRect, _handleRect, DockPhase.Collapsed);
            StatusChanged?.Invoke("Dock化しました。画面端のハンドルにマウスを合わせると展開します。");
        }

        public void Undock()
        {
            if (_animating)
            {
                StopTransition();
                if (Win32.IsWindow(_explorer))
                {
                    _ = Win32.ShowWindow(_explorer, Win32.SW_RESTORE);
                }
            }
            _timer.Stop();
            _phase = DockPhase.None;
            var hWnd = _explorer;
            _explorer = IntPtr.Zero;
            _handle.Visible = false;
            _handle.Expanded = false;
            _collapseRequestedAfterUnpin = false;
            _collapseRequestedAfterWorkDrop = false;
            _fullscreenSuppressed = false;
            _edgeHoverSince = -1;
            _suppressEdgeHoverUntilExit = false;
            RemoveTemporaryTopmost(hWnd);

            if (_hasSavedState && hWnd != IntPtr.Zero && Win32.IsWindow(hWnd))
            {
                RestoreSavedPlacement(hWnd);
            }
            _hasSavedState = false;
            _savedWasTopmost = false;
            DeleteStateFile();
            StatusChanged?.Invoke("Dock解除しました。");
        }

        /// <summary>前回異常終了時に残ったDock状態を復元する(起動時に一度だけ呼ぶ)。</summary>
        public static void RestoreLeftoverWindows()
        {
            var directory = Path.Combine(Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData), "ExplorerDocks");
            if (!Directory.Exists(directory)) return;
            // 旧バージョンの単一Dock復元ファイルも扱う。
            var files = new List<string> { Path.Combine(directory, "state.json") };
            try { files.AddRange(Directory.EnumerateFiles(directory, "state-*.json")); }
            catch { /* 起動は継続 */ }
            foreach (var file in files) RestoreLeftover(file);
        }

        private static void RestoreLeftover(string statePath)
        {
            try
            {
                if (!File.Exists(statePath)) return;
                var state = JsonSerializer.Deserialize<SavedStateFile>(File.ReadAllBytes(statePath));
                if (state is null) { File.Delete(statePath); return; }

                var hWnd = new IntPtr(state.Hwnd);
                bool restored = false;
                if (SavedWindowMatches(hWnd, state))
                {
                    restored = ApplyPlacement(hWnd, state);
                }
                if (!restored)
                {
                    var candidates = new List<IntPtr>();
                    Win32.EnumWindows((h, _) =>
                    {
                        if (Win32.IsExplorerWindow(h) && Win32.GetProcessIdOf(h) == state.Pid)
                        {
                            candidates.Add(h);
                        }
                        return true;
                    }, IntPtr.Zero);
                    if (candidates.Count == 1)
                    {
                        restored = ApplyPlacement(candidates[0], state);
                    }
                }
                File.Delete(statePath);
            }
            catch
            {
                // 復元に失敗してもアプリの起動は続行する
            }
        }

        public void Dispose()
        {
            if (IsDocked)
            {
                try { Undock(); } catch { /* 終了時の後始末は失敗を無視 */ }
            }
            StopTransition();
            _transition.Dispose();
            _timer.Dispose();
            _handleMenu.Dispose();
            _handle.Dispose();
        }

        private void ComputeDockGeometry(IntPtr hWnd)
        {
            _handleCenter = null;
            var rect = GetDockRect(hWnd);
            var screen = Screen.FromRectangle(rect);
            _workRect = screen.WorkingArea;
            _scale = GetScale(hWnd);

            _dockEdge = ChooseNearestEdge(rect, _workRect, _dockEdge);
            _dockLeft = rect.Left;
            _dockTop = rect.Top;
            _dockWidth = rect.Width;
            _dockHeight = rect.Height;

            if (_lastDockPosition is { } previous) ApplySavedDockPosition(previous, updateWindow: false);
            ClampDockGeometry();
            UpdateHandleRect();
        }

        private void ApplySavedDockPosition(DockPositionFile previous, bool updateWindow = true)
        {
            var preferredScreen = Array.Find(Screen.AllScreens,
                screen => string.Equals(screen.DeviceName, previous.Screen, StringComparison.OrdinalIgnoreCase));
            var work = preferredScreen?.WorkingArea ?? _workRect;
            int center = previous.Edge is DockEdge.Left or DockEdge.Right
                ? work.Top + (int)Math.Round(work.Height * previous.Position)
                : work.Left + (int)Math.Round(work.Width * previous.Position);
            if (updateWindow) SetDockTarget(work, previous.Edge, center);
            else
            {
                _workRect = work;
                _scale = GetScaleForScreen(Screen.FromRectangle(work), _explorer != IntPtr.Zero ? _explorer : IntPtr.Zero);
                _dockEdge = previous.Edge;
                _handleCenter = center;
                if (_dockEdge is DockEdge.Left or DockEdge.Right)
                    _dockTop = center - _dockHeight / 2;
                else
                    _dockLeft = center - _dockWidth / 2;
            }
        }

        private static double GetScaleForScreen(Screen screen, IntPtr fallbackHwnd)
        {
            // DPIの異なる別モニターに前回の収納先がある場合も、ハンドルサイズを合わせる。
            IntPtr monitor = Win32.MonitorFromPoint(
                new Win32.POINT { X = screen.Bounds.Left + screen.Bounds.Width / 2,
                    Y = screen.Bounds.Top + screen.Bounds.Height / 2 }, 2);
            if (monitor != IntPtr.Zero
                && Win32.GetDpiForMonitor(monitor, 0, out uint dpiX, out _) == 0
                && dpiX >= 96)
                return dpiX / 96.0;
            return GetScale(fallbackHwnd);
        }

        private Rectangle GetDockRect(IntPtr hWnd)
        {
            // 通常時は今の矩形、最大化・最小化時は復元時の矩形を使う
            if (!Win32.IsIconic(hWnd) && !Win32.IsZoomed(hWnd)
                && Win32.GetWindowRect(hWnd, out var current))
            {
                return Rectangle.FromLTRB(current.Left, current.Top, current.Right, current.Bottom);
            }

            var placement = new Win32.WINDOWPLACEMENT { Length = Marshal.SizeOf<Win32.WINDOWPLACEMENT>() };
            _ = Win32.GetWindowPlacement(hWnd, ref placement);
            var normal = placement.NormalPosition;
            var workspace = SystemInformation.WorkingArea;
            return Rectangle.FromLTRB(
                normal.Left + workspace.Left,
                normal.Top + workspace.Top,
                normal.Right + workspace.Left,
                normal.Bottom + workspace.Top);
        }

        private static DockEdge ChooseNearestEdge(Rectangle rect, Rectangle work, DockEdge current)
        {
            int left = Math.Abs(rect.Left - work.Left);
            int right = Math.Abs(rect.Right - work.Right);
            int top = Math.Abs(rect.Top - work.Top);
            int bottom = Math.Abs(rect.Bottom - work.Bottom);

            int min = Math.Min(Math.Min(left, right), Math.Min(top, bottom));
            int currentDistance = current switch
            {
                DockEdge.Left => left,
                DockEdge.Top => top,
                DockEdge.Bottom => bottom,
                _ => right,
            };

            // 角に近い位置では今の辺を優先し、勝手に辺が入れ替わらないようにする
            if (currentDistance == min) return current;
            if (left == min) return DockEdge.Left;
            if (right == min) return DockEdge.Right;
            if (top == min) return DockEdge.Top;
            return DockEdge.Bottom;
        }

        private static double GetScale(IntPtr hWnd)
        {
            uint dpi = Win32.GetDpiForWindow(hWnd);
            return dpi >= 96 ? dpi / 96.0 : 1.0;
        }

        private void ClampDockGeometry()
        {
            _dockWidth = Math.Min(Math.Max(1, _dockWidth), Math.Max(1, _workRect.Width));
            _dockHeight = Math.Min(Math.Max(1, _dockHeight), Math.Max(1, _workRect.Height));
            int maxLeft = Math.Max(_workRect.Left, _workRect.Right - _dockWidth);
            int maxTop = Math.Max(_workRect.Top, _workRect.Bottom - _dockHeight);
            _dockLeft = Math.Clamp(_dockLeft, _workRect.Left, maxLeft);
            _dockTop = Math.Clamp(_dockTop, _workRect.Top, maxTop);
        }

        private void UpdateHandleRect()
        {
            int thickness = Math.Max(4, (int)Math.Round((_showLabels ? LabeledHandleThickness : HandleThickness) * _scale));
            int gap = Math.Max(0, (int)Math.Round(HandleGap * _scale));
            int limit = _dockEdge is DockEdge.Left or DockEdge.Right
                ? _workRect.Height
                : _workRect.Width;
            int minLength = Math.Min(24, Math.Max(1, limit));
            int length = Math.Clamp(Math.Min(_maxHandleSpan,
                (int)Math.Round((_showLabels ? LabeledHandleLength : HandleLength) * _scale)),
                minLength, Math.Max(1, limit));
            int centerX = _handleCenter ?? (_dockLeft + _dockWidth / 2);
            int centerY = _handleCenter ?? (_dockTop + _dockHeight / 2);

            _handle.Edge = _dockEdge;
            _handleRect = _dockEdge switch
            {
                DockEdge.Left => new Rectangle(
                    _workRect.Left + gap,
                    CenterStart(centerY, length, _workRect.Top, _workRect.Bottom),
                    thickness, length),
                DockEdge.Right => new Rectangle(
                    _workRect.Right - gap - thickness,
                    CenterStart(centerY, length, _workRect.Top, _workRect.Bottom),
                    thickness, length),
                DockEdge.Top => new Rectangle(
                    CenterStart(centerX, length, _workRect.Left, _workRect.Right),
                    _workRect.Top + gap,
                    length, thickness),
                _ => new Rectangle(
                    CenterStart(centerX, length, _workRect.Left, _workRect.Right),
                    _workRect.Bottom - thickness,
                    length, thickness),
            };
        }

        private static int CenterStart(int center, int length, int min, int max)
            => Math.Clamp(center - length / 2, min, Math.Max(min, max - length));

        private void RefreshWindowTitle()
        {
            if (!Win32.IsWindow(_explorer)) return;
            long now = Environment.TickCount64;
            _lastTitleCheck = now;
            if (_groupLabel is not null)
            {
                _handle.Label = _groupLabel;
                return;
            }

            string title = Win32.GetTitleOf(_explorer).Trim();
            // Windows 11の複数タブ用タイトルが閉じた後も残ることがある。
            // ラベルにはフォルダー名だけを表示する。
            int otherTabs = title.IndexOf("とその他", StringComparison.Ordinal);
            if (otherTabs >= 0) title = title[..otherTabs].Trim();
            foreach (string suffix in new[] { " - エクスプローラー", " – エクスプローラー",
                         " — エクスプローラー", " - File Explorer", "-エクスプローラー",
                         " - Explorer", "-Explorer" })
                if (title.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                {
                    title = title[..^suffix.Length].Trim();
                    break;
                }

            // 汎用ウィンドウ名ではなく、実際に開いているフォルダー名を使う。
            if (title is "エクスプローラー" or "ファイル エクスプローラー" or "Explorer" or "File Explorer" or "")
            {
                if (_lastFolderNameCheck == 0 || now - _lastFolderNameCheck >= 5000)
                {
                    _lastFolderNameCheck = now;
                    string? folder = ExplorerFolders.ForWindow(_explorer);
                    _folderNameFromPath = folder is null ? ""
                        : string.Equals(folder, ExplorerFolders.ThisPc, StringComparison.OrdinalIgnoreCase)
                            ? "PC" : Path.GetFileName(Path.TrimEndingDirectorySeparator(folder));
                    if (_folderNameFromPath.Length == 0 && folder is not null) _folderNameFromPath = folder;
                }
                title = _folderNameFromPath;
            }
            _handle.Label = title;
        }

        public void SetHandleMaxSpan(int maxSpan)
        {
            maxSpan = Math.Max(24, maxSpan);
            if (_maxHandleSpan == maxSpan) return;
            _maxHandleSpan = maxSpan;
            if (!IsDocked) return;
            UpdateHandleRect();
            if (_animating)
                _animationEnd = _animationTarget == DockPhase.Collapsed ? _handleRect : ExpandedDockBounds();
            if (!_fullscreenSuppressed) _ = ShowHandle(false);
        }

        public void SetHandleCenter(int center)
        {
            if (!IsDocked) return;
            _handleCenter = center;
            if (_dockEdge is DockEdge.Left or DockEdge.Right) _dockTop = center - _dockHeight / 2;
            else _dockLeft = center - _dockWidth / 2;
            UpdateHandleRect();
            if (_animating)
                _animationEnd = _animationTarget == DockPhase.Collapsed ? _handleRect : ExpandedDockBounds();
            if (_phase == DockPhase.Expanded && !_animating) _ = ApplyDockGeometry();
            if (!_fullscreenSuppressed) _ = ShowHandle(false);
        }

        /// <summary>同じ位置に揃える場合も、個別に別の画面端へ移す場合もこの入口で変更する。</summary>
        public void SetDockTarget(Rectangle work, DockEdge edge, int center)
        {
            if (!IsDocked || work.IsEmpty) return;
            _workRect = work;
            _dockEdge = edge;
            _handleCenter = center;
            _scale = GetScaleForScreen(Screen.FromRectangle(work), _explorer);
            if (edge is DockEdge.Left or DockEdge.Right)
            {
                _dockLeft = edge == DockEdge.Left ? work.Left : work.Right - _dockWidth;
                _dockTop = center - _dockHeight / 2;
            }
            else
            {
                _dockTop = edge == DockEdge.Top ? work.Top : work.Bottom - _dockHeight;
                _dockLeft = center - _dockWidth / 2;
            }
            ClampDockGeometry();
            UpdateHandleRect();
            if (_animating)
                _animationEnd = _animationTarget == DockPhase.Collapsed ? _handleRect : ExpandedDockBounds();
            if (_phase == DockPhase.Expanded && !_animating) _ = ApplyDockGeometry();
            if (!_fullscreenSuppressed) _ = ShowHandle(false);
        }

        private void MoveHandleTo(Point point)
        {
            if (_fullscreenSuppressed || !IsDocked) return;
            // 整列モードでもモニターをまたいでドラッグ可能にする。
            // 左右に隣接していても作業領域を結合せず、各モニターの左右端へ吸着する。
            var work = Screen.FromPoint(point).WorkingArea;
            var edge = ChooseNearestEdge(new Rectangle(point.X, point.Y, 1, 1), work, _dockEdge);
            int center = edge is DockEdge.Left or DockEdge.Right ? point.Y : point.X;
            if (work != _workRect || edge != _dockEdge)
                SetDockTarget(work, edge, center);
            else
                SetHandleCenter(center);
        }

        public void RaiseHandle()
        {
            if (!IsDocked || _fullscreenSuppressed || !_handle.Visible) return;
            _ = Win32.SetWindowPos(_handle.Handle, Win32.HWND_TOPMOST,
                0, 0, 0, 0, Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE);
        }

        public void CollapseFromCoordinator(bool force = false)
        {
            if (!force && _pinned) return;
            if (_animating && _animationTarget == DockPhase.Expanded)
            {
                _collapseRequestedByHandle = true;
                return;
            }
            if (_phase == DockPhase.Expanded && !_animating) StartCollapse();
        }

        /// <summary>
        /// ドラッグでWorkを結合した直後は、固定状態にかかわらず一度収納する。
        /// 展開アニメーション中なら完了後に収納する。
        /// </summary>
        public void CollapseAfterWorkDrop()
        {
            if (!IsDocked) return;
            _collapseRequestedAfterWorkDrop = true;
            _suppressEdgeHoverUntilExit = true;
            _handle.SuppressHoverUntilExit();
            _outsideTick = -1;
            _edgeHoverSince = -1;
            if (_phase == DockPhase.Expanded && !_animating) StartCollapse();
        }

        public void CollapseAfterUnpin()
        {
            if (_pinned || !IsDocked) return;
            // 展開アニメーション中でも、完了したら必ず収納する。
            if (_animating && _animationTarget == DockPhase.Expanded)
            {
                _collapseRequestedAfterUnpin = true;
                return;
            }
            if (_phase != DockPhase.Expanded || _animating) return;

            _collapseRequestedAfterUnpin = false;
            _handle.SuppressHoverUntilExit();
            _suppressEdgeHoverUntilExit = true;
            StartCollapse();
        }

        public void ExpandFromCoordinator(bool manual = false)
        {
            if (manual)
            {
                // クリック操作は自動ホバーの再展開抑止より優先する。
                _collapseRequestedAfterWorkDrop = false;
                _collapseRequestedAfterUnpin = false;
                _collapseRequestedByHandle = false;
                _suppressEdgeHoverUntilExit = false;
            }
            if (_animating)
            {
                if (manual && _animationTarget == DockPhase.Collapsed)
                    _expandRequestedByHandle = true;
                return;
            }
            if (!_collapseRequestedAfterWorkDrop && _phase == DockPhase.Collapsed && !_fullscreenSuppressed
                && !IsFullscreenOnDockMonitor())
                BeginTransition(_handleRect, ExpandedDockBounds(), DockPhase.Expanded);
        }

        /// <summary>ハンドルを表示し、実際のウィンドウ矩形を作業領域内に合わせる。</summary>
        private bool ShowHandle(bool allowHover)
        {
            if (!_handleEnabled) { _handle.Visible = false; return true; }
            try
            {
                _handle.Edge = _dockEdge;
                _handle.Bounds = _handleRect;
                _handle.Visible = true;
                // 生成時の自動スケールや最小幅で丸められた場合に備えて再設定する
                _handle.Bounds = _handleRect;

                // Explorerを最前面表示した直後でも、ハンドルをさらに手前へ出して確実にクリックできるようにする。
                _ = Win32.SetWindowPos(_handle.Handle, Win32.HWND_TOPMOST,
                    _handleRect.Left, _handleRect.Top, _handleRect.Width, _handleRect.Height,
                    Win32.SWP_NOACTIVATE | Win32.SWP_SHOWWINDOW);

                if (!Win32.GetWindowRect(_handle.Handle, out var r)) return false;
                var actual = Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
                if (actual.Width < 4 || actual.Height < 4) return false;

                if (actual != _handleRect)
                {
                    _handleRect = actual;
                    if (_handleRect.Right > _workRect.Right) _handleRect.X = _workRect.Right - _handleRect.Width;
                    if (_handleRect.Left < _workRect.Left) _handleRect.X = _workRect.Left;
                    if (_handleRect.Bottom > _workRect.Bottom) _handleRect.Y = _workRect.Bottom - _handleRect.Height;
                    if (_handleRect.Top < _workRect.Top) _handleRect.Y = _workRect.Top;
                    _handle.Bounds = _handleRect;
                }

                if (!Win32.IsWindowVisible(_handle.Handle)) return false;
                // 表示した瞬間にカーソルがハンドル上にある場合もホバー展開を開始する
                if (allowHover) _handle.CheckHover();
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>選ばれた画面端の表示位置。</summary>
        private Rectangle DockBounds()
        {
            ClampDockGeometry();
            int x = _dockEdge switch
            {
                DockEdge.Left => _workRect.Left,
                DockEdge.Right => _workRect.Right - _dockWidth,
                _ => _dockLeft,
            };
            int y = _dockEdge switch
            {
                DockEdge.Top => _workRect.Top,
                DockEdge.Bottom => _workRect.Bottom - _dockHeight,
                _ => _dockTop,
            };
            return new Rectangle(x, y, _dockWidth, _dockHeight);
        }

        /// <summary>
        /// 展開中もハンドルをクリックできるよう、Explorer本体を少しだけ内側へずらす。
        /// 画面いっぱいのExplorerの場合だけ幅・高さも調整して、ハンドルの領域を確保する。
        /// </summary>
        private Rectangle ExpandedDockBounds()
        {
            if (_workBounds is Rectangle workBounds) return workBounds;
            var bounds = DockBounds();
            int handleSize = _dockEdge is DockEdge.Left or DockEdge.Right
                ? Math.Max(_handleRect.Width, Math.Max(4, (int)Math.Round(HandleThickness * _scale)))
                : Math.Max(_handleRect.Height, Math.Max(4, (int)Math.Round(HandleThickness * _scale)));
            int inset = handleSize + Math.Max(2, (int)Math.Round(ExpandedHandleMargin * _scale));

            switch (_dockEdge)
            {
                case DockEdge.Left:
                    bounds.Width = Math.Min(bounds.Width, Math.Max(1, _workRect.Width - inset));
                    bounds.X = _workRect.Left + inset;
                    break;
                case DockEdge.Right:
                    bounds.Width = Math.Min(bounds.Width, Math.Max(1, _workRect.Width - inset));
                    bounds.X = _workRect.Right - inset - bounds.Width;
                    break;
                case DockEdge.Top:
                    bounds.Height = Math.Min(bounds.Height, Math.Max(1, _workRect.Height - inset));
                    bounds.Y = _workRect.Top + inset;
                    break;
                case DockEdge.Bottom:
                    bounds.Height = Math.Min(bounds.Height, Math.Max(1, _workRect.Height - inset));
                    bounds.Y = _workRect.Bottom - inset - bounds.Height;
                    break;
            }
            return bounds;
        }

        private bool ApplyDockGeometry(bool bringToFront = false)
        {
            var bounds = ExpandedDockBounds();
            const uint flags = Win32.SWP_NOACTIVATE | Win32.SWP_SHOWWINDOW;
            if (!bringToFront)
            {
                // サイズ変更やWork内の再配置では、他のアプリの前に割り込まない。
                return Win32.SetWindowPos(_explorer, Win32.HWND_TOP,
                    bounds.Left, bounds.Top, bounds.Width, bounds.Height,
                    flags | Win32.SWP_NOZORDER);
            }

            // NOTOPMOSTだけでは、ブラウザなどの通常ウィンドウより背面に残る場合がある。
            // 展開時に一度だけ前面へ出し、直後に通常のZ順へ戻す（常時最前面にはしない）。
            if (!Win32.SetWindowPos(_explorer, Win32.HWND_TOPMOST,
                bounds.Left, bounds.Top, bounds.Width, bounds.Height, flags)) return false;
            return _savedWasTopmost || Win32.SetWindowPos(_explorer, Win32.HWND_NOTOPMOST,
                0, 0, 0, 0, Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE);
        }

        private void RemoveTemporaryTopmost(IntPtr hWnd)
        {
            if (_savedWasTopmost || hWnd == IntPtr.Zero || !Win32.IsWindow(hWnd)) return;
            _ = Win32.SetWindowPos(hWnd, Win32.HWND_NOTOPMOST, 0, 0, 0, 0,
                Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE);
        }

        private void SavePlacement(IntPtr hWnd)
        {
            // 位置・サイズ・表示状態は触らずに保存する(収納は可視化の切替のみ)
            var placement = new Win32.WINDOWPLACEMENT { Length = Marshal.SizeOf<Win32.WINDOWPLACEMENT>() };
            _ = Win32.GetWindowPlacement(hWnd, ref placement);
            _savedPlacement = placement;
            _savedWasTopmost = (Win32.GetWindowLong(hWnd, Win32.GWL_EXSTYLE) & Win32.WS_EX_TOPMOST) != 0;
            _hasSavedState = true;
        }

        /// <summary>保存済みのウィンドウ位置・状態へ戻す。</summary>
        private void RestoreSavedPlacement(IntPtr hWnd)
        {
            if (!_hasSavedState) return;

            var placement = _savedPlacement;
            placement.Length = Marshal.SizeOf<Win32.WINDOWPLACEMENT>();
            // Dock解除後は必ず表示する(最小化状態は通常表示へ戻す)
            if (placement.ShowCmd == Win32.SW_SHOWMINIMIZED) placement.ShowCmd = Win32.SW_SHOWNORMAL;

            if (!Win32.SetWindowPlacement(hWnd, ref placement))
            {
                _ = Win32.ShowWindow(hWnd, Win32.SW_RESTORE);
            }
            _hasSavedState = false;
        }

        private void WriteStateFile()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_statePath)!);
                var state = new SavedStateFile
                {
                    Hwnd = _explorer.ToInt64(),
                    Pid = Win32.GetProcessIdOf(_explorer),
                    Title = Win32.GetTitleOf(_explorer),
                    ShowCmd = _savedPlacement.ShowCmd,
                    Left = _savedPlacement.NormalPosition.Left,
                    Top = _savedPlacement.NormalPosition.Top,
                    Right = _savedPlacement.NormalPosition.Right,
                    Bottom = _savedPlacement.NormalPosition.Bottom,
                };
                File.WriteAllText(_statePath, JsonSerializer.Serialize(state));
            }
            catch
            {
                // 永続化は補助機能なので失敗しても継続
            }
        }

        private void DeleteStateFile()
        {
            try { File.Delete(_statePath); } catch { /* 無視 */ }
        }

        private DockPositionFile? ReadDockPosition()
        {
            try
            {
                var position = JsonSerializer.Deserialize<DockPositionFile>(File.ReadAllBytes(_positionPath));
                return position is { Version: 1 }
                    && Enum.IsDefined(position.Edge)
                    && double.IsFinite(position.Position)
                    && position.Position is >= 0 and <= 1 ? position : null;
            }
            catch { return null; }
        }

        private void SaveDockPosition()
        {
            if (!_independentPosition && !_persistSharedPosition) return;
            try
            {
                var screen = Screen.FromRectangle(_workRect);
                double center = _dockEdge is DockEdge.Left or DockEdge.Right
                    ? _dockTop + _dockHeight / 2.0 - _workRect.Top
                    : _dockLeft + _dockWidth / 2.0 - _workRect.Left;
                double length = _dockEdge is DockEdge.Left or DockEdge.Right
                    ? _workRect.Height : _workRect.Width;
                _lastDockPosition = new DockPositionFile
                {
                    Version = 1,
                    Screen = screen.DeviceName,
                    Edge = _dockEdge,
                    Position = Math.Clamp(center / Math.Max(1, length), 0, 1),
                };
                Directory.CreateDirectory(Path.GetDirectoryName(_positionPath)!);
                File.WriteAllText(_positionPath, JsonSerializer.Serialize(_lastDockPosition));
            }
            catch { /* 前回位置の保存に失敗してもDock動作は継続 */ }
        }

        private static bool SavedWindowMatches(IntPtr hWnd, SavedStateFile state)
            => Win32.IsExplorerWindow(hWnd) && Win32.GetProcessIdOf(hWnd) == state.Pid;

        private static bool ApplyPlacement(IntPtr hWnd, SavedStateFile state)
        {
            var placement = new Win32.WINDOWPLACEMENT
            {
                Length = Marshal.SizeOf<Win32.WINDOWPLACEMENT>(),
                ShowCmd = state.ShowCmd == Win32.SW_SHOWMINIMIZED ? Win32.SW_SHOWNORMAL : state.ShowCmd,
                NormalPosition = new Win32.RECT
                {
                    Left = state.Left,
                    Top = state.Top,
                    Right = state.Right,
                    Bottom = state.Bottom,
                },
            };
            return Win32.SetWindowPlacement(hWnd, ref placement);
        }

        private void OnTick(object? sender, EventArgs e)
        {
            if (_phase == DockPhase.None)
            {
                _timer.Stop();
                return;
            }
            if (!Win32.IsWindow(_explorer))
            {
                OnExplorerClosed();
                return;
            }
            if (UpdateFullscreenSuppression() || _animating) return;
            // Windows 11のリサイズ通知が抜けても実寸からWorkの隣接配置を更新する。
            if (_phase == DockPhase.Expanded) PollWorkResize();
            // ドロップ直後の再展開を防ぎ、一度ハンドル付近を離れたら通常動作に戻す。
            if (_collapseRequestedAfterWorkDrop && !EdgeActivationRect().Contains(Cursor.Position))
                _collapseRequestedAfterWorkDrop = false;
            if (Environment.TickCount64 - _lastTitleCheck >= 1200) RefreshWindowTitle();

            switch (_phase)
            {
                case DockPhase.Collapsed:
                    WatchCollapsed();
                    break;
                case DockPhase.Expanded:
                    WatchExpanded();
                    break;
            }
        }

        private bool UpdateFullscreenSuppression()
        {
            bool fullscreen = IsFullscreenOnDockMonitor();
            if (fullscreen == _fullscreenSuppressed) return fullscreen;

            _fullscreenSuppressed = fullscreen;
            _edgeHoverSince = -1;
            if (fullscreen)
            {
                if (_animating) StopTransition();
                DockVisibilityChanged?.Invoke(_explorer, false);
                _ = Win32.ShowWindow(_explorer, Win32.SW_HIDE);
                RemoveTemporaryTopmost(_explorer);
                _phase = DockPhase.Collapsed;
                _handle.Expanded = false;
                _handle.Visible = false;
                _outsideTick = -1;
            }
            else if (!ShowHandle(false))
            {
                Undock();
            }
            return fullscreen;
        }

        private bool IsFullscreenOnDockMonitor()
        {
            var foreground = Win32.GetForegroundWindow();
            if (foreground == IntPtr.Zero || foreground == _explorer ||
                !Win32.IsWindowVisible(foreground) || Win32.IsIconic(foreground)) return false;

            var windowClass = Win32.GetClassNameOf(foreground);
            if (windowClass is "Progman" or "WorkerW" or "Shell_TrayWnd") return false;
            if (!Win32.GetWindowRect(foreground, out var r)) return false;

            // 作業領域ではなくモニター全体を覆うウィンドウだけをフルスクリーン扱いにする。
            var monitor = Screen.FromRectangle(_workRect).Bounds;
            const int tolerance = 2;
            return r.Left <= monitor.Left + tolerance && r.Top <= monitor.Top + tolerance
                && r.Right >= monitor.Right - tolerance && r.Bottom >= monitor.Bottom - tolerance;
        }

        private Rectangle EdgeActivationRect()
        {
            var bounds = _handleRect;
            if (_dockEdge is DockEdge.Left or DockEdge.Right) bounds.Inflate(18, 32);
            else bounds.Inflate(32, 18);
            return Rectangle.Intersect(bounds, _workRect);
        }

        private void WatchCollapsed()
        {
            // 収納は非表示の実現のため、外部から再表示されたら展開状態へ合わせる
            if (Win32.IsWindowVisible(_explorer) && !Win32.IsIconic(_explorer))
            {
                StartExpansion();
                return;
            }

            if (!_handleEnabled || _handleMenu.Visible) { _edgeHoverSince = -1; return; }
            bool inArea = EdgeActivationRect().Contains(Cursor.Position);
            if (!DockHandleForm.IsDataDragActive && Win32.IsMouseButtonPressed())
            {
                _edgeHoverSince = -1;
                return;
            }
            if (_suppressEdgeHoverUntilExit)
            {
                if (!inArea) _suppressEdgeHoverUntilExit = false;
                _edgeHoverSince = -1;
                return;
            }
            if (!inArea)
            {
                _edgeHoverSince = -1;
                return;
            }
            long now = Environment.TickCount64;
            if (_edgeHoverSince < 0) _edgeHoverSince = now;
            else if (now - _edgeHoverSince >= EdgeHoverDelayMs) StartExpansion();
        }

        private void WatchExpanded()
        {
            if (_animating || _collapseDelayMs < 0 || _pinned) return;
            long now = Environment.TickCount64;

            var current = CurrentWindowRect();
            Win32.GetCursorPos(out var p);
            var cursor = new Point(p.X, p.Y);
            bool inside = current.Contains(cursor) || EdgeActivationRect().Contains(cursor)
                || (GroupCursorInside?.Invoke(cursor) ?? false);

            // ファイルのドラッグ中は離れたDockを収納できるよう、押下状態を無視する。
            bool busy = (!DockHandleForm.IsDataDragActive && Win32.IsMouseButtonPressed())
                || IsMenuOrDialogActive();
            if (inside || busy)
            {
                _outsideTick = -1;
                return;
            }

            if (_outsideTick < 0)
            {
                _outsideTick = now;
            }
            else if (now - _outsideTick >= Math.Max(_collapseDelayMs, _workBounds.HasValue ? 550 : 0)
                     && now - _expandedTick >= MinExpandedMs)
            {
                StartCollapse();
            }
        }

        private bool IsMenuOrDialogActive()
        {
            var fg = Win32.GetForegroundWindow();
            if (fg == IntPtr.Zero) return false;
            if (string.Equals(Win32.GetClassNameOf(fg), MenuWindowClass, StringComparison.Ordinal)) return true;
            if (fg != _explorer && Win32.GetWindow(fg, Win32.GW_OWNER) == _explorer) return true;
            return IsContextMenuOpen();
        }

        /// <summary>
        /// 右クリックメニューが開いているか。フォアグラウンドがExplorerのままのメニュー
        /// (Windows 11の新しいメニュー=XAMLのポップアップ、従来のメニュー)も拾うため、
        /// 前面ではなく、Explorer本体と同じプロセスの表示中メニューウィンドウを探す。
        /// </summary>
        private bool IsContextMenuOpen()
        {
            uint pid = Win32.GetProcessIdOf(_explorer);
            bool found = false;
            Win32.EnumWindows((hWnd, _) =>
            {
                if (!Win32.IsWindowVisible(hWnd)) return true;
                string name = Win32.GetClassNameOf(hWnd);
                bool popup = name == MenuWindowClass
                    || (name == "Windows.UI.Core.CoreWindow" || name == "Microsoft.UI.Content.PopupWindowSiteBridge"
                        || name == "Xaml_WindowedPopupClass");
                if (popup && Win32.GetProcessIdOf(hWnd) == pid) found = true;
                return !found;
            }, IntPtr.Zero);
            return found;
        }

        private void ToggleExpansion()
        {
            if (_animating)
            {
                if (_animationTarget == DockPhase.Expanded) CollapseFromCoordinator(force: true);
                else ExpandFromCoordinator(manual: true);
                return;
            }
            if (_phase == DockPhase.Collapsed)
            {
                // ホバー抑制中も、明示的なクリックなら展開できる。
                _collapseRequestedAfterWorkDrop = false;
                _suppressEdgeHoverUntilExit = false;
                StartExpansion();
            }
            else if (_phase == DockPhase.Expanded)
            {
                StartCollapse();
            }
        }

        public void ToggleFromHandle() => ToggleExpansion();

        private void StartExpansion()
        {
            if (_phase != DockPhase.Collapsed || _animating || _fullscreenSuppressed
                || _suppressEdgeHoverUntilExit || IsFullscreenOnDockMonitor()) return;
            if (!Win32.IsWindow(_explorer))
            {
                OnExplorerClosed();
                return;
            }

            // Workの他メンバーも同じタイミングで展開する。
            Expanding?.Invoke(this);
            BeginTransition(_handleRect, ExpandedDockBounds(), DockPhase.Expanded);
        }

        private void StartCollapse()
        {
            if (_phase != DockPhase.Expanded || _animating) return;
            // 収納後、ハンドルにカーソルが残っていても即座に再展開しない。
            // 一度ハンドル周辺から離れれば、通常どおりホバーで展開できる。
            _suppressEdgeHoverUntilExit = true;
            _handle.SuppressHoverUntilExit();
            Collapsing?.Invoke(this);

            var rect = GetDockRect(_explorer);
            if (_workBounds.HasValue)
            {
                // Work配置中の実ウィンドウ位置を通常Dockの収納先として記憶しない。
                if (_animationsEnabled) BeginTransition(rect, _handleRect, DockPhase.Collapsed);
                DockVisibilityChanged?.Invoke(_explorer, false);
                _ = Win32.ShowWindow(_explorer, Win32.SW_HIDE);
                RemoveTemporaryTopmost(_explorer);
                if (!_animationsEnabled) BeginTransition(rect, _handleRect, DockPhase.Collapsed);
                return;
            }
            if (!_handleCenter.HasValue)
            {
                var screen = Screen.FromRectangle(rect);
                _workRect = screen.WorkingArea;
                _scale = GetScale(_explorer);
                _dockEdge = ChooseNearestEdge(rect, _workRect, _dockEdge);
            }
            _dockLeft = rect.Left;
            _dockTop = rect.Top;
            _dockWidth = rect.Width;
            _dockHeight = rect.Height;
            ClampDockGeometry();
            UpdateHandleRect();

            // ハンドルを表示できない場合はExplorerを隠さない。
            _handle.Expanded = false;
            if (!ShowHandle(false))
            {
                Undock();
                return;
            }
            SaveDockPosition();

            // アニメーションの最初の輪郭を表示してからExplorerを隠し、開始時の空白を防ぐ。
            if (_animationsEnabled) BeginTransition(rect, _handleRect, DockPhase.Collapsed);
            DockVisibilityChanged?.Invoke(_explorer, false);
            _ = Win32.ShowWindow(_explorer, Win32.SW_HIDE);
            RemoveTemporaryTopmost(_explorer);
            if (!_animationsEnabled) BeginTransition(rect, _handleRect, DockPhase.Collapsed);
        }

        private void BeginTransition(Rectangle start, Rectangle end, DockPhase target)
        {
            var visibleStart = Rectangle.Intersect(start, _workRect);
            _animationStart = visibleStart.IsEmpty
                ? (target == DockPhase.Collapsed ? DockBounds() : end) : visibleStart;
            _animationEnd = end;
            _animationTarget = target;
            _animationDurationMs = target == DockPhase.Collapsed ? CollapseAnimationMs : ExpandAnimationMs;
            _animating = true;
            if (!_animationsEnabled)
            {
                FinishTransition();
                return;
            }
            try
            {
                _transition.BeginCue(_animationStart, _animationEnd, target == DockPhase.Collapsed);
                _animationClock.Restart();
                _animationCancellation = new CancellationTokenSource();
                _ = AnimateTransitionAsync(_animationCancellation.Token);
            }
            catch
            {
                StopTransition();
                Undock();
                throw;
            }
        }

        private void StopTransition()
        {
            var cancellation = _animationCancellation;
            _animationCancellation = null;
            cancellation?.Cancel();
            cancellation?.Dispose();
            _animationClock.Stop();
            _transition.Hide();
            _animating = false;
        }

        /// <summary>
        /// 次の画面合成(垂直同期)まで待つ。固定間隔のタイマーよりコマの間隔がそろい、
        /// システム全体のタイマー精度を上げる必要もない。
        /// </summary>
        private static async Task WaitForNextFrameAsync(CancellationToken cancellation)
        {
            long start = Stopwatch.GetTimestamp();
            int hr = await Task.Run(Win32.DwmFlush, cancellation);
            // 合成待ちができなかった場合だけ、通常のタイマーで間隔をあける
            if (hr != 0 || Stopwatch.GetElapsedTime(start).TotalMilliseconds < 2)
            {
                await Task.Delay(8, cancellation);
            }
        }

        private async Task AnimateTransitionAsync(CancellationToken cancellation)
        {
            try
            {
                while (_animating && !cancellation.IsCancellationRequested)
                {
                    // 画面の更新に合わせてUIスレッドへ戻り、実経過時間から位置を決める。
                    await WaitForNextFrameAsync(cancellation);
                    if (!_animating || cancellation.IsCancellationRequested) return;
                    if (!Win32.IsWindow(_explorer))
                    {
                        StopTransition();
                        OnExplorerClosed();
                        return;
                    }

                    double t = Math.Clamp(_animationClock.Elapsed.TotalMilliseconds / _animationDurationMs, 0, 1);
                    // 収納は両端で速度がゼロになる曲線にし、大きな輪郭の急な動き出しを防ぐ。
                    // 展開側のレスポンスとイージングは変更しない。
                    double u = 1 - t;
                    double progress = _animationTarget == DockPhase.Collapsed
                        ? t * t * (3 - 2 * t)
                        : 1 - u * u * u;
                    var current = new RectangleF(
                        (float)(_animationStart.Left + (_animationEnd.Left - _animationStart.Left) * progress),
                        (float)(_animationStart.Top + (_animationEnd.Top - _animationStart.Top) * progress),
                        Math.Max(1, (float)(_animationStart.Width + (_animationEnd.Width - _animationStart.Width) * progress)),
                        Math.Max(1, (float)(_animationStart.Height + (_animationEnd.Height - _animationStart.Height) * progress)));
                    _transition.ShowCue(current, progress);

                    if (t < 1) continue;
                    FinishTransition();
                    return;
                }
            }
            catch (OperationCanceledException) { }
            catch
            {
                // 描画に失敗した場合もExplorerが非表示のまま残らないよう復元する。
                if (_animating)
                {
                    try { Undock(); }
                    catch { StopTransition(); }
                }
            }
        }

        private void FinishTransition()
        {
            // 次に映すもの(ハンドル/Explorer)を先に出してから輪郭を消し、何も映らない瞬間を作らない
            _outsideTick = -1;
            _edgeHoverSince = -1;
            if (_animationTarget == DockPhase.Collapsed)
            {
                _collapseRequestedAfterUnpin = false;
                _phase = DockPhase.Collapsed;
                _handle.Expanded = false;
                bool shown = ShowHandle(false);
                StopTransition();
                if (!shown) Undock();
                else
                {
                    if (_animationsEnabled) _handle.PulseArrival();
                    if (_expandRequestedByHandle)
                    {
                        _expandRequestedByHandle = false;
                        ExpandFromCoordinator(manual: true);
                    }
                }
            }
            else
            {
                if (Win32.IsIconic(_explorer) || Win32.IsZoomed(_explorer))
                {
                    _ = Win32.ShowWindow(_explorer, Win32.SW_RESTORE);
                }
                bool applied = ApplyDockGeometry(bringToFront: true);
                StopTransition();
                if (!applied)
                {
                    Undock();
                    return;
                }
                _phase = DockPhase.Expanded;
                _handle.Expanded = true;
                if (!ShowHandle(false))
                {
                    Undock();
                    return;
                }
                _expandedTick = Environment.TickCount64;
                DockVisibilityChanged?.Invoke(_explorer, true);
                // クリックで収納待ちのメンバーを、展開完了通知で再展開しない。
                bool collapseByHandle = _collapseRequestedByHandle;
                _collapseRequestedByHandle = false;
                if (!collapseByHandle) Expanded?.Invoke(this);
                if (collapseByHandle || _collapseRequestedAfterWorkDrop) StartCollapse();
                else if (_collapseRequestedAfterUnpin) CollapseAfterUnpin();
            }
        }

        internal void HandleMoveSizeEvent(uint eventType)
        {
            if (_phase != DockPhase.Expanded) return;
            if (eventType == Win32.EVENT_SYSTEM_MOVESIZESTART)
            {
                _titleBarDrag = IsTitleBarDrag();
                return;
            }
            if (eventType != Win32.EVENT_SYSTEM_MOVESIZEEND) return;

            try
            {
                SnapExpandedWindowToEdge();
            }
            catch
            {
                // ネイティブコールバック内の例外でアプリを終了させない
            }
        }

        /// <summary>
        /// Dock中のウィンドウが途中で止まらないよう、移動終了時に画面端へ戻す。
        /// リサイズ後は今の辺を維持し、移動後は最寄りの辺へスナップする。
        /// </summary>
        private bool _titleBarDrag;

        /// <summary>カーソルがタイトルバー付近にあれば、枠のリサイズではなく移動の操作とみなす。</summary>
        private bool IsTitleBarDrag()
        {
            if (!Win32.GetWindowRect(_explorer, out var r)) return false;
            var cursor = Cursor.Position;
            int band = (int)Math.Round(100 * _scale);
            int frame = (int)Math.Round(8 * _scale);
            return cursor.X >= r.Left + frame && cursor.X < r.Right - frame
                && cursor.Y >= r.Top + frame && cursor.Y <= r.Top + band;
        }

        private void SnapExpandedWindowToEdge()
        {
            if (_animating || _phase != DockPhase.Expanded || !Win32.IsWindow(_explorer)) return;
            bool titleBarDrag = _titleBarDrag;
            _titleBarDrag = false;
            if (titleBarDrag) _handleCenter = null;
            if (Win32.IsIconic(_explorer)) return;
            if (Win32.IsZoomed(_explorer))
            {
                // 画面上端へ運ぶとWindowsが最大化するので、移動として扱えるよう元に戻す
                if (!titleBarDrag) return;
                _ = Win32.ShowWindow(_explorer, Win32.SW_RESTORE);
            }
            if (_workBounds is Rectangle workBounds)
            {
                var current = GetDockRect(_explorer);
                // タイトルバーでの移動中にWindowsのスナップで大きさが変わっても、移動として扱う
                if (titleBarDrag) current = new Rectangle(current.Location, workBounds.Size);
                // Workをタイトルバーで別のモニターへ運んだ場合、元の場所に戻さず
                // Work全体の収納先を更新する。サイズ変更だけでは移動先を変更しない。
                var destinationWork = Screen.FromRectangle(current).WorkingArea;
                if (current.Size == workBounds.Size && destinationWork != _workRect)
                {
                    var edge = ChooseNearestEdge(current, destinationWork, _dockEdge);
                    int center = edge is DockEdge.Left or DockEdge.Right
                        ? current.Top + current.Height / 2 : current.Left + current.Width / 2;
                    if (DockTargetRequested is { } relocate)
                        relocate(this, destinationWork, edge, center);
                    else SetDockTarget(destinationWork, edge, center);
                    return;
                }
                if (current.Width != workBounds.Width || current.Height != workBounds.Height)
                {
                    // Workで手動リサイズした実寸を保存して、ほかのウィンドウを再配置する。
                    // 単なる移動なら既存の配置位置へ戻す。
                    _dockWidth = Math.Max(1, current.Width);
                    _dockHeight = Math.Max(1, current.Height);
                    LayoutChanged?.Invoke(this);
                }
                else if (current != workBounds)
                {
                    // 並べ替えの判定はWork全体を知っている側に任せる
                    if (WorkWindowMoved is { } moved) moved(this, current);
                    else _ = ApplyDockGeometry();
                }
                return;
            }

            var rect = GetDockRect(_explorer);
            if (titleBarDrag) rect = new Rectangle(rect.Location, new Size(_dockWidth, _dockHeight));
            bool resized = rect.Width != _dockWidth || rect.Height != _dockHeight;
            var screen = Screen.FromRectangle(rect);
            _workRect = screen.WorkingArea;
            _scale = GetScale(_explorer);

            if (!resized)
            {
                _dockEdge = ChooseNearestEdge(rect, _workRect, _dockEdge);
            }

            _dockLeft = rect.Left;
            _dockTop = rect.Top;
            _dockWidth = rect.Width;
            _dockHeight = rect.Height;
            ClampDockGeometry();
            UpdateHandleRect();
            if (ApplyDockGeometry())
            {
                SaveDockPosition();
                _ = ShowHandle(false);
                LayoutChanged?.Invoke(this);
            }
        }

        private void OnDisplayChanged()
        {
            if (!IsDocked) return;

            // ディスプレイ構成が変わったときだけDock先を取り直す
            _workRect = Screen.FromRectangle(_workRect).WorkingArea;
            ClampDockGeometry();
            UpdateHandleRect();
            SaveDockPosition();

            if (_fullscreenSuppressed) return;
            if (_phase == DockPhase.Expanded)
            {
                _ = ApplyDockGeometry();
                _ = ShowHandle(false);
            }
            else
            {
                _ = ShowHandle(true);
            }
            LayoutChanged?.Invoke(this);
        }

        private void OnExplorerClosed()
        {
            StopTransition();
            _timer.Stop();
            _phase = DockPhase.None;
            _collapseRequestedAfterUnpin = false;
            _collapseRequestedAfterWorkDrop = false;
            _explorer = IntPtr.Zero;
            _fullscreenSuppressed = false;
            _edgeHoverSince = -1;
            _suppressEdgeHoverUntilExit = false;
            _handle.Visible = false;
            _handle.Expanded = false;
            _hasSavedState = false;
            _savedWasTopmost = false;
            DeleteStateFile();
            StatusChanged?.Invoke("エクスプローラーが閉じられたため、Dockを解除しました。");
        }

        private Rectangle CurrentWindowRect()
        {
            _ = Win32.GetWindowRect(_explorer, out var r);
            return Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
        }

        private sealed class DockPositionFile
        {
            public int Version { get; set; }
            public string Screen { get; set; } = "";
            public DockEdge Edge { get; set; }
            public double Position { get; set; }
        }

        private sealed class SavedStateFile
        {
            public long Hwnd { get; set; }
            public uint Pid { get; set; }
            public string Title { get; set; } = "";
            public int ShowCmd { get; set; }
            public int Left { get; set; }
            public int Top { get; set; }
            public int Right { get; set; }
            public int Bottom { get; set; }
        }

        /// <summary>操作を奪わない半透明の収納方向インジケーター。</summary>
        internal sealed class DockTransitionForm : LayeredForm
        {
            private const int GlowMargin = 6;
            private static readonly Color Fill = Color.FromArgb(52, DockColors.AccentHover);
            private static readonly Color Glow = Color.FromArgb(56, DockColors.Accent);
            private static readonly Color Border = Color.FromArgb(240, DockColors.AccentLight);

            private bool _collapsing;
            private RectangleF _cue;
            private Size _canvas;

            public DockTransitionForm()
            {
                TopMost = true;
            }

            protected override CreateParams CreateParams
            {
                get
                {
                    const int WS_EX_TRANSPARENT = 0x00000020;
                    var cp = base.CreateParams;
                    cp.ExStyle |= WS_EX_TRANSPARENT;
                    return cp;
                }
            }

            protected override void WndProc(ref Message m)
            {
                if (m.Msg == 0x0084) { m.Result = new IntPtr(-1); return; } // WM_NCHITTEST → HTTRANSPARENT
                base.WndProc(ref m);
            }

            public void BeginCue(Rectangle start, Rectangle end, bool collapsing)
            {
                _collapsing = collapsing;
                _ = Handle;
                // 補間中にDIBを再確保しないよう、最大フレームの描画領域を先に確保する。
                ReserveSurface(new Size(
                    Math.Max(start.Width, end.Width) + GlowMargin * 2 + 2,
                    Math.Max(start.Height, end.Height) + GlowMargin * 2 + 2));
                // 前回の最終フレームが一瞬映らないよう、最初のフレームを描いてから表示する
                ShowCue(start, 0);
                if (!Visible) Show();
            }

            public void ShowDropCue(Rectangle bounds)
            {
                if (Visible && _cue.Size == bounds.Size
                    && Location == new Point(bounds.Left - GlowMargin, bounds.Top - GlowMargin)) return;
                _collapsing = false;
                _ = Handle;
                ShowCue(bounds, 1);
                if (!Visible) Show();
            }

            /// <summary>輪郭の位置・大きさを1フレーム分更新する。小数座標のまま描いてなめらかに動かす。</summary>
            public void ShowCue(RectangleF bounds, double progress)
            {
                var window = Rectangle.FromLTRB(
                    (int)Math.Floor(bounds.Left) - GlowMargin,
                    (int)Math.Floor(bounds.Top) - GlowMargin,
                    (int)Math.Ceiling(bounds.Right) + GlowMargin,
                    (int)Math.Ceiling(bounds.Bottom) + GlowMargin);
                _cue = new RectangleF(bounds.X - window.X, bounds.Y - window.Y, bounds.Width, bounds.Height);
                _canvas = window.Size;
                // 収納は端へ吸い込まれるように薄く、展開は実体に近づくように濃くする
                double opacity = _collapsing ? 1 - 0.4 * progress : 0.7 + 0.3 * progress;
                Present(window.Location, _canvas, opacity, Draw);
            }

            protected override void OnVisibleChanged(EventArgs e)
            {
                base.OnVisibleChanged(e);
                // 画面サイズ級の描画バッファは表示中だけ持つ
                if (!Visible) ReleaseSurface();
            }

            /// <summary>半透明の板と縁だけを描く。動きで方向が伝わるので矢印は付けない。</summary>
            private void Draw(Graphics g)
            {
                var cue = _cue;
                if (cue.Width < 2 || cue.Height < 2) return;
                float radius = Math.Min(8f, Math.Min(cue.Width, cue.Height) / 2f);
                using var path = RoundedRect(cue, radius);
                using var fill = new SolidBrush(Fill);
                using var glow = new Pen(Glow, 6f);
                using var border = new Pen(Border, 1.5f);
                g.FillPath(fill, path);
                g.DrawPath(glow, path);
                g.DrawPath(border, path);
            }
        }

        /// <summary>
        /// 画面端の細いハンドル。最前面だがフォーカスを取らない。
        /// </summary>
        private sealed class DockHandleForm : LayeredForm
        {
            private const double HoverMs = 120;
            private const double PulseMs = 480;
            private static readonly Color BaseColor = Color.FromArgb(235, DockColors.Navy);
            private static readonly Color HoverColor = DockColors.Accent;
            private static readonly Color PulseColor = DockColors.AccentHover;
            private static readonly Color EdgeColor = Color.FromArgb(60, 255, 255, 255);
            private static readonly Color DotColor = Color.FromArgb(235, 255, 255, 255);

            private readonly System.Windows.Forms.Timer _hoverTimer = new() { Interval = 85 };
            private readonly System.Windows.Forms.Timer _animationTimer = new() { Interval = 15 };
            private bool _hovered;
            private bool _dropTarget;
            private static bool _dataDragActive;
            private bool _expanded;
            private bool _suppressHoverUntilExit;
            private bool _animationsEnabled = true;
            private bool _showLabel = true;
            private string _label = "";
            private Font? _labelFont;
            private int _labelFontSize;
            private bool _dragging;
            private Point _mouseDown;
            private readonly ToolTip _toolTip = new() { InitialDelay = 500, ReshowDelay = 200 };
            private double _hover;
            private double _pulse;
            private long _pulseStart;
            private long _lastTick;

            private DockEdge _edge = DockEdge.Right;
            public DockEdge Edge
            {
                get => _edge;
                set
                {
                    if (_edge == value) return;
                    _edge = value;
                    Render();
                }
            }

            public bool DropTarget
            {
                set { if (_dropTarget == value) return; _dropTarget = value; Render(); }
            }

            public static bool IsDataDragActive
            {
                get
                {
                    if (_dataDragActive && !Win32.IsMouseButtonPressed()) _dataDragActive = false;
                    return _dataDragActive;
                }
            }

            public bool Expanded
            {
                set
                {
                    if (_expanded == value) return;
                    _expanded = value;
                    Render();
                }
            }

            public bool AnimationsEnabled
            {
                set
                {
                    _animationsEnabled = value;
                    if (value) return;
                    _animationTimer.Stop();
                    _pulse = 0;
                    _hover = _hovered ? 1 : 0;
                    Render();
                }
            }

            public bool ShowLabel
            {
                set { if (_showLabel == value) return; _showLabel = value; Render(); }
            }
            public string Label
            {
                set
                {
                    if (_label == value) return;
                    _label = value;
                    _toolTip.SetToolTip(this, value);
                    Render();
                }
            }

            public event Action? Hover;
            public event Action? ToggleClicked;
            public event Action? DisplayChanged;
            public event Action<Point>? RepositionRequested;
            public event Action? RepositionFinished;

            public DockHandleForm()
            {
                TopMost = true;
                Text = "ExplorerDocks Handle";
                Cursor = Cursors.Hand;
                AllowDrop = true;
                _animationTimer.Tick += OnAnimationTick;
                _toolTip.SetToolTip(this, _label);

                // 右クリックメニューを開く間もなく展開しないよう、少しだけ待つ
                _hoverTimer.Tick += (_, _) =>
                {
                    _hoverTimer.Stop();
                    if (Visible && !_expanded && !_suppressHoverUntilExit && Bounds.Contains(Cursor.Position))
                    {
                        Hover?.Invoke();
                    }
                    else
                    {
                        SetHovered(false);
                    }
                };
            }

            /// <summary>収納完了を知らせる短い光。</summary>
            public void PulseArrival()
            {
                _pulseStart = Environment.TickCount64;
                _pulse = 1;
                StartAnimation();
            }

            /// <summary>表示直後にカーソルがハンドル上にある場合もホバー扱いにする。</summary>
            public void CheckHover()
            {
                if (!Visible || _expanded || _suppressHoverUntilExit) return;
                if (Bounds.Contains(Cursor.Position))
                {
                    SetHovered(true);
                    _hoverTimer.Start();
                }
            }

            public void SuppressHoverUntilExit()
            {
                _suppressHoverUntilExit = true;
                _hoverTimer.Stop();
            }

            private void SetHovered(bool hovered)
            {
                if (_hovered == hovered) return;
                _hovered = hovered;
                if (_animationsEnabled)
                {
                    StartAnimation();
                }
                else
                {
                    _hover = hovered ? 1 : 0;
                    Render();
                }
            }

            private void StartAnimation()
            {
                if (_animationTimer.Enabled) return;
                _lastTick = Environment.TickCount64;
                _animationTimer.Start();
            }

            private void OnAnimationTick(object? sender, EventArgs e)
            {
                long now = Environment.TickCount64;
                _hover = Approach(_hover, _hovered ? 1 : 0, Math.Max(1, now - _lastTick), HoverMs);
                _lastTick = now;
                if (_pulse > 0)
                {
                    double t = Math.Min(1, (now - _pulseStart) / PulseMs);
                    _pulse = Math.Pow(1 - t, 2);
                }
                if (_hover == (_hovered ? 1 : 0) && _pulse <= 0) _animationTimer.Stop();
                Render();
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    _hoverTimer.Dispose();
                    _animationTimer.Dispose();
                    _toolTip.Dispose();
                    _labelFont?.Dispose();
                }
                base.Dispose(disposing);
            }

            protected override void OnResize(EventArgs e)
            {
                base.OnResize(e);
                Render();
            }

            private static bool ContainsJapanese(string text)
            {
                foreach (char c in text)
                {
                    if ((c >= '぀' && c <= 'ヿ') ||
                        (c >= '㐀' && c <= '䶿') ||
                        (c >= '一' && c <= '鿿') ||
                        c == '々' || c == 'ー')
                    {
                        return true;
                    }
                }
                return false;
            }

            private void DrawCenteredRotatedLabel(Graphics g, Brush textBrush, string text, float w, float h)
            {
                if (string.IsNullOrWhiteSpace(text)) return;

                // DrawString の行ボックスは文字の実際の輪郭より大きく、
                // 回転後に左右の余白が偏る。輪郭そのものを基準に中央揃えする。
                using var format = new StringFormat(StringFormat.GenericTypographic)
                {
                    FormatFlags = StringFormatFlags.NoWrap | StringFormatFlags.MeasureTrailingSpaces,
                };
                using var glyphs = new GraphicsPath();
                glyphs.AddString(text, _labelFont!.FontFamily, (int)_labelFont.Style,
                    _labelFont.Size, PointF.Empty, format);

                using (var rotate = new Matrix())
                {
                    rotate.Rotate(Edge == DockEdge.Left ? -90f : 90f);
                    glyphs.Transform(rotate);
                }

                var ink = glyphs.GetBounds();
                if (ink.Width <= 0f || ink.Height <= 0f) return;

                // 矢印の領域は呼び出し側で除いてあるので、残りに収まるよう縮小する。
                float maxWidth = Math.Max(1f, w - 4f);
                float maxHeight = Math.Max(1f, h - 4f);
                float scale = Math.Min(1f, Math.Min(maxWidth / ink.Width, maxHeight / ink.Height));
                if (scale < 1f)
                {
                    using var shrink = new Matrix();
                    shrink.Scale(scale, scale);
                    glyphs.Transform(shrink);
                    ink = glyphs.GetBounds();
                }

                // 表示文字の実寸（インク領域）の中心を、ハンドルの中心へ置く。
                using var center = new Matrix();
                center.Translate(w / 2f - (ink.Left + ink.Width / 2f),
                    h / 2f - (ink.Top + ink.Height / 2f));
                glyphs.Transform(center);
                g.FillPath(textBrush, glyphs);
            }

            private void DrawVerticalJapaneseLabel(Graphics g, Brush textBrush, string text, float w, float h, float thickness)
            {
                if (string.IsNullOrWhiteSpace(text)) return;

                float glyphHeight = _labelFont!.GetHeight(g);
                float step = Math.Max(1f, glyphHeight * 0.78f);
                float totalHeight = step * (text.Length - 1) + glyphHeight;
                if (totalHeight > h - 8f)
                {
                    step = Math.Max(1f, ((h - 8f) - glyphHeight) / Math.Max(1, text.Length - 1));
                    totalHeight = step * (text.Length - 1) + glyphHeight;
                }

                float y = (h - totalHeight) / 2f;
                using var format = new StringFormat(StringFormat.GenericTypographic)
                {
                    Alignment = StringAlignment.Center,
                    LineAlignment = StringAlignment.Center,
                    FormatFlags = StringFormatFlags.NoWrap,
                };

                for (int i = 0; i < text.Length; i++)
                {
                    g.DrawString(text[i].ToString(), _labelFont, textBrush,
                        new RectangleF(0f, y, w, glyphHeight), format);
                    y += step;
                }
            }

            protected override void WndProc(ref Message m)
            {
                base.WndProc(ref m);
                if (m.Msg == 0x007E) DisplayChanged?.Invoke(); // WM_DISPLAYCHANGE
            }

            private void Render()
            {
                if (Width <= 0 || Height <= 0) return;
                Present(null, Size, 1, Draw);
            }

            /// <summary>両端が丸いピル形のつまみ。</summary>
            private void Draw(Graphics g)
            {
                float w = Width;
                float h = Height;
                float thickness = Math.Min(w, h);
                var rect = new RectangleF(0, 0, w, h);
                using var path = RoundedRect(rect, thickness / 2f);

                var color = Lerp(BaseColor, HoverColor, EaseOutCubic(_hover));
                color = Lerp(color, PulseColor, _pulse);
                if (_dropTarget) color = PulseColor;
                using var fill = new SolidBrush(color);
                g.FillPath(fill, path);

                using var edgePath = RoundedRect(new RectangleF(0.5f, 0.5f, w - 1f, h - 1f), (thickness - 1f) / 2f);
                using var edge = new Pen(Lerp(EdgeColor, Color.FromArgb(200, DockColors.AccentLight), _pulse), 1f);
                g.DrawPath(edge, edgePath);

                // 収納方向を示す小さなシェブロン。
                bool vertical = Edge is DockEdge.Left or DockEdge.Right;
                float cx = vertical ? w / 2f : w - thickness / 2f - 2f;
                float cy = vertical ? thickness / 2f + 2f : h / 2f;
                bool forward = Edge is DockEdge.Right or DockEdge.Bottom;
                if (_expanded) forward = !forward;
                float halfWidth = Math.Clamp(thickness * 0.18f, 2.6f, 3.7f);
                float halfHeight = Math.Clamp(thickness * 0.14f, 2.2f, 3.1f);
                using var arrow = new Pen(Color.FromArgb(235, 255, 255, 255), 1.25f)
                {
                    StartCap = LineCap.Round,
                    EndCap = LineCap.Round,
                    LineJoin = LineJoin.Round,
                };
                PointF[] points = vertical
                    ? new[]
                    {
                        new PointF(cx + (forward ? -halfWidth : halfWidth), cy - halfHeight),
                        new PointF(cx + (forward ? halfWidth : -halfWidth), cy),
                        new PointF(cx + (forward ? -halfWidth : halfWidth), cy + halfHeight),
                    }
                    : new[]
                    {
                        new PointF(cx - halfWidth, cy + (forward ? -halfHeight : halfHeight)),
                        new PointF(cx, cy + (forward ? halfHeight : -halfHeight)),
                        new PointF(cx + halfWidth, cy + (forward ? -halfHeight : halfHeight)),
                    };
                g.DrawLines(arrow, points);

                if (_showLabel && Math.Max(w, h) > 70)
                {
                    int fontSize = Math.Clamp((int)Math.Round(thickness * 0.6f), 12, 20);
                    if (_labelFont is null || _labelFontSize != fontSize)
                    {
                        _labelFont?.Dispose();
                        _labelFont = new Font("Yu Gothic UI", fontSize, FontStyle.Regular, GraphicsUnit.Pixel);
                        _labelFontSize = fontSize;
                    }
                    g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                    using var textBrush = new SolidBrush(Color.FromArgb(250, 255, 255, 255));
                    using var format = new StringFormat(StringFormat.GenericTypographic)
                    {
                        Alignment = StringAlignment.Center,
                        LineAlignment = StringAlignment.Center,
                        Trimming = StringTrimming.EllipsisCharacter,
                        FormatFlags = StringFormatFlags.NoWrap | StringFormatFlags.MeasureTrailingSpaces,
                    };
                    // 名前や「(2)」が矢印に重ならないよう、矢印の領域を除いた範囲の中央に描く
                    float arrowZone = thickness / 2f + 2f + halfHeight + 4f;
                    if (vertical)
                    {
                        var state = g.Save();
                        g.TranslateTransform(0f, arrowZone);
                        float labelHeight = Math.Max(1f, h - arrowZone - 2f);
                        if (ContainsJapanese(_label))
                            DrawVerticalJapaneseLabel(g, textBrush, _label, w, labelHeight, thickness);
                        else
                            DrawCenteredRotatedLabel(g, textBrush, _label, w, labelHeight);
                        g.Restore(state);
                    }
                    else
                    {
                        float labelZone = thickness / 2f + 2f + halfWidth + 4f;
                        g.DrawString(_label, _labelFont, textBrush,
                            new RectangleF(2f, 0, Math.Max(1f, w - labelZone - 2f), h), format);
                    }
                }
            }

            protected override void OnDragEnter(DragEventArgs e)
            {
                base.OnDragEnter(e);
                e.Effect = DragDropEffects.None;
                _dataDragActive = true;
                if (Visible && !_expanded && !_suppressHoverUntilExit)
                {
                    SetHovered(true);
                    _hoverTimer.Start();
                }
            }

            protected override void OnDragOver(DragEventArgs e)
            {
                base.OnDragOver(e);
                e.Effect = DragDropEffects.None;
            }

            protected override void OnDragLeave(EventArgs e)
            {
                base.OnDragLeave(e);
                _hoverTimer.Stop();
                if (!Bounds.Contains(Cursor.Position)) SetHovered(false);
            }

            protected override void OnDragDrop(DragEventArgs e)
            {
                base.OnDragDrop(e);
                e.Effect = DragDropEffects.None;
                _dataDragActive = false;
                _hoverTimer.Stop();
            }

            protected override void OnMouseEnter(EventArgs e)
            {
                base.OnMouseEnter(e);
                SetHovered(true);
                if (!_expanded && !_suppressHoverUntilExit) _hoverTimer.Start();
            }

            protected override void OnMouseLeave(EventArgs e)
            {
                base.OnMouseLeave(e);
                _hoverTimer.Stop();
                if (!Bounds.Contains(Cursor.Position)) _suppressHoverUntilExit = false;
                SetHovered(false);
            }

            protected override void OnMouseDown(MouseEventArgs e)
            {
                base.OnMouseDown(e);
                _hoverTimer.Stop();
                if (e.Button == MouseButtons.Left)
                {
                    _mouseDown = Cursor.Position;
                    _dragging = false;
                }
            }

            protected override void OnMouseMove(MouseEventArgs e)
            {
                base.OnMouseMove(e);
                if (e.Button != MouseButtons.Left) return;
                var cursor = Cursor.Position;
                if (!_dragging && Math.Abs(cursor.X - _mouseDown.X) <= SystemInformation.DragSize.Width / 2
                    && Math.Abs(cursor.Y - _mouseDown.Y) <= SystemInformation.DragSize.Height / 2) return;
                _dragging = true;
                RepositionRequested?.Invoke(cursor);
            }

            protected override void OnMouseUp(MouseEventArgs e)
            {
                base.OnMouseUp(e);
                if (e.Button != MouseButtons.Left) return;
                if (_dragging)
                {
                    _dragging = false;
                    RepositionFinished?.Invoke();
                }
                else
                {
                    ToggleClicked?.Invoke();
                }
            }

            protected override void OnVisibleChanged(EventArgs e)
            {
                base.OnVisibleChanged(e);
                if (Visible)
                {
                    Render();
                    return;
                }
                _animationTimer.Stop();
                _hoverTimer.Stop();
                _pulse = 0;
                _hover = 0;
                _hovered = false;
                _suppressHoverUntilExit = false;
                // 次に表示したとき前回の状態が一瞬映らないよう、初期状態を描いておく
                Render();
            }
        }
    }
}
