using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.IO;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ExplorerDocks
{
    internal enum DockPlacementMode
    {
        Aligned = 0,
        Independent = 1,
    }

    internal enum WorkDropAction { None, Swap, Left, Right, Above, Below }

    /// <summary>各ExplorerのDockを管理し、整列/個別配置と展開排他を制御する。</summary>
    internal sealed class DockCoordinator : IDisposable
    {
        private readonly Dictionary<IntPtr, DockService> _docks = new();
        private readonly Dictionary<IntPtr, DockEntry> _entries = new();
        private readonly List<DockEntry> _pendingRestore = new();
        private readonly HashSet<string> _workNames = new(StringComparer.OrdinalIgnoreCase);
        private bool _propagating;
        private bool _changingGroupPin;
        private bool _restoring;
        private readonly System.Windows.Forms.Timer _maintenance = new() { Interval = 900 };
        private bool _animationsEnabled = true;
        private bool _showLabels = true;
        private DockPlacementMode _placementMode = DockPlacementMode.Aligned;
        private int _collapseDelayMs = 280;
        private bool _disposed;
        private bool _suspendSaving;
        private int _maintenanceCount;
        private readonly Win32.WinEventDelegate _moveSizeEvent;
        private readonly IntPtr _moveSizeHook;
        private readonly Win32.WinEventDelegate _locationEvent;
        private readonly IntPtr _locationHook;
        private readonly HashSet<IntPtr> _resizingWorkExplorers = new();
        private readonly System.Windows.Forms.Timer _workResizeTimer = new() { Interval = 16 };
        private readonly Dictionary<string, DockService> _pendingWorkResizes = new();
        private readonly object _resizePositionsGate = new();
        private (IntPtr Window, uint Thread, uint Process, Rectangle Bounds)[]? _pendingResizePositions;
        private Task? _resizePositionsTask;
        private readonly HashSet<IntPtr> _movingExplorers = new();
        private readonly System.Windows.Forms.Timer _dropTimer = new() { Interval = 1 };
        private readonly System.Windows.Forms.Timer _dragHoverTimer = new() { Interval = 70 };
        private readonly System.Windows.Forms.Timer _snapResumeTimer = new() { Interval = 1 };
        private IntPtr _droppedExplorer;
        private Point _dropPoint;
        private readonly Dictionary<IntPtr, IntPtr> _handleDropTargets = new();
        private readonly DockService.DockTransitionForm _workDropPreview = new();

        public event Action<string>? StatusChanged;
        public event Action<IntPtr, bool>? DockVisibilityChanged;
        public event Action<IntPtr>? WorkRequested;
        public event Action<IntPtr>? WorkCreateRequested;
        public event Action<IntPtr>? RenameRequested;

        public DockCoordinator() : this(DockProfiles.Read())
        {
        }

        internal DockCoordinator(IEnumerable<DockEntry> saved)
        {
            _pendingRestore.AddRange(saved);
            foreach (var name in DockProfiles.ReadWorkNames()) _workNames.Add(name);
            foreach (var entry in _pendingRestore)
                if (entry.Kind == DockGroupKind.Work && !string.IsNullOrWhiteSpace(entry.WorkName))
                    _workNames.Add(entry.WorkName);
            _maintenance.Tick += (_, _) => Maintain();
            _maintenance.Start();
            _workResizeTimer.Tick += (_, _) => FlushWorkResizes();
            _snapResumeTimer.Tick += (_, _) =>
            {
                _snapResumeTimer.Stop();
                ResumeWindowSnapping();
            };
            _moveSizeEvent = OnWindowMoveSize;
            _moveSizeHook = Win32.SetWinEventHook(
                Win32.EVENT_SYSTEM_MOVESIZESTART, Win32.EVENT_SYSTEM_MOVESIZEEND,
                IntPtr.Zero, _moveSizeEvent, 0, 0,
                Win32.WINEVENT_OUTOFCONTEXT | Win32.WINEVENT_SKIPOWNPROCESS);
            _locationEvent = OnExplorerLocationChanged;
            _locationHook = Win32.SetWinEventHook(
                Win32.EVENT_OBJECT_LOCATIONCHANGE, Win32.EVENT_OBJECT_LOCATIONCHANGE,
                IntPtr.Zero, _locationEvent, 0, 0,
                Win32.WINEVENT_OUTOFCONTEXT | Win32.WINEVENT_SKIPOWNPROCESS);
            _dragHoverTimer.Tick += (_, _) => UpdateDropTargets();
            _dropTimer.Tick += (_, _) =>
            {
                _dropTimer.Stop();
                var source = _droppedExplorer;
                var point = _dropPoint;
                _droppedExplorer = IntPtr.Zero;
                try { JoinWorkByDrop(source, point); }
                catch (Exception ex) { StatusChanged?.Invoke($"Workへの追加に失敗しました: {ex.Message}"); }
            };
        }

        public bool IsDocked => _docks.Count != 0;
        public bool Contains(IntPtr hwnd) => _docks.ContainsKey(hwnd);
        public bool IsPinned(IntPtr hwnd) => _docks.TryGetValue(hwnd, out var dock) && dock.Pinned;

        public void ShowContextMenu(IntPtr hwnd, Point location)
        {
            if (_docks.TryGetValue(hwnd, out var dock)) dock.ShowContextMenu(location);
        }

        public void TogglePin(IntPtr hwnd)
        {
            if (!_docks.TryGetValue(hwnd, out var dock)) return;

            bool wasPinned = dock.Pinned;
            dock.Pinned = !wasPinned;
            if (!wasPinned) return;

            // 解除は単なる自動収納の再開ではなく、クリック操作として収納する。
            // Work内では所属するウィンドウもまとめて収納する。
            if (!_entries.TryGetValue(hwnd, out var state) || state.Kind != DockGroupKind.Work)
            {
                dock.CollapseAfterUnpin();
                return;
            }
            foreach (var (memberHwnd, member) in _docks)
                if (_entries.TryGetValue(memberHwnd, out var entry)
                    && entry.Kind == DockGroupKind.Work && entry.GroupId == state.GroupId)
                    member.CollapseAfterUnpin();
        }
        public string[] WorkNames => _workNames
            .Concat(_entries.Values.Concat(_pendingRestore)
                .Where(entry => entry.Kind == DockGroupKind.Work)
                .Select(entry => entry.WorkName))
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();

        public (IntPtr Window, string Folder)[] WorkMembers(string name) =>
            _entries.Where(p => p.Value.Kind == DockGroupKind.Work
                && string.Equals(p.Value.WorkName, name, StringComparison.OrdinalIgnoreCase)
                && _docks.ContainsKey(p.Key))
            .Select(p => (p.Key, ExplorerFolders.ForWindow(p.Key) is { } path
                ? (string.Equals(path, ExplorerFolders.ThisPc, StringComparison.OrdinalIgnoreCase)
                    ? "PC" : Path.GetFileName(Path.TrimEndingDirectorySeparator(path))) : Win32.GetTitleOf(p.Key)))
            .ToArray();

        public void RenameWork(string previousName, string name)
        {
            name = NormalizeWorkName(name);
            if (!_workNames.Contains(previousName)) return;
            if (string.Equals(previousName, name, StringComparison.Ordinal)) return;
            if (_workNames.Contains(name) && !string.Equals(previousName, name, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("同じ名前のWorkが既にあります。");
            var names = new HashSet<string>(_workNames, StringComparer.OrdinalIgnoreCase);
            names.Remove(previousName);
            names.Add(name);
            DockProfiles.WriteWorkNames(names);
            _workNames.Clear();
            _workNames.UnionWith(names);
            foreach (var entry in _entries.Values.Concat(_pendingRestore))
                if (entry.Kind == DockGroupKind.Work
                    && string.Equals(entry.WorkName, previousName, StringComparison.OrdinalIgnoreCase))
                    entry.WorkName = name;
            UpdateGroups();
            SaveSessions();
        }

        public void DeleteWork(string name)
        {
            if (!_workNames.Contains(name)) return;
            var released = _entries.Where(p => p.Value.Kind == DockGroupKind.Work
                && string.Equals(p.Value.WorkName, name, StringComparison.OrdinalIgnoreCase)
                && _docks.ContainsKey(p.Key)).Select(p => _docks[p.Key]).ToArray();
            var names = new HashSet<string>(_workNames, StringComparer.OrdinalIgnoreCase);
            names.Remove(name);
            DockProfiles.WriteWorkNames(names);
            _workNames.Clear();
            _workNames.UnionWith(names);
            // Workだけを解除し、登録フォルダーのDockはそのまま残す。
            foreach (var entry in _entries.Values.Concat(_pendingRestore))
                if (entry.Kind == DockGroupKind.Work
                    && string.Equals(entry.WorkName, name, StringComparison.OrdinalIgnoreCase))
                {
                    entry.Kind = DockGroupKind.None;
                    entry.GroupId = "";
                    entry.WorkName = "";
                }
            UpdateGroups();
            if (_placementMode == DockPlacementMode.Aligned) ArrangeHandles();
            else SpreadReleasedHandles(released);
            SaveSessions();
            StatusChanged?.Invoke($"Work「{name}」を削除しました。");
        }

        private static string NormalizeWorkName(string value)
        {
            string name = value.Trim().Replace('\r', ' ').Replace('\n', ' ');
            if (name.Length is < 1 or > 64)
                throw new ArgumentException("Work名は1～64文字で指定してください。");
            return name;
        }

        public void CreateWork(string name)
        {
            name = NormalizeWorkName(name);
            if (!_workNames.Add(name))
                throw new InvalidOperationException("同じ名前のWorkが既にあります。");
            try { DockProfiles.WriteWorkNames(_workNames); }
            catch
            {
                _workNames.Remove(name);
                throw;
            }
            StatusChanged?.Invoke($"Work「{name}」を作成しました。");
        }
        public (IntPtr Window, string Folder)[] DockedFolders =>
            _docks.Keys.Select(h => (h, Win32.GetTitleOf(h))).ToArray();

        public string HandleName(IntPtr hwnd)
        {
            if (!_entries.TryGetValue(hwnd, out var state)) return "";
            return state.Kind == DockGroupKind.Work ? state.WorkName :
                string.IsNullOrWhiteSpace(state.CustomLabel) ? Win32.GetTitleOf(hwnd) : state.CustomLabel;
        }

        public void RenameHandle(IntPtr hwnd, string name)
        {
            if (!_entries.TryGetValue(hwnd, out var state)) return;
            name = name.Trim().Replace('\r', ' ').Replace('\n', ' ');
            if (name.Length is < 1 or > 64)
                throw new ArgumentException("名前は1～64文字で指定してください。");
            if (state.Kind == DockGroupKind.Work)
                RenameWork(state.WorkName, name);
            else state.CustomLabel = name;
            UpdateGroups();
            SaveSessions();
        }

        public DockPlacementMode PlacementMode
        {
            get => _placementMode;
            set
            {
                if (!Enum.IsDefined(value)) throw new ArgumentOutOfRangeException(nameof(value));
                if (_placementMode == value) return;
                _placementMode = value;
                foreach (var dock in _docks.Values)
                {
                    dock.UseIndependentPosition = value == DockPlacementMode.Independent;
                    if (value == DockPlacementMode.Independent)
                    {
                        dock.SetHandleMaxSpan(int.MaxValue);
                        dock.ShowLabels = _showLabels;
                    }
                }
                UpdateSharedPositionOwner();
                UpdateGroups();
                if (value == DockPlacementMode.Aligned) ArrangeHandles();
                else
                {
                    // 前回の個別位置を戻したあと、同じフォルダー名などによる重なりだけ解消する。
                    foreach (var dock in _docks.Values.Where(d => d.IsDocked))
                        PlaceNewIndependentHandle(dock);
                    SyncWorkMembers();
                    RaiseAllHandles();
                }
                SaveSessions();
            }
        }

        public bool AnimationsEnabled
        {
            get => _animationsEnabled;
            set
            {
                _animationsEnabled = value;
                foreach (var dock in _docks.Values) dock.AnimationsEnabled = value;
            }
        }

        public int CollapseDelayMs
        {
            get => _collapseDelayMs;
            set
            {
                _collapseDelayMs = Math.Clamp(value, -1, 10000);
                foreach (var dock in _docks.Values)
                    dock.CollapseDelayMs = _collapseDelayMs;
            }
        }

        public bool ShowLabels
        {
            get => _showLabels;
            set
            {
                if (_showLabels == value) return;
                _showLabels = value;
                foreach (var dock in _docks.Values) dock.ShowLabels = value;
                ArrangeHandles();
            }
        }

        public void Dock(IntPtr hwnd, DockEntry? restore = null)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(DockCoordinator));
            if (_docks.ContainsKey(hwnd)) return;
            if (!Win32.IsExplorerWindow(hwnd)) throw new InvalidOperationException("対象はエクスプローラーではありません。");

            var dock = new DockService(useSavedPosition: _docks.Count == 0)
            {
                UseIndependentPosition = _placementMode == DockPlacementMode.Independent,
                AnimationsEnabled = _animationsEnabled,
                CollapseDelayMs = _collapseDelayMs,
                ShowLabels = _showLabels,
            };
            dock.PersistSharedPosition = _placementMode == DockPlacementMode.Aligned && _docks.Count == 0;
            dock.StatusChanged += RelayStatus;
            dock.DockVisibilityChanged += RelayVisibility;
            dock.Expanded += OnExpanded;
            dock.Expanding += OnExpanding;
            dock.Collapsing += OnCollapsing;
            dock.GroupCursorInside = point => _entries.TryGetValue(hwnd, out var own) &&
                own.Kind != DockGroupKind.None && _docks.Any(e =>
                {
                    if (e.Key == hwnd || !_entries.TryGetValue(e.Key, out var group)) return false;
                    if (group.Kind != own.Kind || group.GroupId != own.GroupId) return false;
                    var other = e.Value;
                    if (!other.IsExpanded || !Win32.GetWindowRect(e.Key, out var r)) return false;
                    var rect = Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
                    rect.Inflate(16, 16); // Work内のウィンドウ間の小さな隙間も移動可能にする。
                    return rect.Contains(point);
                });
            dock.HandlePositionChanged += OnHandlePositionChanged;
            dock.DockTargetRequested += OnDockTargetRequested;
            dock.HandleDragMoved += OnHandleDragMoved;
            dock.HandleDragFinished += OnHandleDragFinished;
            dock.WorkDissolveRequested += source =>
            {
                if (CurrentWorkName(source.Window) is { } name) DissolveWork(name);
            };
            dock.LayoutChanged += OnLayoutChanged;
            dock.WorkWindowMoved += OnWorkWindowMoved;
            dock.WorkArrangementRequested += OnWorkArrangementRequested;
            dock.WorkSizeSyncRequested += OnWorkSizeSyncRequested;
            dock.WorkSizeChanged += OnWorkSizeChanged;
            dock.WorkResizeCompleted += OnWorkResizeCompleted;
            dock.UndockAllRequested += OnUndockAllRequested;
            dock.PinnedChanged += OnPinnedChanged;
            dock.HandleToggleRequested += OnHandleToggleRequested;
            dock.WorkRequested += d => WorkRequested?.Invoke(d.Window);
            dock.WorkCreateRequested += d => WorkCreateRequested?.Invoke(d.Window);
            dock.RenameRequested += d => RenameRequested?.Invoke(d.Window);
            _docks.Add(hwnd, dock);
            if (restore is not null && (int)restore.Kind == 1)
            {
                restore.Kind = DockGroupKind.None;
                restore.GroupId = "";
                restore.WorkName = "";
            }
            // 非表示にする前にフォルダーを記録する。閉じた後もWorkの登録先を復元できるようにする。
            string? currentFolder = restore?.Path ?? ExplorerFolders.ForWindow(hwnd);
            _entries[hwnd] = restore ?? new DockEntry { Path = currentFolder ?? "" };
            // 手動Dockで同じフォルダーを開いても、別Workの復元待ち登録は消さない。
            if (restore is null && currentFolder is not null)
            {
                int index = _pendingRestore.FindIndex(e => e.Kind == DockGroupKind.None &&
                    string.Equals(e.Path, currentFolder, StringComparison.OrdinalIgnoreCase));
                if (index >= 0) _pendingRestore.RemoveAt(index);
            }
            try
            {
                dock.Dock(hwnd);
                if (restore is not null)
                {
                    if (restore.Width > 0 && restore.Height > 0)
                        dock.SetPreferredDockSize(new Size(restore.Width, restore.Height));
                    dock.RestorePosition(restore.Screen, restore.Edge, restore.Position);
                    dock.Pinned = restore.Pinned;
                }
                if (_placementMode == DockPlacementMode.Aligned && !_restoring)
                {
                    var first = _docks.Values.FirstOrDefault(d => d.IsDocked && !ReferenceEquals(d, dock));
                    if (first is not null)
                        dock.SetDockTarget(first.DockWorkArea, first.Edge, first.HandleCenter);
                }
                else if (restore is null)
                {
                    PlaceNewIndependentHandle(dock);
                    dock.SaveCurrentDockPosition();
                }
                bool vertical = dock.Edge is DockEdge.Left or DockEdge.Right;
                int workSpan = vertical ? dock.DockWorkArea.Height : dock.DockWorkArea.Width;
                int count = _docks.Values.Count(d => d.IsDocked && d.DockWorkArea == dock.DockWorkArea
                    && d.Edge == dock.Edge);
                if (count * 30 > workSpan)
                    throw new InvalidOperationException("この画面端にはこれ以上Dockを追加できません。別の辺かモニターを使用してください。");
                UpdateSharedPositionOwner();
                UpdateGroups();
                if (_placementMode == DockPlacementMode.Aligned) ArrangeHandles();
                else RaiseAllHandles();
                SaveSessions();
            }
            catch
            {
                _docks.Remove(hwnd);
                _entries.Remove(hwnd);
                dock.Dispose();
                throw;
            }
        }

        public void Undock(IntPtr hwnd)
        {
            FinishResizePositions();
            if (!_docks.Remove(hwnd, out var dock)) return;
            _workFrameGaps.Remove(hwnd);
            _resizingWorkExplorers.Remove(hwnd);
            _entries.Remove(hwnd);
            try { dock.Undock(); }
            finally
            {
                dock.Dispose();
                UpdateSharedPositionOwner();
                UpdateGroups();
                if (_placementMode == DockPlacementMode.Aligned) ArrangeHandles();
                SaveSessions();
            }
        }

        public void UndockAll()
        {
            foreach (var hwnd in _docks.Keys.ToArray()) Undock(hwnd);
        }

        public void PrepareForExplorerRestart()
        {
            if (_docks.Count == 0) return;
            // 記録に失敗した場合、Windows Explorerを再起動して保存済みWorkを失わせない。
            if (!SaveSessions())
                throw new IOException("Workの保存に失敗したため、Explorerの再起動を中止しました。");
            foreach (var entry in _entries.Values) AddPendingRestore(entry);
            _suspendSaving = true;
            try { UndockAll(); }
            finally { _suspendSaving = false; }
        }

        public void RestoreLeftoverWindows() => DockService.RestoreLeftoverWindows();

        private void RelayStatus(string text) => StatusChanged?.Invoke(text);
        private void RelayVisibility(IntPtr hwnd, bool visible) => DockVisibilityChanged?.Invoke(hwnd, visible);

        private void OnUndockAllRequested(DockService source)
        {
            if (_docks.Values.Contains(source)) UndockAll();
        }

        private void OnPinnedChanged(DockService source)
        {
            if (_changingGroupPin) return;
            _changingGroupPin = true;
            try
            {
                if (_entries.TryGetValue(source.Window, out var state)
                    && state.Kind == DockGroupKind.Work)
                {
                    foreach (var (hwnd, dock) in _docks)
                        if (hwnd != source.Window && _entries.TryGetValue(hwnd, out var entry)
                            && entry.Kind == DockGroupKind.Work && entry.GroupId == state.GroupId)
                            dock.Pinned = source.Pinned;
                }
            }
            finally { _changingGroupPin = false; }
            SaveSessions();
        }

        private void OnExpanding(DockService active)
        {
            LayoutWorkGroups();
            if (_propagating || !_entries.TryGetValue(active.Window, out var state)) return;
            _propagating = true;
            try
            {
                var members = _entries.Where(p => state.Kind != DockGroupKind.None
                    && p.Value.Kind == state.Kind && p.Value.GroupId == state.GroupId)
                    .Select(p => p.Key).ToHashSet();
                members.Add(active.Window);
                foreach (var (hwnd, dock) in _docks)
                    if (!members.Contains(hwnd)) dock.CollapseFromCoordinator();
                foreach (var hwnd in members)
                    if (hwnd != active.Window && _docks.TryGetValue(hwnd, out var dock))
                        dock.ExpandFromCoordinator();
            }
            finally { _propagating = false; }
        }

        private void OnExpanded(DockService active)
        {
            if (_propagating || !_entries.TryGetValue(active.Window, out var state)) return;
            _propagating = true;
            try
            {
                var members = _entries.Where(p => state.Kind == DockGroupKind.Work
                    && p.Value.Kind == state.Kind && p.Value.GroupId == state.GroupId)
                    .Select(p => p.Key).ToHashSet();
                members.Add(active.Window);
                foreach (var (hwnd, dock) in _docks)
                    if (!members.Contains(hwnd)) dock.CollapseFromCoordinator();
                foreach (var hwnd in members)
                    if (_docks.TryGetValue(hwnd, out var member) && !ReferenceEquals(member, active))
                        member.ExpandFromCoordinator();
            }
            finally { _propagating = false; }
            RaiseAllHandles();
        }

        private void OnHandleToggleRequested(DockService source)
        {
            if (!_entries.TryGetValue(source.Window, out var state) || state.Kind != DockGroupKind.Work)
            {
                source.ToggleFromHandle();
                return;
            }

            // Workのハンドルはひとつ。クリックでは固定の有無にかかわらず全メンバーを切り替える。
            var members = _entries.Where(pair => pair.Value.Kind == DockGroupKind.Work
                    && pair.Value.GroupId == state.GroupId)
                .Select(pair => pair.Key)
                .Where(_docks.ContainsKey)
                .Select(hwnd => _docks[hwnd])
                .ToArray();
            bool collapse = source.IsTransitioningToExpanded
                || (source.IsExpanded && !source.IsTransitioningToCollapsed);
            _propagating = true;
            try
            {
                if (!collapse)
                {
                    var grouped = members.ToHashSet();
                    foreach (var dock in _docks.Values)
                        if (!grouped.Contains(dock)) dock.CollapseFromCoordinator();
                }
                foreach (var dock in members)
                {
                    if (collapse) dock.CollapseFromCoordinator(force: true);
                    else dock.ExpandFromCoordinator(manual: true);
                }
            }
            finally { _propagating = false; }
        }

        private void OnCollapsing(DockService active)
        {
            FinishResizePositions();
            if (_propagating || !_entries.TryGetValue(active.Window, out var state)
                || state.Kind == DockGroupKind.None) return;
            _propagating = true;
            try
            {
                foreach (var (hwnd, dock) in _docks)
                    if (hwnd != active.Window && _entries.TryGetValue(hwnd, out var entry)
                        && entry.Kind == state.Kind && entry.GroupId == state.GroupId)
                        dock.CollapseFromCoordinator();
            }
            finally { _propagating = false; }
        }

        private void OnWorkSizeChanged(DockService source)
        {
            if (_disposed || !_entries.TryGetValue(source.Window, out var entry) ||
                entry.Kind != DockGroupKind.Work) return;
            _pendingWorkResizes[entry.GroupId] = source;
            if (!_workResizeTimer.Enabled) _workResizeTimer.Start();
        }

        private void FlushWorkResizes()
        {
            if (_pendingWorkResizes.Count == 0)
            {
                _workResizeTimer.Stop();
                return;
            }
            var pending = _pendingWorkResizes.Values.ToArray();
            _pendingWorkResizes.Clear();
            foreach (var source in pending)
            {
                if (_disposed || !source.ResizingWorkWindow || !_docks.ContainsKey(source.Window)
                    || !_entries.TryGetValue(source.Window, out var entry)
                    || entry.Kind != DockGroupKind.Work) continue;
                if (!source.CaptureWorkResize()) continue;
                LayoutWorkGroups(entry.GroupId);
            }
        }

        private void OnWorkSizeSyncRequested(DockService source)
        {
            if (_disposed || !_entries.TryGetValue(source.Window, out var entry)
                || entry.Kind != DockGroupKind.Work) return;
            bool enabled = !entry.SyncWindowSizes;
            foreach (var member in _entries.Values.Concat(_pendingRestore).Where(e =>
                e.Kind == DockGroupKind.Work && e.GroupId == entry.GroupId))
                member.SyncWindowSizes = enabled;
            var members = _entries.Where(e => e.Value.Kind == DockGroupKind.Work
                    && e.Value.GroupId == entry.GroupId && _docks.ContainsKey(e.Key))
                .Select(e => _docks[e.Key]).ToArray();
            foreach (var member in members) member.WorkSizeSyncEnabled = enabled;
            if (enabled)
            {
                var sizes = SyncWorkSizes(members.Select(d => d.PreferredDockSize).ToArray(),
                    Array.IndexOf(members, source));
                for (int i = 0; i < members.Length; i++) members[i].SetPreferredDockSize(sizes[i]);
            }
            LayoutWorkGroups();
            SaveSessions();
            StatusChanged?.Invoke(enabled ? "Workのウィンドウサイズ同期をオンにしました。"
                : "Workのウィンドウサイズ同期をオフにしました。");
        }

        private void OnWorkResizeCompleted(DockService source)
        {
            if (_disposed) return;
            FinishResizePositions();
            _resizingWorkExplorers.Remove(source.Window);
            if (_entries.TryGetValue(source.Window, out var entry))
            {
                _pendingWorkResizes.Remove(entry.GroupId);
                source.CaptureWorkResize();
                if (entry.SyncWindowSizes)
                {
                    var size = source.PreferredDockSize;
                    foreach (var (hwnd, member) in _docks)
                        if (_entries.TryGetValue(hwnd, out var other) && other.Kind == DockGroupKind.Work
                            && other.GroupId == entry.GroupId) member.SetPreferredDockSize(size);
                }
                source.EndWorkResize();
                LayoutWorkGroups(entry.GroupId);
            }
            if (_pendingWorkResizes.Count == 0) _workResizeTimer.Stop();
            SaveSessions();
        }

        private void OnWorkWindowMoved(DockService source, Rectangle moved)
        {
            var drop = FindWorkDrop(source.Window, Cursor.Position);
            _workDropPreview.Hide();
            if (drop.Target is null)
            {
                source.ReapplyWorkBounds();
                return;
            }
            ApplyWorkDrop(source, drop.Target, drop.Action);
        }

        private void ApplyWorkDrop(DockService source, DockEntry target, WorkDropAction action)
        {
            if (_disposed || !_entries.TryGetValue(source.Window, out var movedEntry)
                || movedEntry.Kind != DockGroupKind.Work)
            {
                source.ReapplyWorkBounds();
                return;
            }
            var group = _entries.Where(e => e.Value.Kind == DockGroupKind.Work
                    && e.Value.GroupId == movedEntry.GroupId && _docks.ContainsKey(e.Key))
                .OrderBy(e => e.Value.Slot).ToList();

            if (!group.Any(e => ReferenceEquals(e.Value, target)) || ReferenceEquals(movedEntry, target)
                || action == WorkDropAction.None)
            {
                source.ReapplyWorkBounds();
                return;
            }

            bool changed = false;
            bool stacked = action is WorkDropAction.Above or WorkDropAction.Below;
            if (action == WorkDropAction.Swap)
            {
                changed = movedEntry.Slot != target.Slot;
                (movedEntry.Slot, target.Slot) = (target.Slot, movedEntry.Slot);
            }
            else
            {
                bool after = action is WorkDropAction.Right or WorkDropAction.Below;
                var order = group.Select(e => e.Value).Where(e => !ReferenceEquals(e, movedEntry)).ToList();
                order.Insert(order.IndexOf(target) + (after ? 1 : 0), movedEntry);
                for (int i = 0; i < order.Count; i++)
                {
                    if (order[i].Slot != i || order[i].Stacked != stacked) changed = true;
                    order[i].Slot = i;
                    order[i].Stacked = stacked;
                }
            }
            LayoutWorkGroups();
            source.ReapplyWorkBounds();
            if (!changed) return;
            SaveSessions();
            StatusChanged?.Invoke(action == WorkDropAction.Swap ? "ウィンドウの位置を交換しました。"
                : stacked ? "Workを上下に並べました。" : "Workを左右に並べました。");
        }

        internal static WorkDropAction GetWorkDropAction(Rectangle target, Point point)
        {
            if (target.Width <= 0 || target.Height <= 0) return WorkDropAction.None;
            var near = target;
            near.Inflate(48, 48);
            if (!near.Contains(point)) return WorkDropAction.None;
            double x = (point.X - target.Left) / (double)target.Width - 0.5;
            double y = (point.Y - target.Top) / (double)target.Height - 0.5;
            if (Math.Abs(x) <= 0.25 && Math.Abs(y) <= 0.25) return WorkDropAction.Swap;
            return Math.Abs(y) > Math.Abs(x)
                ? y < 0 ? WorkDropAction.Above : WorkDropAction.Below
                : x < 0 ? WorkDropAction.Left : WorkDropAction.Right;
        }

        private (DockEntry? Target, Rectangle Bounds, WorkDropAction Action) FindWorkDrop(IntPtr source, Point point)
        {
            if (!_entries.TryGetValue(source, out var own) || own.Kind != DockGroupKind.Work)
                return default;
            (DockEntry? Target, Rectangle Bounds, WorkDropAction Action) result = default;
            double best = double.MaxValue;
            foreach (var (hwnd, entry) in _entries)
            {
                if (hwnd == source || entry.Kind != DockGroupKind.Work || entry.GroupId != own.GroupId
                    || !_docks.TryGetValue(hwnd, out var dock) || !dock.IsExpanded
                    || dock.WorkBounds is not Rectangle bounds) continue;
                var action = GetWorkDropAction(bounds, point);
                if (action == WorkDropAction.None) continue;
                double dx = point.X - (bounds.Left + bounds.Width / 2.0);
                double dy = point.Y - (bounds.Top + bounds.Height / 2.0);
                double distance = dx * dx + dy * dy;
                if (distance >= best) continue;
                best = distance;
                result = (entry, bounds, action);
            }
            return result;
        }

        /// <summary>右クリックメニューからWorkの並べ方(横/縦)を切り替える。</summary>
        private void OnWorkArrangementRequested(DockService source, bool stacked)
        {
            if (_disposed || !_entries.TryGetValue(source.Window, out var entry)
                || entry.Kind != DockGroupKind.Work) return;
            foreach (var member in _entries.Values.Where(e => e.Kind == DockGroupKind.Work
                && e.GroupId == entry.GroupId))
                member.Stacked = stacked;
            LayoutWorkGroups();
            SaveSessions();
            StatusChanged?.Invoke(stacked ? "Workを縦に並べました。" : "Workを横に並べました。");
        }

        private void OnLayoutChanged(DockService source)
        {
            if (_placementMode == DockPlacementMode.Aligned) ArrangeHandles();
            else
            {
                LayoutWorkGroups();
                RaiseAllHandles();
            }
            SaveSessions();
        }

        private void OnHandlePositionChanged(DockService source)
        {
            // Workのハンドルをドラッグした場合は、先頭以外のメンバーも同じモニターへ移す。
            RelocateWorkMembers(source, source.DockWorkArea, source.Edge, source.HandleCenter);
            if (_placementMode == DockPlacementMode.Aligned) ArrangeHandles();
            else
            {
                SyncWorkMembers();
                RaiseAllHandles();
            }
            SaveSessions();
        }

        private void OnDockTargetRequested(DockService source, Rectangle work, DockEdge edge, int center)
        {
            if (_disposed || !source.IsDocked || work.IsEmpty) return;
            source.SetDockTarget(work, edge, center);
            source.SaveCurrentDockPosition();
            RelocateWorkMembers(source, work, edge, center);
            if (_placementMode == DockPlacementMode.Aligned) ArrangeHandles();
            else
            {
                SyncWorkMembers();
                RaiseAllHandles();
            }
            // 位置はExplorerごとに保持し、Workの閉じたメンバーも復元先を更新する。
            SaveSessions();
        }

        private void RelocateWorkMembers(DockService source, Rectangle work, DockEdge edge, int center)
        {
            if (!_entries.TryGetValue(source.Window, out var state) ||
                state.Kind != DockGroupKind.Work || string.IsNullOrEmpty(state.GroupId)) return;
            foreach (var (hwnd, dock) in _docks)
            {
                if (ReferenceEquals(dock, source)) continue;
                if (_entries.TryGetValue(hwnd, out var member) &&
                    member.Kind == DockGroupKind.Work && member.GroupId == state.GroupId)
                {
                    dock.SetDockTarget(work, edge, center);
                    dock.SaveCurrentDockPosition();
                }
            }
            // 閉じているWorkメンバーも次に開くと同じモニターへ復元される。
            var screenName = Screen.FromRectangle(work).DeviceName;
            double position = DockService.PositionFraction(work, edge, center);
            foreach (var entry in _pendingRestore)
                if (entry.Kind == DockGroupKind.Work && entry.GroupId == state.GroupId)
                {
                    entry.Screen = screenName;
                    entry.Edge = edge;
                    entry.Position = position;
                }
        }

        private void Maintain()
        {
            if (_disposed) return;
            bool changed = false;
            foreach (var (hwnd, dock) in _docks.ToArray())
            {
                if (Win32.IsWindow(hwnd) && dock.IsDocked) continue;
                // 閉じたExplorerはWorkから削除せず、次のWork選択時に復元する。
                // 明示的なDock解除はUndock()で処理し、従来どおり登録を外す。
                if (_entries.TryGetValue(hwnd, out var entry)
                    && entry.Kind == DockGroupKind.Work
                    && !string.IsNullOrWhiteSpace(entry.Path))
                    AddPendingRestore(entry);
                _workFrameGaps.Remove(hwnd);
                _resizingWorkExplorers.Remove(hwnd);
                _docks.Remove(hwnd);
                _entries.Remove(hwnd);
                dock.Dispose();
                changed = true;
            }
            if (!changed && _docks.Count > 0 && ++_maintenanceCount % 30 == 0
                && !_docks.Values.Any(d => d.ResizingWorkWindow)) SaveSessions();
            if (changed)
            {
                UpdateSharedPositionOwner();
                if (_placementMode == DockPlacementMode.Aligned) ArrangeHandles();
                UpdateGroups();
                SaveSessions();
                StatusChanged?.Invoke($"Dock: {_docks.Count}件");
            }
        }

        private void UpdateSharedPositionOwner()
        {
            var owner = _docks.Values.FirstOrDefault(d => d.IsDocked);
            foreach (var dock in _docks.Values)
                dock.PersistSharedPosition = _placementMode == DockPlacementMode.Aligned
                    && ReferenceEquals(dock, owner);
        }

        private void RaiseAllHandles()
        {
            foreach (var dock in _docks.Values.Where(d => d.HandleEnabled)) dock.RaiseHandle();
        }

        private void ArrangeHandles()
        {
            if (_placementMode != DockPlacementMode.Aligned) return;
            // 整列はモニターと辺ごと。隣接モニターの境界側も別々のDockとして維持する。
            // 全Dockを先頭のモニター・辺に移すと、別モニターへドラッグできなくなる。
            var anchor = _docks.Values.FirstOrDefault(d => d.IsDocked && d.HandleEnabled);
            if (anchor is null) return;
            // 同じモニター・同じ辺のDockを一列に整列する。
            foreach (var group in _docks.Values.Where(d => d.IsDocked && d.HandleEnabled)
                .GroupBy(d => (d.DockWorkArea, d.Edge)))
            {
                var docks = group.OrderBy(d => d.HandleCenter).ToArray();
                if (docks.Length == 1)
                {
                    docks[0].SetHandleMaxSpan(int.MaxValue);
                    docks[0].ShowLabels = _showLabels;
                    continue;
                }
                bool vertical = group.Key.Edge is DockEdge.Left or DockEdge.Right;
                var work = group.Key.DockWorkArea;
                int start = vertical ? work.Top : work.Left;
                int end = vertical ? work.Bottom : work.Right;
                int capacity = end - start;
                if (capacity <= 0) continue;

                // 画面端が混み合う場合だけ自動的にコンパクト表示に切り替える。
                bool showNames = _showLabels &&
                    docks.Sum(d => d.LabeledHandleSpan + 6) <= capacity;
                foreach (var dock in docks) dock.ShowLabels = showNames;

                int spanLimit = Math.Max(24, capacity / docks.Length - 6);
                foreach (var dock in docks) dock.SetHandleMaxSpan(spanLimit);
                int maxSpan = docks.Max(d => d.HandleSpan);
                int spacing = Math.Min(Math.Max(maxSpan + 24, 200),
                    Math.Max(1, capacity / docks.Length));
                int first = (int)Math.Round(docks.Average(d => (double)d.HandleCenter)
                    - (docks.Length - 1) * spacing / 2.0);
                int minFirst = start + maxSpan / 2;
                int maxFirst = end - (maxSpan + 1) / 2 - (docks.Length - 1) * spacing;
                first = Math.Clamp(first, minFirst, Math.Max(minFirst, maxFirst));
                for (int i = 0; i < docks.Length; ++i)
                    docks[i].SetHandleCenter(first + i * spacing);
            }
            SyncWorkMembers();
            anchor.SaveCurrentDockPosition();
            RaiseAllHandles();
        }

        // Workを解除した際、元々1つの位置に重なっていたハンドルを個別に離す。
        // 既存の個別Dockの場所は動かさず、空いている場所だけを使う。
        private void SpreadReleasedHandles(DockService[] released)
        {
            foreach (var group in released.Where(d => d.IsDocked)
                .GroupBy(d => (d.DockWorkArea, d.Edge)))
            {
                var members = group.ToArray();
                if (members.Length < 2) continue;
                bool vertical = group.Key.Edge is DockEdge.Left or DockEdge.Right;
                int start = vertical ? group.Key.DockWorkArea.Top : group.Key.DockWorkArea.Left;
                int end = vertical ? group.Key.DockWorkArea.Bottom : group.Key.DockWorkArea.Right;
                int center = members[0].HandleCenter;
                int spacing = Math.Max(160, members.Max(d => d.HandleSpan) + 24);
                var occupied = _docks.Values.Where(d => d.IsDocked && d.HandleEnabled
                    && d.DockWorkArea == group.Key.DockWorkArea && d.Edge == group.Key.Edge
                    && !members.Contains(d)).ToList();

                for (int i = 0; i < members.Length; i++)
                {
                    var dock = members[i];
                    int ideal = center + (int)Math.Round((i - (members.Length - 1) / 2.0) * spacing);
                    int half = (dock.HandleSpan + 1) / 2;
                    int min = start + half;
                    int max = end - half;
                    if (min > max) continue;
                    ideal = Math.Clamp(ideal, min, max);
                    int destination = ideal;
                    bool found = false;
                    int step = Math.Max(12, dock.HandleSpan / 2);
                    for (int attempt = 0; attempt < (end - start) / step + 3 && !found; attempt++)
                    {
                        foreach (int offset in attempt == 0 ? new[] { 0 } : new[] { attempt * step, -attempt * step })
                        {
                            int candidate = ideal + offset;
                            if (candidate < min || candidate > max) continue;
                            if (occupied.Any(other => Math.Abs(other.HandleCenter - candidate)
                                < (other.HandleSpan + dock.HandleSpan) / 2 + 12)) continue;
                            destination = candidate;
                            found = true;
                            break;
                        }
                    }
                    if (found) dock.SetHandleCenter(destination);
                    occupied.Add(dock);
                }
            }
            RaiseAllHandles();
        }

        private void SyncWorkMembers()
        {
            foreach (var group in _entries.Where(e => e.Value.Kind == DockGroupKind.Work
                && !string.IsNullOrEmpty(e.Value.GroupId)).GroupBy(e => e.Value.GroupId))
            {
                var docks = group.Select(e => e.Key).Where(_docks.ContainsKey)
                    .Select(h => _docks[h]).ToArray();
                if (docks.Length <= 1) continue;
                for (int i = 1; i < docks.Length; i++)
                    docks[i].SetDockTarget(docks[0].DockWorkArea, docks[0].Edge, docks[0].HandleCenter);
            }
            LayoutWorkGroups();
        }

        // ハンドルを基準に、Work内のExplorerを均等な格子状に配置する。
        // 2つなら左右に1つずつ。画面幅が足りない場合だけ段を増やす。
        private void LayoutWorkGroups(string? onlyGroup = null)
        {
            if (onlyGroup is null) FinishResizePositions();
            var positions = onlyGroup is null ? new Dictionary<DockService, Rectangle>() : null;
            foreach (var group in _entries.Where(e => e.Value.Kind == DockGroupKind.Work
                && !string.IsNullOrEmpty(e.Value.GroupId)
                && (onlyGroup is null || e.Value.GroupId == onlyGroup)).GroupBy(e => e.Value.GroupId))
            {
                var ordered = group.Where(e => _docks.ContainsKey(e.Key) && _docks[e.Key].IsDocked)
                    .OrderBy(e => e.Value.Slot).ToArray();
                var members = new DockService[ordered.Length];
                bool stacked = false;
                bool syncSizes = false;
                int activeIndex = -1;
                for (int i = 0; i < ordered.Length; i++)
                {
                    var entry = ordered[i];
                    members[i] = _docks[entry.Key];
                    stacked |= entry.Value.Stacked;
                    syncSizes |= entry.Value.SyncWindowSizes;
                    if (members[i].ResizingWorkWindow) activeIndex = i;
                }
                if (members.Length <= 1) continue;

                // ハンドルの位置・辺は最初に登録したExplorerを基準にする(並び順とは別)
                var anchor = _docks[group.First(e => _docks.TryGetValue(e.Key, out var dock) && dock.IsDocked).Key];
                var work = anchor.DockWorkArea;
                int inset = Math.Max(24, (anchor.Edge is DockEdge.Left or DockEdge.Right
                    ? anchor.HandleBounds.Width : anchor.HandleBounds.Height) + 8);
                switch (anchor.Edge)
                {
                    case DockEdge.Left: work.X += inset; work.Width -= inset; break;
                    case DockEdge.Right: work.Width -= inset; break;
                    case DockEdge.Top: work.Y += inset; work.Height -= inset; break;
                    case DockEdge.Bottom: work.Height -= inset; break;
                }
                if (work.Width <= 0 || work.Height <= 0) continue;
                var sizes = new Size[members.Length];
                var syncedSize = members[Math.Max(0, activeIndex)].PreferredDockSize;
                for (int i = 0; i < members.Length; i++)
                {
                    sizes[i] = syncSizes ? syncedSize : members[i].PreferredDockSize;
                    members[i].WorkStacked = stacked;
                    members[i].WorkSizeSyncEnabled = syncSizes;
                    if (syncSizes) members[i].SetPreferredDockSize(sizes[i]);
                }
                // 透明なリサイズ枠の分だけ重ね、見た目の縁どうしが隣り合うようにする
                var (gapX, gapY) = WorkGaps(anchor.Window, activeIndex >= 0);
                var rectangles = LayoutWorkBounds(work, anchor.Edge, anchor.HandleCenter, sizes, stacked, gapX, gapY);
                if (activeIndex >= 0 && Win32.GetWindowRect(members[activeIndex].Window, out var live))
                {
                    var actual = Rectangle.FromLTRB(live.Left, live.Top, live.Right, live.Bottom);
                    if (stacked) AnchorStackedWork(rectangles, activeIndex, actual, gapY);
                    else AnchorWorkToResizedWindow(work, rectangles, activeIndex, actual, gapX);
                }
                var livePositions = positions is null && activeIndex >= 0
                    ? new List<(IntPtr Window, Rectangle Bounds)>() : null;
                for (int i = 0; i < members.Length; i++)
                {
                    if (livePositions is not null)
                    {
                        members[i].SetWorkBounds(rectangles[i], apply: false);
                        if (i != activeIndex && members[i].IsExpanded
                            && !members[i].IsTransitioningToExpanded && !members[i].IsTransitioningToCollapsed)
                            livePositions.Add((members[i].Window, rectangles[i]));
                    }
                    else if (positions is null) members[i].SetWorkBounds(rectangles[i]);
                    else positions[members[i]] = rectangles[i];
                }
                if (livePositions is { Count: > 0 }) QueueResizePositions(livePositions.ToArray());
            }
            // 同じ矩形なら再配置しない。展開中の不要な描画更新を防ぐ。
            if (positions is null) return;
            foreach (var dock in _docks.Values)
                dock.SetWorkBounds(positions.TryGetValue(dock, out var rect) ? rect : null);
        }

        private void QueueResizePositions((IntPtr Window, Rectangle Bounds)[] positions)
        {
            var pending = new List<(IntPtr Window, uint Thread, uint Process, Rectangle Bounds)>();
            foreach (var (window, bounds) in positions)
            {
                uint thread = Win32.GetWindowThreadProcessId(window, out uint process);
                if (process == Environment.ProcessId)
                {
                    Win32.SetWindowPos(window, IntPtr.Zero, bounds.X, bounds.Y, bounds.Width, bounds.Height,
                        Win32.SWP_NOACTIVATE | Win32.SWP_NOZORDER | Win32.SWP_NOOWNERZORDER);
                }
                else if (thread != 0) pending.Add((window, thread, process, bounds));
            }
            if (pending.Count == 0) return;
            lock (_resizePositionsGate)
            {
                _pendingResizePositions = pending.ToArray();
                _resizePositionsTask ??= Task.Run(ApplyResizePositionsAsync);
            }
        }

        private async Task ApplyResizePositionsAsync()
        {
            while (true)
            {
                (IntPtr Window, uint Thread, uint Process, Rectangle Bounds)[] positions;
                lock (_resizePositionsGate)
                {
                    if (_pendingResizePositions is null)
                    {
                        _resizePositionsTask = null;
                        return;
                    }
                    positions = _pendingResizePositions;
                    _pendingResizePositions = null;
                }
                long start = Stopwatch.GetTimestamp();
                foreach (var (window, thread, process, bounds) in positions)
                {
                    if (Win32.GetWindowThreadProcessId(window, out uint currentProcess) != thread
                        || currentProcess != process || !Win32.IsWindowVisible(window)) continue;
                    Win32.SetWindowPos(window, IntPtr.Zero, bounds.X, bounds.Y, bounds.Width, bounds.Height,
                        Win32.SWP_NOACTIVATE | Win32.SWP_NOZORDER | Win32.SWP_NOOWNERZORDER);
                }
                // 同じExplorerスレッドに描画要求を詰めず、入力を処理する時間を残す。
                int pause = Math.Clamp((int)Math.Ceiling(Stopwatch.GetElapsedTime(start).TotalMilliseconds * 2), 8, 80);
                await Task.Delay(pause).ConfigureAwait(false);
            }
        }

        private void FinishResizePositions()
        {
            Task? pending;
            lock (_resizePositionsGate) pending = _resizePositionsTask;
            pending?.GetAwaiter().GetResult();
        }

        // 中央寄せの再計算でドラッグ中のウィンドウ位置がずれないよう、実座標に揃える。
        // 横に並ぶ2ウィンドウでは、広げた側に合わせてもう一方を確実に押し出す。
        internal static void AnchorWorkToResizedWindow(Rectangle work, Rectangle[] bounds,
            int activeIndex, Rectangle actual, int gap)
        {
            if (activeIndex < 0 || activeIndex >= bounds.Length) return;
            var planned = bounds[activeIndex];
            int dx = actual.Left - planned.Left;
            int dy = actual.Top - planned.Top;
            bounds[activeIndex] = actual;
            for (int i = 0; i < bounds.Length; i++)
            {
                if (i == activeIndex) continue;
                bounds[i].Offset(dx, dy);
            }

            if (bounds.Length != 2) return;
            int otherIndex = 1 - activeIndex;
            var other = bounds[otherIndex];
            if (activeIndex == 0)
                other.X = actual.Right + gap;
            else
                other.X = actual.Left - gap - other.Width;
            other.Y = actual.Top;

            // 画面端で横幅が不足する場合、先に相手の幅を必要最小限に縮める。
            if (other.Left < work.Left || other.Right > work.Right)
            {
                int free = activeIndex == 0
                    ? work.Right - actual.Right - gap
                    : actual.Left - work.Left - gap;
                if (free >= Math.Min(200, other.Width))
                {
                    other.Width = Math.Min(other.Width, free);
                    other.X = activeIndex == 0 ? actual.Right + gap
                        : actual.Left - gap - other.Width;
                }
                else
                {
                    // 横に置けない場合は上下の空きへ移す。
                    int below = actual.Bottom + gap;
                    int above = actual.Top - gap - other.Height;
                    if (below + other.Height <= work.Bottom)
                        other.Y = below;
                    else if (above >= work.Top)
                        other.Y = above;
                    other.X = Math.Clamp(actual.Left, work.Left,
                        Math.Max(work.Left, work.Right - other.Width));
                }
            }
            bounds[otherIndex] = other;
        }

        // 縦積みのWorkでリサイズ中のウィンドウを基準に、上下のウィンドウを詰め直す。
        internal static void AnchorStackedWork(Rectangle[] bounds, int activeIndex, Rectangle actual, int gap)
        {
            if (activeIndex < 0 || activeIndex >= bounds.Length) return;
            int dx = actual.Left - bounds[activeIndex].Left;
            bounds[activeIndex] = actual;
            for (int i = activeIndex + 1; i < bounds.Length; i++)
                bounds[i] = new Rectangle(bounds[i].X + dx, bounds[i - 1].Bottom + gap,
                    bounds[i].Width, bounds[i].Height);
            for (int i = activeIndex - 1; i >= 0; i--)
                bounds[i] = new Rectangle(bounds[i].X + dx, bounds[i + 1].Top - gap - bounds[i].Height,
                    bounds[i].Width, bounds[i].Height);
        }

        internal static Rectangle[] CalculateWorkBounds(Rectangle work, DockEdge edge,
            int handleCenter, IReadOnlyList<Size> sizes)
            => LayoutWorkBounds(work, edge, handleCenter, sizes, stacked: false, 8, 8);

        internal static Size[] SyncWorkSizes(Size[] sizes, int sourceIndex)
        {
            if (sizes.Length == 0) return sizes;
            var source = sizes[Math.Clamp(sourceIndex, 0, sizes.Length - 1)];
            return Enumerable.Repeat(source, sizes.Length).ToArray();
        }

        private readonly Dictionary<IntPtr, (uint Dpi, int X, int Y)> _workFrameGaps = new();

        /// <summary>Windows 11の透明なリサイズ枠の幅から、見た目で隣り合う間隔(負の値)を求める。</summary>
        private (int X, int Y) WorkGaps(IntPtr hwnd, bool resizing)
        {
            uint dpi = Win32.GetDpiForWindow(hwnd);
            bool cached = _workFrameGaps.TryGetValue(hwnd, out var previous) && previous.Dpi == dpi;
            if (cached && resizing) return (previous.X, previous.Y);
            if (Win32.IsWindowVisible(hwnd) && Win32.GetWindowRect(hwnd, out var outer)
                && Win32.DwmGetWindowAttribute(hwnd, Win32.DWMWA_EXTENDED_FRAME_BOUNDS, out var visible,
                    System.Runtime.InteropServices.Marshal.SizeOf<Win32.RECT>()) == 0)
            {
                int left = Math.Max(0, visible.Left - outer.Left);
                int right = Math.Max(0, outer.Right - visible.Right);
                int top = Math.Max(0, visible.Top - outer.Top);
                int bottom = Math.Max(0, outer.Bottom - visible.Bottom);
                // 枠幅はリサイズ中は変わらない。DPI変更時と操作終了後に測り直す。
                var gaps = (X: -(left + right), Y: -(top + bottom));
                _workFrameGaps[hwnd] = (dpi, gaps.X, gaps.Y);
                return gaps;
            }
            return cached ? (previous.X, previous.Y) : (0, 0);
        }

        // 各列・行の寸法を使用する。1枚をリサイズしたときも、隣のExplorerを詰め直す。
        // stackedなら縦一列に積む(左右の画面端で上下に並べる配置)。
        internal static Rectangle[] LayoutWorkBounds(Rectangle work, DockEdge edge,
            int handleCenter, IReadOnlyList<Size> sizes, bool stacked, int gapX, int gapY)
        {
            int count = sizes.Count;
            if (count == 0) return Array.Empty<Rectangle>();
            int cols = stacked ? 1 : Math.Min(count, Math.Max(1, (work.Width + gapX) / 300));
            int rows = (count + cols - 1) / cols;
            var widths = new int[cols];
            var heights = new int[rows];
            for (int i = 0; i < count; i++)
            {
                int col = i % cols;
                int row = i / cols;
                widths[col] = Math.Max(widths[col], Math.Clamp(sizes[i].Width, 1, work.Width));
                heights[row] = Math.Max(heights[row], Math.Clamp(sizes[i].Height, 1, work.Height));
            }
            FitSizes(widths, Math.Max(cols, work.Width - (cols - 1) * gapX));
            FitSizes(heights, Math.Max(rows, work.Height - (rows - 1) * gapY));
            int totalWidth = widths.Sum() + (cols - 1) * gapX;
            int totalHeight = heights.Sum() + (rows - 1) * gapY;
            int centerX = edge is DockEdge.Top or DockEdge.Bottom
                ? handleCenter : work.Left + work.Width / 2;
            int centerY = edge is DockEdge.Left or DockEdge.Right
                ? handleCenter : work.Top + work.Height / 2;
            int startX = WorkStartX(edge, work, totalWidth, centerX);
            int startY = edge switch
            {
                DockEdge.Top => work.Top,
                DockEdge.Bottom => work.Bottom - totalHeight,
                _ => Math.Clamp(centerY - totalHeight / 2, work.Top, work.Bottom - totalHeight),
            };
            var result = new Rectangle[count];
            int y = startY;
            for (int row = 0; row < rows; row++)
            {
                int rowCount = Math.Min(cols, count - row * cols);
                int rowWidth = widths.Take(rowCount).Sum() + (rowCount - 1) * gapX;
                int x = startX + (totalWidth - rowWidth) / 2;
                for (int col = 0; col < rowCount; col++)
                {
                    int index = row * cols + col;
                    int width = Math.Min(sizes[index].Width, widths[col]);
                    int height = Math.Min(sizes[index].Height, heights[row]);
                    int offsetX = edge == DockEdge.Right ? widths[col] - width : 0;
                    int offsetY = edge == DockEdge.Bottom ? heights[row] - height : 0;
                    result[index] = new Rectangle(x + offsetX, y + offsetY, width, height);
                    x += widths[col] + gapX;
                }
                y += heights[row] + gapY;
            }
            return result;
        }

        private static void FitSizes(int[] values, int available)
        {
            int sum = values.Sum();
            if (sum <= available) return;
            if (values.All(value => value == values[0]))
            {
                Array.Fill(values, Math.Max(1, available / values.Length));
                return;
            }
            int remaining = available;
            int oldSum = sum;
            for (int i = 0; i < values.Length; i++)
            {
                int scaled = i == values.Length - 1 ? remaining
                    : Math.Clamp((int)((long)values[i] * available / oldSum), 1,
                        Math.Max(1, remaining - (values.Length - 1 - i)));
                values[i] = scaled;
                remaining -= scaled;
            }
        }

        internal static int WorkStartX(DockEdge edge, Rectangle work, int width, int centerX)
        {
            return edge switch
            {
                DockEdge.Left => work.Left,
                DockEdge.Right => work.Right - width,
                _ => Math.Clamp(centerX - width / 2, work.Left, work.Right - width),
            };
        }

        private void PlaceNewIndependentHandle(DockService newlyAdded)
        {
            // 新規Dockだけ空き位置へ移動する。既存Dockの座標は変更しない。
            var occupied = _docks.Values.TakeWhile(d => !ReferenceEquals(d, newlyAdded))
                .Where(d => d.IsDocked && d.DockWorkArea == newlyAdded.DockWorkArea
                    && d.Edge == newlyAdded.Edge).ToArray();
            if (occupied.Length == 0) return;

            int span = newlyAdded.Edge is DockEdge.Left or DockEdge.Right
                ? newlyAdded.DockWorkArea.Height : newlyAdded.DockWorkArea.Width;
            int start = newlyAdded.Edge is DockEdge.Left or DockEdge.Right
                ? newlyAdded.DockWorkArea.Top : newlyAdded.DockWorkArea.Left;
            int end = start + span;
            int initial = newlyAdded.HandleCenter;
            int step = Math.Max(30, newlyAdded.HandleSpan + 8);
            for (int i = 0; i <= occupied.Length * 3 + 4; i++)
            {
                int signedOffset = i == 0 ? 0 : ((i + 1) / 2) * (i % 2 == 1 ? 1 : -1);
                int candidate = initial + signedOffset * step;
                int half = (newlyAdded.HandleSpan + 1) / 2;
                if (candidate - half < start || candidate + half > end) continue;
                if (occupied.Any(d => Math.Abs(d.HandleCenter - candidate)
                    < (d.HandleSpan + newlyAdded.HandleSpan) / 2 + 6)) continue;
                newlyAdded.SetHandleCenter(candidate);
                return;
            }
            // 空きがなければ初期位置を維持。個別配置では既存Dockを再配置しない。
        }


        // Win32のリサイズ通知に合わせて、ドラッグ中も隣のWorkウィンドウを移動する。
        private void OnExplorerLocationChanged(IntPtr hook, uint type, IntPtr hwnd,
            int objectId, int childId, uint threadId, uint eventTime)
        {
            if (_disposed || objectId != Win32.OBJID_WINDOW || childId != 0 ||
                !_resizingWorkExplorers.Contains(hwnd) || !_docks.TryGetValue(hwnd, out var dock)) return;
            OnWorkSizeChanged(dock);
        }

        // Explorerのタイトルバーをハンドル上で離したときにWorkへまとめる。
        // 通常Dockが宛先なら、そのフォルダー名で新しいWorkを作成する。
        private void OnWindowMoveSize(IntPtr hook, uint type, IntPtr hwnd,
            int objectId, int childId, uint threadId, uint eventTime)
        {
            if (objectId != Win32.OBJID_WINDOW || childId != 0 || _disposed) return;
            if (type == Win32.EVENT_SYSTEM_MOVESIZESTART)
            {
                FinishResizePositions();
                if (_docks.TryGetValue(hwnd, out var dragged) && dragged.IsExpanded)
                {
                    dragged.HandleMoveSizeEvent(type);
                    SuspendWindowSnapping(hwnd);
                }
                if (_docks.TryGetValue(hwnd, out var active) && active.IsExpanded &&
                    _entries.TryGetValue(hwnd, out var entry) && entry.Kind == DockGroupKind.Work
                    && !active.MovingByTitleBar)
                {
                    active.BeginWorkResize();
                    _resizingWorkExplorers.Add(hwnd);
                }
                if (Win32.IsExplorerWindow(hwnd) && Win32.GetWindowRect(hwnd, out var startingRect))
                {
                    var startCursor = Cursor.Position;
                    // タイトルバー内の操作に限定し、枠のリサイズをWorkへの追加と誤認しない。
                    if (startCursor.X >= startingRect.Left + 8 && startCursor.X < startingRect.Right - 8
                        && startCursor.Y >= startingRect.Top + 8 && startCursor.Y <= startingRect.Top + 100)
                    {
                        _movingExplorers.Add(hwnd);
                        _dragHoverTimer.Start();
                    }
                }
                return;
            }
            if (type != Win32.EVENT_SYSTEM_MOVESIZEEND) return;
            // Explorer側の移動終了処理とDockの配置確定が終わるまで、スナップを再開しない。
            if (hwnd == _snapSuspendedFor) _snapResumeTimer.Start();
            if (_resizingWorkExplorers.Contains(hwnd) && _docks.TryGetValue(hwnd, out var resized))
            {
                OnWorkResizeCompleted(resized);
            }
            else if (_docks.TryGetValue(hwnd, out var moved)) moved.HandleMoveSizeEvent(type);
            if (!_movingExplorers.Remove(hwnd)) return;
            if (_movingExplorers.Count == 0) _dragHoverTimer.Stop();
            ClearDropTargets();
            if (!Win32.IsExplorerWindow(hwnd) || !Win32.IsWindowVisible(hwnd)) return;
            // MOVESIZESTARTでタイトルバー操作を確認済み。
            // Dock済みのExplorerは終了イベント直後に位置が補正されるため、終了座標をウィンドウ矩形とは照合しない。
            var cursor = Cursor.Position;
            _droppedExplorer = hwnd;
            _dropPoint = cursor;
            _dropTimer.Stop();
            _dropTimer.Start();
        }

        private IntPtr _snapSuspendedFor;

        /// <summary>
        /// Dock中のExplorerをドラッグしている間だけWindowsのスナップ(画面端での分割表示)を止める。
        /// アプリ側の画面端への吸着と競合するため。設定ファイルには書かず、終了時に必ず戻す。
        /// </summary>
        private void SuspendWindowSnapping(IntPtr hwnd)
        {
            _snapResumeTimer.Stop();
            if (_snapSuspendedFor != IntPtr.Zero) return;
            if (!Win32.SystemParametersInfo(Win32.SPI_GETWINARRANGING, 0, out int enabled, 0) || enabled == 0) return;
            if (Win32.SystemParametersInfo(Win32.SPI_SETWINARRANGING, 0, IntPtr.Zero, Win32.SPIF_SENDCHANGE))
                _snapSuspendedFor = hwnd;
        }

        private void ResumeWindowSnapping()
        {
            if (_snapSuspendedFor == IntPtr.Zero) return;
            _snapSuspendedFor = IntPtr.Zero;
            _ = Win32.SystemParametersInfo(Win32.SPI_SETWINARRANGING, 1, IntPtr.Zero, Win32.SPIF_SENDCHANGE);
        }

        private void OnHandleDragMoved(DockService source, Point cursor)
        {
            IntPtr target = DropTargetAt(cursor, source.Window);
            // 同じWorkに属するハンドルは統合先として扱わない。
            if (target != IntPtr.Zero && CurrentWorkName(source.Window) is { } own
                && string.Equals(own, CurrentWorkName(target), StringComparison.OrdinalIgnoreCase))
                target = IntPtr.Zero;
            _handleDropTargets[source.Window] = target;
            foreach (var (hwnd, dock) in _docks)
                dock.SetDropTarget(hwnd == target && target != IntPtr.Zero);
        }

        private void OnHandleDragFinished(DockService source, Point cursor)
        {
            _handleDropTargets.TryGetValue(source.Window, out IntPtr target);
            _handleDropTargets.Remove(source.Window);
            ClearDropTargets();
            if (target == IntPtr.Zero || !_entries.ContainsKey(target)) return;
            // 最後に強調されていた収納へドロップしたときだけ結合する。
            var targetDock = _docks[target];
            var rect = targetDock.HandleBounds;
            rect.Inflate(16, 16);
            if (!rect.Contains(cursor)) return;
            JoinWorkByDrop(source.Window, cursor, target);
        }

        private IntPtr DropTargetAt(Point point, IntPtr source)
        {
            foreach (var (hwnd, dock) in _docks)
                if (hwnd != source && dock.IsDocked && dock.HandleEnabled)
                {
                    var bounds = dock.HandleBounds;
                    bounds.Inflate(6, 6);
                    if (bounds.Contains(point)) return hwnd;
                }
            return IntPtr.Zero;
        }

        private void ClearDropTargets()
        {
            _workDropPreview.Hide();
            foreach (var dock in _docks.Values) dock.SetDropTarget(false);
        }

        private void UpdateDropTargets()
        {
            if (_movingExplorers.Count == 0)
            {
                _dragHoverTimer.Stop();
                ClearDropTargets();
                return;
            }
            var point = Cursor.Position;
            var target = DropTargetAt(point, _movingExplorers.First());
            foreach (var (hwnd, dock) in _docks) dock.SetDropTarget(hwnd == target);
            var drop = target == IntPtr.Zero ? FindWorkDrop(_movingExplorers.First(), point) : default;
            if (drop.Target is null || !Win32.IsMouseButtonPressed())
            {
                _workDropPreview.Hide();
                return;
            }
            var preview = drop.Bounds;
            switch (drop.Action)
            {
                case WorkDropAction.Left: preview.Width /= 2; break;
                case WorkDropAction.Right: preview.X += preview.Width / 2; preview.Width /= 2; break;
                case WorkDropAction.Above: preview.Height /= 2; break;
                case WorkDropAction.Below: preview.Y += preview.Height / 2; preview.Height /= 2; break;
            }
            _workDropPreview.ShowDropCue(preview);
        }

        private void JoinWorkByDrop(IntPtr source, Point point, IntPtr explicitTarget = default)
        {
            if (_disposed || source == IntPtr.Zero || !Win32.IsWindow(source)) return;
            var target = explicitTarget != IntPtr.Zero ? explicitTarget : DropTargetAt(point, source);
            if (target == IntPtr.Zero || !_entries.TryGetValue(target, out var anchor)) return;

            string name;
            if (anchor.Kind == DockGroupKind.Work)
            {
                name = anchor.WorkName;
            }
            else
            {
                string? path = ExplorerFolders.ForWindow(target);
                string baseName = path is null ? Win32.GetTitleOf(target)
                    : string.Equals(path, ExplorerFolders.ThisPc, StringComparison.OrdinalIgnoreCase)
                        ? "PC" : Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
                if (string.IsNullOrWhiteSpace(baseName)) baseName = "Work";
                baseName = baseName.Length > 64 ? baseName[..64] : baseName;
                name = baseName;
                for (int index = 2; _workNames.Contains(name); index++)
                {
                    string suffix = $" ({index})";
                    name = baseName[..Math.Min(baseName.Length, 64 - suffix.Length)] + suffix;
                }
            }

            // 登録経路は共通。ドロップ時だけWork全体を収納状態に揃える。
            AddToWork(name, target, source);
            var groupId = _entries[target].GroupId;
            foreach (var (hwnd, dock) in _docks)
                if (_entries.TryGetValue(hwnd, out var entry)
                    && entry.Kind == DockGroupKind.Work && entry.GroupId == groupId)
                    dock.CollapseAfterWorkDrop();
        }

        public string? CurrentWorkName(IntPtr hwnd) =>
            _entries.TryGetValue(hwnd, out var state) && state.Kind == DockGroupKind.Work
                ? state.WorkName : null;

        public void AddToWork(IntPtr hwnd, string name) => AddToWork(name, hwnd);

        public void DissolveWork(string name) => DeleteWork(name);

        // WorkからWorkへは所属する全Explorerを移す。元Workは空にならないよう統合する。
        private void MergeWork(string fromName, string toName)
        {
            if (string.Equals(fromName, toName, StringComparison.OrdinalIgnoreCase)) return;
            var sourceEntries = _entries.Values.Concat(_pendingRestore).Where(e =>
                e.Kind == DockGroupKind.Work &&
                string.Equals(e.WorkName, fromName, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (sourceEntries.Length == 0) return;
            var targetEntry = _entries.Values.Concat(_pendingRestore).FirstOrDefault(e =>
                e.Kind == DockGroupKind.Work &&
                string.Equals(e.WorkName, toName, StringComparison.OrdinalIgnoreCase));
            string groupId = targetEntry?.GroupId is { Length: > 0 } id
                ? id : Guid.NewGuid().ToString("N");
            foreach (var entry in _entries.Values.Concat(_pendingRestore))
            {
                if (entry.Kind != DockGroupKind.Work) continue;
                if (!string.Equals(entry.WorkName, fromName, StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(entry.WorkName, toName, StringComparison.OrdinalIgnoreCase)) continue;
                entry.GroupId = groupId;
                entry.WorkName = toName;
                entry.SyncWindowSizes = targetEntry?.SyncWindowSizes ?? sourceEntries.Any(e => e.SyncWindowSizes);
            }
            _workNames.Remove(fromName);
            _workNames.Add(toName);
            DockProfiles.WriteWorkNames(_workNames);
            UpdateGroups();
            if (_placementMode == DockPlacementMode.Aligned) ArrangeHandles();
            else RaiseAllHandles();
            SaveSessions();
            StatusChanged?.Invoke($"Work「{fromName}」を「{toName}」に統合しました。");
        }

        private void AddToWork(string name, params IntPtr[] windows)
        {
            name = NormalizeWorkName(name);
            var targets = windows.Distinct().ToArray();
            if (targets.Length == 0) return;
            var sourceWorks = targets.Select(CurrentWorkName)
                .Where(n => n is not null && !string.Equals(n, name, StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            // 既存Workの片方だけを移動させず、グループ全体を統合する。
            foreach (var sourceWork in sourceWorks)
                MergeWork(sourceWork!, name);

            // まずDock化を完了させ、その後にまとめてWork所属を書き換える。
            foreach (var hwnd in targets)
                if (!_docks.ContainsKey(hwnd)) Dock(hwnd);

            // 名前だけで所属を分けない。既存WorkのIDを必ず再利用する。
            var existing = _entries.Values.FirstOrDefault(e => e.Kind == DockGroupKind.Work
                && string.Equals(e.WorkName, name, StringComparison.OrdinalIgnoreCase));
            string groupId = existing?.GroupId is { Length: > 0 } id
                ? id : Guid.NewGuid().ToString("N");

            // 旧版で同じWork名に異なるIDが付いた状態も、登録操作時に統合する。
            bool changed = false;
            foreach (var entry in _entries.Values.Concat(_pendingRestore))
                if (entry.Kind == DockGroupKind.Work
                    && string.Equals(entry.WorkName, name, StringComparison.OrdinalIgnoreCase)
                    && entry.GroupId != groupId)
                {
                    entry.GroupId = groupId;
                    changed = true;
                }
            // 新しく加わるウィンドウは末尾に置き、Workの並び方(縦/横)を引き継ぐ
            var members = _entries.Values.Where(e => e.Kind == DockGroupKind.Work && e.GroupId == groupId).ToArray();
            int nextSlot = members.Length == 0 ? 0 : members.Max(e => e.Slot) + 1;
            bool groupStacked = members.Any(e => e.Stacked);
            var groupSettings = members.Concat(_pendingRestore.Where(e =>
                e.Kind == DockGroupKind.Work && e.GroupId == groupId)).ToArray();
            bool groupSyncSizes = groupSettings.Length == 0 || groupSettings.Any(e => e.SyncWindowSizes);
            foreach (var hwnd in targets)
            {
                if (!_entries.TryGetValue(hwnd, out var state)) continue;
                if (state.Kind == DockGroupKind.Work && state.GroupId == groupId
                    && string.Equals(state.WorkName, name, StringComparison.OrdinalIgnoreCase)) continue;
                state.Slot = nextSlot++;
                state.Stacked = groupStacked;
                state.SyncWindowSizes = groupSyncSizes;
                state.Kind = DockGroupKind.Work;
                state.GroupId = groupId;
                state.WorkName = name;
                changed = true;
            }
            if (_workNames.Add(name)) DockProfiles.WriteWorkNames(_workNames);
            if (!changed) return; // 同じWorkに追加し直してもグループを分割しない。
            UpdateGroups();
            if (_placementMode == DockPlacementMode.Aligned) ArrangeHandles();
            else RaiseAllHandles();
            SaveSessions();
            StatusChanged?.Invoke($"{name} に追加しました。");
        }

        private void UpdateGroups()
        {
            foreach (var dock in _docks.Values)
            {
                dock.HandleEnabled = true;
                dock.IsWork = _entries.TryGetValue(dock.Window, out var workEntry)
                    && workEntry.Kind == DockGroupKind.Work;
                dock.WorkSizeSyncEnabled = dock.IsWork && workEntry!.SyncWindowSizes;
                dock.SetHandleLabel(_entries.TryGetValue(dock.Window, out var entry)
                    && !string.IsNullOrWhiteSpace(entry.CustomLabel) ? entry.CustomLabel : null);
            }
            foreach (var group in _entries.Where(e => e.Value.Kind == DockGroupKind.Work
                && !string.IsNullOrEmpty(e.Value.GroupId))
                .GroupBy(e => e.Value.GroupId))
            {
                var members = group.Select(e => e.Key).Where(_docks.ContainsKey).ToArray();
                if (members.Length == 0) continue;
                var anchor = _docks[members[0]];
                anchor.SetHandleLabel(group.First().Value.WorkName);
                for (int i = 1; i < members.Length; i++)
                {
                    var dock = _docks[members[i]];
                    dock.HandleEnabled = false;
                    // Workの各ウィンドウはひとつの画面端にまとめる。
                    dock.SetDockTarget(anchor.DockWorkArea, anchor.Edge, anchor.HandleCenter);
                }
            }
            LayoutWorkGroups();
        }

        public async System.Threading.Tasks.Task SelectWorkAsync(string name)
        {
            if (!_workNames.Contains(name)) return;
            await RestoreSavedAsync(name);
            if (_disposed) return;
            var anchor = _entries.FirstOrDefault(pair =>
                pair.Value.Kind == DockGroupKind.Work &&
                string.Equals(pair.Value.WorkName, name, StringComparison.OrdinalIgnoreCase) &&
                _docks.ContainsKey(pair.Key)).Key;
            if (anchor == IntPtr.Zero)
            {
                StatusChanged?.Invoke($"Work「{name}」には復元できるExplorerがありません。");
                return;
            }
            LayoutWorkGroups();
            _docks[anchor].ExpandFromCoordinator();
            OnExpanding(_docks[anchor]);
        }

        private void AddPendingRestore(DockEntry entry)
        {
            if (!_pendingRestore.Any(p => string.Equals(p.EntryId, entry.EntryId,
                StringComparison.OrdinalIgnoreCase)))
                _pendingRestore.Add(entry);
        }

        public async System.Threading.Tasks.Task RestoreSavedAsync(string? onlyWork = null)
        {
            if (_restoring || _disposed) return;
            var saved = _pendingRestore.ToArray();
            if (saved.Length == 0) return;
            _restoring = true;
            try
            {
                var used = new HashSet<IntPtr>();
                foreach (var entry in saved)
                {
                    if (_disposed) return;
                    if (onlyWork is not null && (entry.Kind != DockGroupKind.Work ||
                        !string.Equals(entry.WorkName, onlyWork, StringComparison.OrdinalIgnoreCase))) continue;
                    // 同じフォルダー名ではなく登録IDで判定する（別ウィンドウを省略しない）。
                    if (_entries.Any(pair => _docks.ContainsKey(pair.Key) &&
                        string.Equals(pair.Value.EntryId, entry.EntryId,
                            StringComparison.OrdinalIgnoreCase))) continue;
                    if (!ExplorerFolders.CanRestore(entry.Path)) continue;
                    IntPtr hwnd = IntPtr.Zero;
                    var existing = ExplorerFolders.List();
                    foreach (var (candidate, path) in existing)
                    {
                        if (!used.Contains(candidate) && !_docks.ContainsKey(candidate)
                            && string.Equals(path, entry.Path, StringComparison.OrdinalIgnoreCase))
                        {
                            hwnd = candidate;
                            break;
                        }
                    }
                    if (hwnd == IntPtr.Zero)
                    {
                        try
                        {
                            var info = new System.Diagnostics.ProcessStartInfo("explorer.exe")
                            { UseShellExecute = true };
                            // PCを開くのは、保存済みDockにPCがある場合だけ。
                            info.Arguments = string.Equals(entry.Path, ExplorerFolders.ThisPc,
                                StringComparison.OrdinalIgnoreCase)
                                ? ExplorerFolders.ThisPc : "/n,\"" + entry.Path + "\"";
                            System.Diagnostics.Process.Start(info);
                        }
                        catch { continue; }
                        for (int attempt = 0; attempt < 32 && hwnd == IntPtr.Zero; attempt++)
                        {
                            await System.Threading.Tasks.Task.Delay(250);
                            if (_disposed) return;
                            foreach (var (candidate, path) in ExplorerFolders.List())
                            {
                                if (!used.Contains(candidate) && !_docks.ContainsKey(candidate)
                                    && string.Equals(path, entry.Path, StringComparison.OrdinalIgnoreCase))
                                {
                                    hwnd = candidate;
                                    break;
                                }
                            }
                        }
                    }
                    if (hwnd == IntPtr.Zero) continue;
                    used.Add(hwnd);
                    try
                    {
                        Dock(hwnd, entry);
                        _pendingRestore.RemoveAll(e => string.Equals(e.EntryId, entry.EntryId,
                            StringComparison.OrdinalIgnoreCase));
                    }
                    catch { AddPendingRestore(entry); }
                }
                UpdateGroups();
                if (_placementMode == DockPlacementMode.Aligned) ArrangeHandles();
            }
            finally
            {
                _restoring = false;
                if (!_disposed) UpdateGroups();
                SaveSessions();
            }
        }

        private bool SaveSessions()
        {
            if (_restoring || _disposed || _suspendSaving) return false;
            try
            {
                var locations = ExplorerFolders.List();
                var entries = new List<DockEntry>();
                foreach (var (hwnd, dock) in _docks)
                {
                    if (!_entries.TryGetValue(hwnd, out var entry)) continue;
                    if (locations.TryGetValue(hwnd, out var path)) entry.Path = path;
                    // 一時的なShell COM取得失敗で不明なパスを無視すると、
                    // 既存のgroups.jsonが一部欠けた状態で上書きされる。
                    if (string.IsNullOrWhiteSpace(entry.Path)) return false;
                    var position = dock.PositionSnapshot();
                    entry.Screen = position.Screen;
                    entry.Edge = position.Edge;
                    entry.Position = position.Position;
                    entry.Pinned = dock.Pinned;
                    entry.Width = dock.PreferredDockSize.Width;
                    entry.Height = dock.PreferredDockSize.Height;
                    entries.Add(entry);
                }
                entries.AddRange(_pendingRestore.Where(e =>
                    !entries.Any(active => string.Equals(active.EntryId, e.EntryId,
                        StringComparison.OrdinalIgnoreCase))));
                DockProfiles.Write(entries);
                return true;
            }
            catch { return false; }
        }

        public void SaveCurrentSessions() { _ = SaveSessions(); }

        public void Dispose()
        {
            if (_disposed) return;
            FinishResizePositions();
            _disposed = true;
            _maintenance.Stop();
            _maintenance.Dispose();
            _workResizeTimer.Stop();
            _workResizeTimer.Dispose();
            _pendingWorkResizes.Clear();
            _workFrameGaps.Clear();
            _dropTimer.Stop();
            _dropTimer.Dispose();
            _dragHoverTimer.Stop();
            _dragHoverTimer.Dispose();
            _snapResumeTimer.Stop();
            _snapResumeTimer.Dispose();
            ClearDropTargets();
            _workDropPreview.Dispose();
            if (_moveSizeHook != IntPtr.Zero) Win32.UnhookWinEvent(_moveSizeHook);
            ResumeWindowSnapping();
            if (_locationHook != IntPtr.Zero) Win32.UnhookWinEvent(_locationHook);
            _movingExplorers.Clear();
            UndockAll();
        }
    }
}
