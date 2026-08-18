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

        public static void TriggerGradleScanAsync()
        {
            lock (gradleScanLock)
            {
                if (isGradleScanning) return;
                isGradleScanning = true;
            }
            Logger.Log("启动 Gradle 依赖及 Wrappers 后台异步扫描线程...");
            System.Threading.ThreadPool.QueueUserWorkItem(delegate {
                try
                {
                    DoGradleScan();
                }
                catch (Exception ex)
                {
                    Logger.Log("Gradle 后台扫描发生异常: " + ex.Message);
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

        private static void DoGradleScan()
        {
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
            Logger.Log(string.Format("Gradle 后台扫描已完成：找到 {0} 个依赖包，KMP 占比 {1}，总缓存 {2}", depCount, kmpCount, HttpServer.FormatFileSize(totalSize)));
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
            StringBuilder sb = new StringBuilder();
            sb.Append(HttpServer.GetHtmlHeader("Gradle 依赖管理", "", "layout-explorer"));
            sb.Append("<script>const currentView = 'gradle';</script>");

            var favList = FileExplorer.GetFavorites();

            // Left Sidebar Tree Pane
            sb.Append("<div class='explorer-sidebar' id='sidebar-pane'>");
            sb.Append("  <div class='sidebar-expand-btn' onclick='toggleSidebar(\"left\")' style='display: none;'>▶ 导 航</div>");
            sb.Append("  <div class='sidebar-title' style='display: flex; justify-content: space-between; align-items: center; width: 100%;'>");
            sb.Append("    <span>📂 导航目录</span>");
            sb.Append("    <span class='sidebar-toggle-btn' onclick='toggleSidebar(\"left\"); event.stopPropagation();' style='cursor: pointer; font-size: 0.8rem; color: var(--text-muted); padding: 2px 6px; border-radius: 4px;' title='收起左栏'>◀</span>");
            sb.Append("  </div>");
            sb.Append("  <div class='tree-container'>");
            
            // 1. Home Node
            sb.Append("    <div class='tree-node root-node'>");
            sb.Append("      <a href='/' class='tree-link'>🏠 导航主页</a>");
            sb.Append("    </div>");

            // 2. Quick Access Node
            sb.Append("    <div class='tree-node branch-node' id='node-quick-access'>");
            sb.Append("      <div class='tree-row' onclick='toggleTreeNode(\"quick-access\")'>");
            sb.Append("        <span class='tree-arrow'>▼</span>");
            sb.Append("        <span class='tree-folder-icon'>🚀</span>");
            sb.Append("        <span class='tree-text'>常用快速访问</span>");
            sb.Append("      </div>");
            sb.Append("      <div class='tree-children' id='children-quick-access'>");

            string desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            if (Directory.Exists(desktopPath))
                sb.AppendFormat("        <a href='/c/Users/{0}/Desktop/' class='tree-link' title='{1}'>🖥️ 桌面</a>", Environment.UserName, desktopPath.Replace("'", "\\'"));
            
            string downloadsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            if (Directory.Exists(downloadsPath))
                sb.AppendFormat("        <a href='/c/Users/{0}/Downloads/' class='tree-link' title='{1}'>📥 下载</a>", Environment.UserName, downloadsPath.Replace("'", "\\'"));

            string docsPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            if (Directory.Exists(docsPath))
                sb.AppendFormat("        <a href='/c/Users/{0}/Documents/' class='tree-link' title='{1}'>📁 我的文档</a>", Environment.UserName, docsPath.Replace("'", "\\'"));

            string profilePath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (Directory.Exists(profilePath))
                sb.AppendFormat("        <a href='/c/Users/{0}/' class='tree-link' title='{1}'>👤 用户主目录</a>", Environment.UserName, profilePath.Replace("'", "\\'"));

            sb.Append("      </div>");
            sb.Append("    </div>");

            // 3. Favorites Node
            sb.Append("    <div class='tree-node branch-node' id='node-favorites'>");
            sb.Append("      <div class='tree-row' onclick='toggleTreeNode(\"favorites\")'>");
            sb.Append("        <span class='tree-arrow'>▼</span>");
            sb.Append("        <span class='tree-folder-icon'>⭐</span>");
            sb.Append("        <span class='tree-text'>我的收藏夹</span>");
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
            sb.Append("        <span class='tree-text'>物理磁盘分区</span>");
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

            // 5. Gradle Node (active!)
            sb.Append("    <div class='tree-node root-node active-node' style='margin-top: 10px; border-top: 1px solid var(--border-color); padding-top: 8px;'>");
            sb.Append("      <a href='/?view=gradle' class='tree-link active-node' style='font-weight: bold;'>☕ Gradle 依赖管理</a>");
            sb.Append("    </div>");

            sb.Append("  </div>");
            sb.Append("</div>");

            // Load and append middle & right column layout from gradle.html
            sb.Append(HttpServer.LoadResource("gradle.html"));

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
            #region Gradle API Routing
            if (false) {}
            else if (rawPath.Equals("api/gradle/info", StringComparison.OrdinalIgnoreCase))
                {
                    string gHome = GetGradleHome();
                    if (string.IsNullOrEmpty(gHome))
                    {
                        HttpServer.ServeJson(response, 400, "{\"success\":false,\"message\":\"未在宿主系统中检测到有效的 Gradle 缓存根目录\"}");
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
                        HttpServer.ServeJson(response, 400, "{\"success\":false,\"message\":\"未在宿主系统中检测到有效的 Gradle 缓存根目录\"}");
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
                        HttpServer.ServeJson(response, 400, "{\"success\":false,\"message\":\"扫描已经在进行中，请勿重复发起\"}");
                    }
                    else
                    {
                        GradleExplorer.TriggerGradleScanAsync();
                        HttpServer.ServeJson(response, 200, "{\"success\":true,\"message\":\"已成功拉起后台扫描\"}");
                    }
                }
                else if (rawPath.Equals("api/gradle/delete-wrapper", StringComparison.OrdinalIgnoreCase))
                {
                    string pathStr = request.QueryString["path"];
                    if (string.IsNullOrEmpty(pathStr))
                    {
                        HttpServer.ServeJson(response, 400, "{\"success\":false,\"message\":\"缺少路径参数\"}");
                        return true;
                    }
                    string gHome = GetGradleHome();
                    if (string.IsNullOrEmpty(gHome))
                    {
                        HttpServer.ServeJson(response, 400, "{\"success\":false,\"message\":\"未在宿主系统中检测到有效的 Gradle 缓存根目录\"}");
                        return true;
                    }
                    string allowedPrefix = Path.Combine(gHome, "wrapper", "dists").ToLower();
                    string fullPath = Path.GetFullPath(pathStr).ToLower();
                    
                    if (!fullPath.StartsWith(allowedPrefix) || fullPath == allowedPrefix)
                    {
                        HttpServer.ServeJson(response, 403, "{\"success\":false,\"message\":\"非法操作：该路径不在可清理的 Gradle Wrapper 分发包白名单范围内\"}");
                        return true;
                    }
                    
                    if (Directory.Exists(pathStr))
                    {
                        try
                        {
                            Directory.Delete(pathStr, true);
                            Logger.Log("已物理清理已解压的 Gradle Wrapper 分发包：" + Path.GetFileName(pathStr));
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
                        HttpServer.ServeJson(response, 404, "{\"success\":false,\"message\":\"指定的分发包文件夹不存在\"}");
                    }
                }
                else if (rawPath.Equals("api/gradle/wrapper-detail", StringComparison.OrdinalIgnoreCase))
                {
                    string version = request.QueryString["version"] ?? "";
                    if (string.IsNullOrEmpty(version))
                    {
                        HttpServer.ServeJson(response, 400, "{\"success\":false,\"message\":\"Missing version\"}");
                        return true;
                    }
                    string gHome = GetGradleHome();
                    if (string.IsNullOrEmpty(gHome))
                    {
                        HttpServer.ServeJson(response, 400, "{\"success\":false,\"message\":\"未在宿主系统中检测到有效的 Gradle 缓存根目录\"}");
                        return true;
                    }
                    string distsDir = Path.Combine(gHome, Path.Combine("wrapper", "dists"));
                    if (!Directory.Exists(distsDir))
                    {
                        HttpServer.ServeJson(response, 404, "{\"success\":false,\"message\":\"Wrapper dists folder not found\"}");
                        return true;
                    }

                    string[] subdirs = Directory.GetDirectories(distsDir, "gradle-" + version + "-*");
                    if (subdirs.Length == 0)
                    {
                        HttpServer.ServeJson(response, 404, "{\"success\":false,\"message\":\"Selected Wrapper version folder not found\"}");
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
                        HttpServer.ServeJson(response, 400, "{\"success\":false,\"message\":\"Missing group, name or version\"}");
                        return true;
                    }

                    string gHome = GetGradleHome();
                    if (string.IsNullOrEmpty(gHome))
                    {
                        HttpServer.ServeJson(response, 400, "{\"success\":false,\"message\":\"未在宿主系统中检测到有效的 Gradle 缓存根目录\"}");
                        return true;
                    }
                    string files21Path = Path.Combine(gHome, "caches", "modules-2", "files-2.1");
                    string versionDir = Path.Combine(files21Path, Path.Combine(group, Path.Combine(artifact, version)));

                    if (!Directory.Exists(versionDir))
                    {
                        HttpServer.ServeJson(response, 404, "{\"success\":false,\"message\":\"Dependency version folder not found in caches\"}");
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
                        HttpServer.ServeJson(response, 400, "{\"success\":false,\"message\":\"Missing parameters\"}");
                        return true;
                    }

                    string gHome = GetGradleHome();
                    if (string.IsNullOrEmpty(gHome))
                    {
                        HttpServer.ServeJson(response, 400, "{\"success\":false,\"message\":\"未在宿主系统中检测到有效的 Gradle 缓存根目录\"}");
                        return true;
                    }
                    string files21Path = Path.Combine(gHome, "caches", "modules-2", "files-2.1");
                    string versionDir = Path.Combine(files21Path, Path.Combine(group, Path.Combine(name, version)));

                    if (Directory.Exists(versionDir))
                    {
                        try
                        {
                            Directory.Delete(versionDir, true);
                            Logger.Log(string.Format("已物理清理 Gradle 依赖库版本：{0}:{1}:{2}", group, name, version));
                            HttpServer.ServeJson(response, 200, "{\"success\":true}");
                        }
                        catch (Exception ex)
                        {
                            HttpServer.ServeJson(response, 500, string.Format("{{\"success\":false,\"message\":\"{0}\"}}", HttpServer.EscapeJson(ex.Message)));
                        }
                    }
                    else
                    {
                        HttpServer.ServeJson(response, 404, "{\"success\":false,\"message\":\"未找到该依赖库版本目录\"}");
                    }
                }
                else if (rawPath.Equals("api/gradle/version-files", StringComparison.OrdinalIgnoreCase))
                {
                    string group = request.QueryString["group"] ?? "";
                    string name = request.QueryString["name"] ?? "";
                    string version = request.QueryString["version"] ?? "";

                    if (string.IsNullOrEmpty(group) || string.IsNullOrEmpty(name) || string.IsNullOrEmpty(version))
                    {
                        HttpServer.ServeJson(response, 400, "{\"success\":false,\"message\":\"Missing parameters\"}");
                        return true;
                    }

                    string gHome = GetGradleHome();
                    if (string.IsNullOrEmpty(gHome))
                    {
                        HttpServer.ServeJson(response, 400, "{\"success\":false,\"message\":\"未在宿主系统中检测到有效的 Gradle 缓存根目录\"}");
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
                        HttpServer.ServeJson(response, 404, "{\"success\":false,\"message\":\"未找到该依赖库版本目录\"}");
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
