using System;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace LocalDiskServer
{
    public static class HttpServer
    {
        public static HttpListener listener;
        public static HttpListener httpsListener;
        public static Thread serverThread;
        public static Thread httpsServerThread;
        public static readonly string versionHash = DateTime.Now.Ticks.ToString("x");

        public static void StartServer()
        {
            try
            {
                StopServer();

                int port = ServerApplicationContext.port;
                int httpsPort = ServerApplicationContext.https_port;
                bool useHttps = ServerApplicationContext.use_https;

                // 1. 初始化 HTTP Listener
                listener = new HttpListener();
                try
                {
                    listener.Prefixes.Add(string.Format("http://localhost:{0}/", port));
                }
                catch { }

                try
                {
                    listener.Prefixes.Add(string.Format("http://127.0.0.1:{0}/", port));
                }
                catch { }

                if (listener.Prefixes.Count == 0)
                {
                    throw new Exception(I18nManager.T("err_http_bind_fail"));
                }

                listener.Start();
                Logger.Log(I18nManager.T("log_http_started", port));

                serverThread = new Thread(ServerLoop);
                serverThread.IsBackground = true;
                serverThread.Start();

                // 2. 初始化 HTTPS Listener (仅在 useHttps 为 true 时)
                bool httpsStarted = false;
                if (useHttps)
                {
                    try
                    {
                        httpsListener = new HttpListener();
                        try
                        {
                            httpsListener.Prefixes.Add(string.Format("https://localhost:{0}/", httpsPort));
                        }
                        catch { }

                        try
                        {
                            httpsListener.Prefixes.Add(string.Format("https://127.0.0.1:{0}/", httpsPort));
                        }
                        catch { }

                        if (httpsListener.Prefixes.Count > 0)
                        {
                            httpsListener.Start();
                            Logger.Log(I18nManager.T("log_https_started", httpsPort));

                            httpsServerThread = new Thread(HttpsServerLoop);
                            httpsServerThread.IsBackground = true;
                            httpsServerThread.Start();
                            httpsStarted = true;
                        }
                    }
                    catch (Exception ex)
                    {
                        Logger.Log(I18nManager.T("log_https_start_failed", ex.Message));
                        MessageBox.Show(I18nManager.T("dialog_https_start_fail", ex.Message), I18nManager.T("dialog_warning"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    }
                }

                // 3. 更新托盘状态菜单文本
                string statusText = string.Format("http://localhost:{0}", port);
                if (httpsStarted)
                {
                    statusText += string.Format(" & https://localhost:{0}", httpsPort);
                }
                ServerApplicationContext.statusMenuItem.Text = I18nManager.T("menu_status_running", statusText);

                string balloonMsg = I18nManager.T("tray_balloon_content", port);
                if (httpsStarted)
                {
                    balloonMsg = string.Format("HTTP: {0} | HTTPS: {1}\n{2}", port, httpsPort, I18nManager.T("tray_balloon_content", port));
                }
                ServerApplicationContext.trayIcon.ShowBalloonTip(3000, I18nManager.T("tray_balloon_title"), balloonMsg, ToolTipIcon.Info);
            }
            catch (Exception ex)
            {
                ServerApplicationContext.statusMenuItem.Text = I18nManager.T("menu_status_stopped");
                MessageBox.Show(I18nManager.T("dialog_error") + ": " + ex.Message, I18nManager.T("dialog_error"), MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        public static void StopServer()
        {
            if (listener != null)
            {
                try
                {
                    listener.Stop();
                    listener.Close();
                }
                catch { }
                listener = null;
            }

            if (serverThread != null)
            {
                try
                {
                    serverThread.Abort();
                }
                catch { }
                serverThread = null;
            }

            if (httpsListener != null)
            {
                try
                {
                    httpsListener.Stop();
                    httpsListener.Close();
                }
                catch { }
                httpsListener = null;
            }

            if (httpsServerThread != null)
            {
                try
                {
                    httpsServerThread.Abort();
                }
                catch { }
                httpsServerThread = null;
            }
        }

        private static void ServerLoop()
        {
            while (listener != null && listener.IsListening)
            {
                try
                {
                    HttpListenerContext context = listener.GetContext();
                    ThreadPool.QueueUserWorkItem(delegate {
                        ProcessRequest(context);
                    });
                }
                catch { }
            }
        }

        private static void HttpsServerLoop()
        {
            while (httpsListener != null && httpsListener.IsListening)
            {
                try
                {
                    HttpListenerContext context = httpsListener.GetContext();
                    ThreadPool.QueueUserWorkItem(delegate {
                        ProcessRequest(context);
                    });
                }
                catch { }
            }
        }

        private static void ProcessRequest(HttpListenerContext context)
        {
            HttpListenerRequest request = context.Request;
            HttpListenerResponse response = context.Response;

            try
            {
                string rawPath = Uri.UnescapeDataString(request.Url.LocalPath).Trim('/');
                if (!rawPath.Equals("api/logs", StringComparison.OrdinalIgnoreCase))
                {
                    Logger.Log(I18nManager.T("log_request_received", request.HttpMethod, request.RawUrl, request.RemoteEndPoint));
                }
                
                if (rawPath.Equals("favicon.ico", StringComparison.OrdinalIgnoreCase))
                {
                    response.StatusCode = 404;
                    response.Close();
                    return;
                }

                if (rawPath.Equals("style.css", StringComparison.OrdinalIgnoreCase))
                {
                    ServeStaticResource(response, "style.css", "text/css; charset=utf-8");
                    return;
                }

                if (rawPath.Equals("app.js", StringComparison.OrdinalIgnoreCase))
                {
                    ServeStaticResource(response, "app.js", "application/javascript; charset=utf-8");
                    return;
                }

                if (rawPath.Equals("gradle", StringComparison.OrdinalIgnoreCase))
                {
                    GradleExplorer.ServeGradleDashboard(response);
                    return;
                }

                if (rawPath.Equals("npm", StringComparison.OrdinalIgnoreCase))
                {
                    NpmExplorer.ServeNpmDashboard(response);
                    return;
                }

                if (rawPath.Equals("pnpm", StringComparison.OrdinalIgnoreCase))
                {
                    PnpmExplorer.ServePnpmDashboard(response);
                    return;
                }

                if (rawPath.Equals("maven", StringComparison.OrdinalIgnoreCase))
                {
                    MavenExplorer.ServeMavenDashboard(response);
                    return;
                }

                if (rawPath.StartsWith("api/", StringComparison.OrdinalIgnoreCase))
                {
                    HandleApiRequest(rawPath, request, response);
                    return;
                }

                if (string.IsNullOrEmpty(rawPath))
                {
                    string view = request.QueryString["view"];
                    if ("gradle".Equals(view, StringComparison.OrdinalIgnoreCase))
                    {
                        GradleExplorer.ServeGradleDashboard(response);
                    }
                    else
                    {
                        ServeDriveList(response);
                    }
                    return;
                }

                string[] parts = rawPath.Split('/');
                string driveLetter = parts[0];

                if (driveLetter.Length == 1 && char.IsLetter(driveLetter[0]))
                {
                    string physicalPath = driveLetter.ToUpper() + ":\\";
                    if (parts.Length > 1)
                    {
                        string subPath = string.Join(Path.DirectorySeparatorChar.ToString(), parts, 1, parts.Length - 1);
                        physicalPath = Path.Combine(physicalPath, subPath);
                    }

                    if (Directory.Exists(physicalPath))
                    {
                        FileExplorer.ServeDirectory(response, physicalPath, rawPath);
                    }
                    else if (File.Exists(physicalPath))
                    {
                        bool isRaw = request.QueryString["raw"] == "1";
                        bool forceText = request.QueryString["force_text"] == "1";
                        FileExplorer.ServeFile(response, physicalPath, isRaw, forceText);
                    }
                    else
                    {
                        ServeError(response, 404, I18nManager.T("err_not_found", physicalPath));
                    }
                }
                else
                {
                    ServeError(response, 400, I18nManager.T("err_invalid_drive"));
                }
            }
            catch (Exception ex)
            {
                ServeError(response, 500, I18nManager.T("err_internal", ex.Message));
            }
        }

        private static void HandleApiRequest(string rawPath, HttpListenerRequest request, HttpListenerResponse response)
        {
            if (request.HttpMethod == "OPTIONS")
            {
                response.StatusCode = 200;
                response.Headers.Add("Access-Control-Allow-Origin", "*");
                response.Headers.Add("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
                response.Headers.Add("Access-Control-Allow-Headers", "Content-Type");
                response.Close();
                return;
            }

            try
            {
                if (ServerApplicationContext.HandleSettingsApi(rawPath, request, response)) return;
                if (Logger.HandleApi(rawPath, request, response)) return;
                if (NpmExplorer.HandleApi(rawPath, request, response)) return;
                if (PnpmExplorer.HandleApi(rawPath, request, response)) return;
                if (MavenExplorer.HandleApi(rawPath, request, response)) return;
                if (GradleExplorer.HandleApi(rawPath, request, response)) return;
                if (FileExplorer.HandleApi(rawPath, request, response)) return;

                ServeError(response, 404, I18nManager.T("err_api_not_found", rawPath));
            }
            catch (Exception ex)
            {
                ServeJson(response, 500, "{\"success\":false,\"message\":\"" + EscapeJson(ex.Message) + "\"}");
            }
        }

        public static void ServeJson(HttpListenerResponse response, int statusCode, string json)
        {
            try
            {
                byte[] buffer = Encoding.UTF8.GetBytes(json);
                response.StatusCode = statusCode;
                response.ContentType = "application/json; charset=utf-8";
                response.ContentLength64 = buffer.Length;
                response.Headers.Add("Access-Control-Allow-Origin", "*");
                response.Headers.Add("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
                response.Headers.Add("Access-Control-Allow-Headers", "Content-Type");
                response.OutputStream.Write(buffer, 0, buffer.Length);
                response.Close();
            }
            catch { }
        }

        public static void ServeError(HttpListenerResponse response, int statusCode, string message)
        {
            try
            {
                response.StatusCode = statusCode;
                StringBuilder sb = new StringBuilder();
                sb.Append(GetHtmlHeader("Error - " + statusCode, ""));
                sb.AppendFormat("<div style='text-align:center; padding: 50px 20px;'>" +
                                "  <h1 style='font-size: 4rem; color: #e74c3c; margin: 0;'>{0}</h1>" +
                                "  <p style='font-size: 1.2rem; color: #7f8c8d;'>{1}</p>" +
                                "  <a href='/' style='display:inline-block; margin-top:20px; padding:10px 20px; background:#3498db; color:white; border-radius:4px; text-decoration:none;'>{2}</a>" +
                                "</div>", statusCode, WebUtility.HtmlEncode(message), I18nManager.T("err_back_home"));
                sb.Append(GetHtmlFooter());

                byte[] buffer = Encoding.UTF8.GetBytes(sb.ToString());
                response.ContentType = "text/html; charset=utf-8";
                response.ContentLength64 = buffer.Length;
                response.OutputStream.Write(buffer, 0, buffer.Length);
            }
            catch { }
            finally
            {
                try { response.OutputStream.Close(); } catch { }
            }
        }

        private static void ServeStaticResource(HttpListenerResponse response, string resourceName, string contentType)
        {
            try
            {
                string content = LoadResource(resourceName);
                byte[] buffer = Encoding.UTF8.GetBytes(content);
                response.StatusCode = 200;
                response.ContentType = contentType;
                response.ContentLength64 = buffer.Length;
                response.Headers.Add("Cache-Control", "public, max-age=3600");
                response.OutputStream.Write(buffer, 0, buffer.Length);
                response.OutputStream.Close();
            }
            catch
            {
                response.StatusCode = 500;
                response.Close();
            }
        }

        public static string LoadResource(string name)
        {
            using (var stream = System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream(name))
            {
                if (stream == null) return "";
                using (var reader = new System.IO.StreamReader(stream))
                {
                    return reader.ReadToEnd();
                }
            }
        }

        public static string GetHtmlHeader(string title, string pathInfo, string bodyClass = "")
        {
            string html = LoadResource("header.html");
            if (string.IsNullOrEmpty(html)) return "";
            html = html.Replace("{TITLE}", WebUtility.HtmlEncode(title));
            html = html.Replace("{BODY_CLASS}", bodyClass);
            html = html.Replace("{HTTP_PORT}", ServerApplicationContext.port.ToString());
            html = html.Replace("{HTTPS_PORT}", ServerApplicationContext.https_port.ToString());
            html = html.Replace("{USE_HTTPS}", ServerApplicationContext.use_https.ToString().ToLower());
            html = html.Replace("{VERSION_HASH}", versionHash);
            html = html.Replace("{I18N_JSON}", I18nManager.GetCurrentStringsJson());
            return html;
        }

        public static string GetHtmlFooter()
        {
            string html = LoadResource("footer.html");
            if (string.IsNullOrEmpty(html)) return "";
            
            StringBuilder sbShells = new StringBuilder();
            sbShells.Append("[");
            var shells = ServerApplicationContext.availableShells;
            for (int i = 0; i < shells.Count; i++)
            {
                var s = shells[i];
                if (i > 0) sbShells.Append(",");
                sbShells.AppendFormat("{{name:\"{0}\", exePath:\"{1}\"}}", EscapeJson(s.Name), EscapeJson(s.ExePath.Replace("\\", "\\\\")));
            }
            sbShells.Append("]");
            string shellsJson = sbShells.ToString();
            
            string footerHtml = html.Replace("{SHELLS_JSON}", shellsJson);
            footerHtml = footerHtml.Replace("{VERSION_HASH}", versionHash);
            footerHtml = footerHtml.Replace("{APP_VERSION}", ServerApplicationContext.APP_VERSION);
            footerHtml = footerHtml.Replace("{MODAL_PROPERTIES_TITLE}", I18nManager.T("modal_properties_title"));
            footerHtml = footerHtml.Replace("{MODAL_BTN_OK}", I18nManager.T("modal_btn_ok"));
            footerHtml = footerHtml.Replace("{MENU_VIEW_LOGS}", I18nManager.T("menu_view_logs"));
            footerHtml = footerHtml.Replace("{MODAL_LOGS_TITLE}", I18nManager.T("modal_logs_title"));
            footerHtml = footerHtml.Replace("{MODAL_LOGS_BTN_CLEAR}", I18nManager.T("modal_logs_btn_clear"));
            
            // Settings Modal Placeholders
            footerHtml = footerHtml.Replace("{SETTINGS_MODAL_TITLE}", I18nManager.T("settings_modal_title"));
            footerHtml = footerHtml.Replace("{SETTINGS_SEC_NETWORK}", I18nManager.T("settings_sec_network"));
            footerHtml = footerHtml.Replace("{SETTINGS_HTTP_PORT}", I18nManager.T("settings_http_port"));
            footerHtml = footerHtml.Replace("{SETTINGS_HTTPS_PORT}", I18nManager.T("settings_https_port"));
            footerHtml = footerHtml.Replace("{SETTINGS_USE_HTTPS}", I18nManager.T("settings_use_https"));
            footerHtml = footerHtml.Replace("{SETTINGS_SEC_FEATURES}", I18nManager.T("settings_sec_features"));
            footerHtml = footerHtml.Replace("{SETTINGS_ENABLE_DEV}", I18nManager.T("settings_enable_dev"));
            footerHtml = footerHtml.Replace("{SETTINGS_SEC_CACHE}", I18nManager.T("settings_sec_cache"));
            footerHtml = footerHtml.Replace("{SETTINGS_CACHE_SIZE_LABEL}", I18nManager.T("settings_cache_size_label"));
            footerHtml = footerHtml.Replace("{SETTINGS_CACHE_CALCULATING}", I18nManager.T("settings_cache_calculating"));
            footerHtml = footerHtml.Replace("{SETTINGS_BTN_CLEAR_CACHE}", I18nManager.T("settings_btn_clear_cache"));
            footerHtml = footerHtml.Replace("{SETTINGS_BTN_OPEN_CACHE_DIR}", I18nManager.T("settings_btn_open_cache_dir"));
            footerHtml = footerHtml.Replace("{SETTINGS_SEC_GENERAL}", I18nManager.T("settings_sec_general"));
            footerHtml = footerHtml.Replace("{SETTINGS_LANGUAGE}", I18nManager.T("settings_language"));
            footerHtml = footerHtml.Replace("{SETTINGS_STARTUP}", I18nManager.T("settings_startup"));
            footerHtml = footerHtml.Replace("{SETTINGS_SEC_TEXT_EXT}", I18nManager.T("settings_sec_text_ext"));
            footerHtml = footerHtml.Replace("{SETTINGS_TEXT_EXT_DESC}", I18nManager.T("settings_text_ext_desc"));
            footerHtml = footerHtml.Replace("{DIALOG_TEXT_EXT_TOGGLE_FORMAT}", I18nManager.T("dialog_text_ext_toggle_format"));
            footerHtml = footerHtml.Replace("{SETTINGS_SEC_SYSTEM_OPS}", I18nManager.T("settings_sec_system_ops"));
            footerHtml = footerHtml.Replace("{SETTINGS_BTN_OPEN_CONFIG}", I18nManager.T("settings_btn_open_config"));
            footerHtml = footerHtml.Replace("{SETTINGS_BTN_OPEN_APP_DIR}", I18nManager.T("settings_btn_open_app_dir"));
            footerHtml = footerHtml.Replace("{SETTINGS_BTN_VIEW_LOGS}", I18nManager.T("settings_btn_view_logs"));
            footerHtml = footerHtml.Replace("{SETTINGS_BTN_CANCEL}", I18nManager.T("settings_btn_cancel"));
            footerHtml = footerHtml.Replace("{SETTINGS_BTN_SAVE}", I18nManager.T("settings_btn_save"));

            return footerHtml;
        }

        public static bool IsTextExtension(string ext)
        {
            if (string.IsNullOrEmpty(ext)) return false;
            string cleanExt = ext.TrimStart('.').Trim().ToLower();
            string[] allowed = ServerApplicationContext.textExtensionsStr.Split(new char[] { ',', ';', ' ', '\r', '\n', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (string item in allowed)
            {
                if (item.TrimStart('.').Trim().ToLower() == cleanExt)
                {
                    return true;
                }
            }
            return false;
        }

        public static string GetMimeType(string ext)
        {
            switch (ext)
            {
                case ".html": case ".htm": return "text/html; charset=utf-8";
                case ".css": return "text/css";
                case ".js": return "application/javascript";
                case ".json": return "application/json; charset=utf-8";
                case ".png": return "image/png";
                case ".jpg": case ".jpeg": return "image/jpeg";
                case ".gif": return "image/gif";
                case ".svg": return "image/svg+xml";
                case ".ico": return "image/x-icon";
                case ".mp3": return "audio/mpeg";
                case ".wav": return "audio/wav";
                case ".mp4": return "video/mp4";
                case ".webm": return "video/webm";
                case ".pdf": return "application/pdf";
                default: return "application/octet-stream";
            }
        }

        public static string FormatFileSize(long bytes)
        {
            string[] suffixes = { "B", "KB", "MB", "GB", "TB" };
            int counter = 0;
            decimal number = bytes;
            while (Math.Round(number / 1024) >= 1)
            {
                number /= 1024;
                counter++;
            }
            return string.Format("{0:n1} {1}", number, suffixes[counter]);
        }

        public static string EscapeJson(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            StringBuilder sb = new StringBuilder();
            foreach (char c in s)
            {
                switch (c)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '"': sb.Append("\\\""); break;
                    case '/': sb.Append("\\/"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < ' ')
                        {
                            sb.AppendFormat("\\u{0:x4}", (int)c);
                        }
                        else
                        {
                            sb.Append(c);
                        }
                        break;
                }
            }
            return sb.ToString();
        }
    
        // --- Extracted SVG and Utility Helpers ---
        public static string GetDriveSvg()
        { return @"<svg class='file-icon' width='36' height='36' viewBox='0 0 24 24' fill='none' stroke='#2980b9' stroke-width='2' stroke-linecap='round' stroke-linejoin='round'><rect x='2' y='2' width='20' height='8' rx='2' ry='2'></rect><rect x='2' y='14' width='20' height='8' rx='2' ry='2'></rect><line x1='6' y1='6' x2='6.01' y2='6'></line><line x1='6' y1='18' x2='6.01' y2='18'></line></svg>"; }

        public static string GetFolderSvg()
        { return @"<svg class='file-icon' width='20' height='20' viewBox='0 0 24 24' fill='#f1c40f' stroke='#d35400' stroke-width='1.5' stroke-linecap='round' stroke-linejoin='round'><path d='M22 19a2 2 0 0 1-2 2H4a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h5l2 3h9a2 2 0 0 1 2 2z'></path></svg>"; }

        public static string GetFileIconSvg(string ext)
        {
            ext = ext.ToLower();
            string color = "#7f8c8d"; // Default gray for standard files
            
            // Highlight specific file types
            if (ext == ".txt" || ext == ".md" || ext == ".log" || ext == ".ini") color = "#3498db"; // Text files: Blue
            else if (ext == ".html" || ext == ".css" || ext == ".js" || ext == ".json") color = "#e67e22"; // Code: Orange
            else if (ext == ".png" || ext == ".jpg" || ext == ".jpeg" || ext == ".gif" || ext == ".svg") color = "#2ecc71"; // Images: Green
            else if (ext == ".mp3" || ext == ".wav") color = "#9b59b6"; // Audio: Purple
            else if (ext == ".mp4" || ext == ".webm") color = "#e74c3c"; // Video: Red
            else if (ext == ".pdf") color = "#c0392b"; // PDF: Dark Red
            else if (ext == ".zip" || ext == ".rar" || ext == ".7z" || ext == ".tar" || ext == ".gz") color = "#f1c40f"; // Archive: Yellow

            return string.Format(@"<svg class='file-icon' width='20' height='20' viewBox='0 0 24 24' fill='none' stroke='{0}' stroke-width='1.5' stroke-linecap='round' stroke-linejoin='round'><path d='M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8z'></path><polyline points='14 2 14 8 20 8'></polyline><line x1='16' y1='13' x2='8' y2='13'></line><line x1='16' y1='17' x2='8' y2='17'></line><polyline points='10 9 9 9 8 9'></polyline></svg>", color);
        }

        public static string PhysicalToWebPath(string physicalPath)
        {
            if (string.IsNullOrEmpty(physicalPath)) return "/";
            try
            {
                string fullPath = Path.GetFullPath(physicalPath);
                if (fullPath.Length >= 2 && fullPath[1] == ':')
                {
                    char driveLetter = char.ToLower(fullPath[0]);
                    string subPath = fullPath.Substring(2).Replace('\\', '/').Trim('/');
                    if (string.IsNullOrEmpty(subPath))
                    { return "/" + driveLetter + "/"; }
                    return "/" + driveLetter + "/" + subPath + "/";
                }
            }
            catch { }
            return "/";
        }

        public static string GetMonitorSvg()
        { return @"<svg class='file-icon' width='36' height='36' viewBox='0 0 24 24' fill='none' stroke='#3498db' stroke-width='2' stroke-linecap='round' stroke-linejoin='round'><rect x='2' y='3' width='20' height='14' rx='2' ry='2'></rect><line x1='8' y1='21' x2='16' y2='21'></line><line x1='12' y1='17' x2='12' y2='21'></line></svg>"; }

        public static string GetDocumentsSvg()
        { return @"<svg class='file-icon' width='36' height='36' viewBox='0 0 24 24' fill='none' stroke='#2ecc71' stroke-width='2' stroke-linecap='round' stroke-linejoin='round'><path d='M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8z'></path><polyline points='14 2 14 8 20 8'></polyline><line x1='16' y1='13' x2='8' y2='13'></line><line x1='16' y1='17' x2='8' y2='17'></line><polyline points='10 9 9 9 8 9'></polyline></svg>"; }

        public static string GetDownloadSvg()
        { return @"<svg class='file-icon' width='36' height='36' viewBox='0 0 24 24' fill='none' stroke='#e67e22' stroke-width='2' stroke-linecap='round' stroke-linejoin='round'><path d='M21 15v4a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-4'></path><polyline points='7 10 12 15 17 10'></polyline><line x1='12' y1='15' x2='12' y2='3'></line></svg>"; }

        public static string GetUserSvg()
        { return @"<svg class='file-icon' width='36' height='36' viewBox='0 0 24 24' fill='none' stroke='#9b59b6' stroke-width='2' stroke-linecap='round' stroke-linejoin='round'><path d='M20 21v-2a4 4 0 0 0-4-4H8a4 4 0 0 0-4 4v2'></path><circle cx='12' cy='7' r='4'></circle></svg>"; }

        public static string GetPicturesSvg()
        { return @"<svg class='file-icon' width='36' height='36' viewBox='0 0 24 24' fill='none' stroke='#e91e63' stroke-width='2' stroke-linecap='round' stroke-linejoin='round'><rect x='3' y='3' width='18' height='18' rx='2' ry='2'></rect><circle cx='8.5' cy='8.5' r='1.5'></circle><polyline points='21 15 16 10 5 21'></polyline></svg>"; }

        public static string GetMusicSvg()
        { return @"<svg class='file-icon' width='36' height='36' viewBox='0 0 24 24' fill='none' stroke='#00bcd4' stroke-width='2' stroke-linecap='round' stroke-linejoin='round'><path d='M9 18V5l12-2v13'></path><circle cx='6' cy='18' r='3'></circle><circle cx='18' cy='16' r='3'></circle></svg>"; }

        public static string GetVideosSvg()
        { return @"<svg class='file-icon' width='36' height='36' viewBox='0 0 24 24' fill='none' stroke='#ff5722' stroke-width='2' stroke-linecap='round' stroke-linejoin='round'><polygon points='23 7 16 12 23 17 23 7'></polygon><rect x='1' y='5' width='15' height='14' rx='2' ry='2'></rect></svg>"; }

        public static string GetTempSvg()
        { return @"<svg class='file-icon' width='36' height='36' viewBox='0 0 24 24' fill='none' stroke='#f39c12' stroke-width='2' stroke-linecap='round' stroke-linejoin='round'><polygon points='13 2 3 14 12 14 11 22 21 10 12 10 13 2'></polygon></svg>"; }

        public static string GetMavenSvg()
        { return @"<svg class='file-icon' width='36' height='36' viewBox='0 0 24 24' fill='none' stroke='#c71a36' stroke-width='2' stroke-linecap='round' stroke-linejoin='round'><path d='M12 2L2 7l10 5 10-5-10-5zM2 17l10 5 10-5M2 12l10 5 10-5'/></svg>"; }

        public static string GetNpmSvg()
        { return @"<svg class='file-icon' width='36' height='36' viewBox='0 0 24 24' fill='none' stroke='#cb3837' stroke-width='2' stroke-linecap='round' stroke-linejoin='round'><path d='M3 5h18v14H3V5zm3 11h3V8H6v8zm5 0h3v-5h2V8h-5v8zm7 0h3V8h-3v8z'/></svg>"; }

        public static string GetPnpmSvg()
        { return @"<svg class='file-icon' width='36' height='36' viewBox='0 0 24 24' fill='none' stroke='#f69220' stroke-width='2' stroke-linecap='round' stroke-linejoin='round'><rect x='3' y='3' width='5' height='5' rx='1'></rect><rect x='9.5' y='3' width='5' height='5' rx='1'></rect><rect x='16' y='3' width='5' height='5' rx='1'></rect><rect x='3' y='9.5' width='5' height='5' rx='1'></rect><rect x='9.5' y='9.5' width='5' height='5' rx='1'></rect><rect x='16' y='9.5' width='5' height='5' rx='1'></rect><rect x='3' y='16' width='5' height='5' rx='1'></rect><rect x='9.5' y='16' width='5' height='5' rx='1'></rect></svg>"; }

        public static string GetAndroidSvg()
        { return @"<svg class='file-icon' width='36' height='36' viewBox='0 0 24 24' fill='none' stroke='#3ddc84' stroke-width='2' stroke-linecap='round' stroke-linejoin='round'><path d='M17 10V6a5 5 0 0 0-10 0v4'></path><line x1='8' y1='3' x2='6' y2='1'></line><line x1='16' y1='3' x2='18' y2='1'></line><circle cx='9' cy='7' r='1' fill='#3ddc84'></circle><circle cx='15' cy='7' r='1' fill='#3ddc84'></circle><rect x='4' y='10' width='16' height='11' rx='2'></rect></svg>"; }

        public static string GetQuickAccessSvg(string key)
        {
            switch (key)
            {
                case "desktop": return GetMonitorSvg();
                case "downloads": return GetDownloadSvg();
                case "documents": return GetDocumentsSvg();
                case "pictures": return GetPicturesSvg();
                case "music": return GetMusicSvg();
                case "videos": return GetVideosSvg();
                case "user_profile": return GetUserSvg();
                case "temp": return GetTempSvg();
                default: return GetFolderSvg();
            }
        }

        // Action menu handlers
    
        public static void ServeDriveList(HttpListenerResponse response)
        {
            StringBuilder sb = new StringBuilder();
            sb.Append(HttpServer.GetHtmlHeader(I18nManager.T("lobby_title"), ""));

            // Unified compact toolbar for navigation home
            sb.Append("<div class='toolbar'>");
            sb.Append("  <div class='toolbar-left'>");
            sb.Append("    <div class='address-bar-wrapper' onmousedown='activateAddressInput(event)'>");
            sb.Append("      <div class='breadcrumbs' id='breadcrumbs-bar'>");
            sb.AppendFormat("        <span>🏠 {0}</span><span class='app-version-tag'>v{1}</span>", I18nManager.T("lobby_header_title"), ServerApplicationContext.APP_VERSION);
            sb.Append("      </div>");
            sb.Append("      <input type='text' id='address-input' style='display: none;' onkeydown='handleAddressKey(event)' onblur='deactivateAddressInput()'>");
            sb.Append("    </div>");
            sb.AppendFormat("    <button id='protocol-switch-btn' onclick='toggleProtocol(event)' class='btn-back' style='height: 32px; padding: 0 10px; margin-left: 8px; border: 1px solid var(--border-color); border-radius: 4px; background: var(--container-bg); color: var(--text-color); cursor: pointer; font-size: 0.85rem; display: flex; align-items: center; gap: 4px; flex-shrink: 0;' title='{0}'></button>", I18nManager.T("lobby_proto_toggle_title"));
            sb.Append("  </div>");
            sb.Append("  <div class='toolbar-right' style='display: flex; align-items: center; gap: 8px;'>");
            sb.AppendFormat("    <button class='btn-toolbar-settings' onclick='showSettingsModal()' style='height: 32px; padding: 0 10px; border: 1px solid var(--border-color); border-radius: 4px; background: var(--container-bg); color: var(--text-color); cursor: pointer; font-size: 0.85rem; display: flex; align-items: center; gap: 4px;' title='{0}'><span>⚙️</span> <span>{0}</span></button>", I18nManager.T("lobby_settings_btn"));
            sb.AppendFormat("    <button class='btn-toolbar-logs' onclick='showLogs()' style='height: 32px; padding: 0 10px; border: 1px solid var(--border-color); border-radius: 4px; background: var(--container-bg); color: var(--text-color); cursor: pointer; font-size: 0.85rem; display: flex; align-items: center; gap: 4px;' title='{0}'><span>📝</span> <span>{0}</span></button>", I18nManager.T("lobby_logs_btn"));
            sb.Append("    <select id='view-select' onchange='setViewMode(this.value)' style='height: 32px; background: var(--container-bg); color: var(--text-color); border: 1px solid var(--border-color); border-radius: 4px; padding: 4px 8px; cursor: pointer; outline: none; font-size: 0.85rem;'>");
            sb.AppendFormat("      <option value='details'>{0}</option>", I18nManager.T("lobby_view_details"));
            sb.AppendFormat("      <option value='large'>{0}</option>", I18nManager.T("lobby_view_large"));
            sb.AppendFormat("      <option value='medium'>{0}</option>", I18nManager.T("lobby_view_medium"));
            sb.Append("    </select>");
            sb.AppendFormat("    <input type='text' id='search' placeholder='{0}' oninput='filterCards()'>", I18nManager.T("lobby_search_placeholder"));
            sb.Append("  </div>");
            sb.Append("</div>");

            // Section 0: Favorites (if any)
            var favList = FileExplorer.GetFavorites();
            if (favList.Count > 0)
            {
                sb.AppendFormat("<h2>⭐ {0}</h2>", I18nManager.T("lobby_favorites_title"));
                sb.Append("<div class='grid'>");
                foreach (string fav in favList)
                {
                    if (File.Exists(fav) || Directory.Exists(fav))
                    {
                        bool isDir = Directory.Exists(fav);
                        string webLink = HttpServer.PhysicalToWebPath(fav);
                        string ext = isDir ? "" : Path.GetExtension(fav);
                        string icon = isDir ? HttpServer.GetFolderSvg() : HttpServer.GetFileIconSvg(ext);
                        string name = Path.GetFileName(fav);
                        if (string.IsNullOrEmpty(name)) name = fav; // E.g., drive root
                        string desc = isDir ? I18nManager.T("lobby_fav_folder") : I18nManager.T("lobby_fav_file");
                        string htmlEscapedPath = fav.Replace("'", "&#39;").Replace("\"", "&quot;");
                        sb.AppendFormat(
                            "<a href='{0}' class='card drive-card fav-card' data-path='{1}' data-type='{2}' data-favorite='true' style='position:relative;'>" +
                            "  <div class='icon-wrapper' style='color:#f1c40f;'>{3}</div>" +
                            "  <div class='card-info'>" +
                            "    <div class='title'>{4}</div>" +
                            "    <div class='desc'>{5}</div>" +
                            "  </div>" +
                            "  <span class='fav-star-btn active' data-path='{1}'>★</span>" +
                            "</a>",
                            webLink, htmlEscapedPath, isDir ? "dir" : "file", icon, name, desc);
                    }
                }
                sb.Append("</div>");
                sb.Append("<hr style='border: 0; border-top: 1px solid var(--border-color); margin: 12px 0;'>");
            }

            // Section 1: Quick Access Shortcuts
            sb.AppendFormat("<h2>🚀 {0}</h2>", I18nManager.T("quick_access_title"));
            sb.Append("<div class='grid'>");

            var quickList = FileExplorer.GetStandardQuickAccessItems();
            foreach (var q in quickList)
            {
                string iconSvg = HttpServer.GetQuickAccessSvg(q.Key);
                string htmlEscapedPath = q.PhysicalPath.Replace("'", "&#39;").Replace("\"", "&quot;");
                sb.AppendFormat(
                    "<a href='{0}' class='card drive-card' data-path='{1}' data-type='dir'>" +
                    "  <div class='icon-wrapper'>{2}</div>" +
                    "  <div class='card-info'>" +
                    "    <div class='title'>{3}</div>" +
                    "    <div class='desc'>{4}</div>" +
                    "  </div>" +
                    "</a>",
                    q.WebPath, htmlEscapedPath, iconSvg, q.Title, q.Description);
            }
            sb.Append("</div>");

            // Section 2: Developer Ecosystem & Package Repositories (Only when enabled)
            if (ServerApplicationContext.enable_dev_ecosystem)
            {
                sb.Append("<hr style='border: 0; border-top: 1px solid var(--border-color); margin: 16px 0;'>");
                sb.AppendFormat("<h2>📦 {0}</h2>", I18nManager.T("lobby_dev_ecosystem_title"));
                sb.Append("<div class='grid'>");

                // 1. Gradle (Ready)
                sb.AppendFormat(
                    "<a href='/?view=gradle' class='card drive-card' data-path='/?view=gradle' data-type='dir' title='{0} ({1}) - {2}'>" +
                    "  <div class='icon-wrapper' style='font-size: 2.2rem; display: flex; align-items: center; justify-content: center;'>☕</div>" +
                    "  <div class='card-info'>" +
                    "    <div class='card-title-row'>" +
                    "      <span class='title title-text' title='{0}'>{0}</span>" +
                    "      <span class='dev-badge ready'>{1}</span>" +
                    "    </div>" +
                    "    <div class='desc' title='{2}'>{2}</div>" +
                    "  </div>" +
                    "</a>",
                    I18nManager.T("lobby_gradle_title"), I18nManager.T("tag_ready"), I18nManager.T("lobby_gradle_desc")
                );

                // 2. Maven (Ready)
                sb.AppendFormat(
                    "<a href='/maven' class='card drive-card dev-card' title='{0} ({1}) - {2}'>" +
                    "  <div class='icon-wrapper'>{3}</div>" +
                    "  <div class='card-info'>" +
                    "    <div class='card-title-row'>" +
                    "      <span class='title title-text' title='{0}'>{0}</span>" +
                    "      <span class='dev-badge ready'>{1}</span>" +
                    "    </div>" +
                    "    <div class='desc' title='{2}'>{2}</div>" +
                    "  </div>" +
                    "</a>",
                    I18nManager.T("lobby_maven_title"), I18nManager.T("tag_ready"), I18nManager.T("lobby_maven_desc"), GetMavenSvg()
                );

                // 3. NPM (Ready)
                sb.AppendFormat(
                    "<a href='/npm' class='card drive-card' title='{1} ({2}) - {3}'>" +
                    "  <div class='icon-wrapper'>{0}</div>" +
                    "  <div class='card-info'>" +
                    "    <div class='card-title-row'>" +
                    "      <span class='title title-text' title='{1}'>{1}</span>" +
                    "      <span class='dev-badge ready'>{2}</span>" +
                    "    </div>" +
                    "    <div class='desc' title='{3}'>{3}</div>" +
                    "  </div>" +
                    "</a>",
                    GetNpmSvg(), I18nManager.T("lobby_npm_title"), I18nManager.T("tag_ready"), I18nManager.T("lobby_npm_desc")
                );

                // 4. PNPM (Ready)
                sb.AppendFormat(
                    "<a href='/pnpm' class='card drive-card' title='{1} ({2}) - {3}'>" +
                    "  <div class='icon-wrapper'>{0}</div>" +
                    "  <div class='card-info'>" +
                    "    <div class='card-title-row'>" +
                    "      <span class='title title-text' title='{1}'>{1}</span>" +
                    "      <span class='dev-badge ready'>{2}</span>" +
                    "    </div>" +
                    "    <div class='desc' title='{3}'>{3}</div>" +
                    "  </div>" +
                    "</a>",
                    GetPnpmSvg(), I18nManager.T("lobby_pnpm_title"), I18nManager.T("tag_ready"), I18nManager.T("lobby_pnpm_desc")
                );

                // 5. Android (In Plan)
                sb.AppendFormat(
                    "<div class='card drive-card dev-card-disabled' style='opacity: 0.85; cursor: default;' title='{1} ({2}) - {3}'>" +
                    "  <div class='icon-wrapper'>{0}</div>" +
                    "  <div class='card-info'>" +
                    "    <div class='card-title-row'>" +
                    "      <span class='title title-text' title='{1}'>{1}</span>" +
                    "      <span class='dev-badge plan'>{2}</span>" +
                    "    </div>" +
                    "    <div class='desc' title='{3}'>{3}</div>" +
                    "  </div>" +
                    "</div>",
                    GetAndroidSvg(), I18nManager.T("lobby_android_title"), I18nManager.T("tag_coming_soon"), I18nManager.T("lobby_android_desc")
                );

                sb.Append("</div>");
            }
            sb.Append("<hr style='border: 0; border-top: 1px solid var(--border-color); margin: 16px 0;'>");

            // Section 3: Physical Drives
            sb.AppendFormat("<h2>💾 {0}</h2>", I18nManager.T("lobby_drives_title"));
            sb.Append("<div class='grid'>");

            DriveInfo[] drives = DriveInfo.GetDrives();
            foreach (DriveInfo drive in drives)
            {
                if (drive.IsReady)
                {
                    string dLetter = drive.Name.Substring(0, 1).ToLower();
                    string sizeInfo = I18nManager.T("lobby_drive_space", 
                        drive.AvailableFreeSpace / 1024 / 1024 / 1024, 
                        drive.TotalSize / 1024 / 1024 / 1024);
                    string htmlEscapedDrivePath = drive.Name.Replace("'", "&#39;").Replace("\"", "&quot;");
                    string driveName = I18nManager.T("lobby_drive_name", drive.Name.Substring(0, 1).ToUpper());

                    sb.AppendFormat(
                        "<a href='/{0}/' class='card drive-card' data-path='{1}' data-type='dir' data-drive='true'>" +
                        "  <div class='icon-wrapper'>{2}</div>" +
                        "  <div class='card-info'>" +
                        "    <div class='title'>{3}</div>" +
                        "    <div class='desc'>{4}</div>" +
                        "  </div>" +
                        "</a>",
                        dLetter, htmlEscapedDrivePath, HttpServer.GetDriveSvg(), driveName, sizeInfo);
                }
            }
            sb.Append("</div>");

            sb.AppendFormat(
                "<div class='lobby-footer'>" +
                "  <span class='lobby-footer-title'>⚡ LocalDiskServer <span class='lobby-footer-ver'>v{0}</span></span>" +
                "</div>",
                ServerApplicationContext.APP_VERSION
            );

            sb.Append(HttpServer.GetHtmlFooter());

            byte[] buffer = Encoding.UTF8.GetBytes(sb.ToString());
            response.ContentType = "text/html; charset=utf-8";
            response.ContentLength64 = buffer.Length;
            response.OutputStream.Write(buffer, 0, buffer.Length);
            response.OutputStream.Close();
        }
    
        
        public static Encoding DetectEncoding(string filePath)
        {
            byte[] bom = new byte[4];
            int readBytes = 0;
            try
            {
                using (var file = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    readBytes = file.Read(bom, 0, 4);
                }
            }
            catch { }

            if (readBytes >= 3 && bom[0] == 0xef && bom[1] == 0xbb && bom[2] == 0xbf) return Encoding.UTF8;
            if (readBytes >= 2 && bom[0] == 0xff && bom[1] == 0xfe) return Encoding.Unicode; // UTF-16 LE
            if (readBytes >= 2 && bom[0] == 0xfe && bom[1] == 0xff) return Encoding.BigEndianUnicode; // UTF-16 BE
            if (readBytes >= 4 && bom[0] == 0x00 && bom[1] == 0x00 && bom[2] == 0xfe && bom[3] == 0xff) return Encoding.UTF32;

            // Heuristic detection: Read 8KB buffer and analyze UTF-8 integrity
            byte[] buffer = new byte[8192];
            int bufferRead = 0;
            try
            {
                using (var file = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    bufferRead = file.Read(buffer, 0, buffer.Length);
                }
            }
            catch { }

            if (bufferRead > 0 && IsValidUtf8(buffer, bufferRead))
            { return Encoding.UTF8; }

            // Fallback to GBK/CP936 for standard Windows Chinese compatibility, then system default ANSI
            try
            {
                return Encoding.GetEncoding(936); // GBK
            }
            catch
            { return Encoding.Default; }
        }

        public static bool IsValidUtf8(byte[] buffer, int length)
        {
            int i = 0;
            while (i < length)
            {
                if (buffer[i] < 0x80)
                {
                    i++;
                    continue;
                }
                int expectedLength = 0;
                if ((buffer[i] & 0xE0) == 0xC0) expectedLength = 1;
                else if ((buffer[i] & 0xF0) == 0xE0) expectedLength = 2;
                else if ((buffer[i] & 0xF8) == 0xF0) expectedLength = 3;
                else return false;

                if (i + expectedLength >= length)
                {
                    for (int j = 1; i + j < length; j++)
                    {
                        if ((buffer[i + j] & 0xC0) != 0x80) return false;
                    }
                    return true;
                }
                for (int j = 1; j <= expectedLength; j++)
                {
                    if ((buffer[i + j] & 0xC0) != 0x80) return false;
                }
                i += 1 + expectedLength;
            }
            return true;
        }

    }
}
