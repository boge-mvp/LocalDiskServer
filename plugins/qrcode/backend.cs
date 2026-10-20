using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace QrCodePlugin
{
    class Program
    {
        private static JavaScriptSerializer serializer = new JavaScriptSerializer();
        private static Dictionary<string, string> i18n = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static string currentLanguage = "zh-CN";
        private static string baseDir;
        private static object writeLock = new object();

        // 协议常量定义 (与 LocalDiskServer 宿主保持 100% 一致)
        private const byte T_HANDSHAKE_REQ = 0x01;
        private const byte T_HANDSHAKE_RESP = 0x02;
        private const byte T_REQUEST_HEAD = 0x03;
        private const byte T_BIN_CHUNK = 0x04;
        private const byte T_RESPONSE_HEAD = 0x05;
        private const byte T_EVENT = 0x06;

        static void Main(string[] args)
        {
            baseDir = AppDomain.CurrentDomain.BaseDirectory;
            LoadLanguage("zh-CN");

            // 1. 握手阶段 (Handshake)
            byte[] frame = ReadFrame();
            if (frame == null || frame.Length == 0 || frame[0] != T_HANDSHAKE_REQ)
            {
                return;
            }

            string hsJson = Encoding.UTF8.GetString(frame, 5, frame.Length - 5);
            Dictionary<string, object> hsReq = serializer.Deserialize<Dictionary<string, object>>(hsJson);
            
            // 宿主上下文与看门狗
            if (hsReq.ContainsKey("hostContext"))
            {
                Dictionary<string, object> hostContext = hsReq["hostContext"] as Dictionary<string, object>;
                if (hostContext != null)
                {
                    if (hostContext.ContainsKey("language") && hostContext["language"] != null)
                    {
                        LoadLanguage(hostContext["language"].ToString());
                    }
                    if (hostContext.ContainsKey("hostPid") && hostContext["hostPid"] != null)
                    {
                        int hostPid = Convert.ToInt32(hostContext["hostPid"]);
                        StartWatchdog(hostPid);
                    }
                }
            }

            // 发送握手响应
            Dictionary<string, object> hsResp = new Dictionary<string, object>();
            hsResp["protocolVersion"] = 1;
            hsResp["id"] = "qrcode";
            hsResp["status"] = "ready";
            byte[] hsRespBytes = Encoding.UTF8.GetBytes(serializer.Serialize(hsResp));
            WriteFrame(T_HANDSHAKE_RESP, hsRespBytes);

            // 2. 主事件与请求处理循环
            while (true)
            {
                frame = ReadFrame();
                if (frame == null) break; // 宿主断开标准输入，正常退出
                byte type = frame[0];

                if (type == T_EVENT)
                {
                    // 处理宿主动态广播事件 (如语言切换)
                    try
                    {
                        string eventJson = Encoding.UTF8.GetString(frame, 5, frame.Length - 5);
                        Dictionary<string, object> evt = serializer.Deserialize<Dictionary<string, object>>(eventJson);
                        if (evt != null && evt.ContainsKey("name") && evt["name"].ToString() == "language_changed")
                        {
                            if (evt.ContainsKey("language") && evt["language"] != null)
                            {
                                LoadLanguage(evt["language"].ToString());
                            }
                        }
                    }
                    catch { }
                    continue;
                }

                if (type == T_REQUEST_HEAD)
                {
                    string reqJson = Encoding.UTF8.GetString(frame, 5, frame.Length - 5);
                    Dictionary<string, object> head = serializer.Deserialize<Dictionary<string, object>>(reqJson);
                    
                    int id = Convert.ToInt32(head["id"]);
                    string path = head.ContainsKey("path") ? head["path"].ToString() : "";
                    
                    // 动态按需纠偏语言
                    if (head.ContainsKey("language") && head["language"] != null)
                    {
                        string reqLang = head["language"].ToString();
                        if (!string.Equals(reqLang, currentLanguage, StringComparison.OrdinalIgnoreCase))
                        {
                            LoadLanguage(reqLang);
                        }
                    }

                    int bodyLen = head.ContainsKey("bodyLen") ? Convert.ToInt32(head["bodyLen"]) : 0;
                    byte[] reqBody = null;
                    if (bodyLen > 0)
                    {
                        byte[] chunk = ReadFrame();
                        if (chunk != null && chunk.Length >= 9 && chunk[0] == T_BIN_CHUNK)
                        {
                            reqBody = new byte[chunk.Length - 9];
                            Array.Copy(chunk, 9, reqBody, 0, reqBody.Length);
                        }
                    }

                    // 异步派发处理，确保不阻塞下行流
                    ThreadPool.QueueUserWorkItem(delegate
                    {
                        HandleRequest(id, path, reqBody);
                    });
                }
            }
        }

        private static void HandleRequest(int id, string path, byte[] reqBody)
        {
            try
            {
                // 页面请求: subPath 为空、/ 或 /index.html
                if (string.IsNullOrEmpty(path) || path == "/" || path == "/index.html")
                {
                    string htmlPath = Path.Combine(baseDir, "index.html");
                    if (File.Exists(htmlPath))
                    {
                        string rawHtml = File.ReadAllText(htmlPath, Encoding.UTF8);
                        string renderedHtml = ReplaceTokens(rawHtml);
                        byte[] htmlBytes = Encoding.UTF8.GetBytes(renderedHtml);
                        SendResponse(id, 200, "text/html; charset=utf-8", htmlBytes);
                        return;
                    }
                }

                if (path == "/info" || path == "info")
                {
                    Dictionary<string, object> info = new Dictionary<string, object>();
                    info["id"] = "qrcode";
                    info["name"] = T("qr_title", "二维码工具箱");
                    info["status"] = "online";
                    info["version"] = "1.0.0";
                    byte[] infoBytes = Encoding.UTF8.GetBytes(serializer.Serialize(info));
                    SendResponse(id, 200, "application/json; charset=utf-8", infoBytes);
                    return;
                }

                // 未匹配路径
                byte[] notFound = Encoding.UTF8.GetBytes("{\"error\": \"Not Found\"}");
                SendResponse(id, 404, "application/json; charset=utf-8", notFound);
            }
            catch (Exception ex)
            {
                byte[] err = Encoding.UTF8.GetBytes("{\"error\": \"" + ex.Message.Replace("\"", "\\\"") + "\"}");
                SendResponse(id, 500, "application/json; charset=utf-8", err);
            }
        }

        private static void SendResponse(int id, int status, string contentType, byte[] body)
        {
            int len = body != null ? body.Length : 0;
            Dictionary<string, object> respHead = new Dictionary<string, object>();
            respHead["id"] = id;
            respHead["status"] = status;
            respHead["type"] = contentType;
            respHead["bodyLen"] = len;

            byte[] headBytes = Encoding.UTF8.GetBytes(serializer.Serialize(respHead));
            lock (writeLock)
            {
                WriteFrame(T_RESPONSE_HEAD, headBytes);
                if (len > 0)
                {
                    // 构造数据块帧：4 字节数据块 ID (大端/小端均可，与宿主保持 4 字节头一致) + 原始数据
                    byte[] chunk = new byte[4 + len];
                    chunk[0] = (byte)(id & 0xFF);
                    chunk[1] = (byte)((id >> 8) & 0xFF);
                    chunk[2] = (byte)((id >> 16) & 0xFF);
                    chunk[3] = (byte)((id >> 24) & 0xFF);
                    Array.Copy(body, 0, chunk, 4, len);
                    WriteFrame(T_BIN_CHUNK, chunk);
                }
            }
        }

        private static string ReplaceTokens(string html)
        {
            StringBuilder sb = new StringBuilder(html);
            foreach (KeyValuePair<string, string> kv in i18n)
            {
                string token = "{" + kv.Key.ToUpperInvariant() + "}";
                sb.Replace(token, kv.Value);
            }

            // 安全兜底替换关键全局标记
            sb.Replace("{NAV_HOME}", T("nav_home", "导航主页"));
            sb.Replace("{QR_TITLE}", T("qr_title", "二维码工具箱"));
            sb.Replace("{LOBBY_PROTO_TOGGLE_TITLE}", T("lobby_proto_toggle_title", "切换协议"));
            sb.Replace("{QR_GEN_TITLE}", T("qr_gen_title", "二维码生成器"));
            sb.Replace("{QR_DECODE_TITLE}", T("qr_decode_title", "二维码识别与解析"));
            sb.Replace("{QR_ECC_LABEL}", T("qr_ecc_label", "纠错等级"));
            sb.Replace("{QR_BTN_GENERATE}", T("qr_btn_generate", "生成二维码"));
            sb.Replace("{QR_INPUT_PLACEHOLDER}", T("qr_input_placeholder", "请输入要生成二维码的网址 (URL) 或任意文本内容..."));
            sb.Replace("{QR_BTN_COPY_IMG}", T("qr_btn_copy_img", "复制图片"));
            sb.Replace("{QR_BTN_DOWNLOAD}", T("qr_btn_download", "下载 PNG"));
            sb.Replace("{QR_BTN_CLEAR}", T("qr_btn_clear", "清空"));
            sb.Replace("{QR_DROPZONE_HINT}", T("qr_dropzone_hint", "拖拽二维码图片至此处"));
            sb.Replace("{QR_DROPZONE_SUB}", T("qr_dropzone_sub", "点击选择文件，或在页面任意位置直接按 Ctrl+V 粘贴截图"));
            sb.Replace("{QR_RESULT_LBL}", T("qr_result_lbl", "解析结果"));
            sb.Replace("{QR_RESULT_PLACEHOLDER}", T("qr_result_placeholder", "解析出的文本或链接将显示在这里..."));
            sb.Replace("{QR_BTN_COPY_TEXT}", T("qr_btn_copy_text", "复制文本"));
            sb.Replace("{QR_BTN_OPEN_URL}", T("qr_btn_open_url", "在浏览器中打开链接"));

            return sb.ToString();
        }

        private static string T(string key, string fallback)
        {
            if (i18n.ContainsKey(key)) return i18n[key];
            return fallback;
        }

        private static void LoadLanguage(string lang)
        {
            currentLanguage = lang;
            Dictionary<string, string> newMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            // 1. 先载入 zh-CN 兜底底座
            string fallbackPath = Path.Combine(baseDir, "lang", "zh-CN.ini");
            ParseIni(fallbackPath, newMap);

            // 2. 若目标语言不是 zh-CN，覆盖目标语言
            if (!string.Equals(lang, "zh-CN", StringComparison.OrdinalIgnoreCase))
            {
                string targetPath = Path.Combine(baseDir, "lang", lang + ".ini");
                ParseIni(targetPath, newMap);
            }

            i18n = newMap;
        }

        private static void ParseIni(string path, Dictionary<string, string> map)
        {
            if (!File.Exists(path)) return;
            string[] lines = File.ReadAllLines(path, Encoding.UTF8);
            foreach (string line in lines)
            {
                string trimmed = line.Trim();
                if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith("#") || trimmed.StartsWith(";")) continue;
                int eq = trimmed.IndexOf('=');
                if (eq > 0)
                {
                    string k = trimmed.Substring(0, eq).Trim();
                    string v = trimmed.Substring(eq + 1).Trim();
                    map[k] = v;
                }
            }
        }

        private static byte[] ReadFrame()
        {
            Stream cin = Console.OpenStandardInput();
            byte[] head = new byte[5];
            int read = 0;
            while (read < 5)
            {
                int n = cin.Read(head, read, 5 - read);
                if (n <= 0) return null; // 宿主退出或关闭输入流
                read += n;
            }

            // 小端序解析长度 (与 LocalDiskServer 宿主保持 100% 一致)
            int len = head[1] | (head[2] << 8) | (head[3] << 16) | (head[4] << 24);
            if (len < 0 || len > 64 * 1024 * 1024) return null;

            byte[] full = new byte[5 + len];
            Array.Copy(head, 0, full, 0, 5);
            read = 0;
            while (read < len)
            {
                int n = cin.Read(full, 5 + read, len - read);
                if (n <= 0) return null;
                read += n;
            }
            return full;
        }

        private static void WriteFrame(byte type, byte[] payload)
        {
            Stream cout = Console.OpenStandardOutput();
            int len = payload != null ? payload.Length : 0;
            byte[] frame = new byte[5 + len];
            frame[0] = type;
            frame[1] = (byte)(len & 0xFF);
            frame[2] = (byte)((len >> 8) & 0xFF);
            frame[3] = (byte)((len >> 16) & 0xFF);
            frame[4] = (byte)((len >> 24) & 0xFF);
            if (len > 0)
            {
                Array.Copy(payload, 0, frame, 5, len);
            }
            cout.Write(frame, 0, frame.Length);
            cout.Flush();
        }

        private static void StartWatchdog(int hostPid)
        {
            Thread t = new Thread(delegate()
            {
                try
                {
                    Process host = Process.GetProcessById(hostPid);
                    host.WaitForExit();
                }
                catch { }
                finally
                {
                    Environment.Exit(0);
                }
            });
            t.IsBackground = true;
            t.Start();
        }
    }
}
