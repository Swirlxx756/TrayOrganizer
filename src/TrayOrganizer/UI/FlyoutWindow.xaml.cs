using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using TrayOrganizer.Core;

namespace TrayOrganizer.UI
{
    /// <summary>Pixel-accurate window placement and per-monitor DPI helpers (private P/Invokes).</summary>
    internal static class PixelPlacement
    {
        private const uint SWP_NOSIZE = 0x0001;
        private const uint SWP_NOZORDER = 0x0004;
        private const uint SWP_NOACTIVATE = 0x0010;
        private const uint MONITOR_DEFAULTTONEAREST = 2;

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromPoint(POINT pt, uint flags);

        [DllImport("shcore.dll")]
        private static extern int GetDpiForMonitor(IntPtr hMonitor, int dpiType, out uint dpiX, out uint dpiY);

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

        [DllImport("user32.dll")]
        private static extern IntPtr GetThreadDpiAwarenessContext();

        [DllImport("user32.dll")]
        private static extern int GetAwarenessFromDpiAwarenessContext(IntPtr value);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        private static int _perMonitor = -1;

        public static bool IsPerMonitorAware()
        {
            if (_perMonitor < 0)
            {
                _perMonitor = 0;
                try
                {
                    // 2 = DPI_AWARENESS_PER_MONITOR_AWARE
                    if (GetAwarenessFromDpiAwarenessContext(GetThreadDpiAwarenessContext()) == 2)
                        _perMonitor = 1;
                }
                catch { }
            }
            return _perMonitor == 1;
        }

        /// <summary>Scale factor (1.0 = 96 dpi) of the monitor at a physical-pixel point.</summary>
        public static double GetMonitorScale(int x, int y, double fallback)
        {
            if (!IsPerMonitorAware()) return fallback;
            try
            {
                var pt = new POINT { X = x, Y = y };
                IntPtr mon = MonitorFromPoint(pt, MONITOR_DEFAULTTONEAREST);
                uint dx, dy;
                if (mon != IntPtr.Zero && GetDpiForMonitor(mon, 0, out dx, out dy) == 0 && dx > 0)
                    return dx / 96.0;
            }
            catch { }
            return fallback;
        }

        /// <summary>Moves (and optionally resizes) a window in physical pixels without activating it.</summary>
        public static void Place(IntPtr hwnd, int x, int y, int w, int h, bool resize)
        {
            if (hwnd == IntPtr.Zero) return;
            uint flags = SWP_NOZORDER | SWP_NOACTIVATE;
            if (!resize) flags |= SWP_NOSIZE;
            SetWindowPos(hwnd, IntPtr.Zero, x, y, w, h, flags);
        }

        public static void ForceForeground(IntPtr hwnd)
        {
            if (hwnd != IntPtr.Zero) SetForegroundWindow(hwnd);
        }
    }

    public partial class FlyoutWindow : Window
    {
        private const string NewFolderDropTag = "NEW_FOLDER_DROP";
        private const string BackTag = "BACK_TILE";
        private const string FolderGlyph = "";
        private const int WM_DPICHANGED = 0x02E0;

        // Everything uses the Windows 11 overflow grid: 40x40 hit targets, 16x16 DIP icons, 4 DIP radius.
        // With folder names on, folder tiles are wider/taller to fit a 12 px caption.
        private const double IconTile = 40;
        private const double IconSize = 16;
        private const double TileMargin = 1;
        private const double PanelPadding = 5;       // + TileMargin = 6 DIP from the border to the first hit target
        private const double TileRadius = 4;
        // Folder tiles (iOS-style frosted squircle plate with a 3x3 grid of member icons) are bigger than app tiles.
        private const double FolderTile = 56;      // hit target without names
        private const double FolderPlate = 48;     // the plate itself
        private const double PlateRadius = 11;     // ~24 % of the plate size
        private const double MiniIcon = 12;
        private const double MiniGap = 2;
        private const double MiniPad = 4;          // 3 * 12 + 2 * 2 + 2 * 4 = 48
        // Large folder: 96 DIP plate = 2x2 cells of 40 DIP (6 DIP padding, 4 DIP gap) with 36 DIP app icons.
        private const double LargeTile = 112;
        private const double LargePlate = 96;
        private const double LargeRadius = 22;
        private const double LargeCell = 40;
        private const double LargePad = 6;
        private const double LargeIcon = 36;
        private const double LargeMini = 16;
        private const double LargeMiniPad = 2;     // 2 * 16 + 4 + 2 * 2 = 40 (cell step 44: 6 + 40 + 4 + 40 + 6 = 96)
        private const double NamedTileW = 72;
        private const double NamedTileH = 78;
        private const double NameH = 16;

        // Plate fills: normal / hover / pressed, per taskbar theme.
        private static readonly Brush[] PlateLight = new Brush[]
        {
            FrozenBrush(Color.FromArgb(0xA6, 0xFF, 0xFF, 0xFF)),
            FrozenBrush(Color.FromArgb(0xC8, 0xFF, 0xFF, 0xFF)),
            FrozenBrush(Color.FromArgb(0x8C, 0xF2, 0xF2, 0xF2))
        };
        private static readonly Brush[] PlateDark = new Brush[]
        {
            FrozenBrush(Color.FromArgb(0x2B, 0xFF, 0xFF, 0xFF)),
            FrozenBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)),
            FrozenBrush(Color.FromArgb(0x1C, 0xFF, 0xFF, 0xFF))
        };
        private static readonly Brush EdgeLight = FrozenGradient(Color.FromArgb(0xC0, 0xFF, 0xFF, 0xFF), Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF));
        private static readonly Brush EdgeDark = FrozenGradient(Color.FromArgb(0x38, 0xFF, 0xFF, 0xFF), Color.FromArgb(0x0C, 0xFF, 0xFF, 0xFF));

        private static Brush FrozenBrush(Color c)
        {
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }

        private static Brush FrozenGradient(Color top, Color bottom)
        {
            var b = new LinearGradientBrush(top, bottom, 90);
            b.Freeze();
            return b;
        }

        private sealed class PlateRefs
        {
            public Grid Root;
            public Border Surface;
            public ScaleTransform Scale;
            public bool Large;
        }

        private readonly Dictionary<Border, PlateRefs> _plates = new Dictionary<Border, PlateRefs>();

        /// <summary>Raised by the settings page when a purely visual flyout option changed (e.g. folder names).</summary>
        public static event EventHandler AppearanceChanged;

        public static void RaiseAppearanceChanged()
        {
            var h = AppearanceChanged;
            if (h != null) h(null, EventArgs.Empty);
        }

        private sealed class TileRefs
        {
            public Border Tile;
            public TrayFolder Folder;
            public TrayIconItem Item;
            public Image[] Previews;
            public Image Icon;
            public bool Large;
            public int PreviewOffset;
        }

        private readonly OrganizerController _controller;
        private readonly Action _openMainWindowCallback;
        private readonly Action<TrayFolder> _openForAddingCallback;
        private readonly List<TileRefs> _tiles = new List<TileRefs>();

        private TrayFolder _openFolder;
        private bool _isInExternalDragMode;
        private bool _openedForDrag;
        private DateTime _suppressDeactivateCloseUntil = DateTime.MinValue;
        private DateTime _lastClickTime = DateTime.MinValue;
        private TrayIconItem _lastClickedIcon;

        private string _structureSig = null;
        private string _lastViewKey = null;
        private Border _highlightedTile;
        private Border _pressedTile;

        // Context menu state
        private ContextMenu _menu;
        private bool _menuKeepOpen;

        // Close animation / hide bookkeeping
        private bool _closing;
        private int _closeVersion;
        private bool _hideStamped;

        // View transition (folder open / back) state
        private bool _navBusy;
        private int _viewAnimVersion;
        private bool _viewAnimPending;
        private Point _viewAnimOrigin;
        private TrayFolder _viewAnimBackFolder;

        // Auto-dismiss after a left click launched an app: hide once the mouse leaves the flyout.
        private bool _autoDismissArmed;
        private DispatcherTimer _leaveTimer;

        // Placement state (physical pixels): keeps the edge facing the taskbar anchored when the size changes.
        private bool _hasPlacement;
        private bool _placeBottom = true;
        private int _placeEdge;
        private int _placeAlign;   // 0 = left edge at _placeRef, 1 = centered on _placeRef, 2 = right edge at _placeRef
        private int _placeRef;
        private double _placeScale = 1.0;
        private System.Drawing.Rectangle _placeWork;

        public DateTime LastHiddenAtUtc { get; private set; }

        public FlyoutWindow(OrganizerController controller, Action openMainWindowCallback, Action<TrayFolder> openForAddingCallback)
        {
            // The flyout follows the TASKBAR theme (like real tray flyouts), so it carries its own
            // brush set; everything inside it, tooltips included, resolves brush keys through it.
            ThemeManager.ApplyThemeTo(Resources, !ThemeManager.IsTaskbarLight);
            InitializeComponent();
            _controller = controller;
            _openMainWindowCallback = openMainWindowCallback;
            _openForAddingCallback = openForAddingCallback;
            LastHiddenAtUtc = DateTime.MinValue;

            ThemeManager.ThemeChanged += (s, e) =>
            {
                ThemeManager.ApplyThemeTo(Resources, !ThemeManager.IsTaskbarLight);
                if (IsVisible && !_closing && !_navBusy)
                    RefreshView(true); // plate brushes are picked per theme
            };

            IsVisibleChanged += FlyoutWindow_IsVisibleChanged;
            SizeChanged += FlyoutWindow_SizeChanged;

            _controller.IconsUpdated += (s, e) =>
            {
                if (IsVisible && !_closing)
                    RefreshView(false);
            };

            // The signature contains the folder-name flag, so this rebuilds the root view when it changed.
            AppearanceChanged += (s, e) =>
            {
                if (IsVisible && !_closing)
                    RefreshView(false);
            };
        }

        private bool FlatMode
        {
            get { return _controller.Config != null && _controller.Config.FlyoutLayout == 1; }
        }

        private bool ShowNames
        {
            get { return _controller.Config != null && _controller.Config.ShowFolderNames; }
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            UpdateWindowExStyle(_isInExternalDragMode);
            ThemeManager.ApplyWindowChrome(this, NativeMethods.DWMSBT_TRANSIENTWINDOW, "FlyoutFallback", true, true);

            var src = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
            if (src != null) src.AddHook(WndProcHook);
        }

        private IntPtr WndProcHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_DPICHANGED && _hasPlacement)
            {
                // Windows suggests its own rect when the flyout crosses monitors; re-apply ours afterwards.
                Dispatcher.BeginInvoke(new Action(ApplyPlacement), DispatcherPriority.Background);
            }
            return IntPtr.Zero;
        }

        private void UpdateWindowExStyle(bool noActivate)
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;
            int exStyle = NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE);
            exStyle |= NativeMethods.WS_EX_TOOLWINDOW;
            if (noActivate)
                exStyle |= NativeMethods.WS_EX_NOACTIVATE;
            else
                exStyle &= ~NativeMethods.WS_EX_NOACTIVATE;
            NativeMethods.SetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE, exStyle);
        }

        // ------------------------------------------------------------------
        // Visibility / animation
        // ------------------------------------------------------------------

        private bool AnimationsOn
        {
            get { return _controller.Config != null && _controller.Config.EnableFlyoutAnimations; }
        }

        private static void Tune(DoubleAnimation a)
        {
            Timeline.SetDesiredFrameRate(a, 60);
        }

        private void FlyoutWindow_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (IsVisible)
            {
                PlayEntrance();
            }
            else
            {
                if (!_hideStamped) LastHiddenAtUtc = DateTime.UtcNow;
                _hideStamped = false;
                _hasPlacement = false;
                _openedForDrag = false;
                _pressedTile = null;
                _autoDismissArmed = false;
                StopLeaveTimer();
                CloseMenu();
                SetHighlightedTile(null);

                _closing = false;
                _closeVersion++;
                _viewAnimPending = false;
                ResetViewAnim();
                ContentRoot.BeginAnimation(UIElement.OpacityProperty, null);
                ContentRoot.Opacity = 1;
                ContentTranslate.BeginAnimation(TranslateTransform.YProperty, null);
                ContentTranslate.Y = 0;
            }
        }

        private void CancelClose()
        {
            if (!_closing) return;
            _closing = false;
            _closeVersion++;
        }

        private void PlayEntrance()
        {
            CancelClose();
            ContentRoot.BeginAnimation(UIElement.OpacityProperty, null);
            ContentTranslate.BeginAnimation(TranslateTransform.YProperty, null);
            if (!AnimationsOn)
            {
                ContentRoot.Opacity = 1;
                ContentTranslate.Y = 0;
                return;
            }

            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            var duration = new Duration(TimeSpan.FromMilliseconds(160));
            var fade = new DoubleAnimation(0, 1, duration) { EasingFunction = ease };
            var slide = new DoubleAnimation(_placeBottom ? 12 : -12, 0, duration) { EasingFunction = ease };
            Tune(fade);
            Tune(slide);
            ContentRoot.BeginAnimation(UIElement.OpacityProperty, fade);
            ContentTranslate.BeginAnimation(TranslateTransform.YProperty, slide);
        }

        /// <summary>
        /// Hides the flyout with a short fade + slide toward the taskbar (instantly when animations are off
        /// or while an external drag is in progress). LastHiddenAtUtc is stamped when the hide is requested.
        /// </summary>
        public void HideAnimated()
        {
            if (!IsVisible || _closing) return;
            LastHiddenAtUtc = DateTime.UtcNow;
            _hideStamped = true;

            if (!AnimationsOn || _isInExternalDragMode)
            {
                Hide();
                return;
            }

            CloseMenu();
            StopLeaveTimer();
            _closing = true;
            int ver = ++_closeVersion;

            var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
            var duration = new Duration(TimeSpan.FromMilliseconds(120));
            var fade = new DoubleAnimation(1, 0, duration) { EasingFunction = ease };
            var slide = new DoubleAnimation(0, _placeBottom ? 10 : -10, duration) { EasingFunction = ease };
            Tune(fade);
            Tune(slide);
            fade.Completed += (s, e) =>
            {
                if (ver != _closeVersion || !_closing) return;
                _closing = false;
                if (IsVisible) Hide();
            };
            ContentRoot.BeginAnimation(UIElement.OpacityProperty, fade);
            ContentTranslate.BeginAnimation(TranslateTransform.YProperty, slide);
        }

        // ---- folder open / back transition (opacity + scale of the tile panel only) ----

        private void ResetViewAnim()
        {
            _viewAnimVersion++;
            _navBusy = false;
            TilesWrapPanel.BeginAnimation(UIElement.OpacityProperty, null);
            TilesWrapPanel.Opacity = 1;
            TilesWrapPanel.CacheMode = null;
            TilesWrapPanel.RenderTransform = null;
        }

        private Point PanelCenter()
        {
            return new Point(TilesWrapPanel.ActualWidth / 2, TilesWrapPanel.ActualHeight / 2);
        }

        private Point CenterOfTile(Border tile)
        {
            try
            {
                PlateRefs pr;
                if (_plates.TryGetValue(tile, out pr))
                    return pr.Root.TranslatePoint(new Point(pr.Root.ActualWidth / 2, pr.Root.ActualHeight / 2), TilesWrapPanel);
                return tile.TranslatePoint(new Point(tile.ActualWidth / 2, tile.ActualHeight / 2), TilesWrapPanel);
            }
            catch
            {
                return PanelCenter();
            }
        }

        /// <summary>Opens a folder (or goes back to the root when target is null), animated when enabled.</summary>
        private void NavigateTo(TrayFolder target, Border clicked)
        {
            if (_navBusy || _closing) return;
            _autoDismissArmed = false;
            StopLeaveTimer();
            TrayFolder from = _openFolder;

            if (!AnimationsOn || !IsVisible || _isInExternalDragMode)
            {
                _openFolder = target;
                RefreshView(true);
                return;
            }

            Point origin = clicked != null ? CenterOfTile(clicked) : PanelCenter();
            ResetViewAnim();
            int ver = _viewAnimVersion;
            _navBusy = true;

            var st = new ScaleTransform(1, 1, origin.X, origin.Y);
            TilesWrapPanel.RenderTransform = st;
            TilesWrapPanel.CacheMode = new BitmapCache();

            var duration = new Duration(TimeSpan.FromMilliseconds(80));
            var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
            var fade = new DoubleAnimation(1, 0, duration) { EasingFunction = ease };
            // Opening a folder: the folder view swells past the viewer; going back: the folder contents shrink away.
            double outScale = target != null ? 1.12 : 0.9;
            var sx = new DoubleAnimation(1, outScale, duration) { EasingFunction = ease };
            var sy = new DoubleAnimation(1, outScale, duration) { EasingFunction = ease };
            Tune(fade);
            Tune(sx);
            Tune(sy);
            fade.Completed += (s, e) =>
            {
                if (ver != _viewAnimVersion) return;
                _navBusy = false;
                if (!IsVisible || _closing) return;
                _openFolder = target;
                _viewAnimPending = true;
                _viewAnimOrigin = origin;
                _viewAnimBackFolder = from;
                RefreshView(true);
                _viewAnimPending = false;
                if (_viewAnimVersion == ver) ResetViewAnim(); // no view change happened: undo the held fade-out
            };
            TilesWrapPanel.BeginAnimation(UIElement.OpacityProperty, fade);
            st.BeginAnimation(ScaleTransform.ScaleXProperty, sx);
            st.BeginAnimation(ScaleTransform.ScaleYProperty, sy);
        }

        private void PlayViewIn()
        {
            ResetViewAnim();
            int ver = _viewAnimVersion;
            TilesWrapPanel.UpdateLayout();

            Point origin = _viewAnimOrigin;
            if (_openFolder == null)
            {
                origin = PanelCenter();
                if (_viewAnimBackFolder != null)
                {
                    foreach (var child in TilesWrapPanel.Children.OfType<Border>())
                    {
                        if (ReferenceEquals(child.Tag, _viewAnimBackFolder))
                        {
                            origin = CenterOfTile(child);
                            break;
                        }
                    }
                }
            }

            // iOS-like: a folder's icons zoom out of the folder tile; coming back, the folder view settles from slightly enlarged.
            double startScale = _openFolder != null ? 0.55 : 1.1;
            var st = new ScaleTransform(startScale, startScale, origin.X, origin.Y);
            TilesWrapPanel.RenderTransform = st;
            TilesWrapPanel.CacheMode = new BitmapCache();

            var duration = new Duration(TimeSpan.FromMilliseconds(200));
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            var fade = new DoubleAnimation(0, 1, duration) { EasingFunction = ease };
            var sx = new DoubleAnimation(startScale, 1, duration) { EasingFunction = ease };
            var sy = new DoubleAnimation(startScale, 1, duration) { EasingFunction = ease };
            Tune(fade);
            Tune(sx);
            Tune(sy);
            fade.Completed += (s, e) =>
            {
                if (ver != _viewAnimVersion) return;
                ResetViewAnim();
            };
            TilesWrapPanel.BeginAnimation(UIElement.OpacityProperty, fade);
            st.BeginAnimation(ScaleTransform.ScaleXProperty, sx);
            st.BeginAnimation(ScaleTransform.ScaleYProperty, sy);
        }

        // ------------------------------------------------------------------
        // Show / hide entry points
        // ------------------------------------------------------------------

        public void ShowInteractiveNearTray()
        {
            ShowInteractiveCore(false, 0, 0);
        }

        public void ShowInteractiveNearTray(int anchorScreenX, int anchorScreenY)
        {
            ShowInteractiveCore(true, anchorScreenX, anchorScreenY);
        }

        private void ShowInteractiveCore(bool useAnchor, int anchorX, int anchorY)
        {
            bool wasClosing = _closing;
            CancelClose();

            _isInExternalDragMode = false;
            _openedForDrag = false;
            _autoDismissArmed = false;
            StopLeaveTimer();
            CloseMenu();
            ShowActivated = true;
            UpdateWindowExStyle(false);
            SetHighlightedTile(null);

            _openFolder = null;
            _viewAnimPending = false;
            RefreshView(true);

            if (!IsVisible)
                Show();
            UpdateLayout();

            if (useAnchor)
                PositionAtAnchor(anchorX, anchorY);
            else
                PositionNearTray(false);
            if (wasClosing)
                PlayEntrance();
            Activate();
        }

        public void ShowForExternalDrag(TrayIconItem draggedIcon, string sourceArea)
        {
            bool wasClosing = _closing;
            CancelClose();

            // Remember whether this drag is what opened the flyout (it then closes again if the drop misses).
            if (!(IsVisible && _isInExternalDragMode))
                _openedForDrag = !IsVisible;

            _isInExternalDragMode = true;
            _autoDismissArmed = false;
            StopLeaveTimer();
            CloseMenu();
            ShowActivated = false;
            UpdateWindowExStyle(true);

            _openFolder = null;
            _viewAnimPending = false;

            RefreshView(true);
            bool wasVisible = IsVisible;
            if (!wasVisible)
                Show();
            UpdateLayout();
            if (!wasVisible)
                PositionNearTray(true);
            if (wasClosing)
                PlayEntrance();
        }

        /// <summary>
        /// The external drag ended and the drop was not accepted by the flyout: if the flyout was
        /// opened just for this drag it hides immediately, otherwise it returns to interactive mode.
        /// </summary>
        public void EndExternalDragMode()
        {
            EndDrag(false);
        }

        private void EndDrag(bool accepted)
        {
            if (!_isInExternalDragMode) return;
            _isInExternalDragMode = false;
            DropOutline.Visibility = Visibility.Collapsed;
            SetHighlightedTile(null);

            if (!accepted && _openedForDrag)
            {
                _openedForDrag = false;
                if (IsVisible) Hide();
                return;
            }
            _openedForDrag = false;

            // A window that was shown without activation becomes a normal interactive one again.
            UpdateWindowExStyle(false);
            RefreshView(true);

            if (accepted && IsVisible)
            {
                // Keep it open and active so clicking elsewhere closes it through Deactivated.
                ShowActivated = true;
                Activate();
                if (!IsActive)
                    PixelPlacement.ForceForeground(new WindowInteropHelper(this).Handle);
            }
        }

        // ------------------------------------------------------------------
        // Positioning (physical pixels, per-monitor DPI)
        // ------------------------------------------------------------------

        private double GetWindowScale()
        {
            double scale = 1.0;
            var source = PresentationSource.FromVisual(this);
            if (source != null && source.CompositionTarget != null)
                scale = source.CompositionTarget.TransformToDevice.M11;
            return scale > 0 ? scale : 1.0;
        }

        private double CurrentHeight()
        {
            return ActualHeight > 0 ? ActualHeight : 300;
        }

        private double CurrentWidth()
        {
            if (ActualWidth > 0) return ActualWidth;
            return (double.IsNaN(Width) || Width <= 0) ? 200 : Width;
        }

        private static int Px(double dip, double scale)
        {
            return (int)Math.Round(dip * scale);
        }

        private void ApplyPlacement()
        {
            if (!_hasPlacement) return;
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;

            double s = _placeScale;
            int w = Px(CurrentWidth(), s);
            int h = Px(CurrentHeight(), s);
            int pad = Px(8, s);
            System.Drawing.Rectangle work = _placeWork;

            // Avoid a needless resize (and the WM_SIZE it causes) when WPF's own size is within rounding.
            NativeMethods.RECT cur;
            bool haveCur = NativeMethods.GetWindowRect(hwnd, out cur);
            bool sameSize = false;
            if (haveCur)
            {
                int cw = cur.Right - cur.Left;
                int ch = cur.Bottom - cur.Top;
                if (Math.Abs(cw - w) <= 1 && Math.Abs(ch - h) <= 1)
                {
                    sameSize = true;
                    w = cw;
                    h = ch;
                }
            }

            int top = _placeBottom ? _placeEdge - h : _placeEdge;
            top = Math.Min(top, work.Bottom - h - pad);
            top = Math.Max(top, work.Top + pad);

            int left;
            if (_placeAlign == 1) left = _placeRef - w / 2;
            else if (_placeAlign == 2) left = _placeRef - w;
            else left = _placeRef;
            left = Math.Min(left, work.Right - w - pad);
            left = Math.Max(left, work.Left + pad);

            if (haveCur && sameSize && cur.Left == left && cur.Top == top)
                return;

            PixelPlacement.Place(hwnd, left, top, w, h, !sameSize);
        }

        public void PositionNearTray(bool avoidOverflowFlyout)
        {
            System.Drawing.Rectangle primary;
            try { primary = System.Windows.Forms.Screen.PrimaryScreen.WorkingArea; }
            catch { primary = new System.Drawing.Rectangle(0, 0, 1280, 720); }

            System.Drawing.Point probe = new System.Drawing.Point(primary.Right - 1, primary.Bottom - 1);
            NativeMethods.RECT rOv = new NativeMethods.RECT();
            bool hasOverflow = false;
            if (avoidOverflowFlyout)
            {
                IntPtr ovWnd = NativeMethods.FindWindow("TopLevelWindowForOverflowXamlIsland", IntPtr.Zero);
                if (ovWnd != IntPtr.Zero && NativeMethods.IsWindowVisible(ovWnd) && NativeMethods.GetWindowRect(ovWnd, out rOv))
                {
                    hasOverflow = true;
                    probe = new System.Drawing.Point((rOv.Left + rOv.Right) / 2, (rOv.Top + rOv.Bottom) / 2);
                }
            }

            System.Drawing.Rectangle wa = primary;
            try { wa = System.Windows.Forms.Screen.FromPoint(probe).WorkingArea; }
            catch { }

            double s = PixelPlacement.GetMonitorScale(probe.X, probe.Y, GetWindowScale());
            int w = Px(CurrentWidth(), s);
            int margin = Px(12, s);
            int rightEdge = wa.Right - margin;
            int bottomEdge = wa.Bottom - margin;

            if (hasOverflow)
            {
                int gap = Px(8, s);
                if (rOv.Left - w - gap >= wa.Left)
                {
                    rightEdge = rOv.Left - gap;
                    bottomEdge = Math.Min(wa.Bottom - margin, rOv.Bottom);
                }
            }

            _placeWork = wa;
            _placeScale = s;
            _placeBottom = true;
            _placeEdge = bottomEdge;
            _placeAlign = 2;
            _placeRef = rightEdge;
            _hasPlacement = true;
            ApplyPlacement();
        }

        private void PositionAtAnchor(int anchorX, int anchorY)
        {
            System.Windows.Forms.Screen screen;
            try
            {
                screen = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point(anchorX, anchorY));
            }
            catch
            {
                PositionNearTray(false);
                return;
            }

            double s = PixelPlacement.GetMonitorScale(anchorX, anchorY, GetWindowScale());
            var wa = screen.WorkingArea;
            var b = screen.Bounds;
            int margin = Px(12, s);

            bool tbTop = wa.Top > b.Top;
            bool tbBottom = wa.Bottom < b.Bottom;
            bool tbLeft = wa.Left > b.Left;
            bool tbRight = wa.Right < b.Right;

            int align = 1;
            int refX = anchorX;
            bool placeBottom = true;
            int edge;

            if (tbLeft || tbRight)
            {
                // Vertical taskbar: sit beside it, near the bottom of the work area.
                if (tbLeft) { align = 0; refX = wa.Left + margin; }
                else { align = 2; refX = wa.Right - margin; }
                edge = wa.Bottom - margin;
            }
            else if (tbTop)
            {
                placeBottom = false;
                edge = wa.Top + margin;
            }
            else if (tbBottom)
            {
                edge = wa.Bottom - margin;
            }
            else
            {
                // Taskbar auto-hides: decide by which half of the screen the click came from.
                if (anchorY < b.Top + b.Height / 2)
                {
                    placeBottom = false;
                    edge = wa.Top + margin + Px(52, s);
                }
                else
                {
                    edge = wa.Bottom - margin - Px(52, s);
                }
            }

            _placeWork = wa;
            _placeScale = s;
            _placeBottom = placeBottom;
            _placeEdge = edge;
            _placeAlign = align;
            _placeRef = refX;
            _hasPlacement = true;
            ApplyPlacement();
        }

        private void FlyoutWindow_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (IsVisible && _hasPlacement)
                ApplyPlacement();
        }

        // ------------------------------------------------------------------
        // Drag & drop hit testing
        // ------------------------------------------------------------------

        private static bool IsDropTile(Border b)
        {
            return b.Tag is TrayFolder || string.Equals(b.Tag as string, NewFolderDropTag);
        }

        public void HighlightDropTargetAtScreenPoint(int screenX, int screenY)
        {
            if (!IsVisible) return;
            if (FlatMode) return; // the whole panel is the target (outline set while the drag is active)
            Border hitTile = null;
            if (IsElementHitByScreenPoint(ContentScroll, screenX, screenY))
            {
                foreach (var child in TilesWrapPanel.Children.OfType<Border>())
                {
                    if (IsDropTile(child) && IsElementHitByScreenPoint(child, screenX, screenY))
                    {
                        hitTile = child;
                        break;
                    }
                }
            }
            SetHighlightedTile(hitTile);
        }

        private void SetHighlightedTile(Border tile)
        {
            if (ReferenceEquals(tile, _highlightedTile)) return;
            var old = _highlightedTile;
            _highlightedTile = tile;
            if (old != null) ApplyTileVisual(old);
            if (tile != null) ApplyTileVisual(tile);
        }

        private static bool IsElementHitByScreenPoint(FrameworkElement el, int screenX, int screenY)
        {
            if (el == null || !el.IsVisible || el.ActualWidth <= 0 || el.ActualHeight <= 0)
                return false;
            try
            {
                Point topLeft = el.PointToScreen(new Point(0, 0));
                Point bottomRight = el.PointToScreen(new Point(el.ActualWidth, el.ActualHeight));
                return screenX >= topLeft.X && screenX <= bottomRight.X &&
                       screenY >= topLeft.Y && screenY <= bottomRight.Y;
            }
            catch
            {
                return false;
            }
        }

        public bool TryAcceptExternalDrop(int screenX, int screenY, TrayIconItem draggedIcon, string sourceArea)
        {
            if (!IsVisible || draggedIcon == null) return false;

            var hwnd = new WindowInteropHelper(this).Handle;
            NativeMethods.RECT rWin;
            if (hwnd == IntPtr.Zero || !NativeMethods.GetWindowRect(hwnd, out rWin) || !rWin.Contains(screenX, screenY))
                return false;

            TrayFolder targetFolder = null;

            if (!FlatMode && IsElementHitByScreenPoint(ContentScroll, screenX, screenY))
            {
                foreach (var child in TilesWrapPanel.Children.OfType<Border>())
                {
                    if (!IsElementHitByScreenPoint(child, screenX, screenY))
                        continue;
                    var tf = child.Tag as TrayFolder;
                    if (tf != null)
                    {
                        targetFolder = tf;
                        break;
                    }
                    if (string.Equals(child.Tag as string, NewFolderDropTag))
                    {
                        targetFolder = _controller.CreateFolder(string.Format("收纳组 {0}", _controller.Folders.Count + 1));
                        break;
                    }
                }
            }

            if (targetFolder == null)
            {
                targetFolder = _openFolder ?? _controller.Folders.FirstOrDefault() ?? _controller.CreateFolder("默认文件夹");
            }

            _controller.BindIconsToFolder(new[] { draggedIcon }, targetFolder,
                string.Format("外部拖拽落入浮层文件夹 (来源: {0}, 坐标: {1},{2})", sourceArea, screenX, screenY));
            if (!FlatMode)
                _openFolder = targetFolder;
            EndDrag(true);
            RefreshView(true);
            return true;
        }

        public void OpenSpecificFolder(TrayFolder folder)
        {
            _openFolder = folder;
            RefreshView(true);
        }

        // ------------------------------------------------------------------
        // View building (only folders and running icons are ever shown)
        // ------------------------------------------------------------------

        public void RebuildView()
        {
            RefreshView(true);
        }

        private List<TrayIconItem> GetOnlineMembers(string folderId)
        {
            return _controller.AllIcons
                .Where(x => x.FolderId == folderId && x.IsOnline)
                .ToList();
        }

        private static List<ImageSource> GetPreviews(List<TrayIconItem> onlineMembers)
        {
            return onlineMembers
                .Where(x => x.IconSource != null)
                .Select(x => x.IconSource)
                .Take(9)
                .ToList();
        }

        private string BuildSignature(List<TrayIconItem> members)
        {
            var sb = new StringBuilder();
            sb.Append(_isInExternalDragMode ? 'D' : 'N');
            if (FlatMode)
            {
                sb.Append('L');
                foreach (var m in GetFlatItems())
                    sb.Append(RuntimeHelpers.GetHashCode(m)).Append('|').Append(m.AppDisplayName).Append(';');
            }
            else if (_openFolder == null)
            {
                bool large = LargeFolders;
                sb.Append('R').Append(ShowNames ? 'T' : 't').Append(large ? 'B' : 'S');
                foreach (var f in _controller.Folders)
                {
                    var online = GetOnlineMembers(f.Id);
                    sb.Append(f.Id).Append('|').Append(f.Name).Append('|').Append(f.Glyph).Append('|');
                    if (large)
                    {
                        foreach (var m in online)
                            sb.Append(RuntimeHelpers.GetHashCode(m)).Append(',');
                        sb.Append(GetPreviews(online).Count);
                    }
                    else
                    {
                        sb.Append(GetPreviews(online).Count);
                    }
                    sb.Append(';');
                }
            }
            else
            {
                sb.Append('F').Append(_openFolder.Id).Append('|').Append(_openFolder.Name).Append(';');
                foreach (var m in members)
                    sb.Append(RuntimeHelpers.GetHashCode(m)).Append('|').Append(m.AppDisplayName).Append(';');
            }
            return sb.ToString();
        }

        /// <summary>Flat layout: all running organized icons, by folder order, then by binding order (fallback: name).</summary>
        private List<TrayIconItem> GetFlatItems()
        {
            var result = new List<TrayIconItem>();
            foreach (var folder in _controller.Folders)
            {
                var f = folder;
                var members = _controller.AllIcons
                    .Where(x => x.IsOnline && x.IsOrganized && x.FolderId == f.Id)
                    .ToList();
                var keyed = new List<KeyValuePair<int, TrayIconItem>>();
                foreach (var m in members)
                {
                    int idx = int.MaxValue;
                    for (int i = 0; i < f.Bindings.Count; i++)
                    {
                        var b = f.Bindings[i];
                        if (b.RegKey == m.RegKey ||
                            (string.Equals(b.ExeFileName, m.ExeFileName, StringComparison.OrdinalIgnoreCase) && b.Uid == m.Uid))
                        {
                            idx = i;
                            break;
                        }
                    }
                    keyed.Add(new KeyValuePair<int, TrayIconItem>(idx, m));
                }
                result.AddRange(keyed
                    .OrderBy(k => k.Key)
                    .ThenBy(k => k.Value.AppDisplayName ?? "", StringComparer.CurrentCultureIgnoreCase)
                    .Select(k => k.Value));
            }
            return result;
        }

        private void RefreshView(bool force)
        {
            if (FlatMode) _openFolder = null;
            if (_openFolder != null && !_controller.Folders.Contains(_openFolder))
                _openFolder = null;

            List<TrayIconItem> members = _openFolder != null ? GetOnlineMembers(_openFolder.Id) : null;
            string sig = BuildSignature(members);

            // The periodic refresh only patches tooltips / images in place: no rebuild, no animation restart.
            if (!force && sig == _structureSig)
            {
                UpdateTilesInPlace();
                return;
            }
            _structureSig = sig;

            string viewKey = _openFolder != null ? _openFolder.Id : "";
            bool viewChanged = viewKey != _lastViewKey;
            _lastViewKey = viewKey;

            TilesWrapPanel.Children.Clear();
            _tiles.Clear();
            _plates.Clear();
            _highlightedTile = null;
            _pressedTile = null;

            if (FlatMode)
                BuildFlatTiles();
            else if (_openFolder == null)
                BuildRootTiles();
            else
                BuildFolderTiles(members);

            DropOutline.Visibility = (FlatMode && _isInExternalDragMode) ? Visibility.Visible : Visibility.Collapsed;

            if (viewChanged)
            {
                ContentScroll.ScrollToTop();
                if (_viewAnimPending && AnimationsOn && IsVisible && !_isInExternalDragMode)
                    PlayViewIn();
                else
                    ResetViewAnim();
                _viewAnimPending = false;
            }
        }

        private static int ClampCols(int count, int min, int max)
        {
            return Math.Max(min, Math.Min(max, count));
        }

        private void BuildRootTiles()
        {
            var folders = _controller.Folders.ToList();
            int count = folders.Count + (_isInExternalDragMode ? 1 : 0);
            bool names = ShowNames;
            bool large = LargeFolders;
            int cols = large ? ClampCols(count, 2, 3) : ClampCols(count, 3, names ? 4 : 6);
            double slot = FolderTileW(large, names) + TileMargin * 2;
            double slotH = FolderTileH(large, names) + TileMargin * 2;
            TilesWrapPanel.Width = cols * slot;
            TilesWrapPanel.Margin = new Thickness(PanelPadding);

            foreach (var folder in folders)
                TilesWrapPanel.Children.Add(CreateFolderTile(folder));

            if (_isInExternalDragMode)
                TilesWrapPanel.Children.Add(CreateNewFolderDropTile());

            if (count == 0)
                TilesWrapPanel.Children.Add(CreateEmptyMark(cols * slot, slotH));
        }

        private void BuildFlatTiles()
        {
            var items = GetFlatItems();
            int cols = ClampCols(items.Count, 1, 6);
            double slot = IconTile + TileMargin * 2;
            TilesWrapPanel.Width = cols * slot;
            TilesWrapPanel.Margin = new Thickness(PanelPadding);

            foreach (var item in items)
                TilesWrapPanel.Children.Add(CreateIconTile(item));

            if (items.Count == 0)
                TilesWrapPanel.Children.Add(new Border { Width = IconTile, Height = IconTile, Margin = new Thickness(TileMargin), IsHitTestVisible = false });
        }

        private void BuildFolderTiles(List<TrayIconItem> members)
        {
            int count = 1 + members.Count;
            int cols = ClampCols(count, 3, 6);
            double slot = IconTile + TileMargin * 2;
            TilesWrapPanel.Width = cols * slot;
            TilesWrapPanel.Margin = new Thickness(PanelPadding);

            TilesWrapPanel.Children.Add(CreateBackTile());
            foreach (var item in members)
                TilesWrapPanel.Children.Add(CreateIconTile(item));
        }

        private void UpdateTilesInPlace()
        {
            foreach (var r in _tiles)
            {
                if (r.Folder != null)
                {
                    string tip = r.Folder.Name;
                    if (!string.Equals(r.Tile.ToolTip as string, tip))
                        r.Tile.ToolTip = tip;

                    var online = GetOnlineMembers(r.Folder.Id);
                    List<ImageSource> imgs = r.Large
                        ? online.Skip(r.PreviewOffset).Where(x => x.IconSource != null).Select(x => x.IconSource).Take(4).ToList()
                        : GetPreviews(online);
                    for (int i = 0; i < r.Previews.Length; i++)
                    {
                        ImageSource src = i < imgs.Count ? imgs[i] : null;
                        if (!ReferenceEquals(r.Previews[i].Source, src))
                            r.Previews[i].Source = src;
                    }
                }
                else if (r.Item != null)
                {
                    string tip = IconToolTip(r.Item);
                    if (!string.Equals(r.Tile.ToolTip as string, tip))
                        r.Tile.ToolTip = tip;
                    if (r.Icon != null && !ReferenceEquals(r.Icon.Source, r.Item.IconSource))
                        r.Icon.Source = r.Item.IconSource;
                }
            }
        }

        // ------------------------------------------------------------------
        // Tile factories
        // ------------------------------------------------------------------

        private static FontFamily IconFontFamily
        {
            get
            {
                var ff = Application.Current.TryFindResource("IconFont") as FontFamily;
                return ff ?? new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");
            }
        }

        private static TextBlock CreateGlyph(string glyph, double size, string brushKey)
        {
            var tb = new TextBlock
            {
                Text = glyph,
                FontFamily = IconFontFamily,
                FontSize = size,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            tb.SetResourceReference(TextBlock.ForegroundProperty, brushKey);
            return tb;
        }

        private static string IconToolTip(TrayIconItem item)
        {
            string t = (item.ToolTip ?? "").Trim();
            return t.Length > 0 ? t : item.AppDisplayName;
        }

        private Border CreateTileBorder(double width, double height, double margin, double radius, object tag)
        {
            return new Border
            {
                Width = width,
                Height = height,
                Margin = new Thickness(margin),
                CornerRadius = new CornerRadius(radius),
                Background = Brushes.Transparent,
                BorderBrush = Brushes.Transparent,
                BorderThickness = new Thickness(1),
                Tag = tag
            };
        }

        private void SetPlateScale(PlateRefs p, double target)
        {
            if (Math.Abs(p.Scale.ScaleX - target) < 0.0005) return;
            if (AnimationsOn && IsVisible)
            {
                var dur = new Duration(TimeSpan.FromMilliseconds(90));
                var ax = new DoubleAnimation(target, dur);
                var ay = new DoubleAnimation(target, dur);
                Tune(ax);
                Tune(ay);
                p.Scale.BeginAnimation(ScaleTransform.ScaleXProperty, ax);
                p.Scale.BeginAnimation(ScaleTransform.ScaleYProperty, ay);
            }
            else
            {
                p.Scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                p.Scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                p.Scale.ScaleX = target;
                p.Scale.ScaleY = target;
            }
        }

        /// <summary>Folder / "+" tile: the hit target stays transparent, the frosted plate carries the state.</summary>
        private void ApplyPlateVisual(Border tile, PlateRefs p)
        {
            bool light = ThemeManager.IsTaskbarLight;
            Brush[] fills = light ? PlateLight : PlateDark;
            bool over = tile.IsMouseOver;
            bool pressed = over && ReferenceEquals(tile, _pressedTile);

            if (ReferenceEquals(tile, _highlightedTile))
            {
                p.Surface.SetResourceReference(Border.BackgroundProperty, "AccentSubtle");
                p.Surface.SetResourceReference(Border.BorderBrushProperty, "AccentFill");
                SetPlateScale(p, 1.0);
                return;
            }

            p.Surface.Background = fills[pressed ? 2 : (over ? 1 : 0)];
            p.Surface.BorderBrush = light ? EdgeLight : EdgeDark;
            // Large folders hold clickable cells, so they never scale under the pointer.
            SetPlateScale(p, p.Large ? 1.0 : (pressed ? 0.97 : ((over && AnimationsOn) ? 1.04 : 1.0)));
        }

        /// <summary>The 46 DIP frosted plate (optionally with a 3x3 grid of member icons or a centered overlay).</summary>
        private PlateRefs CreatePlate(Border tile, List<ImageSource> previews, UIElement overlay, bool large, out Image[] images)
        {
            double size = large ? LargePlate : FolderPlate;
            var root = new Grid
            {
                Width = size,
                Height = size,
                IsHitTestVisible = large,
                SnapsToDevicePixels = true,
                UseLayoutRounding = true,
                RenderTransformOrigin = new Point(0.5, 0.5),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            var scale = new ScaleTransform(1, 1);
            root.RenderTransform = scale;

            var surface = new Border
            {
                CornerRadius = new CornerRadius(large ? LargeRadius : PlateRadius),
                BorderThickness = new Thickness(1),
                IsHitTestVisible = false
            };
            root.Children.Add(surface);

            int n = previews != null ? previews.Count : 0;
            images = new Image[n];
            if (n > 0)
            {
                var c = new Canvas { Width = FolderPlate, Height = FolderPlate, IsHitTestVisible = false, SnapsToDevicePixels = true };
                for (int i = 0; i < n; i++)
                {
                    var img = new Image
                    {
                        Source = previews[i],
                        Width = MiniIcon,
                        Height = MiniIcon,
                        Stretch = Stretch.Uniform,
                        SnapsToDevicePixels = true,
                        UseLayoutRounding = true
                    };
                    RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
                    Canvas.SetLeft(img, MiniPad + (i % 3) * (MiniIcon + MiniGap));
                    Canvas.SetTop(img, MiniPad + (i / 3) * (MiniIcon + MiniGap));
                    c.Children.Add(img);
                    images[i] = img;
                }
                root.Children.Add(c);
            }
            if (overlay != null)
                root.Children.Add(overlay);

            var refs = new PlateRefs { Root = root, Surface = surface, Scale = scale, Large = large };
            _plates[tile] = refs;
            ApplyPlateVisual(tile, refs);
            return refs;
        }

        private void ApplyTileVisual(Border tile)
        {
            PlateRefs plate;
            if (_plates.TryGetValue(tile, out plate))
            {
                ApplyPlateVisual(tile, plate);
                return;
            }

            if (ReferenceEquals(tile, _highlightedTile))
            {
                tile.SetResourceReference(Border.BackgroundProperty, "AccentSubtle");
                tile.SetResourceReference(Border.BorderBrushProperty, "AccentFill");
                return;
            }

            tile.BorderBrush = Brushes.Transparent;
            if (tile.IsMouseOver)
            {
                tile.SetResourceReference(Border.BackgroundProperty,
                    ReferenceEquals(tile, _pressedTile) ? "SubtleFillPressed" : "SubtleFillHover");
            }
            else
            {
                tile.Background = Brushes.Transparent;
            }
        }

        /// <summary>System-style hover/pressed fills (no scaling, like the Windows 11 overflow panel).</summary>
        private void AttachHover(Border tile)
        {
            tile.MouseEnter += (s, e) => ApplyTileVisual(tile);
            tile.MouseLeave += (s, e) =>
            {
                if (ReferenceEquals(_pressedTile, tile)) _pressedTile = null;
                ApplyTileVisual(tile);
            };
            tile.MouseLeftButtonDown += (s, e) =>
            {
                _pressedTile = tile;
                ApplyTileVisual(tile);
            };
            tile.MouseLeftButtonUp += (s, e) =>
            {
                if (ReferenceEquals(_pressedTile, tile)) _pressedTile = null;
                ApplyTileVisual(tile);
            };
        }

        /// <summary>A 16x16 DIP tray-style icon, drawn on device pixels with high-quality resampling.</summary>
        private static Image CreateTrayImage(ImageSource source)
        {
            return CreateTrayImage(source, IconSize);
        }

        private static Image CreateTrayImage(ImageSource source, double size)
        {
            var img = new Image
            {
                Source = source,
                Width = size,
                Height = size,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                SnapsToDevicePixels = true,
                UseLayoutRounding = true
            };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
            RenderOptions.SetEdgeMode(img, EdgeMode.Aliased);
            return img;
        }

        private TextBlock CreateNameText(string text, double width)
        {
            var tb = new TextBlock
            {
                Text = text,
                Width = width,
                Height = NameH,
                FontSize = 12,
                TextAlignment = TextAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                TextWrapping = TextWrapping.NoWrap,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 2, 0, 0),
                IsHitTestVisible = false
            };
            tb.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimary");
            return tb;
        }

        private bool LargeFolders
        {
            get { return _controller.Config != null && _controller.Config.FolderSize == 1; }
        }

        private static double FolderTileW(bool large, bool names)
        {
            return large ? LargeTile : (names ? NamedTileW : FolderTile);
        }

        private static double FolderTileH(bool large, bool names)
        {
            if (large) return names ? LargeTile + 22 : LargeTile;
            return names ? NamedTileH : FolderTile;
        }

        /// <summary>Puts the plate (centered in a square slot) and, when names are on, the caption under it.</summary>
        private static UIElement ComposeFolderTileContent(UIElement glyph, TextBlock name, double slotSize, double slotWidth)
        {
            var f = glyph as FrameworkElement;
            if (f != null)
            {
                f.HorizontalAlignment = HorizontalAlignment.Center;
                f.VerticalAlignment = VerticalAlignment.Center;
            }
            if (name == null)
                return glyph;

            var slot = new Grid { Height = slotSize, Width = slotWidth };
            slot.Children.Add(glyph);
            var stack = new StackPanel
            {
                Orientation = Orientation.Vertical,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
            stack.Children.Add(slot);
            stack.Children.Add(name);
            return stack;
        }

        private Border CreateFolderTile(TrayFolder folder)
        {
            bool large = LargeFolders;
            bool names = ShowNames;
            double tw = FolderTileW(large, names);
            double th = FolderTileH(large, names);
            var online = GetOnlineMembers(folder.Id);

            var tile = CreateTileBorder(tw, th, TileMargin, TileRadius, folder);
            tile.ToolTip = folder.Name;

            Image[] images;
            PlateRefs plate;
            var refs = new TileRefs { Tile = tile, Folder = folder, Large = large };
            if (large)
            {
                Canvas cells = BuildLargeCells(online, out images);
                plate = CreatePlate(tile, null, cells, true, out images);
                refs.PreviewOffset = 3;
                refs.Previews = LargeMiniImages(cells);
            }
            else
            {
                plate = CreatePlate(tile, GetPreviews(online), null, false, out images);
                refs.Previews = images;
            }

            double slotSize = large ? LargeTile : FolderTile;
            tile.Child = ComposeFolderTileContent(plate.Root, names ? CreateNameText(folder.Name, tw - 8) : null, slotSize, tw - 8);

            AttachHover(tile);
            tile.MouseLeftButtonUp += (s, e) =>
            {
                e.Handled = true;
                if (_isInExternalDragMode) return;
                NavigateTo(folder, tile);
            };
            tile.MouseRightButtonUp += (s, e) =>
            {
                e.Handled = true;
                ShowFlyoutMenu(folder);
            };

            _tiles.Add(refs);
            return tile;
        }

        /// <summary>
        /// Large folder: 2x2 cells of 40 DIP. Up to 4 members are directly clickable app cells; with more than 4,
        /// three app cells plus a 4th cell holding a mini 3x3 grid of the rest (that cell is not a hit target, so it opens the folder).
        /// </summary>
        private Canvas BuildLargeCells(List<TrayIconItem> online, out Image[] unused)
        {
            unused = new Image[0];
            var canvas = new Canvas { Width = LargePlate, Height = LargePlate, SnapsToDevicePixels = true };
            bool overflow = online.Count > 4;
            int appCells = overflow ? 3 : Math.Min(4, online.Count);

            for (int i = 0; i < appCells; i++)
            {
                var item = online[i];
                var cell = CreateTileBorder(LargeCell, LargeCell, 0, 8, item);
                cell.ToolTip = IconToolTip(item);
                var img = CreateTrayImage(item.IconSource, LargeIcon);
                cell.Child = img;
                AttachHover(cell);
                WireAppActions(cell, item);
                cell.MouseLeftButtonDown += (s, e) => { e.Handled = true; };
                Canvas.SetLeft(cell, LargePad + (i % 2) * (LargeCell + 4));
                Canvas.SetTop(cell, LargePad + (i / 2) * (LargeCell + 4));
                canvas.Children.Add(cell);
                _tiles.Add(new TileRefs { Tile = cell, Item = item, Icon = img });
            }

            if (overflow)
            {
                var mini = new Canvas { Width = LargeCell, Height = LargeCell, IsHitTestVisible = false, SnapsToDevicePixels = true, Tag = "MINI" };
                var rest = online.Skip(3).Where(x => x.IconSource != null).Take(4).ToList();
                for (int j = 0; j < rest.Count; j++)
                {
                    var img = new Image
                    {
                        Source = rest[j].IconSource,
                        Width = LargeMini,
                        Height = LargeMini,
                        Stretch = Stretch.Uniform,
                        SnapsToDevicePixels = true,
                        UseLayoutRounding = true
                    };
                    RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
                    Canvas.SetLeft(img, LargeMiniPad + (j % 2) * (LargeMini + 4));
                    Canvas.SetTop(img, LargeMiniPad + (j / 2) * (LargeMini + 4));
                    mini.Children.Add(img);
                }
                Canvas.SetLeft(mini, LargePad + LargeCell + 4);
                Canvas.SetTop(mini, LargePad + LargeCell + 4);
                canvas.Children.Add(mini);
            }
            return canvas;
        }

        private static Image[] LargeMiniImages(Canvas cells)
        {
            foreach (UIElement el in cells.Children)
            {
                var c = el as Canvas;
                if (c != null && string.Equals(c.Tag as string, "MINI"))
                    return c.Children.OfType<Image>().ToArray();
            }
            return new Image[0];
        }

        private Border CreateNewFolderDropTile()
        {
            bool large = LargeFolders;
            bool names = ShowNames;
            double tw = FolderTileW(large, names);
            double th = FolderTileH(large, names);
            var tile = CreateTileBorder(tw, th, TileMargin, TileRadius, NewFolderDropTag);

            TextBlock plus = CreateGlyph("", large ? 28 : IconSize + 2, "AccentText");
            plus.IsHitTestVisible = false;
            Image[] images;
            PlateRefs plate = CreatePlate(tile, null, plus, large, out images);
            tile.Child = ComposeFolderTileContent(plate.Root, names ? CreateNameText("新建文件夹", tw - 8) : null,
                large ? LargeTile : FolderTile, tw - 8);

            AttachHover(tile);
            return tile;
        }

        private UIElement CreateEmptyMark(double width, double height)
        {
            var mark = new Border
            {
                Width = width,
                Height = height,
                IsHitTestVisible = false,
                Child = CreateGlyph(FolderGlyph, 22, "TextTertiary")
            };
            return mark;
        }

        private Border CreateBackTile()
        {
            var tile = CreateTileBorder(IconTile, IconTile, TileMargin, TileRadius, BackTag);
            tile.ToolTip = "返回";
            tile.Child = CreateGlyph("", IconSize, "TextPrimary");
            AttachHover(tile);
            tile.MouseLeftButtonUp += (s, e) =>
            {
                e.Handled = true;
                if (_isInExternalDragMode) return;
                NavigateTo(null, null);
            };
            // Right click on the back tile falls through to the background menu.
            return tile;
        }

        private Border CreateIconTile(TrayIconItem item)
        {
            var tile = CreateTileBorder(IconTile, IconTile, TileMargin, TileRadius, item);
            tile.ToolTip = IconToolTip(item);

            var img = CreateTrayImage(item.IconSource);
            tile.Child = img;

            AttachHover(tile);
            WireAppActions(tile, item);

            _tiles.Add(new TileRefs { Tile = tile, Item = item, Icon = img });
            return tile;
        }

        /// <summary>Left / double / right click pass-through to the tray app (app tiles and large-folder cells).</summary>
        private void WireAppActions(Border tile, TrayIconItem item)
        {
            tile.MouseLeftButtonUp += (s, e) =>
            {
                e.Handled = true;
                if (!item.IsOnline) return;
                _suppressDeactivateCloseUntil = DateTime.UtcNow.AddMilliseconds(1200);

                var now = DateTime.UtcNow;
                if (_lastClickedIcon == item && (now - _lastClickTime).TotalMilliseconds <= NativeMethods.GetDoubleClickTime())
                {
                    _lastClickedIcon = null;
                    TrayActionDispatcher.InvokeDoubleClick(item);
                }
                else
                {
                    _lastClickedIcon = item;
                    _lastClickTime = now;
                    TrayActionDispatcher.InvokeLeftClick(item);
                }

                // Like the official ^ overflow: once the app was launched/activated, close when the mouse leaves.
                _autoDismissArmed = true;
                if (!ContentRoot.IsMouseOver)
                    StartLeaveTimer();
            };

            tile.MouseRightButtonUp += (s, e) =>
            {
                e.Handled = true;
                if (!item.IsOnline) return;
                _autoDismissArmed = false;
                StopLeaveTimer();
                _suppressDeactivateCloseUntil = DateTime.UtcNow.AddMilliseconds(2000);
                TrayActionDispatcher.InvokeContextMenu(item);
            };
        }

        // ------------------------------------------------------------------
        // Auto-dismiss (mouse left the flyout after a left click launched an app)
        // ------------------------------------------------------------------

        private void StartLeaveTimer()
        {
            if (_leaveTimer == null)
            {
                _leaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
                _leaveTimer.Tick += LeaveTimer_Tick;
            }
            _leaveTimer.Stop();
            _leaveTimer.Start();
        }

        private void StopLeaveTimer()
        {
            if (_leaveTimer != null) _leaveTimer.Stop();
        }

        private void LeaveTimer_Tick(object sender, EventArgs e)
        {
            _leaveTimer.Stop();
            if (!_autoDismissArmed || !IsVisible || _isInExternalDragMode) return;
            if (ContentRoot.IsMouseOver) return;
            if (_menu != null && _menu.IsOpen) return;
            _autoDismissArmed = false;
            HideAnimated();
        }

        private void ContentRoot_MouseEnter(object sender, MouseEventArgs e)
        {
            StopLeaveTimer();
        }

        private void ContentRoot_MouseLeave(object sender, MouseEventArgs e)
        {
            if (_autoDismissArmed && !_isInExternalDragMode)
                StartLeaveTimer();
        }

        // ------------------------------------------------------------------
        // Context menu (flyout background / folder tiles; app tiles keep the app's native menu)
        // ------------------------------------------------------------------

        private static TextBlock MenuGlyph(string glyph)
        {
            return new TextBlock
            {
                Text = glyph,
                FontFamily = IconFontFamily,
                FontSize = 14,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
        }

        private void CloseMenu()
        {
            if (_menu != null && _menu.IsOpen)
                _menu.IsOpen = false;
        }

        private void ContentRoot_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            ShowFlyoutMenu(null);
        }

        private void ShowFlyoutMenu(TrayFolder contextFolder)
        {
            if (_isInExternalDragMode || _closing || _navBusy) return;
            CloseMenu();

            TrayFolder addTarget = FlatMode ? null : (contextFolder ?? _openFolder);

            var menu = new ContextMenu();
            // The flyout follows the taskbar theme, so the menu gets the same brush set.
            ThemeManager.ApplyThemeTo(menu.Resources, !ThemeManager.IsTaskbarLight);
            menu.PlacementTarget = ContentRoot;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint;

            var openMain = new MenuItem { Header = "打开管理界面", Icon = MenuGlyph("") };
            openMain.Click += (s, e) =>
            {
                HideAnimated();
                if (_openMainWindowCallback != null)
                    _openMainWindowCallback();
            };
            menu.Items.Add(openMain);

            var addIcons = new MenuItem { Header = "添加图标…", Icon = MenuGlyph("") };
            addIcons.Click += (s, e) =>
            {
                HideAnimated();
                if (_openForAddingCallback != null)
                    _openForAddingCallback(addTarget);
                else if (_openMainWindowCallback != null)
                    _openMainWindowCallback();
            };
            menu.Items.Add(addIcons);

            var newFolder = new MenuItem { Header = "新建文件夹", Icon = MenuGlyph("") };
            newFolder.Click += (s, e) =>
            {
                _menuKeepOpen = true;
                _controller.CreateFolder(string.Format("新建文件夹 {0}", _controller.Folders.Count + 1));
                RefreshView(true);
            };
            if (!FlatMode)
                menu.Items.Add(newFolder);

            if (_openFolder != null && !FlatMode)
            {
                menu.Items.Add(new Separator());
                var back = new MenuItem { Header = "返回", Icon = MenuGlyph("") };
                back.Click += (s, e) =>
                {
                    _menuKeepOpen = true;
                    NavigateTo(null, null);
                };
                menu.Items.Add(back);
            }

            _menuKeepOpen = false;
            menu.Closed += Menu_Closed;
            _menu = menu;
            _suppressDeactivateCloseUntil = DateTime.UtcNow.AddMilliseconds(400);
            menu.IsOpen = true;
        }

        private void Menu_Closed(object sender, RoutedEventArgs e)
        {
            if (!ReferenceEquals(sender, _menu)) return;
            _menu = null;

            // Decide shortly after the menu is gone: dismissed by clicking elsewhere -> close the flyout too.
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(220) };
            t.Tick += (s, ev) =>
            {
                t.Stop();
                if (!IsVisible || _closing || _isInExternalDragMode || _menu != null) return;
                if (_menuKeepOpen)
                {
                    if (!IsActive) Activate();
                }
                else if (!IsActive)
                {
                    HideAnimated();
                }
            };
            t.Start();
        }

        // ------------------------------------------------------------------
        // Event handlers
        // ------------------------------------------------------------------

        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Escape) return;
            e.Handled = true;
            if (_openFolder != null && !_isInExternalDragMode)
                NavigateTo(null, null);
            else
                HideAnimated();
        }

        private void Window_Deactivated(object sender, EventArgs e)
        {
            if (_isInExternalDragMode) return;
            if (_menu != null && _menu.IsOpen) return;
            if (DateTime.UtcNow < _suppressDeactivateCloseUntil) return;
            HideAnimated();
        }
    }
}
