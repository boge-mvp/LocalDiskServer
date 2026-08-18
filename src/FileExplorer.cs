using System;
using System.IO;
using System.Net;
using System.Text;
using System.Collections.Generic;
using System.Windows.Forms;
using System.Diagnostics;

namespace LocalDiskServer
{
    public static class FileExplorer
    {
        public static readonly List<string> clipboardPaths = new List<string>();
        public static bool isClipboardCut = false;

        public static List<string> GetFavorites()
        {
            var list = new List<string>();
            if (string.IsNullOrEmpty(ServerApplicationContext.favoritesStr)) return list;
            string[] parts = ServerApplicationContext.favoritesStr.Split(new char[] { ',' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (string p in parts)
            {
                string decoded = Uri.UnescapeDataString(p);
                if (!list.Contains(decoded)) list.Add(decoded);
            }
            return list;
        }

        public static void SaveFavorites(List<string> list)
        {
            var encodedList = new List<string>();
            foreach (string p in list)
            {
                encodedList.Add(Uri.EscapeDataString(p));
            }
            ServerApplicationContext.favoritesStr = string.Join(",", encodedList.ToArray());
            ServerApplicationContext.SaveConfigStatic();
        }

        
        public static void CopyDirectory(string sourceDir, string targetDir)
        {
            Directory.CreateDirectory(targetDir);
            foreach (string file in Directory.GetFiles(sourceDir))
            {
                string dest = Path.Combine(targetDir, Path.GetFileName(file));
                File.Copy(file, dest, true);
            }
            foreach (string folder in Directory.GetDirectories(sourceDir))
            {
                string dest = Path.Combine(targetDir, Path.GetFileName(folder));
                CopyDirectory(folder, dest);
            }
        }

        public static void CalculateDirectoryInfo(string dirPath, ref long totalSize, ref long fileCount, ref long dirCount)
        {
            foreach (string file in Directory.GetFiles(dirPath))
            {
                try
                {
                    FileInfo fi = new FileInfo(file);
                    totalSize += fi.Length;
                    fileCount++;
                }
                catch {}
            }
            foreach (string folder in Directory.GetDirectories(dirPath))
            {
                dirCount++;
                try
                {
                    CalculateDirectoryInfo(folder, ref totalSize, ref fileCount, ref dirCount);
                }
                catch {}
            }
        }

        public static void ServeDirectory(HttpListenerResponse response, string dirPath, string webPath)
        {
            var favList = GetFavorites();
            StringBuilder sb = new StringBuilder();
            sb.Append(HttpServer.GetHtmlHeader("目录: " + dirPath, webPath, "layout-explorer"));
            sb.AppendFormat("<script>const currentDirPath = '{0}';</script>", dirPath.Replace("\\", "\\\\").Replace("'", "\\'"));

            string[] parts = webPath.Split(new char[] { '/' }, StringSplitOptions.RemoveEmptyEntries);

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
                sb.AppendFormat("        <a href='/c/Users/{0}/Desktop/' class='tree-link{1}' title='{2}'>🖥️ 桌面</a>", 
                    Environment.UserName, dirPath.Equals(desktopPath, StringComparison.OrdinalIgnoreCase) ? " active-node" : "", desktopPath.Replace("'", "\'"));
            
            string downloadsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            if (Directory.Exists(downloadsPath))
                sb.AppendFormat("        <a href='/c/Users/{0}/Downloads/' class='tree-link{1}' title='{2}'>📥 下载</a>", 
                    Environment.UserName, dirPath.Equals(downloadsPath, StringComparison.OrdinalIgnoreCase) ? " active-node" : "", downloadsPath.Replace("'", "\'"));

            string docsPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            if (Directory.Exists(docsPath))
                sb.AppendFormat("        <a href='/c/Users/{0}/Documents/' class='tree-link{1}' title='{2}'>📁 我的文档</a>", 
                    Environment.UserName, dirPath.Equals(docsPath, StringComparison.OrdinalIgnoreCase) ? " active-node" : "", docsPath.Replace("'", "\'"));

            string profilePath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (Directory.Exists(profilePath))
                sb.AppendFormat("        <a href='/c/Users/{0}/' class='tree-link{1}' title='{2}'>👤 用户主目录</a>", 
                    Environment.UserName, dirPath.Equals(profilePath, StringComparison.OrdinalIgnoreCase) ? " active-node" : "", profilePath.Replace("'", "\'"));

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
                    sb.AppendFormat("        <a href='{0}' class='tree-link{1}' title='{2}'>📁 {3}</a>", 
                        webLink, dirPath.Equals(fav, StringComparison.OrdinalIgnoreCase) ? " active-node" : "", fav.Replace("'", "\'"), fName);
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
                    bool isCurrentDrive = dirPath.StartsWith(dPath, StringComparison.OrdinalIgnoreCase);
                    
                    sb.AppendFormat("        <div class='tree-node'>");
                    sb.AppendFormat("          <div class='tree-row{0}' data-path='{1}'>", (dirPath.Equals(dPath, StringComparison.OrdinalIgnoreCase)) ? " active" : "", dPath.Replace("\\", "\\\\").Replace("'", "\\'"));
                    sb.AppendFormat("            <span class='tree-arrow collapsed' onclick='expandTreeNode(event, \"{0}\")'>▶</span>", dPath.Replace("\\", "\\\\").Replace("'", "\\'"));
                    sb.AppendFormat("            <a href='{0}' class='tree-link-inline' style='color:inherit;'>💽 {1}</a>", dWeb, dName);
                    sb.AppendFormat("          </div>");
                    sb.AppendFormat("          <div class='tree-children' id='dir-{0}' style='display:none;'></div>", dPath.Replace("\\", "_").Replace(":", "_"));
                    sb.AppendFormat("        </div>");
                }
            }
            sb.Append("      </div>");
            sb.Append("    </div>");

            // 5. Gradle Analyzer Node
            sb.Append("    <div class='tree-node root-node' style='margin-top: 10px; border-top: 1px solid var(--border-color); padding-top: 8px;'>");
            sb.Append("      <a href='/?view=gradle' class='tree-link' style='font-weight: bold;'>☕ Gradle 依赖管理</a>");
            sb.Append("    </div>");

            sb.Append("  </div>");
            sb.Append("</div>");

            // 5. Middle Main Content Panel
            sb.Append("<div class='explorer-main'>");

            // Unified compact toolbar
            sb.Append("<div class='toolbar'>");
            sb.Append("  <div class='toolbar-left'>");

            // Render Back Button if in sub-directory
            if (parts.Length > 0)
            {
                string parentLink = "/";
                if (parts.Length > 1)
                {
                    parentLink = "/" + string.Join("/", parts, 0, parts.Length - 1) + "/";
                }
                sb.AppendFormat("    <a href='{0}' class='btn-back'>⬅ 返回</a>", parentLink);
                sb.Append("    <span class='toolbar-separator'>|</span>");
            }

            // Breadcrumbs Path & Address Bar Wrapper
            sb.Append("    <div class='address-bar-wrapper' onmousedown='activateAddressInput(event)'>");
            sb.Append("      <div class='breadcrumbs' id='breadcrumbs-bar'>");
            sb.Append("        <a href='/'>计算机</a>");
            string runningPath = "";
            for (int i = 0; i < parts.Length; i++)
            {
                runningPath += "/" + parts[i];
                if (i == parts.Length - 1)
                {
                    if (i == 0)
                        sb.AppendFormat(" &gt; <span class='breadcrumb-current'>{0}:</span>", parts[i].ToUpper());
                    else
                        sb.AppendFormat(" &gt; <span class='breadcrumb-current'>{0}</span>", parts[i]);
                }
                else
                {
                    if (i == 0)
                        sb.AppendFormat(" &gt; <a href='{0}/'>{1}:</a>", runningPath, parts[i].ToUpper());
                    else
                        sb.AppendFormat(" &gt; <a href='{0}/'>{1}</a>", runningPath, parts[i]);
                }
            }
            sb.Append("      </div>");
            sb.Append("      <input type='text' id='address-input' style='display: none;' onkeydown='handleAddressKey(event)' onblur='deactivateAddressInput()'>");
            sb.Append("    </div>");
            sb.Append("    <button id='protocol-switch-btn' onclick='toggleProtocol(event)' class='btn-back' style='height: 32px; padding: 0 10px; margin-left: 8px; border: 1px solid var(--border-color); border-radius: 4px; background: var(--container-bg); color: var(--text-color); cursor: pointer; font-size: 0.85rem; display: flex; align-items: center; gap: 4px; flex-shrink: 0;' title='一键切换 HTTP / HTTPS 安全沙箱协议'></button>");
            sb.Append("  </div>");

            // Search Bar and View Switcher on the Right
            sb.Append("  <div class='toolbar-right' style='display: flex; align-items: center; gap: 8px;'>");
            sb.Append("    <select id='view-select' onchange='setViewMode(this.value)' style='height: 32px; background: var(--container-bg); color: var(--text-color); border: 1px solid var(--border-color); border-radius: 4px; padding: 4px 8px; cursor: pointer; outline: none; font-size: 0.85rem;'>");
            sb.Append("      <option value='details'>📋 详细信息</option>");
            sb.Append("      <option value='large'>🔲 大图标</option>");
            sb.Append("      <option value='medium'>⚃ 中等图标</option>");
            sb.Append("    </select>");
            sb.Append("    <input type='text' id='search' placeholder='🔎 快速筛选当前目录...' oninput='filterList()'>");
            sb.Append("  </div>");
            sb.Append("</div>");

            sb.Append("<table id='file-table'>");
            sb.Append("<thead><tr><th>名称</th><th style='width: 45px; text-align: center;'>收藏</th><th style='width: 150px;'>修改时间</th><th style='width: 120px; text-align: right;'>大小</th></tr></thead>");
            sb.Append("<tbody>");

            try
            {
                // List directories
                string[] dirs = Directory.GetDirectories(dirPath);
                foreach (string d in dirs)
                {
                    DirectoryInfo di = new DirectoryInfo(d);
                    string name = di.Name;
                    string relativeLink = "/" + webPath.TrimEnd('/') + "/" + Uri.EscapeDataString(name) + "/";
                    bool isFav = favList.Contains(di.FullName);
                    string htmlEscapedPath = di.FullName.Replace("'", "&#39;").Replace("\"", "&quot;");

                    sb.AppendFormat(
                        "<tr class='item-row dir-row' data-name='{0}' data-path='{1}' data-type='dir' data-favorite='{2}'>" +
                        "  <td><a href='{3}'>{4} <span class='name-text'>{5}</span></a></td>" +
                        "  <td style='text-align: center; width: 45px;'><span class='fav-star-btn{6}' data-path='{1}'>★</span></td>" +
                        "  <td>{7}</td>" +
                        "  <td style='text-align: right;'>-</td>" +
                        "</tr>",
                        name.ToLower(), htmlEscapedPath, isFav ? "true" : "false", relativeLink, HttpServer.GetFolderSvg(), name, isFav ? " active" : "", di.LastWriteTime.ToString("yyyy-MM-dd HH:mm"));
                }

                // List files
                string[] files = Directory.GetFiles(dirPath);
                foreach (string f in files)
                {
                    FileInfo fi = new FileInfo(f);
                    string name = fi.Name;
                    string relativeLink = "/" + webPath.TrimEnd('/') + "/" + Uri.EscapeDataString(name);
                    string sizeStr = HttpServer.FormatFileSize(fi.Length);
                    string fileSvg = HttpServer.GetFileIconSvg(fi.Extension);
                    bool isFav = favList.Contains(fi.FullName);
                    string htmlEscapedPath = fi.FullName.Replace("'", "&#39;").Replace("\"", "&quot;");

                    sb.AppendFormat(
                        "<tr class='item-row file-row' data-name='{0}' data-path='{1}' data-type='file' data-favorite='{2}'>" +
                        "  <td><a href='{3}'>{4} <span class='name-text'>{5}</span></a></td>" +
                        "  <td style='text-align: center; width: 45px;'><span class='fav-star-btn{6}' data-path='{1}'>★</span></td>" +
                        "  <td>{7}</td>" +
                        "  <td style='text-align: right;'>{8}</td>" +
                        "</tr>",
                        name.ToLower(), htmlEscapedPath, isFav ? "true" : "false", relativeLink, fileSvg, name, isFav ? " active" : "", fi.LastWriteTime.ToString("yyyy-MM-dd HH:mm"), sizeStr);
                }
            }
            catch (UnauthorizedAccessException)
            {
                sb.Append("<tr><td colspan='4' style='color: #e74c3c; padding: 20px; text-align: center;'>⚠️ 访问被拒绝：无权限访问此目录。</td></tr>");
            }
            catch (Exception ex)
            {
                sb.AppendFormat("<tr><td colspan='4' style='color: #e74c3c; padding: 20px; text-align: center;'>⚠️ 读取目录出错: {0}</td></tr>", ex.Message);
            }

            sb.Append("</tbody></table>");
            sb.Append("</div>"); // Close explorer-main

            // 6. Right Live Preview Panel
            sb.Append("<div class='explorer-preview' id='preview-pane'>");
            sb.Append("  <div class='preview-expand-btn' onclick='toggleSidebar(\"right\")' style='display: none;'>◀ 预 览</div>");
            sb.Append("  <div class='preview-title' style='display: flex; justify-content: space-between; align-items: center; width: 100%;'>");
            sb.Append("    <span>ℹ️ 预览窗格</span>");
            sb.Append("    <span class='preview-toggle-btn' onclick='toggleSidebar(\"right\"); event.stopPropagation();' style='cursor: pointer; font-size: 0.8rem; color: var(--text-muted); padding: 2px 6px; border-radius: 4px;' title='收起右栏'>▶</span>");
            sb.Append("  </div>");
            sb.Append("  <div class='preview-content' id='preview-content'>");
            sb.Append("    <div style='color: var(--text-muted); font-size: 0.9rem; padding-top: 40px;'>🔍 未选择任何项目</div>");
            sb.Append("  </div>");
            sb.Append("</div>");

            sb.Append(HttpServer.GetHtmlFooter());

            byte[] buffer = Encoding.UTF8.GetBytes(sb.ToString());
            response.ContentType = "text/html; charset=utf-8";
            response.ContentLength64 = buffer.Length;
            response.OutputStream.Write(buffer, 0, buffer.Length);
            response.OutputStream.Close();
        }

        public static void ServeFile(HttpListenerResponse response, string filePath, bool isRaw, bool forceText)
        {
            FileInfo fi = new FileInfo(filePath);
            string ext = fi.Extension.ToLower();

            // Text extension types to serve directly as plain text
            bool isText = forceText || HttpServer.IsTextExtension(ext);

            if (isText && !isRaw)
            {
                ServeTextFileDirect(response, filePath, fi);
                return;
            }

            // Stream standard binary file
            string contentType = HttpServer.GetMimeType(ext);
            response.ContentType = contentType;
            response.ContentLength64 = fi.Length;

            // Trigger download for common download types, or if explicitly requested raw
            if (contentType == "application/octet-stream")
            {
                response.Headers.Add("Content-Disposition", "attachment; filename=\"" + Uri.EscapeDataString(fi.Name) + "\"");
            }

            try
            {
                using (FileStream fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    byte[] buffer = new byte[64 * 1024]; // 64KB chunk
                    int bytesRead;
                    while ((bytesRead = fs.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        response.OutputStream.Write(buffer, 0, bytesRead);
                    }
                }
            }
            catch (Exception ex)
            {
                // Since response header is already sent, we just close the connection safely
                Debug.WriteLine("Stream transmission error: " + ex.Message);
            }
            finally
            {
                response.OutputStream.Close();
            }
        }

        public static void ServeTextFileDirect(HttpListenerResponse response, string filePath, FileInfo fi)
        {
            try
            {
                // Detect encoding dynamically to prevent Mojibake
                Encoding detectedEncoding = HttpServer.DetectEncoding(filePath);
                string content = "";

                using (FileStream fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (StreamReader sr = new StreamReader(fs, detectedEncoding))
                {
                    content = sr.ReadToEnd();
                }

                byte[] buffer = Encoding.UTF8.GetBytes(content);
                response.ContentType = "text/plain; charset=utf-8";
                response.ContentLength64 = buffer.Length;
                response.OutputStream.Write(buffer, 0, buffer.Length);
            }
            catch (Exception ex)
            {
                HttpServer.ServeError(response, 500, "读取文本文件失败: " + ex.Message);
            }
            finally
            {
                response.OutputStream.Close();
            }
        }

        

        
        public static bool HandleApi(string rawPath, HttpListenerRequest request, HttpListenerResponse response)
        {
            #region API Routing
            if (false) {}
            else if (rawPath.Equals("api/favorite/toggle", StringComparison.OrdinalIgnoreCase))
                {
                    string path = request.QueryString["path"];
                    if (string.IsNullOrEmpty(path))
                    {
                        HttpServer.ServeJson(response, 400, "{\"success\":false,\"message\":\"Missing path\"}");
                        return true;
                    }
                    var favs = FileExplorer.GetFavorites();
                    bool isFav = false;
                    if (favs.Contains(path))
                    {
                        favs.Remove(path);
                    }
                    else
                    {
                        favs.Add(path);
                        isFav = true;
                    }
                    FileExplorer.SaveFavorites(favs);
                    HttpServer.ServeJson(response, 200, string.Format("{{\"success\":true,\"isFavorite\":{0}}}", isFav ? "true" : "false"));
                }
                else if (rawPath.Equals("api/file/delete", StringComparison.OrdinalIgnoreCase))
                {
                    string pathsStr = request.QueryString["paths"];
                    if (string.IsNullOrEmpty(pathsStr))
                    {
                        HttpServer.ServeJson(response, 400, "{\"success\":false,\"message\":\"Missing paths\"}");
                        return true;
                    }
                    string[] paths = pathsStr.Split(new char[] { '|' }, StringSplitOptions.RemoveEmptyEntries);
                    int count = 0;
                    foreach (string p in paths)
                    {
                        if (File.Exists(p))
                        {
                            File.Delete(p);
                            count++;
                        }
                        else if (Directory.Exists(p))
                        {
                            Directory.Delete(p, true);
                            count++;
                        }
                    }
                    HttpServer.ServeJson(response, 200, string.Format("{{\"success\":true,\"message\":\"成功删除 {0} 个项目\"}}", count));
                }
                else if (rawPath.Equals("api/file/rename", StringComparison.OrdinalIgnoreCase))
                {
                    string path = request.QueryString["path"];
                    string newName = request.QueryString["newName"];
                    if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(newName))
                    {
                        HttpServer.ServeJson(response, 400, "{\"success\":false,\"message\":\"Missing path or newName\"}");
                        return true;
                    }
                    string parent = Path.GetDirectoryName(path);
                    string dest = Path.Combine(parent, newName);
                    if (File.Exists(path))
                    {
                        File.Move(path, dest);
                        HttpServer.ServeJson(response, 200, "{\"success\":true,\"message\":\"文件重命名成功\"}");
                    }
                    else if (Directory.Exists(path))
                    {
                        Directory.Move(path, dest);
                        HttpServer.ServeJson(response, 200, "{\"success\":true,\"message\":\"文件夹重命名成功\"}");
                    }
                    else
                    {
                        HttpServer.ServeJson(response, 404, "{\"success\":false,\"message\":\"Path not found\"}");
                    }
                }
                else if (rawPath.Equals("api/clipboard/set", StringComparison.OrdinalIgnoreCase))
                {
                    string pathsStr = request.QueryString["paths"];
                    string action = request.QueryString["action"];
                    if (string.IsNullOrEmpty(pathsStr) || string.IsNullOrEmpty(action))
                    {
                        HttpServer.ServeJson(response, 400, "{\"success\":false,\"message\":\"Missing paths or action\"}");
                        return true;
                    }
                    FileExplorer.clipboardPaths.Clear();
                    string[] paths = pathsStr.Split(new char[] { '|' }, StringSplitOptions.RemoveEmptyEntries);
                    foreach (string p in paths)
                    {
                        if (File.Exists(p) || Directory.Exists(p))
                        {
                            FileExplorer.clipboardPaths.Add(p);
                        }
                    }
                    FileExplorer.isClipboardCut = action.Equals("cut", StringComparison.OrdinalIgnoreCase);
                    HttpServer.ServeJson(response, 200, string.Format("{{\"success\":true,\"count\":{0},\"action\":\"{1}\"}}", FileExplorer.clipboardPaths.Count, FileExplorer.isClipboardCut ? "cut" : "copy"));
                }
                else if (rawPath.Equals("api/file/paste", StringComparison.OrdinalIgnoreCase))
                {
                    string targetDir = request.QueryString["targetDir"];
                    if (string.IsNullOrEmpty(targetDir) || !Directory.Exists(targetDir))
                    {
                        HttpServer.ServeJson(response, 400, "{\"success\":false,\"message\":\"Invalid target directory\"}");
                        return true;
                    }
                    int count = 0;
                    foreach (string src in FileExplorer.clipboardPaths)
                    {
                        string name = Path.GetFileName(src);
                        if (string.IsNullOrEmpty(name)) continue;
                        string dest = Path.Combine(targetDir, name);

                        if (File.Exists(src))
                        {
                            if (FileExplorer.isClipboardCut)
                            {
                                if (File.Exists(dest)) File.Delete(dest);
                                File.Move(src, dest);
                            }
                            else
                            {
                                File.Copy(src, dest, true);
                            }
                            count++;
                        }
                        else if (Directory.Exists(src))
                        {
                            if (FileExplorer.isClipboardCut)
                            {
                                if (Directory.Exists(dest)) Directory.Delete(dest, true);
                                Directory.Move(src, dest);
                            }
                            else
                            {
                                CopyDirectory(src, dest);
                            }
                            count++;
                        }
                    }
                    if (FileExplorer.isClipboardCut)
                    {
                        FileExplorer.clipboardPaths.Clear();
                    }
                    HttpServer.ServeJson(response, 200, string.Format("{{\"success\":true,\"message\":\"成功粘贴 {0} 个项目\"}}", count));
                }
                else if (rawPath.Equals("api/file/properties", StringComparison.OrdinalIgnoreCase))
                {
                    string pathsStr = request.QueryString["paths"];
                    if (string.IsNullOrEmpty(pathsStr))
                    {
                        HttpServer.ServeJson(response, 400, "{\"success\":false,\"message\":\"Missing paths\"}");
                        return true;
                    }
                    string[] paths = pathsStr.Split(new char[] { '|' }, StringSplitOptions.RemoveEmptyEntries);
                    if (paths.Length == 0)
                    {
                        HttpServer.ServeJson(response, 400, "{\"success\":false,\"message\":\"No valid paths\"}");
                        return true;
                    }

                    if (paths.Length == 1)
                    {
                        string p = paths[0];
                        if (File.Exists(p))
                        {
                            FileInfo fi = new FileInfo(p);
                            string parent = Path.GetDirectoryName(p) ?? "";
                            string extName = fi.Extension.TrimStart('.').ToUpper();
                            string sizeText = HttpServer.FormatFileSize(fi.Length);
                            string attrs = fi.Attributes.ToString();
                            string created = fi.CreationTime.ToString("yyyy-MM-dd HH:mm:ss");
                            string modified = fi.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss");

                            StringBuilder json = new StringBuilder();
                            json.Append("{");
                            json.Append("\"success\":true,");
                            json.Append("\"multi\":false,");
                            json.AppendFormat("\"name\":\"{0}\",", HttpServer.EscapeJson(fi.Name));
                            json.AppendFormat("\"isDir\":false,");
                            json.AppendFormat("\"ext\":\"{0}\",", HttpServer.EscapeJson(extName));
                            json.AppendFormat("\"folder\":\"{0}\",", HttpServer.EscapeJson(parent));
                            json.AppendFormat("\"path\":\"{0}\",", HttpServer.EscapeJson(p));
                            json.AppendFormat("\"size\":\"{0}\",", HttpServer.EscapeJson(sizeText));
                            json.AppendFormat("\"sizeBytes\":{0},", fi.Length);
                            json.AppendFormat("\"created\":\"{0}\",", HttpServer.EscapeJson(created));
                            json.AppendFormat("\"modified\":\"{0}\",", HttpServer.EscapeJson(modified));
                            json.AppendFormat("\"attrs\":\"{0}\"", HttpServer.EscapeJson(attrs));
                            json.Append("}");
                            HttpServer.ServeJson(response, 200, json.ToString());
                        }
                        else if (Directory.Exists(p))
                        {
                            DirectoryInfo di = new DirectoryInfo(p);
                            string parent = Path.GetDirectoryName(p) ?? "";
                            string created = di.CreationTime.ToString("yyyy-MM-dd HH:mm:ss");
                            string modified = di.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss");
                            string attrs = di.Attributes.ToString();

                            long totalSize = 0;
                            long fileCount = 0;
                            long dirCount = 0;
                            try
                            {
                                CalculateDirectoryInfo(p, ref totalSize, ref fileCount, ref dirCount);
                            }
                            catch {}

                            string sizeText = HttpServer.FormatFileSize(totalSize);

                            StringBuilder json = new StringBuilder();
                            json.Append("{");
                            json.Append("\"success\":true,");
                            json.Append("\"multi\":false,");
                            json.AppendFormat("\"name\":\"{0}\",", HttpServer.EscapeJson(di.Name));
                            json.AppendFormat("\"isDir\":true,");
                            json.AppendFormat("\"folder\":\"{0}\",", HttpServer.EscapeJson(parent));
                            json.AppendFormat("\"path\":\"{0}\",", HttpServer.EscapeJson(p));
                            json.AppendFormat("\"size\":\"{0}\",", HttpServer.EscapeJson(sizeText));
                            json.AppendFormat("\"sizeBytes\":{0},", totalSize);
                            json.AppendFormat("\"files\":{0},", fileCount);
                            json.AppendFormat("\"folders\":{0},", dirCount);
                            json.AppendFormat("\"created\":\"{0}\",", HttpServer.EscapeJson(created));
                            json.AppendFormat("\"modified\":\"{0}\",", HttpServer.EscapeJson(modified));
                            json.AppendFormat("\"attrs\":\"{0}\"", HttpServer.EscapeJson(attrs));
                            json.Append("}");
                            HttpServer.ServeJson(response, 200, json.ToString());
                        }
                        else
                        {
                            HttpServer.ServeJson(response, 404, "{\"success\":false,\"message\":\"路径不存在\"}");
                        }
                    }
                    else
                    {
                        // Multi items properties
                        long totalSize = 0;
                        long fileCount = 0;
                        long dirCount = 0;
                        string parentDir = "";

                        if (paths.Length > 0)
                        {
                            try
                            {
                                parentDir = Path.GetDirectoryName(paths[0]) ?? "";
                            }
                            catch {}
                        }

                        foreach (string p in paths)
                        {
                            if (File.Exists(p))
                            {
                                FileInfo fi = new FileInfo(p);
                                totalSize += fi.Length;
                                fileCount++;
                            }
                            else if (Directory.Exists(p))
                            {
                                dirCount++;
                                try
                                {
                                    CalculateDirectoryInfo(p, ref totalSize, ref fileCount, ref dirCount);
                                }
                                catch {}
                                }
                        }

                        string sizeText = HttpServer.FormatFileSize(totalSize);

                        StringBuilder json = new StringBuilder();
                        json.Append("{");
                        json.Append("\"success\":true,");
                        json.Append("\"multi\":true,");
                        json.AppendFormat("\"count\":{0},", paths.Length);
                        json.AppendFormat("\"files\":{0},", fileCount);
                        json.AppendFormat("\"folders\":{0},", dirCount);
                        json.AppendFormat("\"folder\":\"{0}\",", HttpServer.EscapeJson(parentDir));
                        json.AppendFormat("\"size\":\"{0}\",", HttpServer.EscapeJson(sizeText));
                        json.AppendFormat("\"sizeBytes\":{0}", totalSize);
                        json.Append("}");
                        HttpServer.ServeJson(response, 200, json.ToString());
                    }
                }
            else if (rawPath.Equals("api/file/open-host", StringComparison.OrdinalIgnoreCase))
                {
                    string pathStr = request.QueryString["path"];
                    if (string.IsNullOrEmpty(pathStr))
                    {
                        HttpServer.ServeJson(response, 400, "{\"success\":false,\"message\":\"缺少路径参数\"}");
                        return true;
                    }
                    if (!File.Exists(pathStr) && !Directory.Exists(pathStr))
                    {
                        HttpServer.ServeJson(response, 404, "{\"success\":false,\"message\":\"路径不存在\"}");
                        return true;
                    }
                    try
                    {
                        System.Diagnostics.ProcessStartInfo psi = new System.Diagnostics.ProcessStartInfo();
                        if (File.Exists(pathStr))
                        {
                            psi.FileName = "explorer.exe";
                            psi.Arguments = string.Format("/select,\"{0}\"", pathStr);
                        }
                        else
                        {
                            psi.FileName = pathStr;
                            psi.UseShellExecute = true;
                        }
                        System.Diagnostics.Process.Start(psi);
                        Logger.Log(string.Format("在宿主电脑上定位/打开项目: {0}", pathStr));
                        HttpServer.ServeJson(response, 200, "{\"success\":true}");
                    }
                    catch (Exception ex)
                    {
                        Logger.Log(string.Format("在宿主电脑上打开项目失败: {0}, 错误: {1}", pathStr, ex.Message));
                        HttpServer.ServeJson(response, 500, string.Format("{{\"success\":false,\"message\":\"{0}\"}}", HttpServer.EscapeJson(ex.Message)));
                    }
                }
                else if (rawPath.Equals("api/file/open-terminal", StringComparison.OrdinalIgnoreCase))
                {
                    string pathStr = request.QueryString["path"];
                    string exeStr = request.QueryString["exe"];
                    if (string.IsNullOrEmpty(pathStr) || pathStr.Equals("undefined", StringComparison.OrdinalIgnoreCase))
                    {
                        pathStr = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                        if (string.IsNullOrEmpty(pathStr) || !Directory.Exists(pathStr))
                        {
                            pathStr = AppDomain.CurrentDomain.BaseDirectory;
                        }
                    }
                    if (string.IsNullOrEmpty(exeStr))
                    {
                        exeStr = "cmd.exe";
                    }

                    if (!Directory.Exists(pathStr))
                    {
                        if (File.Exists(pathStr))
                        {
                            pathStr = Path.GetDirectoryName(pathStr);
                        }
                        else
                        {
                            HttpServer.ServeJson(response, 404, "{\"success\":false,\"message\":\"路径不存在\"}");
                            return true;
                        }
                    }

                    try
                    {
                        System.Diagnostics.ProcessStartInfo psi = new System.Diagnostics.ProcessStartInfo();
                        psi.FileName = exeStr;
                        psi.WorkingDirectory = pathStr;
                        psi.UseShellExecute = true;
                        System.Diagnostics.Process.Start(psi);
                        Logger.Log(string.Format("在宿主电脑终端中打开路径: {0}, 使用终端: {1}", pathStr, exeStr));
                        HttpServer.ServeJson(response, 200, "{\"success\":true}");
                    }
                    catch (Exception ex)
                    {
                        Logger.Log(string.Format("打开宿主电脑终端失败: {0}, 错误: {1}", pathStr, ex.Message));
                        HttpServer.ServeJson(response, 500, string.Format("{{\"success\":false,\"message\":\"{0}\"}}", HttpServer.EscapeJson(ex.Message)));
                    }
                }
                else if (rawPath.Equals("api/explorer/exists", StringComparison.OrdinalIgnoreCase))
                {
                    string pathStr = request.QueryString["path"];
                    if (string.IsNullOrEmpty(pathStr))
                    {
                        HttpServer.ServeJson(response, 400, "{\"success\":false,\"message\":\"缺少路径参数\"}");
            return true;
                    }
                    bool exists = Directory.Exists(pathStr) || File.Exists(pathStr);
                    bool isDir = Directory.Exists(pathStr);
                    HttpServer.ServeJson(response, 200, string.Format("{{\"success\":true,\"exists\":{0},\"isDir\":{1}}}", exists.ToString().ToLower(), isDir.ToString().ToLower()));
                }
                else if (rawPath.Equals("api/explorer/tree", StringComparison.OrdinalIgnoreCase))
                {
                    string pathStr = request.QueryString["path"];
                    if (string.IsNullOrEmpty(pathStr))
                    {
                        var drives = DriveInfo.GetDrives();
                        StringBuilder dsb = new StringBuilder();
                        dsb.Append("{\"success\":true,\"parent\":\"\",\"folders\":[");
                        int count = 0;
                        for (int i = 0; i < drives.Length; i++)
                        {
                            if (drives[i].IsReady)
                            {
                                string dPath = drives[i].Name;
                                string dName = drives[i].Name.TrimEnd('\\');
                                dsb.AppendFormat("{0}{{\"name\":\"{1}\",\"path\":\"{2}\"}}", count > 0 ? "," : "", HttpServer.EscapeJson(dName), HttpServer.EscapeJson(dPath));
                                count++;
                            }
                        }
                        dsb.Append("]}");
                        HttpServer.ServeJson(response, 200, dsb.ToString());
                    }
                    else
                    {
                        try
                        {
                            string[] subdirs = Directory.GetDirectories(pathStr);
                            StringBuilder dsb = new StringBuilder();
                            dsb.Append("{\"success\":true,\"parent\":\"" + HttpServer.EscapeJson(pathStr) + "\",\"folders\":[");
                            int count = 0;
                            for (int i = 0; i < subdirs.Length; i++)
                            {
                                try
                                {
                                    DirectoryInfo di = new DirectoryInfo(subdirs[i]);
                                    string attrs = di.Attributes.ToString();
                                    dsb.AppendFormat("{0}{{\"name\":\"{1}\",\"path\":\"{2}\"}}", count > 0 ? "," : "", HttpServer.EscapeJson(di.Name), HttpServer.EscapeJson(di.FullName));
                                    count++;
                                }
                                catch {}
                            }
                            dsb.Append("]}");
                            HttpServer.ServeJson(response, 200, dsb.ToString());
                        }
                        catch (Exception ex)
                        {
                            HttpServer.ServeJson(response, 500, string.Format("{{\"success\":false,\"message\":\"{0}\"}}", HttpServer.EscapeJson(ex.Message)));
                        }
                    }
                }
            else if (rawPath.Equals("api/file/preview", StringComparison.OrdinalIgnoreCase))
                {
                    string pathStr = request.QueryString["path"];
                    if (string.IsNullOrEmpty(pathStr))
                    {
                        HttpServer.ServeJson(response, 400, "{\"success\":false,\"message\":\"缺少路径参数\"}");
                        return true;
                    }
                    if (!File.Exists(pathStr))
                    {
                        HttpServer.ServeJson(response, 404, "{\"success\":false,\"message\":\"文件不存在\"}");
                        return true;
                    }
                    try
                    {
                        byte[] bytes = new byte[2048];
                        int bytesRead = 0;
                        using (FileStream fs = new FileStream(pathStr, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                        {
                            bytesRead = fs.Read(bytes, 0, bytes.Length);
                        }
                        Encoding detectedEncoding = HttpServer.DetectEncoding(pathStr);
                        string text = detectedEncoding.GetString(bytes, 0, bytesRead);
                        HttpServer.ServeJson(response, 200, string.Format("{{\"success\":true,\"content\":\"{0}\"}}", HttpServer.EscapeJson(text)));
                    }
                    catch (Exception ex)
                    {
                        HttpServer.ServeJson(response, 500, string.Format("{{\"success\":false,\"message\":\"{0}\"}}", HttpServer.EscapeJson(ex.Message)));
                    }
                }
            else
            { return false; }
            return true;
            #endregion
        }
    }
}
