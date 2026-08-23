using System;
using System.IO;
using System.Net;
using System.Text;
using System.Collections.Generic;
using System.Windows.Forms;
using System.Xml;
using CachedDependency = LocalDiskServer.ServerApplicationContext.CachedDependency;

namespace LocalDiskServer
{
    public static class GradleExplorer
    {
        public static bool isGradleScanning = false;
        public static readonly object gradleScanLock = new object();
        public static string cachedGradleHome = "";
        public static string cachedWrappersJson = "[]";
        public static int cachedDependencyCount = 0;
        public static int cachedKmpCount = 0;
        public static long cachedTotalSize = 0;

        public static readonly List<CachedDependency> cachedDependencies = new List<CachedDependency>();

        public static string GetCacheFilePath()
        {
            try
            {
                string cacheDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "cache");
                if (!Directory.Exists(cacheDir))
                {
                    Directory.CreateDirectory(cacheDir);
                }
                return Path.Combine(cacheDir, "gradle_cache.dat");
            }
            catch
            {
                return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "gradle_cache.dat");
            }
        }

        public static void ClearCacheAndReleaseResources()
        {
            lock (gradleScanLock)
            {
                cachedDependencies.Clear();
                cachedDependencies.TrimExcess();
                cachedWrappersJson = "[]";
                cachedDependencyCount = 0;
                cachedKmpCount = 0;
                cachedTotalSize = 0;
                cachedGradleHome = "";
            }
            GC.Collect();
            Logger.Log(I18nManager.T("log_dev_ecosystem_released"));
        }

        public static void SaveToDiskCache(long distsTicks, long filesTicks)
        {
            try
            {
                string cacheFile = GetCacheFilePath();
                StringBuilder sb = new StringBuilder();
                sb.AppendFormat("#META:{0}|{1}|{2}|{3}|{4}|{5}", distsTicks, filesTicks, cachedDependencyCount, cachedKmpCount, cachedTotalSize, cachedGradleHome ?? "").AppendLine();
                sb.AppendLine("#WRAPPERS:" + (cachedWrappersJson ?? "[]"));
                lock (gradleScanLock)
                {
                    for (int i = 0; i < cachedDependencies.Count; i++)
                    {
                        var d = cachedDependencies[i];
                        sb.AppendFormat("{0}|{1}|{2}|{3}|{4}|{5}",
                            d.Group ?? "", d.Artifact ?? "", d.Version ?? "",
                            d.IsKmp ? "1" : "0", d.FriendlySize ?? "", d.LocalPath ?? "").AppendLine();
                    }
                }
                File.WriteAllText(cacheFile, sb.ToString(), Encoding.UTF8);
                Logger.Log(I18nManager.T("log_dev_ecosystem_saved", cacheFile));
            }
            catch (Exception ex)
            {
                Logger.Log("SaveToDiskCache Exception: " + ex.Message);
            }
        }

        public static bool TryLoadFromDiskCache(out long distsTicks, out long filesTicks)
        {
            distsTicks = 0;
            filesTicks = 0;
            try
            {
                string cacheFile = GetCacheFilePath();
                if (!File.Exists(cacheFile)) return false;

                var sw = System.Diagnostics.Stopwatch.StartNew();
                string[] lines = File.ReadAllLines(cacheFile, Encoding.UTF8);
                if (lines == null || lines.Length < 2) return false;

                string metaLine = lines[0];
                if (!metaLine.StartsWith("#META:")) return false;
                string[] metaParts = metaLine.Substring(6).Split('|');
                if (metaParts.Length < 6) return false;

                long.TryParse(metaParts[0], out distsTicks);
                long.TryParse(metaParts[1], out filesTicks);
                int depCount = 0; int.TryParse(metaParts[2], out depCount);
                int kmpCount = 0; int.TryParse(metaParts[3], out kmpCount);
                long totalSize = 0; long.TryParse(metaParts[4], out totalSize);
                string gHome = metaParts[5];

                string wrapLine = lines[1];
                string wrappers = wrapLine.StartsWith("#WRAPPERS:") ? wrapLine.Substring(10) : "[]";

                List<CachedDependency> deps = new List<CachedDependency>(Math.Max(32, lines.Length - 2));
                for (int i = 2; i < lines.Length; i++)
                {
                    string line = lines[i];
                    if (string.IsNullOrEmpty(line)) continue;
                    string[] parts = line.Split('|');
                    if (parts.Length >= 6)
                    {
                        deps.Add(new CachedDependency
                        {
                            Group = parts[0],
                            Artifact = parts[1],
                            Version = parts[2],
                            IsKmp = parts[3] == "1",
                            FriendlySize = parts[4],
                            LocalPath = parts[5]
                        });
                    }
                }

                lock (gradleScanLock)
                {
                    cachedGradleHome = gHome;
                    cachedWrappersJson = wrappers;
                    cachedDependencyCount = depCount;
                    cachedKmpCount = kmpCount;
                    cachedTotalSize = totalSize;
                    cachedDependencies.Clear();
                    cachedDependencies.AddRange(deps);
                }

                sw.Stop();
                Logger.Log(I18nManager.T("log_dev_ecosystem_fast_loaded", "Gradle", deps.Count, sw.ElapsedMilliseconds));
                return true;
            }
            catch (Exception ex)
            {
                Logger.Log("TryLoadFromDiskCache Exception: " + ex.Message);
                return false;
            }
        }

        public static void TriggerGradleScanAsync(bool forceRescan = false)
        {
            if (!ServerApplicationContext.enable_dev_ecosystem) return;

            lock (gradleScanLock)
            {
                if (isGradleScanning) return;
                isGradleScanning = true;
            }

            System.Threading.ThreadPool.QueueUserWorkItem(delegate {
                try
                {
                    if (!ServerApplicationContext.enable_dev_ecosystem) return;

                    string gHome = GetGradleHome();
                    cachedGradleHome = gHome;
                    if (string.IsNullOrEmpty(gHome))
                    {
                        lock (gradleScanLock)
                        {
                            cachedWrappersJson = "[]";
                            cachedDependencyCount = 0;
                            cachedKmpCount = 0;
                            cachedTotalSize = 0;
                            cachedDependencies.Clear();
                        }
                        return;
                    }

                    string distsPath = Path.Combine(gHome, "wrapper", "dists");
                    string files21Path = Path.Combine(gHome, "caches", "modules-2", "files-2.1");

                    long curDistsTicks = Directory.Exists(distsPath) ? Directory.GetLastWriteTimeUtc(distsPath).Ticks : 0;
                    long curFilesTicks = Directory.Exists(files21Path) ? Directory.GetLastWriteTimeUtc(files21Path).Ticks : 0;

                    // 1. 如果非强制重扫且内存为空，优先尝试从磁盘快照秒级冷启动
                    if (!forceRescan && cachedDependencies.Count == 0)
                    {
                        long cachedDistsTicks, cachedFilesTicks;
                        if (TryLoadFromDiskCache(out cachedDistsTicks, out cachedFilesTicks))
                        {
                            // 2. 毫秒级时间戳比对探活
                            if (curDistsTicks == cachedDistsTicks && curFilesTicks == cachedFilesTicks)
                            {
                                Logger.Log(I18nManager.T("log_dev_ecosystem_verified"));
                                return; // 数据100%真实一致，探活成功退出！
                            }
                        }
                    }
                    else if (!forceRescan && cachedDependencies.Count > 0)
                    {
                        // 内存已有数据，仅读取元数据比对
                        long cachedDistsTicks, cachedFilesTicks;
                        if (ReadMetadataTicks(out cachedDistsTicks, out cachedFilesTicks))
                        {
                            if (curDistsTicks == cachedDistsTicks && curFilesTicks == cachedFilesTicks)
                            {
                                Logger.Log(I18nManager.T("log_dev_ecosystem_verified"));
                                return;
                            }
                        }
                    }

                    // 3. 执行真实物理扫描
                    Logger.Log(I18nManager.T("log_gradle_scan_thread_started"));
                    DoGradleScan(gHome, distsPath, files21Path, curDistsTicks, curFilesTicks);
                }
                catch (Exception ex)
                {
                    Logger.Log(I18nManager.T("log_gradle_scan_ex", ex.Message));
                }
                finally
                {
                    lock (gradleScanLock)
                    {
                        isGradleScanning = false;
                    }
                }
            });
        }

        private static bool ReadMetadataTicks(out long distsTicks, out long filesTicks)
        {
            distsTicks = 0;
            filesTicks = 0;
            try
            {
                string cacheFile = GetCacheFilePath();
                if (!File.Exists(cacheFile)) return false;
                using (var reader = new StreamReader(cacheFile, Encoding.UTF8))
                {
                    string metaLine = reader.ReadLine();
                    if (metaLine != null && metaLine.StartsWith("#META:"))
                    {
                        string[] parts = metaLine.Substring(6).Split('|');
                        if (parts.Length >= 2)
                        {
                            long.TryParse(parts[0], out distsTicks);
                            long.TryParse(parts[1], out filesTicks);
                            return true;
                        }
                    }
                }
            }
            catch {}
            return false;
        }

        private static void DoGradleScan(string gHome, string distsPath, string files21Path, long distsTicks, long filesTicks)
        {
            if (!ServerApplicationContext.enable_dev_ecosystem) return;

            // 1. Scan Wrappers
            StringBuilder wrappersJson = new StringBuilder();
            wrappersJson.Append("[");
            if (Directory.Exists(distsPath))
            {
                string[] dirs = Directory.GetDirectories(distsPath);
                for (int i = 0; i < dirs.Length; i++)
                {
                    string dName = Path.GetFileName(dirs[i]);
                    long size = GetDirSize(dirs[i]);
                    int fileCount = 0;
                    try 
                    { 
                        System.Collections.Generic.List<string> wFiles = new System.Collections.Generic.List<string>();
                        SafeGetFiles(dirs[i], "*", wFiles);
                        fileCount = wFiles.Count;
                    } 
                    catch {}
                    string friendlyVersion = dName.Replace("gradle-", "").Replace("-all", "").Replace("-bin", "");
                    
                    if (i > 0) wrappersJson.Append(",");
                    wrappersJson.AppendFormat("{{\"version\":\"{0}\",\"fullName\":\"{1}\",\"size\":\"{2}\",\"files\":{3},\"path\":\"{4}\"}}",
                        HttpServer.EscapeJson(friendlyVersion), HttpServer.EscapeJson(dName), HttpServer.FormatFileSize(size), fileCount, HttpServer.EscapeJson(dirs[i].Replace("\\", "\\\\")));
                }
            }
            wrappersJson.Append("]");
            string tmpWrappers = wrappersJson.ToString();

            // 2. Scan Dependencies
            System.Collections.Generic.List<CachedDependency> tmpDeps = new System.Collections.Generic.List<CachedDependency>();
            int depCount = 0;
            int kmpCount = 0;
            long totalSize = 0;

            if (Directory.Exists(files21Path))
            {
                System.Collections.Generic.List<string> pomList = new System.Collections.Generic.List<string>();
                SafeGetFiles(files21Path, "*.pom", pomList);
                string[] pomFiles = pomList.ToArray();
                depCount = pomFiles.Length;
                
                foreach (string pom in pomFiles)
                {
                    string parentDir = SafeGetDirectoryName(pom);
                    string versionDir = SafeGetDirectoryName(parentDir);
                    if (Directory.Exists(versionDir))
                    {
                        bool isKmp = false;
                        try
                        {
                            isKmp = SafeHasFile(versionDir, "*.module");
                        }
                        catch {}
                        if (isKmp)
                        {
                            kmpCount++;
                        }
                        
                        string[] parts = pom.Split(new char[] { Path.DirectorySeparatorChar, '/' }, StringSplitOptions.RemoveEmptyEntries);
                        int filesIdx = -1;
                        for (int i = 0; i < parts.Length; i++)
                        {
                            if (parts[i].Equals("files-2.1", StringComparison.OrdinalIgnoreCase))
                            {
                                filesIdx = i;
                                break;
                            }
                        }
                        if (filesIdx != -1 && filesIdx + 3 < parts.Length)
                        {
                            string group = parts[filesIdx + 1];
                            string artifact = parts[filesIdx + 2];
                            string version = parts[filesIdx + 3];
                            long size = GetDirSize(versionDir);
                            
                            tmpDeps.Add(new CachedDependency
                            {
                                Group = group,
                                Artifact = artifact,
                                Version = version,
                                IsKmp = isKmp,
                                FriendlySize = HttpServer.FormatFileSize(size),
                                LocalPath = versionDir
                            });
                        }
                    }
                }
                totalSize = GetDirSize(files21Path);
            }

            lock (gradleScanLock)
            {
                cachedWrappersJson = tmpWrappers;
                cachedDependencyCount = depCount;
                cachedKmpCount = kmpCount;
                cachedTotalSize = totalSize;
                cachedDependencies.Clear();
                cachedDependencies.AddRange(tmpDeps);
            }
            SaveToDiskCache(distsTicks, filesTicks);
            Logger.Log(I18nManager.T("log_gradle_scan_finished", depCount, kmpCount, HttpServer.FormatFileSize(totalSize)));
        }

        private static string GetGradleHome()
        {
            string envHome = Environment.GetEnvironmentVariable("GRADLE_USER_HOME", EnvironmentVariableTarget.Process);
            if (string.IsNullOrEmpty(envHome))
            {
                envHome = Environment.GetEnvironmentVariable("GRADLE_USER_HOME", EnvironmentVariableTarget.User);
            }
            if (string.IsNullOrEmpty(envHome))
            {
                envHome = Environment.GetEnvironmentVariable("GRADLE_USER_HOME", EnvironmentVariableTarget.Machine);
            }

            if (!string.IsNullOrEmpty(envHome))
            {
                try
                {
                    // 展开可能包含的嵌套环境变量（如 %USERPROFILE%）
                    envHome = Environment.ExpandEnvironmentVariables(envHome);
                    // 清理可能误带的双引号、单引号或首尾空格
                    envHome = envHome.Trim('\"', '\'', ' ', '\t');
                    if (Directory.Exists(envHome))
                    {
                        return envHome;
                    }
                }
                catch { }
            }

            string realGradle = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".gradle");
            if (Directory.Exists(realGradle))
            {
                return realGradle;
            }
            return null;
        }

        public static void ServeGradleDashboard(HttpListenerResponse response)
        {
            if (!ServerApplicationContext.enable_dev_ecosystem)
            {
                response.Redirect("/");
                response.OutputStream.Close();
                return;
            }

            StringBuilder sb = new StringBuilder();
            sb.Append(HttpServer.GetHtmlHeader(I18nManager.T("gradle_page_title"), "", "layout-explorer"));
            sb.Append("<script>const currentView = 'gradle';</script>");

            var favList = FileExplorer.GetFavorites();

            // Left Sidebar Tree Pane
            sb.Append("<div class='explorer-sidebar' id='sidebar-pane'>");
            sb.AppendFormat("  <div class='sidebar-expand-btn' onclick='toggleSidebar(\"left\")' style='display: none;'>{0}</div>", I18nManager.T("nav_btn_expand"));
            sb.Append("  <div class='sidebar-title' style='display: flex; justify-content: space-between; align-items: center; width: 100%;'>");
            sb.AppendFormat("    <span>📂 {0}</span>", I18nManager.T("nav_title"));
            sb.AppendFormat("    <span class='sidebar-toggle-btn' onclick='toggleSidebar(\"left\"); event.stopPropagation();' style='cursor: pointer; font-size: 0.8rem; color: var(--text-muted); padding: 2px 6px; border-radius: 4px;' title='{0}'>◀</span>", I18nManager.T("nav_btn_collapse"));
            sb.Append("  </div>");
            sb.Append("  <div class='tree-container'>");
            
            // 1. Home Node
            sb.Append("    <div class='tree-node root-node'>");
            sb.AppendFormat("      <a href='/' class='tree-link'>🏠 {0}</a>", I18nManager.T("nav_home"));
            sb.Append("    </div>");

            // 2. Quick Access Node
            sb.Append("    <div class='tree-node branch-node' id='node-quick-access'>");
            sb.Append("      <div class='tree-row' onclick='toggleTreeNode(\"quick-access\")'>");
            sb.Append("        <span class='tree-arrow'>▼</span>");
            sb.Append("        <span class='tree-folder-icon'>🚀</span>");
            sb.AppendFormat("        <span class='tree-text'>{0}</span>", I18nManager.T("quick_access_title"));
            sb.Append("      </div>");
            sb.Append("      <div class='tree-children' id='children-quick-access'>");

            var quickItems = FileExplorer.GetStandardQuickAccessItems();
            foreach (var q in quickItems)
            {
                sb.AppendFormat("        <a href='{0}' class='tree-link' title='{1}'>{2} {3}</a>",
                    q.WebPath, q.PhysicalPath.Replace("'", "\\'"), q.Emoji, q.Title);
            }

            sb.Append("      </div>");
            sb.Append("    </div>");

            // 3. Favorites Node
            sb.Append("    <div class='tree-node branch-node' id='node-favorites'>");
            sb.Append("      <div class='tree-row' onclick='toggleTreeNode(\"favorites\")'>");
            sb.Append("        <span class='tree-arrow'>▼</span>");
            sb.Append("        <span class='tree-folder-icon'>⭐</span>");
            sb.AppendFormat("        <span class='tree-text'>{0}</span>", I18nManager.T("nav_favorites"));
            sb.Append("      </div>");
            sb.Append("      <div class='tree-children' id='children-favorites'>");
            foreach (string fav in favList)
            {
                if (Directory.Exists(fav))
                {
                    string fName = Path.GetFileName(fav);
                    if (string.IsNullOrEmpty(fName)) fName = fav;
                    string webLink = HttpServer.PhysicalToWebPath(fav);
                    sb.AppendFormat("        <a href='{0}' class='tree-link' title='{1}'>📁 {2}</a>", webLink, fav.Replace("'", "\\'"), fName);
                }
            }
            sb.Append("      </div>");
            sb.Append("    </div>");

            // 4. Drives Node
            sb.Append("    <div class='tree-node branch-node' id='node-drives'>");
            sb.Append("      <div class='tree-row' onclick='toggleTreeNode(\"drives\")'>");
            sb.Append("        <span class='tree-arrow'>▼</span>");
            sb.Append("        <span class='tree-folder-icon'>💾</span>");
            sb.AppendFormat("        <span class='tree-text'>{0}</span>", I18nManager.T("nav_drives"));
            sb.Append("      </div>");
            sb.Append("      <div class='tree-children' id='children-drives'>");
            var drives = DriveInfo.GetDrives();
            foreach (var d in drives)
            {
                if (d.IsReady)
                {
                    string dPath = d.Name;
                    string dName = d.Name.TrimEnd('\\');
                    string dWeb = "/" + dName.ToLower().Replace(":", "") + "/";
                    sb.AppendFormat("        <div class='tree-node'>");
                    sb.AppendFormat("          <div class='tree-row' data-path='{0}'>", dPath.Replace("\\", "\\\\").Replace("'", "\\'"));
                    sb.AppendFormat("            <span class='tree-arrow collapsed' onclick='expandTreeNode(event, \"{0}\")'>▶</span>", dPath.Replace("\\", "\\\\").Replace("'", "\\'"));
                    sb.AppendFormat("            <a href='{0}' class='tree-link-inline' style='color:inherit;'>💽 {1}</a>", dWeb, dName);
                    sb.AppendFormat("          </div>");
                    sb.AppendFormat("          <div class='tree-children' id='dir-{0}' style='display:none;'></div>", dPath.Replace("\\", "_").Replace(":", "_"));
                    sb.AppendFormat("        </div>");
                }
            }
            sb.Append("      </div>");
            sb.Append("    </div>");

            // 5. Developer Ecosystem Node (Gradle active!)
            sb.Append("    <div class='tree-node root-node' style='margin-top: 10px; border-top: 1px solid var(--border-color); padding-top: 8px;'>");
            sb.Append("      <div class='tree-row'>");
            sb.Append("        <span class='tree-arrow' onclick='toggleDevEcosystem(event)'>▼</span>");
            sb.AppendFormat("        <span class='tree-label' style='font-weight: bold; cursor: pointer;' onclick='toggleDevEcosystem(event)'>📦 {0}</span>", I18nManager.T("nav_dev_ecosystem"));
            sb.Append("      </div>");
            sb.Append("      <div class='tree-children' id='children-dev-ecosystem'>");
            sb.AppendFormat("        <div class='tree-node'><div class='tree-row active'><a href='/?view=gradle' class='tree-link-inline active-node' style='color:inherit; font-weight: bold;'>☕ {0}</a></div></div>", I18nManager.T("nav_gradle"));
            sb.AppendFormat("        <div class='tree-node'><div class='tree-row' style='opacity: 0.65;' title='{1}'><span class='tree-link-inline' style='color:inherit; cursor: default;'>🪶 {0} <span class='dev-badge plan'>{1}</span></span></div></div>", I18nManager.T("nav_maven"), I18nManager.T("tag_coming_soon"));
            sb.AppendFormat("        <div class='tree-node'><div class='tree-row' style='opacity: 0.65;' title='{1}'><span class='tree-link-inline' style='color:inherit; cursor: default;'>📦 {0} <span class='dev-badge plan'>{1}</span></span></div></div>", I18nManager.T("nav_npm"), I18nManager.T("tag_coming_soon"));
            sb.AppendFormat("        <div class='tree-node'><div class='tree-row' style='opacity: 0.65;' title='{1}'><span class='tree-link-inline' style='color:inherit; cursor: default;'>⚡ {0} <span class='dev-badge plan'>{1}</span></span></div></div>", I18nManager.T("nav_pnpm"), I18nManager.T("tag_coming_soon"));
            sb.AppendFormat("        <div class='tree-node'><div class='tree-row' style='opacity: 0.65;' title='{1}'><span class='tree-link-inline' style='color:inherit; cursor: default;'>🤖 {0} <span class='dev-badge plan'>{1}</span></span></div></div>", I18nManager.T("nav_android"), I18nManager.T("tag_coming_soon"));
            sb.Append("      </div>");
            sb.Append("    </div>");

            sb.Append("  </div>");
            sb.Append("</div>");

            // Load and append middle & right column layout from gradle.html
            string gradleHtml = HttpServer.LoadResource("gradle.html");
            gradleHtml = gradleHtml.Replace("{GRADLE_BREADCRUMB_HOME}", I18nManager.T("gradle_breadcrumb_home"));
            gradleHtml = gradleHtml.Replace("{GRADLE_PAGE_TITLE}", I18nManager.T("gradle_page_title"));
            gradleHtml = gradleHtml.Replace("{LOBBY_PROTO_TOGGLE_TITLE}", I18nManager.T("lobby_proto_toggle_title"));
            gradleHtml = gradleHtml.Replace("{GRADLE_SEARCH_PLACEHOLDER}", I18nManager.T("gradle_search_placeholder"));
            gradleHtml = gradleHtml.Replace("{GRADLE_BTN_RESCAN}", I18nManager.T("gradle_btn_rescan"));
            gradleHtml = gradleHtml.Replace("{GRADLE_SUMMARY_TITLE}", I18nManager.T("gradle_summary_title"));
            gradleHtml = gradleHtml.Replace("{GRADLE_STAT_HOME}", I18nManager.T("gradle_stat_home"));
            gradleHtml = gradleHtml.Replace("{GRADLE_STAT_COUNT}", I18nManager.T("gradle_stat_count"));
            gradleHtml = gradleHtml.Replace("{GRADLE_STAT_KMP}", I18nManager.T("gradle_stat_kmp"));
            gradleHtml = gradleHtml.Replace("{GRADLE_STAT_SIZE}", I18nManager.T("gradle_stat_size"));
            gradleHtml = gradleHtml.Replace("{GRADLE_WRAPPERS_TITLE}", I18nManager.T("gradle_wrappers_title"));
            gradleHtml = gradleHtml.Replace("{GRADLE_WRAPPERS_SCANNING}", I18nManager.T("gradle_wrappers_scanning"));
            gradleHtml = gradleHtml.Replace("{GRADLE_LIST_TITLE}", I18nManager.T("gradle_list_title"));
            gradleHtml = gradleHtml.Replace("{GRADLE_TH_COORD}", I18nManager.T("gradle_th_coord"));
            gradleHtml = gradleHtml.Replace("{GRADLE_TH_VERSION}", I18nManager.T("gradle_th_version"));
            gradleHtml = gradleHtml.Replace("{GRADLE_TH_KMP}", I18nManager.T("gradle_th_kmp"));
            gradleHtml = gradleHtml.Replace("{GRADLE_TH_SIZE}", I18nManager.T("gradle_th_size"));
            gradleHtml = gradleHtml.Replace("{GRADLE_LOADING}", I18nManager.T("gradle_loading"));
            gradleHtml = gradleHtml.Replace("{GRADLE_PAGINATION_INFO}", I18nManager.T("gradle_pagination_info", 1, 1, 0));
            gradleHtml = gradleHtml.Replace("{GRADLE_PAGE_SIZE_LABEL}", I18nManager.T("gradle_page_size_label"));
            gradleHtml = gradleHtml.Replace("{GRADLE_PAGE_FIRST}", I18nManager.T("gradle_page_first"));
            gradleHtml = gradleHtml.Replace("{GRADLE_PAGE_PREV}", I18nManager.T("gradle_page_prev"));
            gradleHtml = gradleHtml.Replace("{GRADLE_PAGE_NEXT}", I18nManager.T("gradle_page_next"));
            gradleHtml = gradleHtml.Replace("{GRADLE_PAGE_LAST}", I18nManager.T("gradle_page_last"));
            gradleHtml = gradleHtml.Replace("{PREVIEW_BTN_EXPAND}", I18nManager.T("preview_btn_expand"));
            gradleHtml = gradleHtml.Replace("{GRADLE_DETAIL_TITLE}", I18nManager.T("gradle_detail_title"));
            gradleHtml = gradleHtml.Replace("{PREVIEW_BTN_COLLAPSE}", I18nManager.T("preview_btn_collapse"));
            gradleHtml = gradleHtml.Replace("{GRADLE_DETAIL_EMPTY}", I18nManager.T("gradle_detail_empty"));
            gradleHtml = gradleHtml.Replace("{GRADLE_MODAL_VERSIONS}", I18nManager.T("gradle_modal_versions"));
            gradleHtml = gradleHtml.Replace("{GRADLE_MODAL_DEPS}", I18nManager.T("gradle_modal_deps"));
            gradleHtml = gradleHtml.Replace("{GRADLE_MODAL_FILES}", I18nManager.T("gradle_modal_files"));
            gradleHtml = gradleHtml.Replace("{MODAL_BTN_OK}", I18nManager.T("modal_btn_ok"));

            sb.Append(gradleHtml);

            sb.Append(HttpServer.GetHtmlFooter());

            byte[] buffer = Encoding.UTF8.GetBytes(sb.ToString());
            response.ContentType = "text/html; charset=utf-8";
            response.ContentLength64 = buffer.Length;
            response.OutputStream.Write(buffer, 0, buffer.Length);
            response.OutputStream.Close();
        }

        private static long GetDirSize(string path)
        {
            long size = 0;
            SafeGetDirSize(path, ref size);
            return size;
        }

        private static void SafeGetDirSize(string path, ref long size)
        {
            try
            {
                if (path.Length >= 248) return;
                foreach (string f in Directory.GetFiles(path, "*"))
                {
                    try { size += new FileInfo(f).Length; } catch {}
                }
                foreach (string d in Directory.GetDirectories(path))
                {
                    SafeGetDirSize(d, ref size);
                }
            }
            catch {}
        }

        private static void SafeGetFiles(string path, string pattern, System.Collections.Generic.List<string> result)
        {
            try
            {
                if (path.Length >= 248) return;
                foreach (string f in Directory.GetFiles(path, pattern))
                {
                    result.Add(f);
                }
                foreach (string d in Directory.GetDirectories(path))
                {
                    SafeGetFiles(d, pattern, result);
                }
            }
            catch {}
        }

        private static bool SafeHasFile(string path, string pattern)
        {
            try
            {
                if (path.Length >= 248) return false;
                if (Directory.GetFiles(path, pattern).Length > 0) return true;
                foreach (string d in Directory.GetDirectories(path))
                {
                    if (SafeHasFile(d, pattern)) return true;
                }
            }
            catch {}
            return false;
        }

        private static string SafeGetDirectoryName(string path)
        {
            if (string.IsNullOrEmpty(path)) return "";
            try
            {
                int idx = path.LastIndexOfAny(new char[] { '\\', '/' });
                if (idx > 0)
                {
                    return path.Substring(0, idx);
                }
            }
            catch {}
            return "";
        }

public static bool HandleApi(string rawPath, HttpListenerRequest request, HttpListenerResponse response)
        {
            if (!ServerApplicationContext.enable_dev_ecosystem)
            {
                HttpServer.ServeJson(response, 403, string.Format("{{\"success\":false,\"message\":\"{0}\"}}", HttpServer.EscapeJson(I18nManager.T("err_dev_ecosystem_disabled"))));
                return true;
            }

            #region Gradle API Routing
            if (false) {}
            else if (rawPath.Equals("api/gradle/info", StringComparison.OrdinalIgnoreCase))
                {
                    string gHome = GetGradleHome();
                    if (string.IsNullOrEmpty(gHome))
                    {
                        HttpServer.ServeJson(response, 400, string.Format("{{\"success\":false,\"message\":\"{0}\"}}", HttpServer.EscapeJson(I18nManager.T("api_gradle_no_root"))));
                        return true;
                    }
                    
                    bool scanning;
                    string wrappers;
                    int depCount;
                    int kmpCount;
                    long totalSize;
                    lock (gradleScanLock)
                    {
                        scanning = GradleExplorer.isGradleScanning;
                        wrappers = GradleExplorer.cachedWrappersJson;
                        depCount = GradleExplorer.cachedDependencyCount;
                        kmpCount = GradleExplorer.cachedKmpCount;
                        totalSize = GradleExplorer.cachedTotalSize;
                    }

                    string responseJson = string.Format(
                        "{{\"success\":true,\"gradleHome\":\"{0}\",\"isScanning\":{1},\"wrappers\":{2},\"dependencyCount\":{3},\"kmpCount\":{4},\"totalSize\":\"{5}\"}}",
                        HttpServer.EscapeJson(gHome), scanning ? "true" : "false", wrappers, depCount, kmpCount, HttpServer.FormatFileSize(totalSize)
                    );
                    HttpServer.ServeJson(response, 200, responseJson);
                }
                else if (rawPath.Equals("api/gradle/search", StringComparison.OrdinalIgnoreCase))
                {
                    string q = request.QueryString["q"] ?? "";
                    q = q.ToLower();
                    string gHome = GetGradleHome();
                    if (string.IsNullOrEmpty(gHome))
                    {
                        HttpServer.ServeJson(response, 400, string.Format("{{\"success\":false,\"message\":\"{0}\"}}", HttpServer.EscapeJson(I18nManager.T("api_gradle_no_root"))));
                        return true;
                    }

                    StringBuilder sb = new StringBuilder();
                    sb.Append("[");
                    
                    System.Collections.Generic.List<CachedDependency> matches = new System.Collections.Generic.List<CachedDependency>();
                    lock (gradleScanLock)
                    {
                        foreach (var dep in GradleExplorer.cachedDependencies)
                        {
                            if (string.IsNullOrEmpty(q) || dep.Group.ToLower().Contains(q) || dep.Artifact.ToLower().Contains(q))
                            {
                                matches.Add(dep);
                            }
                        }
                    }

                    for (int i = 0; i < matches.Count; i++)
                    {
                        var dep = matches[i];
                        if (i > 0) sb.Append(",");
                        sb.AppendFormat("{{\"group\":\"{0}\",\"artifact\":\"{1}\",\"version\":\"{2}\",\"isKmp\":{3},\"size\":\"{4}\",\"path\":\"{5}\"}}",
                            HttpServer.EscapeJson(dep.Group), HttpServer.EscapeJson(dep.Artifact), HttpServer.EscapeJson(dep.Version), dep.IsKmp ? "true" : "false", dep.FriendlySize, HttpServer.EscapeJson(dep.LocalPath.Replace("\\", "\\\\")));
                    }
                    sb.Append("]");
                    HttpServer.ServeJson(response, 200, string.Format("{{\"success\":true,\"results\":{0}}}", sb.ToString()));
                }
                else if (rawPath.Equals("api/gradle/refresh", StringComparison.OrdinalIgnoreCase))
                {
                    bool alreadyScanning;
                    lock (gradleScanLock)
                    {
                        alreadyScanning = GradleExplorer.isGradleScanning;
                    }
                    if (alreadyScanning)
                    {
                        HttpServer.ServeJson(response, 400, string.Format("{{\"success\":false,\"message\":\"{0}\"}}", HttpServer.EscapeJson(I18nManager.T("api_gradle_scanning"))));
                    }
                    else
                    {
                        GradleExplorer.TriggerGradleScanAsync(true);
                        HttpServer.ServeJson(response, 200, string.Format("{{\"success\":true,\"message\":\"{0}\"}}", HttpServer.EscapeJson(I18nManager.T("api_gradle_scan_started"))));
                    }
                }
                else if (rawPath.Equals("api/gradle/delete-wrapper", StringComparison.OrdinalIgnoreCase))
                {
                    string pathStr = request.QueryString["path"];
                    if (string.IsNullOrEmpty(pathStr))
                    {
                        HttpServer.ServeJson(response, 400, string.Format("{{\"success\":false,\"message\":\"{0}\"}}", HttpServer.EscapeJson(I18nManager.T("api_missing_path"))));
                        return true;
                    }
                    string gHome = GetGradleHome();
                    if (string.IsNullOrEmpty(gHome))
                    {
                        HttpServer.ServeJson(response, 400, string.Format("{{\"success\":false,\"message\":\"{0}\"}}", HttpServer.EscapeJson(I18nManager.T("api_gradle_no_root"))));
                        return true;
                    }
                    string allowedPrefix = Path.Combine(gHome, "wrapper", "dists").ToLower();
                    string fullPath = Path.GetFullPath(pathStr).ToLower();
                    
                    if (!fullPath.StartsWith(allowedPrefix) || fullPath == allowedPrefix)
                    {
                        HttpServer.ServeJson(response, 403, string.Format("{{\"success\":false,\"message\":\"{0}\"}}", HttpServer.EscapeJson(I18nManager.T("api_gradle_illegal_wrapper_path"))));
                        return true;
                    }
                    
                    if (Directory.Exists(pathStr))
                    {
                        try
                        {
                            Directory.Delete(pathStr, true);
                            Logger.Log(I18nManager.T("log_gradle_wrapper_deleted", Path.GetFileName(pathStr)));
                            GradleExplorer.TriggerGradleScanAsync();
                            HttpServer.ServeJson(response, 200, "{\"success\":true}");
                        }
                        catch (Exception ex)
                        {
                            HttpServer.ServeJson(response, 500, string.Format("{{\"success\":false,\"message\":\"{0}\"}}", HttpServer.EscapeJson(ex.Message)));
                        }
                    }
                    else
                    {
                        HttpServer.ServeJson(response, 404, string.Format("{{\"success\":false,\"message\":\"{0}\"}}", HttpServer.EscapeJson(I18nManager.T("api_gradle_wrapper_not_found"))));
                    }
                }
                else if (rawPath.Equals("api/gradle/wrapper-detail", StringComparison.OrdinalIgnoreCase))
                {
                    string version = request.QueryString["version"] ?? "";
                    if (string.IsNullOrEmpty(version))
                    {
                        HttpServer.ServeJson(response, 400, string.Format("{{\"success\":false,\"message\":\"{0}\"}}", HttpServer.EscapeJson(I18nManager.T("api_missing_param"))));
                        return true;
                    }
                    string gHome = GetGradleHome();
                    if (string.IsNullOrEmpty(gHome))
                    {
                        HttpServer.ServeJson(response, 400, string.Format("{{\"success\":false,\"message\":\"{0}\"}}", HttpServer.EscapeJson(I18nManager.T("api_gradle_no_root"))));
                        return true;
                    }
                    string distsDir = Path.Combine(gHome, Path.Combine("wrapper", "dists"));
                    if (!Directory.Exists(distsDir))
                    {
                        HttpServer.ServeJson(response, 404, string.Format("{{\"success\":false,\"message\":\"{0}\"}}", HttpServer.EscapeJson(I18nManager.T("api_gradle_wrapper_dists_not_found"))));
                        return true;
                    }

                    string[] subdirs = Directory.GetDirectories(distsDir, "gradle-" + version + "-*");
                    if (subdirs.Length == 0)
                    {
                        HttpServer.ServeJson(response, 404, string.Format("{{\"success\":false,\"message\":\"{0}\"}}", HttpServer.EscapeJson(I18nManager.T("api_gradle_wrapper_ver_not_found"))));
                        return true;
                    }

                    string versionDir = subdirs[0];
                    string absolutePath = versionDir;
                    string hashFolder = "Unknown";
                    string zipFile = "Unknown";
                    bool zipExists = false;
                    string unpackedFolder = "Unknown";
                    StringBuilder subfoldersJson = new StringBuilder();
                    subfoldersJson.Append("[");

                    string[] hashDirs = Directory.GetDirectories(versionDir);
                    if (hashDirs.Length > 0)
                    {
                        string hashPath = hashDirs[0];
                        hashFolder = Path.GetFileName(hashPath);

                        string[] zips = Directory.GetFiles(hashPath, "*.zip");
                        if (zips.Length > 0)
                        {
                            zipFile = Path.GetFileName(zips[0]);
                            zipExists = true;
                        }

                        string[] unpackedDirs = Directory.GetDirectories(hashPath);
                        if (unpackedDirs.Length > 0)
                        {
                            unpackedFolder = Path.GetFileName(unpackedDirs[0]);
                            string targetUnpacked = unpackedDirs[0];

                            string[] unpackedChildren = Directory.GetDirectories(targetUnpacked);
                            for (int i = 0; i < unpackedChildren.Length; i++)
                            {
                                if (i > 0) subfoldersJson.Append(",");
                                subfoldersJson.Append("\"" + HttpServer.EscapeJson(Path.GetFileName(unpackedChildren[i])) + "\"");
                            }
                        }
                    }
                    subfoldersJson.Append("]");

                    long totalBytes = GetDirSize(versionDir);
                    int fileCount = 0;
                    System.Collections.Generic.List<string> wrapperFiles = new System.Collections.Generic.List<string>();
                    SafeGetFiles(versionDir, "*", wrapperFiles);
                    fileCount = wrapperFiles.Count;

                    string jsonResp = string.Format(
                        "{{\"success\":true,\"version\":\"{0}\",\"path\":\"{1}\",\"hashFolder\":\"{2}\",\"zipFile\":\"{3}\",\"zipExists\":{4},\"unpackedFolder\":\"{5}\",\"subfolders\":{6},\"size\":\"{7}\",\"fileCount\":{8}}}",
                        HttpServer.EscapeJson(version), HttpServer.EscapeJson(absolutePath.Replace("\\", "\\\\")), HttpServer.EscapeJson(hashFolder), HttpServer.EscapeJson(zipFile), zipExists ? "true" : "false", HttpServer.EscapeJson(unpackedFolder), subfoldersJson.ToString(), HttpServer.FormatFileSize(totalBytes), fileCount
                    );
                    HttpServer.ServeJson(response, 200, jsonResp);
                }
                else if (rawPath.Equals("api/gradle/detail", StringComparison.OrdinalIgnoreCase))
                {
                    string group = request.QueryString["group"] ?? "";
                    string artifact = request.QueryString["name"] ?? "";
                    string version = request.QueryString["version"] ?? "";

                    if (string.IsNullOrEmpty(group) || string.IsNullOrEmpty(artifact) || string.IsNullOrEmpty(version))
                    {
                        HttpServer.ServeJson(response, 400, string.Format("{{\"success\":false,\"message\":\"{0}\"}}", HttpServer.EscapeJson(I18nManager.T("api_missing_param"))));
                        return true;
                    }

                    string gHome = GetGradleHome();
                    if (string.IsNullOrEmpty(gHome))
                    {
                        HttpServer.ServeJson(response, 400, string.Format("{{\"success\":false,\"message\":\"{0}\"}}", HttpServer.EscapeJson(I18nManager.T("api_gradle_no_root"))));
                        return true;
                    }
                    string files21Path = Path.Combine(gHome, "caches", "modules-2", "files-2.1");
                    string versionDir = Path.Combine(files21Path, Path.Combine(group, Path.Combine(artifact, version)));

                    if (!Directory.Exists(versionDir))
                    {
                        HttpServer.ServeJson(response, 404, string.Format("{{\"success\":false,\"message\":\"{0}\"}}", HttpServer.EscapeJson(I18nManager.T("api_gradle_dep_not_found"))));
                        return true;
                    }

                    System.Collections.Generic.List<string> detailPomList = new System.Collections.Generic.List<string>();
                    SafeGetFiles(versionDir, "*.pom", detailPomList);
                    string[] pomFiles = detailPomList.ToArray();
                    string pomPath = pomFiles.Length > 0 ? pomFiles[0] : "";

                    string description = "";
                    string license = "Unknown";
                    string organization = "";
                    StringBuilder depsJson = new StringBuilder();
                    depsJson.Append("[");

                    if (File.Exists(pomPath))
                    {
                        try
                        {
                            XmlDocument doc = new XmlDocument();
                            doc.Load(pomPath);

                            // C# 5.0 Compliant - NO null-conditional operator '?.'
                            XmlNode descNode = doc.SelectSingleNode("//*[local-name()='description']");
                            description = descNode != null ? descNode.InnerText.Trim() : "";

                            XmlNode licNode = doc.SelectSingleNode("//*[local-name()='license']/*[local-name()='name']");
                            license = licNode != null ? licNode.InnerText.Trim() : "Unknown";

                            XmlNode orgNode = doc.SelectSingleNode("//*[local-name()='organization']/*[local-name()='name']");
                            organization = orgNode != null ? orgNode.InnerText.Trim() : "";

                            XmlNodeList depNodes = doc.SelectNodes("//*[local-name()='dependency']");
                            bool isFirstDep = true;
                            foreach (XmlNode dep in depNodes)
                            {
                                XmlNode dgNode = dep.SelectSingleNode("*[local-name()='groupId']");
                                string dg = dgNode != null ? dgNode.InnerText.Trim() : "";

                                XmlNode daNode = dep.SelectSingleNode("*[local-name()='artifactId']");
                                string da = daNode != null ? daNode.InnerText.Trim() : "";

                                XmlNode dvNode = dep.SelectSingleNode("*[local-name()='version']");
                                string dv = dvNode != null ? dvNode.InnerText.Trim() : "";

                                XmlNode dscopeNode = dep.SelectSingleNode("*[local-name()='scope']");
                                string dscope = dscopeNode != null ? dscopeNode.InnerText.Trim() : "compile";

                                if (dv.StartsWith("${") && dv.EndsWith("}"))
                                {
                                    string varName = dv.Substring(2, dv.Length - 3).Trim();
                                    if (varName.Equals("project.version", StringComparison.OrdinalIgnoreCase) || varName.Equals("version", StringComparison.OrdinalIgnoreCase))
                                    {
                                        dv = version;
                                    }
                                    else
                                    {
                                        XmlNode propNode = doc.SelectSingleNode("//*[local-name()='properties']/*[local-name()='" + varName + "']");
                                        if (propNode != null)
                                        {
                                            dv = propNode.InnerText.Trim();
                                        }
                                        else
                                        {
                                            dv = "Unknown";
                                        }
                                    }
                                }

                                if (!string.IsNullOrEmpty(dg) && !string.IsNullOrEmpty(da))
                                {
                                    string depDir = Path.Combine(files21Path, Path.Combine(dg, Path.Combine(da, dv)));
                                    bool isDownloaded = Directory.Exists(depDir);

                                    if (!isFirstDep) depsJson.Append(",");
                                    isFirstDep = false;

                                    depsJson.AppendFormat(
                                        "{{\"group\":\"{0}\",\"artifact\":\"{1}\",\"version\":\"{2}\",\"scope\":\"{3}\",\"isDownloaded\":{4}}}",
                                        HttpServer.EscapeJson(dg), HttpServer.EscapeJson(da), HttpServer.EscapeJson(dv), HttpServer.EscapeJson(dscope), isDownloaded ? "true" : "false"
                                    );
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            description = "POM XML parsing error: " + ex.Message;
                        }
                    }
                    depsJson.Append("]");

                    System.Collections.Generic.List<string> detailModuleList = new System.Collections.Generic.List<string>();
                    SafeGetFiles(versionDir, "*.module", detailModuleList);
                    string[] moduleFiles = detailModuleList.ToArray();
                    bool isKmp = moduleFiles.Length > 0;
                    StringBuilder platformsJson = new StringBuilder();
                    platformsJson.Append("[");
                    if (isKmp && File.Exists(moduleFiles[0]))
                    {
                        try
                        {
                            string moduleText = File.ReadAllText(moduleFiles[0]).ToLower();
                            System.Collections.Generic.List<string> plist = new System.Collections.Generic.List<string>();
                            if (moduleText.Contains("ios")) plist.Add("iOS");
                            if (moduleText.Contains("android")) plist.Add("Android");
                            if (moduleText.Contains("jvm") || moduleText.Contains("desktop")) plist.Add("JVM/Desktop");
                            if (moduleText.Contains("js")) plist.Add("JS");
                            if (moduleText.Contains("wasm")) plist.Add("Wasm");
                            if (moduleText.Contains("macos") || moduleText.Contains("apple")) plist.Add("macOS");
                            if (moduleText.Contains("linux")) plist.Add("Linux");
                            if (moduleText.Contains("windows") || moduleText.Contains("mingw")) plist.Add("Windows");

                            for (int i = 0; i < plist.Count; i++)
                            {
                                if (i > 0) platformsJson.Append(",");
                                platformsJson.Append("\"" + HttpServer.EscapeJson(plist[i]) + "\"");
                            }
                        }
                        catch {}
                    }
                    platformsJson.Append("]");

                    long totalSize = GetDirSize(versionDir);
                    string implCode = string.Format("implementation \\\"{0}:{1}:{2}\\\"", group, artifact, version);
                    string kmpCode = string.Format("implementation(\\\"{0}:{1}:{2}\\\")", group, artifact, version);

                    string resp = string.Format(
                        "{{\"success\":true,\"group\":\"{0}\",\"artifact\":\"{1}\",\"version\":\"{2}\",\"isKmp\":{3},\"platforms\":{4},\"size\":\"{5}\",\"sizeBytes\":{6},\"license\":\"{7}\",\"organization\":\"{8}\",\"description\":\"{9}\",\"dependencies\":{10},\"implementationCode\":\"{11}\",\"kmpCode\":\"{12}\"}}",
                        HttpServer.EscapeJson(group), HttpServer.EscapeJson(artifact), HttpServer.EscapeJson(version), isKmp ? "true" : "false", platformsJson.ToString(),
                        HttpServer.FormatFileSize(totalSize), totalSize, HttpServer.EscapeJson(license), HttpServer.EscapeJson(organization), HttpServer.EscapeJson(description),
                        depsJson.ToString(), implCode, kmpCode
                    );
                    HttpServer.ServeJson(response, 200, resp);
                }
                else if (rawPath.Equals("api/gradle/delete", StringComparison.OrdinalIgnoreCase))
                {
                    string group = request.QueryString["group"] ?? "";
                    string name = request.QueryString["name"] ?? "";
                    string version = request.QueryString["version"] ?? "";

                    if (string.IsNullOrEmpty(group) || string.IsNullOrEmpty(name) || string.IsNullOrEmpty(version))
                    {
                        HttpServer.ServeJson(response, 400, string.Format("{{\"success\":false,\"message\":\"{0}\"}}", HttpServer.EscapeJson(I18nManager.T("api_missing_param"))));
                        return true;
                    }

                    string gHome = GetGradleHome();
                    if (string.IsNullOrEmpty(gHome))
                    {
                        HttpServer.ServeJson(response, 400, string.Format("{{\"success\":false,\"message\":\"{0}\"}}", HttpServer.EscapeJson(I18nManager.T("api_gradle_no_root"))));
                        return true;
                    }
                    string files21Path = Path.Combine(gHome, "caches", "modules-2", "files-2.1");
                    string versionDir = Path.Combine(files21Path, Path.Combine(group, Path.Combine(name, version)));

                    if (Directory.Exists(versionDir))
                    {
                        try
                        {
                            Directory.Delete(versionDir, true);
                            Logger.Log(I18nManager.T("log_gradle_dep_deleted", group, name, version));
                            HttpServer.ServeJson(response, 200, "{\"success\":true}");
                        }
                        catch (Exception ex)
                        {
                            HttpServer.ServeJson(response, 500, string.Format("{{\"success\":false,\"message\":\"{0}\"}}", HttpServer.EscapeJson(ex.Message)));
                        }
                    }
                    else
                    {
                        HttpServer.ServeJson(response, 404, string.Format("{{\"success\":false,\"message\":\"{0}\"}}", HttpServer.EscapeJson(I18nManager.T("api_gradle_dep_not_found"))));
                    }
                }
                else if (rawPath.Equals("api/gradle/version-files", StringComparison.OrdinalIgnoreCase))
                {
                    string group = request.QueryString["group"] ?? "";
                    string name = request.QueryString["name"] ?? "";
                    string version = request.QueryString["version"] ?? "";

                    if (string.IsNullOrEmpty(group) || string.IsNullOrEmpty(name) || string.IsNullOrEmpty(version))
                    {
                        HttpServer.ServeJson(response, 400, string.Format("{{\"success\":false,\"message\":\"{0}\"}}", HttpServer.EscapeJson(I18nManager.T("api_missing_param"))));
                        return true;
                    }

                    string gHome = GetGradleHome();
                    if (string.IsNullOrEmpty(gHome))
                    {
                        HttpServer.ServeJson(response, 400, string.Format("{{\"success\":false,\"message\":\"{0}\"}}", HttpServer.EscapeJson(I18nManager.T("api_gradle_no_root"))));
                        return true;
                    }

                    string files21Path = Path.Combine(gHome, "caches", "modules-2", "files-2.1");
                    string versionDir = Path.Combine(files21Path, Path.Combine(group, Path.Combine(name, version)));

                    if (Directory.Exists(versionDir))
                    {
                        try
                        {
                            StringBuilder sb = new StringBuilder();
                            sb.Append("[");
                            string[] files = Directory.GetFiles(versionDir, "*", SearchOption.AllDirectories);
                            int count = 0;
                            foreach (string f in files)
                            {
                                if (count > 0) sb.Append(",");
                                FileInfo fi = new FileInfo(f);
                                sb.AppendFormat("{{\"name\":\"{0}\",\"path\":\"{1}\",\"size\":\"{2}\"}}",
                                    HttpServer.EscapeJson(fi.Name), HttpServer.EscapeJson(f.Replace("\\", "\\\\")), HttpServer.FormatFileSize(fi.Length));
                                count++;
                            }
                            sb.Append("]");
                            HttpServer.ServeJson(response, 200, string.Format("{{\"success\":true,\"files\":{0}}}", sb.ToString()));
                        }
                        catch (Exception ex)
                        {
                            HttpServer.ServeJson(response, 500, string.Format("{{\"success\":false,\"message\":\"{0}\"}}", HttpServer.EscapeJson(ex.Message)));
                        }
                    }
                    else
                    {
                        HttpServer.ServeJson(response, 404, string.Format("{{\"success\":false,\"message\":\"{0}\"}}", HttpServer.EscapeJson(I18nManager.T("api_gradle_dep_not_found"))));
                    }
                    return true;
                }
            else
            {
                return false;
            }
            return true;
            #endregion
        }
    }
}
