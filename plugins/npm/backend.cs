using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;

internal static class NpmBackend
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

    public class NpmSubModuleItem
    {
        public string Name { get; set; }
        public string Version { get; set; }
        public string InstallPath { get; set; }
        public long Size { get; set; }
    }

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
        public Dictionary<string, string> DeclaredDependencies { get; set; }
        public List<NpmSubModuleItem> NestedModules { get; set; }

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
            DeclaredDependencies = new Dictionary<string, string>();
            NestedModules = new List<NpmSubModuleItem>();
        }
    }

    public class NpmScanResult
    {
        public List<NpmPackageItem> Packages { get; set; }
        public long TotalPkgSize { get; set; }
        public long CacacheSize { get; set; }
        public long NpxSize { get; set; }
        public long LogsSize { get; set; }
        public string NodeVersion { get; set; }
        public string NodePath { get; set; }
        public string NpmVersion { get; set; }
        public string NpmPath { get; set; }
        public string GlobalPrefix { get; set; }
        public string GlobalBinDir { get; set; }
        public string RegistryUrl { get; set; }
        public string NpmRoot { get; set; }
        public string CacheDir { get; set; }
        public string LogsDir { get; set; }
        public string NpxDir { get; set; }
        public string CacacheDir { get; set; }
        public string NpmrcPath { get; set; }
        public string NpmrcContent { get; set; }
        public Dictionary<string, string> NpmrcConfigs { get; set; }

        public NpmScanResult()
        {
            Packages = new List<NpmPackageItem>();
            NpmrcConfigs = new Dictionary<string, string>();
            NodeVersion = "";
            NodePath = "";
            NpmVersion = "";
            NpmPath = "";
            GlobalPrefix = "";
            GlobalBinDir = "";
            RegistryUrl = "";
            NpmRoot = "";
            CacheDir = "";
            LogsDir = "";
            NpxDir = "";
            CacacheDir = "";
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
        TriggerNpmScanAsync();

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
                    ack["plugin"] = "npm";
                    ack["version"] = "1.0.0";
                    WriteFrame(stdout, T_HANDSHAKE_ACK, Encoding.UTF8.GetBytes(json.Serialize(ack)));
                    Log("NPM 插件握手完成");
                }
                else if (type == T_REQUEST_HEAD)
                {
                    HandleRequest(stdin, stdout, payload);
                }
            }
        }
        catch (Exception ex)
        {
            Log("NPM 插件致命异常退出: " + ex.Message);
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
        Console.Error.WriteLine("[npm] " + msg);
    }
        private static readonly object npmScanLock = new object();
        private static bool isScanning = false;
        private static NpmScanResult cachedResult = null;
        private static long cachedRootTicks = 0;
        private static long cachedCacheTicks = 0;

        public static bool IsScanning { get { return isScanning; } }

        public static string GetCacheFilePath()
        {
            string cacheDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "cache");
            if (!Directory.Exists(cacheDir))
            {
                try { Directory.CreateDirectory(cacheDir); } catch { }
            }
            return Path.Combine(cacheDir, "npm_cache.dat");
        }

        public static void ClearCacheAndReleaseResources()
        {
            lock (npmScanLock)
            {
                if (cachedResult != null)
                {
                    cachedResult.Packages.Clear();
                    cachedResult.Packages.TrimExcess();
                    cachedResult = null;
                }
                cachedRootTicks = 0;
                cachedCacheTicks = 0;
                GC.Collect();
            }
            Log(T("log_dev_ecosystem_released"));
        }

        public static void TriggerNpmScanAsync(bool forceRescan = false)
        {
            if (!true) return;

            ThreadPool.QueueUserWorkItem(delegate
            {
                lock (npmScanLock)
                {
                    if (isScanning) return;
                    isScanning = true;
                }

                try
                {
                    string npmRoot = GetDefaultNpmRoot();
                    string cacheDir = GetDefaultNpmCacheDir();

                    long curRootTicks = 0;
                    long curCacheTicks = 0;

                    if (!string.IsNullOrEmpty(npmRoot) && Directory.Exists(npmRoot))
                    {
                        try { curRootTicks = Directory.GetLastWriteTimeUtc(npmRoot).Ticks; } catch { }
                    }
                    if (!string.IsNullOrEmpty(cacheDir) && Directory.Exists(cacheDir))
                    {
                        try { curCacheTicks = Directory.GetLastWriteTimeUtc(cacheDir).Ticks; } catch { }
                    }

                    if (!forceRescan && cachedResult == null)
                    {
                        long sRoot, sCache;
                        if (TryLoadFromDiskCache(out sRoot, out sCache))
                        {
                            if (curRootTicks == sRoot && curCacheTicks == sCache)
                            {
                                Log(T("log_dev_ecosystem_verified"));
                                return;
                            }
                        }
                    }

                    Log(T("log_npm_scan_started"));
                    NpmScanResult res = DoNpmScan(npmRoot, cacheDir);

                    lock (npmScanLock)
                    {
                        cachedResult = res;
                        cachedRootTicks = curRootTicks;
                        cachedCacheTicks = curCacheTicks;
                    }

                    SaveToDiskCache(curRootTicks, curCacheTicks);
                    Log(T("log_npm_scan_finished", res.Packages.Count, FormatSize(res.CacacheSize + res.NpxSize + res.LogsSize)));
                }
                catch (Exception ex)
                {
                    Log("Npm scan error: " + ex.Message);
                }
                finally
                {
                    lock (npmScanLock)
                    {
                        isScanning = false;
                    }
                }
            });
        }

        public static string DetectNodeVersion(out string nodePath)
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

        private static string DetectNpmVersion(out string npmPath)
        {
            npmPath = "";
            try
            {
                string pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
                string[] paths = pathEnv.Split(';');
                foreach (string p in paths)
                {
                    if (string.IsNullOrEmpty(p)) continue;
                    string candidateCmd = Path.Combine(p.Trim(), "npm.cmd");
                    if (File.Exists(candidateCmd))
                    {
                        npmPath = candidateCmd;
                        break;
                    }
                    string candidatePs1 = Path.Combine(p.Trim(), "npm.ps1");
                    if (File.Exists(candidatePs1) && string.IsNullOrEmpty(npmPath))
                    {
                        npmPath = candidatePs1;
                    }
                }

                if (string.IsNullOrEmpty(npmPath))
                {
                    string defaultNpm = @"C:\Program Files\nodejs\npm.cmd";
                    if (File.Exists(defaultNpm)) npmPath = defaultNpm;
                }

                if (!string.IsNullOrEmpty(npmPath))
                {
                    ProcessStartInfo psi = new ProcessStartInfo
                    {
                        FileName = "cmd.exe",
                        Arguments = "/c npm -v",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        CreateNoWindow = true
                    };
                    using (Process proc = Process.Start(psi))
                    {
                        if (proc.WaitForExit(3000))
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

        private static string GetDefaultNpmRoot()
        {
            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string npmModules = Path.Combine(appData, "npm", "node_modules");
            if (Directory.Exists(npmModules)) return npmModules;

            string roamingNpm = Path.Combine(appData, "npm");
            if (Directory.Exists(roamingNpm)) return roamingNpm;

            return "";
        }

        private static string GetDefaultNpmCacheDir()
        {
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string localCache = Path.Combine(localAppData, "npm-cache");
            if (Directory.Exists(localCache)) return localCache;

            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string roamingCache = Path.Combine(appData, "npm-cache");
            if (Directory.Exists(roamingCache)) return roamingCache;

            return "";
        }

        private static NpmScanResult DoNpmScan(string npmRoot, string cacheDir)
        {
            NpmScanResult res = new NpmScanResult();
            res.NpmRoot = npmRoot;
            res.CacheDir = cacheDir;

            string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            res.GlobalPrefix = Path.Combine(appData, "npm");
            res.GlobalBinDir = res.GlobalPrefix;

            string nodePath;
            res.NodeVersion = DetectNodeVersion(out nodePath);
            res.NodePath = nodePath;

            string npmPath;
            res.NpmVersion = DetectNpmVersion(out npmPath);
            res.NpmPath = npmPath;

            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string npmrc = Path.Combine(userProfile, ".npmrc");
            if (File.Exists(npmrc))
            {
                res.NpmrcPath = npmrc;
                try
                {
                    res.NpmrcContent = File.ReadAllText(npmrc, Encoding.UTF8);
                    string[] lines = File.ReadAllLines(npmrc, Encoding.UTF8);
                    foreach (string l in lines)
                    {
                        string line = l.Trim();
                        if (string.IsNullOrEmpty(line) || line.StartsWith("#") || line.StartsWith(";")) continue;
                        int idx = line.IndexOf('=');
                        if (idx > 0)
                        {
                            string k = line.Substring(0, idx).Trim();
                            string v = line.Substring(idx + 1).Trim();
                            res.NpmrcConfigs[k] = v;
                        }
                    }
                }
                catch { }
            }

            // 探测 Registry
            res.RegistryUrl = DetectRegistry(npmrc);

            // 扫描 node_modules 全局包
            if (!string.IsNullOrEmpty(npmRoot) && Directory.Exists(npmRoot))
            {
                ScanPackages(npmRoot, res);
            }

            // 扫描缓存体积
            if (!string.IsNullOrEmpty(cacheDir) && Directory.Exists(cacheDir))
            {
                string cacache = Path.Combine(cacheDir, "_cacache");
                if (Directory.Exists(cacache))
                {
                    res.CacacheDir = cacache;
                    res.CacacheSize = FastGetDirSize(cacache);
                }

                string npx = Path.Combine(cacheDir, "_npx");
                if (Directory.Exists(npx))
                {
                    res.NpxDir = npx;
                    res.NpxSize = FastGetDirSize(npx);
                }

                string logs = Path.Combine(cacheDir, "_logs");
                if (Directory.Exists(logs))
                {
                    res.LogsDir = logs;
                    res.LogsSize = FastGetDirSize(logs);
                }
            }

            return res;
        }

        private static void ScanPackages(string rootDir, NpmScanResult res)
        {
            try
            {
                DirectoryInfo dirInfo = new DirectoryInfo(rootDir);
                foreach (DirectoryInfo subDir in dirInfo.GetDirectories())
                {
                    if (subDir.Name.StartsWith("@"))
                    {
                        // 作用域包 @scope/pkg
                        try
                        {
                            foreach (DirectoryInfo scopeDir in subDir.GetDirectories())
                            {
                                NpmPackageItem item = ParsePackage(scopeDir.FullName, subDir.Name + "/" + scopeDir.Name);
                                if (item != null)
                                {
                                    res.Packages.Add(item);
                                    res.TotalPkgSize += item.Size;
                                }
                            }
                        }
                        catch { }
                    }
                    else
                    {
                        NpmPackageItem item = ParsePackage(subDir.FullName, subDir.Name);
                        if (item != null)
                        {
                            res.Packages.Add(item);
                            res.TotalPkgSize += item.Size;
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

                // 提取 name
                Match mName = Regex.Match(content, "\"name\"\\s*:\\s*\"([^\"]+)\"");
                if (mName.Success) item.Name = mName.Groups[1].Value;

                // 提取 version
                Match mVer = Regex.Match(content, "\"version\"\\s*:\\s*\"([^\"]+)\"");
                if (mVer.Success) item.Version = mVer.Groups[1].Value;

                // 提取 description
                Match mDesc = Regex.Match(content, "\"description\"\\s*:\\s*\"([^\"]+)\"");
                if (mDesc.Success) item.Description = mDesc.Groups[1].Value;

                // 提取 license
                Match mLic = Regex.Match(content, "\"license\"\\s*:\\s*\"([^\"]+)\"");
                if (mLic.Success) item.License = mLic.Groups[1].Value;

                // 提取 homepage
                Match mHome = Regex.Match(content, "\"homepage\"\\s*:\\s*\"([^\"]+)\"");
                if (mHome.Success) item.Homepage = mHome.Groups[1].Value;

                // 提取 author
                Match mAuth = Regex.Match(content, "\"author\"\\s*:\\s*\"([^\"]+)\"");
                if (mAuth.Success) item.Author = mAuth.Groups[1].Value;
                else
                {
                    Match mAuthObj = Regex.Match(content, "\"author\"\\s*:\\s*\\{[^}]*\"name\"\\s*:\\s*\"([^\"]+)\"");
                    if (mAuthObj.Success) item.Author = mAuthObj.Groups[1].Value;
                }

                // 提取 bin
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

                // 提取 dependencies 键值对字典
                Match mDeps = Regex.Match(content, "\"dependencies\"\\s*:\\s*\\{([^}]+)\\}");
                if (mDeps.Success)
                {
                    foreach (Match dm in Regex.Matches(mDeps.Groups[1].Value, "\"([^\"]+)\"\\s*:\\s*\"([^\"]+)\""))
                    {
                        item.DeclaredDependencies[dm.Groups[1].Value] = dm.Groups[2].Value;
                    }
                    item.DepsCount = item.DeclaredDependencies.Count;
                }

                // 探测并扫描该模块物理目录下的 node_modules 嵌套子依赖
                string subNm = Path.Combine(dirPath, "node_modules");
                if (Directory.Exists(subNm))
                {
                    try
                    {
                        DirectoryInfo subNmDir = new DirectoryInfo(subNm);
                        foreach (DirectoryInfo subPkg in subNmDir.GetDirectories())
                        {
                            if (subPkg.Name.StartsWith("@"))
                            {
                                try
                                {
                                    foreach (DirectoryInfo scopeSub in subPkg.GetDirectories())
                                    {
                                        AddNestedSubModule(scopeSub.FullName, subPkg.Name + "/" + scopeSub.Name, item.NestedModules);
                                    }
                                }
                                catch { }
                            }
                            else
                            {
                                AddNestedSubModule(subPkg.FullName, subPkg.Name, item.NestedModules);
                            }
                        }
                    }
                    catch { }
                }

                return item;
            }
            catch
            {
                return null;
            }
        }

        private static void AddNestedSubModule(string dirPath, string name, List<NpmSubModuleItem> list)
        {
            try
            {
                if (string.IsNullOrEmpty(name) || name.StartsWith(".")) return;

                NpmSubModuleItem sub = new NpmSubModuleItem();
                sub.Name = name;
                sub.InstallPath = dirPath;
                sub.Size = FastGetDirSize(dirPath);

                string pJson = Path.Combine(dirPath, "package.json");
                if (File.Exists(pJson))
                {
                    try
                    {
                        string json = File.ReadAllText(pJson, Encoding.UTF8);
                        Match mVer = Regex.Match(json, "\"version\"\\s*:\\s*\"([^\"]+)\"");
                        if (mVer.Success) sub.Version = mVer.Groups[1].Value;
                    }
                    catch { }
                }
                list.Add(sub);
            }
            catch { }
        }

        private static string DetectRegistry(string npmrcPath)
        {
            if (!string.IsNullOrEmpty(npmrcPath) && File.Exists(npmrcPath))
            {
                try
                {
                    string[] lines = File.ReadAllLines(npmrcPath);
                    foreach (string l in lines)
                    {
                        string line = l.Trim();
                        if (line.StartsWith("registry=", StringComparison.OrdinalIgnoreCase) || line.StartsWith("registry =", StringComparison.OrdinalIgnoreCase))
                        {
                            int idx = line.IndexOf('=');
                            if (idx >= 0) return line.Substring(idx + 1).Trim();
                        }
                    }
                }
                catch { }
            }
            return "https://registry.npmjs.org/";
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

        private static void SaveToDiskCache(long rootTicks, long cacheTicks)
        {
            try
            {
                string cacheFile = GetCacheFilePath();
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("ROOT_TICKS=" + rootTicks);
                sb.AppendLine("CACHE_TICKS=" + cacheTicks);

                lock (npmScanLock)
                {
                    if (cachedResult != null)
                    {
                        sb.AppendLine("TOTAL_PKG_SIZE=" + cachedResult.TotalPkgSize);
                        sb.AppendLine("CACACHE_SIZE=" + cachedResult.CacacheSize);
                        sb.AppendLine("NPX_SIZE=" + cachedResult.NpxSize);
                        sb.AppendLine("LOGS_SIZE=" + cachedResult.LogsSize);
                        sb.AppendLine("REGISTRY=" + EscapeLine(cachedResult.RegistryUrl));
                        sb.AppendLine("NPM_ROOT=" + EscapeLine(cachedResult.NpmRoot));
                        sb.AppendLine("CACHE_DIR=" + EscapeLine(cachedResult.CacheDir));
                        sb.AppendLine("NPMRC=" + EscapeLine(cachedResult.NpmrcPath));
                        sb.AppendLine("PKG_COUNT=" + cachedResult.Packages.Count);

                        foreach (NpmPackageItem p in cachedResult.Packages)
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

        private static bool TryLoadFromDiskCache(out long rootTicks, out long cacheTicks)
        {
            rootTicks = 0;
            cacheTicks = 0;
            string cacheFile = GetCacheFilePath();
            if (!File.Exists(cacheFile)) return false;

            try
            {
                Stopwatch sw = Stopwatch.StartNew();
                string[] lines = File.ReadAllLines(cacheFile, Encoding.UTF8);
                NpmScanResult res = new NpmScanResult();

                foreach (string line in lines)
                {
                    if (string.IsNullOrEmpty(line)) continue;
                    if (line.StartsWith("ROOT_TICKS=")) long.TryParse(line.Substring(11), out rootTicks);
                    else if (line.StartsWith("CACHE_TICKS=")) long.TryParse(line.Substring(12), out cacheTicks);
                    else if (line.StartsWith("TOTAL_PKG_SIZE=")) { long sz; if (long.TryParse(line.Substring(15), out sz)) res.TotalPkgSize = sz; }
                    else if (line.StartsWith("CACACHE_SIZE=")) { long sz; if (long.TryParse(line.Substring(13), out sz)) res.CacacheSize = sz; }
                    else if (line.StartsWith("NPX_SIZE=")) { long sz; if (long.TryParse(line.Substring(9), out sz)) res.NpxSize = sz; }
                    else if (line.StartsWith("LOGS_SIZE=")) { long sz; if (long.TryParse(line.Substring(10), out sz)) res.LogsSize = sz; }
                    else if (line.StartsWith("REGISTRY=")) res.RegistryUrl = UnescapeLine(line.Substring(9));
                    else if (line.StartsWith("NPM_ROOT=")) res.NpmRoot = UnescapeLine(line.Substring(9));
                    else if (line.StartsWith("CACHE_DIR=")) res.CacheDir = UnescapeLine(line.Substring(10));
                    else if (line.StartsWith("NPMRC=")) res.NpmrcPath = UnescapeLine(line.Substring(6));
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
                            res.Packages.Add(item);
                        }
                    }
                }

                lock (npmScanLock)
                {
                    cachedResult = res;
                    cachedRootTicks = rootTicks;
                    cachedCacheTicks = cacheTicks;
                }

                sw.Stop();
                Log(T("log_dev_ecosystem_fast_loaded", "NPM", res.Packages.Count, sw.ElapsedMilliseconds));
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

                template = template.Replace("{NPM_BREADCRUMB}", T("npm_breadcrumb"));
                template = template.Replace("{NPM_PAGE_TITLE}", T("npm_page_title"));
                template = template.Replace("{LOBBY_PROTO_TOGGLE_TITLE}", T("lobby_proto_toggle_title"));
                template = template.Replace("{NPM_SEARCH_PLACEHOLDER}", T("npm_search_placeholder"));
                template = template.Replace("{NPM_BTN_RESCAN}", T("npm_btn_rescan"));
                template = template.Replace("{NPM_STAT_PKGS}", T("npm_stat_pkgs"));
                template = template.Replace("{NPM_STAT_CACACHE}", T("npm_stat_cacache"));
                template = template.Replace("{NPM_STAT_TEMP}", T("npm_stat_temp"));
                template = template.Replace("{NPM_BTN_CONFIG_DETAILS}", T("npm_btn_config_details"));
                template = template.Replace("{NPM_BTN_CLEAN_LOGS}", T("npm_btn_clean_logs"));
                template = template.Replace("{NPM_BTN_CLEAN_NPX}", T("npm_btn_clean_npx"));
                template = template.Replace("{NPM_BTN_OPEN_ROOT}", T("npm_btn_open_root"));
                template = template.Replace("{NPM_TH_NAME}", T("npm_th_name"));
                template = template.Replace("{NPM_TH_VERSION}", T("npm_th_version"));
                template = template.Replace("{NPM_TH_LICENSE}", T("npm_th_license"));
                template = template.Replace("{NPM_TH_BIN}", T("npm_th_bin"));
                template = template.Replace("{NPM_TH_SIZE}", T("npm_th_size"));
                template = template.Replace("{NPM_LOADING}", T("npm_loading"));
                template = template.Replace("{PREVIEW_BTN_EXPAND}", T("preview_btn_expand"));
                template = template.Replace("{NPM_DETAIL_TITLE}", T("npm_detail_title"));
                template = template.Replace("{PREVIEW_BTN_COLLAPSE}", T("preview_btn_collapse"));
                template = template.Replace("{NPM_DETAIL_EMPTY}", T("npm_detail_empty"));
                template = template.Replace("{MODAL_BTN_OK}", T("modal_btn_ok"));
                template = template.Replace("{NPM_MODAL_CONFIG_TITLE}", T("npm_modal_config_title"));
                template = template.Replace("{PAGE_SIZE_LABEL}", T("pagination_page_size"));
                template = template.Replace("{PAGE_FIRST}", T("pagination_first"));
                template = template.Replace("{PAGE_PREV}", T("pagination_prev"));
                template = template.Replace("{PAGE_NEXT}", T("pagination_next"));
                template = template.Replace("{PAGE_LAST}", T("pagination_last"));

                respBody = Encoding.UTF8.GetBytes(template);
            }
            else if (path == "data")
            {
                NpmScanResult res;
                lock (npmScanLock)
                {
                    res = cachedResult;
                }

                if (res == null)
                {
                    respBody = Encoding.UTF8.GetBytes("{\"scanning\":" + (isScanning ? "true" : "false") + ",\"packages\":[],\"totalPkgSize\":0,\"cacacheSize\":0,\"npxSize\":0,\"logsSize\":0,\"registry\":\"\",\"npmRoot\":\"\",\"cacheDir\":\"\",\"npmrc\":\"\"}");
                }
                else
                {
                    StringBuilder sbData = new StringBuilder();
                    sbData.Append("{\"scanning\":").Append(isScanning ? "true" : "false");
                    sbData.Append(",\"totalPkgSize\":").Append(res.TotalPkgSize);
                    sbData.Append(",\"cacacheSize\":").Append(res.CacacheSize);
                    sbData.Append(",\"npxSize\":").Append(res.NpxSize);
                    sbData.Append(",\"logsSize\":").Append(res.LogsSize);
                    sbData.Append(",\"nodeVersion\":\"").Append(EscapeJson(res.NodeVersion)).Append("\"");
                    sbData.Append(",\"nodePath\":\"").Append(EscapeJson(res.NodePath)).Append("\"");
                    sbData.Append(",\"npmVersion\":\"").Append(EscapeJson(res.NpmVersion)).Append("\"");
                    sbData.Append(",\"npmPath\":\"").Append(EscapeJson(res.NpmPath)).Append("\"");
                    sbData.Append(",\"globalPrefix\":\"").Append(EscapeJson(res.GlobalPrefix)).Append("\"");
                    sbData.Append(",\"globalBinDir\":\"").Append(EscapeJson(res.GlobalBinDir)).Append("\"");
                    sbData.Append(",\"registry\":\"").Append(EscapeJson(res.RegistryUrl)).Append("\"");
                    sbData.Append(",\"npmRoot\":\"").Append(EscapeJson(res.NpmRoot)).Append("\"");
                    sbData.Append(",\"cacheDir\":\"").Append(EscapeJson(res.CacheDir)).Append("\"");
                    sbData.Append(",\"logsDir\":\"").Append(EscapeJson(res.LogsDir)).Append("\"");
                    sbData.Append(",\"npxDir\":\"").Append(EscapeJson(res.NpxDir)).Append("\"");
                    sbData.Append(",\"cacacheDir\":\"").Append(EscapeJson(res.CacacheDir)).Append("\"");
                    sbData.Append(",\"npmrc\":\"").Append(EscapeJson(res.NpmrcPath)).Append("\"");
                    sbData.Append(",\"npmrcContent\":\"").Append(EscapeJson(res.NpmrcContent)).Append("\"");

                    sbData.Append(",\"npmrcConfigs\":{");
                    int cfgIdx = 0;
                    foreach (var kvp in res.NpmrcConfigs)
                    {
                        if (cfgIdx++ > 0) sbData.Append(",");
                        sbData.Append("\"").Append(EscapeJson(kvp.Key)).Append("\":\"").Append(EscapeJson(kvp.Value)).Append("\"");
                    }
                    sbData.Append("}");

                    sbData.Append(",\"packages\":[");
                    for (int i = 0; i < res.Packages.Count; i++)
                    {
                        if (i > 0) sbData.Append(",");
                        NpmPackageItem p = res.Packages[i];
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
                        sbData.Append(",\"declaredDependencies\":{");
                        int depIdx = 0;
                        foreach (var d in p.DeclaredDependencies)
                        {
                            if (depIdx++ > 0) sbData.Append(",");
                            sbData.Append("\"").Append(EscapeJson(d.Key)).Append("\":\"").Append(EscapeJson(d.Value)).Append("\"");
                        }
                        sbData.Append("}");
                        sbData.Append(",\"nestedModules\":[");
                        for (int k = 0; k < p.NestedModules.Count; k++)
                        {
                            if (k > 0) sbData.Append(",");
                            NpmSubModuleItem sub = p.NestedModules[k];
                            sbData.Append("{");
                            sbData.Append("\"name\":\"").Append(EscapeJson(sub.Name)).Append("\"");
                            sbData.Append(",\"version\":\"").Append(EscapeJson(sub.Version)).Append("\"");
                            sbData.Append(",\"installPath\":\"").Append(EscapeJson(sub.InstallPath)).Append("\"");
                            sbData.Append(",\"size\":").Append(sub.Size);
                            sbData.Append("}");
                        }
                        sbData.Append("]");
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
                else if (Directory.Exists(p) || File.Exists(p))
                {
                    Process.Start("explorer.exe", Directory.Exists(p) ? p : ("/select,\"" + p + "\""));
                    respBody = Encoding.UTF8.GetBytes("{\"success\":true}");
                }
                else
                {
                    status = 404;
                    respBody = Encoding.UTF8.GetBytes("{\"success\":false,\"message\":\"" + EscapeJson(T("api_path_not_found")) + "\"}");
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
                    string targetDir = Directory.Exists(p) ? p : Path.GetDirectoryName(p);
                    if (!string.IsNullOrEmpty(targetDir) && Directory.Exists(targetDir))
                    {
                        ProcessStartInfo psi = new ProcessStartInfo
                        {
                            FileName = "powershell.exe",
                            WorkingDirectory = targetDir,
                            UseShellExecute = true
                        };
                        Process.Start(psi);
                        respBody = Encoding.UTF8.GetBytes("{\"success\":true}");
                    }
                    else
                    {
                        status = 404;
                        respBody = Encoding.UTF8.GetBytes("{\"success\":false,\"message\":\"" + EscapeJson(T("api_path_not_found")) + "\"}");
                    }
                }
            }
            else if (path == "refresh")
            {
                TriggerNpmScanAsync(true);
                respBody = Encoding.UTF8.GetBytes("{\"success\":true,\"message\":\"" + EscapeJson(T("api_gradle_scan_started")) + "\"}");
            }
            else if (path == "clean-logs")
            {
                string cacheDir = GetDefaultNpmCacheDir();
                string logsDir = Path.Combine(cacheDir, "_logs");
                if (Directory.Exists(logsDir))
                {
                    Directory.Delete(logsDir, true);
                    TriggerNpmScanAsync(true);
                }
                respBody = Encoding.UTF8.GetBytes("{\"success\":true,\"message\":\"" + EscapeJson(T("npm_clean_success")) + "\"}");
            }
            else if (path == "clean-npx")
            {
                string cacheDir = GetDefaultNpmCacheDir();
                string npxDir = Path.Combine(cacheDir, "_npx");
                if (Directory.Exists(npxDir))
                {
                    Directory.Delete(npxDir, true);
                    TriggerNpmScanAsync(true);
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
                        string content = File.ReadAllText(pkgJson, Encoding.UTF8);
                        respBody = Encoding.UTF8.GetBytes("{\"success\":true,\"content\":\"" + EscapeJson(content) + "\"}");
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
