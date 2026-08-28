using System;
using System.Collections.Generic;
using System.Net;

namespace LocalDiskServer
{
    public static class Logger
    {
        public static readonly List<string> logsList = new List<string>();
        public static readonly object logsLock = new object();

        public static void Log(string message)
        {
            string timeStr = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            lock (logsLock)
            {
                logsList.Add("[" + timeStr + "] " + message);
                if (logsList.Count > 1000)
                {
                    logsList.RemoveAt(0);
                }
            }
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
