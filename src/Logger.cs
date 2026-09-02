using System;
using System.Collections.Generic;
using System.Net;

namespace LocalDiskServer
{
    public static class Logger
    {
        public static readonly List<string> logsList = new List<string>();
        public static readonly object logsLock = new object();

        private static readonly string diskLogDir = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");

        static Logger()
        {
            // 启动时清理 7 天前的旧磁盘日志（整体兜底，绝不影响启动）
            try
            {
                if (System.IO.Directory.Exists(diskLogDir))
                {
                    DateTime threshold = DateTime.Now.Date.AddDays(-7);
                    foreach (string file in System.IO.Directory.GetFiles(diskLogDir, "server_*.log"))
                    {
                        string name = System.IO.Path.GetFileNameWithoutExtension(file);
                        DateTime fileDate;
                        if (name.Length == 15 &&
                            DateTime.TryParseExact(name.Substring(7), "yyyyMMdd",
                                System.Globalization.CultureInfo.InvariantCulture,
                                System.Globalization.DateTimeStyles.None, out fileDate) &&
                            fileDate < threshold)
                        {
                            try { System.IO.File.Delete(file); } catch { }
                        }
                    }
                }
            }
            catch { }
        }

        public static void Log(string message)
        {
            string timeStr = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            string line = "[" + timeStr + "] " + message;
            lock (logsLock)
            {
                logsList.Add(line);
                if (logsList.Count > 1000)
                {
                    logsList.RemoveAt(0);
                }
            }
            AppendDiskLog(line);
        }

        private static void AppendDiskLog(string line)
        {
            try
            {
                if (!System.IO.Directory.Exists(diskLogDir))
                {
                    System.IO.Directory.CreateDirectory(diskLogDir);
                }
                string filePath = System.IO.Path.Combine(diskLogDir, "server_" + DateTime.Now.ToString("yyyyMMdd") + ".log");
                // 与内存日志同一把锁序列化并发写者，避免多线程同时 AppendAllText 抛文件占用异常
                lock (logsLock)
                {
                    System.IO.File.AppendAllText(filePath, line + "\r\n", System.Text.Encoding.UTF8);
                }
            }
            catch { }
        }

        public static bool HandleApi(string rawPath, HttpListenerRequest request, HttpListenerResponse response)
        {
            if (rawPath.Equals("api/logs", StringComparison.OrdinalIgnoreCase))
            {
                System.Text.StringBuilder json = new System.Text.StringBuilder();
                json.Append("{\"success\":true,\"logs\":[");
                lock (logsLock)
                {
                    for (int i = 0; i < logsList.Count; i++)
                    {
                        json.Append("\"" + HttpServer.EscapeJson(logsList[i]) + "\"");
                        if (i < logsList.Count - 1) json.Append(",");
                    }
                }
                json.Append("]}");
                HttpServer.ServeJson(response, 200, json.ToString());
                return true;
            }
            else if (rawPath.Equals("api/logs/clear", StringComparison.OrdinalIgnoreCase))
            {
                lock (logsLock)
                {
                    logsList.Clear();
                }
                Log(I18nManager.T("log_cleared"));
                HttpServer.ServeJson(response, 200, "{\"success\":true}");
                return true;
            }
            else if (rawPath.Equals("api/logs/client-error", StringComparison.OrdinalIgnoreCase) && request.HttpMethod == "POST")
            {
                try
                {
                    string body = "";
                    using (System.IO.StreamReader reader = new System.IO.StreamReader(request.InputStream, request.ContentEncoding ?? System.Text.Encoding.UTF8))
                    {
                        body = reader.ReadToEnd();
                    }

                    string errorMsg = "";
                    string source = "";
                    string line = "";
                    string col = "";

                    Dictionary<string, string> pairs = ServerApplicationContext.ExtractSimpleJsonPairs(body);
                    if (pairs.ContainsKey("message")) errorMsg = pairs["message"];
                    if (pairs.ContainsKey("source")) source = pairs["source"];
                    if (pairs.ContainsKey("lineno")) line = pairs["lineno"];
                    if (pairs.ContainsKey("colno")) col = pairs["colno"];

                    string location = "";
                    if (!string.IsNullOrEmpty(source))
                    {
                        int lastSlash = Math.Max(source.LastIndexOf('/'), source.LastIndexOf('\\'));
                        string fileName = lastSlash >= 0 ? source.Substring(lastSlash + 1) : source;
                        location = fileName;
                        if (!string.IsNullOrEmpty(line))
                        {
                            location += ":" + line;
                            if (!string.IsNullOrEmpty(col)) location += ":" + col;
                        }
                    }

                    string formattedMsg = "";
                    if (!string.IsNullOrEmpty(location))
                    {
                        formattedMsg = location + ": " + errorMsg;
                    }
                    else
                    {
                        formattedMsg = errorMsg;
                    }

                    if (!string.IsNullOrEmpty(formattedMsg))
                    {
                        Log(I18nManager.T("log_client_error", formattedMsg));
                    }

                    HttpServer.ServeJson(response, 200, "{\"success\":true}");
                    return true;
                }
                catch (Exception ex)
                {
                    HttpServer.ServeJson(response, 500, "{\"success\":false,\"error\":\"" + HttpServer.EscapeJson(ex.Message) + "\"}");
                    return true;
                }
            }
            return false;
        }
    }
}
