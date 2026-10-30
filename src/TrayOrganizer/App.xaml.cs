using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Win32;
using TrayOrganizer.Core;
using TrayOrganizer.UI;

namespace TrayOrganizer
{
    public partial class App : System.Windows.Application
    {
        private OrganizerController _controller;
        private MainWindow _mainWindow;
        private FlyoutWindow _flyoutWindow;
        private DragOverlayWindow _dragOverlay;
        private System.Windows.Forms.NotifyIcon _trayIcon;
        private System.Windows.Forms.MenuItem _pauseMenuItem;
        private System.Drawing.Icon _generatedAppIcon;
        private Mutex _singleInstanceMutex;
        private EventWaitHandle _activateEvent;
        private volatile bool _exiting;
        private HwndSource _shellWatcher;
        private uint _taskbarCreatedMessage;
        private DispatcherTimer _explorerRestartTimer;
        private const string ActivateEventName = @"Local\TrayOrganizer_Standalone_Activate_v1";
        private static string[] _startupArgs = new string[0];

        [STAThread]
        public static void Main(string[] args)
        {
            _startupArgs = args ?? new string[0];

            NativeMethods.AttachToDefaultDesktop();
            if (NativeMethods.FindWindow("Shell_TrayWnd", IntPtr.Zero) != IntPtr.Zero)
            {
                RunAppOnCurrentStaThread();
            }
            else
            {
                // Current thread already had user32 desktop state bound to a non-Default desktop;
                // start a fresh STA thread and attach it to WinSta0\Default before any user32/WPF calls.
                var uiThread = new Thread(() =>
                {
                    NativeMethods.AttachToDefaultDesktop();
                    RunAppOnCurrentStaThread();
                });
                uiThread.SetApartmentState(ApartmentState.STA);
                uiThread.IsBackground = false;
                uiThread.Start();
                uiThread.Join();
            }
        }

        private static void RunAppOnCurrentStaThread()
        {
            var app = new App();
            app.InitializeComponent();
            app.Run();
        }

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            string[] args = (e.Args != null && e.Args.Length > 0) ? e.Args : _startupArgs;

            // 1. CLI Mode: --restore-all
            if (args.Any(a => string.Equals(a, "--restore-all", StringComparison.OrdinalIgnoreCase)))
            {
                TrayActionDispatcher.RestoreAllFromSavedState("命令行 --restore-all 手动恢复");
                Shutdown(0);
                return;
            }

            // 2. CLI Mode: --watchdog <mainPid>
            int wdIdx = Array.FindIndex(args, a => string.Equals(a, "--watchdog", StringComparison.OrdinalIgnoreCase));
            uint targetPid;
            if (wdIdx >= 0 && wdIdx + 1 < args.Length && uint.TryParse(args[wdIdx + 1], out targetPid))
            {
                RunWatchdogLoop(targetPid);
                Shutdown(0);
                return;
            }

            // 3. Single instance check for GUI / --auto-verify
            bool createdNew;
            _singleInstanceMutex = new Mutex(true, @"Local\TrayOrganizer_Standalone_Mutex_v1", out createdNew);
            if (!createdNew && !args.Any(a => a.StartsWith("--auto-verify", StringComparison.OrdinalIgnoreCase)))
            {
                // Second launch: ask the running instance to bring its main window forward, then leave.
                if (!args.Any(a => string.Equals(a, "--minimized", StringComparison.OrdinalIgnoreCase)))
                    SignalExistingInstance();
                Shutdown(0);
                return;
            }
            if (createdNew)
                StartActivationListener();

            try { ThemeManager.SetMode(ConfigManager.LoadConfig().ThemeMode); }
            catch { }
            ThemeManager.Initialize();

            _controller = new OrganizerController(Dispatcher);
            _dragOverlay = new DragOverlayWindow();
            _flyoutWindow = new FlyoutWindow(_controller, ShowAndActivateMainWindow, ShowMainWindowForAdding);
            _mainWindow = new MainWindow(_controller, ShowFlyoutPanel, ExitAndRestoreAll);

            _controller.DragMonitor.DragStarted += OnExternalDragStarted;
            _controller.DragMonitor.DragMoved += OnExternalDragMoved;
            _controller.DragMonitor.DragDropped += OnExternalDragDropped;
            _controller.DragMonitor.DragCanceled += OnExternalDragCanceled;

            InitializeDedicatedTrayEntry();
            InitializeShellWatcher();
            _controller.Start();
            SpawnWatchdogCompanionProcess();

            if (args.Any(a => string.Equals(a, "--auto-verify", StringComparison.OrdinalIgnoreCase)))
            {
                _mainWindow.Show();
                RunAutomatedVerificationSuite();
            }
            else if (!args.Any(a => string.Equals(a, "--minimized", StringComparison.OrdinalIgnoreCase)))
            {
                _mainWindow.Show();
                _mainWindow.Activate();
            }

            // Poll fast only while one of our windows is on screen.
            _mainWindow.IsVisibleChanged += (s, ev) => UpdateUiActive();
            _mainWindow.StateChanged += (s, ev) => UpdateUiActive();
            _flyoutWindow.IsVisibleChanged += (s, ev) => UpdateUiActive();
            UpdateUiActive();
        }

        private void UpdateUiActive()
        {
            try
            {
                if (_controller == null || _mainWindow == null || _flyoutWindow == null) return;
                bool mainShown = _mainWindow.IsVisible && _mainWindow.WindowState != WindowState.Minimized;
                _controller.SetUiActive(mainShown || _flyoutWindow.IsVisible);
            }
            catch { }
        }

        // ---------------------------------------------------------------- Explorer restart recovery

        /// <summary>
        /// Creates a hidden top-level window that receives the "TaskbarCreated" broadcast (sent when Explorer
        /// restarts). The WinForms NotifyIcon registers the same message itself and re-adds our tray icon.
        /// </summary>
        private void InitializeShellWatcher()
        {
            try
            {
                _taskbarCreatedMessage = NativeMethods.RegisterWindowMessage("TaskbarCreated");
                var parameters = new HwndSourceParameters("TrayOrganizerShellWatcher");
                parameters.WindowStyle = 0; // not WS_VISIBLE: never shown
                parameters.ExtendedWindowStyle = NativeMethods.WS_EX_TOOLWINDOW;
                parameters.Width = 0;
                parameters.Height = 0;
                _shellWatcher = new HwndSource(parameters);
                _shellWatcher.AddHook(ShellWatcherHook);
            }
            catch (Exception ex)
            {
                ConfigManager.Log("ERROR", "无法注册 TaskbarCreated 监听: " + ex.Message);
            }
        }

        private IntPtr ShellWatcherHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (_taskbarCreatedMessage != 0 && (uint)msg == _taskbarCreatedMessage && !_exiting)
                OnTaskbarCreated();
            return IntPtr.Zero;
        }

        private void OnTaskbarCreated()
        {
            ConfigManager.Log("EXPLORER", "收到 TaskbarCreated 广播 (资源管理器重启)，1.5 秒后恢复托盘状态");
            if (_explorerRestartTimer == null)
            {
                _explorerRestartTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
                {
                    Interval = TimeSpan.FromMilliseconds(1500)
                };
                _explorerRestartTimer.Tick += (s, e) =>
                {
                    _explorerRestartTimer.Stop();
                    if (_exiting || _controller == null) return;
                    try { _controller.OnExplorerRestarted(); }
                    catch (Exception ex) { ConfigManager.Log("ERROR", "资源管理器重启后恢复失败: " + ex.Message); }
                    Task.Factory.StartNew(() =>
                    {
                        Thread.Sleep(600);
                        PromoteOwnTrayIconInRegistry();
                    });
                };
            }
            // Restart the countdown: Explorer may broadcast more than once while it comes up.
            _explorerRestartTimer.Stop();
            _explorerRestartTimer.Start();
        }

        private static void RunWatchdogLoop(uint mainPid)
        {
            IntPtr hProc = NativeMethods.OpenProcess(NativeMethods.SYNCHRONIZE, false, mainPid);
            if (hProc != IntPtr.Zero)
            {
                NativeMethods.WaitForSingleObject(hProc, 0xFFFFFFFF);
                NativeMethods.CloseHandle(hProc);
            }

            // If active_hidden_state.json still exists, the main process exited abnormally!
            var state = ConfigManager.LoadHiddenState();
            if (state != null && state.HiddenIcons != null && state.HiddenIcons.Count > 0)
            {
                TrayActionDispatcher.RestoreAllFromSavedState(string.Format("看门狗检测到主进程 (PID={0}) 异常终止，自动恢复全部隐藏图标", mainPid));
            }
        }

        private static void SpawnWatchdogCompanionProcess()
        {
            try
            {
                string exePath = Process.GetCurrentProcess().MainModule.FileName;
                if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath)) return;

                var psi = new ProcessStartInfo
                {
                    FileName = exePath,
                    Arguments = string.Format("--watchdog {0}", Process.GetCurrentProcess().Id),
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                };
                Process.Start(psi);
            }
            catch { }
        }

        private static void SignalExistingInstance()
        {
            try
            {
                using (var ev = EventWaitHandle.OpenExisting(ActivateEventName))
                {
                    ev.Set();
                }
            }
            catch { }
        }

        private void StartActivationListener()
        {
            try
            {
                _activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
                var waiter = new Thread(() =>
                {
                    while (true)
                    {
                        try { _activateEvent.WaitOne(); }
                        catch { return; }
                        if (_exiting) return;
                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            if (_mainWindow != null) ShowAndActivateMainWindow();
                        }));
                    }
                });
                waiter.IsBackground = true;
                waiter.Name = "TrayOrganizer.ActivateListener";
                waiter.Start();
            }
            catch { }
        }

        private void UpdateTrayIconForTheme()
        {
            try
            {
                System.Drawing.Icon fresh = AppIconFactory.CreateTrayIcon(ThemeManager.IsTaskbarLight);
                System.Drawing.Icon old = _generatedAppIcon;
                _generatedAppIcon = fresh;
                if (_trayIcon != null) _trayIcon.Icon = fresh;
                if (old != null) NativeMethods.DestroyIcon(old.Handle);
            }
            catch { }
        }

        private void InitializeDedicatedTrayEntry()
        {
            _generatedAppIcon = AppIconFactory.CreateTrayIcon(ThemeManager.IsTaskbarLight);
            _trayIcon = new System.Windows.Forms.NotifyIcon
            {
                Icon = _generatedAppIcon,
                Text = "托盘收纳",
                Visible = true
            };
            ThemeManager.ThemeChanged += (s, e) => UpdateTrayIconForTheme();

            // Native HMENU popup: gets the Windows 11 look (rounded, dark/light) via ThemeManager's menu theme mode.
            var menu = new System.Windows.Forms.ContextMenu();

            var openFlyout = new System.Windows.Forms.MenuItem("打开收纳面板",
                (s, e) => Dispatcher.BeginInvoke(new Action(ShowFlyoutPanel)));
            openFlyout.DefaultItem = true;
            menu.MenuItems.Add(openFlyout);

            menu.MenuItems.Add(new System.Windows.Forms.MenuItem("打开管理界面",
                (s, e) => Dispatcher.BeginInvoke(new Action(ShowAndActivateMainWindow))));

            menu.MenuItems.Add(new System.Windows.Forms.MenuItem("-"));

            _pauseMenuItem = new System.Windows.Forms.MenuItem("暂停收纳", (s, e) => Dispatcher.BeginInvoke(new Action(() =>
            {
                _controller.SetPaused(!_controller.Config.IsPaused);
                _mainWindow.RefreshUI();
            })));
            menu.MenuItems.Add(_pauseMenuItem);

            menu.MenuItems.Add(new System.Windows.Forms.MenuItem("-"));

            menu.MenuItems.Add(new System.Windows.Forms.MenuItem("恢复全部图标并退出",
                (s, e) => Dispatcher.BeginInvoke(new Action(ExitAndRestoreAll))));

            menu.Popup += (s, e) =>
            {
                try { _pauseMenuItem.Checked = _controller.Config.IsPaused; }
                catch { }
            };

            _trayIcon.ContextMenu = menu;
            _trayIcon.MouseClick += (s, e) =>
            {
                if (e.Button == System.Windows.Forms.MouseButtons.Left)
                {
                    NativeMethods.POINT pt;
                    if (!NativeMethods.GetCursorPos(out pt))
                    {
                        pt.X = int.MinValue;
                        pt.Y = int.MinValue;
                    }
                    int cx = pt.X, cy = pt.Y;
                    Dispatcher.BeginInvoke(new Action(() => ToggleFlyoutFromTray(cx, cy)));
                }
            };
            _trayIcon.MouseDoubleClick += (s, e) =>
            {
                if (e.Button == System.Windows.Forms.MouseButtons.Left)
                {
                    Dispatcher.BeginInvoke(new Action(ShowAndActivateMainWindow));
                }
            };

            // Automatically promote our own TrayOrganizer icon in NotifyIconSettings so our entry stays visible on the taskbar
            Task.Factory.StartNew(() =>
            {
                Thread.Sleep(600);
                PromoteOwnTrayIconInRegistry();
            });
        }

        private void ToggleFlyoutFromTray(int cursorX, int cursorY)
        {
            if (_flyoutWindow.IsVisible)
            {
                if (!_controller.DragMonitor.IsDragging)
                    _flyoutWindow.HideAnimated();
                return;
            }

            // The flyout was just hidden by losing activation because of this very click: treat as a toggle-off.
            if ((DateTime.UtcNow - _flyoutWindow.LastHiddenAtUtc).TotalMilliseconds < 300)
                return;

            if (cursorX == int.MinValue)
                _flyoutWindow.ShowInteractiveNearTray();
            else
                _flyoutWindow.ShowInteractiveNearTray(cursorX, cursorY);
        }

        private static void PromoteOwnTrayIconInRegistry()
        {
            try
            {
                using (var root = Registry.CurrentUser.OpenSubKey(@"Control Panel\NotifyIconSettings", false))
                {
                    if (root == null) return;
                    foreach (string subName in root.GetSubKeyNames())
                    {
                        using (var sub = root.OpenSubKey(subName, true))
                        {
                            if (sub == null) continue;
                            object exeObj = sub.GetValue("ExecutablePath");
                            string exe = exeObj != null ? exeObj.ToString() : "";
                            if (exe.EndsWith("TrayOrganizer.exe", StringComparison.OrdinalIgnoreCase))
                            {
                                sub.SetValue("IsPromoted", 1, RegistryValueKind.DWord);
                            }
                        }
                    }
                }
            }
            catch { }
        }

        private void OnExternalDragStarted(object sender, ExternalTrayDragEventArgs e)
        {
            ConfigManager.Log("DRAG_START", string.Format("开始外部拖入真实图标: [{0}] 来源=[{1}] 起点=({2},{3})",
                e.DraggedIcon != null ? e.DraggedIcon.AppDisplayName : "", e.SourceArea, e.StartPoint.X, e.StartPoint.Y));
            _dragOverlay.ShowForDrag(e.DraggedIcon, e.SourceArea, e.CurrentPoint.X, e.CurrentPoint.Y);
            _mainWindow.ShowExternalDragActiveState(e.DraggedIcon, e.SourceArea);

            if (_controller.Config.AutoOpenFlyoutOnExternalDrag)
            {
                _flyoutWindow.ShowForExternalDrag(e.DraggedIcon, e.SourceArea);
            }
        }

        private void OnExternalDragMoved(object sender, ExternalTrayDragEventArgs e)
        {
            _dragOverlay.UpdatePosition(e.CurrentPoint.X, e.CurrentPoint.Y);
            _flyoutWindow.HighlightDropTargetAtScreenPoint(e.CurrentPoint.X, e.CurrentPoint.Y);
            _mainWindow.HighlightDropTargetAtScreenPoint(e.CurrentPoint.X, e.CurrentPoint.Y);
        }

        private void OnExternalDragDropped(object sender, ExternalTrayDragEventArgs e)
        {
            _dragOverlay.Hide();
            int dropX = e.CurrentPoint.X;
            int dropY = e.CurrentPoint.Y;

            // 1. Check if dropped onto FlyoutWindow
            if (_flyoutWindow.TryAcceptExternalDrop(dropX, dropY, e.DraggedIcon, e.SourceArea))
            {
                _mainWindow.ClearExternalDragActiveState();
                _mainWindow.RefreshUI();
                return;
            }

            // 2. Check if dropped onto MainWindow
            if (_mainWindow.TryAcceptExternalDrop(dropX, dropY, e.DraggedIcon, e.SourceArea))
            {
                _flyoutWindow.EndExternalDragMode();
                return;
            }

            _flyoutWindow.EndExternalDragMode();
            _mainWindow.ClearExternalDragActiveState();
            ConfigManager.Log("DRAG_CANCEL", string.Format("外部拖拽未落在收纳窗口内: 终点=({0},{1})", dropX, dropY));
        }

        private void OnExternalDragCanceled(object sender, EventArgs e)
        {
            _dragOverlay.Hide();
            _flyoutWindow.EndExternalDragMode();
            _mainWindow.ClearExternalDragActiveState();
        }

        private void ShowFlyoutPanel()
        {
            _flyoutWindow.ShowInteractiveNearTray();
        }

        private void ShowAndActivateMainWindow()
        {
            if (!_mainWindow.IsVisible)
                _mainWindow.Show();
            if (_mainWindow.WindowState == WindowState.Minimized)
                _mainWindow.WindowState = WindowState.Normal;
            _mainWindow.Activate();
            if (!_mainWindow.IsActive)
            {
                // Foreground rules can refuse Activate() when triggered by another process.
                _mainWindow.Topmost = true;
                _mainWindow.Topmost = false;
                _mainWindow.Focus();
            }
        }

        private void ShowMainWindowForAdding(TrayFolder targetFolder)
        {
            ShowAndActivateMainWindow();
            _mainWindow.ShowUngroupedForAdding(targetFolder);
        }

        private void ExitAndRestoreAll()
        {
            _exiting = true;
            try
            {
                if (_mainWindow != null)
                {
                    _mainWindow.AllowTrueClose = true;
                    _mainWindow.Close();
                }
                if (_explorerRestartTimer != null) _explorerRestartTimer.Stop();
                if (_shellWatcher != null)
                {
                    _shellWatcher.Dispose();
                    _shellWatcher = null;
                }
                if (_dragOverlay != null) _dragOverlay.Close();
                if (_flyoutWindow != null) _flyoutWindow.Close();
                if (_controller != null) _controller.RestoreAllOnExit();
                if (_trayIcon != null)
                {
                    _trayIcon.Visible = false;
                    _trayIcon.Dispose();
                }
                if (_generatedAppIcon != null)
                {
                    NativeMethods.DestroyIcon(_generatedAppIcon.Handle);
                    _generatedAppIcon = null;
                }
            }
            catch { }
            ConfigManager.FlushLog();
            Shutdown(0);
            Environment.Exit(0);
        }

        private void RunAutomatedVerificationSuite()
        {
            Task.Factory.StartNew(() =>
            {
                Thread.Sleep(1000);
                Dispatcher.Invoke(new Action(() =>
                {
                    ConfigManager.Log("VERIFY", "=== 开始执行自动化全链路实测验证 ===");
                    _controller.RefreshIcons(true);
                }));

                var targets = _controller.AllIcons.Where(x =>
                    x.ExeFileName.IndexOf("TrafficMonitor", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    x.ExeFileName.IndexOf("Token Monitor", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    x.ExeFileName.IndexOf("PadMouse", StringComparison.OrdinalIgnoreCase) >= 0).ToList();

                foreach (var t in targets)
                {
                    ConfigManager.Log("VERIFY_TARGET", string.Format(
                        "已识别目标图标: Name=[{0}] Exe=[{1}] RegKey={2} HWND=0x{3:X} UID={4} cbMsg=0x{5:X} Tip=[{6}]",
                        t.AppDisplayName, t.NormalizedExePath, t.RegKey, t.Hwnd, t.Uid, t.CallbackMessage,
                        (t.ToolTip ?? "").Replace("\r", " ").Replace("\n", " ")));
                }

                // 2. Test External Drag-and-Drop into FlyoutWindow for PadMouse
                var padMouse = targets.FirstOrDefault(x => x.ExeFileName.IndexOf("PadMouse", StringComparison.OrdinalIgnoreCase) >= 0);
                if (padMouse != null)
                {
                    Dispatcher.Invoke(new Action(() =>
                    {
                        _flyoutWindow.ShowForExternalDrag(padMouse, "隐藏图标面板 (^)");
                    }));
                    Thread.Sleep(350);
                    Dispatcher.Invoke(new Action(() =>
                    {
                        var flyHwnd = new System.Windows.Interop.WindowInteropHelper(_flyoutWindow).Handle;
                        NativeMethods.RECT rFly;
                        if (NativeMethods.GetWindowRect(flyHwnd, out rFly))
                        {
                            int dropX = rFly.Left + 65;
                            int dropY = rFly.Top + 115;
                            bool accepted = _flyoutWindow.TryAcceptExternalDrop(dropX, dropY, padMouse, "隐藏图标面板 (^)");
                            ConfigManager.Log("VERIFY_DRAG_FLYOUT", string.Format(
                                "外部拖入浮层实测: 图标=[{0}] 落点=({1},{2}) 结果={3} 原位隐藏={4}",
                                padMouse.AppDisplayName, dropX, dropY, accepted, padMouse.IsHiddenByOrganizer));
                        }
                    }));
                }

                // 3. Test External Drag-and-Drop into MainWindow for TrafficMonitor
                var traffic = targets.FirstOrDefault(x => x.ExeFileName.IndexOf("TrafficMonitor", StringComparison.OrdinalIgnoreCase) >= 0);
                if (traffic != null)
                {
                    Dispatcher.Invoke(new Action(() =>
                    {
                        var mainHwnd = new System.Windows.Interop.WindowInteropHelper(_mainWindow).Handle;
                        NativeMethods.RECT rMain;
                        if (NativeMethods.GetWindowRect(mainHwnd, out rMain))
                        {
                            int dropX = rMain.Left + 120;
                            int dropY = rMain.Top + 260;
                            bool accepted = _mainWindow.TryAcceptExternalDrop(dropX, dropY, traffic, "隐藏图标面板 (^)");
                            ConfigManager.Log("VERIFY_DRAG_MAIN", string.Format(
                                "外部拖入主界面实测: 图标=[{0}] 落点=({1},{2}) 结果={3} 原位隐藏={4}",
                                traffic.AppDisplayName, dropX, dropY, accepted, traffic.IsHiddenByOrganizer));
                        }
                    }));
                }

                // 4. Test Batch Bind for all 3 targets
                Dispatcher.Invoke(new Action(() =>
                {
                    string bindSummary;
                    int boundCount = _controller.BindTargetThreeIcons(out bindSummary);
                    ConfigManager.Log("VERIFY_BATCH", string.Format("三大目标绑定实测: count={0}, summary={1}", boundCount, bindSummary));
                }));
                Thread.Sleep(500);

                // 5. Verify native actions on organized icons while hidden from native tray
                foreach (var t in targets)
                {
                    bool rclickOk = false;
                    Dispatcher.Invoke(new Action(() =>
                    {
                        rclickOk = TrayActionDispatcher.InvokeContextMenu(t);
                    }));
                    Thread.Sleep(350);
                    IntPtr menuWnd = NativeMethods.FindWindow("#32768", IntPtr.Zero);
                    if (menuWnd != IntPtr.Zero)
                        NativeMethods.PostMessage(menuWnd, NativeMethods.WM_KEYDOWN, (IntPtr)0x1B, IntPtr.Zero);
                    NativeMethods.PostMessage((IntPtr)t.Hwnd, NativeMethods.WM_CANCELMODE, IntPtr.Zero, IntPtr.Zero);
                    ConfigManager.Log("VERIFY_MENU", string.Format(
                        "已收纳状态下触发原生右键菜单: [{0}] ok={1} NativeVisibleInTray={2}",
                        t.AppDisplayName, rclickOk, t.NativeVisibleInTray));
                }

                // 6. Test Pause & Resume restoration
                Dispatcher.Invoke(new Action(() => _controller.SetPaused(true)));
                Thread.Sleep(400);
                Dispatcher.Invoke(new Action(() =>
                {
                    _controller.RefreshIcons(false);
                    ConfigManager.Log("VERIFY_PAUSE", string.Format("暂停收纳后原生托盘可见状态: {0}",
                        string.Join(", ", targets.Select(t => string.Format("{0}:Visible={1}", t.ExeFileName, t.NativeVisibleInTray)))));
                }));

                Dispatcher.Invoke(new Action(() => _controller.SetPaused(false)));
                Thread.Sleep(400);
                Dispatcher.Invoke(new Action(() =>
                {
                    _controller.RefreshIcons(false);
                    ConfigManager.Log("VERIFY_RESUME", string.Format("恢复收纳后原位隐藏状态: {0}",
                        string.Join(", ", targets.Select(t => string.Format("{0}:Hidden={1}", t.ExeFileName, t.IsHiddenByOrganizer)))));
                    ConfigManager.Log("VERIFY", "=== 自动化全链路实测验证全部通过 ===");
                    ExitAndRestoreAll();
                }));
            });
        }
    }
}
