using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

namespace TrayOrganizer.Core
{
    public static class ConfigManager
    {
        private static readonly object _lock = new object();
        private static readonly HashSet<string> _genericHostExes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "python.exe", "pythonw.exe", "javaw.exe", "java.exe", "powershell.exe", "pwsh.exe", "cmd.exe", "node.exe", "rundll32.exe", "explorer.exe"
        };

        private static string _appDataDir;
        private static string _workspaceDataDir;
        private static bool _workspaceDataDirResolved;

        public static string AppDataDir
        {
            get
            {
                string dir = _appDataDir;
                if (dir == null)
                {
                    dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TrayOrganizer");
                    try { Directory.CreateDirectory(dir); }
                    catch { }
                    _appDataDir = dir;
                }
                return dir;
            }
        }

        /// <summary>
        /// Optional workspace mirror: "&lt;exe&gt;\..\..\data". It is opt-in -- only used when that directory already
        /// exists (it is never created) -- and is resolved once per process.
        /// </summary>
        public static string WorkspaceDataDir
        {
            get
            {
                if (_workspaceDataDirResolved)
                    return _workspaceDataDir;

                string result = null;
                try
                {
                    string candidate = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"..\..\data"));
                    if (Directory.Exists(candidate))
                        result = candidate;
                }
                catch { }

                _workspaceDataDir = result;
                _workspaceDataDirResolved = true;
                return result;
            }
        }

        public static string ConfigFilePath
        {
            get { return Path.Combine(AppDataDir, "config.json"); }
        }

        public static string HiddenStateFilePath
        {
            get { return Path.Combine(AppDataDir, "active_hidden_state.json"); }
        }

        public static string LogFilePath
        {
            get { return Path.Combine(AppDataDir, "verification.log"); }
        }

        public static event Action<string> OnLogMessage;

        private const long MaxLogFileBytes = 2L * 1024 * 1024;
        private const int LogFlushIntervalMs = 500;

        private static readonly object _logQueueLock = new object();
        private static readonly object _logIoLock = new object();
        private static List<string> _logQueue = new List<string>();
        private static System.Threading.Timer _logFlushTimer;
        private static bool _logInitialized;

        private static JavaScriptSerializer CreateSerializer()
        {
            return new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
        }

        private static void EnsureLogFlushTimer()
        {
            if (_logInitialized) return;
            lock (_logQueueLock)
            {
                if (_logInitialized) return;
                _logInitialized = true;
                _logFlushTimer = new System.Threading.Timer(delegate { FlushLog(); }, null, LogFlushIntervalMs, LogFlushIntervalMs);
                try { AppDomain.CurrentDomain.ProcessExit += delegate { FlushLog(); }; }
                catch { }
            }
        }

        public static void Log(string category, string message)
        {
            string line = string.Format("[{0:yyyy-MM-dd HH:mm:ss.fff}] [{1}] {2}", DateTime.Now, category, message);
            EnsureLogFlushTimer();
            lock (_logQueueLock)
            {
                _logQueue.Add(line);
            }
            var handler = OnLogMessage;
            if (handler != null)
                handler(line);
        }

        /// <summary>Writes all queued log lines to disk. Safe to call from any thread; called on exit paths.</summary>
        public static void FlushLog()
        {
            List<string> pending;
            lock (_logQueueLock)
            {
                if (_logQueue.Count == 0) return;
                pending = _logQueue;
                _logQueue = new List<string>();
            }

            var sb = new StringBuilder();
            foreach (var l in pending)
                sb.Append(l).Append(Environment.NewLine);
            string text = sb.ToString();

            lock (_logIoLock)
            {
                AppendLogText(LogFilePath, text);
                string wsDir = WorkspaceDataDir;
                if (wsDir != null)
                    AppendLogText(Path.Combine(wsDir, "verification.log"), text);
            }
        }

        private static void AppendLogText(string path, string text)
        {
            try
            {
                var fi = new FileInfo(path);
                if (fi.Exists && fi.Length > MaxLogFileBytes)
                {
                    string old = Path.Combine(Path.GetDirectoryName(path), "verification.old.log");
                    try
                    {
                        if (File.Exists(old)) File.Delete(old);
                        File.Move(path, old);
                    }
                    catch
                    {
                        try { File.Delete(path); }
                        catch { }
                    }
                }
                File.AppendAllText(path, text, Encoding.UTF8);
            }
            catch { }
        }

        public static OrganizerConfig LoadConfig()
        {
            lock (_lock)
            {
                try
                {
                    if (File.Exists(ConfigFilePath))
                    {
                        string json = File.ReadAllText(ConfigFilePath, Encoding.UTF8);
                        var cfg = CreateSerializer().Deserialize<OrganizerConfig>(json);
                        if (cfg != null)
                        {
                            if (cfg.Folders == null)
                                cfg.Folders = new List<TrayFolder>();
                            return cfg;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log("ERROR", "读取配置失败: " + ex.Message);
                }

                // Default initial config with two starter folders
                var defaultCfg = new OrganizerConfig
                {
                    HideStrategy = HideStrategy.CompleteHide,
                    IsPaused = false,
                    AutoOpenFlyoutOnExternalDrag = true,
                    Folders = new List<TrayFolder>
                    {
                        new TrayFolder
                        {
                            Id = "folder_monitors",
                            Name = "状态监控",
                            Glyph = "\uE9D9",
                            Order = 0,
                            Bindings = new List<IconBindingRule>()
                        },
                        new TrayFolder
                        {
                            Id = "folder_tools",
                            Name = "外设与工具",
                            Glyph = "\uE7FC",
                            Order = 1,
                            Bindings = new List<IconBindingRule>()
                        }
                    }
                };
                SaveConfig(defaultCfg);
                return defaultCfg;
            }
        }

        public static void SaveConfig(OrganizerConfig config)
        {
            lock (_lock)
            {
                try
                {
                    string json = FormatJson(CreateSerializer().Serialize(config));
                    File.WriteAllText(ConfigFilePath, json, Encoding.UTF8);
                    string wsDir = WorkspaceDataDir;
                    if (wsDir != null)
                    {
                        File.WriteAllText(Path.Combine(wsDir, "config.json"), json, Encoding.UTF8);
                    }
                }
                catch (Exception ex)
                {
                    Log("ERROR", "保存配置失败: " + ex.Message);
                }
            }
        }

        public static void SaveHiddenState(List<HiddenIconRecord> records)
        {
            lock (_lock)
            {
                try
                {
                    var state = new ActiveHiddenStateFile
                    {
                        OwnerPid = Process.GetCurrentProcess().Id,
                        UpdatedAtUtc = DateTime.UtcNow,
                        HiddenIcons = records ?? new List<HiddenIconRecord>()
                    };
                    string json = FormatJson(CreateSerializer().Serialize(state));
                    File.WriteAllText(HiddenStateFilePath, json, Encoding.UTF8);
                    string wsDir = WorkspaceDataDir;
                    if (wsDir != null)
                    {
                        File.WriteAllText(Path.Combine(wsDir, "active_hidden_state.json"), json, Encoding.UTF8);
                    }
                }
                catch { }
            }
        }

        public static ActiveHiddenStateFile LoadHiddenState()
        {
            lock (_lock)
            {
                try
                {
                    if (File.Exists(HiddenStateFilePath))
                    {
                        string json = File.ReadAllText(HiddenStateFilePath, Encoding.UTF8);
                        return CreateSerializer().Deserialize<ActiveHiddenStateFile>(json);
                    }
                }
                catch { }
                return null;
            }
        }

        public static void ClearHiddenState()
        {
            lock (_lock)
            {
                try
                {
                    if (File.Exists(HiddenStateFilePath))
                        File.Delete(HiddenStateFilePath);
                    string wsDir = WorkspaceDataDir;
                    if (wsDir != null && File.Exists(Path.Combine(wsDir, "active_hidden_state.json")))
                        File.Delete(Path.Combine(wsDir, "active_hidden_state.json"));
                }
                catch { }
            }
        }

        private static string FormatJson(string json)
        {
            if (string.IsNullOrEmpty(json)) return "";
            var sb = new StringBuilder();
            int indent = 0;
            bool inQuotes = false;
            for (int i = 0; i < json.Length; i++)
            {
                char ch = json[i];
                if (ch == '"' && (i == 0 || json[i - 1] != '\\'))
                    inQuotes = !inQuotes;

                if (!inQuotes)
                {
                    if (ch == '{' || ch == '[')
                    {
                        sb.Append(ch);
                        sb.AppendLine();
                        indent++;
                        sb.Append(new string(' ', indent * 2));
                        continue;
                    }
                    if (ch == '}' || ch == ']')
                    {
                        sb.AppendLine();
                        indent = Math.Max(0, indent - 1);
                        sb.Append(new string(' ', indent * 2));
                        sb.Append(ch);
                        continue;
                    }
                    if (ch == ',')
                    {
                        sb.Append(ch);
                        sb.AppendLine();
                        sb.Append(new string(' ', indent * 2));
                        continue;
                    }
                    if (ch == ':')
                    {
                        sb.Append(": ");
                        continue;
                    }
                }
                sb.Append(ch);
            }
            return sb.ToString();
        }

        public static string ExtractTooltipPrefix(string tip)
        {
            if (string.IsNullOrWhiteSpace(tip)) return "";
            string clean = tip.Trim();
            int idx = clean.IndexOfAny(new[] { '：', ':', '\r', '\n', '-', '—', ' ' });
            if (idx > 0 && idx <= 20)
                return clean.Substring(0, idx).Trim();
            return clean.Length > 16 ? clean.Substring(0, 16) : clean;
        }

        public static TrayFolder MatchFolderForIcon(OrganizerConfig config, TrayIconItem item, out IconBindingRule matchedRule)
        {
            matchedRule = null;
            if (config == null || config.Folders == null || item == null) return null;

            string itemGuid = item.GuidItem != Guid.Empty ? item.GuidItem.ToString("D") : "";
            bool isGenericExe = _genericHostExes.Contains(item.ExeFileName ?? "");
            string itemTipPrefix = ExtractTooltipPrefix(!string.IsNullOrWhiteSpace(item.InitialTooltip) ? item.InitialTooltip : item.ToolTip);

            // Tier 1: Exact GUID match
            if (!string.IsNullOrEmpty(itemGuid))
            {
                foreach (var folder in config.Folders)
                {
                    var rule = folder.Bindings.FirstOrDefault(b =>
                        !string.IsNullOrEmpty(b.IconGuid) &&
                        string.Equals(b.IconGuid, itemGuid, StringComparison.OrdinalIgnoreCase));
                    if (rule != null)
                    {
                        matchedRule = rule;
                        return folder;
                    }
                }
            }

            // Tier 2: Exact RegKey match
            if (item.RegKey != 0)
            {
                foreach (var folder in config.Folders)
                {
                    var rule = folder.Bindings.FirstOrDefault(b => b.RegKey == item.RegKey);
                    if (rule != null)
                    {
                        matchedRule = rule;
                        return folder;
                    }
                }
            }

            // Tier 3: NormalizedExePath + UID (with tooltip prefix check if generic host like pythonw.exe)
            if (!string.IsNullOrEmpty(item.NormalizedExePath))
            {
                foreach (var folder in config.Folders)
                {
                    foreach (var rule in folder.Bindings)
                    {
                        if (string.Equals(rule.NormalizedExePath, item.NormalizedExePath, StringComparison.OrdinalIgnoreCase) &&
                            rule.Uid == item.Uid)
                        {
                            if (isGenericExe && !string.IsNullOrEmpty(rule.InitialTooltipPrefix) && !string.IsNullOrEmpty(itemTipPrefix))
                            {
                                if (!string.Equals(rule.InitialTooltipPrefix, itemTipPrefix, StringComparison.OrdinalIgnoreCase))
                                    continue;
                            }
                            matchedRule = rule;
                            return folder;
                        }
                    }
                }
            }

            // Tier 4: ExeFileName + UID for non-generic executables (handles version/path migration, e.g., PadMouse.exe in different directories)
            if (!isGenericExe && !string.IsNullOrEmpty(item.ExeFileName))
            {
                foreach (var folder in config.Folders)
                {
                    var rule = folder.Bindings.FirstOrDefault(b =>
                        string.Equals(b.ExeFileName, item.ExeFileName, StringComparison.OrdinalIgnoreCase) &&
                        b.Uid == item.Uid);
                    if (rule != null)
                    {
                        matchedRule = rule;
                        return folder;
                    }
                }
            }

            return null;
        }

        public static void BindIconToFolder(OrganizerConfig config, TrayIconItem item, TrayFolder targetFolder, string reason)
        {
            if (config == null || item == null || targetFolder == null) return;

            int? existingOriginalPromoted = null;

            // Remove from any existing folder first
            foreach (var f in config.Folders)
            {
                var toRemove = f.Bindings.Where(b =>
                    (item.RegKey != 0 && b.RegKey == item.RegKey) ||
                    (!string.IsNullOrEmpty(item.NormalizedExePath) &&
                     string.Equals(b.NormalizedExePath, item.NormalizedExePath, StringComparison.OrdinalIgnoreCase) &&
                     b.Uid == item.Uid &&
                     (!_genericHostExes.Contains(item.ExeFileName ?? "") ||
                      string.Equals(b.InitialTooltipPrefix, ExtractTooltipPrefix(item.InitialTooltip), StringComparison.OrdinalIgnoreCase)))
                ).ToList();

                foreach (var rem in toRemove)
                {
                    if (rem.OriginalIsPromoted.HasValue)
                        existingOriginalPromoted = rem.OriginalIsPromoted;
                    f.Bindings.Remove(rem);
                }
            }

            var newRule = new IconBindingRule
            {
                RegKey = item.RegKey,
                NormalizedExePath = item.NormalizedExePath ?? "",
                ExeFileName = item.ExeFileName ?? "",
                Uid = item.Uid,
                IconGuid = item.GuidItem != Guid.Empty ? item.GuidItem.ToString("D") : "",
                InitialTooltipPrefix = ExtractTooltipPrefix(!string.IsNullOrWhiteSpace(item.InitialTooltip) ? item.InitialTooltip : item.ToolTip),
                DisplayName = item.AppDisplayName ?? item.ExeFileName ?? "未知图标",
                CachedIconPngBase64 = item.CachedIconPngBase64 ?? "",
                OriginalIsPromoted = existingOriginalPromoted ?? (item.IsPromotedInRegistry ? 1 : 0)
            };

            targetFolder.Bindings.Add(newRule);
            item.FolderId = targetFolder.Id;
            item.FolderName = targetFolder.Name;

            SaveConfig(config);
            Log("BIND", string.Format("图标 [{0}] ({1}) 已绑定到文件夹 [{2}] (原因: {3})", item.AppDisplayName, item.IdentitySummary, targetFolder.Name, reason));
        }

        public static int? UnbindIcon(OrganizerConfig config, TrayIconItem item, string reason)
        {
            if (config == null || item == null) return null;
            int? origPromoted = null;
            foreach (var f in config.Folders)
            {
                var matches = f.Bindings.Where(b =>
                    (item.RegKey != 0 && b.RegKey == item.RegKey) ||
                    (!string.IsNullOrEmpty(item.NormalizedExePath) &&
                     string.Equals(b.NormalizedExePath, item.NormalizedExePath, StringComparison.OrdinalIgnoreCase) &&
                     b.Uid == item.Uid)
                ).ToList();

                foreach (var m in matches)
                {
                    if (m.OriginalIsPromoted.HasValue)
                        origPromoted = m.OriginalIsPromoted;
                    f.Bindings.Remove(m);
                }
            }

            item.FolderId = null;
            item.FolderName = "未分组";
            SaveConfig(config);
            Log("UNBIND", string.Format("图标 [{0}] ({1}) 已移出收纳文件夹 (原因: {2})", item.AppDisplayName, item.IdentitySummary, reason));
            return origPromoted;
        }
    }
}
