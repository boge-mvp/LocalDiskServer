using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;

internal static class PnpmBackend
{
    private const int PROTOCOL_VERSION = 1;
    private const byte T_HANDSHAKE_REQ = 0x01;
    private const byte T_HANDSHAKE_ACK = 0x02;
    private const byte T_REQUEST_HEAD = 0x03;
    private const byte T_RESPONSE_HEAD = 0x04;
    private const byte T_BIN_CHUNK = 0x05;
    private const int CHUNK = 64 * 1024;

    private static readonly JavaScriptSerializer json = new JavaScriptSerializer();
    private static Dictionary<string, object> hostContext;
    private static string currentLanguage = "zh-CN";
    private static readonly Dictionary<string, string> i18nStrings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public class NpmPackageItem
    {
        public string Name { get; set; }
        public string Version { get; set; }
        public string Description { get; set; }
        public string License { get; set; }
        public string Author { get; set; }
        public string Homepage { get; set; }
        public string Bin { get; set; }
        public long Size { get; set; }
        public DateTime LastModified { get; set; }
        public string InstallPath { get; set; }
        public int DepsCount { get; set; }
        public string RawPackageJson { get; set; }

        public NpmPackageItem()
        {
            Name = "";
            Version = "";
            Description = "";
            License = "";
            Author = "";
            Homepage = "";
            Bin = "";
            InstallPath = "";
            RawPackageJson = "";
        }
    }

    public class PnpmStorePackageItem
    {
        public string Name { get; set; }
        public string Version { get; set; }
        public int FileCount { get; set; }
        public long Size { get; set; }
        public DateTime LastModified { get; set; }
        public string IndexFilePath { get; set; }
        public string Hash { get; set; }
        public int DepsCount { get; set; }
        public Dictionary<string, string> Dependencies { get; set; }

        public PnpmStorePackageItem()
        {
            Name = "";
            Version = "";
            IndexFilePath = "";
            Hash = "";
            Dependencies = new Dictionary<string, string>();
        }
    }

    public class PnpmDiskStoreItem
    {
        public string DriveLetter { get; set; }
        public string StorePath { get; set; }
        public string StoreVersion { get; set; }
        public int FileCount { get; set; }
        public long Size { get; set; }
        public DateTime LastModified { get; set; }
        public List<PnpmStorePackageItem> Packages { get; set; }

        public PnpmDiskStoreItem()
        {
            DriveLetter = "";
            StorePath = "";
            StoreVersion = "";
            Packages = new List<PnpmStorePackageItem>();
        }
    }

    public class PnpmScanResult
    {
        public List<PnpmDiskStoreItem> Stores { get; set; }
        public List<NpmPackageItem> GlobalPackages { get; set; }
        public long TotalStoreSize { get; set; }
        public long MetadataSize { get; set; }
        public long DlxSize { get; set; }
        public long TotalGlobalPkgSize { get; set; }
        public string PnpmVersion { get; set; }
        public string PnpmPath { get; set; }
        public string NodeVersion { get; set; }
        public string NodePath { get; set; }
        public string GlobalBinDir { get; set; }
        public string GlobalModulesDir { get; set; }
        public string StateDir { get; set; }
        public string CacheDir { get; set; }
        public string NpmrcPath { get; set; }
        public string NpmrcContent { get; set; }
        public Dictionary<string, string> NpmrcConfigs { get; set; }

        public PnpmScanResult()
        {
            Stores = new List<PnpmDiskStoreItem>();
            GlobalPackages = new List<NpmPackageItem>();
            NpmrcConfigs = new Dictionary<string, string>();
            PnpmVersion = "";
            PnpmPath = "";
            NodeVersion = "";
            NodePath = "";
            GlobalBinDir = "";
            GlobalModulesDir = "";
            StateDir = "";
            CacheDir = "";
            NpmrcPath = "";
            NpmrcContent = "";
        }
    }

    private static int Main()
    {
        int parentPid = -1;
        try { parentPid = Convert.ToInt32(Environment.GetEnvironmentVariable("LDS_PARENT_PID")); } catch { }
        if (parentPid > 0)
        {
            Thread watchdog = new Thread(delegate()
            {
                while (true)
                {
                    Thread.Sleep(3000);
                    try { Process.GetProcessById(parentPid); } catch { Environment.Exit(0); }
                }
            });
            watchdog.IsBackground = true;
            watchdog.Start();
        }

        Stream stdin = Console.OpenStandardInput();
        Stream stdout = Console.OpenStandardOutput();

        // 启动时后台自动扫描
        TriggerPnpmScanAsync();

        try
        {
            while (true)
            {
                byte type;
                byte[] payload;
                ReadFrame(stdin, out type, out payload);

                if (type == T_HANDSHAKE_REQ)
                {
                    hostContext = json.DeserializeObject(Encoding.UTF8.GetString(payload)) as Dictionary<string, object>;
                    if (hostContext != null)
                    {
                        if (hostContext.ContainsKey("parentPid"))
                        {
                            parentPid = Convert.ToInt32(hostContext["parentPid"]);
                        }
                        if (hostContext.ContainsKey("language") && hostContext["language"] != null)
                        {
                            currentLanguage = Convert.ToString(hostContext["language"]);
                        }
                    }
                    LoadLanguage(currentLanguage);
                    Dictionary<string, object> ack = new Dictionary<string, object>();
                    ack["protocol"] = PROTOCOL_VERSION;
                    ack["plugin"] = "pnpm";
                    ack["version"] = "1.0.0";
                    WriteFrame(stdout, T_HANDSHAKE_ACK, Encoding.UTF8.GetBytes(json.Serialize(ack)));
                    Log("PNPM 插件握手完成");
                }
                else if (type == T_REQUEST_HEAD)
                {
                    HandleRequest(stdin, stdout, payload);
                }
            }
        }
        catch (Exception ex)
        {
            Log("PNPM 插件致命异常退出: " + ex.Message);
            return 1;
        }
    }

    private static void LoadLanguage(string lang)
    {
        try
        {
            string langFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "lang\\" + lang + ".ini");
            if (!File.Exists(langFile))
            {
                langFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "lang\\zh-CN.ini");
            }
            if (File.Exists(langFile))
            {
                string[] lines = File.ReadAllLines(langFile, Encoding.UTF8);
                lock (i18nStrings)
                {
                    i18nStrings.Clear();
                    foreach (string l in lines)
                    {
                        string t = l.Trim();
                        if (string.IsNullOrEmpty(t) || t.StartsWith("#") || t.StartsWith(";")) continue;
                        int eq = t.IndexOf('=');
                        if (eq > 0)
                        {
                            string k = t.Substring(0, eq).Trim();
                            string v = t.Substring(eq + 1).Trim();
                            i18nStrings[k] = v.Replace("\\n", "\n").Replace("\\t", "\t");
                        }
                    }
                }
            }
        }
        catch { }
    }

    private static string T(string key, params object[] args)
    {
        string val;
        lock (i18nStrings)
        {
            if (!i18nStrings.TryGetValue(key, out val)) val = key;
        }
        if (args != null && args.Length > 0)
        {
            try { return string.Format(val, args); } catch { return val; }
        }
        return val;
    }

    private static string EscapeJson(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");
    }

    private static string FormatFileSize(long bytes)
    {
        if (bytes <= 0) return "0 B";
        string[] units = new string[] { "B", "KB", "MB", "GB", "TB" };
        int i = 0;
        double d = bytes;
        while (d >= 1024 && i < units.Length - 1)
        {
            d /= 1024;
            i++;
        }
        return string.Format("{0:0.##} {1}", d, units[i]);
    }

    private static void Log(string msg)
    {
        Console.Error.WriteLine("[pnpm] " + msg);
    }
        private static string DetectNodeVersion(out string nodePath)
        {
            nodePath = "";
            try
            {
                string pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
                string[] paths = pathEnv.Split(';');
                foreach (string p in paths)
                {
                    if (string.IsNullOrEmpty(p)) continue;
                    string candidate = Path.Combine(p.Trim(), "node.exe");
                    if (File.Exists(candidate))
                    {
                        nodePath = candidate;
                        break;
                    }
                }

                if (string.IsNullOrEmpty(nodePath))
                {
                    string defaultNode = @"C:\Program Files\nodejs\node.exe";
                    if (File.Exists(defaultNode)) nodePath = defaultNode;
                }

                if (!string.IsNullOrEmpty(nodePath))
                {
                    ProcessStartInfo psi = new ProcessStartInfo
                    {
                        FileName = nodePath,
                        Arguments = "-v",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        CreateNoWindow = true
                    };
                    using (Process proc = Process.Start(psi))
                    {
                        if (proc.WaitForExit(2000))
                        {
                            string outStr = proc.StandardOutput.ReadToEnd().Trim();
                            if (!string.IsNullOrEmpty(outStr)) return outStr;
                        }
                    }
                }
            }
            catch { }
            return "";
        }

        private static readonly object pnpmScanLock = new object();
        private static bool isScanning = false;
        private static PnpmScanResult cachedResult = null;
        private static long cachedStoreTicks = 0;
        private static long cachedCacheTicks = 0;

        public static bool IsScanning { get { return isScanning; } }

        public static string GetCacheFilePath()
        {
            string cacheDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "cache");
            if (!Directory.Exists(cacheDir))
            {
                try { Directory.CreateDirectory(cacheDir); } catch { }
            }
            return Path.Combine(cacheDir, "pnpm_cache.dat");
        }

        public static void ClearCacheAndReleaseResources()
        {
            lock (pnpmScanLock)
            {
                if (cachedResult != null)
                {
                    cachedResult.Stores.Clear();
                    cachedResult.GlobalPackages.Clear();
                    cachedResult = null;
                }
                cachedStoreTicks = 0;
                cachedCacheTicks = 0;
                GC.Collect();
            }
            Log(T("log_dev_ecosystem_released"));
        }

        public static void TriggerPnpmScanAsync(bool forceRescan = false)
        {
            if (!true) return;

            ThreadPool.QueueUserWorkItem(delegate
            {
                lock (pnpmScanLock)
                {
                    if (isScanning) return;
                    isScanning = true;
                }

                try
                {
                    string localCache = GetDefaultPnpmCacheDir();
                    long curCacheTicks = 0;
                    if (!string.IsNullOrEmpty(localCache) && Directory.Exists(localCache))
                    {
                        try { curCacheTicks = Directory.GetLastWriteTimeUtc(localCache).Ticks; } catch { }
                    }

                    long curStoreTicks = GetAggregatedStoreTicks();

                    if (!forceRescan && cachedResult == null)
                    {
                        long sStore, sCache;
                        if (TryLoadFromDiskCache(out sStore, out sCache))
                        {
                            if (curStoreTicks == sStore && curCacheTicks == sCache)
                            {
                                Log(T("log_dev_ecosystem_verified"));
                                return;
                            }
                        }
                    }

                    Log(T("log_pnpm_scan_started"));
                    PnpmScanResult res = DoPnpmScan(localCache);

                    lock (pnpmScanLock)
                    {
                        cachedResult = res;
                        cachedStoreTicks = curStoreTicks;
                        cachedCacheTicks = curCacheTicks;
                    }

                    SaveToDiskCache(curStoreTicks, curCacheTicks);
                    Log(T("log_pnpm_scan_finished", res.Stores.Count, res.GlobalPackages.Count, FormatSize(res.TotalStoreSize + res.DlxSize + res.MetadataSize)));
                }
                catch (Exception ex)
                {
                    Log("Pnpm scan error: " + ex.Message);
                }
                finally
                {
                    lock (pnpmScanLock)
                    {
                        isScanning = false;
                    }
                }
            });
        }

        private static string GetDefaultPnpmCacheDir()
        {
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string pnpmCache = Path.Combine(localAppData, "pnpm-cache");
            if (Directory.Exists(pnpmCache)) return pnpmCache;
            return "";
        }

        private static long GetAggregatedStoreTicks()
        {
            long ticks = 0;
            try
            {
                DriveInfo[] drives = DriveInfo.GetDrives();
                foreach (DriveInfo d in drives)
                {
                    if (!d.IsReady) continue;
                    string r = d.RootDirectory.FullName;
                    string[] cands = new string[] {
                        Path.Combine(r, ".pnpm-store"),
                        Path.Combine(r, "pnpm-store"),
                        Path.Combine(r, "apps", "cache", "pnpm")
                    };
                    foreach (string cand in cands)
                    {
                        if (Directory.Exists(cand))
                        {
                            ticks += Directory.GetLastWriteTimeUtc(cand).Ticks;
                        }
                    }
                }
            }
            catch { }
            return ticks;
        }

        public static void DetectPnpmCli(out string pnpmVersion, out string pnpmPath)
        {
            pnpmVersion = "";
            pnpmPath = "";

            try
            {
                string pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
                string[] paths = pathEnv.Split(Path.PathSeparator);
                foreach (string p in paths)
                {
                    if (string.IsNullOrEmpty(p)) continue;
                    string c1 = Path.Combine(p.Trim('\"', ' '), "pnpm.cmd");
                    string c2 = Path.Combine(p.Trim('\"', ' '), "pnpm.ps1");
                    string c3 = Path.Combine(p.Trim('\"', ' '), "pnpm.exe");
                    if (File.Exists(c1)) { pnpmPath = c1; break; }
                    else if (File.Exists(c2)) { pnpmPath = c2; break; }
                    else if (File.Exists(c3)) { pnpmPath = c3; break; }
                }
            }
            catch { }

            if (!string.IsNullOrEmpty(pnpmPath) && File.Exists(pnpmPath))
            {
                try
                {
                    ProcessStartInfo psi = new ProcessStartInfo
                    {
                        FileName = "cmd.exe",
                        Arguments = "/c \"" + pnpmPath + "\" -v",
                        RedirectStandardOutput = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                    using (var p = Process.Start(psi))
                    {
                        string output = p.StandardOutput.ReadToEnd();
                        p.WaitForExit(4000);
                        if (!string.IsNullOrEmpty(output))
                        {
                            pnpmVersion = output.Trim();
                        }
                    }
                }
                catch { }
            }
        }

        private static PnpmScanResult DoPnpmScan(string cacheDir)
        {
            PnpmScanResult res = new PnpmScanResult();
            res.CacheDir = cacheDir;

            string pnpmVer, pnpmPath;
            DetectPnpmCli(out pnpmVer, out pnpmPath);
            res.PnpmVersion = pnpmVer;
            res.PnpmPath = pnpmPath;

            string nodePath;
            string nodeVer = DetectNodeVersion(out nodePath);
            res.NodeVersion = nodeVer;
            res.NodePath = nodePath;
            if (!string.IsNullOrEmpty(nodePath) && File.Exists(nodePath))
            {
                try
                {
                    ProcessStartInfo psi = new ProcessStartInfo
                    {
                        FileName = nodePath,
                        Arguments = "-v",
                        RedirectStandardOutput = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                    using (var p = Process.Start(psi))
                    {
                        string outStr = p.StandardOutput.ReadToEnd();
                        p.WaitForExit(3000);
                        res.NodeVersion = outStr != null ? outStr.Trim() : "";
                    }
                }
                catch { }
            }

            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            res.GlobalBinDir = Path.Combine(localAppData, "pnpm");
            res.StateDir = Path.Combine(localAppData, "pnpm", "state");

            // 读取 .npmrc 中关于 pnpm 的设定
            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string npmrcPath = Path.Combine(userProfile, ".npmrc");
            if (File.Exists(npmrcPath))
            {
                res.NpmrcPath = npmrcPath;
                try
                {
                    res.NpmrcContent = File.ReadAllText(npmrcPath, Encoding.UTF8);
                    string[] lines = File.ReadAllLines(npmrcPath, Encoding.UTF8);
                    foreach (string line in lines)
                    {
                        string trimmed = line.Trim();
                        if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith("#") || trimmed.StartsWith(";")) continue;
                        int eqIdx = trimmed.IndexOf('=');
                        if (eqIdx > 0)
                        {
                            string key = trimmed.Substring(0, eqIdx).Trim();
                            string val = trimmed.Substring(eqIdx + 1).Trim();
                            if (!string.IsNullOrEmpty(key) && !res.NpmrcConfigs.ContainsKey(key))
                            {
                                res.NpmrcConfigs[key] = val;
                            }
                        }
                    }
                }
                catch { }
            }

            HashSet<string> detectedStorePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // 1. 扫描所有物理驱动器
            try
            {
                DriveInfo[] drives = DriveInfo.GetDrives();
                foreach (DriveInfo d in drives)
                {
                    if (!d.IsReady) continue;
                    string r = d.RootDirectory.FullName;

                    string[] candidates = new string[] {
                        Path.Combine(r, ".pnpm-store"),
                        Path.Combine(r, "pnpm-store"),
                        Path.Combine(r, "apps", "cache", "pnpm"),
                        Path.Combine(r, "cache", "pnpm")
                    };

                    foreach (string cand in candidates)
                    {
                        if (Directory.Exists(cand) && !detectedStorePaths.Contains(cand))
                        {
                            PnpmDiskStoreItem storeItem = InspectStoreDirectory(cand, d.Name);
                            if (storeItem != null)
                            {
                                detectedStorePaths.Add(cand);
                                res.Stores.Add(storeItem);
                                res.TotalStoreSize += storeItem.Size;
                            }
                        }
                    }
                }
            }
            catch { }

            // 2. 扫描用户 AppData 目录下的 pnpm store
            string userPnpmStore = Path.Combine(localAppData, "pnpm", "store");
            if (Directory.Exists(userPnpmStore) && !detectedStorePaths.Contains(userPnpmStore))
            {
                PnpmDiskStoreItem storeItem = InspectStoreDirectory(userPnpmStore, Path.GetPathRoot(userPnpmStore));
                if (storeItem != null)
                {
                    detectedStorePaths.Add(userPnpmStore);
                    res.Stores.Add(storeItem);
                    res.TotalStoreSize += storeItem.Size;
                }
            }

            // 3. 扫描 PNPM 全局安装包
            ScanPnpmGlobalPackages(localAppData, res);

            // 4. 扫描元数据与 DLX
            if (!string.IsNullOrEmpty(cacheDir) && Directory.Exists(cacheDir))
            {
                string dlx = Path.Combine(cacheDir, "dlx");
                if (Directory.Exists(dlx)) res.DlxSize = FastGetDirSize(dlx);

                string meta1 = Path.Combine(cacheDir, "metadata-v1.3");
                if (Directory.Exists(meta1)) res.MetadataSize += FastGetDirSize(meta1);

                string meta2 = Path.Combine(cacheDir, "metadata-full-v1.3");
                if (Directory.Exists(meta2)) res.MetadataSize += FastGetDirSize(meta2);
            }

            return res;
        }

        private static PnpmDiskStoreItem InspectStoreDirectory(string storePath, string driveLetter)
        {
            try
            {
                PnpmDiskStoreItem item = new PnpmDiskStoreItem();
                item.DriveLetter = driveLetter.TrimEnd('\\');
                item.StorePath = storePath;
                item.LastModified = Directory.GetLastWriteTime(storePath);

                // 探测版本结构（如 v3, v10, 2 等）
                DirectoryInfo dir = new DirectoryInfo(storePath);
                DirectoryInfo[] subDirs = dir.GetDirectories();
                string ver = "v3";
                DirectoryInfo activeVerDir = null;
                foreach (DirectoryInfo sub in subDirs)
                {
                    if (sub.Name.StartsWith("v", StringComparison.OrdinalIgnoreCase) || sub.Name == "2" || sub.Name == "3")
                    {
                        ver = sub.Name;
                        activeVerDir = sub;
                        break;
                    }
                }
                item.StoreVersion = ver;

                // 统计文件数与大小
                int fileCount = 0;
                long totalSize = 0;
                foreach (FileInfo fi in dir.GetFiles("*", SearchOption.AllDirectories))
                {
                    fileCount++;
                    totalSize += fi.Length;
                }
                item.FileCount = fileCount;
                item.Size = totalSize;

                // 扫描 Store 的 index 目录解析包模块
                if (activeVerDir != null)
                {
                    string indexDir = Path.Combine(activeVerDir.FullName, "index");
                    if (Directory.Exists(indexDir))
                    {
                        ScanStoreIndexPackages(indexDir, item.Packages);
                    }
                }

                return item;
            }
            catch
            {
                return null;
            }
        }

        private static void ScanStoreIndexPackages(string indexDir, List<PnpmStorePackageItem> list)
        {
            try
            {
                DirectoryInfo idxDir = new DirectoryInfo(indexDir);
                FileInfo[] files = idxDir.GetFiles("*.json", SearchOption.AllDirectories);
                Regex nameRegex = new Regex("^[0-9a-fA-F]+-(.+)@([^@]+)\\.json$", RegexOptions.Compiled);

                // index 目录同级即版本目录，其下 files 为 CAS 内容文件根（files/<hash前2位>/<hash去前2位>）
                string storeFilesDir = "";
                try
                {
                    string verDir = Path.GetDirectoryName(Path.GetFullPath(indexDir).TrimEnd(Path.DirectorySeparatorChar));
                    if (!string.IsNullOrEmpty(verDir))
                    {
                        string cand = Path.Combine(verDir, "files");
                        if (Directory.Exists(cand)) storeFilesDir = cand;
                    }
                }
                catch { }

                foreach (FileInfo fi in files)
                {
                    Match m = nameRegex.Match(fi.Name);
                    if (m.Success)
                    {
                        PnpmStorePackageItem pkg = new PnpmStorePackageItem();
                        pkg.Name = m.Groups[1].Value.Replace("+", "/");
                        pkg.Version = m.Groups[2].Value;
                        pkg.LastModified = fi.LastWriteTime;
                        pkg.IndexFilePath = fi.FullName;

                        int dashIdx = fi.Name.IndexOf('-');
                        if (dashIdx > 0)
                        {
                            pkg.Hash = fi.Name.Substring(0, dashIdx);
                        }

                        // 快速从 index 文件解析 files 文件数与总体积
                        try
                        {
                            string content = File.ReadAllText(fi.FullName, Encoding.UTF8);
                            MatchCollection sizeMatches = Regex.Matches(content, "\"size\"\\s*:\\s*(\\d+)");
                            pkg.FileCount = sizeMatches.Count;
                            long pkgSize = 0;
                            foreach (Match sm in sizeMatches)
                            {
                                long s;
                                if (long.TryParse(sm.Groups[1].Value, out s))
                                {
                                    pkgSize += s;
                                }
                            }
                            pkg.Size = pkgSize > 0 ? pkgSize : fi.Length;

                            // 从 store 内容文件中反查该包的 package.json 提取声明依赖
                            pkg.Dependencies = ExtractStorePkgDependencies(content, storeFilesDir);
                            pkg.DepsCount = pkg.Dependencies.Count;
                        }
                        catch
                        {
                            pkg.FileCount = 1;
                            pkg.Size = fi.Length;
                        }

                        list.Add(pkg);
                    }
                }

                list.Sort(delegate(PnpmStorePackageItem a, PnpmStorePackageItem b)
                {
                    return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
                });
            }
            catch { }
        }

        // 从 index JSON 的 "package.json" 条目 integrity (sha512-<base64>) 反推 CAS 物理文件路径，
        // 读取其内容解析 dependencies 声明清单；任一环节失败返回空字典（不影响包列表）
        private static Dictionary<string, string> ExtractStorePkgDependencies(string indexContent, string storeFilesDir)
        {
            Dictionary<string, string> deps = new Dictionary<string, string>();
            try
            {
                if (string.IsNullOrEmpty(storeFilesDir) || string.IsNullOrEmpty(indexContent)) return deps;

                Match mEntry = Regex.Match(indexContent, "\"package\\.json\"\\s*:\\s*\\{([^{}]+)\\}");
                if (!mEntry.Success) return deps;

                Match mIntegrity = Regex.Match(mEntry.Groups[1].Value, "\"integrity\"\\s*:\\s*\"sha512-([A-Za-z0-9+/=]+)\"");
                if (!mIntegrity.Success) return deps;

                byte[] hashBytes;
                try { hashBytes = Convert.FromBase64String(mIntegrity.Groups[1].Value); }
                catch { return deps; }
                if (hashBytes.Length < 2) return deps;

                StringBuilder hex = new StringBuilder(hashBytes.Length * 2);
                foreach (byte b in hashBytes) hex.Append(b.ToString("x2"));
                string hexStr = hex.ToString();

                string physicalFile = Path.Combine(storeFilesDir, hexStr.Substring(0, 2), hexStr.Substring(2));
                if (!File.Exists(physicalFile)) return deps;

                string pkgJson = File.ReadAllText(physicalFile, Encoding.UTF8);
                Match mDeps = Regex.Match(pkgJson, "\"dependencies\"\\s*:\\s*\\{([^{}]+)\\}");
                if (!mDeps.Success) return deps;

                foreach (Match mp in Regex.Matches(mDeps.Groups[1].Value, "\"([^\"]+)\"\\s*:\\s*\"([^\"]*)\""))
                {
                    string depName = mp.Groups[1].Value;
                    if (!deps.ContainsKey(depName)) deps[depName] = mp.Groups[2].Value;
                }
            }
            catch { }
            return deps;
        }

        private static void ScanPnpmGlobalPackages(string localAppData, PnpmScanResult res)
        {
            try
            {
                string globalDir = Path.Combine(localAppData, "pnpm", "global");
                if (!Directory.Exists(globalDir)) return;

                DirectoryInfo gDir = new DirectoryInfo(globalDir);
                foreach (DirectoryInfo sub in gDir.GetDirectories())
                {
                    string nm = Path.Combine(sub.FullName, "node_modules");
                    if (Directory.Exists(nm))
                    {
                        if (string.IsNullOrEmpty(res.GlobalModulesDir)) res.GlobalModulesDir = nm;
                        DirectoryInfo nmDir = new DirectoryInfo(nm);
                        foreach (DirectoryInfo pkgDir in nmDir.GetDirectories())
                        {
                            if (pkgDir.Name.StartsWith("@"))
                            {
                                foreach (DirectoryInfo scopeDir in pkgDir.GetDirectories())
                                {
                                    NpmPackageItem item = ParsePackage(scopeDir.FullName, pkgDir.Name + "/" + scopeDir.Name);
                                    if (item != null)
                                    {
                                        res.GlobalPackages.Add(item);
                                        res.TotalGlobalPkgSize += item.Size;
                                    }
                                }
                            }
                            else
                            {
                                NpmPackageItem item = ParsePackage(pkgDir.FullName, pkgDir.Name);
                                if (item != null)
                                {
                                    res.GlobalPackages.Add(item);
                                    res.TotalGlobalPkgSize += item.Size;
                                }
                            }
                        }
                    }
                }
            }
            catch { }
        }

        private static NpmPackageItem ParsePackage(string dirPath, string defaultName)
        {
            string pkgJsonPath = Path.Combine(dirPath, "package.json");
            if (!File.Exists(pkgJsonPath)) return null;

            try
            {
                NpmPackageItem item = new NpmPackageItem();
                item.InstallPath = dirPath;
                item.Name = defaultName;
                item.LastModified = Directory.GetLastWriteTime(dirPath);
                item.Size = FastGetDirSize(dirPath);

                string content = File.ReadAllText(pkgJsonPath, Encoding.UTF8);
                item.RawPackageJson = content;

                Match mName = Regex.Match(content, "\"name\"\\s*:\\s*\"([^\"]+)\"");
                if (mName.Success) item.Name = mName.Groups[1].Value;

                Match mVer = Regex.Match(content, "\"version\"\\s*:\\s*\"([^\"]+)\"");
                if (mVer.Success) item.Version = mVer.Groups[1].Value;

                Match mDesc = Regex.Match(content, "\"description\"\\s*:\\s*\"([^\"]+)\"");
                if (mDesc.Success) item.Description = mDesc.Groups[1].Value;

                Match mLic = Regex.Match(content, "\"license\"\\s*:\\s*\"([^\"]+)\"");
                if (mLic.Success) item.License = mLic.Groups[1].Value;

                Match mHome = Regex.Match(content, "\"homepage\"\\s*:\\s*\"([^\"]+)\"");
                if (mHome.Success) item.Homepage = mHome.Groups[1].Value;

                Match mAuth = Regex.Match(content, "\"author\"\\s*:\\s*\"([^\"]+)\"");
                if (mAuth.Success) item.Author = mAuth.Groups[1].Value;

                Match mBinStr = Regex.Match(content, "\"bin\"\\s*:\\s*\"([^\"]+)\"");
                if (mBinStr.Success) item.Bin = item.Name;
                else
                {
                    Match mBinObj = Regex.Match(content, "\"bin\"\\s*:\\s*\\{([^}]+)\\}");
                    if (mBinObj.Success)
                    {
                        List<string> bins = new List<string>();
                        foreach (Match mb in Regex.Matches(mBinObj.Groups[1].Value, "\"([^\"]+)\"\\s*:"))
                        {
                            bins.Add(mb.Groups[1].Value);
                        }
                        item.Bin = string.Join(", ", bins.ToArray());
                    }
                }

                Match mDeps = Regex.Match(content, "\"dependencies\"\\s*:\\s*\\{([^}]+)\\}");
                if (mDeps.Success)
                {
                    item.DepsCount = Regex.Matches(mDeps.Groups[1].Value, "\"[^\"]+\"\\s*:").Count;
                }

                return item;
            }
            catch
            {
                return null;
            }
        }

        private static long FastGetDirSize(string dirPath)
        {
            long size = 0;
            try
            {
                DirectoryInfo dir = new DirectoryInfo(dirPath);
                foreach (FileInfo fi in dir.GetFiles("*", SearchOption.AllDirectories))
                {
                    size += fi.Length;
                }
            }
            catch { }
            return size;
        }

        private static string FormatSize(long bytes)
        {
            if (bytes <= 0) return "0 B";
            if (bytes < 1024) return bytes + " B";
            if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("0.##") + " KB";
            if (bytes < 1024 * 1024 * 1024) return (bytes / (1024.0 * 1024.0)).ToString("0.##") + " MB";
            return (bytes / (1024.0 * 1024.0 * 1024.0)).ToString("0.##") + " GB";
        }

        private static void SaveToDiskCache(long storeTicks, long cacheTicks)
        {
            try
            {
                string cacheFile = GetCacheFilePath();
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("FORMAT=3");
                sb.AppendLine("STORE_TICKS=" + storeTicks);
                sb.AppendLine("CACHE_TICKS=" + cacheTicks);

                lock (pnpmScanLock)
                {
                    if (cachedResult != null)
                    {
                        sb.AppendLine("TOTAL_STORE_SIZE=" + cachedResult.TotalStoreSize);
                        sb.AppendLine("METADATA_SIZE=" + cachedResult.MetadataSize);
                        sb.AppendLine("DLX_SIZE=" + cachedResult.DlxSize);
                        sb.AppendLine("TOTAL_GLOBAL_PKG_SIZE=" + cachedResult.TotalGlobalPkgSize);
                        sb.AppendLine("CACHE_DIR=" + EscapeLine(cachedResult.CacheDir));
                        sb.AppendLine("STORE_COUNT=" + cachedResult.Stores.Count);

                        foreach (PnpmDiskStoreItem s in cachedResult.Stores)
                        {
                            sb.AppendLine(string.Format("STORE\t{0}\t{1}\t{2}\t{3}\t{4}\t{5}",
                                EscapeField(s.DriveLetter),
                                EscapeField(s.StorePath),
                                EscapeField(s.StoreVersion),
                                s.FileCount,
                                s.Size,
                                s.LastModified.Ticks));

                            foreach (PnpmStorePackageItem sp in s.Packages)
                            {
                                sb.AppendLine(string.Format("STOREPKG\t{0}\t{1}\t{2}\t{3}\t{4}\t{5}\t{6}\t{7}\t{8}\t{9}",
                                    EscapeField(s.StorePath),
                                    EscapeField(sp.Name),
                                    EscapeField(sp.Version),
                                    EscapeField(sp.Hash),
                                    EscapeField(sp.IndexFilePath),
                                    sp.FileCount,
                                    sp.Size,
                                    sp.LastModified.Ticks,
                                    sp.DepsCount,
                                    EscapeField(SerializeDeps(sp.Dependencies))));
                            }
                        }

                        sb.AppendLine("PKG_COUNT=" + cachedResult.GlobalPackages.Count);
                        foreach (NpmPackageItem p in cachedResult.GlobalPackages)
                        {
                            sb.AppendLine(string.Format("PKG\t{0}\t{1}\t{2}\t{3}\t{4}\t{5}\t{6}\t{7}\t{8}\t{9}\t{10}",
                                EscapeField(p.Name),
                                EscapeField(p.Version),
                                EscapeField(p.Description),
                                EscapeField(p.License),
                                EscapeField(p.Author),
                                EscapeField(p.Homepage),
                                EscapeField(p.Bin),
                                p.Size,
                                p.LastModified.Ticks,
                                EscapeField(p.InstallPath),
                                p.DepsCount));
                        }
                    }
                }

                File.WriteAllText(cacheFile, sb.ToString(), Encoding.UTF8);
                Log(T("log_dev_ecosystem_saved", cacheFile));
            }
            catch { }
        }

        private static bool TryLoadFromDiskCache(out long storeTicks, out long cacheTicks)
        {
            storeTicks = 0;
            cacheTicks = 0;
            string cacheFile = GetCacheFilePath();
            if (!File.Exists(cacheFile)) return false;

            try
            {
                Stopwatch sw = Stopwatch.StartNew();
                string[] lines = File.ReadAllLines(cacheFile, Encoding.UTF8);
                PnpmScanResult res = new PnpmScanResult();
                bool formatV3 = false;
                PnpmDiskStoreItem currentStore = null;

                foreach (string line in lines)
                {
                    if (string.IsNullOrEmpty(line)) continue;
                    if (line == "FORMAT=3") formatV3 = true;
                    else if (line.StartsWith("STORE_TICKS=")) long.TryParse(line.Substring(12), out storeTicks);
                    else if (line.StartsWith("CACHE_TICKS=")) long.TryParse(line.Substring(12), out cacheTicks);
                    else if (line.StartsWith("TOTAL_STORE_SIZE=")) { long sz; if (long.TryParse(line.Substring(17), out sz)) res.TotalStoreSize = sz; }
                    else if (line.StartsWith("METADATA_SIZE=")) { long sz; if (long.TryParse(line.Substring(14), out sz)) res.MetadataSize = sz; }
                    else if (line.StartsWith("DLX_SIZE=")) { long sz; if (long.TryParse(line.Substring(9), out sz)) res.DlxSize = sz; }
                    else if (line.StartsWith("TOTAL_GLOBAL_PKG_SIZE=")) { long sz; if (long.TryParse(line.Substring(22), out sz)) res.TotalGlobalPkgSize = sz; }
                    else if (line.StartsWith("CACHE_DIR=")) res.CacheDir = UnescapeLine(line.Substring(10));
                    else if (line.StartsWith("STORE\t"))
                    {
                        string[] parts = line.Split('\t');
                        if (parts.Length >= 7)
                        {
                            PnpmDiskStoreItem item = new PnpmDiskStoreItem();
                            item.DriveLetter = UnescapeField(parts[1]);
                            item.StorePath = UnescapeField(parts[2]);
                            item.StoreVersion = UnescapeField(parts[3]);
                            int cnt; if (int.TryParse(parts[4], out cnt)) item.FileCount = cnt;
                            long sz; if (long.TryParse(parts[5], out sz)) item.Size = sz;
                            long ticks; if (long.TryParse(parts[6], out ticks)) item.LastModified = new DateTime(ticks);
                            res.Stores.Add(item);
                            currentStore = item;
                        }
                    }
                    else if (line.StartsWith("STOREPKG\t"))
                    {
                        string[] parts = line.Split('\t');
                        if (parts.Length >= 11 && currentStore != null)
                        {
                            PnpmStorePackageItem item = new PnpmStorePackageItem();
                            item.Name = UnescapeField(parts[2]);
                            item.Version = UnescapeField(parts[3]);
                            item.Hash = UnescapeField(parts[4]);
                            item.IndexFilePath = UnescapeField(parts[5]);
                            int fc; if (int.TryParse(parts[6], out fc)) item.FileCount = fc;
                            long sz; if (long.TryParse(parts[7], out sz)) item.Size = sz;
                            long pticks; if (long.TryParse(parts[8], out pticks)) item.LastModified = new DateTime(pticks);
                            int dc; if (int.TryParse(parts[9], out dc)) item.DepsCount = dc;
                            item.Dependencies = DeserializeDeps(UnescapeField(parts[10]));
                            currentStore.Packages.Add(item);
                        }
                    }
                    else if (line.StartsWith("PKG\t"))
                    {
                        string[] parts = line.Split('\t');
                        if (parts.Length >= 12)
                        {
                            NpmPackageItem item = new NpmPackageItem();
                            item.Name = UnescapeField(parts[1]);
                            item.Version = UnescapeField(parts[2]);
                            item.Description = UnescapeField(parts[3]);
                            item.License = UnescapeField(parts[4]);
                            item.Author = UnescapeField(parts[5]);
                            item.Homepage = UnescapeField(parts[6]);
                            item.Bin = UnescapeField(parts[7]);
                            long sz; if (long.TryParse(parts[8], out sz)) item.Size = sz;
                            long ticks; if (long.TryParse(parts[9], out ticks)) item.LastModified = new DateTime(ticks);
                            item.InstallPath = UnescapeField(parts[10]);
                            int deps; if (int.TryParse(parts[11], out deps)) item.DepsCount = deps;
                            res.GlobalPackages.Add(item);
                        }
                    }
                }

                // 旧格式(v1/v2)缓存缺少 store 包列表或索引路径，返回 false 触发一次全量重扫以升级缓存
                if (!formatV3)
                {
                    return false;
                }

                lock (pnpmScanLock)
                {
                    cachedResult = res;
                    cachedStoreTicks = storeTicks;
                    cachedCacheTicks = cacheTicks;
                }

                sw.Stop();
                Log(T("log_dev_ecosystem_fast_loaded", "PNPM", res.Stores.Count, sw.ElapsedMilliseconds));
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static string EscapeLine(string text) { return (text ?? "").Trim('\r', '\n'); }
        private static string UnescapeLine(string text) { return text ?? ""; }
        private static string EscapeField(string text) { return (text ?? "").Replace("\t", " ").Replace("\r", "").Replace("\n", " "); }
        private static string UnescapeField(string text) { return text ?? ""; }

        // 依赖字典 <-> "名称=版本范围;名称=版本范围" 单行文本（依赖名与 semver 范围均不含 ';'，'=' 以首个为分隔符）
        private static string SerializeDeps(Dictionary<string, string> deps)
        {
            if (deps == null || deps.Count == 0) return "";
            StringBuilder sb = new StringBuilder();
            foreach (var kv in deps)
            {
                if (sb.Length > 0) sb.Append(";");
                sb.Append(EscapeField(kv.Key)).Append("=").Append(EscapeField(kv.Value));
            }
            return sb.ToString();
        }

        private static Dictionary<string, string> DeserializeDeps(string text)
        {
            Dictionary<string, string> deps = new Dictionary<string, string>();
            if (string.IsNullOrEmpty(text)) return deps;
            string[] pairs = text.Split(';');
            foreach (string pair in pairs)
            {
                int eq = pair.IndexOf('=');
                if (eq > 0)
                {
                    string k = pair.Substring(0, eq);
                    if (!deps.ContainsKey(k)) deps[k] = pair.Substring(eq + 1);
                }
            }
            return deps;
        }

    private static void HandleRequest(Stream stdin, Stream stdout, byte[] payload)
    {
        Dictionary<string, object> head = json.DeserializeObject(Encoding.UTF8.GetString(payload)) as Dictionary<string, object>;
        int id = Convert.ToInt32(head["id"]);
        string method = Convert.ToString(head["method"]);
        string path = head.ContainsKey("path") ? Convert.ToString(head["path"]) : "";
        long bodyLen = head.ContainsKey("bodyLen") ? Convert.ToInt64(head["bodyLen"]) : 0;

        Dictionary<string, string> query = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (head.ContainsKey("query") && head["query"] is Dictionary<string, object>)
        {
            foreach (var kvp in (Dictionary<string, object>)head["query"])
            {
                query[kvp.Key] = Convert.ToString(kvp.Value);
            }
        }

        byte[] body = new byte[0];
        if (bodyLen > 0)
        {
            using (MemoryStream ms = new MemoryStream())
            {
                long received = 0;
                while (received < bodyLen)
                {
                    byte t;
                    byte[] chunkPayload;
                    ReadFrame(stdin, out t, out chunkPayload);
                    if (t != T_BIN_CHUNK || chunkPayload.Length < 4) continue;
                    int chunkId = chunkPayload[0] | (chunkPayload[1] << 8) | (chunkPayload[2] << 16) | (chunkPayload[3] << 24);
                    if (chunkId != id) continue;
                    ms.Write(chunkPayload, 4, chunkPayload.Length - 4);
                    received += chunkPayload.Length - 4;
                }
                body = ms.ToArray();
            }
        }

        int status = 200;
        string contentType = "application/json; charset=utf-8";
        byte[] respBody = null;

        try
        {
            if (path.Length == 0)
            {
                // 主页面输出
                contentType = "text/html; charset=utf-8";
                string htmlPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "index.html");
                string template = File.ReadAllText(htmlPath, Encoding.UTF8);

                template = template.Replace("{PNPM_BREADCRUMB}", T("pnpm_breadcrumb"));
                template = template.Replace("{LOBBY_PROTO_TOGGLE_TITLE}", T("lobby_proto_toggle_title"));
                template = template.Replace("{NPM_SEARCH_PLACEHOLDER}", T("pnpm_search_placeholder"));
                template = template.Replace("{PNPM_BTN_RESCAN}", T("npm_btn_rescan"));
                template = template.Replace("{PNPM_BTN_CLEAN_DLX}", T("pnpm_btn_clean_dlx"));
                template = template.Replace("{PNPM_BTN_CONFIG_DETAILS}", T("pnpm_btn_config_details"));
                template = template.Replace("{PNPM_SEC_STORES}", T("pnpm_sec_stores"));
                template = template.Replace("{PNPM_SEC_GLOBAL}", T("pnpm_sec_global"));
                template = template.Replace("{NPM_TH_NAME}", T("npm_th_name"));
                template = template.Replace("{NPM_TH_VERSION}", T("npm_th_version"));
                template = template.Replace("{PNPM_TH_FILE_COUNT}", T("pnpm_th_file_count"));
                template = template.Replace("{NPM_TH_SIZE}", T("npm_th_size"));
                template = template.Replace("{PREVIEW_BTN_EXPAND}", T("preview_btn_expand"));
                template = template.Replace("{PNPM_DETAIL_TITLE}", T("pnpm_detail_title"));
                template = template.Replace("{PREVIEW_BTN_COLLAPSE}", T("preview_btn_collapse"));
                template = template.Replace("{PNPM_DETAIL_EMPTY}", T("pnpm_detail_empty"));
                template = template.Replace("{PNPM_MODAL_CONFIG_TITLE}", T("pnpm_modal_config_title"));
                template = template.Replace("{PAGE_SIZE_LABEL}", T("pagination_page_size"));
                template = template.Replace("{PAGE_FIRST}", T("pagination_first"));
                template = template.Replace("{PAGE_PREV}", T("pagination_prev"));
                template = template.Replace("{PAGE_NEXT}", T("pagination_next"));
                template = template.Replace("{PAGE_LAST}", T("pagination_last"));
                template = template.Replace("{PNPM_LOADING}", T("pnpm_loading"));
                template = template.Replace("{MODAL_BTN_OK}", T("modal_btn_ok"));

                respBody = Encoding.UTF8.GetBytes(template);
            }
            else if (path == "data")
            {
                PnpmScanResult res;
                lock (pnpmScanLock)
                {
                    res = cachedResult;
                }

                if (res == null)
                {
                    respBody = Encoding.UTF8.GetBytes("{\"scanning\":" + (isScanning ? "true" : "false") + ",\"stores\":[],\"globalPackages\":[],\"totalStoreSize\":0,\"metadataSize\":0,\"dlxSize\":0,\"totalGlobalPkgSize\":0}");
                }
                else
                {
                    StringBuilder sbData = new StringBuilder();
                    sbData.Append("{\"scanning\":").Append(isScanning ? "true" : "false");
                    sbData.Append(",\"pnpmVersion\":\"").Append(EscapeJson(res.PnpmVersion)).Append("\"");
                    sbData.Append(",\"pnpmPath\":\"").Append(EscapeJson(res.PnpmPath)).Append("\"");
                    sbData.Append(",\"nodeVersion\":\"").Append(EscapeJson(res.NodeVersion)).Append("\"");
                    sbData.Append(",\"nodePath\":\"").Append(EscapeJson(res.NodePath)).Append("\"");
                    sbData.Append(",\"globalBinDir\":\"").Append(EscapeJson(res.GlobalBinDir)).Append("\"");
                    sbData.Append(",\"globalModulesDir\":\"").Append(EscapeJson(res.GlobalModulesDir)).Append("\"");
                    sbData.Append(",\"stateDir\":\"").Append(EscapeJson(res.StateDir)).Append("\"");
                    sbData.Append(",\"npmrcPath\":\"").Append(EscapeJson(res.NpmrcPath)).Append("\"");
                    sbData.Append(",\"npmrcContent\":\"").Append(EscapeJson(res.NpmrcContent)).Append("\"");
                    sbData.Append(",\"npmrcConfigs\":{");
                    int cfgCount = 0;
                    foreach (var kv in res.NpmrcConfigs)
                    {
                        if (cfgCount > 0) sbData.Append(",");
                        sbData.Append("\"").Append(EscapeJson(kv.Key)).Append("\":\"").Append(EscapeJson(kv.Value)).Append("\"");
                        cfgCount++;
                    }
                    sbData.Append("}");
                    sbData.Append(",\"totalStoreSize\":").Append(res.TotalStoreSize);
                    sbData.Append(",\"metadataSize\":").Append(res.MetadataSize);
                    sbData.Append(",\"dlxSize\":").Append(res.DlxSize);
                    sbData.Append(",\"totalGlobalPkgSize\":").Append(res.TotalGlobalPkgSize);
                    sbData.Append(",\"cacheDir\":\"").Append(EscapeJson(res.CacheDir)).Append("\"");
                    sbData.Append(",\"stores\":[");

                    for (int i = 0; i < res.Stores.Count; i++)
                    {
                        if (i > 0) sbData.Append(",");
                        PnpmDiskStoreItem s = res.Stores[i];
                        sbData.Append("{");
                        sbData.Append("\"driveLetter\":\"").Append(EscapeJson(s.DriveLetter)).Append("\"");
                        sbData.Append(",\"storePath\":\"").Append(EscapeJson(s.StorePath)).Append("\"");
                        sbData.Append(",\"storeVersion\":\"").Append(EscapeJson(s.StoreVersion)).Append("\"");
                        sbData.Append(",\"fileCount\":").Append(s.FileCount);
                        sbData.Append(",\"size\":").Append(s.Size);
                        sbData.Append(",\"lastModified\":\"").Append(EscapeJson(s.LastModified.ToString("yyyy-MM-dd HH:mm"))).Append("\"");
                        sbData.Append(",\"packages\":[");
                        for (int j = 0; j < s.Packages.Count; j++)
                        {
                            if (j > 0) sbData.Append(",");
                            PnpmStorePackageItem pkg = s.Packages[j];
                            sbData.Append("{");
                            sbData.Append("\"name\":\"").Append(EscapeJson(pkg.Name)).Append("\"");
                            sbData.Append(",\"version\":\"").Append(EscapeJson(pkg.Version)).Append("\"");
                            sbData.Append(",\"fileCount\":").Append(pkg.FileCount);
                            sbData.Append(",\"size\":").Append(pkg.Size);
                            sbData.Append(",\"lastModified\":\"").Append(EscapeJson(pkg.LastModified.ToString("yyyy-MM-dd HH:mm"))).Append("\"");
                            sbData.Append(",\"indexFilePath\":\"").Append(EscapeJson(pkg.IndexFilePath)).Append("\"");
                            sbData.Append(",\"hash\":\"").Append(EscapeJson(pkg.Hash)).Append("\"");
                            sbData.Append(",\"depsCount\":").Append(pkg.DepsCount);
                            sbData.Append(",\"dependencies\":{");
                            int pkgDepCount = 0;
                            foreach (var kv in pkg.Dependencies)
                            {
                                if (pkgDepCount > 0) sbData.Append(",");
                                sbData.Append("\"").Append(EscapeJson(kv.Key)).Append("\":\"").Append(EscapeJson(kv.Value ?? "")).Append("\"");
                                pkgDepCount++;
                            }
                            sbData.Append("}");
                            sbData.Append("}");
                        }
                        sbData.Append("]");
                        sbData.Append("}");
                    }
                    sbData.Append("],\"globalPackages\":[");

                    for (int i = 0; i < res.GlobalPackages.Count; i++)
                    {
                        if (i > 0) sbData.Append(",");
                        NpmPackageItem p = res.GlobalPackages[i];
                        sbData.Append("{");
                        sbData.Append("\"name\":\"").Append(EscapeJson(p.Name)).Append("\"");
                        sbData.Append(",\"version\":\"").Append(EscapeJson(p.Version)).Append("\"");
                        sbData.Append(",\"description\":\"").Append(EscapeJson(p.Description)).Append("\"");
                        sbData.Append(",\"license\":\"").Append(EscapeJson(p.License)).Append("\"");
                        sbData.Append(",\"author\":\"").Append(EscapeJson(p.Author)).Append("\"");
                        sbData.Append(",\"homepage\":\"").Append(EscapeJson(p.Homepage)).Append("\"");
                        sbData.Append(",\"bin\":\"").Append(EscapeJson(p.Bin)).Append("\"");
                        sbData.Append(",\"size\":").Append(p.Size);
                        sbData.Append(",\"lastModified\":\"").Append(EscapeJson(p.LastModified.ToString("yyyy-MM-dd HH:mm"))).Append("\"");
                        sbData.Append(",\"installPath\":\"").Append(EscapeJson(p.InstallPath)).Append("\"");
                        sbData.Append(",\"depsCount\":").Append(p.DepsCount);
                        sbData.Append("}");
                    }
                    sbData.Append("]}");
                    respBody = Encoding.UTF8.GetBytes(sbData.ToString());
                }
            }
            else if (path == "open-path")
            {
                string p = query.ContainsKey("path") ? query["path"] : "";
                if (string.IsNullOrEmpty(p))
                {
                    status = 400;
                    respBody = Encoding.UTF8.GetBytes("{\"success\":false,\"message\":\"" + EscapeJson(T("api_missing_path")) + "\"}");
                }
                else
                {
                    try
                    {
                        if (File.Exists(p))
                        {
                            Process.Start("explorer.exe", "/select,\"" + p + "\"");
                        }
                        else if (Directory.Exists(p))
                        {
                            Process.Start("explorer.exe", "\"" + p + "\"");
                        }
                        else
                        {
                            status = 404;
                            respBody = Encoding.UTF8.GetBytes("{\"success\":false,\"message\":\"" + EscapeJson(T("api_path_not_found")) + "\"}");
                            goto SendResp;
                        }
                        respBody = Encoding.UTF8.GetBytes("{\"success\":true}");
                    }
                    catch (Exception ex)
                    {
                        status = 500;
                        respBody = Encoding.UTF8.GetBytes("{\"success\":false,\"message\":\"" + EscapeJson(ex.Message) + "\"}");
                    }
                }
            }
            else if (path == "terminal")
            {
                string p = query.ContainsKey("path") ? query["path"] : "";
                if (string.IsNullOrEmpty(p))
                {
                    status = 400;
                    respBody = Encoding.UTF8.GetBytes("{\"success\":false,\"message\":\"" + EscapeJson(T("api_missing_path")) + "\"}");
                }
                else
                {
                    try
                    {
                        string targetDir = p;
                        if (File.Exists(p)) targetDir = Path.GetDirectoryName(p);
                        if (!Directory.Exists(targetDir))
                        {
                            status = 404;
                            respBody = Encoding.UTF8.GetBytes("{\"success\":false,\"message\":\"" + EscapeJson(T("api_path_not_found")) + "\"}");
                            goto SendResp;
                        }
                        ProcessStartInfo psi = new ProcessStartInfo
                        {
                            FileName = "powershell.exe",
                            WorkingDirectory = targetDir,
                            UseShellExecute = true
                        };
                        Process.Start(psi);
                        respBody = Encoding.UTF8.GetBytes("{\"success\":true}");
                    }
                    catch (Exception ex)
                    {
                        status = 500;
                        respBody = Encoding.UTF8.GetBytes("{\"success\":false,\"message\":\"" + EscapeJson(ex.Message) + "\"}");
                    }
                }
            }
            else if (path == "refresh")
            {
                TriggerPnpmScanAsync(true);
                respBody = Encoding.UTF8.GetBytes("{\"success\":true,\"message\":\"" + EscapeJson(T("api_gradle_scan_started")) + "\"}");
            }
            else if (path == "pkg-files")
            {
                string indexFile = query.ContainsKey("indexFile") ? query["indexFile"] : "";
                if (string.IsNullOrEmpty(indexFile) || !File.Exists(indexFile))
                {
                    status = 404;
                    respBody = Encoding.UTF8.GetBytes("{\"success\":false,\"message\":\"" + EscapeJson(T("api_file_not_found")) + "\"}");
                }
                else
                {
                    try
                    {
                        string content = File.ReadAllText(indexFile, Encoding.UTF8);
                        respBody = Encoding.UTF8.GetBytes("{\"success\":true,\"rawIndex\":" + content + "}");
                    }
                    catch (Exception ex)
                    {
                        status = 500;
                        respBody = Encoding.UTF8.GetBytes("{\"success\":false,\"message\":\"" + EscapeJson(ex.Message) + "\"}");
                    }
                }
            }
            else if (path == "clean-dlx")
            {
                string cacheDir = GetDefaultPnpmCacheDir();
                string dlxDir = Path.Combine(cacheDir, "dlx");
                if (Directory.Exists(dlxDir))
                {
                    try
                    {
                        Directory.Delete(dlxDir, true);
                        TriggerPnpmScanAsync(true);
                    }
                    catch { }
                }
                respBody = Encoding.UTF8.GetBytes("{\"success\":true,\"message\":\"" + EscapeJson(T("npm_clean_success")) + "\"}");
            }
            else if (path == "pkg-json")
            {
                string p = query.ContainsKey("path") ? query["path"] : "";
                if (string.IsNullOrEmpty(p))
                {
                    status = 400;
                    respBody = Encoding.UTF8.GetBytes("{\"success\":false,\"message\":\"" + EscapeJson(T("api_missing_path")) + "\"}");
                }
                else
                {
                    string pkgJson = Path.Combine(p, "package.json");
                    if (File.Exists(pkgJson))
                    {
                        try
                        {
                            string content = File.ReadAllText(pkgJson, Encoding.UTF8);
                            respBody = Encoding.UTF8.GetBytes("{\"success\":true,\"content\":\"" + EscapeJson(content) + "\"}");
                        }
                        catch (Exception ex)
                        {
                            status = 500;
                            respBody = Encoding.UTF8.GetBytes("{\"success\":false,\"message\":\"" + EscapeJson(ex.Message) + "\"}");
                        }
                    }
                    else
                    {
                        status = 404;
                        respBody = Encoding.UTF8.GetBytes("{\"success\":false,\"message\":\"" + EscapeJson(T("api_file_not_found")) + "\"}");
                    }
                }
            }
            else
            {
                status = 404;
                respBody = Encoding.UTF8.GetBytes("{\"success\":false,\"error\":\"unknown path: " + path + "\"}");
            }
        }
        catch (Exception ex)
        {
            status = 500;
            respBody = Encoding.UTF8.GetBytes("{\"success\":false,\"error\":\"" + EscapeJson(ex.Message) + "\"}");
        }

SendResp:
        Dictionary<string, object> respHead = new Dictionary<string, object>();
        respHead["id"] = id;
        respHead["status"] = status;
        respHead["type"] = contentType;
        respHead["bodyLen"] = respBody == null ? 0 : respBody.Length;
        WriteFrame(stdout, T_RESPONSE_HEAD, Encoding.UTF8.GetBytes(json.Serialize(respHead)));

        if (respBody != null && respBody.Length > 0)
        {
            int off = 0;
            while (off < respBody.Length)
            {
                int n = Math.Min(CHUNK, respBody.Length - off);
                byte[] frame = new byte[4 + n];
                frame[0] = (byte)(id & 0xFF);
                frame[1] = (byte)((id >> 8) & 0xFF);
                frame[2] = (byte)((id >> 16) & 0xFF);
                frame[3] = (byte)((id >> 24) & 0xFF);
                Array.Copy(respBody, off, frame, 4, n);
                WriteFrame(stdout, T_BIN_CHUNK, frame);
                off += n;
            }
        }
    }

    private static void WriteFrame(Stream s, byte type, byte[] payload)
    {
        byte[] head = new byte[5];
        head[0] = type;
        head[1] = (byte)(payload.Length & 0xFF);
        head[2] = (byte)((payload.Length >> 8) & 0xFF);
        head[3] = (byte)((payload.Length >> 16) & 0xFF);
        head[4] = (byte)((payload.Length >> 24) & 0xFF);
        s.Write(head, 0, 5);
        if (payload.Length > 0) s.Write(payload, 0, payload.Length);
        s.Flush();
    }

    private static void ReadFrame(Stream s, out byte type, out byte[] payload)
    {
        byte[] head = ReadExactly(s, 5);
        int len = head[1] | (head[2] << 8) | (head[3] << 16) | (head[4] << 24);
        if (len < 0 || len > 16 * 1024 * 1024) throw new IOException("bad frame length");
        type = head[0];
        payload = len == 0 ? new byte[0] : ReadExactly(s, len);
    }

    private static byte[] ReadExactly(Stream s, int count)
    {
        byte[] buf = new byte[count];
        int off = 0;
        while (off < count)
        {
            int n = s.Read(buf, off, count - off);
            if (n <= 0) throw new EndOfStreamException();
            off += n;
        }
        return buf;
    }
}
