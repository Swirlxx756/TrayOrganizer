using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;
using TrayOrganizer.Core;

namespace TrayOrganizer.UI
{
    /// <summary>
    /// Follows the Windows 11 light/dark mode and accent color, and publishes them as
    /// DynamicResource brushes (WinUI 3 color tokens) in Application.Resources.
    /// </summary>
    public static class ThemeManager
    {
        private sealed class ChromeEntry
        {
            public WeakReference Window;
            public bool FollowTaskbar;
        }

        private static readonly List<ChromeEntry> _chromeWindows = new List<ChromeEntry>();
        private static bool _hooked;
        // Test-only override: --theme=dark|light on the command line forces both themes.
        private static bool _forcedChecked;
        private static bool _forced;
        private static bool _forcedDark;
        // User setting: 0 = follow system, 1 = light, 2 = dark.
        private static int _mode;
        private static bool _initialized;

        public static bool IsDark { get; private set; }
        public static bool IsTaskbarLight { get; private set; }
        public static Color AccentBase { get; private set; }

        public static readonly int WindowsBuild = NativeMethods.GetWindowsBuildNumber();
        public static bool SupportsSystemBackdrop { get { return WindowsBuild >= 22621; } }

        public static event EventHandler ThemeChanged;

        public static void Initialize()
        {
            ParseForcedTheme();
            ReadSystemTheme();
            ApplyResources();
            ApplyNativeMenuTheme();
            _initialized = true;

            if (!_hooked && !_forced)
            {
                _hooked = true;
                SystemEvents.UserPreferenceChanged += (s, e) =>
                {
                    if (e.Category != UserPreferenceCategory.General &&
                        e.Category != UserPreferenceCategory.Color &&
                        e.Category != UserPreferenceCategory.VisualStyle)
                        return;
                    var app = Application.Current;
                    if (app == null) return;
                    app.Dispatcher.BeginInvoke(new Action(RefreshFromSystem));
                };
            }
        }

        private static void ParseForcedTheme()
        {
            if (_forcedChecked) return;
            _forcedChecked = true;
            try
            {
                foreach (string arg in Environment.GetCommandLineArgs())
                {
                    if (string.Equals(arg, "--theme=dark", StringComparison.OrdinalIgnoreCase))
                    {
                        _forced = true;
                        _forcedDark = true;
                    }
                    else if (string.Equals(arg, "--theme=light", StringComparison.OrdinalIgnoreCase))
                    {
                        _forced = true;
                        _forcedDark = false;
                    }
                }
            }
            catch { }
        }

        /// <summary>0 = follow system, 1 = light, 2 = dark. Safe to call before Initialize.</summary>
        public static void SetMode(int mode)
        {
            if (mode < 0 || mode > 2) mode = 0;
            if (_mode == mode) return;
            _mode = mode;
            if (!_initialized || Application.Current == null) return;
            Reapply(true);
        }

        public static void RefreshFromSystem()
        {
            Reapply(false);
        }

        private static void Reapply(bool force)
        {
            if (_forced) return;
            bool oldDark = IsDark, oldTaskbar = IsTaskbarLight;
            Color oldAccent = AccentBase;
            ReadSystemTheme();
            if (!force && oldDark == IsDark && oldTaskbar == IsTaskbarLight && oldAccent == AccentBase)
                return;

            ApplyResources();
            ApplyNativeMenuTheme();
            foreach (var entry in _chromeWindows.ToArray())
            {
                var w = entry.Window.Target as Window;
                if (w == null) { _chromeWindows.Remove(entry); continue; }
                ApplyDarkTitleBar(new WindowInteropHelper(w).Handle, entry.FollowTaskbar ? !IsTaskbarLight : IsDark);
            }
            var h = ThemeChanged;
            if (h != null) h(null, EventArgs.Empty);
        }

        private static void ReadSystemTheme()
        {
            IsDark = false;
            IsTaskbarLight = true;
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize"))
                {
                    if (key != null)
                    {
                        object apps = key.GetValue("AppsUseLightTheme");
                        object sys = key.GetValue("SystemUsesLightTheme");
                        IsDark = apps is int && (int)apps == 0;
                        IsTaskbarLight = !(sys is int && (int)sys == 0);
                    }
                }
            }
            catch { }

            if (_forced)
            {
                IsDark = _forcedDark;
                IsTaskbarLight = !_forcedDark;
            }
            else if (_mode == 1 || _mode == 2)
            {
                IsDark = _mode == 2;
                IsTaskbarLight = _mode != 2;
            }

            _accentPalette = ReadAccentPalette();
            AccentBase = _accentPalette[3];
        }

        // [Light3, Light2, Light1, Base, Dark1, Dark2, Dark3]
        private static Color[] _accentPalette;

        private static Color[] ReadAccentPalette()
        {
            var fallback = new[]
            {
                Rgb(0x99, 0xEB, 0xFF), Rgb(0x4C, 0xC2, 0xFF), Rgb(0x00, 0x91, 0xF8), Rgb(0x00, 0x78, 0xD4),
                Rgb(0x00, 0x5F, 0xB8), Rgb(0x00, 0x4C, 0x87), Rgb(0x00, 0x3E, 0x92)
            };
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Accent"))
                {
                    var bytes = key != null ? key.GetValue("AccentPalette") as byte[] : null;
                    if (bytes != null && bytes.Length >= 28)
                    {
                        var result = new Color[7];
                        for (int i = 0; i < 7; i++)
                            result[i] = Rgb(bytes[i * 4], bytes[i * 4 + 1], bytes[i * 4 + 2]);
                        return result;
                    }
                }
            }
            catch { }
            return fallback;
        }

        private static Color Rgb(byte r, byte g, byte b) { return Color.FromRgb(r, g, b); }
        private static Color Argb(uint argb)
        {
            return Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);
        }
        private static Color WithAlpha(Color c, byte a) { return Color.FromArgb(a, c.R, c.G, c.B); }

        private static void ApplyResources()
        {
            FillResources(Application.Current.Resources, IsDark);
        }

        /// <summary>
        /// Writes the full brush set for the given theme into a resource dictionary. Used for the
        /// application resources (app theme) and for the flyout's own resources (taskbar theme).
        /// </summary>
        public static void ApplyThemeTo(ResourceDictionary target, bool dark)
        {
            if (target == null) return;
            if (_accentPalette == null) { _accentPalette = ReadAccentPalette(); AccentBase = _accentPalette[3]; }
            FillResources(target, dark);
        }

        private static void FillResources(ResourceDictionary res, bool d)
        {
            Color accent = d ? _accentPalette[1] : _accentPalette[4];

            Set(res, "TextPrimary", d ? Argb(0xFFFFFFFF) : Argb(0xE4000000));
            Set(res, "TextSecondary", d ? Argb(0xC5FFFFFF) : Argb(0x9E000000));
            Set(res, "TextTertiary", d ? Argb(0x87FFFFFF) : Argb(0x72000000));
            Set(res, "TextDisabled", d ? Argb(0x5DFFFFFF) : Argb(0x5C000000));
            Set(res, "TextOnAccent", d ? Argb(0xFF000000) : Argb(0xFFFFFFFF));
            Set(res, "TextOnAccentSecondary", d ? Argb(0x80000000) : Argb(0xB3FFFFFF));
            Set(res, "TextOnAccentDisabled", d ? Argb(0x87FFFFFF) : Argb(0xFFFFFFFF));

            Set(res, "AccentFill", accent);
            Set(res, "AccentFillHover", WithAlpha(accent, 0xE6));
            Set(res, "AccentFillPressed", WithAlpha(accent, 0xCC));
            Set(res, "AccentFillDisabled", d ? Argb(0x28FFFFFF) : Argb(0x37000000));
            Set(res, "AccentText", d ? _accentPalette[0] : _accentPalette[5]);
            Set(res, "AccentSubtle", WithAlpha(accent, d ? (byte)0x26 : (byte)0x1A));

            Set(res, "ControlFill", d ? Argb(0x0FFFFFFF) : Argb(0xB3FFFFFF));
            Set(res, "ControlFillHover", d ? Argb(0x15FFFFFF) : Argb(0x80F9F9F9));
            Set(res, "ControlFillPressed", d ? Argb(0x08FFFFFF) : Argb(0x4DF9F9F9));
            Set(res, "ControlFillDisabled", d ? Argb(0x0BFFFFFF) : Argb(0x4DF9F9F9));
            Set(res, "ControlAltFill", d ? Argb(0x19000000) : Argb(0x06000000));
            Set(res, "ControlAltFillHover", d ? Argb(0x0BFFFFFF) : Argb(0x0F000000));
            Set(res, "ControlStroke", d ? Argb(0x12FFFFFF) : Argb(0x0F000000));
            Set(res, "ControlStrongStroke", d ? Argb(0x8BFFFFFF) : Argb(0x72000000));
            res["ControlElevationBorder"] = ElevationBorder(
                d ? Argb(0x18FFFFFF) : Argb(0x29000000), d ? Argb(0x12FFFFFF) : Argb(0x0F000000));
            res["AccentElevationBorder"] = ElevationBorder(
                d ? Argb(0x23000000) : Argb(0x66000000), Argb(0x14FFFFFF));

            Set(res, "SubtleFillHover", d ? Argb(0x0FFFFFFF) : Argb(0x09000000));
            Set(res, "SubtleFillPressed", d ? Argb(0x0AFFFFFF) : Argb(0x06000000));

            Set(res, "CardFill", d ? Argb(0x0DFFFFFF) : Argb(0xB3FFFFFF));
            Set(res, "CardFillHover", d ? Argb(0x15FFFFFF) : Argb(0xD9FAFAFA));
            Set(res, "CardStroke", d ? Argb(0x19000000) : Argb(0x0F000000));
            Set(res, "LayerFill", d ? Argb(0x4C3A3A3A) : Argb(0x80FFFFFF));
            Set(res, "LayerStroke", d ? Argb(0x19000000) : Argb(0x0F000000));

            Set(res, "WindowFallback", d ? Rgb(0x20, 0x20, 0x20) : Rgb(0xF3, 0xF3, 0xF3));
            Set(res, "FlyoutFallback", d ? Rgb(0x2C, 0x2C, 0x2C) : Rgb(0xF9, 0xF9, 0xF9));
            Set(res, "FlyoutFill", d ? Rgb(0x2C, 0x2C, 0x2C) : Rgb(0xF9, 0xF9, 0xF9));
            Set(res, "FlyoutTint", d ? Argb(0x992C2C2C) : Argb(0x88F9F9F9));
            Set(res, "FlyoutFooterFill", d ? Argb(0x33000000) : Argb(0x0A000000));
            Set(res, "SurfaceStroke", d ? Argb(0x66000000) : Argb(0x1F000000));
            Set(res, "Divider", d ? Argb(0x15FFFFFF) : Argb(0x0F000000));
            Set(res, "SmokeFill", Argb(0x4D000000));
            Set(res, "ScrollThumb", d ? Argb(0x8BFFFFFF) : Argb(0x72000000));
            Set(res, "TextBoxFill", d ? Argb(0x0FFFFFFF) : Argb(0xB3FFFFFF));
            Set(res, "TextBoxFillFocused", d ? Rgb(0x1C, 0x1C, 0x1C) : Rgb(0xFF, 0xFF, 0xFF));

            Set(res, "CriticalText", d ? Rgb(0xFF, 0x99, 0xA4) : Rgb(0xC4, 0x2B, 0x1C));
            Set(res, "CriticalFill", d ? Rgb(0x44, 0x27, 0x26) : Rgb(0xFD, 0xE7, 0xE9));
            Set(res, "SuccessText", d ? Rgb(0x6C, 0xCB, 0x5F) : Rgb(0x0F, 0x7B, 0x0F));
            Set(res, "CautionText", d ? Rgb(0xFC, 0xE1, 0x00) : Rgb(0x9D, 0x5D, 0x00));
            Set(res, "CautionFill", d ? Rgb(0x43, 0x35, 0x19) : Rgb(0xFF, 0xF4, 0xCE));
        }

        private static void Set(ResourceDictionary res, string key, Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            res[key] = brush;
        }

        // WinUI "ControlElevationBorderBrush": a 1px darker stroke along the bottom edge.
        private static Brush ElevationBorder(Color bottom, Color rest)
        {
            var brush = new LinearGradientBrush
            {
                MappingMode = BrushMappingMode.Absolute,
                StartPoint = new Point(0, 0),
                EndPoint = new Point(0, 3),
                RelativeTransform = new ScaleTransform(1, -1, 0, 0.5)
            };
            brush.GradientStops.Add(new GradientStop(bottom, 0.33));
            brush.GradientStops.Add(new GradientStop(rest, 1.0));
            brush.Freeze();
            return brush;
        }

        public static Brush GetBrush(string key)
        {
            return Application.Current.Resources[key] as Brush ?? Brushes.Transparent;
        }

        /// <summary>
        /// Gives a window the Windows 11 look: dark/light title bar, rounded corners and a
        /// Mica (main window) or Acrylic (flyout) backdrop, falling back to a solid color.
        /// Call from OnSourceInitialized.
        /// </summary>
        public static void ApplyWindowChrome(Window window, int backdropType, string fallbackBrushKey, bool forceRoundCorners)
        {
            ApplyWindowChrome(window, backdropType, fallbackBrushKey, forceRoundCorners, false);
        }

        /// <summary>
        /// Same as above; followTaskbar makes the window use the taskbar theme (SystemUsesLightTheme)
        /// for its dark flag instead of the app theme (used by the tray flyout).
        /// </summary>
        public static void ApplyWindowChrome(Window window, int backdropType, string fallbackBrushKey, bool forceRoundCorners, bool followTaskbar)
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;

            _chromeWindows.Add(new ChromeEntry { Window = new WeakReference(window), FollowTaskbar = followTaskbar });
            ApplyDarkTitleBar(hwnd, followTaskbar ? !IsTaskbarLight : IsDark);

            if (forceRoundCorners)
            {
                int corner = NativeMethods.DWMWCP_ROUND;
                TryDwm(hwnd, NativeMethods.DWMWA_WINDOW_CORNER_PREFERENCE, corner);
            }

            bool backdropOk = false;
            if (SupportsSystemBackdrop)
            {
                try
                {
                    var src = HwndSource.FromHwnd(hwnd);
                    if (src != null && src.CompositionTarget != null)
                        src.CompositionTarget.BackgroundColor = Colors.Transparent;
                    var margins = new NativeMethods.MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
                    NativeMethods.DwmExtendFrameIntoClientArea(hwnd, ref margins);
                    backdropOk = TryDwm(hwnd, NativeMethods.DWMWA_SYSTEMBACKDROP_TYPE, backdropType);
                }
                catch { backdropOk = false; }
            }

            if (backdropOk && backdropType == NativeMethods.DWMSBT_TRANSIENTWINDOW)
                window.SetResourceReference(Window.BackgroundProperty, "FlyoutTint");
            else if (backdropOk)
                window.Background = Brushes.Transparent;
            else
                window.SetResourceReference(Window.BackgroundProperty, fallbackBrushKey);
        }

        private static void ApplyDarkTitleBar(IntPtr hwnd, bool dark)
        {
            if (hwnd == IntPtr.Zero) return;
            TryDwm(hwnd, NativeMethods.DWMWA_USE_IMMERSIVE_DARK_MODE, dark ? 1 : 0);
        }

        private static bool TryDwm(IntPtr hwnd, int attr, int value)
        {
            try
            {
                return NativeMethods.DwmSetWindowAttribute(hwnd, attr, ref value, sizeof(int)) == 0;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>Native Win32 popup menus (tray menu) follow the app theme.</summary>
        private static void ApplyNativeMenuTheme()
        {
            if (WindowsBuild < 18362) return;
            try
            {
                NativeMethods.SetPreferredAppMode(IsDark ? 2 : 3); // ForceDark : ForceLight
                NativeMethods.FlushMenuThemes();
            }
            catch { }
        }
    }
}
