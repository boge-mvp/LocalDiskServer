using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace LocalDiskServer
{
    // ─────────────────────────────────────────────────────────────
    // 插件宿主：进程隔离插件系统（v5 全对称字节帧协议）
    //
    // 协议帧格式: [type(1B)][len(4B LE)][payload(len B)]
    //   0x01 HANDSHAKE_REQ  主→子  JSON 宿主上下文（含 protocol 版本）
    //   0x02 HANDSHAKE_ACK  子→主  JSON {protocol, plugin, version}
    //   0x03 REQUEST_HEAD   主→子  JSON {id, method, path, query, headers, bodyLen}
    //   0x04 RESPONSE_HEAD  子→主  JSON {id, status, type, bodyLen}
    //   0x05 BIN_CHUNK      双向   [id(4B LE)][raw bytes] 上/下行 body 唯一通道
    //   0x06 EVENT          子→主  JSON {name, data}
    //   0x07 FATAL          子→主  JSON {code, message}
    // stdout 仅承载协议帧；日志走 stderr（加 [plugin:id] 前缀并入主日志）
    // ─────────────────────────────────────────────────────────────
    public static class PluginHost
    {
        private const int PROTOCOL_VERSION = 1;
        private const int MAX_FRAME = 16 * 1024 * 1024;
        private const int HANDSHAKE_TIMEOUT_MS = 5000;
        private const int CHUNK = 64 * 1024;
        private const int MAX_RESTARTS = 3;

        private const byte T_HANDSHAKE_REQ = 0x01;
        private const byte T_HANDSHAKE_ACK = 0x02;
        private const byte T_REQUEST_HEAD = 0x03;
        private const byte T_RESPONSE_HEAD = 0x04;
        private const byte T_BIN_CHUNK = 0x05;
        private const byte T_EVENT = 0x06;
        private const byte T_FATAL = 0x07;

        private static readonly List<PluginInfo> plugins = new List<PluginInfo>();
        private static readonly Dictionary<string, PluginRuntime> runtimes = new Dictionary<string, PluginRuntime>(StringComparer.OrdinalIgnoreCase);
        private static readonly object initLock = new object();
        private static bool initialized = false;
        private static readonly JavaScriptSerializer json = new JavaScriptSerializer();
        private static string pluginsRoot;

        // ─────────────────────────────────────────────────────────
        // 清单与初始化
        // ─────────────────────────────────────────────────────────
        private class PluginInfo
        {
            public string Id;
            public string Name;
            public string Version;
            public string Description;
            public string Icon;
            public int Order;
            public int TimeoutSec = 10;
            public int Concurrency = 4;
            public bool Warmup;
            public string DirPath;
            public string ExePath;
            public string IndexHtmlPath;
        }

        public static void Initialize()
        {
            lock (initLock)
            {
                if (initialized) return;
                initialized = true;
                pluginsRoot = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "plugins");

                if (!Directory.Exists(pluginsRoot))
                {
                    return;
                }

                string[] dirs = Directory.GetDirectories(pluginsRoot);
                foreach (string dir in dirs)
                {
                    try
                    {
                        PluginInfo info = LoadManifest(dir);
                        if (info == null) continue;
                        plugins.Add(info);
                        PluginRuntime rt = new PluginRuntime(info);
                        if (ServerApplicationContext.IsPluginDisabled(info.Id))
                        {
                            rt.Disabled = true;
                            rt.DisabledReason = "disabled_by_user";
                        }
                        runtimes[info.Id] = rt;
                        LoadPluginStrings(info);
                    }
                    catch (Exception ex)
                    {
                        Logger.Log(I18nManager.T("log_plugin_load_failed", Path.GetFileName(dir), ex.Message));
                    }
                }

                plugins.Sort((a, b) => a.Order.CompareTo(b.Order));

                foreach (PluginInfo p in plugins)
                {
                    PluginRuntime rt = runtimes[p.Id];
                    if (rt.Disabled)
                    {
                        Logger.Log(I18nManager.T("log_plugin_loaded_disabled", p.Name, p.Version));
                        continue;
                    }

                    Logger.Log(I18nManager.T("log_plugin_loaded", p.Name, p.Version));
                    if (p.Warmup)
                    {
                        ThreadPool.QueueUserWorkItem(delegate(object state)
                        {
                            PluginRuntime stateRt = (PluginRuntime)state;
                            try { EnsureRunning(stateRt); }
                            catch (Exception ex) { Logger.Log(I18nManager.T("log_plugin_request_failed", stateRt.Info.Id, ex.Message)); }
                        }, rt);
                    }
                }
            }
        }

        private static PluginInfo LoadManifest(string dir)
        {
            string manifest = Path.Combine(dir, "plugin.json");
            if (!File.Exists(manifest))
            {
                Logger.Log(I18nManager.T("log_plugin_load_failed", Path.GetFileName(dir), "plugin.json missing"));
                return null;
            }

            Dictionary<string, object> m = json.DeserializeObject(File.ReadAllText(manifest, Encoding.UTF8)) as Dictionary<string, object>;
            if (m == null) return null;

            PluginInfo info = new PluginInfo();
            info.DirPath = dir;
            info.Id = GetStr(m, "id");
            info.Name = GetStr(m, "name");
            info.Version = GetStr(m, "version");
            info.Description = GetStr(m, "description");
            info.Icon = GetStr(m, "icon");
            if (string.IsNullOrEmpty(info.Icon)) info.Icon = "🧩";
            info.Order = GetInt(m, "order", 100);
            info.TimeoutSec = GetInt(m, "timeout", 10);
            info.Concurrency = GetInt(m, "concurrency", 4);
            if (info.Concurrency < 1) info.Concurrency = 1;
            info.Warmup = GetBool(m, "warmup");

            if (string.IsNullOrEmpty(info.Id) || string.IsNullOrEmpty(info.Name))
            {
                Logger.Log(I18nManager.T("log_plugin_load_failed", Path.GetFileName(dir), "id/name missing"));
                return null;
            }

            if (!System.Text.RegularExpressions.Regex.IsMatch(info.Id, "^[a-z0-9][a-z0-9-]{1,30}$"))
            {
                Logger.Log(I18nManager.T("log_plugin_load_failed", info.Id, "invalid id"));
                return null;
            }

            string[] reserved = new string[] { "gradle", "npm", "pnpm", "maven" };
            foreach (string r in reserved)
            {
                if (string.Equals(info.Id, r, StringComparison.OrdinalIgnoreCase))
                {
                    Logger.Log(I18nManager.T("log_plugin_load_failed", info.Id, "reserved id"));
                    return null;
                }
            }

            info.ExePath = Path.Combine(dir, "backend.exe");
            if (!File.Exists(info.ExePath))
            {
                Logger.Log(I18nManager.T("log_plugin_load_failed", info.Id, "backend.exe missing"));
                return null;
            }

            string indexHtml = Path.Combine(dir, "index.html");
            if (File.Exists(indexHtml)) info.IndexHtmlPath = indexHtml;

            return info;
        }

        private static void LoadPluginStrings(PluginInfo info)
        {
            string langDir = Path.Combine(info.DirPath, "lang");
            if (!Directory.Exists(langDir)) return;
            string prefix = "plugin_" + info.Id + "_";
            string[] files = Directory.GetFiles(langDir, "*.ini");
            foreach (string file in files)
            {
                string langCode = Path.GetFileNameWithoutExtension(file);
                Dictionary<string, string> dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                ParsePluginIni(File.ReadAllText(file, Encoding.UTF8), dict);
                I18nManager.RegisterStrings(langCode, dict, prefix);
            }
        }

        private static void ParsePluginIni(string content, Dictionary<string, string> target)
        {
            using (StringReader sr = new StringReader(content))
            {
                string line;
                while ((line = sr.ReadLine()) != null)
                {
                    string trimmed = line.Trim();
                    if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith("#") || trimmed.StartsWith(";")) continue;
                    int idx = trimmed.IndexOf('=');
                    if (idx > 0)
                    {
                        target[trimmed.Substring(0, idx).Trim()] = trimmed.Substring(idx + 1).Trim();
                    }
                }
            }
        }

        // ─────────────────────────────────────────────────────────
        // 进程生命周期
        // ─────────────────────────────────────────────────────────
        private class PendingRequest
        {
            public int Id;
            public int Status;
            public string ContentType;
            public long BodyLen;
            public long Received;
            public MemoryStream Body = new MemoryStream();
            public ManualResetEvent Done = new ManualResetEvent(false);
            public string Error;
        }

        private class PluginRuntime
        {
            public PluginRuntime(PluginInfo info) { Info = info; }
            public PluginInfo Info;
            public Process Proc;
            public readonly object WriteLock = new object();
            public readonly Dictionary<int, PendingRequest> Pending = new Dictionary<int, PendingRequest>();
            public readonly object PendingLock = new object();
            public SemaphoreSlim Gate;
            public int NextId = 1;
            public int Restarts;
            public bool Disabled;
            public string DisabledReason;
        }

        private static PluginRuntime EnsureRunning(PluginRuntime rt)
        {
            lock (rt)
            {
                if (rt.Disabled) throw new InvalidOperationException(I18nManager.T("plugin_unavailable", rt.Info.Id));
                if (rt.Proc != null && !rt.Proc.HasExited) return rt;
                if (rt.Proc != null) { try { rt.Proc.Dispose(); } catch { } rt.Proc = null; }
                if (rt.Restarts > MAX_RESTARTS && rt.DisabledReason == null)
                {
                    rt.Disabled = true;
                    rt.DisabledReason = "too many restarts";
                    throw new InvalidOperationException(I18nManager.T("plugin_unavailable", rt.Info.Id));
                }
            }

            ProcessStartInfo psi = new ProcessStartInfo();
            psi.FileName = rt.Info.ExePath;
            psi.Arguments = "";
            psi.WorkingDirectory = rt.Info.DirPath;
            psi.UseShellExecute = false;
            psi.CreateNoWindow = true;
            psi.EnvironmentVariables["LDS_PARENT_PID"] = Convert.ToString(Process.GetCurrentProcess().Id);
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardInput = true;
            psi.RedirectStandardError = true;

            Process proc = new Process();
            proc.StartInfo = psi;
            proc.ErrorDataReceived += delegate(object sender, DataReceivedEventArgs e)
            {
                if (!string.IsNullOrEmpty(e.Data)) Logger.Log("[plugin:" + rt.Info.Id + "] " + e.Data);
            };

            if (!proc.Start()) throw new InvalidOperationException("failed to start backend.exe");
            proc.BeginErrorReadLine();

            lock (rt)
            {
                rt.Proc = proc;
                if (rt.Gate == null) rt.Gate = new SemaphoreSlim(rt.Info.Concurrency);
            }

            Thread reader = new Thread(delegate() { ReadLoop(rt, proc); });
            reader.IsBackground = true;
            reader.Name = "plugin-reader-" + rt.Info.Id;
            reader.Start();

            // 握手: 发送宿主上下文并等待 ACK
            Dictionary<string, object> hello = new Dictionary<string, object>();
            hello["protocol"] = PROTOCOL_VERSION;
            hello["pluginDir"] = rt.Info.DirPath;
            hello["parentPid"] = Process.GetCurrentProcess().Id;
            hello["language"] = I18nManager.CurrentLanguage;
            hello["httpPort"] = ServerApplicationContext.port;
            hello["httpsPort"] = ServerApplicationContext.https_port;
            hello["useHttps"] = ServerApplicationContext.use_https;
            hello["enableDevEcosystem"] = ServerApplicationContext.enable_dev_ecosystem;
            hello["appVersion"] = ServerApplicationContext.APP_VERSION;
            SendFrame(rt, T_HANDSHAKE_REQ, Encoding.UTF8.GetBytes(json.Serialize(hello)));

            lock (rt.PendingLock)
            {
                // 复用 Pending[0] 作为握手等待槽位
                PendingRequest hs = new PendingRequest();
                hs.Id = 0;
                rt.Pending[0] = hs;
            }

            PendingRequest slot;
            lock (rt.PendingLock) { slot = rt.Pending[0]; }
            if (!slot.Done.WaitOne(HANDSHAKE_TIMEOUT_MS))
            {
                KillRuntime(rt, "handshake timeout");
                throw new InvalidOperationException(I18nManager.T("plugin_unavailable", rt.Info.Id));
            }
            if (slot.Error != null)
            {
                KillRuntime(rt, slot.Error);
                throw new InvalidOperationException(I18nManager.T("plugin_unavailable", rt.Info.Id));
            }
            lock (rt.PendingLock) { rt.Pending.Remove(0); }

            lock (rt)
            {
                rt.Restarts = 0;
            }
            return rt;
        }

        private static void KillRuntime(PluginRuntime rt, string reason)
        {
            Logger.Log(I18nManager.T("log_plugin_unloaded", rt.Info.Id) + " (" + reason + ")");
            lock (rt)
            {
                rt.Restarts++;
                if (rt.Restarts > MAX_RESTARTS && !rt.Disabled)
                {
                    rt.Disabled = true;
                    rt.DisabledReason = reason;
                }
            }
            FailAllPending(rt, reason);
            try { if (rt.Proc != null && !rt.Proc.HasExited) rt.Proc.Kill(); } catch { }
        }

        private static void FailAllPending(PluginRuntime rt, string reason)
        {
            lock (rt.PendingLock)
            {
                foreach (KeyValuePair<int, PendingRequest> kvp in rt.Pending)
                {
                    kvp.Value.Error = reason;
                    kvp.Value.Done.Set();
                }
            }
        }

        public static void ShutdownAll()
        {
            lock (initLock)
            {
                foreach (KeyValuePair<string, PluginRuntime> kvp in runtimes)
                {
                    PluginRuntime rt = kvp.Value;
                    try { if (rt.Proc != null && !rt.Proc.HasExited) rt.Proc.Kill(); } catch { }
                }
            }
        }

        // ─────────────────────────────────────────────────────────
        // 帧编解码
        // ─────────────────────────────────────────────────────────
        private static void SendFrame(PluginRuntime rt, byte type, byte[] payload)
        {
            Process proc = rt.Proc;
            if (proc == null) throw new InvalidOperationException("plugin not running");
            Stream s = proc.StandardInput.BaseStream;
            lock (rt.WriteLock)
            {
                byte[] head = new byte[5];
                head[0] = type;
                head[1] = (byte)(payload.Length & 0xFF);
                head[2] = (byte)((payload.Length >> 8) & 0xFF);
                head[3] = (byte)((payload.Length >> 16) & 0xFF);
                head[4] = (byte)((payload.Length >> 24) & 0xFF);
                s.Write(head, 0, 5);
                if (payload.Length > 0) s.Write(payload, 0, payload.Length);
                s.Flush();
            }
        }

        private static byte[] ReadExactly(Stream s, int count)
        {
            byte[] buf = new byte[count];
            int off = 0;
            while (off < count)
            {
                int n = s.Read(buf, off, count - off);
                if (n <= 0) throw new EndOfStreamException();
                off += n;
            }
            return buf;
        }

        private static void ReadLoop(PluginRuntime rt, Process proc)
        {
            Stream s = proc.StandardOutput.BaseStream;
            try
            {
                while (true)
                {
                    byte[] head = ReadExactly(s, 5);
                    int len = head[1] | (head[2] << 8) | (head[3] << 16) | (head[4] << 24);
                    if (len < 0 || len > MAX_FRAME) throw new IOException("bad frame length");
                    byte[] payload = ReadExactly(s, len);

                    switch (head[0])
                    {
                        case T_HANDSHAKE_ACK:
                            HandleHandshakeAck(rt, payload);
                            break;
                        case T_RESPONSE_HEAD:
                            HandleResponseHead(rt, payload);
                            break;
                        case T_BIN_CHUNK:
                            HandleBinChunk(rt, payload);
                            break;
                        case T_EVENT:
                            Logger.Log("[plugin:" + rt.Info.Id + "] event: " + Encoding.UTF8.GetString(payload));
                            break;
                        case T_FATAL:
                            Logger.Log("[plugin:" + rt.Info.Id + "] FATAL: " + Encoding.UTF8.GetString(payload));
                            KillRuntime(rt, "plugin fatal");
                            return;
                        default:
                            KillRuntime(rt, "unknown frame type " + head[0]);
                            return;
                    }
                }
            }
            catch (Exception)
            {
                // 进程退出/管道断裂: 让所有挂起请求失败并允许重启
                lock (rt)
                {
                    if (rt.Proc == proc) rt.Proc = null;
                }
                FailAllPending(rt, "process exited");
            }
        }

        private static void HandleHandshakeAck(PluginRuntime rt, byte[] payload)
        {
            PendingRequest hs;
            lock (rt.PendingLock) { rt.Pending.TryGetValue(0, out hs); }
            if (hs == null) return;
            try
            {
                Dictionary<string, object> ack = json.DeserializeObject(Encoding.UTF8.GetString(payload)) as Dictionary<string, object>;
                int proto = 0;
                if (ack != null && ack.ContainsKey("protocol")) proto = Convert.ToInt32(ack["protocol"]);
                if (proto != PROTOCOL_VERSION)
                {
                    hs.Error = I18nManager.T("log_plugin_protocol_mismatch", rt.Info.Id, PROTOCOL_VERSION.ToString(), proto.ToString());
                }
            }
            catch (Exception ex)
            {
                hs.Error = ex.Message;
            }
            hs.Done.Set();
        }

        private static void HandleResponseHead(PluginRuntime rt, byte[] payload)
        {
            try
            {
                Dictionary<string, object> head = json.DeserializeObject(Encoding.UTF8.GetString(payload)) as Dictionary<string, object>;
                if (head == null) return;
                int id = Convert.ToInt32(head["id"]);
                PendingRequest pr;
                lock (rt.PendingLock) { rt.Pending.TryGetValue(id, out pr); }
                if (pr == null) return;
                pr.Status = Convert.ToInt32(head["status"]);
                pr.ContentType = head.ContainsKey("type") ? Convert.ToString(head["type"]) : "text/plain";
                pr.BodyLen = head.ContainsKey("bodyLen") ? Convert.ToInt64(head["bodyLen"]) : 0;
                pr.Received = 0;
                if (pr.BodyLen == 0)
                {
                    lock (rt.PendingLock) { rt.Pending.Remove(id); }
                    pr.Done.Set();
                }
            }
            catch (Exception ex)
            {
                Logger.Log(I18nManager.T("log_plugin_request_failed", rt.Info.Id, ex.Message));
            }
        }

        private static void HandleBinChunk(PluginRuntime rt, byte[] payload)
        {
            if (payload.Length < 4) return;
            int id = payload[0] | (payload[1] << 8) | (payload[2] << 16) | (payload[3] << 24);
            PendingRequest pr;
            lock (rt.PendingLock) { rt.Pending.TryGetValue(id, out pr); }
            if (pr == null) return;
            pr.Body.Write(payload, 4, payload.Length - 4);
            pr.Received += payload.Length - 4;
            if (pr.Received >= pr.BodyLen)
            {
                lock (rt.PendingLock) { rt.Pending.Remove(id); }
                pr.Done.Set();
            }
        }

        // ─────────────────────────────────────────────────────────
        // 请求转发（上行/下行对称字节通道）
        // ─────────────────────────────────────────────────────────
        private class PluginResponse
        {
            public int Status;
            public string ContentType;
            public byte[] Body;
        }

        private static PluginResponse Forward(PluginRuntime rt, string method, string subPath, NameValueCollection query, Stream bodyStream, long bodyLen)
        {
            rt.Gate.Wait();
            PendingRequest pr = null;
            try
            {
                EnsureRunning(rt);
                Process proc = rt.Proc;
                if (proc == null) throw new InvalidOperationException("plugin not running");

                int id;
                lock (rt)
                {
                    id = rt.NextId++;
                }
                pr = new PendingRequest();
                pr.Id = id;
                lock (rt.PendingLock)
                {
                    rt.Pending[id] = pr;
                }

                Dictionary<string, object> qd = new Dictionary<string, object>();
                if (query != null)
                {
                    foreach (string k in query.AllKeys)
                    {
                        if (k != null) qd[k] = query[k];
                    }
                }

                Dictionary<string, object> head = new Dictionary<string, object>();
                head["id"] = id;
                head["method"] = method;
                head["path"] = subPath;
                head["query"] = qd;
                head["bodyLen"] = bodyLen;
                SendFrame(rt, T_REQUEST_HEAD, Encoding.UTF8.GetBytes(json.Serialize(head)));

                if (bodyLen > 0 && bodyStream != null)
                {
                    byte[] buf = new byte[CHUNK];
                    long sent = 0;
                    while (sent < bodyLen)
                    {
                        int n = bodyStream.Read(buf, 0, (int)Math.Min(CHUNK, bodyLen - sent));
                        if (n <= 0) break;
                        byte[] frame = new byte[4 + n];
                        frame[0] = (byte)(id & 0xFF);
                        frame[1] = (byte)((id >> 8) & 0xFF);
                        frame[2] = (byte)((id >> 16) & 0xFF);
                        frame[3] = (byte)((id >> 24) & 0xFF);
                        Array.Copy(buf, 0, frame, 4, n);
                        SendFrame(rt, T_BIN_CHUNK, frame);
                        sent += n;
                    }
                }

                int timeoutMs = rt.Info.TimeoutSec * 1000;
                if (!pr.Done.WaitOne(timeoutMs))
                {
                    KillRuntime(rt, "request timeout");
                    throw new TimeoutException(I18nManager.T("plugin_timeout", rt.Info.Id));
                }
                if (pr.Error != null)
                {
                    if (pr.Error == "process exited") throw new InvalidOperationException(I18nManager.T("plugin_crashed", rt.Info.Id));
                    throw new InvalidOperationException(I18nManager.T("plugin_unavailable", rt.Info.Id));
                }

                PluginResponse resp = new PluginResponse();
                resp.Status = pr.Status;
                resp.ContentType = pr.ContentType;
                resp.Body = pr.Body.ToArray();
                return resp;
            }
            finally
            {
                if (pr != null)
                {
                    lock (rt.PendingLock) { rt.Pending.Remove(pr.Id); }
                }
                rt.Gate.Release();
            }
        }

        // ─────────────────────────────────────────────────────────
        // HTTP 挂接点
        // ─────────────────────────────────────────────────────────
        public static bool TryServePage(string rawPath, System.Net.HttpListenerRequest request, System.Net.HttpListenerResponse response)
        {
            if (rawPath == null) return false;
            if (!rawPath.StartsWith("plugin/", StringComparison.OrdinalIgnoreCase)) return false;

            string rest = rawPath.Substring(7);
            int slash = rest.IndexOf('/');
            string id = slash < 0 ? rest : rest.Substring(0, slash);
            string subPath = slash < 0 ? "" : rest.Substring(slash + 1);

            PluginRuntime rt;
            if (!runtimes.TryGetValue(id, out rt))
            {
                HttpServer.ServeError(response, 404, I18nManager.T("err_plugin_not_found", id));
                return true;
            }

            // assets 静态资源: 主进程磁盘直读（防目录穿越）
            if (subPath.StartsWith("assets/", StringComparison.OrdinalIgnoreCase))
            {
                ServePluginAsset(rt, subPath.Substring(7), response);
                return true;
            }

            long bodyLen = request.ContentLength64 > 0 ? request.ContentLength64 : 0;
            string html = null;
            try
            {
                PluginResponse resp = Forward(rt, request.HttpMethod, subPath, request.QueryString, request.InputStream, bodyLen);
                if (subPath.Length == 0)
                {
                    // 主页面: 插件返回片段, 主进程统一包装全站 header/footer
                    html = HttpServer.GetHtmlHeader(rt.Info.Name, "");
                    html += Encoding.UTF8.GetString(resp.Body);
                    html += HttpServer.GetHtmlFooter();
                    WriteBytes(response, resp.Status >= 200 && resp.Status < 400 ? 200 : resp.Status, "text/html; charset=utf-8", Encoding.UTF8.GetBytes(html));
                }
                else
                {
                    WriteBytes(response, resp.Status, resp.ContentType, resp.Body);
                }
            }
            catch (TimeoutException ex)
            {
                ServeFailJson(response, 504, ex.Message);
            }
            catch (Exception ex)
            {
                // 后端不可用时主页面降级为静态 index.html
                if (subPath.Length == 0 && rt.Info.IndexHtmlPath != null)
                {
                    try
                    {
                        html = HttpServer.GetHtmlHeader(rt.Info.Name, "");
                        html += File.ReadAllText(rt.Info.IndexHtmlPath, Encoding.UTF8);
                        html += HttpServer.GetHtmlFooter();
                        WriteBytes(response, 200, "text/html; charset=utf-8", Encoding.UTF8.GetBytes(html));
                        return true;
                    }
                    catch { }
                }
                ServeFailJson(response, 502, ex.Message);
            }
            return true;
        }

        public static bool HandleApi(string rawPath, System.Net.HttpListenerRequest request, System.Net.HttpListenerResponse response)
        {
            if (rawPath == null) return false;

            if (rawPath.Equals("api/plugins", StringComparison.OrdinalIgnoreCase) || rawPath.StartsWith("api/plugins/", StringComparison.OrdinalIgnoreCase))
            {
                return HandleManagementApi(rawPath, request, response);
            }

            if (!rawPath.StartsWith("api/plugin/", StringComparison.OrdinalIgnoreCase)) return false;

            string rest = rawPath.Substring(11);
            int slash = rest.IndexOf('/');
            string id = slash < 0 ? rest : rest.Substring(0, slash);
            string subPath = slash < 0 ? "" : rest.Substring(slash + 1);

            PluginRuntime rt;
            if (!runtimes.TryGetValue(id, out rt))
            {
                HttpServer.ServeJson(response, 404, "{\"success\":false,\"error\":\"" + HttpServer.EscapeJson(I18nManager.T("err_plugin_not_found", id)) + "\"}");
                return true;
            }

            long bodyLen = request.ContentLength64 > 0 ? request.ContentLength64 : 0;
            try
            {
                PluginResponse resp = Forward(rt, request.HttpMethod, subPath, request.QueryString, request.InputStream, bodyLen);
                WriteBytes(response, resp.Status, resp.ContentType, resp.Body);
            }
            catch (TimeoutException ex)
            {
                ServeFailJson(response, 504, ex.Message);
            }
            catch (Exception ex)
            {
                ServeFailJson(response, 502, ex.Message);
            }
            return true;
        }

        private static bool HandleManagementApi(string rawPath, System.Net.HttpListenerRequest request, System.Net.HttpListenerResponse response)
        {
            string sub = rawPath.Equals("api/plugins", StringComparison.OrdinalIgnoreCase) ? "list" : rawPath.Substring(12).ToLowerInvariant();

            if (sub == "list" || sub == "")
            {
                string json = GetPluginsJson();
                HttpServer.ServeJson(response, 200, "{\"success\":true,\"plugins\":" + json + "}");
                return true;
            }

            if (sub == "toggle")
            {
                string body = ReadBody(request);
                var pairs = ServerApplicationContext.ExtractSimpleJsonPairs(body);
                string id = request.QueryString["id"] ?? (pairs.ContainsKey("id") ? pairs["id"] : "");
                string enabledStr = request.QueryString["enabled"] ?? (pairs.ContainsKey("enabled") ? pairs["enabled"] : (pairs.ContainsKey("enable") ? pairs["enable"] : ""));
                bool enabled = true;
                bool.TryParse(enabledStr, out enabled);

                if (string.IsNullOrEmpty(id))
                {
                    HttpServer.ServeJson(response, 400, "{\"success\":false,\"error\":\"missing plugin id\"}");
                    return true;
                }

                bool ok = SetPluginEnabled(id, enabled);
                HttpServer.ServeJson(response, ok ? 200 : 500, string.Format("{{\"success\":{0},\"enabled\":{1}}}", ok ? "true" : "false", enabled ? "true" : "false"));
                return true;
            }

            if (sub == "toggle-all")
            {
                string body = ReadBody(request);
                var pairs = ServerApplicationContext.ExtractSimpleJsonPairs(body);
                string enabledStr = request.QueryString["enabled"] ?? (pairs.ContainsKey("enabled") ? pairs["enabled"] : (pairs.ContainsKey("enable") ? pairs["enable"] : ""));
                bool enabled = true;
                bool.TryParse(enabledStr, out enabled);

                bool ok = SetAllPluginsEnabled(enabled);
                HttpServer.ServeJson(response, ok ? 200 : 500, string.Format("{{\"success\":{0},\"enabled\":{1}}}", ok ? "true" : "false", enabled ? "true" : "false"));
                return true;
            }

            if (sub == "uninstall")
            {
                string body = ReadBody(request);
                var pairs = ServerApplicationContext.ExtractSimpleJsonPairs(body);
                string id = request.QueryString["id"] ?? (pairs.ContainsKey("id") ? pairs["id"] : "");

                if (string.IsNullOrEmpty(id))
                {
                    HttpServer.ServeJson(response, 400, "{\"success\":false,\"error\":\"missing plugin id\"}");
                    return true;
                }

                string err;
                bool ok = UninstallPlugin(id, out err);
                if (ok)
                {
                    HttpServer.ServeJson(response, 200, "{\"success\":true}");
                }
                else
                {
                    HttpServer.ServeJson(response, 500, "{\"success\":false,\"error\":\"" + HttpServer.EscapeJson(err) + "\"}");
                }
                return true;
            }

            if (sub == "refresh")
            {
                RescanPlugins();
                HttpServer.ServeJson(response, 200, "{\"success\":true,\"plugins\":" + GetPluginsJson() + "}");
                return true;
            }

            if (sub == "open-dir")
            {
                OpenPluginsDirectory();
                HttpServer.ServeJson(response, 200, "{\"success\":true}");
                return true;
            }

            return false;
        }

        private static string ReadBody(System.Net.HttpListenerRequest request)
        {
            try
            {
                if (request.InputStream == null) return "";
                using (var reader = new System.IO.StreamReader(request.InputStream, request.ContentEncoding ?? System.Text.Encoding.UTF8))
                {
                    return reader.ReadToEnd();
                }
            }
            catch { return ""; }
        }

        private static void ServeFailJson(System.Net.HttpListenerResponse response, int status, string message)
        {
            HttpServer.ServeJson(response, status, "{\"success\":false,\"error\":\"" + HttpServer.EscapeJson(message) + "\"}");
        }

        private static void WriteBytes(System.Net.HttpListenerResponse response, int status, string contentType, byte[] body)
        {
            try
            {
                response.StatusCode = status;
                response.ContentType = string.IsNullOrEmpty(contentType) ? "application/octet-stream" : contentType;
                response.ContentLength64 = body.Length;
                response.Headers.Add("Access-Control-Allow-Origin", "*");
                response.OutputStream.Write(body, 0, body.Length);
                response.OutputStream.Close();
            }
            catch { }
        }

        private static void ServePluginAsset(PluginRuntime rt, string rel, System.Net.HttpListenerResponse response)
        {
            try
            {
                string assetsDir = Path.Combine(rt.Info.DirPath, "assets");
                string full = Path.GetFullPath(Path.Combine(assetsDir, rel.Replace('/', Path.DirectorySeparatorChar)));
                string baseDir = Path.GetFullPath(assetsDir);
                if (!full.StartsWith(baseDir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !File.Exists(full))
                {
                    HttpServer.ServeError(response, 404, rel);
                    return;
                }
                byte[] data = File.ReadAllBytes(full);
                WriteBytes(response, 200, HttpServer.GetMimeType(Path.GetExtension(full).ToLower()), data);
            }
            catch (Exception ex)
            {
                HttpServer.ServeError(response, 500, ex.Message);
            }
        }

        // ─────────────────────────────────────────────────────────
        // 侧边栏 / 托盘 / 管理支持
        // ─────────────────────────────────────────────────────────
        public static int GetPluginCount()
        {
            lock (initLock)
            {
                return plugins.Count;
            }
        }

        public static int GetActivePluginCount()
        {
            lock (initLock)
            {
                int count = 0;
                foreach (PluginInfo p in plugins)
                {
                    PluginRuntime rt;
                    if (runtimes.TryGetValue(p.Id, out rt) && !rt.Disabled)
                    {
                        count++;
                    }
                }
                return count;
            }
        }

        public static string GetPluginsJson()
        {
            lock (initLock)
            {
                StringBuilder sb = new StringBuilder();
                sb.Append("[");
                bool first = true;
                foreach (PluginInfo p in plugins)
                {
                    PluginRuntime rt;
                    if (!runtimes.TryGetValue(p.Id, out rt)) continue;

                    bool enabled = !rt.Disabled;
                    bool isRunning = rt.Proc != null && !rt.Proc.HasExited;

                    if (!first) sb.Append(",");
                    first = false;

                    sb.Append("{");
                    sb.AppendFormat("\"id\":\"{0}\",", HttpServer.EscapeJson(p.Id));
                    sb.AppendFormat("\"name\":\"{0}\",", HttpServer.EscapeJson(p.Name));
                    sb.AppendFormat("\"version\":\"{0}\",", HttpServer.EscapeJson(p.Version));
                    sb.AppendFormat("\"description\":\"{0}\",", HttpServer.EscapeJson(p.Description));
                    sb.AppendFormat("\"icon\":\"{0}\",", HttpServer.EscapeJson(p.Icon));
                    sb.AppendFormat("\"order\":{0},", p.Order);
                    sb.AppendFormat("\"enabled\":{0},", enabled ? "true" : "false");
                    sb.AppendFormat("\"isRunning\":{0},", isRunning ? "true" : "false");
                    sb.AppendFormat("\"hasPage\":{0},", !string.IsNullOrEmpty(p.IndexHtmlPath) ? "true" : "false");
                    sb.AppendFormat("\"dirPath\":\"{0}\"", HttpServer.EscapeJson(p.DirPath));
                    sb.Append("}");
                }
                sb.Append("]");
                return sb.ToString();
            }
        }

        public static List<Dictionary<string, object>> GetPluginsList()
        {
            lock (initLock)
            {
                List<Dictionary<string, object>> list = new List<Dictionary<string, object>>();
                foreach (PluginInfo p in plugins)
                {
                    PluginRuntime rt;
                    if (!runtimes.TryGetValue(p.Id, out rt)) continue;

                    bool enabled = !rt.Disabled;
                    bool isRunning = rt.Proc != null && !rt.Proc.HasExited;

                    Dictionary<string, object> item = new Dictionary<string, object>();
                    item["id"] = p.Id;
                    item["name"] = p.Name;
                    item["version"] = p.Version;
                    item["description"] = p.Description;
                    item["icon"] = p.Icon;
                    item["order"] = p.Order;
                    item["enabled"] = enabled;
                    item["isRunning"] = isRunning;
                    item["hasPage"] = !string.IsNullOrEmpty(p.IndexHtmlPath);
                    item["dirPath"] = p.DirPath;
                    list.Add(item);
                }
                return list;
            }
        }

        public static bool SetPluginEnabled(string pluginId, bool enabled)
        {
            lock (initLock)
            {
                PluginRuntime rt;
                if (!runtimes.TryGetValue(pluginId, out rt)) return false;

                ServerApplicationContext.SetPluginDisabled(pluginId, !enabled);
                lock (rt)
                {
                    if (!enabled)
                    {
                        rt.Disabled = true;
                        rt.DisabledReason = "disabled_by_user";
                        try
                        {
                            if (rt.Proc != null && !rt.Proc.HasExited)
                            {
                                rt.Proc.Kill();
                            }
                        }
                        catch { }
                        try { if (rt.Proc != null) rt.Proc.Dispose(); } catch { }
                        rt.Proc = null;
                        FailAllPending(rt, "plugin disabled by user");
                    }
                    else
                    {
                        rt.Disabled = false;
                        rt.DisabledReason = null;
                        rt.Restarts = 0;
                    }
                }
                Logger.Log(I18nManager.T(enabled ? "log_plugin_enabled" : "log_plugin_disabled", rt.Info.Name));
                return true;
            }
        }

        public static bool SetAllPluginsEnabled(bool enabled)
        {
            lock (initLock)
            {
                foreach (PluginInfo p in plugins)
                {
                    SetPluginEnabled(p.Id, enabled);
                }
                return true;
            }
        }

        public static bool UninstallPlugin(string pluginId, out string error)
        {
            error = null;
            lock (initLock)
            {
                PluginRuntime rt;
                if (!runtimes.TryGetValue(pluginId, out rt))
                {
                    error = "plugin not found";
                    return false;
                }

                // 1. 禁用并停止进程
                SetPluginEnabled(pluginId, false);

                string dir = rt.Info.DirPath;
                string pluginName = rt.Info.Name;

                // 2. 移除元数据与运行时
                plugins.RemoveAll(delegate(PluginInfo x) { return string.Equals(x.Id, pluginId, StringComparison.OrdinalIgnoreCase); });
                runtimes.Remove(pluginId);
                ServerApplicationContext.SetPluginDisabled(pluginId, false);

                // 3. 物理清理目录
                try
                {
                    if (Directory.Exists(dir))
                    {
                        Thread.Sleep(250);
                        Directory.Delete(dir, true);
                    }
                    Logger.Log(I18nManager.T("log_plugin_uninstalled", pluginName));
                    return true;
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                    Logger.Log(I18nManager.T("log_plugin_uninstall_failed", pluginName, ex.Message));
                    return false;
                }
            }
        }

        public static void RescanPlugins()
        {
            lock (initLock)
            {
                ShutdownAll();
                plugins.Clear();
                runtimes.Clear();
                initialized = false;
                Initialize();
            }
        }

        public static string GetNavNodesHtml(string activePath)
        {
            StringBuilder sb = new StringBuilder();
            foreach (PluginInfo p in plugins)
            {
                PluginRuntime rt;
                if (!runtimes.TryGetValue(p.Id, out rt) || rt.Disabled)
                {
                    continue; // 禁用的插件不显示在左侧导航
                }

                string link = "/plugin/" + p.Id;
                string active = (activePath == link || activePath == p.Id) ? " active-node active" : "";
                sb.AppendFormat("<div class='tree-node'><div class='tree-row{0}'><a href='{1}' class='tree-link-inline{0}' style='color:inherit;'>{2} {3}</a></div></div>",
                    active, link, p.Icon, p.Name);
            }
            return sb.ToString();
        }

        public static string GetLobbyCardsHtml()
        {
            StringBuilder sb = new StringBuilder();
            foreach (PluginInfo p in plugins)
            {
                PluginRuntime rt;
                if (!runtimes.TryGetValue(p.Id, out rt) || rt.Disabled)
                {
                    continue; // 禁用的插件不显示在首页大厅
                }

                string link = "/plugin/" + p.Id;
                string icon = string.IsNullOrEmpty(p.Icon) ? "🧩" : p.Icon;
                string title = string.IsNullOrEmpty(p.Name) ? p.Id : p.Name;
                string desc = string.IsNullOrEmpty(p.Description) ? "" : p.Description;
                string ver = string.IsNullOrEmpty(p.Version) ? "1.0.0" : p.Version;

                sb.AppendFormat(
                    "<a href='{0}' class='card drive-card dev-card' title='{1} - {2}'>" +
                    "  <div class='icon-wrapper' style='font-size: 2.2rem; display: flex; align-items: center; justify-content: center;'>{3}</div>" +
                    "  <div class='card-info'>" +
                    "    <div class='card-title-row'>" +
                    "      <span class='title title-text' title='{1}'>{1}</span>" +
                    "      <span class='dev-badge ready'>v{4}</span>" +
                    "    </div>" +
                    "    <div class='desc' title='{2}'>{2}</div>" +
                    "  </div>" +
                    "</a>",
                    link,
                    HttpServer.EscapeJson(title),
                    HttpServer.EscapeJson(desc),
                    icon,
                    HttpServer.EscapeJson(ver)
                );
            }
            return sb.ToString();
        }

        public static void OpenPluginsDirectory()
        {
            try
            {
                if (!Directory.Exists(pluginsRoot)) Directory.CreateDirectory(pluginsRoot);
                Process.Start("explorer.exe", "\"" + pluginsRoot + "\"");
            }
            catch (Exception ex)
            {
                Logger.Log(I18nManager.T("log_plugin_request_failed", "dir", ex.Message));
            }
        }

        // ─────────────────────────────────────────────────────────
        // 工具
        // ─────────────────────────────────────────────────────────
        private static string GetStr(Dictionary<string, object> m, string key)
        {
            object v;
            if (m.TryGetValue(key, out v) && v != null) return Convert.ToString(v);
            return null;
        }

        private static int GetInt(Dictionary<string, object> m, string key, int def)
        {
            object v;
            if (m.TryGetValue(key, out v) && v != null)
            {
                try { return Convert.ToInt32(v); } catch { }
            }
            return def;
        }

        private static bool GetBool(Dictionary<string, object> m, string key)
        {
            object v;
            if (m.TryGetValue(key, out v) && v != null)
            {
                try { return Convert.ToBoolean(v); } catch { }
            }
            return false;
        }
    }
}
