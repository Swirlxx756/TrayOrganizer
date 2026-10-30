using System;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using TrayOrganizer.Core;

namespace TrayOrganizer.UI
{
    public partial class DragOverlayWindow : Window
    {
        public DragOverlayWindow()
        {
            InitializeComponent();
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            var hwnd = new WindowInteropHelper(this).Handle;
            int exStyle = NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE);
            NativeMethods.SetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE,
                exStyle | NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_TRANSPARENT);
        }

        public void ShowForDrag(TrayIconItem item, string sourceArea, int screenX, int screenY)
        {
            if (item == null) return;
            IconImg.Source = item.IconSource;
            TitleText.Text = string.Format("拖入: {0}", item.AppDisplayName);
            SubText.Text = string.Format("来源: {0} · 松开落入文件夹", sourceArea);
            if (!IsVisible)
            {
                _lastScale = 0;
                Show();
            }
            UpdatePosition(screenX, screenY);
        }

        private double _lastScale;

        public void UpdatePosition(int screenX, int screenY)
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;

            double windowScale = 1.0;
            var source = PresentationSource.FromVisual(this);
            if (source != null && source.CompositionTarget != null)
                windowScale = source.CompositionTarget.TransformToDevice.M11;
            if (windowScale <= 0) windowScale = 1.0;

            // Scale of the monitor under the cursor; the overlay is placed purely in physical pixels.
            double s = PixelPlacement.GetMonitorScale(screenX, screenY, windowScale);

            // The window has 12/8/12/16 of transparent margin around the card for its shadow.
            int x = screenX + (int)Math.Round(2 * s);
            int y = screenY - (int)Math.Round(78 * s);

            bool resize = Math.Abs(s - _lastScale) > 0.001;
            _lastScale = s;
            PixelPlacement.Place(hwnd, x, y, (int)Math.Round(Width * s), (int)Math.Round(Height * s), resize);
        }
    }
}
