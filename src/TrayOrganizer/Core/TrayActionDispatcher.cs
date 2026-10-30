using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace TrayOrganizer.Core
{
    public static class TrayActionDispatcher
    {
        private static void ActivateTargetWindow(IntPtr targetHwnd)
        {
            if (targetHwnd == IntPtr.Zero) return;
            try
            {
                IntPtr fgWnd = NativeMethods.GetForegroundWindow();
                uint curThread = NativeMethods.GetCurrentThreadId();
                uint dummy1;
                uint dummy2;
                uint fgThread = fgWnd != IntPtr.Zero ? NativeMethods.GetWindowThreadProcessId(fgWnd, out dummy1) : 0;
                uint targetThread = NativeMethods.GetWindowThreadProcessId(targetHwnd, out dummy2);

                if (fgThread != 0 && fgThread != curThread)
                    NativeMethods.AttachThreadInput(curThread, fgThread, true);
                if (targetThread != 0 && targetThread != curThread)
                    NativeMethods.AttachThreadInput(curThread, targetThread, true);

                NativeMethods.SetForegroundWindow(targetHwnd);

                if (targetThread != 0 && targetThread != curThread)
                    NativeMethods.AttachThreadInput(curThread, targetThread, false);
                if (fgThread != 0 && fgThread != curThread)
                    NativeMethods.AttachThreadInput(curThread, fgThread, false);
            }
            catch
            {
                NativeMethods.SetForegroundWindow(targetHwnd);
            }
        }

        private static string GetWindowClass(IntPtr hWnd)
        {
            if (hWnd == IntPtr.Zero) return "";
            var sb = new StringBuilder(128);
            NativeMethods.GetClassName(hWnd, sb, sb.Capacity);
            return sb.ToString();
        }

        private static IntPtr MakeWParamCoords(int x, int y)
        {
            return (IntPtr)((x & 0xFFFF) | ((y & 0xFFFF) << 16));
        }

        private static IntPtr MakeLParamVersion4(uint msg, uint uid)
        {
            return (IntPtr)((int)(msg & 0xFFFF) | ((int)(uid & 0xFFFF) << 16));
        }

        public static bool InvokeLeftClick(TrayIconItem item)
        {
            if (item == null || !item.IsOnline || item.Hwnd == 0 || item.CallbackMessage == 0)
                return false;

            IntPtr hWnd = (IntPtr)item.Hwnd;
            NativeMethods.POINT pt;
            NativeMethods.GetCursorPos(out pt);
            ActivateTargetWindow(hWnd);

            bool ok;
            if (item.Version >= 4)
            {
                IntPtr wp = MakeWParamCoords(pt.X, pt.Y);
                NativeMethods.PostMessage(hWnd, item.CallbackMessage, wp, MakeLParamVersion4(NativeMethods.WM_LBUTTONDOWN, item.Uid));
                ok = NativeMethods.PostMessage(hWnd, item.CallbackMessage, wp, MakeLParamVersion4(NativeMethods.WM_LBUTTONUP, item.Uid));
                NativeMethods.PostMessage(hWnd, item.CallbackMessage, wp, MakeLParamVersion4(NativeMethods.NIN_SELECT, item.Uid));
            }
            else
            {
                NativeMethods.PostMessage(hWnd, item.CallbackMessage, (IntPtr)item.Uid, (IntPtr)NativeMethods.WM_LBUTTONDOWN);
                ok = NativeMethods.PostMessage(hWnd, item.CallbackMessage, (IntPtr)item.Uid, (IntPtr)NativeMethods.WM_LBUTTONUP);
            }

            ConfigManager.Log("ACTION", string.Format("触发左键单击: [{0}] HWND=0x{1:X} uID={2} cbMsg=0x{3:X} ver={4} ok={5}",
                item.AppDisplayName, item.Hwnd, item.Uid, item.CallbackMessage, item.Version, ok));
            return ok;
        }

        public static bool InvokeDoubleClick(TrayIconItem item)
        {
            if (item == null || !item.IsOnline || item.Hwnd == 0 || item.CallbackMessage == 0)
                return false;

            IntPtr hWnd = (IntPtr)item.Hwnd;
            NativeMethods.POINT pt;
            NativeMethods.GetCursorPos(out pt);
            ActivateTargetWindow(hWnd);

            bool ok;
            if (item.Version >= 4)
            {
                IntPtr wp = MakeWParamCoords(pt.X, pt.Y);
                NativeMethods.PostMessage(hWnd, item.CallbackMessage, wp, MakeLParamVersion4(NativeMethods.WM_LBUTTONDBLCLK, item.Uid));
                ok = NativeMethods.PostMessage(hWnd, item.CallbackMessage, wp, MakeLParamVersion4(NativeMethods.WM_LBUTTONUP, item.Uid));
            }
            else
            {
                NativeMethods.PostMessage(hWnd, item.CallbackMessage, (IntPtr)item.Uid, (IntPtr)NativeMethods.WM_LBUTTONDBLCLK);
                ok = NativeMethods.PostMessage(hWnd, item.CallbackMessage, (IntPtr)item.Uid, (IntPtr)NativeMethods.WM_LBUTTONUP);
            }

            ConfigManager.Log("ACTION", string.Format("触发左键双击: [{0}] HWND=0x{1:X} uID={2} cbMsg=0x{3:X} ver={4} ok={5}",
                item.AppDisplayName, item.Hwnd, item.Uid, item.CallbackMessage, item.Version, ok));
            return ok;
        }

        public static bool InvokeContextMenu(TrayIconItem item)
        {
            if (item == null || !item.IsOnline || item.Hwnd == 0 || item.CallbackMessage == 0)
                return false;

            IntPtr hWnd = (IntPtr)item.Hwnd;
            NativeMethods.POINT pt;
            NativeMethods.GetCursorPos(out pt);
            string cls = GetWindowClass(hWnd);
            ActivateTargetWindow(hWnd);

            bool ok;
            if (cls.IndexOf("Electron_NotifyIconHostWindow", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                // Chromium StatusIconWin triggers OnContextMenu on WM_RBUTTONDOWN or WM_CONTEXTMENU; send WM_CONTEXTMENU once to avoid double-triggering
                ok = NativeMethods.PostMessage(hWnd, item.CallbackMessage, (IntPtr)item.Uid, (IntPtr)NativeMethods.WM_CONTEXTMENU);
            }
            else if (item.Version >= 4)
            {
                IntPtr wp = MakeWParamCoords(pt.X, pt.Y);
                NativeMethods.PostMessage(hWnd, item.CallbackMessage, wp, MakeLParamVersion4(NativeMethods.WM_RBUTTONDOWN, item.Uid));
                NativeMethods.PostMessage(hWnd, item.CallbackMessage, wp, MakeLParamVersion4(NativeMethods.WM_RBUTTONUP, item.Uid));
                ok = NativeMethods.PostMessage(hWnd, item.CallbackMessage, wp, MakeLParamVersion4(NativeMethods.WM_CONTEXTMENU, item.Uid));
            }
            else
            {
                NativeMethods.PostMessage(hWnd, item.CallbackMessage, (IntPtr)item.Uid, (IntPtr)NativeMethods.WM_RBUTTONDOWN);
                ok = NativeMethods.PostMessage(hWnd, item.CallbackMessage, (IntPtr)item.Uid, (IntPtr)NativeMethods.WM_RBUTTONUP);
            }

            ConfigManager.Log("ACTION", string.Format("触发原生右键菜单: [{0}] HWND=0x{1:X} Class={2} uID={3} cbMsg=0x{4:X} pos=({5},{6}) ok={7}",
                item.AppDisplayName, item.Hwnd, cls, item.Uid, item.CallbackMessage, pt.X, pt.Y, ok));
            return ok;
        }

        public static bool SetShellNotifyIconHidden(ulong hwnd, uint uid, Guid guid, bool hide)
        {
            if (hwnd == 0 && guid == Guid.Empty) return false;
            var nid = new NativeMethods.NOTIFYICONDATAW
            {
                cbSize = (uint)Marshal.SizeOf(typeof(NativeMethods.NOTIFYICONDATAW)),
                hWnd = (IntPtr)hwnd,
                uID = uid,
                uFlags = NativeMethods.NIF_STATE,
                dwState = hide ? NativeMethods.NIS_HIDDEN : 0u,
                dwStateMask = NativeMethods.NIS_HIDDEN
            };

            if (guid != Guid.Empty)
            {
                nid.uFlags |= NativeMethods.NIF_GUID;
                nid.guidItem = guid;
            }

            return NativeMethods.Shell_NotifyIconW(NativeMethods.NIM_MODIFY, ref nid);
        }

        public static void SetRegistryIsPromoted(ulong regKey, int? isPromoted)
        {
            if (regKey == 0) return;
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(string.Format(@"Control Panel\NotifyIconSettings\{0}", regKey), true))
                {
                    if (key != null)
                    {
                        if (isPromoted.HasValue)
                            key.SetValue("IsPromoted", isPromoted.Value, RegistryValueKind.DWord);
                        else
                            key.DeleteValue("IsPromoted", false);
                    }
                }
            }
            catch
            {
            }
        }

        public static bool ApplyOrganizeHide(TrayIconItem item, HideStrategy strategy)
        {
            if (item == null || !item.IsOnline) return false;

            // Always demote from taskbar when organized so taskbar is decluttered
            if (item.IsPromotedInRegistry)
            {
                SetRegistryIsPromoted(item.RegKey, 0);
                item.IsPromotedInRegistry = false;
            }

            if (strategy == HideStrategy.CompleteHide)
            {
                bool ok = SetShellNotifyIconHidden(item.Hwnd, item.Uid, item.GuidItem, true);
                if (ok)
                {
                    item.IsHiddenByOrganizer = true;
                    item.NativeVisibleInTray = false;
                }
                return ok;
            }
            else
            {
                // If strategy is DemoteToOverflowOnly, ensure it's not NIS_HIDDEN
                if (!item.NativeVisibleInTray)
                {
                    SetShellNotifyIconHidden(item.Hwnd, item.Uid, item.GuidItem, false);
                    item.NativeVisibleInTray = true;
                }
                item.IsHiddenByOrganizer = false;
                return true;
            }
        }

        public static bool RestoreNativeTrayIcon(TrayIconItem item, int? restorePromoted)
        {
            if (item == null) return false;
            bool ok = true;
            if (item.IsOnline && item.Hwnd != 0)
            {
                ok = SetShellNotifyIconHidden(item.Hwnd, item.Uid, item.GuidItem, false);
                item.NativeVisibleInTray = true;
            }
            item.IsHiddenByOrganizer = false;

            if (restorePromoted.HasValue && item.RegKey != 0)
            {
                SetRegistryIsPromoted(item.RegKey, restorePromoted.Value);
                item.IsPromotedInRegistry = restorePromoted.Value != 0;
            }

            return ok;
        }

        public static int RestoreAllFromSavedState(string reason)
        {
            var state = ConfigManager.LoadHiddenState();
            if (state == null || state.HiddenIcons == null || state.HiddenIcons.Count == 0)
                return 0;

            int restored = 0;
            foreach (var rec in state.HiddenIcons)
            {
                try
                {
                    Guid g = Guid.Empty;
                    if (!string.IsNullOrEmpty(rec.IconGuid))
                        Guid.TryParse(rec.IconGuid, out g);

                    bool ok = SetShellNotifyIconHidden(rec.Hwnd, rec.Uid, g, false);
                    if (rec.OriginalIsPromoted.HasValue && rec.RegKey != 0)
                    {
                        SetRegistryIsPromoted(rec.RegKey, rec.OriginalIsPromoted.Value);
                    }
                    if (ok) restored++;
                }
                catch { }
            }

            ConfigManager.ClearHiddenState();
            ConfigManager.Log("RECOVERY", string.Format("已执行状态恢复 ({0}): 共恢复 {1}/{2} 个托盘图标原位置", reason, restored, state.HiddenIcons.Count));
            ConfigManager.FlushLog();
            return restored;
        }
    }
}
