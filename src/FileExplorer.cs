using System;
using System.IO;
using System.Net;
using System.Text;
using System.Collections.Generic;
using System.Windows.Forms;
using System.Diagnostics;

namespace LocalDiskServer
{
    public class QuickAccessItem
    {
        public string Key { get; set; }
        public string Emoji { get; set; }
        public string Title { get; set; }
        public string Description { get; set; }
        public string PhysicalPath { get; set; }
        public string WebPath { get; set; }
    }

    public static class FileExplorer
    {
        public static readonly List<string> clipboardPaths = new List<string>();
        public static bool isClipboardCut = false;

        public static List<QuickAccessItem> GetStandardQuickAccessItems()
        {
            var list = new List<QuickAccessItem>();

            // 1. Desktop (桌面)
            string desktop = ResolveSystemPath("Desktop", "Desktop", Environment.SpecialFolder.Desktop, "Desktop");
            if (!string.IsNullOrEmpty(desktop) && Directory.Exists(desktop))
            {
                list.Add(new QuickAccessItem
                {
                    Key = "desktop",
                    Emoji = "🖥️",
                    Title = I18nManager.T("quick_desktop"),
                    Description = I18nManager.T("quick_desktop_desc"),
                    PhysicalPath = desktop,
                    WebPath = HttpServer.PhysicalToWebPath(desktop)
                });
            }

            // 2. Downloads (下载 - GUID: {374DE290-123F-4565-9164-39C4925E467B})
            string downloads = ResolveSystemPath("{374DE290-123F-4565-9164-39C4925E467B}", "{374DE290-123F-4565-9164-39C4925E467B}", (Environment.SpecialFolder)(-1), "Downloads");
            if (string.IsNullOrEmpty(downloads) || !Directory.Exists(downloads))
            {
                downloads = ResolveSystemPath("{7D83EE9B-2244-4E70-B1F5-5393042AF1E4}", "{7D83EE9B-2244-4E70-B1F5-5393042AF1E4}", (Environment.SpecialFolder)(-1), "Downloads");
            }
            if (!string.IsNullOrEmpty(downloads) && Directory.Exists(downloads))
            {
                list.Add(new QuickAccessItem
                {
                    Key = "downloads",
                    Emoji = "📥",
                    Title = I18nManager.T("quick_downloads"),
                    Description = I18nManager.T("quick_downloads_desc"),
                    PhysicalPath = downloads,
                    WebPath = HttpServer.PhysicalToWebPath(downloads)
                });
            }

            // 3. Documents (文档)
            string docs = ResolveSystemPath("Personal", "Personal", Environment.SpecialFolder.MyDocuments, "Documents");
            if (!string.IsNullOrEmpty(docs) && Directory.Exists(docs))
            {
                list.Add(new QuickAccessItem
                {
                    Key = "documents",
                    Emoji = "📁",
                    Title = I18nManager.T("quick_documents"),
                    Description = I18nManager.T("quick_documents_desc"),
                    PhysicalPath = docs,
                    WebPath = HttpServer.PhysicalToWebPath(docs)
                });
            }

            // 4. Pictures (图片)
            string pictures = ResolveSystemPath("My Pictures", "My Pictures", Environment.SpecialFolder.MyPictures, "Pictures");
            if (!string.IsNullOrEmpty(pictures) && Directory.Exists(pictures))
            {
                list.Add(new QuickAccessItem
                {
                    Key = "pictures",
                    Emoji = "🖼️",
                    Title = I18nManager.T("quick_pictures"),
                    Description = I18nManager.T("quick_pictures_desc"),
                    PhysicalPath = pictures,
                    WebPath = HttpServer.PhysicalToWebPath(pictures)
                });
            }

            // 5. Music (音乐)
            string music = ResolveSystemPath("My Music", "My Music", Environment.SpecialFolder.MyMusic, "Music");
            if (!string.IsNullOrEmpty(music) && Directory.Exists(music))
            {
                list.Add(new QuickAccessItem
                {
                    Key = "music",
                    Emoji = "🎵",
                    Title = I18nManager.T("quick_music"),
                    Description = I18nManager.T("quick_music_desc"),
                    PhysicalPath = music,
                    WebPath = HttpServer.PhysicalToWebPath(music)
                });
            }

            // 6. Videos (视频)
            string videos = ResolveSystemPath("My Video", "My Video", Environment.SpecialFolder.MyVideos, "Videos");
            if (!string.IsNullOrEmpty(videos) && Directory.Exists(videos))
            {
                list.Add(new QuickAccessItem
                {
                    Key = "videos",
                    Emoji = "🎬",
                    Title = I18nManager.T("quick_videos"),
                    Description = I18nManager.T("quick_videos_desc"),
                    PhysicalPath = videos,
                    WebPath = HttpServer.PhysicalToWebPath(videos)
                });
            }

            // 7. User Profile (用户个人根目录)
            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrEmpty(userProfile) && Directory.Exists(userProfile))
            {
                list.Add(new QuickAccessItem
                {
                    Key = "user_profile",
                    Emoji = "👤",
                    Title = I18nManager.T("quick_user_profile"),
                    Description = I18nManager.T("quick_user_profile_desc"),
                    PhysicalPath = userProfile,
                    WebPath = HttpServer.PhysicalToWebPath(userProfile)
                });
            }

            // 8. Temp (临时文件夹)
            string temp = Path.GetTempPath();
            if (!string.IsNullOrEmpty(temp))
            {
                temp = temp.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (Directory.Exists(temp))
                {
                    list.Add(new QuickAccessItem
                    {
                        Key = "temp",
                        Emoji = "⚡",
                        Title = I18nManager.T("quick_temp"),
                        Description = I18nManager.T("quick_temp_desc"),
                        PhysicalPath = temp,
                        WebPath = HttpServer.PhysicalToWebPath(temp)
                    });
                }
            }

            return list;
        }

        private static string ResolveSystemPath(string userShellFoldersKey, string shellFoldersKey, Environment.SpecialFolder specialFolder, string fallbackSubDir)
        {
            string path = null;

            // 1. 优先查 HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders (支持用户自定义重定向)
            try
            {
                using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders"))
                {
                    if (key != null && !string.IsNullOrEmpty(userShellFoldersKey))
                    {
                        object val = key.GetValue(userShellFoldersKey);
                        if (val != null)
                        {
                            string raw = val.ToString();
                            path = Environment.ExpandEnvironmentVariables(raw);
                            if (Directory.Exists(path)) return path;
                        }
                    }
                }
            }
            catch { }

            // 2. 查 HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Shell Folders
            try
            {
                using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Shell Folders"))
                {
                    if (key != null && !string.IsNullOrEmpty(shellFoldersKey))
                    {
                        object val = key.GetValue(shellFoldersKey);
                        if (val != null)
                        {
                            string raw = val.ToString();
                            path = Environment.ExpandEnvironmentVariables(raw);
                            if (Directory.Exists(path)) return path;
                        }
                    }
                }
            }
            catch { }

            // 3. 查 SpecialFolder
            try
            {
                if ((int)specialFolder >= 0)
                {
                    path = Environment.GetFolderPath(specialFolder);
                    if (!string.IsNullOrEmpty(path) && Directory.Exists(path)) return path;
                }
            }
            catch { }

            // 4. 回退到 %USERPROFILE%\<fallbackSubDir>
            if (!string.IsNullOrEmpty(fallbackSubDir))
            {
                try
                {
                    string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                    string fb = Path.Combine(userProfile, fallbackSubDir);
                    if (Directory.Exists(fb)) return fb;
                }
                catch { }
            }

            return null;
        }

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

        public static string GetFileTypeDescription(string extension)
        {
            if (string.IsNullOrEmpty(extension))
            {
                return I18nManager.T("ft_file");
            }
            string ext = extension.TrimStart('.').ToLower();
            string key = "ft_" + ext;
            string desc = I18nManager.T(key);
            if (desc != key)
            {
                return desc;
            }

            switch (ext)
            {
                case "doc": return I18nManager.T("ft_doc");
                case "docx": return I18nManager.T("ft_docx");
                case "xls":
                case "xlsx":
                case "csv": return I18nManager.T("ft_xls");
                case "ppt":
                case "pptx": return I18nManager.T("ft_ppt");
                case "jpg":
                case "jpeg": return I18nManager.T("ft_jpg");
                case "zip":
                case "rar":
                case "7z":
                case "tar":
                case "gz": return I18nManager.T("ft_zip");
                case "bat":
                case "cmd": return I18nManager.T("ft_bat");
                case "conf":
                case "cfg": return I18nManager.T("ft_ini");
                case "yaml":
                case "yml": return I18nManager.T("ft_yml");
                case "kt":
                case "kts": return I18nManager.T("ft_kt");
                case "cpp":
                case "c":
                case "h": return I18nManager.T("ft_cpp");
                case "htm":
                case "html": return I18nManager.T("ft_html");
                case "mov":
                case "mkv":
                case "avi": return I18nManager.T("ft_video");
                case "wav":
                case "flac":
                case "aac": return I18nManager.T("ft_audio");
                default:
                    return I18nManager.T("type_file_suffix", ext.ToUpper());
            }
        }

        public static void ServeDirectory(HttpListenerResponse response, string dirPath, string webPath)
        {
            var favList = GetFavorites();
            StringBuilder sb = new StringBuilder();
            string folderName = Path.GetFileName(dirPath);
            if (string.IsNullOrEmpty(folderName)) folderName = dirPath;
            sb.Append(HttpServer.GetHtmlHeader(I18nManager.T("explorer_page_title", folderName), webPath, "layout-explorer"));
            sb.AppendFormat("<script>const currentDirPath = '{0}';</script>", dirPath.Replace("\\", "\\\\").Replace("'", "\\'"));

            string[] parts = webPath.Split(new char[] { '/' }, StringSplitOptions.RemoveEmptyEntries);

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

            var quickItems = GetStandardQuickAccessItems();
            foreach (var q in quickItems)
            {
                bool isActive = dirPath.Equals(q.PhysicalPath, StringComparison.OrdinalIgnoreCase);
                sb.AppendFormat("        <a href='{0}' class='tree-link{1}' title='{2}'>{3} {4}</a>",
                    q.WebPath, isActive ? " active-node" : "", q.PhysicalPath.Replace("'", "\'"), q.Emoji, q.Title);
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
            sb.AppendFormat("      <a href='/?view=gradle' class='tree-link' style='font-weight: bold;'>☕ {0}</a>", I18nManager.T("nav_gradle"));
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
                sb.AppendFormat("    <a href='{0}' class='btn-back' title='{1}'>⬅ {2}</a>", parentLink, I18nManager.T("toolbar_up_title"), I18nManager.T("toolbar_up"));
                sb.Append("    <span class='toolbar-separator'>|</span>");
            }

            // Breadcrumbs Path & Address Bar Wrapper
            sb.Append("    <div class='address-bar-wrapper' onmousedown='activateAddressInput(event)'>");
            sb.Append("      <div class='breadcrumbs' id='breadcrumbs-bar'>");
            sb.AppendFormat("        <a href='/'>{0}</a>", I18nManager.T("nav_home"));
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
            sb.AppendFormat("    <button id='protocol-switch-btn' onclick='toggleProtocol(event)' class='btn-back' style='height: 32px; padding: 0 10px; margin-left: 8px; border: 1px solid var(--border-color); border-radius: 4px; background: var(--container-bg); color: var(--text-color); cursor: pointer; font-size: 0.85rem; display: flex; align-items: center; gap: 4px; flex-shrink: 0;' title='{0}'></button>", I18nManager.T("lobby_proto_toggle_title"));
            sb.Append("  </div>");

            // Search Bar and View Switcher on the Right
            sb.Append("  <div class='toolbar-right' style='display: flex; align-items: center; gap: 8px;'>");
            sb.Append("    <select id='view-select' onchange='setViewMode(this.value)' style='height: 32px; background: var(--container-bg); color: var(--text-color); border: 1px solid var(--border-color); border-radius: 4px; padding: 4px 8px; cursor: pointer; outline: none; font-size: 0.85rem;'>");
            sb.AppendFormat("      <option value='details'>{0}</option>", I18nManager.T("lobby_view_details"));
            sb.AppendFormat("      <option value='large'>{0}</option>", I18nManager.T("lobby_view_large"));
            sb.AppendFormat("      <option value='medium'>{0}</option>", I18nManager.T("lobby_view_medium"));
            sb.Append("    </select>");
            sb.AppendFormat("    <input type='text' id='search' placeholder='{0}' oninput='filterList()'>", I18nManager.T("toolbar_search_placeholder"));
            sb.Append("  </div>");
            sb.Append("</div>"); // Close toolbar

            // Scrollable Content Area (Toolbar remains fixed above)
            sb.Append("<div class='explorer-scroll-area'>");
            sb.Append("<table id='file-table'>");
            sb.Append("<thead><tr>");
            sb.AppendFormat("  <th class='col-sortable' data-col='name' onclick='handleHeaderSort(\"name\")' style='width: 280px;'><span class='th-label'>{0}</span> <span class='sort-arrow'></span><div class='col-resizer' onmousedown='initColResize(event, this)'></div></th>", I18nManager.T("th_name"));
            sb.AppendFormat("  <th class='col-sortable' data-col='favorite' onclick='handleHeaderSort(\"favorite\")' style='width: 55px; text-align: center;'><span class='th-label'>{0}</span> <span class='sort-arrow'></span><div class='col-resizer' onmousedown='initColResize(event, this)'></div></th>", I18nManager.T("th_favorite"));
            sb.AppendFormat("  <th class='col-sortable' data-col='time' onclick='handleHeaderSort(\"time\")' style='width: 155px;'><span class='th-label'>{0}</span> <span class='sort-arrow'></span><div class='col-resizer' onmousedown='initColResize(event, this)'></div></th>", I18nManager.T("th_modify_time"));
            sb.AppendFormat("  <th class='col-sortable' data-col='type' onclick='handleHeaderSort(\"type\")' style='width: 130px;'><span class='th-label'>{0}</span> <span class='sort-arrow'></span><div class='col-resizer' onmousedown='initColResize(event, this)'></div></th>", I18nManager.T("th_type"));
            sb.AppendFormat("  <th class='col-sortable' data-col='size' onclick='handleHeaderSort(\"size\")' style='width: 80px; text-align: right;'><span class='th-label'>{0}</span> <span class='sort-arrow'></span></th>", I18nManager.T("th_size"));
            sb.Append("</tr></thead>");
            sb.Append("<tbody>");

            try
            {
                int itemIndex = 0;
                DateTime epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
                string folderTypeDesc = I18nManager.T("type_folder");

                // List directories
                string[] dirs = Directory.GetDirectories(dirPath);
                foreach (string d in dirs)
                {
                    DirectoryInfo di = new DirectoryInfo(d);
                    string name = di.Name;
                    string relativeLink = "/" + webPath.TrimEnd('/') + "/" + Uri.EscapeDataString(name) + "/";
                    bool isFav = favList.Contains(di.FullName);
                    string htmlEscapedPath = di.FullName.Replace("'", "&#39;").Replace("\"", "&quot;");
                    long timeMs = (long)(di.LastWriteTimeUtc - epoch).TotalMilliseconds;

                    sb.AppendFormat(
                        "<tr class='item-row dir-row' data-name='{0}' data-path='{1}' data-type='dir' data-type-desc='{2}' data-favorite='{3}' data-time='{4}' data-size='-1' data-original-index='{5}'>" +
                        "  <td><a href='{6}'>{7} <span class='name-text'>{8}</span></a></td>" +
                        "  <td style='text-align: center;'><span class='fav-star-btn{9}' data-path='{1}'>★</span></td>" +
                        "  <td>{10}</td>" +
                        "  <td>{2}</td>" +
                        "  <td style='text-align: right;'>-</td>" +
                        "</tr>",
                        name.ToLower(), htmlEscapedPath, folderTypeDesc, isFav ? "true" : "false", timeMs, itemIndex++, relativeLink, HttpServer.GetFolderSvg(), name, isFav ? " active" : "", di.LastWriteTime.ToString("yyyy-MM-dd HH:mm"));
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
                    long timeMs = (long)(fi.LastWriteTimeUtc - epoch).TotalMilliseconds;
                    string typeDesc = GetFileTypeDescription(fi.Extension);

                    sb.AppendFormat(
                        "<tr class='item-row file-row' data-name='{0}' data-path='{1}' data-type='file' data-type-desc='{2}' data-favorite='{3}' data-time='{4}' data-size='{5}' data-original-index='{6}'>" +
                        "  <td><a href='{7}'>{8} <span class='name-text'>{9}</span></a></td>" +
                        "  <td style='text-align: center;'><span class='fav-star-btn{10}' data-path='{1}'>★</span></td>" +
                        "  <td>{11}</td>" +
                        "  <td>{2}</td>" +
                        "  <td style='text-align: right;'>{12}</td>" +
                        "</tr>",
                        name.ToLower(), htmlEscapedPath, typeDesc, isFav ? "true" : "false", timeMs, fi.Length, itemIndex++, relativeLink, fileSvg, name, isFav ? " active" : "", fi.LastWriteTime.ToString("yyyy-MM-dd HH:mm"), sizeStr);
                }
            }
            catch (UnauthorizedAccessException)
            {
                sb.AppendFormat("<tr><td colspan='5' style='color: #e74c3c; padding: 20px; text-align: center;'>{0}</td></tr>", WebUtility.HtmlEncode(I18nManager.T("err_access_denied_dir")));
            }
            catch (Exception ex)
            {
                sb.AppendFormat("<tr><td colspan='5' style='color: #e74c3c; padding: 20px; text-align: center;'>{0}</td></tr>", WebUtility.HtmlEncode(I18nManager.T("err_read_dir_failed", ex.Message)));
            }

            sb.Append("</tbody></table>");
            sb.Append("</div>"); // Close explorer-scroll-area

            // 5.5 Bottom Status Bar
            sb.Append("<div class='explorer-statusbar' id='explorer-statusbar'>");
            sb.Append("  <div class='status-left' id='status-left'>");
            sb.AppendFormat("    <span id='status-count'>{0}</span>", I18nManager.T("status_total_items", 0));
            sb.Append("    <span class='status-separator'>|</span>");
            sb.AppendFormat("    <span id='status-detail'>{0}</span>", I18nManager.T("status_total_detail", 0, 0, "0 B"));
            sb.Append("  </div>");
            sb.Append("  <div class='status-right' id='status-right'>");
            sb.AppendFormat("    <span id='status-selected'>{0}</span>", I18nManager.T("status_no_selection"));
            sb.Append("  </div>");
            sb.Append("</div>");

            // 注入多语言状态栏词条字典供前端 JS 动态渲染使用
            sb.Append("<script>");
            sb.Append("window.I18N_STATUS = {");
            sb.AppendFormat("  totalItems: '{0}',", I18nManager.T("status_total_items", "{0}"));
            sb.AppendFormat("  totalDetail: '{0}',", I18nManager.T("status_total_detail", "{0}", "{1}", "{2}"));
            sb.AppendFormat("  selectedItems: '{0}',", I18nManager.T("status_selected_items", "{0}", "{1}"));
            sb.AppendFormat("  noSelection: '{0}'", I18nManager.T("status_no_selection"));
            sb.Append("};");
            sb.Append("</script>");

            sb.Append("</div>"); // Close explorer-main

            // 6. Right Live Preview Panel
            sb.Append("<div class='explorer-preview' id='preview-pane'>");
            sb.AppendFormat("  <div class='preview-expand-btn' onclick='toggleSidebar(\"right\")' style='display: none;'>{0}</div>", I18nManager.T("preview_btn_expand"));
            sb.Append("  <div class='preview-title' style='display: flex; justify-content: space-between; align-items: center; width: 100%;'>");
            sb.AppendFormat("    <span>ℹ️ {0}</span>", I18nManager.T("preview_title"));
            sb.AppendFormat("    <span class='preview-toggle-btn' onclick='toggleSidebar(\"right\"); event.stopPropagation();' style='cursor: pointer; font-size: 0.8rem; color: var(--text-muted); padding: 2px 6px; border-radius: 4px;' title='{0}'>▶</span>", I18nManager.T("preview_btn_collapse"));
            sb.Append("  </div>");
            sb.Append("  <div class='preview-content' id='preview-content'>");
            sb.AppendFormat("    <div style='color: var(--text-muted); font-size: 0.9rem; padding-top: 40px;'>🔍 {0}</div>", I18nManager.T("status_no_selection"));
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
                HttpServer.ServeError(response, 500, I18nManager.T("err_read_text_failed", ex.Message));
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
                        HttpServer.ServeJson(response, 400, string.Format("{{\"success\":false,\"message\":\"{0}\"}}", HttpServer.EscapeJson(I18nManager.T("api_missing_path"))));
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
                        HttpServer.ServeJson(response, 400, string.Format("{{\"success\":false,\"message\":\"{0}\"}}", HttpServer.EscapeJson(I18nManager.T("api_missing_param"))));
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
                    HttpServer.ServeJson(response, 200, string.Format("{{\"success\":true,\"message\":\"{0}\"}}", HttpServer.EscapeJson(I18nManager.T("api_delete_success", count))));
                }
                else if (rawPath.Equals("api/file/rename", StringComparison.OrdinalIgnoreCase))
                {
                    string path = request.QueryString["path"];
                    string newName = request.QueryString["newName"];
                    if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(newName))
                    {
                        HttpServer.ServeJson(response, 400, string.Format("{{\"success\":false,\"message\":\"{0}\"}}", HttpServer.EscapeJson(I18nManager.T("api_missing_param"))));
                        return true;
                    }
                    string parent = Path.GetDirectoryName(path);
                    string dest = Path.Combine(parent, newName);
                    if (File.Exists(path))
                    {
                        File.Move(path, dest);
                        HttpServer.ServeJson(response, 200, string.Format("{{\"success\":true,\"message\":\"{0}\"}}", HttpServer.EscapeJson(I18nManager.T("api_rename_file_success"))));
                    }
                    else if (Directory.Exists(path))
                    {
                        Directory.Move(path, dest);
                        HttpServer.ServeJson(response, 200, string.Format("{{\"success\":true,\"message\":\"{0}\"}}", HttpServer.EscapeJson(I18nManager.T("api_rename_folder_success"))));
                    }
                    else
                    {
                        HttpServer.ServeJson(response, 404, string.Format("{{\"success\":false,\"message\":\"{0}\"}}", HttpServer.EscapeJson(I18nManager.T("api_path_not_found"))));
                    }
                }
                else if (rawPath.Equals("api/clipboard/set", StringComparison.OrdinalIgnoreCase))
                {
                    string pathsStr = request.QueryString["paths"];
                    string action = request.QueryString["action"];
                    if (string.IsNullOrEmpty(pathsStr) || string.IsNullOrEmpty(action))
                    {
                        HttpServer.ServeJson(response, 400, string.Format("{{\"success\":false,\"message\":\"{0}\"}}", HttpServer.EscapeJson(I18nManager.T("api_missing_param"))));
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
                        HttpServer.ServeJson(response, 400, string.Format("{{\"success\":false,\"message\":\"{0}\"}}", HttpServer.EscapeJson(I18nManager.T("api_invalid_target_dir"))));
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
                    HttpServer.ServeJson(response, 200, string.Format("{{\"success\":true,\"message\":\"{0}\"}}", HttpServer.EscapeJson(I18nManager.T("api_paste_success", count))));
                }
                else if (rawPath.Equals("api/file/properties", StringComparison.OrdinalIgnoreCase))
                {
                    string pathsStr = request.QueryString["paths"];
                    if (string.IsNullOrEmpty(pathsStr))
                    {
                        HttpServer.ServeJson(response, 400, string.Format("{{\"success\":false,\"message\":\"{0}\"}}", HttpServer.EscapeJson(I18nManager.T("api_missing_param"))));
                        return true;
                    }
                    string[] paths = pathsStr.Split(new char[] { '|' }, StringSplitOptions.RemoveEmptyEntries);
                    if (paths.Length == 0)
                    {
                        HttpServer.ServeJson(response, 400, string.Format("{{\"success\":false,\"message\":\"{0}\"}}", HttpServer.EscapeJson(I18nManager.T("api_no_valid_paths"))));
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
                            HttpServer.ServeJson(response, 404, string.Format("{{\"success\":false,\"message\":\"{0}\"}}", HttpServer.EscapeJson(I18nManager.T("api_path_not_found"))));
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
                        HttpServer.ServeJson(response, 400, string.Format("{{\"success\":false,\"message\":\"{0}\"}}", HttpServer.EscapeJson(I18nManager.T("api_missing_path"))));
                        return true;
                    }
                    if (!File.Exists(pathStr) && !Directory.Exists(pathStr))
                    {
                        HttpServer.ServeJson(response, 404, string.Format("{{\"success\":false,\"message\":\"{0}\"}}", HttpServer.EscapeJson(I18nManager.T("api_path_not_found"))));
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
                        Logger.Log(I18nManager.T("log_host_locate", pathStr));
                        HttpServer.ServeJson(response, 200, "{\"success\":true}");
                    }
                    catch (Exception ex)
                    {
                        Logger.Log(I18nManager.T("log_host_locate_failed", pathStr, ex.Message));
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
                            HttpServer.ServeJson(response, 404, string.Format("{{\"success\":false,\"message\":\"{0}\"}}", HttpServer.EscapeJson(I18nManager.T("api_path_not_found"))));
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
                        Logger.Log(I18nManager.T("log_host_terminal", pathStr, exeStr));
                        HttpServer.ServeJson(response, 200, "{\"success\":true}");
                    }
                    catch (Exception ex)
                    {
                        Logger.Log(I18nManager.T("log_host_terminal_failed", pathStr, ex.Message));
                        HttpServer.ServeJson(response, 500, string.Format("{{\"success\":false,\"message\":\"{0}\"}}", HttpServer.EscapeJson(ex.Message)));
                    }
                }
                else if (rawPath.Equals("api/explorer/exists", StringComparison.OrdinalIgnoreCase))
                {
                    string pathStr = request.QueryString["path"];
                    if (string.IsNullOrEmpty(pathStr))
                    {
                        HttpServer.ServeJson(response, 400, string.Format("{{\"success\":false,\"message\":\"{0}\"}}", HttpServer.EscapeJson(I18nManager.T("api_missing_path"))));
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
                        HttpServer.ServeJson(response, 400, string.Format("{{\"success\":false,\"message\":\"{0}\"}}", HttpServer.EscapeJson(I18nManager.T("api_missing_path"))));
                        return true;
                    }
                    if (!File.Exists(pathStr))
                    {
                        HttpServer.ServeJson(response, 404, string.Format("{{\"success\":false,\"message\":\"{0}\"}}", HttpServer.EscapeJson(I18nManager.T("api_file_not_found"))));
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
