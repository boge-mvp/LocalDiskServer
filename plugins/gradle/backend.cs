using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Xml;

internal static class GradleBackend
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
    private static string currentLanguage = "zh-CN";
    private static readonly Dictionary<string, string> i18nStrings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    public class CachedDependency
    {
        public string Group { get; set; }
        public string Artifact { get; set; }
        public string Version { get; set; }
        public bool IsKmp { get; set; }
        public string FriendlySize { get; set; }
        public string LocalPath { get; set; }
    }

    private static int Main()
    {
        int parentPid = -1;
        try { parentPid = Convert.ToInt32(Environment.GetEnvironmentVariable("LDS_PARENT_PID")); } catch { }
        if (parentPid > 0)
        {
            Thread watchdog = new Thread(delegate()
            {
                while (true)
                {
                    Thread.Sleep(3000);
                    try { Process.GetProcessById(parentPid); } catch { Environment.Exit(0); }
                }
            });
            watchdog.IsBackground = true;
            watchdog.Start();
        }

        Stream stdin = Console.OpenStandardInput();
        Stream stdout = Console.OpenStandardOutput();

        // 启动时后台自动扫描
        TriggerGradleScanAsync();

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
                        if (hostContext.ContainsKey("parentPid"))
                        {
                            parentPid = Convert.ToInt32(hostContext["parentPid"]);
                        }
                        if (hostContext.ContainsKey("language") && hostContext["language"] != null)
                        {
                            currentLanguage = Convert.ToString(hostContext["language"]);
                        }
                    }
                    LoadLanguage(currentLanguage);
                    Dictionary<string, object> ack = new Dictionary<string, object>();
                    ack["protocol"] = PROTOCOL_VERSION;
                    ack["plugin"] = "gradle";
                    ack["version"] = "1.0.0";
                    WriteFrame(stdout, T_HANDSHAKE_ACK, Encoding.UTF8.GetBytes(json.Serialize(ack)));
                    Log("Gradle 插件握手完成");
                }
                else if (type == T_REQUEST_HEAD)
                {
                    HandleRequest(stdin, stdout, payload);
                }
            }
        }
        catch (Exception ex)
        {
            Log("Gradle 插件致命异常退出: " + ex.Message);
            return 1;
        }
    }

    private static void LoadLanguage(string lang)
    {
        try
        {
            string langFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "lang\\" + lang + ".ini");
            if (!File.Exists(langFile))
            {
                langFile = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "lang\\zh-CN.ini");
            }
            if (File.Exists(langFile))
            {
                string[] lines = File.ReadAllLines(langFile, Encoding.UTF8);
                lock (i18nStrings)
                {
                    i18nStrings.Clear();
                    foreach (string l in lines)
                    {
                        string t = l.Trim();
                        if (string.IsNullOrEmpty(t) || t.StartsWith("#") || t.StartsWith(";")) continue;
                        int eq = t.IndexOf('=');
                        if (eq > 0)
                        {
                            string k = t.Substring(0, eq).Trim();
                            string v = t.Substring(eq + 1).Trim();
                            i18nStrings[k] = v.Replace("\\n", "\n").Replace("\\t", "\t");
                        }
                    }
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

    private static string EscapeJson(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");
    }

    private static string FormatFileSize(long bytes)
    {
        if (bytes <= 0) return "0 B";
        string[] units = new string[] { "B", "KB", "MB", "GB", "TB" };
        int i = 0;
        double d = bytes;
        while (d >= 1024 && i < units.Length - 1)
        {
            d /= 1024;
            i++;
        }
        return string.Format("{0:0.##} {1}", d, units[i]);
    }

    private static void Log(string msg)
    {
        Console.Error.WriteLine("[gradle] " + msg);
    }
        public static bool isGradleScanning = false;
        public static readonly object gradleScanLock = new object();
        public static string cachedGradleHome = "";
        public static string cachedWrappersJson = "[]";
        public static int cachedDependencyCount = 0;
        public static int cachedKmpCount = 0;
        public static long cachedTotalSize = 0;

        public static readonly List<CachedDependency> cachedDependencies = new List<CachedDependency>();

        public static string GetCacheFilePath()
        {
            try
            {
                string cacheDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "cache");
                if (!Directory.Exists(cacheDir))
                {
                    Directory.CreateDirectory(cacheDir);
                }
                return Path.Combine(cacheDir, "gradle_cache.dat");
            }
            catch
            {
                return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "gradle_cache.dat");
            }
        }

        public static void ClearCacheAndReleaseResources()
        {
            lock (gradleScanLock)
            {
                cachedDependencies.Clear();
                cachedDependencies.TrimExcess();
                cachedWrappersJson = "[]";
                cachedDependencyCount = 0;
                cachedKmpCount = 0;
                cachedTotalSize = 0;
                cachedGradleHome = "";
            }
            GC.Collect();
            Log(T("log_dev_ecosystem_released"));
        }

        public static void SaveToDiskCache(long distsTicks, long filesTicks)
        {
            try
            {
                string cacheFile = GetCacheFilePath();
                StringBuilder sb = new StringBuilder();
                sb.AppendFormat("#META:{0}|{1}|{2}|{3}|{4}|{5}", distsTicks, filesTicks, cachedDependencyCount, cachedKmpCount, cachedTotalSize, cachedGradleHome ?? "").AppendLine();
                sb.AppendLine("#WRAPPERS:" + (cachedWrappersJson ?? "[]"));
                lock (gradleScanLock)
                {
                    for (int i = 0; i < cachedDependencies.Count; i++)
                    {
                        var d = cachedDependencies[i];
                        sb.AppendFormat("{0}|{1}|{2}|{3}|{4}|{5}",
                            d.Group ?? "", d.Artifact ?? "", d.Version ?? "",
                            d.IsKmp ? "1" : "0", d.FriendlySize ?? "", d.LocalPath ?? "").AppendLine();
                    }
                }
                File.WriteAllText(cacheFile, sb.ToString(), Encoding.UTF8);
                Log(T("log_dev_ecosystem_saved", cacheFile));
            }
            catch (Exception ex)
            {
                Log("SaveToDiskCache Exception: " + ex.Message);
            }
        }

        public static bool TryLoadFromDiskCache(out long distsTicks, out long filesTicks)
        {
            distsTicks = 0;
            filesTicks = 0;
            try
            {
                string cacheFile = GetCacheFilePath();
                if (!File.Exists(cacheFile)) return false;

                var sw = System.Diagnostics.Stopwatch.StartNew();
                string[] lines = File.ReadAllLines(cacheFile, Encoding.UTF8);
                if (lines == null || lines.Length < 2) return false;

                string metaLine = lines[0];
                if (!metaLine.StartsWith("#META:")) return false;
                string[] metaParts = metaLine.Substring(6).Split('|');
                if (metaParts.Length < 6) return false;

                long.TryParse(metaParts[0], out distsTicks);
                long.TryParse(metaParts[1], out filesTicks);
                int depCount = 0; int.TryParse(metaParts[2], out depCount);
                int kmpCount = 0; int.TryParse(metaParts[3], out kmpCount);
                long totalSize = 0; long.TryParse(metaParts[4], out totalSize);
                string gHome = metaParts[5];

                string wrapLine = lines[1];
                string wrappers = wrapLine.StartsWith("#WRAPPERS:") ? wrapLine.Substring(10) : "[]";

                List<CachedDependency> deps = new List<CachedDependency>(Math.Max(32, lines.Length - 2));
                for (int i = 2; i < lines.Length; i++)
                {
                    string line = lines[i];
                    if (string.IsNullOrEmpty(line)) continue;
                    string[] parts = line.Split('|');
                    if (parts.Length >= 6)
                    {
                        deps.Add(new CachedDependency
                        {
                            Group = parts[0],
                            Artifact = parts[1],
                            Version = parts[2],
                            IsKmp = parts[3] == "1",
                            FriendlySize = parts[4],
                            LocalPath = parts[5]
                        });
                    }
                }

                lock (gradleScanLock)
                {
                    cachedGradleHome = gHome;
                    cachedWrappersJson = wrappers;
                    cachedDependencyCount = depCount;
                    cachedKmpCount = kmpCount;
                    cachedTotalSize = totalSize;
                    cachedDependencies.Clear();
                    cachedDependencies.AddRange(deps);
                }

                sw.Stop();
                Log(T("log_dev_ecosystem_fast_loaded", "Gradle", deps.Count, sw.ElapsedMilliseconds));
                return true;
            }
            catch (Exception ex)
            {
                Log("TryLoadFromDiskCache Exception: " + ex.Message);
                return false;
            }
        }

        public static void TriggerGradleScanAsync(bool forceRescan = false)
        {
            if (!true) return;

            lock (gradleScanLock)
            {
                if (isGradleScanning) return;
                isGradleScanning = true;
            }

            System.Threading.ThreadPool.QueueUserWorkItem(delegate {
                try
                {
                    if (!true) return;

                    string gHome = GetGradleHome();
                    cachedGradleHome = gHome;
                    if (string.IsNullOrEmpty(gHome))
                    {
                        lock (gradleScanLock)
                        {
                            cachedWrappersJson = "[]";
                            cachedDependencyCount = 0;
                            cachedKmpCount = 0;
                            cachedTotalSize = 0;
                            cachedDependencies.Clear();
                        }
                        return;
                    }

                    string distsPath = Path.Combine(gHome, "wrapper", "dists");
                    string files21Path = Path.Combine(gHome, "caches", "modules-2", "files-2.1");

                    long curDistsTicks = Directory.Exists(distsPath) ? Directory.GetLastWriteTimeUtc(distsPath).Ticks : 0;
                    long curFilesTicks = Directory.Exists(files21Path) ? Directory.GetLastWriteTimeUtc(files21Path).Ticks : 0;

                    // 1. 如果非强制重扫且内存为空，优先尝试从磁盘快照秒级冷启动
                    if (!forceRescan && cachedDependencies.Count == 0)
                    {
                        long cachedDistsTicks, cachedFilesTicks;
                        if (TryLoadFromDiskCache(out cachedDistsTicks, out cachedFilesTicks))
                        {
                            // 2. 毫秒级时间戳比对探活
                            if (curDistsTicks == cachedDistsTicks && curFilesTicks == cachedFilesTicks)
                            {
                                Log(T("log_dev_ecosystem_verified"));
                                return; // 数据100%真实一致，探活成功退出！
                            }
                        }
                    }
                    else if (!forceRescan && cachedDependencies.Count > 0)
                    {
                        // 内存已有数据，仅读取元数据比对
                        long cachedDistsTicks, cachedFilesTicks;
                        if (ReadMetadataTicks(out cachedDistsTicks, out cachedFilesTicks))
                        {
                            if (curDistsTicks == cachedDistsTicks && curFilesTicks == cachedFilesTicks)
                            {
                                Log(T("log_dev_ecosystem_verified"));
                                return;
                            }
                        }
                    }

                    // 3. 执行真实物理扫描
                    Log(T("log_gradle_scan_thread_started"));
                    DoGradleScan(gHome, distsPath, files21Path, curDistsTicks, curFilesTicks);
                }
                catch (Exception ex)
                {
                    Log(T("log_gradle_scan_ex", ex.Message));
                }
                finally
                {
                    lock (gradleScanLock)
                    {
                        isGradleScanning = false;
                    }
                }
            });
        }

        private static bool ReadMetadataTicks(out long distsTicks, out long filesTicks)
        {
            distsTicks = 0;
            filesTicks = 0;
            try
            {
                string cacheFile = GetCacheFilePath();
                if (!File.Exists(cacheFile)) return false;
                using (var reader = new StreamReader(cacheFile, Encoding.UTF8))
                {
                    string metaLine = reader.ReadLine();
                    if (metaLine != null && metaLine.StartsWith("#META:"))
                    {
                        string[] parts = metaLine.Substring(6).Split('|');
                        if (parts.Length >= 2)
                        {
                            long.TryParse(parts[0], out distsTicks);
                            long.TryParse(parts[1], out filesTicks);
                            return true;
                        }
                    }
                }
            }
            catch {}
            return false;
        }

        private static void DoGradleScan(string gHome, string distsPath, string files21Path, long distsTicks, long filesTicks)
        {
            if (!true) return;

            // 1. Scan Wrappers
            StringBuilder wrappersJson = new StringBuilder();
            wrappersJson.Append("[");
            if (Directory.Exists(distsPath))
            {
                string[] dirs = Directory.GetDirectories(distsPath);
                for (int i = 0; i < dirs.Length; i++)
                {
                    string dName = Path.GetFileName(dirs[i]);
                    long size = GetDirSize(dirs[i]);
                    int fileCount = 0;
                    try 
                    { 
                        System.Collections.Generic.List<string> wFiles = new System.Collections.Generic.List<string>();
                        SafeGetFiles(dirs[i], "*", wFiles);
                        fileCount = wFiles.Count;
                    } 
                    catch {}
                    string friendlyVersion = dName.Replace("gradle-", "").Replace("-all", "").Replace("-bin", "");
                    
                    if (i > 0) wrappersJson.Append(",");
                    wrappersJson.AppendFormat("{{\"version\":\"{0}\",\"fullName\":\"{1}\",\"size\":\"{2}\",\"files\":{3},\"path\":\"{4}\"}}",
                        EscapeJson(friendlyVersion), EscapeJson(dName), FormatFileSize(size), fileCount, EscapeJson(dirs[i]));
                }
            }
            wrappersJson.Append("]");
            string tmpWrappers = wrappersJson.ToString();

            // 2. Scan Dependencies
            System.Collections.Generic.List<CachedDependency> tmpDeps = new System.Collections.Generic.List<CachedDependency>();
            int depCount = 0;
            int kmpCount = 0;
            long totalSize = 0;

            if (Directory.Exists(files21Path))
            {
                System.Collections.Generic.List<string> pomList = new System.Collections.Generic.List<string>();
                SafeGetFiles(files21Path, "*.pom", pomList);
                string[] pomFiles = pomList.ToArray();
                depCount = pomFiles.Length;
                
                foreach (string pom in pomFiles)
                {
                    string parentDir = SafeGetDirectoryName(pom);
                    string versionDir = SafeGetDirectoryName(parentDir);
                    if (Directory.Exists(versionDir))
                    {
                        bool isKmp = false;
                        try
                        {
                            isKmp = SafeHasFile(versionDir, "*.module");
                        }
                        catch {}
                        if (isKmp)
                        {
                            kmpCount++;
                        }
                        
                        string[] parts = pom.Split(new char[] { Path.DirectorySeparatorChar, '/' }, StringSplitOptions.RemoveEmptyEntries);
                        int filesIdx = -1;
                        for (int i = 0; i < parts.Length; i++)
                        {
                            if (parts[i].Equals("files-2.1", StringComparison.OrdinalIgnoreCase))
                            {
                                filesIdx = i;
                                break;
                            }
                        }
                        if (filesIdx != -1 && filesIdx + 3 < parts.Length)
                        {
                            string group = parts[filesIdx + 1];
                            string artifact = parts[filesIdx + 2];
                            string version = parts[filesIdx + 3];
                            long size = GetDirSize(versionDir);
                            
                            tmpDeps.Add(new CachedDependency
                            {
                                Group = group,
                                Artifact = artifact,
                                Version = version,
                                IsKmp = isKmp,
                                FriendlySize = FormatFileSize(size),
                                LocalPath = versionDir
                            });
                        }
                    }
                }
                totalSize = GetDirSize(files21Path);
            }

            lock (gradleScanLock)
            {
                cachedWrappersJson = tmpWrappers;
                cachedDependencyCount = depCount;
                cachedKmpCount = kmpCount;
                cachedTotalSize = totalSize;
                cachedDependencies.Clear();
                cachedDependencies.AddRange(tmpDeps);
            }
            SaveToDiskCache(distsTicks, filesTicks);
            Log(T("log_gradle_scan_finished", depCount, kmpCount, FormatFileSize(totalSize)));
        }

        private static string GetGradleHome()
        {
            string envHome = Environment.GetEnvironmentVariable("GRADLE_USER_HOME", EnvironmentVariableTarget.Process);
            if (string.IsNullOrEmpty(envHome))
            {
                envHome = Environment.GetEnvironmentVariable("GRADLE_USER_HOME", EnvironmentVariableTarget.User);
            }
            if (string.IsNullOrEmpty(envHome))
            {
                envHome = Environment.GetEnvironmentVariable("GRADLE_USER_HOME", EnvironmentVariableTarget.Machine);
            }

            if (!string.IsNullOrEmpty(envHome))
            {
                try
                {
                    // 展开可能包含的嵌套环境变量（如 %USERPROFILE%）
                    envHome = Environment.ExpandEnvironmentVariables(envHome);
                    // 清理可能误带的双引号、单引号或首尾空格
                    envHome = envHome.Trim('\"', '\'', ' ', '\t');
                    if (Directory.Exists(envHome))
                    {
                        return envHome;
                    }
                }
                catch { }
            }

            string realGradle = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".gradle");
            if (Directory.Exists(realGradle))
            {
                return realGradle;
            }
            return null;
        }

        public static void DetectJavaRuntime(out string javaHome, out string javaVersion, out string javaPath)
        {
            javaHome = Environment.GetEnvironmentVariable("JAVA_HOME") ?? "";
            javaVersion = "";
            javaPath = "";

            if (!string.IsNullOrEmpty(javaHome) && Directory.Exists(javaHome))
            {
                string exe = Path.Combine(javaHome, "bin", "java.exe");
                if (File.Exists(exe)) javaPath = exe;
            }

            if (string.IsNullOrEmpty(javaPath))
            {
                try
                {
                    string pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
                    string[] paths = pathEnv.Split(Path.PathSeparator);
                    foreach (string p in paths)
                    {
                        if (string.IsNullOrEmpty(p)) continue;
                        string candidate = Path.Combine(p.Trim('\"', ' '), "java.exe");
                        if (File.Exists(candidate))
                        {
                            javaPath = candidate;
                            if (string.IsNullOrEmpty(javaHome))
                            {
                                string binDir = Path.GetDirectoryName(candidate);
                                if (!string.IsNullOrEmpty(binDir))
                                {
                                    javaHome = Path.GetDirectoryName(binDir) ?? "";
                                }
                            }
                            break;
                        }
                    }
                }
                catch { }
            }

            if (!string.IsNullOrEmpty(javaPath) && File.Exists(javaPath))
            {
                try
                {
                    ProcessStartInfo psi = new ProcessStartInfo
                    {
                        FileName = javaPath,
                        Arguments = "-version",
                        RedirectStandardError = true,
                        RedirectStandardOutput = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                    using (var p = Process.Start(psi))
                    {
                        string err = p.StandardError.ReadToEnd();
                        string outStr = p.StandardOutput.ReadToEnd();
                        p.WaitForExit(3000);
                        string output = !string.IsNullOrEmpty(err) ? err : outStr;
                        if (!string.IsNullOrEmpty(output))
                        {
                            string[] lines = output.Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                            if (lines.Length > 0)
                            {
                                javaVersion = lines[0].Trim();
                            }
                        }
                    }
                }
                catch { }
            }
        }

        public static void DetectGradleCli(out string gradleCliVersion, out string gradleCliPath)
        {
            gradleCliVersion = "";
            gradleCliPath = "";

            try
            {
                string pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
                string[] paths = pathEnv.Split(Path.PathSeparator);
                foreach (string p in paths)
                {
                    if (string.IsNullOrEmpty(p)) continue;
                    string candidateCmd = Path.Combine(p.Trim('\"', ' '), "gradle.bat");
                    string candidateExe = Path.Combine(p.Trim('\"', ' '), "gradle.exe");
                    if (File.Exists(candidateCmd))
                    {
                        gradleCliPath = candidateCmd;
                        break;
                    }
                    else if (File.Exists(candidateExe))
                    {
                        gradleCliPath = candidateExe;
                        break;
                    }
                }
            }
            catch { }

            if (!string.IsNullOrEmpty(gradleCliPath) && File.Exists(gradleCliPath))
            {
                try
                {
                    ProcessStartInfo psi = new ProcessStartInfo
                    {
                        FileName = "cmd.exe",
                        Arguments = "/c \"" + gradleCliPath + "\" -v",
                        RedirectStandardOutput = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };
                    using (var p = Process.Start(psi))
                    {
                        string output = p.StandardOutput.ReadToEnd();
                        p.WaitForExit(5000);
                        if (!string.IsNullOrEmpty(output))
                        {
                            string[] lines = output.Split(new char[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                            foreach (string line in lines)
                            {
                                if (line.StartsWith("Gradle ") || line.Contains("Gradle"))
                                {
                                    gradleCliVersion = line.Trim();
                                    break;
                                }
                            }
                        }
                    }
                }
                catch { }
            }
        }


        private static long GetDirSize(string path)
        {
            long size = 0;
            SafeGetDirSize(path, ref size);
            return size;
        }

        private static void SafeGetDirSize(string path, ref long size)
        {
            try
            {
                if (path.Length >= 248) return;
                foreach (string f in Directory.GetFiles(path, "*"))
                {
                    try { size += new FileInfo(f).Length; } catch {}
                }
                foreach (string d in Directory.GetDirectories(path))
                {
                    SafeGetDirSize(d, ref size);
                }
            }
            catch {}
        }

        private static void SafeGetFiles(string path, string pattern, System.Collections.Generic.List<string> result)
        {
            try
            {
                if (path.Length >= 248) return;
                foreach (string f in Directory.GetFiles(path, pattern))
                {
                    result.Add(f);
                }
                foreach (string d in Directory.GetDirectories(path))
                {
                    SafeGetFiles(d, pattern, result);
                }
            }
            catch {}
        }

        private static bool SafeHasFile(string path, string pattern)
        {
            try
            {
                if (path.Length >= 248) return false;
                if (Directory.GetFiles(path, pattern).Length > 0) return true;
                foreach (string d in Directory.GetDirectories(path))
                {
                    if (SafeHasFile(d, pattern)) return true;
                }
            }
            catch {}
            return false;
        }

        private static string SafeGetDirectoryName(string path)
        {
            if (string.IsNullOrEmpty(path)) return "";
            try
            {
                int idx = path.LastIndexOfAny(new char[] { '\\', '/' });
                if (idx > 0)
                {
                    return path.Substring(0, idx);
                }
            }
            catch {}
            return "";
        }


    private static void HandleRequest(Stream stdin, Stream stdout, byte[] payload)
    {
        Dictionary<string, object> head = json.DeserializeObject(Encoding.UTF8.GetString(payload)) as Dictionary<string, object>;
        int id = Convert.ToInt32(head["id"]);
        string method = Convert.ToString(head["method"]);
        string path = head.ContainsKey("path") ? Convert.ToString(head["path"]) : "";
        long bodyLen = head.ContainsKey("bodyLen") ? Convert.ToInt64(head["bodyLen"]) : 0;

        Dictionary<string, string> query = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (head.ContainsKey("query") && head["query"] is Dictionary<string, object>)
        {
            foreach (var kvp in (Dictionary<string, object>)head["query"])
            {
                query[kvp.Key] = Convert.ToString(kvp.Value);
            }
        }

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

        int status = 200;
        string contentType = "application/json; charset=utf-8";
        byte[] respBody = null;

        try
        {
            if (path.Length == 0)
            {
                // 主页面输出: 读 index.html 并替换模板占位符
                contentType = "text/html; charset=utf-8";
                string htmlPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "index.html");
                string html = File.ReadAllText(htmlPath, Encoding.UTF8);

                html = html.Replace("{GRADLE_BREADCRUMB_HOME}", T("gradle_breadcrumb_home"));
                html = html.Replace("{GRADLE_PAGE_TITLE}", T("gradle_page_title"));
                html = html.Replace("{GRADLE_BTN_CONFIG_DETAILS}", T("gradle_btn_config_details"));
                html = html.Replace("{GRADLE_MODAL_CONFIG_TITLE}", T("gradle_modal_config_title"));
                html = html.Replace("{GRADLE_CFG_SEC_RUNTIME}", T("gradle_cfg_sec_runtime"));
                html = html.Replace("{GRADLE_CFG_SEC_PATHS}", T("gradle_cfg_sec_paths"));
                html = html.Replace("{GRADLE_CFG_SEC_PROPS}", T("gradle_cfg_sec_props"));
                html = html.Replace("{LOBBY_PROTO_TOGGLE_TITLE}", T("lobby_proto_toggle_title"));
                html = html.Replace("{GRADLE_SEARCH_PLACEHOLDER}", T("gradle_search_placeholder"));
                html = html.Replace("{GRADLE_BTN_RESCAN}", T("gradle_btn_rescan"));
                html = html.Replace("{GRADLE_SUMMARY_TITLE}", T("gradle_summary_title"));
                html = html.Replace("{GRADLE_STAT_HOME}", T("gradle_stat_home"));
                html = html.Replace("{GRADLE_STAT_COUNT}", T("gradle_stat_count"));
                html = html.Replace("{GRADLE_STAT_KMP}", T("gradle_stat_kmp"));
                html = html.Replace("{GRADLE_STAT_SIZE}", T("gradle_stat_size"));
                html = html.Replace("{GRADLE_WRAPPERS_TITLE}", T("gradle_wrappers_title"));
                html = html.Replace("{GRADLE_WRAPPERS_SCANNING}", T("gradle_wrappers_scanning"));
                html = html.Replace("{GRADLE_LIST_TITLE}", T("gradle_list_title"));
                html = html.Replace("{GRADLE_TH_COORD}", T("gradle_th_coord"));
                html = html.Replace("{GRADLE_TH_VERSION}", T("gradle_th_version"));
                html = html.Replace("{GRADLE_TH_KMP}", T("gradle_th_kmp"));
                html = html.Replace("{GRADLE_TH_SIZE}", T("gradle_th_size"));
                html = html.Replace("{GRADLE_LOADING}", T("gradle_loading"));
                html = html.Replace("{GRADLE_PAGINATION_INFO}", T("gradle_pagination_info", 1, 1, 0));
                html = html.Replace("{GRADLE_PAGE_SIZE_LABEL}", T("gradle_page_size_label"));
                html = html.Replace("{GRADLE_PAGE_FIRST}", T("gradle_page_first"));
                html = html.Replace("{GRADLE_PAGE_PREV}", T("gradle_page_prev"));
                html = html.Replace("{GRADLE_PAGE_NEXT}", T("gradle_page_next"));
                html = html.Replace("{GRADLE_PAGE_LAST}", T("gradle_page_last"));
                html = html.Replace("{PREVIEW_BTN_EXPAND}", T("preview_btn_expand"));
                html = html.Replace("{GRADLE_DETAIL_TITLE}", T("gradle_detail_title"));
                html = html.Replace("{PREVIEW_BTN_COLLAPSE}", T("preview_btn_collapse"));
                html = html.Replace("{GRADLE_DETAIL_EMPTY}", T("gradle_detail_empty"));
                html = html.Replace("{GRADLE_MODAL_VERSIONS}", T("gradle_modal_versions"));
                html = html.Replace("{GRADLE_MODAL_DEPS}", T("gradle_modal_deps"));
                html = html.Replace("{GRADLE_MODAL_FILES}", T("gradle_modal_files"));
                html = html.Replace("{MODAL_BTN_OK}", T("modal_btn_ok"));

                respBody = Encoding.UTF8.GetBytes(html);
            }
            else if (path == "info")
            {
                string gHome = GetGradleHome();
                if (string.IsNullOrEmpty(gHome))
                {
                    status = 400;
                    respBody = Encoding.UTF8.GetBytes(string.Format("{{\"success\":false,\"message\":\"{0}\"}}", EscapeJson(T("api_gradle_no_root"))));
                }
                else
                {
                    bool scanning;
                    string wrappers;
                    int depCount;
                    int kmpCount;
                    long totalSize;
                    lock (gradleScanLock)
                    {
                        scanning = isGradleScanning;
                        wrappers = cachedWrappersJson;
                        depCount = cachedDependencyCount;
                        kmpCount = cachedKmpCount;
                        totalSize = cachedTotalSize;
                    }

                    string javaHome, javaVersion, javaPath;
                    DetectJavaRuntime(out javaHome, out javaVersion, out javaPath);

                    string gradleCliVersion, gradleCliPath;
                    DetectGradleCli(out gradleCliVersion, out gradleCliPath);

                    string cachesDir = Path.Combine(gHome, "caches\\modules-2\\files-2.1");
                    string wrapperDistsDir = Path.Combine(gHome, "wrapper\\dists");
                    string daemonDir = Path.Combine(gHome, "daemon");
                    string jdksDir = Path.Combine(gHome, "jdks");
                    string initDir = Path.Combine(gHome, "init.d");

                    string gradlePropertiesPath = Path.Combine(gHome, "gradle.properties");
                    if (!File.Exists(gradlePropertiesPath))
                    {
                        string userHomeProps = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".gradle\\gradle.properties");
                        if (File.Exists(userHomeProps)) gradlePropertiesPath = userHomeProps;
                    }

                    string gradlePropertiesContent = "";
                    Dictionary<string, string> propsDict = new Dictionary<string, string>();
                    if (File.Exists(gradlePropertiesPath))
                    {
                        try
                        {
                            gradlePropertiesContent = File.ReadAllText(gradlePropertiesPath, Encoding.UTF8);
                            string[] pLines = File.ReadAllLines(gradlePropertiesPath, Encoding.UTF8);
                            foreach (string pl in pLines)
                            {
                                string trim = pl.Trim();
                                if (string.IsNullOrEmpty(trim) || trim.StartsWith("#") || trim.StartsWith("!")) continue;
                                int eqIdx = trim.IndexOf('=');
                                if (eqIdx > 0)
                                {
                                    string pk = trim.Substring(0, eqIdx).Trim();
                                    string pv = trim.Substring(eqIdx + 1).Trim();
                                    if (!string.IsNullOrEmpty(pk) && !propsDict.ContainsKey(pk))
                                    {
                                        propsDict[pk] = pv;
                                    }
                                }
                            }
                        }
                        catch { }
                    }

                    StringBuilder sb = new StringBuilder();
                    sb.Append("{");
                    sb.Append("\"success\":true,");
                    sb.AppendFormat("\"gradleHome\":\"{0}\",", EscapeJson(gHome));
                    sb.AppendFormat("\"isScanning\":{0},", scanning ? "true" : "false");
                    sb.AppendFormat("\"wrappers\":{0},", wrappers);
                    sb.AppendFormat("\"dependencyCount\":{0},", depCount);
                    sb.AppendFormat("\"kmpCount\":{0},", kmpCount);
                    sb.AppendFormat("\"totalSize\":\"{0}\",", FormatFileSize(totalSize));
                    sb.AppendFormat("\"javaHome\":\"{0}\",", EscapeJson(javaHome));
                    sb.AppendFormat("\"javaVersion\":\"{0}\",", EscapeJson(javaVersion));
                    sb.AppendFormat("\"javaPath\":\"{0}\",", EscapeJson(javaPath));
                    sb.AppendFormat("\"gradleCliVersion\":\"{0}\",", EscapeJson(gradleCliVersion));
                    sb.AppendFormat("\"gradleCliPath\":\"{0}\",", EscapeJson(gradleCliPath));
                    sb.AppendFormat("\"cachesDir\":\"{0}\",", EscapeJson(cachesDir));
                    sb.AppendFormat("\"wrapperDistsDir\":\"{0}\",", EscapeJson(wrapperDistsDir));
                    sb.AppendFormat("\"daemonDir\":\"{0}\",", EscapeJson(daemonDir));
                    sb.AppendFormat("\"jdksDir\":\"{0}\",", EscapeJson(jdksDir));
                    sb.AppendFormat("\"initDir\":\"{0}\",", EscapeJson(initDir));
                    sb.AppendFormat("\"gradlePropertiesPath\":\"{0}\",", File.Exists(gradlePropertiesPath) ? EscapeJson(gradlePropertiesPath) : "");
                    sb.AppendFormat("\"gradlePropertiesContent\":\"{0}\",", EscapeJson(gradlePropertiesContent));
                    sb.Append("\"gradleProperties\":{");
                    int pCount = 0;
                    foreach (var kvp in propsDict)
                    {
                        if (pCount > 0) sb.Append(",");
                        sb.AppendFormat("\"{0}\":\"{1}\"", EscapeJson(kvp.Key), EscapeJson(kvp.Value));
                        pCount++;
                    }
                    sb.Append("}}");
                    respBody = Encoding.UTF8.GetBytes(sb.ToString());
                }
            }
            else if (path == "search")
            {
                string q = query.ContainsKey("q") ? (query["q"] ?? "").ToLower() : "";
                string gHome = GetGradleHome();
                if (string.IsNullOrEmpty(gHome))
                {
                    status = 400;
                    respBody = Encoding.UTF8.GetBytes(string.Format("{{\"success\":false,\"message\":\"{0}\"}}", EscapeJson(T("api_gradle_no_root"))));
                }
                else
                {
                    StringBuilder sb = new StringBuilder();
                    sb.Append("[");
                    List<CachedDependency> matches = new List<CachedDependency>();
                    lock (gradleScanLock)
                    {
                        foreach (var dep in cachedDependencies)
                        {
                            if (string.IsNullOrEmpty(q) || dep.Group.ToLower().Contains(q) || dep.Artifact.ToLower().Contains(q))
                            {
                                matches.Add(dep);
                            }
                        }
                    }
                    for (int i = 0; i < matches.Count; i++)
                    {
                        var dep = matches[i];
                        if (i > 0) sb.Append(",");
                        sb.AppendFormat("{{\"group\":\"{0}\",\"artifact\":\"{1}\",\"version\":\"{2}\",\"isKmp\":{3},\"size\":\"{4}\",\"path\":\"{5}\"}}",
                            EscapeJson(dep.Group), EscapeJson(dep.Artifact), EscapeJson(dep.Version), dep.IsKmp ? "true" : "false", dep.FriendlySize, EscapeJson(dep.LocalPath));
                    }
                    sb.Append("]");
                    respBody = Encoding.UTF8.GetBytes(string.Format("{{\"success\":true,\"results\":{0}}}", sb.ToString()));
                }
            }
            else if (path == "refresh")
            {
                bool alreadyScanning;
                lock (gradleScanLock)
                {
                    alreadyScanning = isGradleScanning;
                }
                if (alreadyScanning)
                {
                    status = 400;
                    respBody = Encoding.UTF8.GetBytes(string.Format("{{\"success\":false,\"message\":\"{0}\"}}", EscapeJson(T("api_gradle_scanning"))));
                }
                else
                {
                    TriggerGradleScanAsync(true);
                    respBody = Encoding.UTF8.GetBytes(string.Format("{{\"success\":true,\"message\":\"{0}\"}}", EscapeJson(T("api_gradle_scan_started"))));
                }
            }
            else if (path == "delete-wrapper")
            {
                string pathStr = query.ContainsKey("path") ? query["path"] : "";
                if (string.IsNullOrEmpty(pathStr))
                {
                    status = 400;
                    respBody = Encoding.UTF8.GetBytes("{\"success\":false,\"message\":\"" + EscapeJson(T("api_missing_path")) + "\"}");
                }
                else
                {
                    string gHome = GetGradleHome();
                    string fullPath = Path.GetFullPath(pathStr).ToLower();
                    string safeBase = Path.GetFullPath(Path.Combine(gHome, "wrapper\\dists")).ToLower();
                    if (!fullPath.StartsWith(safeBase))
                    {
                        status = 403;
                        respBody = Encoding.UTF8.GetBytes("{\"success\":false,\"message\":\"" + EscapeJson(T("api_gradle_illegal_wrapper_path")) + "\"}");
                    }
                    else if (Directory.Exists(pathStr))
                    {
                        Directory.Delete(pathStr, true);
                        TriggerGradleScanAsync(true);
                        respBody = Encoding.UTF8.GetBytes("{\"success\":true}");
                    }
                    else
                    {
                        status = 404;
                        respBody = Encoding.UTF8.GetBytes("{\"success\":false,\"message\":\"" + EscapeJson(T("api_gradle_wrapper_not_found")) + "\"}");
                    }
                }
            }
            else if (path == "wrapper-detail")
            {
                string ver = query.ContainsKey("version") ? query["version"] : "";
                string gHome = GetGradleHome();
                string distsDir = Path.Combine(gHome, "wrapper\\dists");
                string verDir = Path.Combine(distsDir, "gradle-" + ver + "-all");
                if (!Directory.Exists(verDir)) verDir = Path.Combine(distsDir, "gradle-" + ver + "-bin");

                if (!Directory.Exists(verDir))
                {
                    status = 404;
                    respBody = Encoding.UTF8.GetBytes("{\"success\":false,\"message\":\"" + EscapeJson(T("api_gradle_wrapper_ver_not_found")) + "\"}");
                }
                else
                {
                    string[] hashDirs = Directory.GetDirectories(verDir);
                    string hashFolder = hashDirs.Length > 0 ? Path.GetFileName(hashDirs[0]) : "";
                    string zipFile = Path.Combine(verDir, hashFolder, "gradle-" + ver + "-all.zip");
                    bool zipExists = File.Exists(zipFile);
                    if (!zipExists)
                    {
                        zipFile = Path.Combine(verDir, hashFolder, "gradle-" + ver + "-bin.zip");
                        zipExists = File.Exists(zipFile);
                    }
                    string unpackedFolder = Path.Combine(verDir, hashFolder, "gradle-" + ver);
                    StringBuilder subfoldersJson = new StringBuilder("[");
                    int subCount = 0;
                    if (Directory.Exists(unpackedFolder))
                    {
                        foreach (string sub in Directory.GetDirectories(unpackedFolder))
                        {
                            if (subCount > 0) subfoldersJson.Append(",");
                            subfoldersJson.Append("\"" + EscapeJson(Path.GetFileName(sub)) + "\"");
                            subCount++;
                        }
                    }
                    subfoldersJson.Append("]");

                    long totalBytes = 0;
                    int fileCount = 0;
                    try
                    {
                        foreach (string f in Directory.GetFiles(verDir, "*", SearchOption.AllDirectories))
                        {
                            totalBytes += new FileInfo(f).Length;
                            fileCount++;
                        }
                    }
                    catch { }

                    string resJson = string.Format("{{\"success\":true,\"version\":\"{0}\",\"absolutePath\":\"{1}\",\"hashFolder\":\"{2}\",\"zipFile\":\"{3}\",\"zipExists\":{4},\"unpackedFolder\":\"{5}\",\"subfolders\":{6},\"totalSize\":\"{7}\",\"totalFiles\":{8}}}",
                        EscapeJson(ver), EscapeJson(verDir.Replace("\\", "\\\\")), EscapeJson(hashFolder), EscapeJson(zipFile), zipExists ? "true" : "false", EscapeJson(unpackedFolder), subfoldersJson.ToString(), FormatFileSize(totalBytes), fileCount);
                    respBody = Encoding.UTF8.GetBytes(resJson);
                }
            }
            else if (path == "detail")
            {
                string group = query.ContainsKey("group") ? query["group"] : "";
                string name = query.ContainsKey("name") ? query["name"] : "";
                string version = query.ContainsKey("version") ? query["version"] : "";

                string gHome = GetGradleHome();
                string cachesDir = Path.Combine(gHome, "caches\\modules-2\\files-2.1");
                string versionDir = Path.Combine(cachesDir, group, name, version);

                if (!Directory.Exists(versionDir))
                {
                    status = 404;
                    respBody = Encoding.UTF8.GetBytes("{\"success\":false,\"message\":\"" + EscapeJson(T("api_gradle_dep_not_found")) + "\"}");
                }
                else
                {
                    string pomPath = "";
                    foreach (string f in Directory.GetFiles(versionDir, "*.pom", SearchOption.AllDirectories))
                    {
                        pomPath = f;
                        break;
                    }

                    long totalSize = 0;
                    foreach (string f in Directory.GetFiles(versionDir, "*", SearchOption.AllDirectories))
                    {
                        totalSize += new FileInfo(f).Length;
                    }

                    string license = "";
                    string organization = "";
                    string description = "";
                    StringBuilder depsJson = new StringBuilder("[");
                    int depIdx = 0;

                    if (File.Exists(pomPath))
                    {
                        try
                        {
                            XmlDocument doc = new XmlDocument();
                            doc.Load(pomPath);
                            XmlNamespaceManager nsmgr = new XmlNamespaceManager(doc.NameTable);
                            nsmgr.AddNamespace("m", doc.DocumentElement.NamespaceURI ?? "");

                            XmlNode licNode = doc.SelectSingleNode("//m:licenses/m:license/m:name", nsmgr) ?? doc.SelectSingleNode("//license/name");
                            if (licNode != null) license = licNode.InnerText.Trim();

                            XmlNode orgNode = doc.SelectSingleNode("//m:organization/m:name", nsmgr) ?? doc.SelectSingleNode("//organization/name");
                            if (orgNode != null) organization = orgNode.InnerText.Trim();

                            XmlNode descNode = doc.SelectSingleNode("//m:description", nsmgr) ?? doc.SelectSingleNode("//description");
                            if (descNode != null) description = descNode.InnerText.Trim();

                            XmlNodeList depNodes = doc.SelectNodes("//m:dependencies/m:dependency", nsmgr);
                            if (depNodes == null || depNodes.Count == 0) depNodes = doc.SelectNodes("//dependencies/dependency");
                            if (depNodes != null)
                            {
                                foreach (XmlNode dn in depNodes)
                                {
                                    string g = dn["groupId"] != null ? dn["groupId"].InnerText.Trim() : "";
                                    string a = dn["artifactId"] != null ? dn["artifactId"].InnerText.Trim() : "";
                                    string v = dn["version"] != null ? dn["version"].InnerText.Trim() : "";
                                    string s = dn["scope"] != null ? dn["scope"].InnerText.Trim() : "compile";

                                    if (!string.IsNullOrEmpty(g) && !string.IsNullOrEmpty(a))
                                    {
                                        string depDir = Path.Combine(cachesDir, g, a);
                                        bool isDownloaded = Directory.Exists(depDir);
                                        if (depIdx > 0) depsJson.Append(",");
                                        depsJson.AppendFormat("{{\"group\":\"{0}\",\"name\":\"{1}\",\"version\":\"{2}\",\"scope\":\"{3}\",\"cached\":{4}}}",
                                            EscapeJson(g), EscapeJson(a), EscapeJson(v), EscapeJson(s), isDownloaded ? "true" : "false");
                                        depIdx++;
                                    }
                                }
                            }
                        }
                        catch { }
                    }
                    depsJson.Append("]");

                    bool isKmp = false;
                    List<string> kmpTargets = new List<string>();
                    string[] moduleFiles = Directory.GetFiles(versionDir, "*.module", SearchOption.AllDirectories);
                    if (moduleFiles != null && moduleFiles.Length > 0 && File.Exists(moduleFiles[0]))
                    {
                        try
                        {
                            string moduleJson = File.ReadAllText(moduleFiles[0], Encoding.UTF8);
                            if (moduleJson.Contains("\"variants\""))
                            {
                                isKmp = true;
                                if (moduleJson.Contains("org.jetbrains.kotlin.platform.type"))
                                {
                                    string[] platforms = new string[] { "jvm", "androidJvm", "native", "js", "wasm" };
                                    foreach (string p in platforms)
                                    {
                                        if (moduleJson.Contains("\"" + p + "\"")) kmpTargets.Add(p);
                                    }
                                }
                            }
                        }
                        catch { }
                    }

                    StringBuilder kmpJson = new StringBuilder("[");
                    for (int i = 0; i < kmpTargets.Count; i++)
                    {
                        if (i > 0) kmpJson.Append(",");
                        kmpJson.Append("\"" + EscapeJson(kmpTargets[i]) + "\"");
                    }
                    kmpJson.Append("]");

                    string resJson = string.Format("{{\"success\":true,\"group\":\"{0}\",\"name\":\"{1}\",\"version\":\"{2}\",\"size\":\"{3}\",\"sizeBytes\":{4},\"license\":\"{5}\",\"organization\":\"{6}\",\"description\":\"{7}\",\"dependencies\":{8},\"isKmp\":{9},\"kmpTargets\":{10}}}",
                        EscapeJson(group), EscapeJson(name), EscapeJson(version), FormatFileSize(totalSize), totalSize, EscapeJson(license), EscapeJson(organization), EscapeJson(description), depsJson.ToString(), isKmp ? "true" : "false", kmpJson.ToString());
                    respBody = Encoding.UTF8.GetBytes(resJson);
                }
            }
            else if (path == "delete")
            {
                string group = query.ContainsKey("group") ? query["group"] : "";
                string name = query.ContainsKey("name") ? query["name"] : "";
                string version = query.ContainsKey("version") ? query["version"] : "";

                string gHome = GetGradleHome();
                string cachesDir = Path.Combine(gHome, "caches\\modules-2\\files-2.1");
                string versionDir = Path.Combine(cachesDir, group, name, version);

                if (Directory.Exists(versionDir))
                {
                    Directory.Delete(versionDir, true);
                    lock (gradleScanLock)
                    {
                        cachedDependencies.RemoveAll(delegate(CachedDependency d) {
                            return d.Group == group && d.Artifact == name && d.Version == version;
                        });
                        cachedDependencyCount = cachedDependencies.Count;
                    }
                    respBody = Encoding.UTF8.GetBytes("{\"success\":true}");
                }
                else
                {
                    status = 404;
                    respBody = Encoding.UTF8.GetBytes("{\"success\":false,\"message\":\"" + EscapeJson(T("api_gradle_dep_not_found")) + "\"}");
                }
            }
            else if (path == "version-files")
            {
                string group = query.ContainsKey("group") ? query["group"] : "";
                string name = query.ContainsKey("name") ? query["name"] : "";
                string version = query.ContainsKey("version") ? query["version"] : "";

                string gHome = GetGradleHome();
                string cachesDir = Path.Combine(gHome, "caches\\modules-2\\files-2.1");
                string versionDir = Path.Combine(cachesDir, group, name, version);

                if (Directory.Exists(versionDir))
                {
                    StringBuilder filesJson = new StringBuilder("[");
                    int fIdx = 0;
                    foreach (string f in Directory.GetFiles(versionDir, "*", SearchOption.AllDirectories))
                    {
                        FileInfo fi = new FileInfo(f);
                        if (fIdx > 0) filesJson.Append(",");
                        filesJson.AppendFormat("{{\"name\":\"{0}\",\"path\":\"{1}\",\"size\":\"{2}\",\"sizeBytes\":{3}}}",
                            EscapeJson(fi.Name), EscapeJson(f.Replace("\\", "\\\\")), FormatFileSize(fi.Length), fi.Length);
                        fIdx++;
                    }
                    filesJson.Append("]");
                    respBody = Encoding.UTF8.GetBytes(string.Format("{{\"success\":true,\"files\":{0}}}", filesJson.ToString()));
                }
                else
                {
                    status = 404;
                    respBody = Encoding.UTF8.GetBytes("{\"success\":false,\"message\":\"" + EscapeJson(T("api_gradle_dep_not_found")) + "\"}");
                }
            }
            else if (path == "open-path")
            {
                string p = query.ContainsKey("path") ? query["path"] : "";
                if (Directory.Exists(p) || File.Exists(p))
                {
                    Process.Start("explorer.exe", Directory.Exists(p) ? p : ("/select,\"" + p + "\""));
                    respBody = Encoding.UTF8.GetBytes("{\"success\":true}");
                }
                else
                {
                    status = 404;
                    respBody = Encoding.UTF8.GetBytes("{\"success\":false,\"message\":\"" + EscapeJson(T("api_path_not_found")) + "\"}");
                }
            }
            else if (path == "terminal")
            {
                string p = query.ContainsKey("path") ? query["path"] : "";
                string targetDir = Directory.Exists(p) ? p : Path.GetDirectoryName(p);
                if (!string.IsNullOrEmpty(targetDir) && Directory.Exists(targetDir))
                {
                    ProcessStartInfo psi = new ProcessStartInfo();
                    psi.WorkingDirectory = targetDir;
                    psi.FileName = "powershell.exe";
                    psi.UseShellExecute = true;
                    Process.Start(psi);
                    respBody = Encoding.UTF8.GetBytes("{\"success\":true}");
                }
                else
                {
                    status = 404;
                    respBody = Encoding.UTF8.GetBytes("{\"success\":false,\"message\":\"" + EscapeJson(T("api_path_not_found")) + "\"}");
                }
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
            respBody = Encoding.UTF8.GetBytes("{\"success\":false,\"error\":\"" + EscapeJson(ex.Message) + "\"}");
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
}
