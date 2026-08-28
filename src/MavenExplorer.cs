using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Xml;

namespace LocalDiskServer
{
    public class MavenArtifactItem
    {
        public string GroupId { get; set; }
        public string ArtifactId { get; set; }
        public string Version { get; set; }
        public string Packaging { get; set; }
        public long Size { get; set; }
        public DateTime LastModified { get; set; }
        public string LocalPath { get; set; }
        public bool HasSources { get; set; }
        public bool HasJavadoc { get; set; }
        public int DepCount { get; set; }
        public Dictionary<string, string> Dependencies { get; set; }

        // KMP (Kotlin Multiplatform) 支持
        public bool IsKmp { get; set; }
        public List<string> KmpPlatforms { get; set; }
        public string Description { get; set; }
        public string License { get; set; }

        // POM 解析状态
        public bool ParseFailed { get; set; }
        public string FailReason { get; set; }

        public MavenArtifactItem()
        {
            GroupId = "";
            ArtifactId = "";
            Version = "";
            Packaging = "jar";
            LocalPath = "";
            Dependencies = new Dictionary<string, string>();
            KmpPlatforms = new List<string>();
            License = "";
            Description = "";
            FailReason = "";
        }
    }

    public class MavenScanResult
    {
        public List<MavenArtifactItem> Artifacts { get; set; }
        public long TotalArtifacts { get; set; }
        public long TotalSize { get; set; }
        public string LocalRepoPath { get; set; }
        public string SettingsPath { get; set; }
        public string SettingsContent { get; set; }
        public string MavenVersion { get; set; }
        public string MavenPath { get; set; }
        public string JavaVersion { get; set; }
        public string JavaHome { get; set; }

        public MavenScanResult()
        {
            Artifacts = new List<MavenArtifactItem>();
            LocalRepoPath = "";
            SettingsPath = "";
            SettingsContent = "";
            MavenVersion = "";
            MavenPath = "";
            JavaVersion = "";
            JavaHome = "";
        }
    }

    public static class MavenExplorer
    {
        private static readonly object mavenScanLock = new object();
        private static bool isScanning = false;
        private static MavenScanResult cachedResult = null;
        private static long cachedRepoTicks = 0;

        public static bool IsScanning { get { return isScanning; } }

        public static string GetCacheFilePath()
        {
            string cacheDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "cache");
            if (!Directory.Exists(cacheDir))
            {
                try { Directory.CreateDirectory(cacheDir); } catch { }
            }
            return Path.Combine(cacheDir, "maven_cache.dat");
        }

        public static void ClearCacheAndReleaseResources()
        {
            lock (mavenScanLock)
            {
                if (cachedResult != null)
                {
                    cachedResult.Artifacts.Clear();
                    cachedResult.Artifacts.TrimExcess();
                    cachedResult = null;
                }
                cachedRepoTicks = 0;
                GC.Collect();
            }
            Logger.Log(I18nManager.T("log_dev_ecosystem_released"));
        }

        public static void TriggerMavenScanAsync(bool forceRescan = false)
        {
            if (!ServerApplicationContext.enable_dev_ecosystem) return;

            ThreadPool.QueueUserWorkItem(delegate
            {
                lock (mavenScanLock)
                {
                    if (isScanning) return;
                    isScanning = true;
                }

                try
                {
                    string repoRoot = GetDefaultMavenLocalRepo();
                    long curRepoTicks = 0;

                    if (!string.IsNullOrEmpty(repoRoot) && Directory.Exists(repoRoot))
                    {
                        try { curRepoTicks = Directory.GetLastWriteTimeUtc(repoRoot).Ticks; } catch { }
                    }

                    if (!forceRescan && cachedResult == null)
                    {
                        long sRepo;
                        if (TryLoadFromDiskCache(out sRepo))
                        {
                            if (curRepoTicks == sRepo)
                            {
                                Logger.Log(I18nManager.T("log_dev_ecosystem_verified"));
                                return;
                            }
                        }
                    }

                    Logger.Log(I18nManager.T("log_maven_scan_started"));
                    MavenScanResult res = DoMavenScan(repoRoot);

                    lock (mavenScanLock)
                    {
                        cachedResult = res;
                        cachedRepoTicks = curRepoTicks;
                    }

                    SaveToDiskCache(curRepoTicks);
                    Logger.Log(I18nManager.T("log_maven_scan_finished", res.Artifacts.Count, FormatSize(res.TotalSize)));
                }
                catch (Exception ex)
                {
                    Logger.Log("Maven scan error: " + ex.Message);
                }
                finally
                {
                    lock (mavenScanLock)
                    {
                        isScanning = false;
                    }
                }
            });
        }

        private static string GetDefaultMavenLocalRepo()
        {
            // 1. 优先从 settings.xml 读取 <localRepository> 配置
            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string settingsPath = Path.Combine(userProfile, ".m2", "settings.xml");
            if (File.Exists(settingsPath))
            {
                try
                {
                    string xmlContent = File.ReadAllText(settingsPath, Encoding.UTF8);
                    // 简单解析 <localRepository>/path/to/repo</localRepository>
                    int startTag = xmlContent.IndexOf("<localRepository>");
                    if (startTag >= 0)
                    {
                        int start = startTag + "<localRepository>".Length;
                        int end = xmlContent.IndexOf("</localRepository>", start);
                        if (end > start)
                        {
                            string configuredPath = xmlContent.Substring(start, end - start).Trim();
                            if (!string.IsNullOrEmpty(configuredPath) && Directory.Exists(configuredPath))
                            {
                                return configuredPath;
                            }
                        }
                    }
                }
                catch { /* 解析失败则继续 fallback */ }
            }

            // 2. 环境变量 MAVEN_LOCAL_REPO / M2_REPO
            string envRepo = Environment.GetEnvironmentVariable("MAVEN_LOCAL_REPO") ?? Environment.GetEnvironmentVariable("M2_REPO");
            if (!string.IsNullOrEmpty(envRepo) && Directory.Exists(envRepo)) return envRepo;

            // 3. MAVEN_HOME 环境变量 → 推算 ../.m2/repository
            string mavenHome = Environment.GetEnvironmentVariable("MAVEN_HOME") ?? Environment.GetEnvironmentVariable("M2_HOME");
            if (!string.IsNullOrEmpty(mavenHome))
            {
                string mavenHomeDir = Path.GetDirectoryName(mavenHome);
                if (!string.IsNullOrEmpty(mavenHomeDir))
                {
                    string inferredRepo = Path.Combine(mavenHomeDir, ".m2", "repository");
                    if (Directory.Exists(inferredRepo)) return inferredRepo;
                }
            }

            // 4. 用户主目录下的 .m2/repository（标准默认路径）
            string m2Repo = Path.Combine(userProfile, ".m2", "repository");
            if (Directory.Exists(m2Repo)) return m2Repo;

            // 5. 仅 .m2 目录（旧版可能直接在 .m2 下）
            string m2Dir = Path.Combine(userProfile, ".m2");
            if (Directory.Exists(m2Dir)) return m2Dir;

            return "";
        }

        private static MavenScanResult DoMavenScan(string repoRoot)
        {
            MavenScanResult res = new MavenScanResult();
            res.LocalRepoPath = repoRoot;

            // 探测 Maven CLI
            string mvnVer, mvnPath;
            DetectMavenCli(out mvnVer, out mvnPath);
            res.MavenVersion = mvnVer;
            res.MavenPath = mvnPath;

            // 探测 Java Runtime
            string jHome, jVer, jPath;
            GradleExplorer.DetectJavaRuntime(out jHome, out jVer, out jPath);
            res.JavaHome = jHome;
            res.JavaVersion = jVer;

            // 读取 settings.xml
            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string settingsPath = Path.Combine(userProfile, ".m2", "settings.xml");
            if (File.Exists(settingsPath))
            {
                res.SettingsPath = settingsPath;
                try
                {
                    res.SettingsContent = File.ReadAllText(settingsPath, Encoding.UTF8);
                }
                catch { }
            }

            // 扫描仓库目录
            if (!string.IsNullOrEmpty(repoRoot) && Directory.Exists(repoRoot))
            {
                ScanRepository(repoRoot, res);
                res.TotalArtifacts = res.Artifacts.Count;
            }

            return res;
        }

        private static void ScanRepository(string rootDir, MavenScanResult res)
        {
            try
            {
                DirectoryInfo rootInfo = new DirectoryInfo(rootDir);

                // 第一层：groupId 目录（如 org/, com/, net/）
                foreach (DirectoryInfo groupDir in rootInfo.GetDirectories())
                {
                    if (groupDir.Name.StartsWith(".")) continue; // 跳过隐藏目录

                    // 第二层：artifactId 目录
                    foreach (DirectoryInfo artifactDir in groupDir.GetDirectories())
                    {
                        if (artifactDir.Name.StartsWith(".")) continue;

                        // 第三层：可能是 version 目录或嵌套的 artifactId 目录
                        ScanVersionDirs(artifactDir, groupDir.Name, artifactDir.Name, res);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log("ScanRepository error: " + ex.Message);
            }
        }

        /// <summary>
        /// 递归扫描版本目录（处理非标准的深层嵌套结构）
        /// </summary>
        private static void ScanVersionDirs(DirectoryInfo currentDir, string groupId, string artifactId, MavenScanResult res, int depth = 0)
        {
            // 安全限制：防止无限递归
            if (depth > 3) return;

            foreach (DirectoryInfo subDir in currentDir.GetDirectories())
            {
                if (subDir.Name.StartsWith(".")) continue;

                // 检测是否为版本目录（包含构件文件如 .jar/.aar/.pom）
                bool isVersionDir = IsVersionDirectory(subDir);

                if (isVersionDir)
                {
                    // 这是版本目录，解析构件
                    MavenArtifactItem item = ParseArtifact(
                        subDir.FullName,
                        groupId,
                        artifactId,
                        subDir.Name
                    );
                    if (item != null)
                    {
                        res.Artifacts.Add(item);
                        res.TotalSize += item.Size;
                    }
                }
                else
                {
                    // 不是版本目录，继续递归（可能是嵌套的 artifactId 结构）
                    // 更新 artifactId：使用当前目录名作为新的 artifactId
                    ScanVersionDirs(subDir, groupId, subDir.Name, res, depth + 1);
                }
            }
        }

        /// <summary>
        /// 检测目录是否为 Maven 版本目录（包含实际的构件文件）
        /// </summary>
        private static bool IsVersionDirectory(DirectoryInfo dir)
        {
            try
            {
                // 版本目录应包含至少一个构件文件（.jar, .aar, .war, .ear, .pom 等）
                string[] artifactExtensions = { ".jar", ".aar", ".war", ".ear", ".par", ".ejb", ".pom" };
                
                foreach (FileInfo fi in dir.GetFiles())
                {
                    string ext = fi.Extension.ToLower();
                    foreach (string validExt in artifactExtensions)
                    {
                        if (ext == validExt) return true;
                    }
                }
                
                // 额外检查：如果有 .module 文件（Gradle 元数据）也算
                if (dir.GetFiles("*.module").Length > 0) return true;
            }
            catch { }
            
            return false;
        }

        /// <summary>
        /// 解析版本目录下的 Maven 构件（修正版：优先从 POM 读取元数据）
        /// </summary>
        private static MavenArtifactItem ParseArtifact(string versionDirPath, string groupIdHint, string artifactId, string version)
        {
            try
            {
                MavenArtifactItem item = new MavenArtifactItem();
                item.LocalPath = versionDirPath;
                item.ArtifactId = artifactId;
                item.Version = version;
                item.LastModified = Directory.GetLastWriteTime(versionDirPath);

                DirectoryInfo vDir = new DirectoryInfo(versionDirPath);

                // === 1. 定位并解析 POM 文件（权威数据源）===
                string pomFile = FindPomFile(vDir, artifactId, version);
                bool pomParsed = false;
                XmlDocument pomDocForKmp = null;

                if (pomFile != null && File.Exists(pomFile))
                {
                    try
                    {
                        XmlDocument pomDoc = new XmlDocument();
                        pomDoc.Load(pomFile);
                        pomDocForKmp = pomDoc;
                        XmlNamespaceManager nsmgr = new XmlNamespaceManager(pomDoc.NameTable);
                        nsmgr.AddNamespace("m", "http://maven.apache.org/POM/4.0.0");

                        // 1a. 从 POM 读取 groupId（优先级最高）
                        string pomGroupId = ExtractXmlElement(pomDoc, "m:projectId m:groupId") ??
                                           ExtractXmlElement(pomDoc, "groupId");
                        if (string.IsNullOrEmpty(pomGroupId))
                        {
                            // 尝试从 parent 继承
                            pomGroupId = ExtractXmlElement(pomDoc, "m:parent/m:groupId") ??
                                        ExtractXmlElement(pomDoc, "parent/groupId");
                        }
                        if (!string.IsNullOrEmpty(pomGroupId)) item.GroupId = pomGroupId;
                        else item.GroupId = InferGroupIdFromPath(versionDirPath, artifactId, version) ?? groupIdHint;

                        // 1b. 从 POM 读取 packaging
                        string pomPackaging = ExtractXmlElement(pomDoc, "m:packaging") ??
                                            ExtractXmlElement(pomDoc, "packaging");
                        if (!string.IsNullOrEmpty(pomPackaging)) item.Packaging = pomPackaging.ToLower();

                        // 1c. 读取描述和许可证
                        item.Description = ExtractXmlElement(pomDoc, "m:description") ??
                                          ExtractXmlElement(pomDoc, "description") ?? "";
                        item.License = ExtractXmlElement(pomDoc, "m:licenses/m:license/m:name") ??
                                       ExtractXmlElement(pomDoc, "licenses/license/name") ?? "";

                        // 1d. KMP 检测已迁移至目录扫描阶段（DetectKmpFromLayout），与 POM 解析成败解耦

                        // 1e. 解析依赖（支持 parent 递归）
                        item.Dependencies = ParsePomDependenciesEnhanced(pomFile, vDir, 0);
                        item.DepCount = item.Dependencies.Count;

                        pomParsed = true;
                    }
                    catch (Exception ex)
                    {
                        Logger.Log("POM parse warning for " + pomFile + ": " + ex.Message);
                        item.ParseFailed = true;
                        item.FailReason = "pom_parse_error: " + ex.Message;
                    }
                }

                // POM 解析失败时的降级处理
                if (!pomParsed)
                {
                    if (!item.ParseFailed)
                    {
                        item.ParseFailed = true;
                        item.FailReason = (pomFile == null) ? "no_pom_found" : "pom_unreadable";
                    }
                    item.GroupId = InferGroupIdFromPath(versionDirPath, artifactId, version) ?? groupIdHint;
                }

                // === 2. 扫描目录检测文件 ===
                string mainPrefix = artifactId + "-" + version + ".";
                long totalSize = 0;

                // KMP 检测：基于目录布局 + .module 元数据 + POM 依赖特征
                DetectKmpFromLayout(vDir, pomDocForKmp, item);

                foreach (FileInfo fi in vDir.GetFiles())
                {
                    string fname = fi.Name;
                    string fnameLower = fname.ToLower();

                    // 跳过元数据文件
                    if (IsMetadataFile(fnameLower)) continue;

                    totalSize += fi.Length;

                    // 检测附属包
                    if (fnameLower.EndsWith("-sources.jar")) item.HasSources = true;
                    else if (fnameLower.EndsWith("-javadoc.jar")) item.HasJavadoc = true;
                    // 检测其他 classifier（可选扩展）
                    else if (fnameLower.Contains("-" + version + "-tests.")) { /* tests jar */ }
                }

                item.Size = totalSize;

                return item;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 在版本目录中查找 POM 文件
        /// </summary>
        private static string FindPomFile(DirectoryInfo vDir, string artifactId, string version)
        {
            // 标准命名: {artifactId}-{version}.pom
            string standardPom = Path.Combine(vDir.FullName, artifactId + "-" + version + ".pom");
            if (File.Exists(standardPom)) return standardPom;

            // 无版本号: {artifactId}.pom (罕见)
            string noVerPom = Path.Combine(vDir.FullName, artifactId + ".pom");
            if (File.Exists(noVerPom)) return noVerPom;

            // 任意 .pom 文件
            foreach (FileInfo fi in vDir.GetFiles("*.pom"))
            {
                if (!fi.Name.EndsWith(".sha1", StringComparison.OrdinalIgnoreCase))
                    return fi.FullName;
            }

            return null;
        }

        /// <summary>
        /// 判断是否为 Maven 元数据文件（不计入构件大小）
        /// </summary>
        private static bool IsMetadataFile(string fname)
        {
            if (fname.StartsWith("_") || fname == "resolver-status.properties")
                return true;
            if (fname.EndsWith(".sha1", StringComparison.OrdinalIgnoreCase) ||
                fname.EndsWith(".md5", StringComparison.OrdinalIgnoreCase) ||
                fname.EndsWith(".lastUpdated", StringComparison.OrdinalIgnoreCase))
                return true;
            return false;
        }

        /// <summary>
        /// 从 XML 文档提取元素文本（兼容命名空间）
        /// </summary>
        private static string ExtractXmlElement(XmlDocument doc, string xpath)
        {
            try
            {
                XmlNode node = doc.SelectSingleNode(xpath);
                return node != null ? node.InnerText.Trim() : null;
            }
            catch { return null; }
        }

        /// <summary>
        /// 检测 Kotlin Multiplatform (KMP) 架构
        /// </summary>
        #region KMP Detection

        /// <summary>Kotlin Multiplatform 平台后缀白名单：小写 token → 规范展示名（长 token 优先匹配）</summary>
        private static readonly string[][] KmpPlatformTokens = new string[][]
        {
            new[] { "wasm-js", "WasmJs" }, new[] { "wasm-wasi", "Wasi" }, new[] { "wasm32", "Wasm32" },
            new[] { "iossimulatorarm64", "iOSSimArm64" }, new[] { "iossimulatorx64", "iOSSimX64" },
            new[] { "watchossimulatorarm64", "watchOSSimArm64" }, new[] { "watchossimulatorx64", "watchOSSimX64" },
            new[] { "tvossimulatorarm64", "tvOSSimArm64" }, new[] { "tvossimulatorx64", "tvOSSimX64" },
            new[] { "androidnativearm32", "AndroidNativeArm32" }, new[] { "androidnativearm64", "AndroidNativeArm64" },
            new[] { "linuxarm32hfp", "LinuxArm32Hfp" }, new[] { "linuxarm64", "LinuxArm64" }, new[] { "linuxx64", "LinuxX64" },
            new[] { "mingwx86", "MingwX86" }, new[] { "mingwx64", "MingwX64" },
            new[] { "macosarm64", "macOSArm64" }, new[] { "macosx64", "macOSX64" },
            new[] { "watchosarm32", "watchOSArm32" }, new[] { "watchosarm64", "watchOSArm64" },
            new[] { "watchosx64", "watchOSX64" }, new[] { "watchosx86", "watchOSX86" },
            new[] { "tvosarm64", "tvOSArm64" }, new[] { "tvosx64", "tvOSX64" },
            new[] { "iosarm64", "iOSArm64" }, new[] { "iosx64", "iOSX64" },
            new[] { "jvm", "JVM" }, new[] { "js", "JS" }, new[] { "android", "Android" }, new[] { "metadata", "Common" }
        };

        private static void AddKmpPlatform(MavenArtifactItem item, string platform)
        {
            if (string.IsNullOrEmpty(platform)) return;
            if (!item.KmpPlatforms.Contains(platform)) item.KmpPlatforms.Add(platform);
        }

        /// <summary>
        /// 尝试从 artifactId 剥离 KMP 平台后缀（如 kotlinx-coroutines-core-iosarm64 → iOSArm64）
        /// </summary>
        private static bool TryStripPlatformSuffix(string artifactId, out string platform)
        {
            platform = null;
            if (string.IsNullOrEmpty(artifactId)) return false;
            foreach (string[] tk in KmpPlatformTokens)
            {
                if (artifactId.EndsWith("-" + tk[0], StringComparison.OrdinalIgnoreCase))
                {
                    platform = tk[1];
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// KMP 判定（四信号累进）：① artifactId 平台后缀；② 目录内 .klib / Gradle Module 元数据；
        /// ③ .module 文本中声明的 rootAid-平台 变体坐标；④ POM 真实依赖特征串。
        /// 独立于 POM 解析成败——纯平台变体目录（无 POM）同样能识别。
        /// </summary>
        private static void DetectKmpFromLayout(DirectoryInfo vDir, XmlDocument pomDoc, MavenArtifactItem item)
        {
            try
            {
                // ① artifactId 平台后缀
                string selfPlat;
                if (TryStripPlatformSuffix(item.ArtifactId, out selfPlat))
                {
                    item.IsKmp = true;
                    AddKmpPlatform(item, selfPlat);
                }

                // ② 文件信号 + 缓存 .module 全文
                string moduleText = null;
                bool hasKlib = false;
                foreach (FileInfo fi in vDir.GetFiles())
                {
                    string ln = fi.Name.ToLowerInvariant();
                    if (ln.EndsWith(".klib")) hasKlib = true;
                    else if (ln.EndsWith(".module") && moduleText == null && fi.Length < 5 * 1024 * 1024)
                    {
                        moduleText = File.ReadAllText(fi.FullName).ToLowerInvariant();
                    }
                }
                if (hasKlib) { item.IsKmp = true; AddKmpPlatform(item, "Common"); }

                // ③ Gradle Module Metadata：声明的变体坐标 / 平台属性
                if (!string.IsNullOrEmpty(moduleText))
                {
                    foreach (string[] tk in KmpPlatformTokens)
                    {
                        if (moduleText.Contains(item.ArtifactId + "-" + tk[0]))
                        {
                            item.IsKmp = true;
                            AddKmpPlatform(item, tk[1]);
                        }
                    }
                    if (moduleText.Contains("org.jetbrains.kotlin.platform.type")) item.IsKmp = true;
                }

                // ④ POM 依赖特征（真实坐标串）：atomicfu / kotlin-stdlib 是多平台编译的典型信号
                if (pomDoc != null && !item.IsKmp)
                {
                    XmlNamespaceManager nsmgr2 = new XmlNamespaceManager(pomDoc.NameTable);
                    nsmgr2.AddNamespace("m", "http://maven.apache.org/POM/4.0.0");
                    XmlNodeList depNodes = pomDoc.SelectNodes("//m:dependency/m:artifactId", nsmgr2);
                    if (depNodes != null)
                    {
                        foreach (XmlNode dep in depNodes)
                        {
                            string aid = (dep.InnerText ?? "").Trim();
                            if (aid.Equals("atomicfu", StringComparison.OrdinalIgnoreCase) ||
                                aid.StartsWith("kotlin-stdlib", StringComparison.OrdinalIgnoreCase))
                            {
                                item.IsKmp = true;
                                break;
                            }
                        }
                    }
                }

                // 平台兜底
                if (item.IsKmp && item.KmpPlatforms.Count == 0) AddKmpPlatform(item, "Common");
            }
            catch { /* KMP 检测失败不影响主流程 */ }
        }

        #endregion

        private static string ExtractXmlElementFromNode(XmlNode parent, string localName)
        {
            XmlNode node = parent.SelectSingleNode("*[local-name()='" + localName + "']");
            return node != null ? node.InnerText.Trim() : null;
        }

        /// <summary>
        /// 从物理路径反推 groupId（降级方案：仅当 POM 解析失败时使用）
        /// </summary>
        private static string InferGroupIdFromPath(string versionDirPath, string artifactId, string version)
        {
            try
            {
                DirectoryInfo vDir = new DirectoryInfo(versionDirPath);
                DirectoryInfo artDir = vDir.Parent;
                if (artDir == null || artDir.Name != artifactId) return "";

                // artDir 的父目录就是 groupId 的最底层，继续向上直到 repository 根
                List<string> parts = new List<string>();
                DirectoryInfo current = artDir.Parent;
                
                // 已知的仓库根目录名（停止条件）
                string[] knownRepoRoots = new string[] { ".m2", "repository", "local-repo" };
                
                int safetyCount = 0;
                while (current != null && safetyCount < 15)
                {
                    // 到达已知仓库根目录则停止
                    bool isRepoRoot = false;
                    foreach (string rootName in knownRepoRoots)
                    {
                        if (string.Equals(current.Name, rootName, StringComparison.OrdinalIgnoreCase))
                        {
                            isRepoRoot = true;
                            break;
                        }
                    }
                    if (isRepoRoot) break;
                    
                    // 额外检查：如果父目录同时包含 org + com 则视为仓库根
                    if (current.Parent != null)
                    {
                        bool hasOrg = Directory.Exists(Path.Combine(current.Parent.FullName, "org"));
                        bool hasCom = Directory.Exists(Path.Combine(current.Parent.FullName, "com"));
                        bool hasIo = Directory.Exists(Path.Combine(current.Parent.FullName, "io"));
                        if ((hasOrg && hasCom) || (hasOrg && hasIo) || (hasCom && hasIo))
                        {
                            // 当前目录可能是顶级 groupId（如 org, com, io），继续添加
                            parts.Insert(0, current.Name);
                            break;
                        }
                    }
                    
                    parts.Insert(0, current.Name);
                    current = current.Parent;
                    safetyCount++;
                }
                
                if (parts.Count > 0) return string.Join(".", parts);
                return "";
            }
            catch { return ""; }
        }

        private static Dictionary<string, string> ParsePomDependencies(string pomPath)
        {
            Dictionary<string, string> deps = new Dictionary<string, string>();
            try
            {
                XmlDocument doc = new XmlDocument();
                doc.Load(pomPath);

                XmlNodeList depNodes = doc.SelectNodes("//*[local-name()='dependency']");
                if (depNodes == null) return deps;

                foreach (XmlNode dep in depNodes)
                {
                    XmlNode gidNode = dep.SelectSingleNode("*[local-name()='groupId']");
                    XmlNode aidNode = dep.SelectSingleNode("*[local-name='artifactId']");
                    XmlNode verNode = dep.SelectSingleNode("*[local-name='version']");

                    string gid = gidNode != null ? gidNode.InnerText.Trim() : "";
                    string aid = aidNode != null ? aidNode.InnerText.Trim() : "";
                    string ver = verNode != null ? verNode.InnerText.Trim() : "";

                    if (!string.IsNullOrEmpty(gid) && !string.IsNullOrEmpty(aid))
                    {
                        string key = gid + ":" + aid;
                        if (!deps.ContainsKey(key))
                        {
                            deps[key] = ver;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log("ParsePomDependencies error for " + pomPath + ": " + ex.Message);
            }
            return deps;
        }

        /// <summary>
        /// 增强版 POM 依赖解析：支持 parent 递归 + 深度限制
        /// </summary>
        private static Dictionary<string, string> ParsePomDependenciesEnhanced(string pomPath, DirectoryInfo versionDir, int depth)
        {
            Dictionary<string, string> deps = new Dictionary<string, string>();
            if (depth > 3) return deps;

            try
            {
                XmlDocument doc = new XmlDocument();
                doc.Load(pomPath);

                // 解析直接依赖
                XmlNodeList depNodes = doc.SelectNodes("//*[local-name()='dependency']");
                if (depNodes != null)
                {
                    foreach (XmlNode dep in depNodes)
                    {
                        string gid = ExtractXmlElementFromNode(dep, "groupId");
                        string aid = ExtractXmlElementFromNode(dep, "artifactId");
                        string ver = ExtractXmlElementFromNode(dep, "version");
                        if (!string.IsNullOrEmpty(gid) && !string.IsNullOrEmpty(aid) && !string.IsNullOrEmpty(ver))
                        {
                            string key = gid + ":" + aid;
                            if (!deps.ContainsKey(key)) deps[key] = ver;
                        }
                    }
                }

                // 如果无直接依赖，尝试 parent 递归
                if (deps.Count == 0)
                {
                    XmlNode parentNode = doc.SelectSingleNode("//*[local-name()='parent']");
                    if (parentNode != null)
                    {
                        string pGid = ExtractXmlElementFromNode(parentNode, "groupId");
                        string pAid = ExtractXmlElementFromNode(parentNode, "artifactId");
                        string pVer = ExtractXmlElementFromNode(parentNode, "version");
                        if (!string.IsNullOrEmpty(pGid) && !string.IsNullOrEmpty(pAid) && !string.IsNullOrEmpty(pVer))
                        {
                            string repoRoot = (versionDir != null && versionDir.Parent != null && versionDir.Parent.Parent != null) ? versionDir.Parent.Parent.FullName : null;
                            string parentPom = FindLocalPom(repoRoot, pGid, pAid, pVer);
                            if (parentPom != null)
                            {
                                var parentDeps = ParsePomDependenciesEnhanced(parentPom, versionDir, depth + 1);
                                foreach (var kv in parentDeps)
                                    if (!deps.ContainsKey(kv.Key)) deps[kv.Key] = kv.Value;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log("ParsePomDependenciesEnhanced error: " + ex.Message);
            }
            return deps;
        }

        /// <summary>
        /// 在本地仓库中查找指定坐标的 POM 文件
        /// </summary>
        private static string FindLocalPom(string repoRoot, string groupId, string artifactId, string version)
        {
            try
            {
                if (string.IsNullOrEmpty(repoRoot)) return null;
                string groupPath = groupId.Replace('.', Path.DirectorySeparatorChar);
                string expectedDir = Path.Combine(repoRoot, groupPath, artifactId, version);
                string expectedPom = Path.Combine(expectedDir, artifactId + "-" + version + ".pom");
                if (File.Exists(expectedPom)) return expectedPom;
                string altPom = Path.Combine(expectedDir, artifactId + ".pom");
                if (File.Exists(altPom)) return altPom;
                return null;
            }
            catch { return null; }
        }

        private static void DetectMavenCli(out string mavenVersion, out string mavenPath)
        {
            mavenVersion = "";
            mavenPath = "";

            try
            {
                string pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
                string[] paths = pathEnv.Split(Path.PathSeparator);
                foreach (string p in paths)
                {
                    if (string.IsNullOrEmpty(p)) continue;
                    string c1 = Path.Combine(p.Trim('\"', ' '), "mvn.cmd");
                    string c2 = Path.Combine(p.Trim('\"', ' '), "mvn.bat");
                    if (File.Exists(c1)) { mavenPath = c1; break; }
                    else if (File.Exists(c2)) { mavenPath = c2; break; }
                }

                // 默认安装路径
                if (string.IsNullOrEmpty(mavenPath))
                {
                    string[] defaultPaths = new string[]
                    {
                        @"C:\Program Files\apache-maven\bin\mvn.cmd",
                        @"C:\apache-maven\bin\mvn.cmd",
                        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Programs\apache-maven\bin\mvn.cmd")
                    };
                    foreach (string dp in defaultPaths)
                    {
                        if (File.Exists(dp)) { mavenPath = dp; break; }
                    }
                }

                if (!string.IsNullOrEmpty(mavenPath) && File.Exists(mavenPath))
                {
                    ProcessStartInfo psi = new ProcessStartInfo
                    {
                        FileName = "cmd.exe",
                        Arguments = "/c \"" + mavenPath + "\" -v",
                        RedirectStandardOutput = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                    using (var p = Process.Start(psi))
                    {
                        string output = p.StandardOutput.ReadToEnd();
                        p.WaitForExit(5000);
                        if (!string.IsNullOrEmpty(output))
                        {
                            string[] lines = output.Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                            foreach (string line in lines)
                            {
                                if (line.Contains("Apache Maven") || line.StartsWith("Apache Maven"))
                                {
                                    mavenVersion = line.Trim();
                                    break;
                                }
                            }
                            if (string.IsNullOrEmpty(mavenVersion) && lines.Length > 0)
                            {
                                mavenVersion = lines[0].Trim();
                            }
                        }
                    }
                }
            }
            catch { }
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

        #region Disk Cache

        private static void SaveToDiskCache(long repoTicks)
        {
            try
            {
                string cacheFile = GetCacheFilePath();
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("FORMAT=2");
                sb.AppendLine("SCAN_LOGIC=2");
                sb.AppendLine("REPO_TICKS=" + repoTicks);

                lock (mavenScanLock)
                {
                    if (cachedResult != null)
                    {
                        sb.AppendLine("LOCAL_REPO=" + EscapeLine(cachedResult.LocalRepoPath));
                        sb.AppendLine("TOTAL_ARTIFACTS=" + cachedResult.Artifacts.Count.ToString());
                        sb.AppendLine("TOTAL_SIZE=" + cachedResult.TotalSize.ToString());
                        sb.AppendLine("MAVEN_VER=" + EscapeLine(cachedResult.MavenVersion));
                        sb.AppendLine("MAVEN_PATH=" + EscapeLine(cachedResult.MavenPath));
                        sb.AppendLine("JAVA_VER=" + EscapeLine(cachedResult.JavaVersion));
                        sb.AppendLine("JAVA_HOME=" + EscapeLine(cachedResult.JavaHome));
                        sb.AppendLine("SETTINGS_PATH=" + EscapeLine(cachedResult.SettingsPath));

                        foreach (MavenArtifactItem a in cachedResult.Artifacts)
                        {
                            // 持久化 POM 元数据：依赖/描述/许可证/KMP/解析状态一并落盘（FORMAT=2）
                            List<string> kmpList = (a.KmpPlatforms != null) ? a.KmpPlatforms : new List<string>();
                            sb.AppendLine(string.Format("ART\t{0}\t{1}\t{2}\t{3}\t{4}\t{5}\t{6}\t{7}\t{8}\t{9}\t{10}\t{11}\t{12}\t{13}\t{14}",
                                EscapeField(a.GroupId),
                                EscapeField(a.ArtifactId),
                                EscapeField(a.Version),
                                EscapeField(a.Packaging),
                                a.Size,
                                a.LastModified.Ticks,
                                EscapeField(a.LocalPath),
                                a.HasSources ? "1" : "0",
                                a.HasJavadoc ? "1" : "0",
                                a.DepCount,
                                a.ParseFailed ? "1" : "0",
                                EscapeField(a.FailReason),
                                EscapeField(a.Description),
                                EscapeField(a.License),
                                EscapeField(a.IsKmp ? "1" + "\u0001" + string.Join("\u0002", kmpList) : "")));

                            foreach (var d in a.Dependencies)
                            {
                                sb.AppendLine("DEP\t" + EscapeField(d.Key) + "\t" + EscapeField(d.Value ?? ""));
                            }
                        }
                    }
                }

                File.WriteAllText(cacheFile, sb.ToString(), Encoding.UTF8);
                Logger.Log(I18nManager.T("log_dev_ecosystem_saved", cacheFile));
            }
            catch { }
        }

        private static bool TryLoadFromDiskCache(out long repoTicks)
        {
            repoTicks = 0;
            string cacheFile = GetCacheFilePath();
            if (!File.Exists(cacheFile)) return false;

            try
            {
                Stopwatch sw = Stopwatch.StartNew();
                string[] lines = File.ReadAllLines(cacheFile, Encoding.UTF8);
                MavenScanResult res = new MavenScanResult();
                bool formatOk = false;
                bool logicOk = false;
                MavenArtifactItem lastItem = null;

                foreach (string line in lines)
                {
                    if (string.IsNullOrEmpty(line)) continue;
                    if (line == "FORMAT=2") { formatOk = true; }
                    else if (line == "SCAN_LOGIC=2") { logicOk = true; }
                    else if (line.StartsWith("REPO_TICKS=")) long.TryParse(line.Substring(11), out repoTicks);
                    else if (line.StartsWith("LOCAL_REPO=")) res.LocalRepoPath = UnescapeLine(line.Substring(11));
                    else if (line.StartsWith("TOTAL_ARTIFACTS=")) { int x; if (int.TryParse(line.Substring(16), out x)) res.TotalArtifacts = x; }
                    else if (line.StartsWith("TOTAL_SIZE=")) { long sz; if (long.TryParse(line.Substring(11), out sz)) res.TotalSize = sz; }
                    else if (line.StartsWith("MAVEN_VER=")) res.MavenVersion = UnescapeLine(line.Substring(10));
                    else if (line.StartsWith("MAVEN_PATH=")) res.MavenPath = UnescapeLine(line.Substring(11));
                    else if (line.StartsWith("JAVA_VER=")) res.JavaVersion = UnescapeLine(line.Substring(9));
                    else if (line.StartsWith("JAVA_HOME=")) res.JavaHome = UnescapeLine(line.Substring(10));
                    else if (line.StartsWith("SETTINGS_PATH=")) res.SettingsPath = UnescapeLine(line.Substring(14));
                    else if (line.StartsWith("ART\t"))
                    {
                        string[] parts = line.Split('\t');
                        MavenArtifactItem item = new MavenArtifactItem();
                        item.GroupId = UnescapeField(parts[1]);
                        item.ArtifactId = UnescapeField(parts[2]);
                        item.Version = UnescapeField(parts[3]);
                        item.Packaging = UnescapeField(parts[4]);
                        long sz; if (long.TryParse(parts[5], out sz)) item.Size = sz;
                        long ticks; if (long.TryParse(parts[6], out ticks)) item.LastModified = new DateTime(ticks);
                        item.LocalPath = UnescapeField(parts[7]);
                        item.HasSources = parts[8] == "1";
                        item.HasJavadoc = parts[9] == "1";
                        int depCount; int.TryParse(parts[10], out depCount);
                        item.DepCount = depCount;
                        item.ParseFailed = parts[11] == "1";
                        item.FailReason = UnescapeField(parts[12]);
                        item.Description = UnescapeField(parts[13]);
                        item.License = UnescapeField(parts[14]);

                        // KMP 信息序列化格式: flag\u0001platform1\u0002platform2...
                        string kmpRaw = (parts.Length >= 16) ? UnescapeField(parts[15]) : "";
                        if (kmpRaw != null && kmpRaw.StartsWith("1"))
                        {
                            item.IsKmp = true;
                            string platStr = kmpRaw.Length > 2 ? kmpRaw.Substring(2) : "";
                            if (!string.IsNullOrEmpty(platStr))
                            {
                                foreach (string p in platStr.Split('\u0002'))
                                {
                                    if (!string.IsNullOrEmpty(p)) item.KmpPlatforms.Add(p);
                                }
                            }
                        }
                        if (item.KmpPlatforms.Count == 0 && item.IsKmp) item.KmpPlatforms.Add("JVM");

                        lastItem = item;
                        res.Artifacts.Add(item);
                    }
                    else if (line.StartsWith("DEP\t") && lastItem != null)
                    {
                        string[] parts = line.Split('\t');
                        if (parts.Length >= 3)
                        {
                            string key = UnescapeField(parts[1]);
                            string val = UnescapeField(parts[2]);
                            if (!string.IsNullOrEmpty(key) && !lastItem.Dependencies.ContainsKey(key))
                            {
                                lastItem.Dependencies[key] = val;
                            }
                        }
                    }
                }

                // 旧格式（FORMAT=1）或旧扫描逻辑（无 SCAN_LOGIC=2，KMP 判定已重写）均判定失效：强制全量重扫
                if (!formatOk || !logicOk)
                {
                    return false;
                }

                res.TotalArtifacts = res.Artifacts.Count;

                lock (mavenScanLock)
                {
                    cachedResult = res;
                    cachedRepoTicks = repoTicks;
                }

                sw.Stop();
                Logger.Log(I18nManager.T("log_dev_ecosystem_fast_loaded", "Maven", res.Artifacts.Count, sw.ElapsedMilliseconds));
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

        #endregion

        #region HTTP Handlers

        public static void ServeMavenDashboard(HttpListenerResponse response)
        {
            if (!ServerApplicationContext.enable_dev_ecosystem)
            {
                response.Redirect("/");
                response.OutputStream.Close();
                return;
            }

            if (cachedResult == null && !isScanning)
            {
                TriggerMavenScanAsync();
            }

            string template = HttpServer.LoadResource("maven.html");
            if (string.IsNullOrEmpty(template))
            {
                HttpServer.ServeError(response, 500, I18nManager.T("err_internal", "maven.html not found"));
                return;
            }

            string activePath = "/maven";

            StringBuilder sb = new StringBuilder();
            sb.Append(HttpServer.GetHtmlHeader(I18nManager.T("maven_page_title"), activePath, "layout-explorer"));
            sb.Append("<script>const currentView = 'maven';</script>");
            sb.Append(FileExplorer.RenderSidebar(activePath, I18nManager.CurrentLanguage));

            // i18n 占位符替换
            template = template.Replace("{MAVEN_BREADCRUMB}", I18nManager.T("maven_breadcrumb"));
            template = template.Replace("{MAVEN_PAGE_TITLE}", I18nManager.T("maven_page_title"));
            template = template.Replace("{LOBBY_PROTO_TOGGLE_TITLE}", I18nManager.T("lobby_proto_toggle_title"));
            template = template.Replace("{MAVEN_SEARCH_PLACEHOLDER}", I18nManager.T("maven_search_placeholder"));
            template = template.Replace("{MAVEN_BTN_RESCAN}", I18nManager.T("maven_btn_rescan"));
            template = template.Replace("{MAVEN_SEC_REPO}", I18nManager.T("maven_sec_repo"));
            template = template.Replace("{MAVEN_STAT_ARTIFACTS}", I18nManager.T("maven_stat_artifacts"));
            template = template.Replace("{MAVEN_STAT_SIZE}", I18nManager.T("maven_stat_size"));
            template = template.Replace("{MAVEN_STAT_PATH}", I18nManager.T("maven_stat_path"));
            template = template.Replace("{MAVEN_BTN_CONFIG_DETAILS}", I18nManager.T("maven_btn_config_details"));
            template = template.Replace("{MAVEN_BTN_OPEN_REPO}", I18nManager.T("maven_btn_open_repo"));
            template = template.Replace("{MAVEN_BTN_CLEAN_INVALID}", I18nManager.T("maven_btn_clean_invalid"));
            template = template.Replace("{MAVEN_BTN_FAILED}", I18nManager.T("maven_btn_failed"));
            template = template.Replace("{MAVEN_FAILED_HINT}", I18nManager.T("maven_failed_hint"));
            template = template.Replace("{MAVEN_FAILED_MODAL_TITLE}", I18nManager.T("maven_failed_modal_title"));
            template = template.Replace("{MAVEN_SELECT_ALL}", I18nManager.T("maven_select_all"));
            template = template.Replace("{MAVEN_BATCH_RETRY}", I18nManager.T("maven_batch_retry"));
            template = template.Replace("{MAVEN_BATCH_DELETE}", I18nManager.T("maven_batch_delete"));
            template = template.Replace("{MAVEN_ITEM_FILES_TITLE}", I18nManager.T("maven_item_files_title"));
            template = template.Replace("{MAVEN_TH_GROUPID}", I18nManager.T("maven_th_groupid"));
            template = template.Replace("{MAVEN_TH_ARTIFACTID}", I18nManager.T("maven_th_artifactid"));
            template = template.Replace("{MAVEN_TH_VERSION}", I18nManager.T("maven_th_version"));
            template = template.Replace("{MAVEN_TH_PACKAGING}", I18nManager.T("maven_th_packaging"));
            template = template.Replace("{MAVEN_TH_SIZE}", I18nManager.T("npm_th_size"));
            template = template.Replace("{MAVEN_DETAIL_TITLE}", I18nManager.T("maven_detail_title"));
            template = template.Replace("{MAVEN_DETAIL_EMPTY}", I18nManager.T("maven_detail_empty"));
            template = template.Replace("{MAVEN_LOADING}", I18nManager.T("maven_loading"));
            template = template.Replace("{PREVIEW_BTN_EXPAND}", I18nManager.T("preview_btn_expand"));
            template = template.Replace("{PREVIEW_BTN_COLLAPSE}", I18nManager.T("preview_btn_collapse"));
            template = template.Replace("{MODAL_BTN_OK}", I18nManager.T("modal_btn_ok"));
            template = template.Replace("{MAVEN_MODAL_CONFIG_TITLE}", I18nManager.T("maven_modal_config_title"));
            template = template.Replace("{MAVEN_CFG_SEC_RUNTIME}", I18nManager.T("maven_cfg_sec_runtime"));
            template = template.Replace("{MAVEN_CFG_SEC_REPO}", I18nManager.T("maven_cfg_sec_repo"));
            template = template.Replace("{MAVEN_CFG_SEC_SETTINGS}", I18nManager.T("maven_cfg_sec_settings"));
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
            if (!rawPath.StartsWith("api/maven/", StringComparison.OrdinalIgnoreCase)) return false;

            if (!ServerApplicationContext.enable_dev_ecosystem)
            {
                HttpServer.ServeJson(response, 403, "{\"success\":false,\"message\":\"" + HttpServer.EscapeJson(I18nManager.T("err_dev_ecosystem_disabled")) + "\"}");
                return true;
            }

            string subPath = rawPath.Substring(10).ToLower();

            if (subPath == "data")
            {
                MavenScanResult res;
                lock (mavenScanLock)
                {
                    res = cachedResult;
                }

                if (res == null)
                {
                    HttpServer.ServeJson(response, 200, "{\"scanning\":" + (isScanning ? "true" : "false") + ",\"artifacts\":[],\"totalArtifacts\":0,\"totalSize\":0,\"localRepoPath\":\"\",\"settingsPath\":\"\",\"mavenVersion\":\"\",\"javaVersion\":\"\"}");
                    return true;
                }

                StringBuilder sb = new StringBuilder();
                sb.Append("{\"scanning\":").Append(isScanning ? "true" : "false");
                sb.Append(",\"totalArtifacts\":").Append(res.TotalArtifacts);
                sb.Append(",\"totalSize\":").Append(res.TotalSize);
                sb.Append(",\"localRepoPath\":\"").Append(HttpServer.EscapeJson(res.LocalRepoPath)).Append("\"");
                sb.Append(",\"settingsPath\":\"").Append(HttpServer.EscapeJson(res.SettingsPath)).Append("\"");
                sb.Append(",\"mavenVersion\":\"").Append(HttpServer.EscapeJson(res.MavenVersion)).Append("\"");
                sb.Append(",\"mavenPath\":\"").Append(HttpServer.EscapeJson(res.MavenPath)).Append("\"");
                sb.Append(",\"javaVersion\":\"").Append(HttpServer.EscapeJson(res.JavaVersion)).Append("\"");
                sb.Append(",\"javaHome\":\"").Append(HttpServer.EscapeJson(res.JavaHome)).Append("\"");

                sb.Append(",\"artifacts\":[");
                for (int i = 0; i < res.Artifacts.Count; i++)
                {
                    if (i > 0) sb.Append(",");
                    MavenArtifactItem a = res.Artifacts[i];
                    sb.Append("{");
                    sb.Append("\"groupId\":\"").Append(HttpServer.EscapeJson(a.GroupId)).Append("\"");
                    sb.Append(",\"artifactId\":\"").Append(HttpServer.EscapeJson(a.ArtifactId)).Append("\"");
                    sb.Append(",\"version\":\"").Append(HttpServer.EscapeJson(a.Version)).Append("\"");
                    sb.Append(",\"packaging\":\"").Append(HttpServer.EscapeJson(a.Packaging)).Append("\"");
                    sb.Append(",\"size\":").Append(a.Size);
                    sb.Append(",\"lastModified\":\"").Append(HttpServer.EscapeJson(a.LastModified.ToString("yyyy-MM-dd HH:mm"))).Append("\"");
                    sb.Append(",\"localPath\":\"").Append(HttpServer.EscapeJson(a.LocalPath)).Append("\"");
                    sb.Append(",\"hasSources\":").Append(a.HasSources ? "true" : "false");
                    sb.Append(",\"hasJavadoc\":").Append(a.HasJavadoc ? "true" : "false");
                    sb.Append(",\"depCount\":").Append(a.DepCount);
                    sb.Append(",\"parseFailed\":").Append(a.ParseFailed ? "true" : "false");
                    sb.Append(",\"failReason\":\"").Append(HttpServer.EscapeJson(a.FailReason ?? "")).Append("\"");
                    sb.Append(",\"description\":\"").Append(HttpServer.EscapeJson(a.Description ?? "")).Append("\"");
                    sb.Append(",\"license\":\"").Append(HttpServer.EscapeJson(a.License ?? "")).Append("\"");
                    sb.Append(",\"isKmp\":").Append(a.IsKmp ? "true" : "false");
                    sb.Append(",\"kmpPlatforms\":[");
                    for (int k = 0; k < a.KmpPlatforms.Count; k++)
                    {
                        if (k > 0) sb.Append(",");
                        sb.Append("\"").Append(HttpServer.EscapeJson(a.KmpPlatforms[k])).Append("\"");
                    }
                    sb.Append("]");
                    sb.Append(",\"dependencies\":{");
                    int dc = 0;
                    foreach (var d in a.Dependencies)
                    {
                        if (dc++ > 0) sb.Append(",");
                        sb.Append("\"").Append(HttpServer.EscapeJson(d.Key)).Append("\":\"").Append(HttpServer.EscapeJson(d.Value ?? "")).Append("\"");
                    }
                    sb.Append("}");
                    sb.Append("}");
                }
                sb.Append("]}");

                HttpServer.ServeJson(response, 200, sb.ToString());
                return true;
            }
            else if (subPath == "search")
            {
                string q = request.QueryString["q"] ?? "";
                q = q.ToLower();

                List<MavenArtifactItem> matches = new List<MavenArtifactItem>();
                lock (mavenScanLock)
                {
                    if (cachedResult != null)
                    {
                        foreach (var a in cachedResult.Artifacts)
                        {
                            if (string.IsNullOrEmpty(q) ||
                                a.GroupId.ToLower().Contains(q) ||
                                a.ArtifactId.ToLower().Contains(q) ||
                                a.Version.ToLower().Contains(q))
                            {
                                matches.Add(a);
                            }
                        }
                    }
                }

                StringBuilder sb = new StringBuilder();
                sb.Append("[");
                for (int i = 0; i < matches.Count; i++)
                {
                    if (i > 0) sb.Append(",");
                    var a = matches[i];
                    sb.Append("{\"groupId\":\"").Append(HttpServer.EscapeJson(a.GroupId)).Append("\"");
                    sb.Append(",\"artifactId\":\"").Append(HttpServer.EscapeJson(a.ArtifactId)).Append("\"");
                    sb.Append(",\"version\":\"").Append(HttpServer.EscapeJson(a.Version)).Append("\"");
                    sb.Append(",\"packaging\":\"").Append(HttpServer.EscapeJson(a.Packaging)).Append("\"");
                    sb.Append(",\"size\":").Append(a.Size);
                    sb.Append(",\"hasSources\":").Append(a.HasSources ? "true" : "false");
                    sb.Append(",\"hasJavadoc\":").Append(a.HasJavadoc ? "true" : "false");
                    sb.Append(",\"depCount\":").Append(a.DepCount);
                    sb.Append(",\"localPath\":\"").Append(HttpServer.EscapeJson(a.LocalPath)).Append("\"");
                    sb.Append("}");
                }
                sb.Append("]");
                HttpServer.ServeJson(response, 200, "{\"success\":true,\"results\":" + sb.ToString() + ",\"total\":" + matches.Count + "}");
                return true;
            }
            else if (subPath == "refresh")
            {
                TriggerMavenScanAsync(true);
                HttpServer.ServeJson(response, 200, "{\"success\":true,\"message\":\"" + HttpServer.EscapeJson(I18nManager.T("api_gradle_scan_started")) + "\"}");
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
            else if (subPath == "pom")
            {
                string path = request.QueryString["path"];
                if (string.IsNullOrEmpty(path))
                {
                    HttpServer.ServeJson(response, 400, "{\"success\":false,\"message\":\"" + HttpServer.EscapeJson(I18nManager.T("api_missing_path")) + "\"}");
                    return true;
                }
                // 尝试多种 POM 文件名模式
                string[] candidates = new string[]
                {
                    Path.Combine(path, "pom.xml"),
                    path.EndsWith(".pom") ? path : ""
                };

                foreach (string candidate in candidates)
                {
                    if (string.IsNullOrEmpty(candidate)) continue;
                    if (File.Exists(candidate))
                    {
                        try
                        {
                            string content = File.ReadAllText(candidate, Encoding.UTF8);
                            HttpServer.ServeJson(response, 200, "{\"success\":true,\"content\":\"" + HttpServer.EscapeJson(content) + "\"}");
                            return true;
                        }
                        catch (Exception ex)
                        {
                            HttpServer.ServeJson(response, 500, "{\"success\":false,\"message\":\"" + HttpServer.EscapeJson(ex.Message) + "\"}");
                            return true;
                        }
                    }
                }

                // 在 versionDir 下查找任意 .pom 文件
                if (Directory.Exists(path))
                {
                    try
                    {
                        string[] pomFiles = Directory.GetFiles(path, "*.pom");
                        if (pomFiles.Length > 0 && File.Exists(pomFiles[0]))
                        {
                            string content = File.ReadAllText(pomFiles[0], Encoding.UTF8);
                            HttpServer.ServeJson(response, 200, "{\"success\":true,\"content\":\"" + HttpServer.EscapeJson(content) + "\"}");
                            return true;
                        }
                    }
                    catch { }
                }

                HttpServer.ServeJson(response, 404, "{\"success\":false,\"message\":\"" + HttpServer.EscapeJson(I18nManager.T("api_file_not_found")) + "\"}");
                return true;
            }
            else if (subPath == "clean-invalid" && request.HttpMethod == "POST")
            {
                string body;
                using (StreamReader reader = new StreamReader(request.InputStream, Encoding.UTF8))
                {
                    body = reader.ReadToEnd();
                }
                List<string> paths = ExtractStringArrayFromJson(body);
                EnsureMavenCacheLoaded();
                int cleaned = CleanInvalidByPaths(paths);
                HttpServer.ServeJson(response, 200, "{\"success\":true,\"cleaned\":" + cleaned + ",\"message\":\"Cleaned " + cleaned + " invalid directories.\"}");
                return true;
            }
            else if (subPath == "clean-preview" && request.HttpMethod == "GET")
            {
                EnsureMavenCacheLoaded();
                StringBuilder pb = new StringBuilder();
                int pcount = 0;
                lock (mavenScanLock)
                {
                    if (cachedResult != null)
                    {
                        foreach (var a in cachedResult.Artifacts)
                        {
                            string rt;
                            if (!IsInvalidArtifact(a, out rt)) continue;
                            if (pcount > 0) pb.Append(",");
                            pb.Append("{\"path\":\"").Append(HttpServer.EscapeJson(a.LocalPath))
                              .Append("\",\"coord\":\"").Append(HttpServer.EscapeJson(a.GroupId + ":" + a.ArtifactId + ":v" + a.Version))
                              .Append("\",\"size\":").Append(a.Size)
                              .Append(",\"reason\":\"").Append(rt).Append("\"}");
                            pcount++;
                        }
                    }
                }
                HttpServer.ServeJson(response, 200, "{\"success\":true,\"count\":" + pcount + ",\"items\":[" + pb.ToString() + "]}");
                return true;
            }
            else if (subPath == "retry-items" && request.HttpMethod == "POST")
            {
                string body;
                using (StreamReader reader = new StreamReader(request.InputStream, Encoding.UTF8))
                {
                    body = reader.ReadToEnd();
                }
                List<string> paths = ExtractStringArrayFromJson(body);
                EnsureMavenCacheLoaded();
                int retried = RetryParseItems(paths);
                Logger.Log(I18nManager.T("log_maven_retry_done", retried));
                HttpServer.ServeJson(response, 200, "{\"success\":true,\"retried\":" + retried + ",\"message\":\"Retried " + retried + " artifacts.\"}");
                return true;
            }
            else if (subPath == "delete-items" && request.HttpMethod == "POST")
            {
                string body;
                using (StreamReader reader = new StreamReader(request.InputStream, Encoding.UTF8))
                {
                    body = reader.ReadToEnd();
                }
                List<string> paths = ExtractStringArrayFromJson(body);
                EnsureMavenCacheLoaded();
                int deleted = DeleteArtifactsByPaths(paths);
                Logger.Log(I18nManager.T("log_maven_delete_done", deleted));
                HttpServer.ServeJson(response, 200, "{\"success\":true,\"deleted\":" + deleted + ",\"message\":\"Deleted " + deleted + " artifacts.\"}");
                return true;
            }
            else if (subPath == "item-files" && request.HttpMethod == "GET")
            {
                string path = request.QueryString["path"];
                if (string.IsNullOrEmpty(path) || !IsPathUnderRoot(path, GetDefaultMavenLocalRepo()))
                {
                    HttpServer.ServeJson(response, 403, "{\"success\":false,\"message\":\"Path outside repository root.\"}");
                    return true;
                }
                if (!Directory.Exists(path))
                {
                    HttpServer.ServeJson(response, 404, "{\"success\":false,\"message\":\"Directory not found.\"}");
                    return true;
                }

                StringBuilder fb = new StringBuilder();
                int count = 0;
                try
                {
                    foreach (string dir in Directory.GetDirectories(path))
                    {
                        if (count > 0) fb.Append(",");
                        fb.Append("{\"name\":\"").Append(HttpServer.EscapeJson("[dir] " + Path.GetFileName(dir))).Append("\",\"size\":0}");
                        count++;
                    }
                    foreach (string file in Directory.GetFiles(path))
                    {
                        if (count > 0) fb.Append(",");
                        FileInfo fi = new FileInfo(file);
                        fb.Append("{\"name\":\"").Append(HttpServer.EscapeJson(fi.Name)).Append("\",\"size\":").Append(fi.Length).Append("}");
                        count++;
                    }
                }
                catch (Exception ex)
                {
                    HttpServer.ServeJson(response, 500, "{\"success\":false,\"message\":\"" + HttpServer.EscapeJson(ex.Message) + "\"}");
                    return true;
                }

                HttpServer.ServeJson(response, 200, "{\"success\":true,\"path\":\"" + HttpServer.EscapeJson(path) + "\",\"count\":" + count + ",\"files\":[" + fb.ToString() + "]}");
                return true;
            }
            else if (subPath == "kmp-variants" && request.HttpMethod == "GET")
            {
                string path = request.QueryString["path"];
                if (string.IsNullOrEmpty(path) || !IsPathUnderRoot(path, GetDefaultMavenLocalRepo()))
                {
                    HttpServer.ServeJson(response, 403, "{\"success\":false,\"message\":\"Path outside repository root.\"}");
                    return true;
                }
                HandleGetKmpVariants(path, response);
                return true;
            }

            return false;
        }

        /// <summary>
        /// 从简单 JSON 字符串数组中提取元素（无需完整 JSON 反序列化）
        /// </summary>
        private static List<string> ExtractStringArrayFromJson(string body)
        {
            List<string> result = new List<string>();
            try
            {
                if (string.IsNullOrEmpty(body)) return result;
                int arrStart = body.IndexOf('[');
                int arrEnd = body.LastIndexOf(']');
                if (arrStart < 0 || arrEnd <= arrStart) return result;
                string inner = body.Substring(arrStart + 1, arrEnd - arrStart - 1);

                MatchCollection ms = Regex.Matches(inner, "\"((?:\\\\.|[^\"\\\\])*)\"");
                foreach (Match m in ms)
                {
                    string s = m.Groups[1].Value;
                    s = s.Replace("\\\\", "\u0001").Replace("\\\"", "\"").Replace("\\/", "/").Replace("\u0001", "\\");
                    if (!string.IsNullOrEmpty(s)) result.Add(s);
                }
            }
            catch { }
            return result;
        }

        /// <summary>
        /// 校验目标路径位于仓库根目录之下（防止越权删除/修改）
        /// </summary>
        private static bool IsPathUnderRoot(string path, string root)
        {
            try
            {
                if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(root)) return false;
                string fullP = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string fullR = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                return fullP.StartsWith(fullR + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        /// <summary>
        /// 对指定版本目录重新执行 POM 解析，解析成功则替换缓存条目
        /// </summary>
        private static int RetryParseItems(List<string> paths)
        {
            if (paths == null || paths.Count == 0) return 0;
            int retried = 0;
            lock (mavenScanLock)
            {
                if (cachedResult == null || string.IsNullOrEmpty(cachedResult.LocalRepoPath)) return 0;

                for (int i = 0; i < cachedResult.Artifacts.Count; i++)
                {
                    MavenArtifactItem old = cachedResult.Artifacts[i];
                    bool matched = false;
                    foreach (string p in paths)
                    {
                        if (string.Equals(old.LocalPath, p, StringComparison.OrdinalIgnoreCase)) { matched = true; break; }
                    }
                    if (!matched) continue;
                    if (!IsPathUnderRoot(old.LocalPath, cachedResult.LocalRepoPath)) continue;

                    MavenArtifactItem fresh = ParseArtifact(old.LocalPath, old.GroupId, old.ArtifactId, old.Version);
                    if (fresh != null && !fresh.ParseFailed)
                    {
                        cachedResult.TotalSize += fresh.Size - old.Size;
                        cachedResult.Artifacts[i] = fresh;
                        retried++;
                    }
                }
            }
            if (retried > 0)
            {
                SaveToDiskCache(cachedRepoTicks);
                Logger.Log(I18nManager.T("log_dev_ecosystem_verified"));
            }
            return retried;
        }

        /// <summary>
        /// KMP 平台变体查询：由版本目录定位根模块 .module 元数据 →
        /// 以平台 token 生成变体候选坐标在文本中匹配出声明集 →
        /// 再对每个声明变体做磁盘存在性判定（已下载取目录内文件总大小）。
        /// 纯本地离线推断，不发起网络请求。
        /// </summary>
        private static void HandleGetKmpVariants(string path, HttpListenerResponse response)
        {
            try
            {
                DirectoryInfo vDir = new DirectoryInfo(path);
                if (vDir == null || vDir.Parent == null || !Directory.Exists(path))
                {
                    HttpServer.ServeJson(response, 404, "{\"success\":false,\"message\":\"Directory not found.\"}");
                    return;
                }
                DirectoryInfo rootAidDir = vDir.Parent;
                string rootAid = rootAidDir.Name;
                string version = vDir.Name;
                string repoRoot = GetDefaultMavenLocalRepo();

                // 1. 定位根模块 Gradle Module 元数据（优先 {rootAid}-{version}.module）
                string moduleFile = null;
                try
                {
                    string primary = Path.Combine(path, rootAid + "-" + version + ".module");
                    if (File.Exists(primary))
                    {
                        moduleFile = primary;
                    }
                    else
                    {
                        foreach (string f in Directory.GetFiles(path, "*.module"))
                        {
                            if (new FileInfo(f).Length < 5 * 1024 * 1024) { moduleFile = f; break; }
                        }
                    }
                }
                catch { }

                // 2. 计算组相对路径（repoRoot 之下的 group 段，不含 artifactId 目录本身）
                string groupRel = "";
                try
                {
                    DirectoryInfo groupDir = rootAidDir.Parent;
                    if (groupDir != null)
                    {
                        string dirFull = groupDir.FullName.TrimEnd('\\');
                        string rootTrim = repoRoot.TrimEnd('\\');
                        if (dirFull.Length > rootTrim.Length + 1)
                        {
                            groupRel = dirFull.Substring(rootTrim.Length + 1);
                        }
                    }
                }
                catch { }

                StringBuilder ib = new StringBuilder();
                int icount = 0;
                if (!string.IsNullOrEmpty(moduleFile) && groupRel != "")
                {
                    string text = "";
                    using (StreamReader sr = new StreamReader(moduleFile, Encoding.UTF8)) text = sr.ReadToEnd();
                    if (text.Length > 5 * 1024 * 1024) text = "";
                    string lower = text.ToLowerInvariant();
                    string aidLower = rootAid.ToLowerInvariant();

                    foreach (string[] tk in KmpPlatformTokens)
                    {
                        if (!lower.Contains(aidLower + "-" + tk[0])) continue;

                        string candidatePath = Path.Combine(repoRoot, groupRel, rootAid + "-" + tk[0], version);
                        bool downloaded = Directory.Exists(candidatePath);
                        long size = 0;
                        if (downloaded)
                        {
                            try
                            {
                                foreach (string f in Directory.GetFiles(candidatePath)) size += new FileInfo(f).Length;
                            }
                            catch { }
                        }

                        if (icount > 0) ib.Append(",");
                        ib.Append("{\"name\":\"").Append(HttpServer.EscapeJson(tk[1]))
                          .Append("\",\"downloaded\":").Append(downloaded ? "true" : "false")
                          .Append(",\"size\":").Append(size)
                          .Append(",\"localPath\":\"").Append(HttpServer.EscapeJson(downloaded ? candidatePath : "")).Append("\"}");
                        icount++;
                    }
                }

                HttpServer.ServeJson(response, 200, "{\"success\":true,\"root\":\"" + HttpServer.EscapeJson(rootAid) + "\",\"items\":[" + ib.ToString() + "]}");
            }
            catch (Exception ex)
            {
                HttpServer.ServeJson(response, 500, "{\"success\":false,\"message\":\"" + HttpServer.EscapeJson(ex.Message) + "\"}");
            }
        }

        /// <summary>
        /// 无效缓存判定：
        /// metadata_only = 目录存在且有文件，但无任何主构件（.jar 非 sources/javadoc、.pom），即仅剩下载残片；
        /// missing_dir   = 索引中有记录但磁盘目录已不存在。
        /// </summary>
        private static bool IsInvalidArtifact(MavenArtifactItem a, out string reasonType)
        {
            reasonType = null;
            if (a == null || string.IsNullOrEmpty(a.LocalPath)) return false;
            if (!Directory.Exists(a.LocalPath))
            {
                reasonType = "missing_dir";
                return true;
            }
            try
            {
                string[] files = Directory.GetFiles(a.LocalPath);
                if (files.Length == 0) return false;
                foreach (string f in files)
                {
                    string fn = Path.GetFileName(f).ToLowerInvariant();
                    if (fn.EndsWith(".jar") && !fn.Contains("-sources") && !fn.Contains("-javadoc")) return false;
                    if (fn.EndsWith(".pom")) return false;
                }
                reasonType = "metadata_only";
                return true;
            }
            catch { return false; }
        }

        /// <summary>
        /// 仅清理指定路径中通过校验的无效项：A 类物理删除目录，B 类仅移除索引；同步内存缓存并回写快照
        /// </summary>
        private static int CleanInvalidByPaths(List<string> paths)
        {
            if (paths == null || paths.Count == 0) return 0;
            HashSet<string> wanted = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
            int cleaned = 0;
            lock (mavenScanLock)
            {
                if (cachedResult == null || string.IsNullOrEmpty(cachedResult.LocalRepoPath)) return 0;
                for (int i = cachedResult.Artifacts.Count - 1; i >= 0; i--)
                {
                    MavenArtifactItem a = cachedResult.Artifacts[i];
                    if (!wanted.Contains(a.LocalPath)) continue;
                    string rt;
                    if (!IsInvalidArtifact(a, out rt)) continue;
                    if (!IsPathUnderRoot(a.LocalPath, cachedResult.LocalRepoPath)) continue;

                    bool removed = true;
                    if (rt == "metadata_only")
                    {
                        try { Directory.Delete(a.LocalPath, true); }
                        catch (Exception ex)
                        {
                            Logger.Log("Clean invalid failed [" + a.LocalPath + "]: " + ex.Message);
                            removed = false;
                        }
                    }
                    if (!removed) continue;
                    cachedResult.TotalSize -= a.Size;
                    cachedResult.Artifacts.RemoveAt(i);
                    cachedResult.TotalArtifacts = cachedResult.Artifacts.Count;
                    cleaned++;
                }
            }
            if (cleaned > 0)
            {
                SaveToDiskCache(cachedRepoTicks);
            }
            return cleaned;
        }

        /// <summary>
        /// 变更类接口（重试/删除）的兜底保障：若进程启动后 Maven 缓存尚未完成懒加载，
        /// 则同步从磁盘快照载入一次（毫秒级），避免请求静默空操作
        /// </summary>
        private static void EnsureMavenCacheLoaded()
        {
            lock (mavenScanLock)
            {
                if (cachedResult != null) return;
                long sTicks;
                if (TryLoadFromDiskCache(out sTicks))
                {
                    Logger.Log("[Maven] 变更接口兜底同步载入缓存快照成功");
                }
            }
        }

        /// <summary>
        /// 删除磁盘上的构件目录（含校验仅限仓库根之内），并从缓存列表移除
        /// </summary>
        private static int DeleteArtifactsByPaths(List<string> paths)
        {
            if (paths == null || paths.Count == 0) return 0;
            int deleted = 0;
            lock (mavenScanLock)
            {
                if (cachedResult == null || string.IsNullOrEmpty(cachedResult.LocalRepoPath)) return 0;

                for (int i = cachedResult.Artifacts.Count - 1; i >= 0; i--)
                {
                    MavenArtifactItem a = cachedResult.Artifacts[i];
                    bool matched = false;
                    foreach (string p in paths)
                    {
                        if (string.Equals(a.LocalPath, p, StringComparison.OrdinalIgnoreCase)) { matched = true; break; }
                    }
                    if (!matched) continue;
                    if (!IsPathUnderRoot(a.LocalPath, cachedResult.LocalRepoPath)) continue;

                    try
                    {
                        if (Directory.Exists(a.LocalPath))
                        {
                            Directory.Delete(a.LocalPath, true);
                        }
                        cachedResult.TotalSize -= a.Size;
                        cachedResult.Artifacts.RemoveAt(i);
                        cachedResult.TotalArtifacts = cachedResult.Artifacts.Count;
                        deleted++;
                    }
                    catch (Exception ex)
                    {
                        Logger.Log("Delete artifact failed [" + a.LocalPath + "]: " + ex.Message);
                    }
                }
            }
            if (deleted > 0)
            {
                SaveToDiskCache(cachedRepoTicks);
                Logger.Log(I18nManager.T("log_dev_ecosystem_verified"));
            }
            return deleted;
        }

        #endregion
    }
}
