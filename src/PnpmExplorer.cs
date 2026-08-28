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
    public class PnpmStorePackageItem
    {
        public string Name { get; set; }
        public string Version { get; set; }
        public int FileCount { get; set; }
        public long Size { get; set; }
        public DateTime LastModified { get; set; }
        public string IndexFilePath { get; set; }
        public string Hash { get; set; }
        public Dictionary<string, string> Dependencies { get; set; }
        public int DepsCount { get; set; }

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
        public bool IsActive { get; set; }
        public List<PnpmStorePackageItem> Packages { get; set; }

        public PnpmDiskStoreItem()
        {
            DriveLetter = "";
            StorePath = "";
            StoreVersion = "v3";
            IsActive = true;
            Packages = new List<PnpmStorePackageItem>();
        }
    }

    public class PnpmScanResult
    {
        public List<PnpmDiskStoreItem> Stores { get; set; }
        public List<NpmPackageItem> GlobalPackages { get; set; }
        public long MetadataSize { get; set; }
        public long DlxSize { get; set; }
        public long TotalStoreSize { get; set; }
        public long TotalGlobalPkgSize { get; set; }
        public string PnpmVersion { get; set; }
        public string PnpmPath { get; set; }
        public string NodeVersion { get; set; }
        public string NodePath { get; set; }
        public string GlobalBinDir { get; set; }
        public string GlobalModulesDir { get; set; }
        public string StateDir { get; set; }
        public string StoreDirConfig { get; set; }
        public string CacheDir { get; set; }
        public string NpmrcPath { get; set; }
        public string NpmrcContent { get; set; }
        public Dictionary<string, string> NpmrcConfigs { get; set; }

        public PnpmScanResult()
        {
            Stores = new List<PnpmDiskStoreItem>();
            GlobalPackages = new List<NpmPackageItem>();
            PnpmVersion = "";
            PnpmPath = "";
            NodeVersion = "";
            NodePath = "";
            GlobalBinDir = "";
            GlobalModulesDir = "";
            StateDir = "";
            StoreDirConfig = "";
            CacheDir = "";
            NpmrcPath = "";
            NpmrcContent = "";
            NpmrcConfigs = new Dictionary<string, string>();
        }
    }

    public static class PnpmExplorer
    {
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
            Logger.Log(I18nManager.T("log_dev_ecosystem_released"));
        }

        public static void TriggerPnpmScanAsync(bool forceRescan = false)
        {
            if (!ServerApplicationContext.enable_dev_ecosystem) return;

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
                                Logger.Log(I18nManager.T("log_dev_ecosystem_verified"));
                                return;
                            }
                        }
                    }

                    Logger.Log(I18nManager.T("log_pnpm_scan_started"));
                    PnpmScanResult res = DoPnpmScan(localCache);

                    lock (pnpmScanLock)
                    {
                        cachedResult = res;
                        cachedStoreTicks = curStoreTicks;
                        cachedCacheTicks = curCacheTicks;
                    }

                    SaveToDiskCache(curStoreTicks, curCacheTicks);
                    Logger.Log(I18nManager.T("log_pnpm_scan_finished", res.Stores.Count, res.GlobalPackages.Count, FormatSize(res.TotalStoreSize + res.DlxSize + res.MetadataSize)));
                }
                catch (Exception ex)
                {
                    Logger.Log("Pnpm scan error: " + ex.Message);
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
            string nodeVer = NpmExplorer.DetectNodeVersion(out nodePath);
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
                Logger.Log(I18nManager.T("log_dev_ecosystem_saved", cacheFile));
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
                Logger.Log(I18nManager.T("log_dev_ecosystem_fast_loaded", "PNPM", res.Stores.Count, sw.ElapsedMilliseconds));
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

        public static void ServePnpmDashboard(HttpListenerResponse response)
        {
            if (!ServerApplicationContext.enable_dev_ecosystem)
            {
                response.Redirect("/");
                response.OutputStream.Close();
                return;
            }

            if (cachedResult == null && !isScanning)
            {
                TriggerPnpmScanAsync();
            }

            string template = HttpServer.LoadResource("pnpm.html");
            if (string.IsNullOrEmpty(template))
            {
                HttpServer.ServeError(response, 500, I18nManager.T("err_internal", "pnpm.html not found"));
                return;
            }

            string activePath = "/pnpm";
            string currentLocale = I18nManager.CurrentLanguage;

            StringBuilder sb = new StringBuilder();
            sb.Append(HttpServer.GetHtmlHeader(I18nManager.T("pnpm_page_title"), activePath, "layout-explorer"));
            sb.Append("<script>const currentView = 'pnpm';</script>");
            sb.Append(FileExplorer.RenderSidebar(activePath, currentLocale));

            // 多语言占位符
            template = template.Replace("{PNPM_BREADCRUMB}", I18nManager.T("pnpm_breadcrumb"));
            template = template.Replace("{PNPM_PAGE_TITLE}", I18nManager.T("pnpm_page_title"));
            template = template.Replace("{LOBBY_PROTO_TOGGLE_TITLE}", I18nManager.T("lobby_proto_toggle_title"));
            template = template.Replace("{PNPM_SEC_STORES}", I18nManager.T("pnpm_sec_stores"));
            template = template.Replace("{PNPM_SEC_GLOBAL}", I18nManager.T("pnpm_sec_global"));
            template = template.Replace("{PNPM_SEC_CACHE}", I18nManager.T("pnpm_sec_cache"));
            template = template.Replace("{PNPM_BTN_RESCAN}", I18nManager.T("npm_btn_rescan"));
            template = template.Replace("{PNPM_BTN_CLEAN_DLX}", I18nManager.T("pnpm_btn_clean_dlx"));
            template = template.Replace("{PNPM_BTN_CONFIG_DETAILS}", I18nManager.T("pnpm_btn_config_details"));
            template = template.Replace("{PNPM_MODAL_CONFIG_TITLE}", I18nManager.T("pnpm_modal_config_title"));
            template = template.Replace("{PNPM_CFG_SEC_RUNTIME}", I18nManager.T("pnpm_cfg_sec_runtime"));
            template = template.Replace("{PNPM_CFG_SEC_PATHS}", I18nManager.T("pnpm_cfg_sec_paths"));
            template = template.Replace("{PNPM_CFG_SEC_STORES}", I18nManager.T("pnpm_cfg_sec_stores"));
            template = template.Replace("{PNPM_CFG_SEC_NPMRC}", I18nManager.T("pnpm_cfg_sec_npmrc"));
            template = template.Replace("{PREVIEW_BTN_EXPAND}", I18nManager.T("preview_btn_expand"));
            template = template.Replace("{PREVIEW_BTN_COLLAPSE}", I18nManager.T("preview_btn_collapse"));
            template = template.Replace("{PNPM_DETAIL_TITLE}", I18nManager.T("pnpm_detail_title"));
            template = template.Replace("{PNPM_DETAIL_EMPTY}", I18nManager.T("pnpm_detail_empty"));
            template = template.Replace("{NPM_TH_NAME}", I18nManager.T("npm_th_name"));
            template = template.Replace("{NPM_TH_VERSION}", I18nManager.T("npm_th_version"));
            template = template.Replace("{NPM_TH_LICENSE}", I18nManager.T("npm_th_license"));
            template = template.Replace("{NPM_TH_BIN}", I18nManager.T("npm_th_bin"));
            template = template.Replace("{NPM_TH_SIZE}", I18nManager.T("npm_th_size"));
            template = template.Replace("{PNPM_TH_FILE_COUNT}", I18nManager.T("pnpm_th_file_count"));
            template = template.Replace("{PAGE_SIZE_LABEL}", I18nManager.T("npm_page_size_label"));
            template = template.Replace("{PAGE_FIRST}", I18nManager.T("npm_page_first"));
            template = template.Replace("{PAGE_PREV}", I18nManager.T("npm_page_prev"));
            template = template.Replace("{PAGE_NEXT}", I18nManager.T("npm_page_next"));
            template = template.Replace("{PAGE_LAST}", I18nManager.T("npm_page_last"));
            template = template.Replace("{NPM_SEARCH_PLACEHOLDER}", I18nManager.T("pnpm_search_placeholder"));
            template = template.Replace("{PNPM_LOADING}", I18nManager.T("pnpm_loading"));
            template = template.Replace("{MODAL_BTN_OK}", I18nManager.T("modal_btn_ok"));

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
            if (!rawPath.StartsWith("api/pnpm/", StringComparison.OrdinalIgnoreCase)) return false;

            if (!ServerApplicationContext.enable_dev_ecosystem)
            {
                HttpServer.ServeJson(response, 403, "{\"success\":false,\"message\":\"" + HttpServer.EscapeJson(I18nManager.T("err_dev_ecosystem_disabled")) + "\"}");
                return true;
            }

            string subPath = rawPath.Substring(9).ToLower();

            if (subPath == "data")
            {
                PnpmScanResult res;
                lock (pnpmScanLock)
                {
                    res = cachedResult;
                }

                if (res == null)
                {
                    HttpServer.ServeJson(response, 200, "{\"scanning\":" + (isScanning ? "true" : "false") + ",\"stores\":[],\"globalPackages\":[],\"totalStoreSize\":0,\"metadataSize\":0,\"dlxSize\":0,\"totalGlobalPkgSize\":0}");
                    return true;
                }

                StringBuilder sb = new StringBuilder();
                sb.Append("{\"scanning\":").Append(isScanning ? "true" : "false");
                sb.Append(",\"pnpmVersion\":\"").Append(HttpServer.EscapeJson(res.PnpmVersion)).Append("\"");
                sb.Append(",\"pnpmPath\":\"").Append(HttpServer.EscapeJson(res.PnpmPath)).Append("\"");
                sb.Append(",\"nodeVersion\":\"").Append(HttpServer.EscapeJson(res.NodeVersion)).Append("\"");
                sb.Append(",\"nodePath\":\"").Append(HttpServer.EscapeJson(res.NodePath)).Append("\"");
                sb.Append(",\"globalBinDir\":\"").Append(HttpServer.EscapeJson(res.GlobalBinDir)).Append("\"");
                sb.Append(",\"globalModulesDir\":\"").Append(HttpServer.EscapeJson(res.GlobalModulesDir)).Append("\"");
                sb.Append(",\"stateDir\":\"").Append(HttpServer.EscapeJson(res.StateDir)).Append("\"");
                sb.Append(",\"npmrcPath\":\"").Append(HttpServer.EscapeJson(res.NpmrcPath)).Append("\"");
                sb.Append(",\"npmrcContent\":\"").Append(HttpServer.EscapeJson(res.NpmrcContent)).Append("\"");
                sb.Append(",\"npmrcConfigs\":{");
                int cfgCount = 0;
                foreach (var kv in res.NpmrcConfigs)
                {
                    if (cfgCount > 0) sb.Append(",");
                    sb.Append("\"").Append(HttpServer.EscapeJson(kv.Key)).Append("\":\"").Append(HttpServer.EscapeJson(kv.Value)).Append("\"");
                    cfgCount++;
                }
                sb.Append("}");
                sb.Append(",\"totalStoreSize\":").Append(res.TotalStoreSize);
                sb.Append(",\"metadataSize\":").Append(res.MetadataSize);
                sb.Append(",\"dlxSize\":").Append(res.DlxSize);
                sb.Append(",\"totalGlobalPkgSize\":").Append(res.TotalGlobalPkgSize);
                sb.Append(",\"cacheDir\":\"").Append(HttpServer.EscapeJson(res.CacheDir)).Append("\"");
                sb.Append(",\"stores\":[");

                for (int i = 0; i < res.Stores.Count; i++)
                {
                    if (i > 0) sb.Append(",");
                    PnpmDiskStoreItem s = res.Stores[i];
                    sb.Append("{");
                    sb.Append("\"driveLetter\":\"").Append(HttpServer.EscapeJson(s.DriveLetter)).Append("\"");
                    sb.Append(",\"storePath\":\"").Append(HttpServer.EscapeJson(s.StorePath)).Append("\"");
                    sb.Append(",\"storeVersion\":\"").Append(HttpServer.EscapeJson(s.StoreVersion)).Append("\"");
                    sb.Append(",\"fileCount\":").Append(s.FileCount);
                    sb.Append(",\"size\":").Append(s.Size);
                    sb.Append(",\"lastModified\":\"").Append(HttpServer.EscapeJson(s.LastModified.ToString("yyyy-MM-dd HH:mm"))).Append("\"");
                    sb.Append(",\"packages\":[");
                    for (int j = 0; j < s.Packages.Count; j++)
                    {
                        if (j > 0) sb.Append(",");
                        PnpmStorePackageItem pkg = s.Packages[j];
                        sb.Append("{");
                        sb.Append("\"name\":\"").Append(HttpServer.EscapeJson(pkg.Name)).Append("\"");
                        sb.Append(",\"version\":\"").Append(HttpServer.EscapeJson(pkg.Version)).Append("\"");
                        sb.Append(",\"fileCount\":").Append(pkg.FileCount);
                        sb.Append(",\"size\":").Append(pkg.Size);
                        sb.Append(",\"lastModified\":\"").Append(HttpServer.EscapeJson(pkg.LastModified.ToString("yyyy-MM-dd HH:mm"))).Append("\"");
                        sb.Append(",\"indexFilePath\":\"").Append(HttpServer.EscapeJson(pkg.IndexFilePath)).Append("\"");
                        sb.Append(",\"hash\":\"").Append(HttpServer.EscapeJson(pkg.Hash)).Append("\"");
                        sb.Append(",\"depsCount\":").Append(pkg.DepsCount);
                        sb.Append(",\"dependencies\":{");
                        int pkgDepCount = 0;
                        foreach (var kv in pkg.Dependencies)
                        {
                            if (pkgDepCount > 0) sb.Append(",");
                            sb.Append("\"").Append(HttpServer.EscapeJson(kv.Key)).Append("\":\"").Append(HttpServer.EscapeJson(kv.Value ?? "")).Append("\"");
                            pkgDepCount++;
                        }
                        sb.Append("}");
                        sb.Append("}");
                    }
                    sb.Append("]");
                    sb.Append("}");
                }
                sb.Append("],\"globalPackages\":[");

                for (int i = 0; i < res.GlobalPackages.Count; i++)
                {
                    if (i > 0) sb.Append(",");
                    NpmPackageItem p = res.GlobalPackages[i];
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
                try
                {
                    if (File.Exists(path))
                    {
                        Process.Start("explorer.exe", "/select,\"" + path + "\"");
                    }
                    else if (Directory.Exists(path))
                    {
                        Process.Start("explorer.exe", "\"" + path + "\"");
                    }
                    else
                    {
                        HttpServer.ServeJson(response, 404, "{\"success\":false,\"message\":\"" + HttpServer.EscapeJson(I18nManager.T("api_path_not_found")) + "\"}");
                        return true;
                    }
                    HttpServer.ServeJson(response, 200, "{\"success\":true}");
                }
                catch (Exception ex)
                {
                    HttpServer.ServeJson(response, 500, "{\"success\":false,\"message\":\"" + HttpServer.EscapeJson(ex.Message) + "\"}");
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
                try
                {
                    string targetDir = path;
                    if (File.Exists(path)) targetDir = Path.GetDirectoryName(path);
                    if (!Directory.Exists(targetDir))
                    {
                        HttpServer.ServeJson(response, 404, "{\"success\":false,\"message\":\"" + HttpServer.EscapeJson(I18nManager.T("api_path_not_found")) + "\"}");
                        return true;
                    }
                    ProcessStartInfo psi = new ProcessStartInfo
                    {
                        FileName = "powershell.exe",
                        WorkingDirectory = targetDir,
                        UseShellExecute = true
                    };
                    Process.Start(psi);
                    HttpServer.ServeJson(response, 200, "{\"success\":true}");
                }
                catch (Exception ex)
                {
                    HttpServer.ServeJson(response, 500, "{\"success\":false,\"message\":\"" + HttpServer.EscapeJson(ex.Message) + "\"}");
                }
                return true;
            }
            else if (subPath == "refresh")
            {
                TriggerPnpmScanAsync(true);
                HttpServer.ServeJson(response, 200, "{\"success\":true,\"message\":\"" + HttpServer.EscapeJson(I18nManager.T("api_gradle_scan_started")) + "\"}");
                return true;
            }
            else if (subPath == "pkg-files")
            {
                string indexFile = request.QueryString["indexFile"];
                if (string.IsNullOrEmpty(indexFile) || !File.Exists(indexFile))
                {
                    HttpServer.ServeJson(response, 404, "{\"success\":false,\"message\":\"" + HttpServer.EscapeJson(I18nManager.T("api_file_not_found")) + "\"}");
                    return true;
                }
                try
                {
                    string content = File.ReadAllText(indexFile, Encoding.UTF8);
                    HttpServer.ServeJson(response, 200, "{\"success\":true,\"rawIndex\":" + content + "}");
                }
                catch (Exception ex)
                {
                    HttpServer.ServeJson(response, 500, "{\"success\":false,\"message\":\"" + HttpServer.EscapeJson(ex.Message) + "\"}");
                }
                return true;
            }
            else if (subPath == "clean-dlx")
            {
                string cacheDir = GetDefaultPnpmCacheDir();
                string dlxDir = Path.Combine(cacheDir, "dlx");
                if (Directory.Exists(dlxDir))
                {
                    try
                    {
                        Directory.Delete(dlxDir, true);
                        Logger.Log(I18nManager.T("log_pnpm_clean_dlx", dlxDir));
                        TriggerPnpmScanAsync(true);
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
