using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Xml;
using System.Xml.Linq;

internal static class FormatterBackend
{
    private const int PROTOCOL_VERSION = 1;
    private const byte T_HANDSHAKE_REQ = 0x01;
    private const byte T_HANDSHAKE_ACK = 0x02;
    private const byte T_REQUEST_HEAD = 0x03;
    private const byte T_RESPONSE_HEAD = 0x04;
    private const byte T_BIN_CHUNK = 0x05;
    private const byte T_EVENT = 0x06;
    private const int CHUNK = 64 * 1024;

    private static readonly JavaScriptSerializer json = new JavaScriptSerializer();
    private static Dictionary<string, object> hostContext;
    private static readonly Dictionary<string, string> i18nStrings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    private static string currentLanguage = "zh-CN";

    private static int Main()
    {
        // 监护看门狗：宿主异常退出时子进程自动退出
        int parentPid = -1;
        try { parentPid = Convert.ToInt32(Environment.GetEnvironmentVariable("LDS_PARENT_PID")); }
        catch { }
        if (parentPid > 0)
        {
            Thread watchdog = new Thread(delegate()
            {
                while (true)
                {
                    Thread.Sleep(3000);
                    try { Process.GetProcessById(parentPid); }
                    catch { Environment.Exit(0); }
                }
            });
            watchdog.IsBackground = true;
            watchdog.Start();
        }

        Stream stdin = Console.OpenStandardInput();
        Stream stdout = Console.OpenStandardOutput();

        try
        {
            while (true)
            {
                byte type;
                byte[] payload;
                ReadFrame(stdin, out type, out payload);

                if (type == T_HANDSHAKE_REQ)
                {
                    hostContext = json.DeserializeObject(Encoding.UTF8.GetString(payload)) as Dictionary<string, object>;
                    if (hostContext != null && hostContext.ContainsKey("parentPid"))
                    {
                        Environment.SetEnvironmentVariable("LDS_PARENT_PID", Convert.ToString(hostContext["parentPid"]));
                        try { parentPid = Convert.ToInt32(hostContext["parentPid"]); } catch { }
                    }
                    if (hostContext != null && hostContext.ContainsKey("language") && hostContext["language"] != null)
                    {
                        currentLanguage = Convert.ToString(hostContext["language"]);
                    }
                    LoadLanguage(currentLanguage);

                    Dictionary<string, object> ack = new Dictionary<string, object>();
                    ack["protocol"] = PROTOCOL_VERSION;
                    ack["plugin"] = "formatter";
                    ack["version"] = "1.0.0";
                    WriteFrame(stdout, T_HANDSHAKE_ACK, Encoding.UTF8.GetBytes(json.Serialize(ack)));
                    Log("Formatter 插件握手完成，宿主上下文已就绪，当前语言: " + currentLanguage);
                }
                else if (type == T_REQUEST_HEAD)
                {
                    HandleRequest(stdin, stdout, payload);
                }
                else if (type == T_EVENT)
                {
                    try
                    {
                        Dictionary<string, object> evt = json.DeserializeObject(Encoding.UTF8.GetString(payload)) as Dictionary<string, object>;
                        if (evt != null && evt.ContainsKey("language") && evt["language"] != null)
                        {
                            string newLang = Convert.ToString(evt["language"]);
                            if (!string.IsNullOrEmpty(newLang) && !string.Equals(newLang, currentLanguage, StringComparison.OrdinalIgnoreCase))
                            {
                                currentLanguage = newLang;
                                LoadLanguage(currentLanguage);
                                Log("Formatter 插件已切换语言至: " + currentLanguage);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Log("处理 EVENT 异常: " + ex.Message);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Log("Formatter 插件异常退出: " + ex.Message);
            return 1;
        }
    }

    private static void LoadLanguage(string lang)
    {
        try
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string zhFile = Path.Combine(baseDir, "lang\\zh-CN.ini");
            string targetFile = Path.Combine(baseDir, "lang\\" + lang + ".ini");

            Dictionary<string, string> dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            if (File.Exists(zhFile))
            {
                foreach (string l in File.ReadAllLines(zhFile, Encoding.UTF8))
                {
                    string t = l.Trim();
                    if (string.IsNullOrEmpty(t) || t.StartsWith("#") || t.StartsWith(";")) continue;
                    int eq = t.IndexOf('=');
                    if (eq > 0)
                    {
                        dict[t.Substring(0, eq).Trim()] = t.Substring(eq + 1).Trim().Replace("\\n", "\n").Replace("\\t", "\t");
                    }
                }
            }

            if (!string.Equals(lang, "zh-CN", StringComparison.OrdinalIgnoreCase) && File.Exists(targetFile))
            {
                foreach (string l in File.ReadAllLines(targetFile, Encoding.UTF8))
                {
                    string t = l.Trim();
                    if (string.IsNullOrEmpty(t) || t.StartsWith("#") || t.StartsWith(";")) continue;
                    int eq = t.IndexOf('=');
                    if (eq > 0)
                    {
                        dict[t.Substring(0, eq).Trim()] = t.Substring(eq + 1).Trim().Replace("\\n", "\n").Replace("\\t", "\t");
                    }
                }
            }

            lock (i18nStrings)
            {
                i18nStrings.Clear();
                foreach (KeyValuePair<string, string> kvp in dict)
                {
                    i18nStrings[kvp.Key] = kvp.Value;
                }
            }
        }
        catch { }
    }

    private static string T(string key, params object[] args)
    {
        string val;
        lock (i18nStrings)
        {
            if (!i18nStrings.TryGetValue(key, out val)) val = key;
        }
        if (args != null && args.Length > 0)
        {
            try { return string.Format(val, args); } catch { return val; }
        }
        return val;
    }

    private static void HandleRequest(Stream stdin, Stream stdout, byte[] payload)
    {
        Dictionary<string, object> head = json.DeserializeObject(Encoding.UTF8.GetString(payload)) as Dictionary<string, object>;
        int id = Convert.ToInt32(head["id"]);
        string method = Convert.ToString(head["method"]);
        string path = head.ContainsKey("path") ? Convert.ToString(head["path"]).TrimStart('/') : "";
        long bodyLen = head.ContainsKey("bodyLen") ? Convert.ToInt64(head["bodyLen"]) : 0;

        if (head.ContainsKey("language") && head["language"] != null)
        {
            string reqLang = Convert.ToString(head["language"]);
            if (!string.IsNullOrEmpty(reqLang) && !string.Equals(reqLang, currentLanguage, StringComparison.OrdinalIgnoreCase))
            {
                currentLanguage = reqLang;
                LoadLanguage(currentLanguage);
            }
        }

        byte[] reqBody = new byte[0];
        if (bodyLen > 0)
        {
            using (MemoryStream ms = new MemoryStream())
            {
                long received = 0;
                while (received < bodyLen)
                {
                    byte type;
                    byte[] chunk;
                    ReadFrame(stdin, out type, out chunk);
                    if (type != T_BIN_CHUNK || chunk.Length < 4) continue;
                    int chunkId = chunk[0] | (chunk[1] << 8) | (chunk[2] << 16) | (chunk[3] << 24);
                    if (chunkId != id) continue;
                    int dataLen = chunk.Length - 4;
                    ms.Write(chunk, 4, dataLen);
                    received += dataLen;
                }
                reqBody = ms.ToArray();
            }
        }

        int status = 200;
        string contentType = "application/json; charset=utf-8";
        byte[] respBody = null;

        try
        {
            if (string.IsNullOrEmpty(path))
            {
                contentType = "text/html; charset=utf-8";
                respBody = GetPageBytes();
            }
            else if (path.Equals("api/info", StringComparison.OrdinalIgnoreCase) || path.Equals("info", StringComparison.OrdinalIgnoreCase))
            {
                respBody = GetInfoBytes();
            }
            else if (path.Equals("api/format", StringComparison.OrdinalIgnoreCase) || path.Equals("format", StringComparison.OrdinalIgnoreCase))
            {
                respBody = GetFormatBytes(reqBody);
            }
            else
            {
                status = 404;
                respBody = Encoding.UTF8.GetBytes("{\"success\":false,\"error\":\"未找到请求的端点: " + EscapeJson(path) + "\"}");
            }
        }
        catch (Exception ex)
        {
            status = 500;
            respBody = Encoding.UTF8.GetBytes("{\"success\":false,\"error\":\"处理请求异常: " + EscapeJson(ex.Message) + "\"}");
        }

        Dictionary<string, object> respHead = new Dictionary<string, object>();
        respHead["id"] = id;
        respHead["status"] = status;
        respHead["type"] = contentType;
        respHead["bodyLen"] = respBody == null ? 0 : respBody.Length;
        WriteFrame(stdout, T_RESPONSE_HEAD, Encoding.UTF8.GetBytes(json.Serialize(respHead)));

        if (respBody != null && respBody.Length > 0)
        {
            int off = 0;
            while (off < respBody.Length)
            {
                int n = Math.Min(CHUNK, respBody.Length - off);
                byte[] frame = new byte[4 + n];
                frame[0] = (byte)(id & 0xFF);
                frame[1] = (byte)((id >> 8) & 0xFF);
                frame[2] = (byte)((id >> 16) & 0xFF);
                frame[3] = (byte)((id >> 24) & 0xFF);
                Array.Copy(respBody, off, frame, 4, n);
                WriteFrame(stdout, T_BIN_CHUNK, frame);
                off += n;
            }
        }
    }

    private static byte[] GetPageBytes()
    {
        string indexPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "index.html");
        string html = "";
        if (File.Exists(indexPath))
        {
            html = File.ReadAllText(indexPath, Encoding.UTF8);
        }
        else
        {
            html = "<div>index.html not found</div>";
        }

        lock (i18nStrings)
        {
            foreach (KeyValuePair<string, string> kvp in i18nStrings)
            {
                string token = "{" + kvp.Key.ToUpperInvariant() + "}";
                html = html.Replace(token, kvp.Value);
            }
        }

        html = html.Replace("{NAV_HOME}", i18nStrings.ContainsKey("nav_home") ? i18nStrings["nav_home"] : (currentLanguage.StartsWith("en") ? "Home" : "导航主页"));
        html = html.Replace("{LOBBY_PROTO_TOGGLE_TITLE}", i18nStrings.ContainsKey("lobby_proto_toggle_title") ? i18nStrings["lobby_proto_toggle_title"] : (currentLanguage.StartsWith("en") ? "Toggle Protocol" : "切换协议"));
        html = html.Replace("{OPT_UNESCAPE_NEWLINES}", i18nStrings.ContainsKey("opt_unescape_newlines") ? i18nStrings["opt_unescape_newlines"] : (currentLanguage.StartsWith("en") ? "Unescape \\n" : "解析换行"));
        html = html.Replace("{OPT_UNESCAPE_NEWLINES_TITLE}", i18nStrings.ContainsKey("opt_unescape_newlines_title") ? i18nStrings["opt_unescape_newlines_title"] : (currentLanguage.StartsWith("en") ? "Automatically parse linebreaks inside strings and render as multiline text" : "自动解析字符串内的换行符并在输出中展开为真实多行排版"));
        html = html.Replace("{VIEW_MODE_SINGLE}", i18nStrings.ContainsKey("view_mode_single") ? i18nStrings["view_mode_single"] : (currentLanguage.StartsWith("en") ? "Single" : "单屏"));
        html = html.Replace("{VIEW_MODE_SPLIT}", i18nStrings.ContainsKey("view_mode_split") ? i18nStrings["view_mode_split"] : (currentLanguage.StartsWith("en") ? "Split" : "分屏"));
        html = html.Replace("{TOOLTIP_VIEW_MODE_SINGLE}", i18nStrings.ContainsKey("tooltip_view_mode_single") ? i18nStrings["tooltip_view_mode_single"] : (currentLanguage.StartsWith("en") ? "Single pane mode (format in-place)" : "单屏模式（原输入框就地格式化）"));
        html = html.Replace("{TOOLTIP_VIEW_MODE_SPLIT}", i18nStrings.ContainsKey("tooltip_view_mode_split") ? i18nStrings["tooltip_view_mode_split"] : (currentLanguage.StartsWith("en") ? "Split view (side-by-side input and output)" : "分屏模式（左右双栏对照）"));
        html = html.Replace("{STATUS_ALREADY_FORMATTED}", i18nStrings.ContainsKey("status_already_formatted") ? i18nStrings["status_already_formatted"] : (currentLanguage.StartsWith("en") ? "✓ Already formatted" : "✓ 已是最新格式化内容"));
        html = html.Replace("{TOOLTIP_ALREADY_FORMATTED}", i18nStrings.ContainsKey("tooltip_already_formatted") ? i18nStrings["tooltip_already_formatted"] : (currentLanguage.StartsWith("en") ? "Content is already formatted" : "当前内容已是最新格式化状态"));
        html = html.Replace("{TAB_QRCODE}", i18nStrings.ContainsKey("tab_qrcode") ? i18nStrings["tab_qrcode"] : (currentLanguage.StartsWith("en") ? "QR Code" : "二维码工具"));
        html = html.Replace("{TOOLTIP_VIEW_MODE_TOGGLE}", i18nStrings.ContainsKey("tooltip_view_mode_toggle") ? i18nStrings["tooltip_view_mode_toggle"] : (currentLanguage.StartsWith("en") ? "Toggle Single / Split View" : "切换单屏 / 分屏模式"));

        return Encoding.UTF8.GetBytes(html);
    }

    private static byte[] GetInfoBytes()
    {
        Dictionary<string, object> info = new Dictionary<string, object>();
        info["plugin"] = "formatter";
        info["version"] = "1.0.0";
        info["language"] = currentLanguage;
        info["supportedModes"] = new string[] { "json", "xml", "sql", "codecs", "text" };
        return Encoding.UTF8.GetBytes(json.Serialize(info));
    }

    private static byte[] GetFormatBytes(byte[] reqBody)
    {
        string bodyStr = Encoding.UTF8.GetString(reqBody);
        Dictionary<string, object> req = json.DeserializeObject(bodyStr) as Dictionary<string, object>;
        string type = req != null && req.ContainsKey("type") ? Convert.ToString(req["type"]) : "json";
        string action = req != null && req.ContainsKey("action") ? Convert.ToString(req["action"]) : "format";
        string input = "";
        if (req != null)
        {
            if (req.ContainsKey("input") && req["input"] != null) input = Convert.ToString(req["input"]);
            else if (req.ContainsKey("content") && req["content"] != null) input = Convert.ToString(req["content"]);
        }

        Dictionary<string, object> res = new Dictionary<string, object>();
        res["success"] = true;

        try
        {
            if (type.Equals("xml", StringComparison.OrdinalIgnoreCase))
            {
                if (action.Equals("minify", StringComparison.OrdinalIgnoreCase))
                {
                    XDocument doc = XDocument.Parse(input);
                    res["output"] = doc.ToString(SaveOptions.DisableFormatting);
                }
                else
                {
                    XDocument doc = XDocument.Parse(input);
                    res["output"] = doc.ToString();
                }
            }
            else if (type.Equals("sql", StringComparison.OrdinalIgnoreCase))
            {
                // 简易通用关键字大写与换行
                string[] keywords = new string[] { "SELECT", "FROM", "WHERE", "GROUP BY", "HAVING", "ORDER BY", "LIMIT", "OFFSET", "LEFT JOIN", "RIGHT JOIN", "INNER JOIN", "JOIN", "ON", "AND", "OR", "VALUES", "INSERT INTO", "UPDATE", "SET", "DELETE FROM" };
                string formatted = input;
                foreach (string kw in keywords)
                {
                    string pattern = "\\b" + kw.Replace(" ", "\\s+") + "\\b";
                    formatted = System.Text.RegularExpressions.Regex.Replace(formatted, pattern, "\n" + kw, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                }
                res["output"] = formatted.Trim();
            }
            else if (type.IndexOf("base64", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                if (type.IndexOf("encode", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    res["output"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(input));
                }
                else
                {
                    res["output"] = Encoding.UTF8.GetString(Convert.FromBase64String(input));
                }
            }
            else if (type.IndexOf("text-upper", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                res["output"] = input.ToUpperInvariant();
            }
            else if (type.IndexOf("text-lower", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                res["output"] = input.ToLowerInvariant();
            }
            else
            {
                bool deepParse = req != null && req.ContainsKey("deepParse") && Convert.ToBoolean(req["deepParse"]);
                bool unescapeNewlines = req != null && req.ContainsKey("unescapeNewlines") && Convert.ToBoolean(req["unescapeNewlines"]);
                string sanitizedInput = SanitizeJsonInput(input);
                object obj = json.DeserializeObject(sanitizedInput);
                if (deepParse)
                {
                    obj = DeepParseNestedJson(obj, 0);
                }
                string outJson = json.Serialize(obj);
                if (unescapeNewlines)
                {
                    outJson = ExpandJsonNewlines(outJson);
                }
                res["output"] = outJson;
            }
            res["result"] = res["output"];
        }
        catch (Exception ex)
        {
            res["success"] = false;
            res["error"] = ex.Message;
        }

        return Encoding.UTF8.GetBytes(json.Serialize(res));
    }

    private static string SanitizeJsonInput(string str)
    {
        if (string.IsNullOrEmpty(str)) return str;
        StringBuilder sb = new StringBuilder(str.Length + 64);
        bool inStr = false;
        char quoteChar = '\0';
        bool isEscaped = false;

        for (int i = 0; i < str.Length; i++)
        {
            char c = str[i];
            if (inStr)
            {
                if (isEscaped)
                {
                    sb.Append(c);
                    isEscaped = false;
                }
                else if (c == '\\')
                {
                    sb.Append(c);
                    isEscaped = true;
                }
                else if (c == quoteChar)
                {
                    sb.Append(c);
                    inStr = false;
                }
                else if (c == '\r')
                {
                    if (i + 1 < str.Length && str[i + 1] == '\n') i++;
                    sb.Append("\\n");
                }
                else if (c == '\n')
                {
                    sb.Append("\\n");
                }
                else
                {
                    sb.Append(c);
                }
            }
            else
            {
                if (c == '"' || c == '\'')
                {
                    inStr = true;
                    quoteChar = c;
                    sb.Append(c);
                }
                else
                {
                    sb.Append(c);
                }
            }
        }
        return sb.ToString();
    }

    private static string ExpandJsonNewlines(string jsonStr)
    {
        if (string.IsNullOrEmpty(jsonStr)) return jsonStr;
        StringBuilder sb = new StringBuilder(jsonStr.Length + 128);
        bool inStr = false;
        bool isEsc = false;

        for (int i = 0; i < jsonStr.Length; i++)
        {
            char c = jsonStr[i];
            if (!inStr)
            {
                sb.Append(c);
                if (c == '"')
                {
                    inStr = true;
                }
            }
            else
            {
                if (isEsc)
                {
                    if (c == 'n')
                    {
                        sb.Length--;
                        sb.Append("\n  ");
                    }
                    else if (c == 'r')
                    {
                        sb.Length--;
                    }
                    else if (c == 't')
                    {
                        sb.Length--;
                        sb.Append("\t");
                    }
                    else
                    {
                        sb.Append(c);
                    }
                    isEsc = false;
                }
                else if (c == '\\')
                {
                    isEsc = true;
                    sb.Append(c);
                }
                else if (c == '"')
                {
                    inStr = false;
                    sb.Append(c);
                }
                else
                {
                    sb.Append(c);
                }
            }
        }
        return sb.ToString();
    }

    private static object DeepParseNestedJson(object val, int depth)
    {
        if (depth > 20 || val == null) return val;

        string str = val as string;
        if (str != null)
        {
            string s = str.Trim();
            if ((s.StartsWith("{") && s.EndsWith("}")) || (s.StartsWith("[") && s.EndsWith("]")))
            {
                try
                {
                    object parsed = json.DeserializeObject(s);
                    if (parsed is Dictionary<string, object> || parsed is System.Collections.IEnumerable)
                    {
                        return DeepParseNestedJson(parsed, depth + 1);
                    }
                }
                catch
                {
                    // 保持原字符串
                }
            }
            return val;
        }

        Dictionary<string, object> dict = val as Dictionary<string, object>;
        if (dict != null)
        {
            Dictionary<string, object> newDict = new Dictionary<string, object>();
            foreach (KeyValuePair<string, object> kvp in dict)
            {
                newDict[kvp.Key] = DeepParseNestedJson(kvp.Value, depth + 1);
            }
            return newDict;
        }

        System.Collections.IEnumerable list = val as System.Collections.IEnumerable;
        if (list != null)
        {
            List<object> newList = new List<object>();
            foreach (object item in list)
            {
                newList.Add(DeepParseNestedJson(item, depth + 1));
            }
            return newList;
        }

        return val;
    }

    private static string EscapeJson(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");
    }

    private static void ReadFrame(Stream stream, out byte type, out byte[] payload)
    {
        byte[] head = ReadExact(stream, 5);
        type = head[0];
        int len = head[1] | (head[2] << 8) | (head[3] << 16) | (head[4] << 24);
        if (len < 0 || len > 16 * 1024 * 1024) throw new InvalidOperationException("帧负载长度非法: " + len);
        payload = len == 0 ? new byte[0] : ReadExact(stream, len);
    }

    private static void WriteFrame(Stream stream, byte type, byte[] payload)
    {
        int len = payload != null ? payload.Length : 0;
        byte[] head = new byte[5];
        head[0] = type;
        head[1] = (byte)(len & 0xFF);
        head[2] = (byte)((len >> 8) & 0xFF);
        head[3] = (byte)((len >> 16) & 0xFF);
        head[4] = (byte)((len >> 24) & 0xFF);

        lock (stream)
        {
            stream.Write(head, 0, 5);
            if (len > 0)
            {
                stream.Write(payload, 0, len);
            }
            stream.Flush();
        }
    }

    private static byte[] ReadExact(Stream stream, int count)
    {
        if (count == 0) return new byte[0];
        byte[] buf = new byte[count];
        int read = 0;
        while (read < count)
        {
            int n = stream.Read(buf, read, count - read);
            if (n <= 0) throw new EndOfStreamException();
            read += n;
        }
        return buf;
    }

    private static void Log(string msg)
    {
        try
        {
            Console.Error.WriteLine("[formatter] " + DateTime.Now.ToString("HH:mm:ss.fff") + " " + msg);
        }
        catch { }
    }
}
