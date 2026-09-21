using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

// ─────────────────────────────────────────────────────────────
// 示例插件后端：独立进程，与宿主通过 stdin/stdout 字节帧协议通信
// 协议规范见 README.md「插件协议 v1」章节（与主进程 PluginHost.cs 对称实现）
//
// 演示端点（path）:
//   ""            GET   主页面（读取 index.html 追加动态时间戳）
//   ping          GET   存活探测（并发验收基准点）
//   info          GET   清单信息 + 宿主上下文快照
//   list          GET   ~2MB 依赖列表 JSON（下行 BIN_CHUNK 大负载验收）
//   echo          POST  请求 body 逐字节回显（上行字节通道验收）
//   scan          GET   启动后台模拟扫描（异步任务先例）
//   scan-status   GET   查询扫描进度（扫描期间 ping 仍并发可用）
//   crash         GET   注入崩溃（Environment.FailFast，验收 502 + 自动重拉）
//   hang          GET   注入挂起（验收超时 Kill + 504）
// ─────────────────────────────────────────────────────────────
internal static class ExampleBackend
{
    private const int PROTOCOL_VERSION = 1;
    private const byte T_HANDSHAKE_REQ = 0x01;
    private const byte T_HANDSHAKE_ACK = 0x02;
    private const byte T_REQUEST_HEAD = 0x03;
    private const byte T_RESPONSE_HEAD = 0x04;
    private const byte T_BIN_CHUNK = 0x05;
    private const int CHUNK = 64 * 1024;

    private static readonly JavaScriptSerializer json = new JavaScriptSerializer();
    private static Dictionary<string, object> hostContext;
    private static int scanProgress = -1;
    private static readonly object scanLock = new object();

    private static int Main()
    {
        // 父进程监护: 宿主被强杀时（Windows 不级联杀子进程）自行退出
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
                    if (hostContext != null)
                    {
                        Environment.SetEnvironmentVariable("LDS_PARENT_PID", Convert.ToString(hostContext["parentPid"]));
                        parentPid = Convert.ToInt32(hostContext["parentPid"]);
                    }
                    Dictionary<string, object> ack = new Dictionary<string, object>();
                    ack["protocol"] = PROTOCOL_VERSION;
                    ack["plugin"] = "example";
                    ack["version"] = "1.0.0";
                    WriteFrame(stdout, T_HANDSHAKE_ACK, Encoding.UTF8.GetBytes(json.Serialize(ack)));
                    Log("握手完成，宿主上下文已就绪");
                }
                else if (type == T_REQUEST_HEAD)
                {
                    HandleRequest(stdin, stdout, payload);
                }
            }
        }
        catch (Exception ex)
        {
            Log("致命错误，进程退出: " + ex.Message);
            return 1;
        }
    }

    private static void HandleRequest(Stream stdin, Stream stdout, byte[] payload)
    {
        Dictionary<string, object> head = json.DeserializeObject(Encoding.UTF8.GetString(payload)) as Dictionary<string, object>;
        int id = Convert.ToInt32(head["id"]);
        string method = Convert.ToString(head["method"]);
        string path = head.ContainsKey("path") ? Convert.ToString(head["path"]) : "";
        long bodyLen = head.ContainsKey("bodyLen") ? Convert.ToInt64(head["bodyLen"]) : 0;

        // 读取上行 body（对称 BIN_CHUNK 通道）
        byte[] body = new byte[0];
        if (bodyLen > 0)
        {
            using (MemoryStream ms = new MemoryStream())
            {
                long received = 0;
                while (received < bodyLen)
                {
                    byte t;
                    byte[] chunkPayload;
                    ReadFrame(stdin, out t, out chunkPayload);
                    if (t != T_BIN_CHUNK || chunkPayload.Length < 4) continue;
                    int chunkId = chunkPayload[0] | (chunkPayload[1] << 8) | (chunkPayload[2] << 16) | (chunkPayload[3] << 24);
                    if (chunkId != id) continue;
                    ms.Write(chunkPayload, 4, chunkPayload.Length - 4);
                    received += chunkPayload.Length - 4;
                }
                body = ms.ToArray();
            }
        }

        // 故障注入端点
        if (path == "crash")
        {
            Log("收到 crash 注入指令，FailFast 自毁");
            Environment.FailFast("example plugin crash injection");
        }
        if (path == "hang")
        {
            Log("收到 hang 注入指令，挂起 60s");
            Thread.Sleep(60000);
        }

        int status = 200;
        string contentType = "application/json; charset=utf-8";
        byte[] respBody = null;

        try
        {
            if (path.Length == 0)
            {
                // 主页面: 读静态模板 + 动态时间戳
                contentType = "text/html; charset=utf-8";
                string html = File.ReadAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "index.html"), Encoding.UTF8);
                respBody = Encoding.UTF8.GetBytes(html + "\n<!-- rendered at " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " -->");
            }
            else if (path == "ping")
            {
                respBody = Encoding.UTF8.GetBytes("{\"success\":true,\"pong\":true,\"now\":\"" + DateTime.Now.ToString("HH:mm:ss.fff") + "\"}");
            }
            else if (path == "info")
            {
                Dictionary<string, object> r = new Dictionary<string, object>();
                r["success"] = true;
                r["hostContext"] = hostContext;
                r["process"] = Process.GetCurrentProcess().Id;
                r["dotnet"] = Environment.Version.ToString();
                respBody = Encoding.UTF8.GetBytes(json.Serialize(r));
            }
            else if (path == "list")
            {
                // ~2MB 大 JSON: 下行 BIN_CHUNK 通道验收
                StringBuilder sb = new StringBuilder(2 * 1024 * 1024);
                sb.Append("{\"success\":true,\"total\":10000,\"items\":[");
                Random rnd = new Random(42);
                for (int i = 0; i < 10000; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append("{\"group\":\"com.example.g" + (i / 50) + "\",\"artifact\":\"lib-" + i + "\",\"version\":\"1." + (i % 9) + "." + rnd.Next(99) + "\",\"size\":" + (rnd.Next(900000) + 1024) + ",\"date\":\"202" + (i % 6) + "-0" + (i % 9 + 1) + "-1" + (i % 9) + "\"}");
                }
                sb.Append("]}");
                respBody = Encoding.UTF8.GetBytes(sb.ToString());
            }
            else if (path == "echo")
            {
                // 请求 body 逐字节回显, Content-Type 透传
                respBody = body;
                status = 200;
            }
            else if (path == "scan")
            {
                lock (scanLock)
                {
                    if (scanProgress < 0 || scanProgress >= 100)
                    {
                        scanProgress = 0;
                        Thread worker = new Thread(delegate()
                        {
                            for (int p = 0; p <= 100; p += 5)
                            {
                                Thread.Sleep(200);
                                lock (scanLock) { scanProgress = p; }
                            }
                            Log("模拟扫描完成");
                        });
                        worker.IsBackground = true;
                        worker.Start();
                    }
                }
                respBody = Encoding.UTF8.GetBytes("{\"success\":true,\"started\":true}");
            }
            else if (path == "scan-status")
            {
                int p;
                lock (scanLock) { p = scanProgress; }
                respBody = Encoding.UTF8.GetBytes("{\"success\":true,\"progress\":" + p + "}");
            }
            else
            {
                status = 404;
                respBody = Encoding.UTF8.GetBytes("{\"success\":false,\"error\":\"unknown path: " + path + "\"}");
            }
        }
        catch (Exception ex)
        {
            status = 500;
            respBody = Encoding.UTF8.GetBytes("{\"success\":false,\"error\":\"" + ex.Message.Replace("\"", "'") + "\"}");
        }

        // 响应: RESPONSE_HEAD + 可选下行 BIN_CHUNK
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

    // ── 帧编解码（与宿主 PluginHost 对称） ──
    private static void WriteFrame(Stream s, byte type, byte[] payload)
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

    private static void ReadFrame(Stream s, out byte type, out byte[] payload)
    {
        byte[] head = ReadExactly(s, 5);
        int len = head[1] | (head[2] << 8) | (head[3] << 16) | (head[4] << 24);
        if (len < 0 || len > 16 * 1024 * 1024) throw new IOException("bad frame length");
        type = head[0];
        payload = len == 0 ? new byte[0] : ReadExactly(s, len);
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

    private static void Log(string msg)
    {
        Console.Error.WriteLine("[example] " + msg);
    }
}
