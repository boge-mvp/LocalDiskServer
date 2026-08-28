using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace LocalDiskServer
{
    public class NpmSubModuleItem
    {
        public string Name { get; set; }
        public string Version { get; set; }
        public string InstallPath { get; set; }
        public long Size { get; set; }

        public NpmSubModuleItem()
        {
            Name = "";
            Version = "";
            InstallPath = "";
        }
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
        public string NpmRoot { get; set; }
        public string CacheDir { get; set; }
        public string LogsDir { get; set; }
        public string NpxDir { get; set; }
        public string CacacheDir { get; set; }
        public string RegistryUrl { get; set; }
        public string NpmrcPath { get; set; }
        public string NpmrcContent { get; set; }
        public Dictionary<string, string> NpmrcConfigs { get; set; }

        public NpmScanResult()
        {
            Packages = new List<NpmPackageItem>();
            NodeVersion = "";
            NodePath = "";
            NpmVersion = "";
            NpmPath = "";
            GlobalPrefix = "";
            GlobalBinDir = "";
            NpmRoot = "";
            CacheDir = "";
            LogsDir = "";
            NpxDir = "";
            CacacheDir = "";
            RegistryUrl = "";
            NpmrcPath = "";
            NpmrcContent = "";
            NpmrcConfigs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    public static class NpmExplorer
    {
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
            Logger.Log(I18nManager.T("log_dev_ecosystem_released"));
        }

        public static void TriggerNpmScanAsync(bool forceRescan = false)
        {
            if (!ServerApplicationContext.enable_dev_ecosystem) return;

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
                                Logger.Log(I18nManager.T("log_dev_ecosystem_verified"));
                                return;
                            }
                        }
                    }

                    Logger.Log(I18nManager.T("log_npm_scan_started"));
                    NpmScanResult res = DoNpmScan(npmRoot, cacheDir);

                    lock (npmScanLock)
                    {
                        cachedResult = res;
                        cachedRootTicks = curRootTicks;
                        cachedCacheTicks = curCacheTicks;
                    }

                    SaveToDiskCache(curRootTicks, curCacheTicks);
                    Logger.Log(I18nManager.T("log_npm_scan_finished", res.Packages.Count, FormatSize(res.CacacheSize + res.NpxSize + res.LogsSize)));
                }
                catch (Exception ex)
                {
                    Logger.Log("Npm scan error: " + ex.Message);
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
                Logger.Log(I18nManager.T("log_dev_ecosystem_saved", cacheFile));
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
                Logger.Log(I18nManager.T("log_dev_ecosystem_fast_loaded", "NPM", res.Packages.Count, sw.ElapsedMilliseconds));
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

        public static void ServeNpmDashboard(HttpListenerResponse response)
        {
            if (!ServerApplicationContext.enable_dev_ecosystem)
            {
                response.Redirect("/");
                response.OutputStream.Close();
                return;
            }

            if (cachedResult == null && !isScanning)
            {
                TriggerNpmScanAsync();
            }

            string template = HttpServer.LoadResource("npm.html");
            if (string.IsNullOrEmpty(template))
            {
                HttpServer.ServeError(response, 500, I18nManager.T("err_internal", "npm.html not found"));
                return;
            }

            string activePath = "/npm";
            string currentLocale = I18nManager.CurrentLanguage;

            StringBuilder sb = new StringBuilder();
            sb.Append(HttpServer.GetHtmlHeader(I18nManager.T("npm_page_title"), activePath, "layout-explorer"));
            sb.Append("<script>const currentView = 'npm';</script>");
            sb.Append(FileExplorer.RenderSidebar(activePath, currentLocale));

            // 多语言占位符
            template = template.Replace("{NPM_BREADCRUMB}", I18nManager.T("npm_breadcrumb"));
            template = template.Replace("{NPM_PAGE_TITLE}", I18nManager.T("npm_page_title"));
            template = template.Replace("{LOBBY_PROTO_TOGGLE_TITLE}", I18nManager.T("lobby_proto_toggle_title"));
            template = template.Replace("{NPM_STAT_PKGS}", I18nManager.T("npm_stat_pkgs"));
            template = template.Replace("{NPM_STAT_CACACHE}", I18nManager.T("npm_stat_cacache"));
            template = template.Replace("{NPM_STAT_TEMP}", I18nManager.T("npm_stat_temp"));
            template = template.Replace("{NPM_STAT_ENV}", I18nManager.T("npm_stat_env"));
            template = template.Replace("{NPM_SEARCH_PLACEHOLDER}", I18nManager.T("npm_search_placeholder"));
            template = template.Replace("{NPM_BTN_RESCAN}", I18nManager.T("npm_btn_rescan"));
            template = template.Replace("{NPM_BTN_CONFIG_DETAILS}", I18nManager.T("npm_btn_config_details"));
            template = template.Replace("{NPM_BTN_CLEAN_LOGS}", I18nManager.T("npm_btn_clean_logs"));
            template = template.Replace("{NPM_BTN_CLEAN_NPX}", I18nManager.T("npm_btn_clean_npx"));
            template = template.Replace("{NPM_BTN_OPEN_NPMRC}", I18nManager.T("npm_btn_open_npmrc"));
            template = template.Replace("{NPM_BTN_OPEN_ROOT}", I18nManager.T("npm_btn_open_root"));
            template = template.Replace("{NPM_TH_NAME}", I18nManager.T("npm_th_name"));
            template = template.Replace("{NPM_TH_VERSION}", I18nManager.T("npm_th_version"));
            template = template.Replace("{NPM_TH_LICENSE}", I18nManager.T("npm_th_license"));
            template = template.Replace("{NPM_TH_BIN}", I18nManager.T("npm_th_bin"));
            template = template.Replace("{NPM_TH_SIZE}", I18nManager.T("npm_th_size"));
            template = template.Replace("{PREVIEW_BTN_EXPAND}", I18nManager.T("preview_btn_expand"));
            template = template.Replace("{PREVIEW_BTN_COLLAPSE}", I18nManager.T("preview_btn_collapse"));
            template = template.Replace("{NPM_DETAIL_TITLE}", I18nManager.T("npm_detail_title"));
            template = template.Replace("{NPM_DETAIL_EMPTY}", I18nManager.T("npm_detail_empty"));
            template = template.Replace("{NPM_LOADING}", I18nManager.T("npm_loading"));
            template = template.Replace("{MODAL_BTN_OK}", I18nManager.T("modal_btn_ok"));
            template = template.Replace("{NPM_MODAL_CONFIG_TITLE}", I18nManager.T("npm_modal_config_title"));
            template = template.Replace("{NPM_CFG_SEC_RUNTIME}", I18nManager.T("npm_cfg_sec_runtime"));
            template = template.Replace("{NPM_CFG_SEC_PATHS}", I18nManager.T("npm_cfg_sec_paths"));
            template = template.Replace("{NPM_CFG_SEC_NPMRC}", I18nManager.T("npm_cfg_sec_npmrc"));
            template = template.Replace("{PAGE_SIZE_LABEL}", I18nManager.T("pagination_page_size"));
            template = template.Replace("{PAGE_FIRST}", I18nManager.T("pagination_first"));
            template = template.Replace("{PAGE_PREV}", I18nManager.T("pagination_prev"));
            template = template.Replace("{PAGE_NEXT}", I18nManager.T("pagination_next"));
            template = template.Replace("{PAGE_LAST}", I18nManager.T("pagination_last"));

            sb.Append(template);
            sb.Append(HttpServer.GetHtmlFooter());

            byte[] buffer = Encoding.UTF8.GetBytes(sb.ToString());
            response.ContentType = "text/html; charset=utf-8";
            response.ContentLength64 = buffer.Length;
            response.OutputStream.Write(buffer, 0, buffer.Length);
            response.OutputStream.Close();
        }

        public static bool HandleApi(string rawPath, HttpListenerRequest request, HttpListenerResponse response)
        {
            if (!rawPath.StartsWith("api/npm/", StringComparison.OrdinalIgnoreCase)) return false;

            if (!ServerApplicationContext.enable_dev_ecosystem)
            {
                HttpServer.ServeJson(response, 403, "{\"success\":false,\"message\":\"" + HttpServer.EscapeJson(I18nManager.T("err_dev_ecosystem_disabled")) + "\"}");
                return true;
            }

            string subPath = rawPath.Substring(8).ToLower();

            if (subPath == "data")
            {
                NpmScanResult res;
                lock (npmScanLock)
                {
                    res = cachedResult;
                }

                if (res == null)
                {
                    HttpServer.ServeJson(response, 200, "{\"scanning\":" + (isScanning ? "true" : "false") + ",\"packages\":[],\"totalPkgSize\":0,\"cacacheSize\":0,\"npxSize\":0,\"logsSize\":0,\"registry\":\"\",\"npmRoot\":\"\",\"cacheDir\":\"\",\"npmrc\":\"\"}");
                    return true;
                }

                StringBuilder sb = new StringBuilder();
                sb.Append("{\"scanning\":").Append(isScanning ? "true" : "false");
                sb.Append(",\"totalPkgSize\":").Append(res.TotalPkgSize);
                sb.Append(",\"cacacheSize\":").Append(res.CacacheSize);
                sb.Append(",\"npxSize\":").Append(res.NpxSize);
                sb.Append(",\"logsSize\":").Append(res.LogsSize);
                sb.Append(",\"nodeVersion\":\"").Append(HttpServer.EscapeJson(res.NodeVersion)).Append("\"");
                sb.Append(",\"nodePath\":\"").Append(HttpServer.EscapeJson(res.NodePath)).Append("\"");
                sb.Append(",\"npmVersion\":\"").Append(HttpServer.EscapeJson(res.NpmVersion)).Append("\"");
                sb.Append(",\"npmPath\":\"").Append(HttpServer.EscapeJson(res.NpmPath)).Append("\"");
                sb.Append(",\"globalPrefix\":\"").Append(HttpServer.EscapeJson(res.GlobalPrefix)).Append("\"");
                sb.Append(",\"globalBinDir\":\"").Append(HttpServer.EscapeJson(res.GlobalBinDir)).Append("\"");
                sb.Append(",\"registry\":\"").Append(HttpServer.EscapeJson(res.RegistryUrl)).Append("\"");
                sb.Append(",\"npmRoot\":\"").Append(HttpServer.EscapeJson(res.NpmRoot)).Append("\"");
                sb.Append(",\"cacheDir\":\"").Append(HttpServer.EscapeJson(res.CacheDir)).Append("\"");
                sb.Append(",\"logsDir\":\"").Append(HttpServer.EscapeJson(res.LogsDir)).Append("\"");
                sb.Append(",\"npxDir\":\"").Append(HttpServer.EscapeJson(res.NpxDir)).Append("\"");
                sb.Append(",\"cacacheDir\":\"").Append(HttpServer.EscapeJson(res.CacacheDir)).Append("\"");
                sb.Append(",\"npmrc\":\"").Append(HttpServer.EscapeJson(res.NpmrcPath)).Append("\"");
                sb.Append(",\"npmrcContent\":\"").Append(HttpServer.EscapeJson(res.NpmrcContent)).Append("\"");
                
                sb.Append(",\"npmrcConfigs\":{");
                int cfgIdx = 0;
                foreach (var kvp in res.NpmrcConfigs)
                {
                    if (cfgIdx++ > 0) sb.Append(",");
                    sb.Append("\"").Append(HttpServer.EscapeJson(kvp.Key)).Append("\":\"").Append(HttpServer.EscapeJson(kvp.Value)).Append("\"");
                }
                sb.Append("}");

                sb.Append(",\"packages\":[");
                for (int i = 0; i < res.Packages.Count; i++)
                {
                    if (i > 0) sb.Append(",");
                    NpmPackageItem p = res.Packages[i];
                    sb.Append("{");
                    sb.Append("\"name\":\"").Append(HttpServer.EscapeJson(p.Name)).Append("\"");
                    sb.Append(",\"version\":\"").Append(HttpServer.EscapeJson(p.Version)).Append("\"");
                    sb.Append(",\"description\":\"").Append(HttpServer.EscapeJson(p.Description)).Append("\"");
                    sb.Append(",\"license\":\"").Append(HttpServer.EscapeJson(p.License)).Append("\"");
                    sb.Append(",\"author\":\"").Append(HttpServer.EscapeJson(p.Author)).Append("\"");
                    sb.Append(",\"homepage\":\"").Append(HttpServer.EscapeJson(p.Homepage)).Append("\"");
                    sb.Append(",\"bin\":\"").Append(HttpServer.EscapeJson(p.Bin)).Append("\"");
                    sb.Append(",\"size\":").Append(p.Size);
                    sb.Append(",\"lastModified\":\"").Append(HttpServer.EscapeJson(p.LastModified.ToString("yyyy-MM-dd HH:mm"))).Append("\"");
                    sb.Append(",\"installPath\":\"").Append(HttpServer.EscapeJson(p.InstallPath)).Append("\"");
                    sb.Append(",\"depsCount\":").Append(p.DepsCount);
                    sb.Append(",\"declaredDependencies\":{");
                    int depIdx = 0;
                    foreach (var d in p.DeclaredDependencies)
                    {
                        if (depIdx++ > 0) sb.Append(",");
                        sb.Append("\"").Append(HttpServer.EscapeJson(d.Key)).Append("\":\"").Append(HttpServer.EscapeJson(d.Value)).Append("\"");
                    }
                    sb.Append("}");
                    sb.Append(",\"nestedModules\":[");
                    for (int k = 0; k < p.NestedModules.Count; k++)
                    {
                        if (k > 0) sb.Append(",");
                        NpmSubModuleItem sub = p.NestedModules[k];
                        sb.Append("{");
                        sb.Append("\"name\":\"").Append(HttpServer.EscapeJson(sub.Name)).Append("\"");
                        sb.Append(",\"version\":\"").Append(HttpServer.EscapeJson(sub.Version)).Append("\"");
                        sb.Append(",\"installPath\":\"").Append(HttpServer.EscapeJson(sub.InstallPath)).Append("\"");
                        sb.Append(",\"size\":").Append(sub.Size);
                        sb.Append("}");
                    }
                    sb.Append("]");
                    sb.Append("}");
                }
                sb.Append("]}");

                HttpServer.ServeJson(response, 200, sb.ToString());
                return true;
            }
            else if (subPath == "open-path")
            {
                string path = request.QueryString["path"];
                if (string.IsNullOrEmpty(path))
                {
                    HttpServer.ServeJson(response, 400, "{\"success\":false,\"message\":\"" + HttpServer.EscapeJson(I18nManager.T("api_missing_path")) + "\"}");
                    return true;
                }

                if (Directory.Exists(path) || File.Exists(path))
                {
                    try
                    {
                        Process.Start("explorer.exe", Directory.Exists(path) ? path : ("/select,\"" + path + "\""));
                        Logger.Log(I18nManager.T("log_locate_in_explorer", path));
                        HttpServer.ServeJson(response, 200, "{\"success\":true}");
                    }
                    catch (Exception ex)
                    {
                        HttpServer.ServeJson(response, 500, "{\"success\":false,\"message\":\"" + HttpServer.EscapeJson(ex.Message) + "\"}");
                    }
                }
                else
                {
                    HttpServer.ServeJson(response, 404, "{\"success\":false,\"message\":\"" + HttpServer.EscapeJson(I18nManager.T("api_path_not_found")) + "\"}");
                }
                return true;
            }
            else if (subPath == "terminal")
            {
                string path = request.QueryString["path"];
                if (string.IsNullOrEmpty(path))
                {
                    HttpServer.ServeJson(response, 400, "{\"success\":false,\"message\":\"" + HttpServer.EscapeJson(I18nManager.T("api_missing_path")) + "\"}");
                    return true;
                }

                string targetDir = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(targetDir) && Directory.Exists(targetDir))
                {
                    try
                    {
                        ProcessStartInfo psi = new ProcessStartInfo
                        {
                            FileName = "powershell.exe",
                            WorkingDirectory = targetDir,
                            UseShellExecute = true
                        };
                        Process.Start(psi);
                        Logger.Log(I18nManager.T("log_open_terminal", "PowerShell", targetDir));
                        HttpServer.ServeJson(response, 200, "{\"success\":true}");
                    }
                    catch (Exception ex)
                    {
                        HttpServer.ServeJson(response, 500, "{\"success\":false,\"message\":\"" + HttpServer.EscapeJson(ex.Message) + "\"}");
                    }
                }
                else
                {
                    HttpServer.ServeJson(response, 404, "{\"success\":false,\"message\":\"" + HttpServer.EscapeJson(I18nManager.T("api_path_not_found")) + "\"}");
                }
                return true;
            }
            else if (subPath == "refresh")
            {
                TriggerNpmScanAsync(true);
                HttpServer.ServeJson(response, 200, "{\"success\":true,\"message\":\"" + HttpServer.EscapeJson(I18nManager.T("api_gradle_scan_started")) + "\"}");
                return true;
            }
            else if (subPath == "clean-logs")
            {
                string cacheDir = GetDefaultNpmCacheDir();
                string logsDir = Path.Combine(cacheDir, "_logs");
                if (Directory.Exists(logsDir))
                {
                    try
                    {
                        Directory.Delete(logsDir, true);
                        Logger.Log(I18nManager.T("log_npm_clean_logs", logsDir));
                        TriggerNpmScanAsync(true);
                        HttpServer.ServeJson(response, 200, "{\"success\":true,\"message\":\"" + HttpServer.EscapeJson(I18nManager.T("npm_clean_success")) + "\"}");
                    }
                    catch (Exception ex)
                    {
                        HttpServer.ServeJson(response, 500, "{\"success\":false,\"message\":\"" + HttpServer.EscapeJson(I18nManager.T("npm_clean_fail", ex.Message)) + "\"}");
                    }
                }
                else
                {
                    HttpServer.ServeJson(response, 200, "{\"success\":true,\"message\":\"" + HttpServer.EscapeJson(I18nManager.T("npm_clean_success")) + "\"}");
                }
                return true;
            }
            else if (subPath == "clean-npx")
            {
                string cacheDir = GetDefaultNpmCacheDir();
                string npxDir = Path.Combine(cacheDir, "_npx");
                if (Directory.Exists(npxDir))
                {
                    try
                    {
                        Directory.Delete(npxDir, true);
                        Logger.Log(I18nManager.T("log_npm_clean_npx", npxDir));
                        TriggerNpmScanAsync(true);
                        HttpServer.ServeJson(response, 200, "{\"success\":true,\"message\":\"" + HttpServer.EscapeJson(I18nManager.T("npm_clean_success")) + "\"}");
                    }
                    catch (Exception ex)
                    {
                        HttpServer.ServeJson(response, 500, "{\"success\":false,\"message\":\"" + HttpServer.EscapeJson(I18nManager.T("npm_clean_fail", ex.Message)) + "\"}");
                    }
                }
                else
                {
                    HttpServer.ServeJson(response, 200, "{\"success\":true,\"message\":\"" + HttpServer.EscapeJson(I18nManager.T("npm_clean_success")) + "\"}");
                }
                return true;
            }
            else if (subPath == "pkg-json")
            {
                string path = request.QueryString["path"];
                if (string.IsNullOrEmpty(path))
                {
                    HttpServer.ServeJson(response, 400, "{\"success\":false,\"message\":\"" + HttpServer.EscapeJson(I18nManager.T("api_missing_path")) + "\"}");
                    return true;
                }
                string pkgJson = Path.Combine(path, "package.json");
                if (File.Exists(pkgJson))
                {
                    try
                    {
                        string content = File.ReadAllText(pkgJson, Encoding.UTF8);
                        HttpServer.ServeJson(response, 200, "{\"success\":true,\"content\":\"" + HttpServer.EscapeJson(content) + "\"}");
                    }
                    catch (Exception ex)
                    {
                        HttpServer.ServeJson(response, 500, "{\"success\":false,\"message\":\"" + HttpServer.EscapeJson(ex.Message) + "\"}");
                    }
                }
                else
                {
                    HttpServer.ServeJson(response, 404, "{\"success\":false,\"message\":\"" + HttpServer.EscapeJson(I18nManager.T("api_file_not_found")) + "\"}");
                }
                return true;
            }

            return false;
        }
    }
}
