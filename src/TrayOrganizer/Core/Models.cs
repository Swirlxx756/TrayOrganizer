using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Web.Script.Serialization;
using System.Windows.Media;

namespace TrayOrganizer.Core
{
    public enum HideStrategy
    {
        CompleteHide = 0, // Set NIS_HIDDEN=1 and IsPromoted=0 (completely removes from taskbar and ^ overflow)
        DemoteToOverflowOnly = 1 // Only set IsPromoted=0 (moves from taskbar into ^ overflow)
    }

    public class IconBindingRule
    {
        public IconBindingRule()
        {
            NormalizedExePath = "";
            ExeFileName = "";
            IconGuid = "";
            InitialTooltipPrefix = "";
            DisplayName = "";
            CachedIconPngBase64 = "";
        }

        public ulong RegKey { get; set; }
        public string NormalizedExePath { get; set; }
        public string ExeFileName { get; set; }
        public uint Uid { get; set; }
        public string IconGuid { get; set; }
        public string InitialTooltipPrefix { get; set; }
        public string DisplayName { get; set; }
        public string CachedIconPngBase64 { get; set; }
        public int? OriginalIsPromoted { get; set; }
    }

    public class TrayFolder : INotifyPropertyChanged
    {
        private string _id = Guid.NewGuid().ToString("N");
        private string _name = "新建文件夹";
        private string _glyph = "\uE8B7";
        private int _order;
        private List<IconBindingRule> _bindings = new List<IconBindingRule>();
        private List<ImageSource> _previewImages = new List<ImageSource>();

        public string Id
        {
            get { return _id; }
            set { _id = value; OnPropertyChanged("Id"); }
        }

        public string Name
        {
            get { return _name; }
            set { _name = value; OnPropertyChanged("Name"); }
        }

        public string Glyph
        {
            get { return _glyph; }
            set { _glyph = value; OnPropertyChanged("Glyph"); }
        }

        public int Order
        {
            get { return _order; }
            set { _order = value; OnPropertyChanged("Order"); }
        }

        public List<IconBindingRule> Bindings
        {
            get { return _bindings; }
            set { _bindings = value ?? new List<IconBindingRule>(); OnPropertyChanged("Bindings"); }
        }

        [ScriptIgnore]
        public int ActiveCount { get; set; }

        [ScriptIgnore]
        public int TotalBoundCount
        {
            get { return _bindings != null ? _bindings.Count : 0; }
        }

        [ScriptIgnore]
        public List<ImageSource> PreviewImages
        {
            get { return _previewImages; }
            set { _previewImages = value; }
        }

        public void NotifyStatsChanged()
        {
            OnPropertyChanged("ActiveCount");
            OnPropertyChanged("TotalBoundCount");
            OnPropertyChanged("PreviewImages");
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string name = null)
        {
            var handler = PropertyChanged;
            if (handler != null)
                handler(this, new PropertyChangedEventArgs(name));
        }
    }

    public class OrganizerConfig
    {
        public OrganizerConfig()
        {
            Folders = new List<TrayFolder>();
            HideStrategy = HideStrategy.CompleteHide;
            IsPaused = false;
            AutoOpenFlyoutOnExternalDrag = true;
            EnableFlyoutAnimations = true;
            ShowFolderNames = false;
            ThemeMode = 0;
            FlyoutLayout = 0;
            FolderSize = 0;
        }

        public List<TrayFolder> Folders { get; set; }
        public HideStrategy HideStrategy { get; set; }
        public bool IsPaused { get; set; }
        public bool AutoOpenFlyoutOnExternalDrag { get; set; }
        public bool EnableFlyoutAnimations { get; set; }
        public bool ShowFolderNames { get; set; }
        /// <summary>0 = follow system, 1 = light, 2 = dark.</summary>
        public int ThemeMode { get; set; }
        /// <summary>0 = folders, 1 = flat grid of all running organized icons.</summary>
        public int FlyoutLayout { get; set; }
        /// <summary>0 = small folders (open on click), 1 = large folders with directly clickable app icons.</summary>
        public int FolderSize { get; set; }
    }

    public class HiddenIconRecord
    {
        public HiddenIconRecord()
        {
            IconGuid = "";
            ExePath = "";
        }

        public ulong RegKey { get; set; }
        public ulong Hwnd { get; set; }
        public uint Uid { get; set; }
        public string IconGuid { get; set; }
        public int? OriginalIsPromoted { get; set; }
        public string ExePath { get; set; }
    }

    public class ActiveHiddenStateFile
    {
        public ActiveHiddenStateFile()
        {
            HiddenIcons = new List<HiddenIconRecord>();
        }

        public int OwnerPid { get; set; }
        public DateTime UpdatedAtUtc { get; set; }
        public List<HiddenIconRecord> HiddenIcons { get; set; }
    }

    public class TrayIconItem : INotifyPropertyChanged
    {
        private bool _isSelected;
        private string _toolTip = "";
        private string _folderId;
        private string _folderName = "未分组";
        private bool _isHiddenByOrganizer;
        private bool _isPromotedInRegistry;
        private bool _isOnline = true;
        private ImageSource _iconSource;

        public TrayIconItem()
        {
            RawExePath = "";
            NormalizedExePath = "";
            ExeFileName = "";
            AppDisplayName = "";
            InitialTooltip = "";
            Publisher = "";
            CachedIconPngBase64 = "";
            NativeVisibleInTray = true;
        }

        public ulong RegKey { get; set; }
        public ulong Hwnd { get; set; }
        public uint Uid { get; set; }
        public Guid GuidItem { get; set; }
        public uint CallbackMessage { get; set; }
        public uint Version { get; set; }
        public ulong HIcon { get; set; }
        public uint OwnerPid { get; set; }

        public string RawExePath { get; set; }
        public string NormalizedExePath { get; set; }
        public string ExeFileName { get; set; }
        public string AppDisplayName { get; set; }
        public string InitialTooltip { get; set; }
        public string Publisher { get; set; }
        public string CachedIconPngBase64 { get; set; }

        public bool NativeVisibleInTray { get; set; }
        public NativeMethods.RECT LastBounds { get; set; }

        public bool IsSelected
        {
            get { return _isSelected; }
            set { _isSelected = value; OnPropertyChanged("IsSelected"); }
        }

        public string ToolTip
        {
            get { return _toolTip; }
            set { _toolTip = value ?? ""; OnPropertyChanged("ToolTip"); OnPropertyChanged("EffectiveSubtitle"); }
        }

        public string FolderId
        {
            get { return _folderId; }
            set { _folderId = value; OnPropertyChanged("FolderId"); OnPropertyChanged("IsOrganized"); }
        }

        public string FolderName
        {
            get { return _folderName; }
            set { _folderName = value; OnPropertyChanged("FolderName"); }
        }

        public bool IsOrganized
        {
            get { return !string.IsNullOrEmpty(_folderId); }
        }

        public bool IsHiddenByOrganizer
        {
            get { return _isHiddenByOrganizer; }
            set { _isHiddenByOrganizer = value; OnPropertyChanged("IsHiddenByOrganizer"); OnPropertyChanged("StatusBadgeText"); }
        }

        public bool IsPromotedInRegistry
        {
            get { return _isPromotedInRegistry; }
            set { _isPromotedInRegistry = value; OnPropertyChanged("IsPromotedInRegistry"); OnPropertyChanged("StatusBadgeText"); }
        }

        public bool IsOnline
        {
            get { return _isOnline; }
            set { _isOnline = value; OnPropertyChanged("IsOnline"); OnPropertyChanged("StatusBadgeText"); OnPropertyChanged("EffectiveSubtitle"); }
        }

        public ImageSource IconSource
        {
            get { return _iconSource; }
            set { _iconSource = value; OnPropertyChanged("IconSource"); }
        }

        public string EffectiveSubtitle
        {
            get
            {
                if (!IsOnline)
                    return "未运行 · 绑定已保留";
                if (!string.IsNullOrWhiteSpace(ToolTip))
                    return ToolTip.Replace("\r\n", " · ").Replace("\n", " · ");
                if (!string.IsNullOrWhiteSpace(InitialTooltip))
                    return InitialTooltip.Replace("\r\n", " · ").Replace("\n", " · ");
                return string.Format("{0} (PID={1}, UID={2})", ExeFileName, OwnerPid, Uid);
            }
        }

        public string IdentitySummary
        {
            get
            {
                return GuidItem != Guid.Empty
                    ? string.Format("RegKey={0} | GUID={1}", RegKey, GuidItem)
                    : string.Format("RegKey={0} | {1} | UID={2}", RegKey, ExeFileName, Uid);
            }
        }

        public string StatusBadgeText
        {
            get
            {
                if (!IsOnline) return "未运行";
                if (IsHiddenByOrganizer) return "已收纳";
                if (IsPromotedInRegistry) return "任务栏";
                return "溢出区 (^)";
            }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string name = null)
        {
            var handler = PropertyChanged;
            if (handler != null)
                handler(this, new PropertyChangedEventArgs(name));
        }
    }
}
