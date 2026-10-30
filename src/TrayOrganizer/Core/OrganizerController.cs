using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Threading;

namespace TrayOrganizer.Core
{
    public class OrganizerController : IDisposable
    {
        private readonly TrayIconEngine _engine = new TrayIconEngine();
        private readonly ObservableCollection<TrayIconItem> _allIcons = new ObservableCollection<TrayIconItem>();
        private readonly ObservableCollection<TrayFolder> _folders = new ObservableCollection<TrayFolder>();
        private readonly ExternalTrayDragMonitor _dragMonitor;
        private readonly Dispatcher _dispatcher;
        private readonly DispatcherTimer _refreshTimer;
        private bool _isRefreshing;
        private string _lastHiddenSignature = "";

        private const int FastPollMs = 1000;
        private const int SlowPollMs = 3000;
        private const int BurstPollTicks = 5;
        private bool _uiActive = true;
        private int _burstTicks;
        private string _lastStructureSignature;

        public TrayIconEngine Engine { get { return _engine; } }
        public OrganizerConfig Config { get; private set; }
        public ObservableCollection<TrayIconItem> AllIcons { get { return _allIcons; } }
        public ObservableCollection<TrayFolder> Folders { get { return _folders; } }
        public ExternalTrayDragMonitor DragMonitor { get { return _dragMonitor; } }

        public event EventHandler IconsUpdated;

        public OrganizerController(Dispatcher dispatcher)
        {
            _dispatcher = dispatcher;
            Config = ConfigManager.LoadConfig();

            foreach (var f in Config.Folders.OrderBy(x => x.Order))
                Folders.Add(f);

            _dragMonitor = new ExternalTrayDragMonitor(Engine, _dispatcher, () => AllIcons.Where(x => x.IsOnline).ToList());

            _refreshTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(FastPollMs)
            };
            _refreshTimer.Tick += (s, e) =>
            {
                if (_burstTicks > 0) _burstTicks--;
                RefreshIcons(false);
            };

            // An external tray drag starts: make sure the icon list is fresh and polled quickly while it lasts.
            _dragMonitor.DragStarted += (s, e) =>
            {
                _burstTicks = BurstPollTicks;
                UpdatePollingInterval();
            };
        }

        /// <summary>
        /// Tells the controller whether any window (main window or flyout) is visible. While a window is visible the
        /// icon list is polled every second; with everything hidden it is polled every 3 seconds.
        /// </summary>
        public void SetUiActive(bool active)
        {
            _uiActive = active;
            UpdatePollingInterval();
        }

        private void UpdatePollingInterval()
        {
            bool fast = _uiActive || _burstTicks > 0 || _dragMonitor.IsDragging;
            int ms = fast ? FastPollMs : SlowPollMs;
            if ((int)_refreshTimer.Interval.TotalMilliseconds != ms)
                _refreshTimer.Interval = TimeSpan.FromMilliseconds(ms);
        }

        /// <summary>
        /// Explorer (the taskbar) was restarted: the shell forgot every NIS_HIDDEN flag, so rescan and hide again.
        /// </summary>
        public void OnExplorerRestarted()
        {
            ConfigManager.Log("EXPLORER", "检测到资源管理器重启 (TaskbarCreated)，重新扫描托盘并重新收起已收纳图标");
            RefreshIcons(true);

            int reapplied = 0;
            if (!Config.IsPaused)
            {
                foreach (var item in AllIcons.Where(x => x.IsOnline && x.IsOrganized).ToList())
                {
                    if (TrayActionDispatcher.ApplyOrganizeHide(item, Config.HideStrategy))
                        reapplied++;
                }
                UpdateFolderStatsAndHiddenStateFile();
                var h = IconsUpdated;
                if (h != null) h(this, EventArgs.Empty);
            }
            _burstTicks = BurstPollTicks;
            UpdatePollingInterval();
            ConfigManager.Log("EXPLORER", string.Format("资源管理器重启后已重新应用 {0} 个收纳图标的隐藏状态", reapplied));
        }

        public void Start()
        {
            // Recover any stale hidden icons from a previous session before applying current config
            TrayActionDispatcher.RestoreAllFromSavedState("启动前清理上次遗留状态");

            RefreshIcons(true);
            _refreshTimer.Start();
            DragMonitor.Start();
        }

        public void RefreshIcons(bool forceFullScan = false)
        {
            if (_isRefreshing) return;
            _isRefreshing = true;

            try
            {
                bool anyWindowClosed = AllIcons.Any(x => x.IsOnline && x.Hwnd != 0 && !NativeMethods.IsWindow((IntPtr)x.Hwnd));
                var discovered = Engine.DiscoverTrayIcons(forceFullScan || anyWindowClosed);

                // Structural change (icon appeared / disappeared / app restarted): poll fast for a few ticks.
                string structure = string.Join(";", discovered
                    .OrderBy(x => x.RegKey)
                    .Select(x => string.Format("{0}:{1}:{2}", x.RegKey, x.Hwnd, x.OwnerPid)));
                if (_lastStructureSignature != null && structure != _lastStructureSignature)
                    _burstTicks = BurstPollTicks;
                _lastStructureSignature = structure;
                var existingByKey = AllIcons.Where(x => x.IsOnline && x.RegKey != 0).ToDictionary(x => x.RegKey, x => x);
                var currentOnlineKeys = new HashSet<ulong>();

                bool configDirty = false;
                var matchedRules = new HashSet<IconBindingRule>();

                foreach (var live in discovered)
                {
                    currentOnlineKeys.Add(live.RegKey);

                    IconBindingRule matchedRule;
                    var matchedFolder = ConfigManager.MatchFolderForIcon(Config, live, out matchedRule);
                    if (matchedFolder != null && matchedRule != null)
                    {
                        matchedRules.Add(matchedRule);
                        live.FolderId = matchedFolder.Id;
                        live.FolderName = matchedFolder.Name;

                        // Keep rule metadata up-to-date if RegKey or cached icon updated
                        if (live.RegKey != 0 && matchedRule.RegKey != live.RegKey)
                        {
                            matchedRule.RegKey = live.RegKey;
                            configDirty = true;
                        }
                        if (!string.IsNullOrEmpty(live.CachedIconPngBase64) && string.IsNullOrEmpty(matchedRule.CachedIconPngBase64))
                        {
                            matchedRule.CachedIconPngBase64 = live.CachedIconPngBase64;
                            configDirty = true;
                        }

                        if (!Config.IsPaused)
                        {
                            if (Config.HideStrategy == HideStrategy.CompleteHide && !live.NativeVisibleInTray && !live.IsPromotedInRegistry)
                            {
                                // Already hidden in the shell: nothing to send, just keep our bookkeeping.
                                live.IsHiddenByOrganizer = true;
                            }
                            else
                            {
                                TrayActionDispatcher.ApplyOrganizeHide(live, Config.HideStrategy);
                            }
                        }
                        else
                        {
                            if (!live.NativeVisibleInTray)
                                TrayActionDispatcher.RestoreNativeTrayIcon(live, matchedRule.OriginalIsPromoted);
                        }
                    }
                    else
                    {
                        live.FolderId = null;
                        live.FolderName = "未分组";
                    }

                    TrayIconItem existing;
                    if (existingByKey.TryGetValue(live.RegKey, out existing))
                    {
                        bool appRestarted = existing.Hwnd != live.Hwnd || existing.OwnerPid != live.OwnerPid;
                        existing.Hwnd = live.Hwnd;
                        existing.Uid = live.Uid;
                        existing.GuidItem = live.GuidItem;
                        existing.CallbackMessage = live.CallbackMessage;
                        existing.Version = live.Version;
                        existing.HIcon = live.HIcon;
                        existing.OwnerPid = live.OwnerPid;
                        existing.ToolTip = live.ToolTip;
                        existing.AppDisplayName = live.AppDisplayName;
                        existing.FolderId = live.FolderId;
                        existing.FolderName = live.FolderName;
                        existing.IsHiddenByOrganizer = live.IsHiddenByOrganizer;
                        existing.IsPromotedInRegistry = live.IsPromotedInRegistry;
                        existing.NativeVisibleInTray = live.NativeVisibleInTray;
                        existing.IsOnline = true;
                        if (live.IconSource != null)
                            existing.IconSource = live.IconSource;
                        if (!string.IsNullOrEmpty(live.CachedIconPngBase64))
                            existing.CachedIconPngBase64 = live.CachedIconPngBase64;

                        if (appRestarted && matchedFolder != null)
                        {
                            ConfigManager.Log("AUTO_MATCH", string.Format(
                                "检测到应用重启并自动恢复归属: [{0}] (新PID={1}, 新HWND=0x{2:X}, RegKey={3}) -> 文件夹 [{4}] (原位隐藏={5})",
                                live.AppDisplayName, live.OwnerPid, live.Hwnd, live.RegKey, matchedFolder.Name, live.IsHiddenByOrganizer));
                        }
                    }
                    else
                    {
                        // Remove any offline placeholder for this rule before adding live item
                        var offlineDuplicate = AllIcons.FirstOrDefault(x =>
                            !x.IsOnline &&
                            ((live.RegKey != 0 && x.RegKey == live.RegKey) ||
                             (!string.IsNullOrEmpty(live.ExeFileName) && string.Equals(x.ExeFileName, live.ExeFileName, StringComparison.OrdinalIgnoreCase) && x.Uid == live.Uid)));
                        if (offlineDuplicate != null)
                            AllIcons.Remove(offlineDuplicate);

                        AllIcons.Add(live);
                        if (matchedFolder != null)
                        {
                            ConfigManager.Log("AUTO_MATCH", string.Format(
                                "自动识别并恢复归属: [{0}] (PID={1}, HWND=0x{2:X}, RegKey={3}) -> 文件夹 [{4}] (原位隐藏={5})",
                                live.AppDisplayName, live.OwnerPid, live.Hwnd, live.RegKey, matchedFolder.Name, live.IsHiddenByOrganizer));
                        }
                    }
                }

                // Remove online items that are no longer in discovered
                var goneOnline = AllIcons.Where(x => x.IsOnline && !currentOnlineKeys.Contains(x.RegKey)).ToList();
                foreach (var gone in goneOnline)
                {
                    AllIcons.Remove(gone);
                }

                // Add/update offline bound items so folders preserve and display bound apps that aren't currently running
                var desiredOfflineRules = new List<KeyValuePair<TrayFolder, IconBindingRule>>();
                foreach (var folder in Config.Folders)
                {
                    foreach (var rule in folder.Bindings)
                    {
                        if (!matchedRules.Contains(rule))
                        {
                            desiredOfflineRules.Add(new KeyValuePair<TrayFolder, IconBindingRule>(folder, rule));
                        }
                    }
                }

                // Remove stale offline items
                var currentOffline = AllIcons.Where(x => !x.IsOnline).ToList();
                foreach (var off in currentOffline)
                {
                    bool stillNeeded = desiredOfflineRules.Any(d =>
                        (d.Value.RegKey != 0 && d.Value.RegKey == off.RegKey) ||
                        (string.Equals(d.Value.NormalizedExePath, off.NormalizedExePath, StringComparison.OrdinalIgnoreCase) && d.Value.Uid == off.Uid));
                    if (!stillNeeded)
                        AllIcons.Remove(off);
                }

                foreach (var kv in desiredOfflineRules)
                {
                    var folder = kv.Key;
                    var rule = kv.Value;
                    bool alreadyInList = AllIcons.Any(x =>
                        !x.IsOnline &&
                        ((rule.RegKey != 0 && x.RegKey == rule.RegKey) ||
                         (string.Equals(x.NormalizedExePath, rule.NormalizedExePath, StringComparison.OrdinalIgnoreCase) && x.Uid == rule.Uid)));
                    if (!alreadyInList)
                    {
                        var img = TrayIconEngine.ImageFromBase64(rule.CachedIconPngBase64);
                        AllIcons.Add(new TrayIconItem
                        {
                            RegKey = rule.RegKey,
                            Uid = rule.Uid,
                            NormalizedExePath = rule.NormalizedExePath,
                            ExeFileName = rule.ExeFileName,
                            AppDisplayName = !string.IsNullOrEmpty(rule.DisplayName) ? rule.DisplayName : rule.ExeFileName,
                            InitialTooltip = rule.InitialTooltipPrefix,
                            FolderId = folder.Id,
                            FolderName = folder.Name,
                            IsOnline = false,
                            IsHiddenByOrganizer = false,
                            IconSource = img,
                            CachedIconPngBase64 = rule.CachedIconPngBase64
                        });
                    }
                }

                if (configDirty)
                    ConfigManager.SaveConfig(Config);

                UpdateFolderStatsAndHiddenStateFile();
                var h = IconsUpdated;
                if (h != null) h(this, EventArgs.Empty);
            }
            finally
            {
                _isRefreshing = false;
                UpdatePollingInterval();
            }
        }

        private void UpdateFolderStatsAndHiddenStateFile()
        {
            foreach (var folder in Folders)
            {
                var members = AllIcons.Where(x => x.FolderId == folder.Id).ToList();
                folder.ActiveCount = members.Count(x => x.IsOnline);
                folder.PreviewImages = members
                    .Where(x => x.IconSource != null)
                    .OrderByDescending(x => x.IsOnline)
                    .Select(x => x.IconSource)
                    .Take(4)
                    .ToList();
                folder.NotifyStatsChanged();
            }

            var hiddenRecords = new List<HiddenIconRecord>();
            foreach (var item in AllIcons.Where(x => x.IsOnline && (x.IsHiddenByOrganizer || x.IsOrganized)))
            {
                int? origPromoted = null;
                var folder = Config.Folders.FirstOrDefault(f => f.Id == item.FolderId);
                if (folder != null)
                {
                    var rule = folder.Bindings.FirstOrDefault(b => b.RegKey == item.RegKey || (string.Equals(b.ExeFileName, item.ExeFileName, StringComparison.OrdinalIgnoreCase) && b.Uid == item.Uid));
                    if (rule != null)
                        origPromoted = rule.OriginalIsPromoted;
                }

                hiddenRecords.Add(new HiddenIconRecord
                {
                    RegKey = item.RegKey,
                    Hwnd = item.Hwnd,
                    Uid = item.Uid,
                    IconGuid = item.GuidItem != Guid.Empty ? item.GuidItem.ToString("D") : "",
                    OriginalIsPromoted = origPromoted,
                    ExePath = item.NormalizedExePath
                });
            }

            string sig = string.Join(";", hiddenRecords.OrderBy(r => r.RegKey).Select(r => string.Format("{0}:{1}:{2}", r.RegKey, r.Hwnd, r.Uid)));
            if (sig != _lastHiddenSignature)
            {
                _lastHiddenSignature = sig;
                ConfigManager.SaveHiddenState(hiddenRecords);
            }
        }

        public TrayFolder CreateFolder(string name, string glyph = "\uE8B7")
        {
            if (string.IsNullOrWhiteSpace(name))
                name = string.Format("新建文件夹 {0}", Folders.Count + 1);

            var folder = new TrayFolder
            {
                Id = "folder_" + Guid.NewGuid().ToString("N").Substring(0, 8),
                Name = name.Trim(),
                Glyph = string.IsNullOrWhiteSpace(glyph) ? "\uE8B7" : glyph.Trim(),
                Order = Folders.Count,
                Bindings = new List<IconBindingRule>()
            };

            Config.Folders.Add(folder);
            Folders.Add(folder);
            ConfigManager.SaveConfig(Config);
            ConfigManager.Log("FOLDER", string.Format("已创建文件夹: [{0}] (Id={1})", folder.Name, folder.Id));
            UpdateFolderStatsAndHiddenStateFile();
            return folder;
        }

        public void RenameFolder(TrayFolder folder, string newName)
        {
            if (folder == null || string.IsNullOrWhiteSpace(newName)) return;
            string oldName = folder.Name;
            folder.Name = newName.Trim();
            foreach (var item in AllIcons.Where(x => x.FolderId == folder.Id))
                item.FolderName = folder.Name;

            ConfigManager.SaveConfig(Config);
            ConfigManager.Log("FOLDER", string.Format("文件夹已重命名: [{0}] -> [{1}]", oldName, folder.Name));
            var h = IconsUpdated;
            if (h != null) h(this, EventArgs.Empty);
        }

        public void MoveFolder(TrayFolder folder, int delta)
        {
            if (folder == null || delta == 0) return;
            int index = Folders.IndexOf(folder);
            if (index < 0) return;
            int target = Math.Max(0, Math.Min(Folders.Count - 1, index + delta));
            if (target == index) return;

            Folders.Move(index, target);
            for (int i = 0; i < Folders.Count; i++)
                Folders[i].Order = i;

            Config.Folders.Clear();
            foreach (var f in Folders)
                Config.Folders.Add(f);

            ConfigManager.SaveConfig(Config);
            ConfigManager.Log("FOLDER", string.Format("文件夹 [{0}] 位置调整: {1} -> {2}", folder.Name, index + 1, target + 1));
            var h = IconsUpdated;
            if (h != null) h(this, EventArgs.Empty);
        }

        public void DeleteFolder(TrayFolder folder)
        {
            if (folder == null) return;
            var affected = AllIcons.Where(x => x.FolderId == folder.Id).ToList();
            foreach (var item in affected)
            {
                var rule = folder.Bindings.FirstOrDefault(b => b.RegKey == item.RegKey);
                TrayActionDispatcher.RestoreNativeTrayIcon(item, rule != null ? rule.OriginalIsPromoted : null);
                item.FolderId = null;
                item.FolderName = "未分组";
            }

            Config.Folders.Remove(folder);
            Folders.Remove(folder);
            ConfigManager.SaveConfig(Config);
            ConfigManager.Log("FOLDER", string.Format("已删除文件夹 [{0}]，内部 {1} 个图标已移回未分组并恢复原托盘显示", folder.Name, affected.Count));
            RefreshIcons(false);
        }

        public void BindIconsToFolder(IEnumerable<TrayIconItem> items, TrayFolder targetFolder, string reason)
        {
            if (items == null || targetFolder == null) return;
            var list = items.ToList();
            if (list.Count == 0) return;

            foreach (var item in list)
            {
                ConfigManager.BindIconToFolder(Config, item, targetFolder, reason);
                if (item.IsOnline && !Config.IsPaused)
                {
                    TrayActionDispatcher.ApplyOrganizeHide(item, Config.HideStrategy);
                }
            }

            UpdateFolderStatsAndHiddenStateFile();
            var h = IconsUpdated;
            if (h != null) h(this, EventArgs.Empty);
        }

        public void UnbindIcons(IEnumerable<TrayIconItem> items, string reason)
        {
            if (items == null) return;
            var list = items.ToList();
            if (list.Count == 0) return;

            foreach (var item in list)
            {
                int? origPromoted = ConfigManager.UnbindIcon(Config, item, reason);
                if (item.IsOnline)
                {
                    TrayActionDispatcher.RestoreNativeTrayIcon(item, origPromoted);
                }
                else
                {
                    AllIcons.Remove(item);
                }
            }

            UpdateFolderStatsAndHiddenStateFile();
            var h = IconsUpdated;
            if (h != null) h(this, EventArgs.Empty);
        }

        // Only used by the --auto-verify suite; there is no UI entry point for it any more.
        public int BindTargetThreeIcons(out string summary)
        {
            var monitorFolder = Folders.FirstOrDefault(f => f.Name.Contains("监控")) ?? Folders.FirstOrDefault() ?? CreateFolder("状态监控", "\uE9D9");
            var toolFolder = Folders.FirstOrDefault(f => f.Name.Contains("外设") || f.Name.Contains("工具")) ?? monitorFolder;

            int count = 0;
            var boundNames = new List<string>();

            foreach (var item in AllIcons.Where(x => x.IsOnline).ToList())
            {
                if (item.ExeFileName.IndexOf("TrafficMonitor", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    item.ExeFileName.IndexOf("Token Monitor", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    BindIconsToFolder(new[] { item }, monitorFolder, "一键绑定首批监控目标");
                    boundNames.Add(string.Format("{0} -> {1}", item.AppDisplayName, monitorFolder.Name));
                    count++;
                }
                else if (item.ExeFileName.IndexOf("PadMouse", StringComparison.OrdinalIgnoreCase) >= 0 ||
                         item.ToolTip.IndexOf("PadMouse", StringComparison.OrdinalIgnoreCase) >= 0 ||
                         item.InitialTooltip.IndexOf("PadMouse", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    BindIconsToFolder(new[] { item }, toolFolder, "一键绑定首批外设目标");
                    boundNames.Add(string.Format("{0} -> {1}", item.AppDisplayName, toolFolder.Name));
                    count++;
                }
            }

            summary = count > 0
                ? string.Format("已成功绑定 {0} 个目标图标：{1}", count, string.Join("；", boundNames))
                : "未在当前在线托盘图标中找到流量监控、Token 监控或 PadMouse。";
            return count;
        }

        public void SetPaused(bool paused)
        {
            Config.IsPaused = paused;
            ConfigManager.SaveConfig(Config);

            if (paused)
            {
                foreach (var item in AllIcons.Where(x => x.IsOnline && x.IsOrganized))
                {
                    var folder = Config.Folders.FirstOrDefault(f => f.Id == item.FolderId);
                    var rule = folder != null ? folder.Bindings.FirstOrDefault(b => b.RegKey == item.RegKey) : null;
                    TrayActionDispatcher.RestoreNativeTrayIcon(item, rule != null ? rule.OriginalIsPromoted : null);
                }
                ConfigManager.Log("STATE", "已暂停托盘收纳：所有已收纳图标已临时恢复至系统原托盘位置");
            }
            else
            {
                foreach (var item in AllIcons.Where(x => x.IsOnline && x.IsOrganized))
                {
                    TrayActionDispatcher.ApplyOrganizeHide(item, Config.HideStrategy);
                }
                ConfigManager.Log("STATE", "已恢复托盘收纳：已按持久绑定规则收起图标");
            }

            UpdateFolderStatsAndHiddenStateFile();
            var h = IconsUpdated;
            if (h != null) h(this, EventArgs.Empty);
        }

        public void SetHideStrategy(HideStrategy strategy)
        {
            Config.HideStrategy = strategy;
            ConfigManager.SaveConfig(Config);
            ConfigManager.Log("STATE", string.Format("原位处理模式切换为: {0}", strategy));
            if (!Config.IsPaused)
            {
                foreach (var item in AllIcons.Where(x => x.IsOnline && x.IsOrganized))
                {
                    TrayActionDispatcher.ApplyOrganizeHide(item, strategy);
                }
            }
            UpdateFolderStatsAndHiddenStateFile();
            var h = IconsUpdated;
            if (h != null) h(this, EventArgs.Empty);
        }

        public void RestoreAllOnExit()
        {
            _refreshTimer.Stop();
            DragMonitor.Stop();

            int count = 0;
            foreach (var item in AllIcons.Where(x => x.IsOnline && (x.IsHiddenByOrganizer || x.IsOrganized)))
            {
                var folder = Config.Folders.FirstOrDefault(f => f.Id == item.FolderId);
                var rule = folder != null ? folder.Bindings.FirstOrDefault(b => b.RegKey == item.RegKey) : null;
                if (TrayActionDispatcher.RestoreNativeTrayIcon(item, rule != null ? rule.OriginalIsPromoted : null))
                    count++;
            }

            ConfigManager.ClearHiddenState();
            ConfigManager.Log("EXIT", string.Format("收纳工具正常退出：已将 {0} 个收纳图标恢复至原系统托盘位置", count));
            ConfigManager.FlushLog();
        }

        public void Dispose()
        {
            RestoreAllOnExit();
        }
    }
}
