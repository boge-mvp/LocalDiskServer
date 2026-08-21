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
            return false;
        }
    }
}
