using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using TrayOrganizer.Core;

namespace TrayOrganizer.UI
{
    public partial class MainWindow : Window
    {
        private const string InternalDragFormat = "TrayOrganizer_InternalIcons";

        private sealed class CardParts
        {
            public Border Card;
            public CheckBox Check;
            public Image Icon;
            public TextBlock Subtitle;
            public TextBlock Status;
            public bool? SelectionApplied;
        }

        private sealed class FolderNavParts
        {
            public RadioButton Nav;
            public TextBlock Count;
        }

        private readonly OrganizerController _controller;
        private readonly Action _showFlyoutCallback;
        private readonly Action _exitApplicationCallback;

        private string _mode = "ALL"; // "ALL", "ORGANIZED", "UNGROUPED", "FOLDER"
        private TrayFolder _selectedFolder;
        private TrayFolder _defaultBindTarget; // preselected "收纳到" folder (set from the tray flyout)
        private bool _settingsPage;
        private string _searchText = "";

        private List<TrayIconItem> _visibleItems = new List<TrayIconItem>();
        private readonly Dictionary<TrayIconItem, CardParts> _cards = new Dictionary<TrayIconItem, CardParts>();
        private readonly Dictionary<TrayFolder, FolderNavParts> _folderNavs = new Dictionary<TrayFolder, FolderNavParts>();
        private string _iconsSignature = null;
        private string _foldersSignature = null;

        private bool _internalDragging;
        private bool _pendingRefresh;
        private bool _refreshQueued;
        private bool _shownOnce;
        private Border _dragArmedCard;
        private Point _dragStartPoint;
        private RadioButton _externalHighlightNav;

        // ContentDialog state
        private Func<string, bool> _dialogConfirm;
        private IInputElement _dialogRestoreFocus;
        private int _dialogVersion;

        public bool AllowTrueClose { get; set; }

        public MainWindow(
            OrganizerController controller,
            Action showFlyoutCallback,
            Action exitApplicationCallback)
        {
            InitializeComponent();
            _controller = controller;
            _showFlyoutCallback = showFlyoutCallback;
            _exitApplicationCallback = exitApplicationCallback;

            try { Icon = AppIconFactory.CreateWindowIcon(); }
            catch { }

            ConfigDirText.Text = ConfigManager.AppDataDir;
            SyncSettingsControls();
            StartupToggle.IsChecked = StartupManager.IsEnabled();

            ConfigManager.OnLogMessage += AppendLogLine;
            _controller.IconsUpdated += (s, e) => QueueRefresh();
            IsVisibleChanged += (s, e) =>
            {
                if (IsVisible)
                {
                    _shownOnce = true;
                    StartupToggle.IsChecked = StartupManager.IsEnabled();
                    RefreshUI();
                }
            };

            AppendLogLine(string.Format("[{0:yyyy-MM-dd HH:mm:ss.fff}] [INIT] 托盘收纳主界面已就绪，配置文件: {1}", DateTime.Now, ConfigManager.ConfigFilePath));
            RefreshUI();
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            ThemeManager.ApplyWindowChrome(this, NativeMethods.DWMSBT_MAINWINDOW, "WindowFallback", false);
        }

        // ---------------------------------------------------------------- helpers

        private Style Sty(string key)
        {
            return TryFindResource(key) as Style;
        }

        private FontFamily IconFontFamily
        {
            get
            {
                return (TryFindResource("IconFont") as FontFamily) ?? new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets");
            }
        }

        private TextBlock MakeGlyph(string glyph, double size)
        {
            return new TextBlock
            {
                Text = glyph,
                FontFamily = IconFontFamily,
                FontSize = size,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
        }

        private static string Truncate(string s, int max)
        {
            if (s == null) return "";
            return s.Length > max ? s.Substring(0, max) + "…" : s;
        }

        private void AppendLogLine(string line)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (LogTextBox.Text.Length > 18000)
                    LogTextBox.Text = LogTextBox.Text.Substring(LogTextBox.Text.Length - 12000);
                LogTextBox.AppendText(line + Environment.NewLine);
                LogTextBox.ScrollToEnd();
            }));
        }

        private void QueueRefresh()
        {
            if (_refreshQueued) return;
            _refreshQueued = true;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                _refreshQueued = false;
                RefreshUI();
            }), DispatcherPriority.Background);
        }

        private void RunLater(Action action)
        {
            Dispatcher.BeginInvoke(action, DispatcherPriority.Background);
        }

        // ---------------------------------------------------------------- refresh

        public void RefreshUI()
        {
            if (_internalDragging)
            {
                _pendingRefresh = true;
                return;
            }
            if (_shownOnce && !IsVisible)
            {
                _pendingRefresh = true;
                return;
            }
            _pendingRefresh = false;

            if (_mode == "FOLDER" && (_selectedFolder == null || !_controller.Folders.Contains(_selectedFolder)))
            {
                _mode = "ALL";
                _selectedFolder = null;
            }

            EnsureFoldersNav();
            UpdateNavState();
            UpdateHeader();
            SyncSettingsControls();
            RefreshIconsList();
        }

        private void UpdateNavState()
        {
            int total = _controller.AllIcons.Count;
            int organized = _controller.AllIcons.Count(x => x.IsOrganized);
            NavAllCount.Text = total.ToString();
            NavOrganizedCount.Text = organized.ToString();
            NavUngroupedCount.Text = (total - organized).ToString();

            bool onIcons = !_settingsPage;
            SetNavChecked(NavAll, onIcons && _mode == "ALL");
            SetNavChecked(NavOrganized, onIcons && _mode == "ORGANIZED");
            SetNavChecked(NavUngrouped, onIcons && _mode == "UNGROUPED");
            SetNavChecked(NavSettings, _settingsPage);

            foreach (var kv in _folderNavs)
            {
                kv.Value.Count.Text = string.Format("{0}/{1}", kv.Key.ActiveCount, kv.Key.TotalBoundCount);
                SetNavChecked(kv.Value.Nav, onIcons && _mode == "FOLDER" && _selectedFolder == kv.Key);
            }
        }

        private static void SetNavChecked(RadioButton rb, bool value)
        {
            if (rb.IsChecked != value)
                rb.IsChecked = value;
        }

        private void UpdateHeader()
        {
            string title;
            switch (_mode)
            {
                case "ORGANIZED": title = "已收纳"; break;
                case "UNGROUPED": title = "未分组"; break;
                case "FOLDER": title = _selectedFolder != null ? _selectedFolder.Name : "全部图标"; break;
                default: title = "全部图标"; break;
            }
            if (!string.IsNullOrEmpty(_searchText))
                title = title + " · 搜索结果";
            PageTitle.Text = title;

            int onlineTotal = _controller.AllIcons.Count(x => x.IsOnline);
            int organizedOnline = _controller.AllIcons.Count(x => x.IsOnline && x.IsOrganized);
            PageStats.Text = string.Format("在线 {0} 个 · 已收纳 {1} 个 · 未分组 {2} 个 · 文件夹 {3} 个",
                onlineTotal, organizedOnline, onlineTotal - organizedOnline, _controller.Folders.Count);

            PausedBanner.Visibility = _controller.Config.IsPaused ? Visibility.Visible : Visibility.Collapsed;
        }

        private void SyncSettingsControls()
        {
            var cfg = _controller.Config;
            bool complete = cfg.HideStrategy == HideStrategy.CompleteHide;
            if (HideCompleteRadio.IsChecked != complete) HideCompleteRadio.IsChecked = complete;
            if (HideDemoteRadio.IsChecked != !complete) HideDemoteRadio.IsChecked = !complete;
            if (PauseToggle.IsChecked != cfg.IsPaused) PauseToggle.IsChecked = cfg.IsPaused;
            if (AutoOpenToggle.IsChecked != cfg.AutoOpenFlyoutOnExternalDrag) AutoOpenToggle.IsChecked = cfg.AutoOpenFlyoutOnExternalDrag;
            if (FlyoutAnimToggle.IsChecked != cfg.EnableFlyoutAnimations) FlyoutAnimToggle.IsChecked = cfg.EnableFlyoutAnimations;
            if (ShowFolderNamesToggle.IsChecked != cfg.ShowFolderNames) ShowFolderNamesToggle.IsChecked = cfg.ShowFolderNames;
            bool flat = cfg.FlyoutLayout == 1;
            if (LayoutFolderRadio.IsChecked != !flat) LayoutFolderRadio.IsChecked = !flat;
            if (LayoutFlatRadio.IsChecked != flat) LayoutFlatRadio.IsChecked = flat;
            bool large = cfg.FolderSize == 1;
            if (FolderSizeSmallRadio.IsChecked != !large) FolderSizeSmallRadio.IsChecked = !large;
            if (FolderSizeLargeRadio.IsChecked != large) FolderSizeLargeRadio.IsChecked = large;
            int tm = cfg.ThemeMode;
            if (ThemeSystemRadio.IsChecked != (tm != 1 && tm != 2)) ThemeSystemRadio.IsChecked = (tm != 1 && tm != 2);
            if (ThemeLightRadio.IsChecked != (tm == 1)) ThemeLightRadio.IsChecked = (tm == 1);
            if (ThemeDarkRadio.IsChecked != (tm == 2)) ThemeDarkRadio.IsChecked = (tm == 2);
        }

        private void LayoutRadio_Click(object sender, RoutedEventArgs e)
        {
            var rb = sender as RadioButton;
            if (rb == null) return;
            int mode;
            if (!int.TryParse(rb.Tag as string, out mode)) mode = 0;
            _controller.Config.FlyoutLayout = mode == 1 ? 1 : 0;
            ConfigManager.SaveConfig(_controller.Config);
            FlyoutWindow.RaiseAppearanceChanged();
        }

        private void FolderSizeRadio_Click(object sender, RoutedEventArgs e)
        {
            var rb = sender as RadioButton;
            if (rb == null) return;
            int mode;
            if (!int.TryParse(rb.Tag as string, out mode)) mode = 0;
            _controller.Config.FolderSize = mode == 1 ? 1 : 0;
            ConfigManager.SaveConfig(_controller.Config);
            FlyoutWindow.RaiseAppearanceChanged();
        }

        private void ThemeRadio_Click(object sender, RoutedEventArgs e)
        {
            var rb = sender as RadioButton;
            if (rb == null) return;
            int mode;
            if (!int.TryParse(rb.Tag as string, out mode)) mode = 0;
            _controller.Config.ThemeMode = mode;
            ConfigManager.SaveConfig(_controller.Config);
            ThemeManager.SetMode(mode);
        }

        private void ShowPages()
        {
            IconsPage.Visibility = _settingsPage ? Visibility.Collapsed : Visibility.Visible;
            SettingsPage.Visibility = _settingsPage ? Visibility.Visible : Visibility.Collapsed;
        }

        // ---------------------------------------------------------------- navigation

        private void SwitchView(string mode, TrayFolder folder)
        {
            bool changed = _settingsPage || _mode != mode || _selectedFolder != folder;
            _settingsPage = false;
            _mode = mode;
            _selectedFolder = folder;
            if (changed) ClearSelection();
            if (mode != "UNGROUPED") _defaultBindTarget = null;
            ShowPages();
            RefreshUI();
        }

        /// <summary>
        /// Switches to the 未分组 page and makes <paramref name="targetFolder"/> the preselected
        /// (first, checked) entry of the 收纳到 menu. Null keeps the normal menu.
        /// </summary>
        public void ShowUngroupedForAdding(TrayFolder targetFolder)
        {
            if (!string.IsNullOrEmpty(_searchText))
                SearchBox.Text = "";
            SwitchView("UNGROUPED", null);
            _defaultBindTarget = targetFolder;
        }

        private void ClearSelection()
        {
            foreach (var item in _controller.AllIcons)
                item.IsSelected = false;
        }

        private void NavItem_Click(object sender, RoutedEventArgs e)
        {
            var rb = sender as RadioButton;
            string tag = rb != null ? rb.Tag as string : null;
            if (tag == null) return;

            if (tag == "SETTINGS")
            {
                _settingsPage = true;
                ShowPages();
                RefreshUI();
                return;
            }
            SwitchView(tag, null);
        }

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_controller == null) return;
            _searchText = (SearchBox.Text ?? "").Trim();
            if (_settingsPage)
            {
                _settingsPage = false;
                ShowPages();
            }
            RefreshUI();
        }

        // ---------------------------------------------------------------- folders sidebar

        private void EnsureFoldersNav()
        {
            var sb = new StringBuilder();
            foreach (var f in _controller.Folders)
                sb.Append(f.Id).Append(':').Append(f.Name).Append(';');
            string sig = sb.ToString();
            if (sig == _foldersSignature) return;
            _foldersSignature = sig;

            FoldersPanel.Children.Clear();
            _folderNavs.Clear();
            _externalHighlightNav = null;

            int count = _controller.Folders.Count;
            int index = 0;
            foreach (var folder in _controller.Folders)
            {
                var parts = CreateFolderNav(folder, index, count);
                _folderNavs[folder] = parts;
                FoldersPanel.Children.Add(parts.Nav);
                index++;
            }
        }

        private FolderNavParts CreateFolderNav(TrayFolder folder, int index, int total)
        {
            var nav = new RadioButton
            {
                Style = Sty("NavItem"),
                GroupName = "Nav",
                AllowDrop = true,
                Tag = folder,
                Margin = new Thickness(0, 0, 0, 2)
            };

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var glyph = new TextBlock
            {
                Style = Sty("Glyph"),
                Text = "",
                Margin = new Thickness(0, 0, 12, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            var name = new TextBlock
            {
                Text = folder.Name,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };
            Grid.SetColumn(name, 1);
            var count = new TextBlock
            {
                FontSize = 12,
                Margin = new Thickness(8, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            count.SetResourceReference(TextBlock.ForegroundProperty, "TextTertiary");
            Grid.SetColumn(count, 2);

            grid.Children.Add(glyph);
            grid.Children.Add(name);
            grid.Children.Add(count);
            nav.Content = grid;

            nav.Click += (s, e) => SwitchView("FOLDER", folder);

            // Internal WPF drag & drop target
            nav.DragEnter += (s, e) => HandleFolderDrag(nav, e);
            nav.DragOver += (s, e) => HandleFolderDrag(nav, e);
            nav.DragLeave += (s, e) => SetDropHighlight(nav, false);
            nav.Drop += (s, e) =>
            {
                SetDropHighlight(nav, false);
                var dropped = e.Data.GetData(InternalDragFormat) as List<TrayIconItem>;
                if (dropped != null && dropped.Count > 0)
                {
                    _controller.BindIconsToFolder(dropped, folder, "主界面内拖拽分组");
                    foreach (var d in dropped) d.IsSelected = false;
                    RefreshUI();
                }
                e.Handled = true;
            };

            // Context menu
            var menu = new ContextMenu();
            var rename = new MenuItem { Header = "重命名", Icon = MakeGlyph("", 14) };
            rename.Click += (s, e) => PromptRenameFolder(folder);
            var up = new MenuItem { Header = "上移", Icon = MakeGlyph("", 14), IsEnabled = index > 0 };
            up.Click += (s, e) => { _controller.MoveFolder(folder, -1); RefreshUI(); };
            var down = new MenuItem { Header = "下移", Icon = MakeGlyph("", 14), IsEnabled = index < total - 1 };
            down.Click += (s, e) => { _controller.MoveFolder(folder, 1); RefreshUI(); };
            var del = new MenuItem { Header = "删除", Icon = MakeGlyph("", 14) };
            del.Click += (s, e) => PromptDeleteFolder(folder);
            menu.Items.Add(rename);
            menu.Items.Add(up);
            menu.Items.Add(down);
            menu.Items.Add(new Separator());
            menu.Items.Add(del);
            nav.ContextMenu = menu;

            return new FolderNavParts { Nav = nav, Count = count };
        }

        private void HandleFolderDrag(RadioButton nav, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(InternalDragFormat))
            {
                e.Effects = DragDropEffects.Move;
                SetDropHighlight(nav, true);
            }
            else
            {
                e.Effects = DragDropEffects.None;
            }
            e.Handled = true;
        }

        private static void SetDropHighlight(RadioButton nav, bool on)
        {
            if (on)
                nav.SetResourceReference(Control.BorderBrushProperty, "AccentFill");
            else
                nav.ClearValue(Control.BorderBrushProperty);
        }

        private void NewFolderBtn_Click(object sender, RoutedEventArgs e)
        {
            PromptNewFolder(null, true);
        }

        private void PromptNewFolder(List<TrayIconItem> thenBind, bool navigate)
        {
            ShowDialog("新建文件夹", "为新的收纳文件夹命名。", "新建文件夹", "创建", name =>
            {
                var folder = _controller.CreateFolder((name ?? "").Trim());
                if (thenBind != null && thenBind.Count > 0)
                {
                    _controller.BindIconsToFolder(thenBind, folder, string.Format("主界面新建文件夹并收纳 ({0} 项)", thenBind.Count));
                    foreach (var it in thenBind) it.IsSelected = false;
                }
                if (navigate)
                    SwitchView("FOLDER", folder);
                else
                    RefreshUI();
                return true;
            });
        }

        private void PromptRenameFolder(TrayFolder folder)
        {
            ShowDialog("重命名文件夹", "输入新的文件夹名称。", folder.Name, "重命名", name =>
            {
                if (string.IsNullOrWhiteSpace(name)) return false;
                _controller.RenameFolder(folder, name);
                RefreshUI();
                return true;
            });
        }

        private void PromptDeleteFolder(TrayFolder folder)
        {
            ShowDialog("删除文件夹",
                string.Format("确定要删除“{0}”吗？其中的图标会移回“未分组”，并恢复到系统托盘的原位置。", folder.Name),
                null, "删除", name =>
                {
                    _controller.DeleteFolder(folder);
                    if (_selectedFolder == folder)
                    {
                        _mode = "ALL";
                        _selectedFolder = null;
                    }
                    RefreshUI();
                    return true;
                });
        }

        // ---------------------------------------------------------------- icon list

        private List<TrayIconItem> GetFilteredIcons()
        {
            IEnumerable<TrayIconItem> query = _controller.AllIcons;
            switch (_mode)
            {
                case "ORGANIZED":
                    query = query.Where(x => x.IsOrganized);
                    break;
                case "UNGROUPED":
                    query = query.Where(x => !x.IsOrganized);
                    break;
                case "FOLDER":
                    if (_selectedFolder != null)
                    {
                        string fid = _selectedFolder.Id;
                        query = query.Where(x => x.FolderId == fid);
                    }
                    break;
            }

            if (!string.IsNullOrEmpty(_searchText))
            {
                string q = _searchText;
                query = query.Where(x =>
                    Contains(x.AppDisplayName, q) || Contains(x.ExeFileName, q) || Contains(x.ToolTip, q));
            }

            return query
                .OrderByDescending(x => x.IsOnline)
                .ThenByDescending(x => x.IsOrganized)
                .ThenBy(x => x.AppDisplayName)
                .ToList();
        }

        private static bool Contains(string haystack, string needle)
        {
            return !string.IsNullOrEmpty(haystack) && haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private string BuildSignature(List<TrayIconItem> items)
        {
            var sb = new StringBuilder();
            sb.Append(_mode).Append('|')
              .Append(_selectedFolder != null ? _selectedFolder.Id : "").Append('|')
              .Append(_searchText).Append('|')
              .Append(_controller.Config.IsPaused ? '1' : '0').Append('|');
            foreach (var f in _controller.Folders)
                sb.Append(f.Id).Append(':').Append(f.Name).Append(',');
            sb.Append('|');
            foreach (var it in items)
            {
                sb.Append(RuntimeHelpers.GetHashCode(it)).Append(':')
                  .Append(it.IsOnline ? '1' : '0')
                  .Append(it.IsHiddenByOrganizer ? '1' : '0')
                  .Append(it.IsPromotedInRegistry ? '1' : '0').Append(':')
                  .Append(it.FolderId ?? "").Append(':')
                  .Append(it.FolderName ?? "").Append(':')
                  .Append(it.AppDisplayName ?? "").Append(':')
                  .Append(it.IdentitySummary).Append(';');
            }
            return sb.ToString();
        }

        private void RefreshIconsList()
        {
            var items = GetFilteredIcons();
            _visibleItems = items;
            string sig = BuildSignature(items);

            if (sig != _iconsSignature)
            {
                _iconsSignature = sig;
                double offset = IconsScroll.VerticalOffset;

                IconsListPanel.Children.Clear();
                _cards.Clear();
                foreach (var item in items)
                {
                    var parts = CreateCard(item);
                    _cards[item] = parts;
                    IconsListPanel.Children.Add(parts.Card);
                }

                bool empty = items.Count == 0;
                IconsScroll.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
                EmptyState.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
                if (empty) UpdateEmptyState();

                if (!empty && offset > 0)
                {
                    IconsScroll.UpdateLayout();
                    IconsScroll.ScrollToVerticalOffset(offset);
                }
            }
            else
            {
                foreach (var item in items)
                {
                    CardParts parts;
                    if (_cards.TryGetValue(item, out parts))
                        UpdateCardInPlace(item, parts);
                }
            }

            UpdateSelectionUI();
        }

        private void UpdateEmptyState()
        {
            if (!string.IsNullOrEmpty(_searchText))
            {
                EmptyGlyph.Text = "";
                EmptyTitle.Text = "没有找到匹配的图标";
                EmptyHint.Text = "试试其他关键字，搜索范围包括应用名称、文件名和提示文字。";
                return;
            }
            EmptyGlyph.Text = "";
            switch (_mode)
            {
                case "FOLDER":
                    EmptyTitle.Text = "这个文件夹还是空的";
                    EmptyHint.Text = "把图标拖到左侧文件夹，或勾选图标后点击“收纳到”。也可以直接从系统托盘把图标拖进来。";
                    break;
                case "ORGANIZED":
                    EmptyTitle.Text = "还没有已收纳的图标";
                    EmptyHint.Text = "在“全部图标”里勾选图标，然后点击“收纳到”即可。";
                    break;
                case "UNGROUPED":
                    EmptyTitle.Text = "所有图标都已收纳";
                    EmptyHint.Text = "";
                    break;
                default:
                    EmptyTitle.Text = "没有检测到托盘图标";
                    EmptyHint.Text = "运行中的托盘程序会显示在这里。";
                    break;
            }
        }

        private static string SubtitleOf(TrayIconItem item)
        {
            return Truncate(item.EffectiveSubtitle, 140);
        }

        private CardParts CreateCard(TrayIconItem item)
        {
            var parts = new CardParts();

            var card = new Border
            {
                Style = Sty("Card"),
                Padding = new Thickness(12, 10, 12, 10),
                Margin = new Thickness(0, 0, 0, 4),
                Tag = item,
                ToolTip = item.IdentitySummary,
                Opacity = item.IsOnline ? 1.0 : 0.6
            };
            parts.Card = card;

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // check
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // icon
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }); // text
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); // right side

            var cb = new CheckBox
            {
                MinWidth = 0,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0),
                IsChecked = item.IsSelected
            };
            cb.Click += (s, e) =>
            {
                item.IsSelected = cb.IsChecked == true;
                ApplyCardSelection(parts, item);
                UpdateSelectionUI();
            };
            parts.Check = cb;

            var icon = new Image
            {
                Width = 32,
                Height = 32,
                Stretch = Stretch.Uniform,
                Source = item.IconSource,
                Margin = new Thickness(0, 0, 12, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            RenderOptions.SetBitmapScalingMode(icon, BitmapScalingMode.HighQuality);
            Grid.SetColumn(icon, 1);
            parts.Icon = icon;

            var textStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            textStack.Children.Add(new TextBlock
            {
                Style = Sty("BodyStrong"),
                Text = string.IsNullOrEmpty(item.AppDisplayName) ? item.ExeFileName : item.AppDisplayName,
                TextTrimming = TextTrimming.CharacterEllipsis
            });
            var subtitle = new TextBlock
            {
                Style = Sty("CaptionText"),
                Text = SubtitleOf(item),
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 1, 8, 0)
            };
            textStack.Children.Add(subtitle);
            Grid.SetColumn(textStack, 2);
            parts.Subtitle = subtitle;

            var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            var status = new TextBlock
            {
                Text = item.StatusBadgeText,
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 12, 0)
            };
            status.SetResourceReference(TextBlock.ForegroundProperty, "TextTertiary");
            right.Children.Add(status);
            parts.Status = status;

            if (item.IsOrganized)
            {
                var pill = new Border
                {
                    CornerRadius = new CornerRadius(4),
                    Padding = new Thickness(8, 2, 8, 3),
                    Margin = new Thickness(0, 0, 8, 0),
                    VerticalAlignment = VerticalAlignment.Center
                };
                pill.SetResourceReference(Border.BackgroundProperty, "AccentSubtle");
                var pillText = new TextBlock
                {
                    Text = item.FolderName,
                    FontSize = 12,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    MaxWidth = 140
                };
                pillText.SetResourceReference(TextBlock.ForegroundProperty, "AccentText");
                pill.Child = pillText;
                right.Children.Add(pill);
            }

            var more = new Button
            {
                Style = Sty("SubtleButton"),
                Width = 32,
                Height = 32,
                MinHeight = 32,
                Padding = new Thickness(0),
                ToolTip = "更多操作",
                Content = MakeGlyph("", 16)
            };
            more.Click += (s, e) =>
            {
                e.Handled = true;
                ShowMenuBelowRight(BuildItemMenu(item), more);
            };
            right.Children.Add(more);
            Grid.SetColumn(right, 3);

            grid.Children.Add(cb);
            grid.Children.Add(icon);
            grid.Children.Add(textStack);
            grid.Children.Add(right);
            card.Child = grid;

            // hover
            card.MouseEnter += (s, e) => card.SetResourceReference(Border.BackgroundProperty, "CardFillHover");
            card.MouseLeave += (s, e) => card.SetResourceReference(Border.BackgroundProperty, "CardFill");

            // internal drag to folder
            card.MouseLeftButtonDown += (s, e) =>
            {
                _dragArmedCard = card;
                _dragStartPoint = e.GetPosition(this);
            };
            card.MouseLeftButtonUp += (s, e) => { _dragArmedCard = null; };
            card.MouseMove += (s, e) =>
            {
                if (e.LeftButton != MouseButtonState.Pressed || _dragArmedCard != card || _internalDragging)
                    return;
                Point pos = e.GetPosition(this);
                if (Math.Abs(pos.X - _dragStartPoint.X) <= SystemParameters.MinimumHorizontalDragDistance &&
                    Math.Abs(pos.Y - _dragStartPoint.Y) <= SystemParameters.MinimumVerticalDragDistance)
                    return;

                _dragArmedCard = null;
                var data = new DataObject(InternalDragFormat, TargetsFor(item));
                _internalDragging = true;
                try
                {
                    DragDrop.DoDragDrop(card, data, DragDropEffects.Move);
                }
                finally
                {
                    _internalDragging = false;
                    if (_pendingRefresh) QueueRefresh();
                }
            };

            // our own context menu
            card.MouseRightButtonUp += (s, e) =>
            {
                e.Handled = true;
                var menu = BuildItemMenu(item);
                menu.PlacementTarget = card;
                menu.Placement = PlacementMode.MousePoint;
                menu.IsOpen = true;
            };

            ApplyCardSelection(parts, item);
            return parts;
        }

        private void UpdateCardInPlace(TrayIconItem item, CardParts parts)
        {
            if (!object.ReferenceEquals(parts.Icon.Source, item.IconSource))
                parts.Icon.Source = item.IconSource;

            string sub = SubtitleOf(item);
            if (parts.Subtitle.Text != sub)
                parts.Subtitle.Text = sub;

            string status = item.StatusBadgeText;
            if (parts.Status.Text != status)
                parts.Status.Text = status;

            ApplyCardSelection(parts, item);
        }

        private static void ApplyCardSelection(CardParts parts, TrayIconItem item)
        {
            if (parts.Check.IsChecked != item.IsSelected)
                parts.Check.IsChecked = item.IsSelected;
            if (parts.SelectionApplied != item.IsSelected)
            {
                parts.SelectionApplied = item.IsSelected;
                parts.Card.SetResourceReference(Border.BorderBrushProperty, item.IsSelected ? "AccentFill" : "CardStroke");
            }
        }

        // ---------------------------------------------------------------- selection

        private List<TrayIconItem> GetSelectedVisible()
        {
            return _visibleItems.Where(x => x.IsSelected).ToList();
        }

        private List<TrayIconItem> TargetsFor(TrayIconItem item)
        {
            var sel = GetSelectedVisible();
            if (item.IsSelected && sel.Count > 1)
                return sel;
            return new List<TrayIconItem> { item };
        }

        private void UpdateSelectionUI()
        {
            int total = _visibleItems.Count;
            int sel = _visibleItems.Count(x => x.IsSelected);
            SelectionText.Text = string.Format("已选 {0} 项", sel);
            SelectAllCheck.IsEnabled = total > 0;
            if (sel == 0)
                SelectAllCheck.IsChecked = false;
            else if (sel == total)
                SelectAllCheck.IsChecked = true;
            else
                SelectAllCheck.IsChecked = null;

            BindToBtn.IsEnabled = sel > 0;
            UnbindBtn.IsEnabled = _visibleItems.Any(x => x.IsSelected && x.IsOrganized);
        }

        private void SelectAllCheck_Click(object sender, RoutedEventArgs e)
        {
            bool allSelected = _visibleItems.Count > 0 && _visibleItems.All(x => x.IsSelected);
            bool target = !allSelected;
            foreach (var it in _visibleItems)
                it.IsSelected = target;
            foreach (var kv in _cards)
                ApplyCardSelection(kv.Value, kv.Key);
            UpdateSelectionUI();
        }

        // ---------------------------------------------------------------- commands / menus

        private void BindItems(List<TrayIconItem> items, TrayFolder folder, string reason)
        {
            if (items == null || items.Count == 0 || folder == null) return;
            _controller.BindIconsToFolder(items, folder, reason);
            _defaultBindTarget = null;
            foreach (var it in items) it.IsSelected = false;
            RefreshUI();
        }

        private void UnbindItems(List<TrayIconItem> items)
        {
            var organized = items.Where(x => x.IsOrganized).ToList();
            if (organized.Count == 0) return;
            _controller.UnbindIcons(organized, string.Format("主界面移出收纳 ({0} 项)", organized.Count));
            foreach (var it in organized) it.IsSelected = false;
            RefreshUI();
        }

        private static void ShowMenuBelowRight(ContextMenu menu, FrameworkElement anchor)
        {
            menu.PlacementTarget = anchor;
            menu.Placement = PlacementMode.Custom;
            menu.CustomPopupPlacementCallback = (popupSize, targetSize, offset) =>
                new[]
                {
                    new CustomPopupPlacement(new Point(targetSize.Width - popupSize.Width, targetSize.Height), PopupPrimaryAxis.Horizontal)
                };
            menu.IsOpen = true;
        }

        private ContextMenu BuildItemMenu(TrayIconItem item)
        {
            var targets = TargetsFor(item);
            var menu = new ContextMenu();

            var move = new MenuItem { Header = "移动到", Icon = MakeGlyph("", 14) };
            foreach (var f in _controller.Folders)
            {
                var folder = f;
                var mi = new MenuItem { Header = folder.Name, IsChecked = item.FolderId == folder.Id };
                mi.Click += (s, e) => BindItems(targets, folder, string.Format("主界面菜单移动 ({0} 项)", targets.Count));
                move.Items.Add(mi);
            }
            if (_controller.Folders.Count > 0)
                move.Items.Add(new Separator());
            var newFolder = new MenuItem { Header = "新建文件夹…", Icon = MakeGlyph("", 14) };
            newFolder.Click += (s, e) => PromptNewFolder(targets, false);
            move.Items.Add(newFolder);
            menu.Items.Add(move);

            var unbind = new MenuItem
            {
                Header = "移出收纳",
                Icon = MakeGlyph("", 14),
                IsEnabled = targets.Any(x => x.IsOrganized)
            };
            unbind.Click += (s, e) => UnbindItems(targets);
            menu.Items.Add(unbind);

            menu.Items.Add(new Separator());

            var left = new MenuItem { Header = "模拟左键单击", IsEnabled = item.IsOnline };
            left.Click += (s, e) => RunLater(() => TrayActionDispatcher.InvokeLeftClick(item));
            var dbl = new MenuItem { Header = "模拟双击", IsEnabled = item.IsOnline };
            dbl.Click += (s, e) => RunLater(() => TrayActionDispatcher.InvokeDoubleClick(item));
            var native = new MenuItem { Header = "打开应用原生菜单", IsEnabled = item.IsOnline };
            native.Click += (s, e) => RunLater(() => TrayActionDispatcher.InvokeContextMenu(item));
            menu.Items.Add(left);
            menu.Items.Add(dbl);
            menu.Items.Add(native);
            return menu;
        }

        private void BindToBtn_Click(object sender, RoutedEventArgs e)
        {
            var selected = GetSelectedVisible();
            if (selected.Count == 0) return;

            var menu = new ContextMenu();
            var ordered = new List<TrayFolder>(_controller.Folders);
            if (_defaultBindTarget != null && ordered.Remove(_defaultBindTarget))
                ordered.Insert(0, _defaultBindTarget);
            foreach (var f in ordered)
            {
                var folder = f;
                var mi = new MenuItem { IsChecked = ReferenceEquals(folder, _defaultBindTarget), Header = folder.Name, Icon = MakeGlyph("", 14) };
                mi.Click += (s, ev) => BindItems(selected, folder, string.Format("主界面批量收纳 ({0} 项)", selected.Count));
                menu.Items.Add(mi);
            }
            if (_controller.Folders.Count > 0)
                menu.Items.Add(new Separator());
            var nf = new MenuItem { Header = "新建文件夹…", Icon = MakeGlyph("", 14) };
            nf.Click += (s, ev) => PromptNewFolder(selected, false);
            menu.Items.Add(nf);
            ShowMenuBelowRight(menu, BindToBtn);
        }

        private void UnbindBtn_Click(object sender, RoutedEventArgs e)
        {
            UnbindItems(GetSelectedVisible());
        }

        private void RefreshBtn_Click(object sender, RoutedEventArgs e)
        {
            _controller.RefreshIcons(true);
        }

        private void FlyoutBtn_Click(object sender, RoutedEventArgs e)
        {
            if (_showFlyoutCallback != null)
                _showFlyoutCallback();
        }

        private void ResumeBtn_Click(object sender, RoutedEventArgs e)
        {
            _controller.SetPaused(false);
            RefreshUI();
        }

        // ---------------------------------------------------------------- external tray drag support

        public void ShowExternalDragActiveState(TrayIconItem draggedIcon, string sourceArea)
        {
            if (!IsVisible) return;
            DragBannerText.Text = string.Format("正在拖入 {0} — 松开在左侧文件夹上即可收纳",
                draggedIcon != null ? draggedIcon.AppDisplayName : "图标");
            DragBanner.Visibility = Visibility.Visible;
        }

        public void ClearExternalDragActiveState()
        {
            DragBanner.Visibility = Visibility.Collapsed;
            ClearExternalHighlight();
        }

        private void ClearExternalHighlight()
        {
            if (_externalHighlightNav != null)
            {
                SetDropHighlight(_externalHighlightNav, false);
                _externalHighlightNav = null;
            }
        }

        public void HighlightDropTargetAtScreenPoint(int screenX, int screenY)
        {
            if (!IsVisible || WindowState == WindowState.Minimized)
            {
                ClearExternalHighlight();
                return;
            }

            RadioButton hit = null;
            foreach (var parts in _folderNavs.Values)
            {
                if (IsElementHitByScreenPoint(parts.Nav, screenX, screenY))
                {
                    hit = parts.Nav;
                    break;
                }
            }

            if (hit == _externalHighlightNav) return;
            ClearExternalHighlight();
            if (hit != null)
            {
                SetDropHighlight(hit, true);
                _externalHighlightNav = hit;
            }
        }

        private static bool IsElementHitByScreenPoint(FrameworkElement el, int screenX, int screenY)
        {
            if (el == null || !el.IsVisible || el.ActualWidth <= 0 || el.ActualHeight <= 0)
                return false;
            try
            {
                Point tl = el.PointToScreen(new Point(0, 0));
                Point br = el.PointToScreen(new Point(el.ActualWidth, el.ActualHeight));
                return screenX >= tl.X && screenX <= br.X && screenY >= tl.Y && screenY <= br.Y;
            }
            catch
            {
                return false;
            }
        }

        public bool TryAcceptExternalDrop(int screenX, int screenY, TrayIconItem draggedIcon, string sourceArea)
        {
            ClearExternalDragActiveState();
            if (!IsVisible || WindowState == WindowState.Minimized || draggedIcon == null)
                return false;

            var hwnd = new WindowInteropHelper(this).Handle;
            NativeMethods.RECT rWin;
            if (hwnd == IntPtr.Zero || !NativeMethods.GetWindowRect(hwnd, out rWin) || !rWin.Contains(screenX, screenY))
                return false;

            TrayFolder targetFolder = null;
            foreach (var kv in _folderNavs)
            {
                var tf = kv.Value.Nav.Tag as TrayFolder;
                if (tf != null && IsElementHitByScreenPoint(kv.Value.Nav, screenX, screenY))
                {
                    targetFolder = tf;
                    break;
                }
            }

            if (targetFolder == null)
            {
                targetFolder = _selectedFolder
                    ?? _controller.Folders.FirstOrDefault()
                    ?? _controller.CreateFolder("默认文件夹");
            }

            _controller.BindIconsToFolder(new[] { draggedIcon }, targetFolder,
                string.Format("外部拖拽落入主界面文件夹 (来源: {0}, 坐标: {1},{2})", sourceArea, screenX, screenY));
            SwitchView("FOLDER", targetFolder);
            return true;
        }

        // ---------------------------------------------------------------- settings page handlers

        private void HideStrategyRadio_Click(object sender, RoutedEventArgs e)
        {
            var strategy = HideCompleteRadio.IsChecked == true
                ? HideStrategy.CompleteHide
                : HideStrategy.DemoteToOverflowOnly;
            _controller.SetHideStrategy(strategy);
        }

        private void PauseToggle_Click(object sender, RoutedEventArgs e)
        {
            _controller.SetPaused(PauseToggle.IsChecked == true);
            RefreshUI();
        }

        private void AutoOpenToggle_Click(object sender, RoutedEventArgs e)
        {
            _controller.Config.AutoOpenFlyoutOnExternalDrag = AutoOpenToggle.IsChecked == true;
            ConfigManager.SaveConfig(_controller.Config);
        }

        private void FlyoutAnimToggle_Click(object sender, RoutedEventArgs e)
        {
            _controller.Config.EnableFlyoutAnimations = FlyoutAnimToggle.IsChecked == true;
            ConfigManager.SaveConfig(_controller.Config);
        }

        private void ShowFolderNamesToggle_Click(object sender, RoutedEventArgs e)
        {
            _controller.Config.ShowFolderNames = ShowFolderNamesToggle.IsChecked == true;
            ConfigManager.SaveConfig(_controller.Config);
            // Make an already-visible flyout re-layout right away.
            FlyoutWindow.RaiseAppearanceChanged();
        }

        private void StartupToggle_Click(object sender, RoutedEventArgs e)
        {
            bool want = StartupToggle.IsChecked == true;
            try
            {
                StartupManager.SetEnabled(want);
            }
            catch (Exception ex)
            {
                ConfigManager.Log("ERROR", "设置开机启动失败: " + ex.Message);
            }
            StartupToggle.IsChecked = StartupManager.IsEnabled();
        }

        private void OpenConfigDirBtn_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = ConfigManager.AppDataDir,
                    UseShellExecute = true
                });
            }
            catch { }
        }

        private void ExitAndRestoreBtn_Click(object sender, RoutedEventArgs e)
        {
            AllowTrueClose = true;
            if (_exitApplicationCallback != null)
                _exitApplicationCallback();
        }

        private void CopyLogBtn_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!string.IsNullOrEmpty(LogTextBox.Text))
                    Clipboard.SetText(LogTextBox.Text);
            }
            catch { }
        }

        // ---------------------------------------------------------------- ContentDialog

        private void ShowDialog(string title, string body, string inputInitial, string primaryText, Func<string, bool> onConfirm)
        {
            if (DialogLayer.Visibility != Visibility.Visible)
                _dialogRestoreFocus = Keyboard.FocusedElement;

            _dialogVersion++;
            _dialogConfirm = onConfirm;
            DialogTitle.Text = title;
            DialogBody.Text = body ?? "";
            DialogBody.Visibility = string.IsNullOrEmpty(body) ? Visibility.Collapsed : Visibility.Visible;
            DialogPrimary.Content = primaryText;

            bool hasInput = inputInitial != null;
            DialogInput.Visibility = hasInput ? Visibility.Visible : Visibility.Collapsed;
            if (hasInput) DialogInput.Text = inputInitial;

            DialogLayer.Visibility = Visibility.Visible;
            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (hasInput)
                {
                    DialogInput.Focus();
                    DialogInput.SelectAll();
                }
                else
                {
                    DialogPrimary.Focus();
                }
            }), DispatcherPriority.Input);
        }

        private void CloseDialog()
        {
            if (DialogLayer.Visibility != Visibility.Visible) return;
            DialogLayer.Visibility = Visibility.Collapsed;
            _dialogConfirm = null;
            var restore = _dialogRestoreFocus as UIElement;
            _dialogRestoreFocus = null;
            if (restore != null && restore.IsVisible)
                restore.Focus();
        }

        private void ConfirmDialog()
        {
            var callback = _dialogConfirm;
            if (callback == null)
            {
                CloseDialog();
                return;
            }
            string text = DialogInput.Visibility == Visibility.Visible ? DialogInput.Text : null;
            int version = _dialogVersion;
            bool close = callback(text);
            // the callback may have opened another dialog; only close if it did not
            if (close && version == _dialogVersion)
                CloseDialog();
        }

        private void DialogPrimary_Click(object sender, RoutedEventArgs e)
        {
            ConfirmDialog();
        }

        private void DialogCancel_Click(object sender, RoutedEventArgs e)
        {
            CloseDialog();
        }

        private void DialogLayer_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                CloseDialog();
            }
            else if (e.Key == Key.Enter || e.Key == Key.Return)
            {
                if (Keyboard.FocusedElement == DialogCancel) return;
                e.Handled = true;
                ConfirmDialog();
            }
        }

        private void DialogLayer_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            // modal: swallow clicks on the smoke layer
            e.Handled = true;
        }

        // ---------------------------------------------------------------- window

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            if (!AllowTrueClose)
            {
                e.Cancel = true;
                CloseDialog();
                Hide();
                ConfigManager.Log("UI", "主界面已最小化至右下角专属托盘图标（后台收纳保持运行）");
            }
        }
    }
}
