using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace TrayOrganizer.Core
{
    public class ExternalTrayDragEventArgs : EventArgs
    {
        public TrayIconItem DraggedIcon { get; set; }
        public string SourceArea { get; set; }
        public NativeMethods.POINT StartPoint { get; set; }
        public NativeMethods.POINT CurrentPoint { get; set; }
    }

    public class ExternalTrayDragMonitor : IDisposable
    {
        private readonly TrayIconEngine _engine;
        private readonly Dispatcher _dispatcher;
        private readonly Func<IList<TrayIconItem>> _getActiveIcons;

        private IntPtr _hookHandle = IntPtr.Zero;
        private NativeMethods.LowLevelMouseProc _hookProc;

        private bool _isMouseDownInTray;
        private bool _isDraggingExternalIcon;
        private NativeMethods.POINT _startPt;
        private NativeMethods.POINT _lastPt;
        private TrayIconItem _candidateIcon;
        private string _candidateSourceArea;
        private int _hitTestSessionId;

        public event EventHandler<ExternalTrayDragEventArgs> DragStarted;
        public event EventHandler<ExternalTrayDragEventArgs> DragMoved;
        public event EventHandler<ExternalTrayDragEventArgs> DragDropped;
        public event EventHandler DragCanceled;

        public bool IsDragging
        {
            get { return _isDraggingExternalIcon; }
        }

        public TrayIconItem CurrentDraggedIcon
        {
            get { return _candidateIcon; }
        }

        public ExternalTrayDragMonitor(
            TrayIconEngine engine,
            Dispatcher dispatcher,
            Func<IList<TrayIconItem>> getActiveIcons)
        {
            _engine = engine;
            _dispatcher = dispatcher;
            _getActiveIcons = getActiveIcons;
        }

        public void Start()
        {
            if (_hookHandle != IntPtr.Zero) return;
            _hookProc = HookCallback;
            using (var curProcess = Process.GetCurrentProcess())
            using (var curModule = curProcess.MainModule)
            {
                IntPtr hMod = NativeMethods.GetModuleHandle(curModule != null ? curModule.ModuleName : null);
                _hookHandle = NativeMethods.SetWindowsHookEx(NativeMethods.WH_MOUSE_LL, _hookProc, hMod, 0);
                ConfigManager.Log("DRAG_HOOK", string.Format("外部托盘拖拽监听已启动 (WH_MOUSE_LL=0x{0:X})", _hookHandle.ToInt64()));
            }
        }

        public void Stop()
        {
            if (_hookHandle != IntPtr.Zero)
            {
                NativeMethods.UnhookWindowsHookEx(_hookHandle);
                _hookHandle = IntPtr.Zero;
            }
        }

        private static bool IsPointInNativeTrayOrOverflow(int x, int y)
        {
            IntPtr trayWnd = NativeMethods.FindWindow("Shell_TrayWnd", IntPtr.Zero);
            NativeMethods.RECT rTray;
            if (trayWnd != IntPtr.Zero && NativeMethods.GetWindowRect(trayWnd, out rTray) && rTray.Contains(x, y))
                return true;

            IntPtr ovWnd = NativeMethods.FindWindow("TopLevelWindowForOverflowXamlIsland", IntPtr.Zero);
            NativeMethods.RECT rOv;
            if (ovWnd != IntPtr.Zero && NativeMethods.IsWindowVisible(ovWnd) && NativeMethods.GetWindowRect(ovWnd, out rOv) && rOv.Contains(x, y))
                return true;

            return false;
        }

        private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                uint msg = (uint)wParam.ToInt64();
                var hs = (NativeMethods.MSLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(NativeMethods.MSLLHOOKSTRUCT));

                if (msg == NativeMethods.WM_LBUTTONDOWN)
                {
                    int sx = hs.pt.X;
                    int sy = hs.pt.Y;
                    if (IsPointInNativeTrayOrOverflow(sx, sy))
                    {
                        _isMouseDownInTray = true;
                        _isDraggingExternalIcon = false;
                        _startPt = hs.pt;
                        _lastPt = hs.pt;
                        _candidateIcon = null;
                        _candidateSourceArea = null;
                        int session = ++_hitTestSessionId;

                        var snapshot = _getActiveIcons != null ? _getActiveIcons() : null;
                        Task.Factory.StartNew(() =>
                        {
                            NativeMethods.AttachToDefaultDesktop();
                            string srcArea;
                            var hit = _engine.HitTestExternalTrayIcon(sx, sy, snapshot, out srcArea);
                            if (hit != null && session == _hitTestSessionId && _isMouseDownInTray)
                            {
                                _candidateIcon = hit;
                                _candidateSourceArea = srcArea;
                                ConfigManager.Log("DRAG_HIT", string.Format("捕获托盘图标按下: [{0}] 来源={1} 坐标=({2},{3}) Rect={4}",
                                    hit.AppDisplayName, srcArea, sx, sy, hit.LastBounds));

                                // If mouse already moved past threshold while hit-testing resolved
                                double dist = Math.Sqrt(Math.Pow(_lastPt.X - _startPt.X, 2) + Math.Pow(_lastPt.Y - _startPt.Y, 2));
                                if (dist >= 10 && !_isDraggingExternalIcon)
                                {
                                    _isDraggingExternalIcon = true;
                                    var args = new ExternalTrayDragEventArgs
                                    {
                                        DraggedIcon = _candidateIcon,
                                        SourceArea = _candidateSourceArea,
                                        StartPoint = _startPt,
                                        CurrentPoint = _lastPt
                                    };
                                    _dispatcher.BeginInvoke(new Action(() =>
                                    {
                                        var h = DragStarted;
                                        if (h != null) h(this, args);
                                    }));
                                }
                            }
                        });
                    }
                    else
                    {
                        _isMouseDownInTray = false;
                        _candidateIcon = null;
                    }
                }
                else if (msg == NativeMethods.WM_MOUSEMOVE)
                {
                    if (_isMouseDownInTray)
                    {
                        _lastPt = hs.pt;
                        if (_candidateIcon != null)
                        {
                            double dist = Math.Sqrt(Math.Pow(hs.pt.X - _startPt.X, 2) + Math.Pow(hs.pt.Y - _startPt.Y, 2));
                            if (!_isDraggingExternalIcon && dist >= 10)
                            {
                                _isDraggingExternalIcon = true;
                                var args = new ExternalTrayDragEventArgs
                                {
                                    DraggedIcon = _candidateIcon,
                                    SourceArea = _candidateSourceArea,
                                    StartPoint = _startPt,
                                    CurrentPoint = hs.pt
                                };
                                _dispatcher.BeginInvoke(new Action(() =>
                                {
                                    var h = DragStarted;
                                    if (h != null) h(this, args);
                                }));
                            }
                            else if (_isDraggingExternalIcon)
                            {
                                var args = new ExternalTrayDragEventArgs
                                {
                                    DraggedIcon = _candidateIcon,
                                    SourceArea = _candidateSourceArea,
                                    StartPoint = _startPt,
                                    CurrentPoint = hs.pt
                                };
                                _dispatcher.BeginInvoke(new Action(() =>
                                {
                                    var h = DragMoved;
                                    if (h != null) h(this, args);
                                }));
                            }
                        }
                    }
                }
                else if (msg == NativeMethods.WM_LBUTTONUP)
                {
                    if (_isMouseDownInTray)
                    {
                        bool wasDragging = _isDraggingExternalIcon;
                        var icon = _candidateIcon;
                        var src = _candidateSourceArea;
                        var startPt = _startPt;
                        var dropPt = hs.pt;

                        _isMouseDownInTray = false;
                        _isDraggingExternalIcon = false;
                        _candidateIcon = null;
                        _hitTestSessionId++;

                        if (wasDragging && icon != null)
                        {
                            var args = new ExternalTrayDragEventArgs
                            {
                                DraggedIcon = icon,
                                SourceArea = src,
                                StartPoint = startPt,
                                CurrentPoint = dropPt
                            };
                            _dispatcher.BeginInvoke(new Action(() =>
                            {
                                var h = DragDropped;
                                if (h != null) h(this, args);
                            }));
                        }
                        else
                        {
                            _dispatcher.BeginInvoke(new Action(() =>
                            {
                                var h = DragCanceled;
                                if (h != null) h(this, EventArgs.Empty);
                            }));
                        }
                    }
                }
            }

            return NativeMethods.CallNextHookEx(_hookHandle, nCode, wParam, lParam);
        }

        public void Dispose()
        {
            Stop();
        }
    }
}
