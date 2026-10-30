using System;
using System.Diagnostics;
using Microsoft.Win32;

namespace TrayOrganizer.Core
{
    /// <summary>Launch-at-login via HKCU\...\Run (no admin rights needed).</summary>
    public static class StartupManager
    {
        private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "TrayOrganizer";

        public static bool IsEnabled()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false))
                {
                    if (key == null) return false;
                    object v = key.GetValue(ValueName);
                    return v != null && !string.IsNullOrWhiteSpace(v.ToString());
                }
            }
            catch
            {
                return false;
            }
        }

        public static void SetEnabled(bool enabled)
        {
            using (var key = Registry.CurrentUser.CreateSubKey(RunKeyPath))
            {
                if (key == null) return;
                if (enabled)
                {
                    string exe = GetExePath();
                    key.SetValue(ValueName, "\"" + exe + "\" --minimized", RegistryValueKind.String);
                    ConfigManager.Log("STATE", "已开启开机自动启动");
                }
                else
                {
                    key.DeleteValue(ValueName, false);
                    ConfigManager.Log("STATE", "已关闭开机自动启动");
                }
            }
        }

        private static string GetExePath()
        {
            try
            {
                string p = Process.GetCurrentProcess().MainModule.FileName;
                if (!string.IsNullOrEmpty(p)) return p;
            }
            catch { }
            return System.Reflection.Assembly.GetExecutingAssembly().Location;
        }
    }
}
