using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

namespace TrayOrganizer.Core
{
    public class RegistryIconMeta
    {
        public RegistryIconMeta()
        {
            ExecutablePath = "";
            InitialTooltip = "";
            Publisher = "";
            IconGuid = "";
        }

        public ulong RegKey { get; set; }
        public string KeyName { get; set; }
        public string ExecutablePath { get; set; }
        public string InitialTooltip { get; set; }
        public string Publisher { get; set; }
        public uint Uid { get; set; }
        public string IconGuid { get; set; }
        public bool IsPromoted { get; set; }
        public byte[] IconSnapshot { get; set; }
    }

    public class TrayIconEngine
    {
        private uint _explorerPid;
        private ulong _vtable1;
        private ulong _vtable2;
        private readonly HashSet<IntPtr> _cachedRegions = new HashSet<IntPtr>();
        private readonly Dictionary<ulong, ImageSource> _iconCache = new Dictionary<ulong, ImageSource>();
        private readonly Dictionary<ulong, string> _base64IconCache = new Dictionary<ulong, string>();

        // ---- discovery state / performance tuning ----
        private const int EntrySize = 280;                       // bytes parsed per tray entry (vtable pair + payload)
        private const int ScanChunkBytes = 1024 * 1024;          // fixed chunk read size while scanning regions
        private const int ScanOverlapBytes = EntrySize - 8;      // so entries straddling a chunk boundary are still seen
        private const long MaxRegionBytes = 64L * 1024 * 1024;
        private const long RegionRescanIntervalMs = 2500;        // re-scan known regions for new icons (about every 3rd 1 s tick)
        private const long FullScanIntervalMs = 30000;           // safety-net full scan of all private regions
        private const long EmptyFullScanRetryMs = 3000;          // when nothing was ever found, do not full-scan every tick
        private const long RegistryFullRefreshMs = 10000;
        private const long PerfLogIntervalMs = 60000;

        private static readonly Stopwatch _clock = Stopwatch.StartNew();
        private static readonly int _myPid = Process.GetCurrentProcess().Id;

        private readonly object _discoverLock = new object();
        private readonly byte[] _scanBuf = new byte[ScanChunkBytes + ScanOverlapBytes + 16];
        private readonly byte[] _entryBuf = new byte[EntrySize];
        private readonly byte[] _hdrBuf = new byte[24];
        private readonly byte[] _strBuf = new byte[1024];
        private readonly byte[] _guidBuf = new byte[16];
        private readonly List<KeyValuePair<IntPtr, long>> _regionList = new List<KeyValuePair<IntPtr, long>>();
        private readonly List<ulong> _knownEntries = new List<ulong>();
        private bool _needFullScan = true;
        private long _lastScanMs = long.MinValue / 2;
        private long _lastFullScanMs = long.MinValue / 2;

        // registry metadata cache
        private Dictionary<ulong, RegistryIconMeta> _regMetaCache = new Dictionary<ulong, RegistryIconMeta>();
        private string[] _regNames = new string[0];
        private bool _regLoaded;
        private long _regFullReadMs;

        // PERF self-measurement
        private long _perfWindowStartMs;
        private long _perfTicks;
        private int _perfCalls;
        private int _perfFullScans;
        private int _perfRegionScans;
        private int _perfFastTicks;
        private long _perfBytes;

        private static readonly Dictionary<string, KeyValuePair<string, long>> _fileDescCache =
            new Dictionary<string, KeyValuePair<string, long>>(StringComparer.OrdinalIgnoreCase);
        private const long FileDescTtlMs = 5 * 60 * 1000;

        private static readonly Dictionary<string, string> _knownFolderMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "{6D809377-6AF0-444B-8957-A3773F02200E}", Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles) },
            { "{7C5A40EF-A0FB-44FC-874A-C0F2E0B9FA8E}", Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) },
            { "{7C5A40EF-A0FB-4BFC-874A-C0F2E0B9FA8E}", Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) },
            { "{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}", Environment.GetFolderPath(Environment.SpecialFolder.System) },
            { "{F38BF404-1D43-42F2-9305-67DE0B28FC23}", Environment.GetFolderPath(Environment.SpecialFolder.Windows) }
        };

        public static string NormalizeExecutablePath(string rawPath)
        {
            if (string.IsNullOrWhiteSpace(rawPath)) return "";
            string res = rawPath.Trim();
            foreach (var kv in _knownFolderMap)
            {
                if (res.StartsWith(kv.Key, StringComparison.OrdinalIgnoreCase))
                {
                    res = kv.Value + res.Substring(kv.Key.Length);
                    break;
                }
            }
            return res;
        }

        private static string GetFileDescriptionCached(string normalizedExePath)
        {
            if (string.IsNullOrEmpty(normalizedExePath)) return null;

            long now = _clock.ElapsedMilliseconds;
            lock (_fileDescCache)
            {
                KeyValuePair<string, long> hit;
                if (_fileDescCache.TryGetValue(normalizedExePath, out hit) && now - hit.Value < FileDescTtlMs)
                    return hit.Key;
            }

            string value = null;
            try
            {
                if (File.Exists(normalizedExePath))
                {
                    var vi = FileVersionInfo.GetVersionInfo(normalizedExePath);
                    if (!string.IsNullOrWhiteSpace(vi.FileDescription) &&
                        !vi.FileDescription.Equals("Application", StringComparison.OrdinalIgnoreCase))
                    {
                        value = vi.FileDescription.Trim();
                    }
                    else if (!string.IsNullOrWhiteSpace(vi.ProductName))
                    {
                        value = vi.ProductName.Trim();
                    }
                }
            }
            catch { }

            lock (_fileDescCache)
            {
                _fileDescCache[normalizedExePath] = new KeyValuePair<string, long>(value, now);
            }
            return value;
        }

        public static string ResolveFriendlyAppName(string normalizedExePath, string initialTooltip, string liveTooltip, string publisher)
        {
            string fileName = Path.GetFileNameWithoutExtension(normalizedExePath ?? "");
            if (string.Equals(fileName, "TrafficMonitor", StringComparison.OrdinalIgnoreCase))
                return "流量监控 (TrafficMonitor)";
            if (string.Equals(fileName, "Token Monitor", StringComparison.OrdinalIgnoreCase))
                return "Token 监控 (Token Monitor)";
            if (string.Equals(fileName, "PadMouse", StringComparison.OrdinalIgnoreCase))
                return "PadMouse (手柄鼠标)";

            if (string.Equals(fileName, "pythonw", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(fileName, "python", StringComparison.OrdinalIgnoreCase))
            {
                string tip = !string.IsNullOrWhiteSpace(initialTooltip) ? initialTooltip : liveTooltip;
                if (!string.IsNullOrWhiteSpace(tip))
                {
                    if (tip.IndexOf("PadMouse", StringComparison.OrdinalIgnoreCase) >= 0)
                        return "PadMouse (Python)";
                    string prefix = ConfigManager.ExtractTooltipPrefix(tip);
                    if (!string.IsNullOrWhiteSpace(prefix))
                        return string.Format("{0} ({1})", prefix, fileName);
                }
            }

            // Disk lookup (File.Exists + FileVersionInfo) is cached per executable path: it does not depend on the tooltip.
            string fromFile = GetFileDescriptionCached(normalizedExePath);
            if (fromFile != null) return fromFile;

            if (!string.IsNullOrWhiteSpace(initialTooltip))
            {
                string p = ConfigManager.ExtractTooltipPrefix(initialTooltip);
                if (!string.IsNullOrWhiteSpace(p)) return p;
            }

            return !string.IsNullOrWhiteSpace(fileName) ? fileName : "托盘应用";
        }

        /// <summary>Full, uncached read of all NotifyIconSettings subkeys (including IconSnapshot blobs).</summary>
        public Dictionary<ulong, RegistryIconMeta> ReadRegistryMetadata()
        {
            var dict = new Dictionary<ulong, RegistryIconMeta>();
            try
            {
                using (var root = Registry.CurrentUser.OpenSubKey(@"Control Panel\NotifyIconSettings", false))
                {
                    if (root == null) return dict;
                    dict = ReadRegistryMetadataCore(root, root.GetSubKeyNames(), true);
                }
            }
            catch { }
            return dict;
        }

        private static Dictionary<ulong, RegistryIconMeta> ReadRegistryMetadataCore(RegistryKey root, string[] names, bool withSnapshot)
        {
            var dict = new Dictionary<ulong, RegistryIconMeta>();
            foreach (string subName in names)
            {
                ulong regKey;
                if (!ulong.TryParse(subName, out regKey)) continue;
                try
                {
                    using (var sub = root.OpenSubKey(subName, false))
                    {
                        if (sub == null) continue;

                        object exeObj = sub.GetValue("ExecutablePath");
                        object tipObj = sub.GetValue("InitialTooltip");
                        object pubObj = sub.GetValue("Publisher");
                        object guidObj = sub.GetValue("IconGuid");
                        object promObj = sub.GetValue("IsPromoted");
                        object uidObj = sub.GetValue("UID");

                        var meta = new RegistryIconMeta
                        {
                            RegKey = regKey,
                            KeyName = subName,
                            ExecutablePath = exeObj != null ? exeObj.ToString() : "",
                            InitialTooltip = tipObj != null ? tipObj.ToString() : "",
                            Publisher = pubObj != null ? pubObj.ToString() : "",
                            IconGuid = guidObj != null ? guidObj.ToString() : "",
                            IsPromoted = promObj != null && Convert.ToInt32(promObj) != 0,
                            Uid = uidObj != null ? Convert.ToUInt32(uidObj) : 0u,
                            IconSnapshot = withSnapshot ? sub.GetValue("IconSnapshot") as byte[] : null
                        };
                        dict[regKey] = meta;
                    }
                }
                catch { }
            }
            return dict;
        }

        private static bool SameNames(string[] a, string[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
            {
                if (!string.Equals(a[i], b[i], StringComparison.Ordinal)) return false;
            }
            return true;
        }

        /// <summary>
        /// Cached registry metadata: the subkey names are listed every call (cheap); the full per-key read happens only when
        /// the names change or every RegistryFullRefreshMs. Between full reads only IsPromoted is refreshed (the controller
        /// relies on it being current). IconSnapshot blobs are never loaded here (see ReadIconSnapshot).
        /// keysChanged is true when the set of subkeys changed since the previous call.
        /// </summary>
        private Dictionary<ulong, RegistryIconMeta> GetRegistryMetadataCached(out bool keysChanged)
        {
            keysChanged = false;
            long now = _clock.ElapsedMilliseconds;
            try
            {
                using (var root = Registry.CurrentUser.OpenSubKey(@"Control Panel\NotifyIconSettings", false))
                {
                    if (root == null)
                    {
                        if (_regMetaCache.Count > 0) keysChanged = true;
                        _regMetaCache = new Dictionary<ulong, RegistryIconMeta>();
                        _regNames = new string[0];
                        _regLoaded = true;
                        return _regMetaCache;
                    }

                    string[] names = root.GetSubKeyNames();
                    bool namesChanged = !_regLoaded || !SameNames(names, _regNames);

                    if (namesChanged || now - _regFullReadMs >= RegistryFullRefreshMs)
                    {
                        _regMetaCache = ReadRegistryMetadataCore(root, names, false);
                        if (namesChanged)
                        {
                            keysChanged = _regLoaded;
                            // A brand-new key may still be getting filled in by the shell: re-read again shortly.
                            _regFullReadMs = now - (RegistryFullRefreshMs - 2000);
                        }
                        else
                        {
                            _regFullReadMs = now;
                        }
                        _regNames = names;
                        _regLoaded = true;
                    }
                    else
                    {
                        foreach (var meta in _regMetaCache.Values)
                        {
                            try
                            {
                                using (var sub = root.OpenSubKey(meta.KeyName, false))
                                {
                                    if (sub == null) continue;
                                    object promObj = sub.GetValue("IsPromoted");
                                    meta.IsPromoted = promObj != null && Convert.ToInt32(promObj) != 0;
                                }
                            }
                            catch { }
                        }
                    }
                }
            }
            catch { }
            return _regMetaCache;
        }

        private static byte[] ReadIconSnapshot(ulong regKey)
        {
            if (regKey == 0) return null;
            try
            {
                using (var sub = Registry.CurrentUser.OpenSubKey(@"Control Panel\NotifyIconSettings\" + regKey.ToString(), false))
                {
                    if (sub == null) return null;
                    return sub.GetValue("IconSnapshot") as byte[];
                }
            }
            catch
            {
                return null;
            }
        }

        private bool EnsureExplorerAndVtables(Dictionary<ulong, RegistryIconMeta> regMeta)
        {
            IntPtr shellTray = NativeMethods.FindWindow("Shell_TrayWnd", IntPtr.Zero);
            if (shellTray == IntPtr.Zero) return false;

            uint expPid;
            NativeMethods.GetWindowThreadProcessId(shellTray, out expPid);
            if (expPid == 0) return false;

            if (expPid == _explorerPid && _vtable1 != 0 && _vtable2 != 0)
                return true;

            _explorerPid = expPid;
            _vtable1 = 0;
            _vtable2 = 0;
            _cachedRegions.Clear();
            _knownEntries.Clear();
            _needFullScan = true;

            ulong taskbarBase = 0;
            ulong taskbarEnd = 0;
            try
            {
                using (var proc = Process.GetProcessById((int)expPid))
                {
                    foreach (ProcessModule mod in proc.Modules)
                    {
                        if (string.Equals(mod.ModuleName, "Taskbar.dll", StringComparison.OrdinalIgnoreCase))
                        {
                            taskbarBase = (ulong)mod.BaseAddress.ToInt64();
                            taskbarEnd = taskbarBase + (ulong)mod.ModuleMemorySize;
                            break;
                        }
                    }
                }
            }
            catch { }

            if (taskbarBase != 0)
            {
                // Known RVA on Windows 11 26300.9711
                _vtable1 = taskbarBase + 0x1C8088UL;
                _vtable2 = taskbarBase + 0x1C8050UL;
            }

            // Verify or dynamically discover vtables in explorer.exe heap
            IntPtr hProc = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_INFORMATION | NativeMethods.PROCESS_VM_READ, false, expPid);
            if (hProc == IntPtr.Zero) return false;

            try
            {
                ulong addr = 0;
                NativeMethods.MEMORY_BASIC_INFORMATION mbi;
                uint mbiSize = (uint)Marshal.SizeOf(typeof(NativeMethods.MEMORY_BASIC_INFORMATION));
                while (NativeMethods.VirtualQueryEx(hProc, (IntPtr)addr, out mbi, mbiSize) != 0)
                {
                    long regSize = mbi.RegionSize.ToInt64();
                    if (mbi.State == NativeMethods.MEM_COMMIT &&
                        mbi.Type == NativeMethods.MEM_PRIVATE &&
                        (mbi.Protect == NativeMethods.PAGE_READWRITE || mbi.Protect == NativeMethods.PAGE_READONLY) &&
                        regSize > 0 && regSize <= MaxRegionBytes)
                    {
                        ulong regBase = (ulong)mbi.BaseAddress.ToInt64();
                        for (long off = 0; off < regSize; off += ScanChunkBytes)
                        {
                            int n = ReadScanChunk(hProc, regBase, regSize, off);
                            if (n < EntrySize) continue;
                            int limit = Math.Min(n - EntrySize, ScanChunkBytes - 8);
                            for (int i = 0; i <= limit; i += 8)
                            {
                                ulong candVt1 = BitConverter.ToUInt64(_scanBuf, i);
                                ulong candVt2 = BitConverter.ToUInt64(_scanBuf, i + 8);
                                if (_vtable1 != 0 && candVt1 == _vtable1 && candVt2 == _vtable2)
                                {
                                    return true;
                                }

                                // Dynamic fallback discovery using RegistryKeyId at baseOff + 96
                                if (taskbarBase != 0 && candVt1 > taskbarBase && candVt1 < taskbarEnd &&
                                    candVt2 > taskbarBase && candVt2 < taskbarEnd && candVt1 > candVt2 && (candVt1 - candVt2) <= 0x80)
                                {
                                    int baseOff = i + 32;
                                    ulong regKey = BitConverter.ToUInt64(_scanBuf, baseOff + 96);
                                    ulong hwnd = BitConverter.ToUInt64(_scanBuf, baseOff);
                                    if (regKey != 0 && regMeta.ContainsKey(regKey) && hwnd != 0 && NativeMethods.IsWindow((IntPtr)hwnd))
                                    {
                                        _vtable1 = candVt1;
                                        _vtable2 = candVt2;
                                        ConfigManager.Log("ENGINE", string.Format("已动态定位 Taskbar.dll NotifyIcon vtable: 0x{0:X}, 0x{1:X}", _vtable1, _vtable2));
                                        return true;
                                    }
                                }
                            }
                        }
                    }

                    ulong next = (ulong)mbi.BaseAddress.ToInt64() + (ulong)regSize;
                    if (next <= addr) break;
                    addr = next;
                }
            }
            finally
            {
                NativeMethods.CloseHandle(hProc);
            }

            return _vtable1 != 0;
        }

        private bool ReadMem(IntPtr hProc, ulong addr, byte[] buf, int len, out int read)
        {
            IntPtr r;
            bool ok = NativeMethods.ReadProcessMemory(hProc, (IntPtr)(long)addr, buf, len, out r);
            read = r.ToInt32();
            _perfBytes += read;
            return ok;
        }

        /// <summary>
        /// Reads up to ScanChunkBytes + ScanOverlapBytes bytes of the region starting at regionBase + offset into the pooled
        /// scan buffer. The overlap lets entries that straddle a chunk boundary be parsed by the earlier chunk.
        /// Returns the number of bytes read (0 on failure / nothing left to scan).
        /// </summary>
        private int ReadScanChunk(IntPtr hProc, ulong regionBase, long regionSize, long offset)
        {
            long remaining = regionSize - offset;
            if (remaining < EntrySize) return 0;
            int want = (int)Math.Min(remaining, (long)(ScanChunkBytes + ScanOverlapBytes));
            int read;
            if (!ReadMem(hProc, regionBase + (ulong)offset, _scanBuf, want, out read))
                return 0;
            return read;
        }

        private string ReadWString(IntPtr hProc, ulong ptr, ulong len)
        {
            if (ptr < 0x10000 || len == 0 || len > 512) return "";
            int bytes = (int)len * 2;
            int read;
            if (ReadMem(hProc, ptr, _strBuf, bytes, out read))
                return Encoding.Unicode.GetString(_strBuf, 0, bytes);
            return "";
        }

        private string ReadHString(IntPtr hProc, ulong hstr)
        {
            if (hstr < 0x10000) return "";
            int read;
            if (!ReadMem(hProc, hstr, _hdrBuf, 24, out read))
                return "";
            uint len = BitConverter.ToUInt32(_hdrBuf, 4);
            ulong ptr = BitConverter.ToUInt64(_hdrBuf, 16);
            return ReadWString(hProc, ptr, len);
        }

        private ImageSource ExtractIconImage(ulong regKey, ulong hIconVal, string normalizedExePath, out string pngBase64)
        {
            pngBase64 = "";
            ImageSource cachedImg;
            if (_iconCache.TryGetValue(regKey, out cachedImg) && cachedImg != null)
            {
                _base64IconCache.TryGetValue(regKey, out pngBase64);
                return cachedImg;
            }

            ImageSource result = null;

            // 1. Try HICON
            if (hIconVal != 0)
            {
                try
                {
                    IntPtr hCopy = NativeMethods.CopyIcon((IntPtr)hIconVal);
                    IntPtr hToUse = hCopy != IntPtr.Zero ? hCopy : (IntPtr)hIconVal;
                    var bmp = Imaging.CreateBitmapSourceFromHIcon(hToUse, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                    bmp.Freeze();
                    result = bmp;
                    if (hCopy != IntPtr.Zero)
                        NativeMethods.DestroyIcon(hCopy);
                }
                catch { }
            }

            // 2. Try Registry IconSnapshot (PNG bytes)
            // (loaded lazily: only when this regKey has no cached icon yet and the HICON could not be used)
            byte[] snapshotBytes = result == null ? ReadIconSnapshot(regKey) : null;
            if (result == null && snapshotBytes != null && snapshotBytes.Length > 16)
            {
                try
                {
                    using (var ms = new MemoryStream(snapshotBytes))
                    {
                        var bi = new BitmapImage();
                        bi.BeginInit();
                        bi.CacheOption = BitmapCacheOption.OnLoad;
                        bi.StreamSource = ms;
                        bi.EndInit();
                        bi.Freeze();
                        result = bi;
                    }
                }
                catch { }
            }

            // 3. Try Associated Icon of executable
            if (result == null && !string.IsNullOrEmpty(normalizedExePath) && File.Exists(normalizedExePath))
            {
                try
                {
                    using (var ico = System.Drawing.Icon.ExtractAssociatedIcon(normalizedExePath))
                    {
                        if (ico != null)
                        {
                            var bmp = Imaging.CreateBitmapSourceFromHIcon(ico.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                            bmp.Freeze();
                            result = bmp;
                        }
                    }
                }
                catch { }
            }

            var bs = result as BitmapSource;
            if (bs != null)
            {
                try
                {
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(bs));
                    using (var ms = new MemoryStream())
                    {
                        encoder.Save(ms);
                        pngBase64 = Convert.ToBase64String(ms.ToArray());
                    }
                }
                catch { }
            }

            if (result != null && regKey != 0)
            {
                _iconCache[regKey] = result;
                if (!string.IsNullOrEmpty(pngBase64))
                    _base64IconCache[regKey] = pngBase64;
            }

            return result;
        }

        public static ImageSource ImageFromBase64(string base64)
        {
            if (string.IsNullOrWhiteSpace(base64)) return null;
            try
            {
                byte[] bytes = Convert.FromBase64String(base64);
                using (var ms = new MemoryStream(bytes))
                {
                    var bi = new BitmapImage();
                    bi.BeginInit();
                    bi.CacheOption = BitmapCacheOption.OnLoad;
                    bi.StreamSource = ms;
                    bi.EndInit();
                    bi.Freeze();
                    return bi;
                }
            }
            catch
            {
                return null;
            }
        }

        public List<TrayIconItem> DiscoverTrayIcons(bool forceFullScan = false)
        {
            lock (_discoverLock)
            {
                long t0 = Stopwatch.GetTimestamp();
                try
                {
                    return DiscoverCore(forceFullScan);
                }
                finally
                {
                    RecordPerf(Stopwatch.GetTimestamp() - t0);
                }
            }
        }

        private void RecordPerf(long ticks)
        {
            long now = _clock.ElapsedMilliseconds;
            if (_perfWindowStartMs == 0) _perfWindowStartMs = now;

            _perfTicks += ticks;
            _perfCalls++;

            long windowMs = now - _perfWindowStartMs;
            if (windowMs < PerfLogIntervalMs) return;

            double avgMs = _perfCalls > 0 ? (_perfTicks * 1000.0 / Stopwatch.Frequency) / _perfCalls : 0.0;
            ConfigManager.Log("PERF", string.Format(
                "DiscoverTrayIcons 平均 {0:F2} ms (调用 {1} 次, 快速路径 {2} 次), 完整扫描 {3} 次, 区域重扫 {4} 次, 近 {5:F0} 秒读取 explorer 内存 {6:F2} MB",
                avgMs, _perfCalls, _perfFastTicks, _perfFullScans, _perfRegionScans, windowMs / 1000.0, _perfBytes / (1024.0 * 1024.0)));

            _perfWindowStartMs = now;
            _perfTicks = 0;
            _perfCalls = 0;
            _perfFastTicks = 0;
            _perfFullScans = 0;
            _perfRegionScans = 0;
            _perfBytes = 0;
        }

        private List<TrayIconItem> DiscoverCore(bool forceFullScan)
        {
            bool keysChanged;
            var regMeta = GetRegistryMetadataCached(out keysChanged);
            var discovered = new Dictionary<ulong, TrayIconItem>();

            if (!EnsureExplorerAndVtables(regMeta))
                return new List<TrayIconItem>();

            if (keysChanged) _needFullScan = true;

            long now = _clock.ElapsedMilliseconds;
            bool full = forceFullScan || _needFullScan ||
                        (now - _lastFullScanMs >= FullScanIntervalMs) ||
                        (_cachedRegions.Count == 0 && now - _lastFullScanMs >= EmptyFullScanRetryMs);
            bool regionDue = now - _lastScanMs >= RegionRescanIntervalMs;

            IntPtr hProc = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_INFORMATION | NativeMethods.PROCESS_VM_READ, false, _explorerPid);
            if (hProc == IntPtr.Zero) return new List<TrayIconItem>();

            try
            {
                if (!full && !regionDue)
                {
                    // Normal tick: re-read only the exact entries remembered from the last scan.
                    int fast = TryFastPath(hProc, regMeta, discovered);
                    if (fast == 0)
                    {
                        _perfFastTicks++;
                        return discovered.Values.ToList();
                    }
                    discovered.Clear();
                    if (fast == 2) full = true;
                }

                ScanMemory(hProc, regMeta, discovered, full);
            }
            finally
            {
                NativeMethods.CloseHandle(hProc);
            }

            return discovered.Values.ToList();
        }

        /// <summary>0 = all remembered entries valid, 1 = a tracked HWND died (region rescan), 2 = entry no longer valid (full rescan).</summary>
        private int TryFastPath(IntPtr hProc, Dictionary<ulong, RegistryIconMeta> regMeta, Dictionary<ulong, TrayIconItem> discovered)
        {
            for (int k = 0; k < _knownEntries.Count; k++)
            {
                int read;
                if (!ReadMem(hProc, _knownEntries[k], _entryBuf, EntrySize, out read) || read < EntrySize)
                    return 2;
                if (BitConverter.ToUInt64(_entryBuf, 0) != _vtable1 || BitConverter.ToUInt64(_entryBuf, 8) != _vtable2)
                    return 2;

                TrayIconItem item;
                ulong key;
                int code = ParseEntry(hProc, _entryBuf, 0, regMeta, out item, out key);
                if (code == 0) return 1;
                if (code == 2) discovered[key] = item;
            }
            return 0;
        }

        private void ScanMemory(IntPtr hProc, Dictionary<ulong, RegistryIconMeta> regMeta, Dictionary<ulong, TrayIconItem> discovered, bool full)
        {
            uint mbiSize = (uint)Marshal.SizeOf(typeof(NativeMethods.MEMORY_BASIC_INFORMATION));
            _regionList.Clear();

            if (!full)
            {
                foreach (var baseAddr in _cachedRegions)
                {
                    NativeMethods.MEMORY_BASIC_INFORMATION mbi;
                    if (NativeMethods.VirtualQueryEx(hProc, baseAddr, out mbi, mbiSize) != 0 &&
                        mbi.State == NativeMethods.MEM_COMMIT)
                    {
                        long s = mbi.RegionSize.ToInt64();
                        if (s > 0 && s <= MaxRegionBytes)
                            _regionList.Add(new KeyValuePair<IntPtr, long>(mbi.BaseAddress, s));
                    }
                }
                if (_regionList.Count == 0) full = true;
            }

            long now = _clock.ElapsedMilliseconds;
            if (full)
            {
                _regionList.Clear();
                _cachedRegions.Clear();
                ulong addr = 0;
                NativeMethods.MEMORY_BASIC_INFORMATION mbi;
                while (NativeMethods.VirtualQueryEx(hProc, (IntPtr)addr, out mbi, mbiSize) != 0)
                {
                    long regSize = mbi.RegionSize.ToInt64();
                    if (mbi.State == NativeMethods.MEM_COMMIT &&
                        mbi.Type == NativeMethods.MEM_PRIVATE &&
                        (mbi.Protect == NativeMethods.PAGE_READWRITE || mbi.Protect == NativeMethods.PAGE_READONLY) &&
                        regSize > 0 && regSize <= MaxRegionBytes)
                    {
                        _regionList.Add(new KeyValuePair<IntPtr, long>(mbi.BaseAddress, regSize));
                    }
                    ulong next = (ulong)mbi.BaseAddress.ToInt64() + (ulong)regSize;
                    if (next <= addr) break;
                    addr = next;
                }
                _perfFullScans++;
                _lastFullScanMs = now;
                _needFullScan = false;
            }
            else
            {
                _perfRegionScans++;
            }
            _lastScanMs = now;

            _knownEntries.Clear();

            for (int r = 0; r < _regionList.Count; r++)
            {
                IntPtr regionBaseAddr = _regionList[r].Key;
                long regSize = _regionList[r].Value;
                ulong regBase = (ulong)regionBaseAddr.ToInt64();

                for (long off = 0; off < regSize; off += ScanChunkBytes)
                {
                    int n = ReadScanChunk(hProc, regBase, regSize, off);
                    if (n < EntrySize) continue;

                    // Entries starting in [0, ScanChunkBytes) belong to this chunk; the overlap tail only completes them.
                    int limit = Math.Min(n - EntrySize, ScanChunkBytes - 8);
                    for (int i = 0; i <= limit; i += 8)
                    {
                        if (BitConverter.ToUInt64(_scanBuf, i) != _vtable1 || BitConverter.ToUInt64(_scanBuf, i + 8) != _vtable2)
                            continue;

                        _cachedRegions.Add(regionBaseAddr);

                        TrayIconItem item;
                        ulong key;
                        if (ParseEntry(hProc, _scanBuf, i, regMeta, out item, out key) == 2)
                        {
                            discovered[key] = item;
                            _knownEntries.Add(regBase + (ulong)off + (ulong)i);
                        }
                    }
                }
            }
        }

        /// <summary>Parses one tray entry (vtable pair at buf[i]). Returns 0 = dead/invalid HWND, 1 = skipped on purpose, 2 = item built.</summary>
        private int ParseEntry(IntPtr hProc, byte[] buf, int i, Dictionary<ulong, RegistryIconMeta> regMeta, out TrayIconItem item, out ulong keyToStore)
        {
            item = null;
            keyToStore = 0;

            int baseOff = i + 32;
            ulong hwnd = BitConverter.ToUInt64(buf, baseOff);
            if (hwnd == 0 || !NativeMethods.IsWindow((IntPtr)hwnd))
                return 0;

            uint ownerPid;
            NativeMethods.GetWindowThreadProcessId((IntPtr)hwnd, out ownerPid);
            if (ownerPid == (uint)_myPid)
                return 1; // Skip our own TrayOrganizer tray icon

            uint uid = BitConverter.ToUInt32(buf, baseOff + 8);
            Array.Copy(buf, baseOff + 12, _guidBuf, 0, 16);
            Guid guid = new Guid(_guidBuf);

            ulong strPtr = BitConverter.ToUInt64(buf, baseOff + 32);
            ulong strLen = BitConverter.ToUInt64(buf, baseOff + 48);
            ulong strCap = BitConverter.ToUInt64(buf, baseOff + 56);
            string rawExePath = "";
            if (strCap >= 8)
                rawExePath = ReadWString(hProc, strPtr, strLen);
            else if (strLen > 0 && strLen < 8)
                rawExePath = Encoding.Unicode.GetString(buf, baseOff + 32, (int)strLen * 2);

            uint cbMsg = BitConverter.ToUInt32(buf, baseOff + 72);
            uint ver = BitConverter.ToUInt32(buf, baseOff + 76);
            ulong hstrTip = BitConverter.ToUInt64(buf, baseOff + 80);
            string liveTip = ReadHString(hProc, hstrTip);
            ulong regKey = BitConverter.ToUInt64(buf, baseOff + 96);
            bool nativeVisible = BitConverter.ToUInt64(buf, baseOff + 160) != 0;
            ulong hIconVal = BitConverter.ToUInt64(buf, baseOff + 232);

            RegistryIconMeta meta;
            regMeta.TryGetValue(regKey, out meta);
            if (string.IsNullOrEmpty(rawExePath) && meta != null)
                rawExePath = meta.ExecutablePath;

            string normExe = NormalizeExecutablePath(rawExePath);
            string exeName = Path.GetFileName(normExe);

            // Skip internal Explorer hidden placeholder icons that have no tooltip and belong to explorer.exe
            if (string.Equals(exeName, "explorer.exe", StringComparison.OrdinalIgnoreCase) &&
                string.IsNullOrWhiteSpace(liveTip) &&
                (meta == null || string.IsNullOrWhiteSpace(meta.InitialTooltip)))
            {
                return 1;
            }

            string b64;
            var img = ExtractIconImage(regKey, hIconVal, normExe, out b64);
            string friendlyName = ResolveFriendlyAppName(normExe, meta != null ? meta.InitialTooltip : "", liveTip, meta != null ? meta.Publisher : "");

            item = new TrayIconItem
            {
                RegKey = regKey,
                Hwnd = hwnd,
                Uid = uid,
                GuidItem = guid,
                CallbackMessage = cbMsg,
                Version = ver,
                HIcon = hIconVal,
                OwnerPid = ownerPid,
                RawExePath = rawExePath,
                NormalizedExePath = normExe,
                ExeFileName = exeName,
                AppDisplayName = friendlyName,
                InitialTooltip = meta != null ? meta.InitialTooltip : "",
                Publisher = meta != null ? meta.Publisher : "",
                ToolTip = liveTip,
                IsPromotedInRegistry = meta != null && meta.IsPromoted,
                NativeVisibleInTray = nativeVisible,
                IsOnline = true,
                IconSource = img,
                CachedIconPngBase64 = b64
            };

            keyToStore = regKey != 0 ? regKey : ((hwnd << 16) ^ uid);
            return 2;
        }

        public static bool QueryIconScreenRect(TrayIconItem item, out NativeMethods.RECT rect)
        {
            rect = default(NativeMethods.RECT);
            if (item == null || !item.IsOnline) return false;

            var nii = new NativeMethods.NOTIFYICONIDENTIFIER
            {
                cbSize = (uint)Marshal.SizeOf(typeof(NativeMethods.NOTIFYICONIDENTIFIER))
            };

            if (item.GuidItem != Guid.Empty)
            {
                nii.guidItem = item.GuidItem;
            }
            else
            {
                nii.hWnd = (IntPtr)item.Hwnd;
                nii.uID = item.Uid;
            }

            int hr = NativeMethods.Shell_NotifyIconGetRect(ref nii, out rect);
            if (hr == 0 && rect.Width > 0 && rect.Height > 0)
            {
                item.LastBounds = rect;
                return true;
            }
            return false;
        }

        public TrayIconItem HitTestExternalTrayIcon(int screenX, int screenY, IList<TrayIconItem> currentIcons, out string sourceArea)
        {
            sourceArea = null;
            IntPtr trayWnd = NativeMethods.FindWindow("Shell_TrayWnd", IntPtr.Zero);
            IntPtr ovWnd = NativeMethods.FindWindow("TopLevelWindowForOverflowXamlIsland", IntPtr.Zero);

            NativeMethods.RECT rTray;
            NativeMethods.RECT rOv;
            bool inTray = trayWnd != IntPtr.Zero && NativeMethods.GetWindowRect(trayWnd, out rTray) && rTray.Contains(screenX, screenY);
            bool inOv = ovWnd != IntPtr.Zero && NativeMethods.IsWindowVisible(ovWnd) && NativeMethods.GetWindowRect(ovWnd, out rOv) && rOv.Contains(screenX, screenY);

            if (!inTray && !inOv)
                return null;

            sourceArea = inOv ? "隐藏图标面板 (^)" : "任务栏常驻托盘";

            var candidates = currentIcons ?? DiscoverTrayIcons(false);
            foreach (var icon in candidates)
            {
                if (!icon.IsOnline || !icon.NativeVisibleInTray)
                    continue;

                NativeMethods.RECT rIcon;
                if (QueryIconScreenRect(icon, out rIcon) && rIcon.Contains(screenX, screenY))
                {
                    return icon;
                }
            }

            // On newer Windows 11 builds Shell_NotifyIconGetRect returns the chevron button's rect for every icon
            // inside the ^ overflow panel, so a rect hit test can never match there. Fall back to UI Automation:
            // the panel exposes one named Button per icon (its name is the icon tooltip).
            if (inOv)
            {
                var viaUia = HitTestOverflowViaAutomation(ovWnd, screenX, screenY, candidates);
                if (viaUia != null) return viaUia;
            }

            return null;
        }

        private static string NormalizeName(string s)
        {
            return (s ?? "").Trim();
        }

        private static TrayIconItem HitTestOverflowViaAutomation(IntPtr ovWnd, int screenX, int screenY, IEnumerable<TrayIconItem> candidates)
        {
            try
            {
                var root = System.Windows.Automation.AutomationElement.FromHandle(ovWnd);
                if (root == null) return null;

                var buttonCond = new System.Windows.Automation.PropertyCondition(
                    System.Windows.Automation.AutomationElement.ControlTypeProperty,
                    System.Windows.Automation.ControlType.Button);
                var buttons = root.FindAll(System.Windows.Automation.TreeScope.Descendants, buttonCond);

                string bestName = null;
                double bestArea = double.MaxValue;
                foreach (System.Windows.Automation.AutomationElement b in buttons)
                {
                    System.Windows.Rect r = b.Current.BoundingRectangle;
                    if (r.IsEmpty || !r.Contains(screenX, screenY)) continue;
                    double area = r.Width * r.Height;
                    if (area < bestArea)
                    {
                        bestArea = area;
                        bestName = NormalizeName(b.Current.Name);
                    }
                }

                if (string.IsNullOrEmpty(bestName)) return null;

                foreach (var icon in candidates)
                {
                    if (!icon.IsOnline || !icon.NativeVisibleInTray) continue;
                    string tip = NormalizeName(icon.ToolTip);
                    string app = NormalizeName(icon.AppDisplayName);
                    if ((tip.Length > 0 && (bestName == tip || bestName.Contains(tip) || tip.Contains(bestName))) ||
                        (app.Length > 0 && bestName == app))
                        return icon;
                }
            }
            catch (Exception ex)
            {
                ConfigManager.Log("DRAG_HIT", "溢出面板 UIA 命中测试失败: " + ex.Message);
            }
            return null;
        }
    }
}
