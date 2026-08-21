using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;

namespace LocalDiskServer
{
    public class LanguageInfo
    {
        public string Code { get; set; }
        public string Name { get; set; }
        public string FilePath { get; set; }
    }

    public static class I18nManager
    {
        private static readonly Dictionary<string, string> currentStrings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, string> fallbackStrings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public static string CurrentLanguageCode { get; private set; }
        public static string CurrentLanguageName { get; private set; }
        public static string CurrentLanguage { get { return CurrentLanguageCode ?? "zh-CN"; } }

        public static event Action LanguageChanged;

        static I18nManager()
        {
            CurrentLanguageCode = "en-US";
            CurrentLanguageName = "English";

            // 预加载内置英文包作为终极 fallback 底座
            try
            {
                LoadEmbeddedLocale("en-US", fallbackStrings);
            }
            catch {}
        }

        public static void Initialize(string configuredLanguage)
        {
            try
            {
                // 1. 确保内部预置语言包释放到运行目录的 locales/ 文件夹中
                EnsureLocalesExtracted();

                // 2. 解析最终应生效的语言代码
                string targetLang = ResolveLanguage(configuredLanguage);

                // 3. 加载选定语言
                LoadLanguage(targetLang);
            }
            catch (Exception ex)
            {
                Logger.Log(T("log_i18n_init_ex", ex.Message));
            }
        }

        public static void EnsureLocalesExtracted()
        {
            try
            {
                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                string localesDir = Path.Combine(baseDir, "locales");
                if (!Directory.Exists(localesDir))
                {
                    Directory.CreateDirectory(localesDir);
                }

                // 预置语言资源清单
                string[] defaultLocales = new string[] { "zh-CN.ini", "en-US.ini" };

                foreach (string localeFile in defaultLocales)
                {
                    string targetPath = Path.Combine(localesDir, localeFile);
                    string langCode = Path.GetFileNameWithoutExtension(localeFile);
                    
                    // 读取内嵌最新内容
                    var embeddedDict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    LoadEmbeddedLocale(langCode, embeddedDict);

                    if (!File.Exists(targetPath))
                    {
                        // 首次释放
                        Assembly asm = Assembly.GetExecutingAssembly();
                        Stream stream = GetResourceStream("locales/" + localeFile);
                        if (stream != null)
                        {
                            using (stream)
                            using (FileStream fs = new FileStream(targetPath, FileMode.Create, FileAccess.Write))
                            {
                                stream.CopyTo(fs);
                            }
                            Logger.Log(T("log_i18n_extracted", targetPath));
                        }
                    }
                    else
                    {
                        // 已存在文件：检查是否有新增键值需要增量补充
                        try
                        {
                            var diskDict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                            ParseIniFile(targetPath, diskDict);

                            var missingKeys = new List<string>();
                            foreach (var kvp in embeddedDict)
                            {
                                if (!diskDict.ContainsKey(kvp.Key))
                                {
                                    missingKeys.Add(kvp.Key);
                                }
                            }

                            if (missingKeys.Count > 0)
                            {
                                StringBuilder sb = new StringBuilder();
                                sb.AppendLine();
                                sb.AppendLine("# --- Auto Increment Synced Translation Keys ---");
                                foreach (var key in missingKeys)
                                {
                                    string val = embeddedDict[key].Replace("\n", "\\n").Replace("\t", "\\t");
                                    sb.AppendLine(string.Format("{0}={1}", key, val));
                                }
                                File.AppendAllText(targetPath, sb.ToString(), Encoding.UTF8);
                                Logger.Log(T("log_i18n_keys_added", localeFile, missingKeys.Count));
                            }
                        }
                        catch (Exception ex)
                        {
                            Logger.Log(T("log_i18n_update_ex", ex.Message));
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log(T("log_i18n_extract_ex", ex.Message));
            }
        }

        public static List<LanguageInfo> GetAvailableLanguages()
        {
            List<LanguageInfo> list = new List<LanguageInfo>();
            try
            {
                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                string localesDir = Path.Combine(baseDir, "locales");
                if (Directory.Exists(localesDir))
                {
                    string[] files = Directory.GetFiles(localesDir, "*.ini");
                    foreach (string file in files)
                    {
                        string fileName = Path.GetFileNameWithoutExtension(file);
                        string langCode = fileName;
                        string langName = fileName;

                        // 快速解析文件头部的 language_code 和 language_name
                        try
                        {
                            string[] lines = File.ReadAllLines(file, Encoding.UTF8);
                            foreach (string line in lines)
                            {
                                string trimmed = line.Trim();
                                if (trimmed.StartsWith("#") || trimmed.StartsWith(";")) continue;
                                if (trimmed.StartsWith("language_code=", StringComparison.OrdinalIgnoreCase))
                                {
                                    langCode = trimmed.Substring(14).Trim();
                                }
                                else if (trimmed.StartsWith("language_name=", StringComparison.OrdinalIgnoreCase))
                                {
                                    langName = trimmed.Substring(14).Trim();
                                }
                            }
                        }
                        catch { }

                        list.Add(new LanguageInfo
                        {
                            Code = langCode,
                            Name = langName,
                            FilePath = file
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log(T("log_i18n_get_langs_ex", ex.Message));
            }

            if (list.Count == 0)
            {
                // 内存兜底
                list.Add(new LanguageInfo { Code = "en-US", Name = "English", FilePath = "" });
                list.Add(new LanguageInfo { Code = "zh-CN", Name = "简体中文", FilePath = "" });
            }

            return list;
        }

        public static string ResolveLanguage(string configuredLang)
        {
            List<LanguageInfo> available = GetAvailableLanguages();

            // 1. 如果有指定语言，且本地存在该语言配置文件，直接使用
            if (!string.IsNullOrEmpty(configuredLang))
            {
                foreach (LanguageInfo info in available)
                {
                    if (string.Equals(info.Code, configuredLang, StringComparison.OrdinalIgnoreCase))
                    {
                        return info.Code;
                    }
                }
            }

            // 2. 否则，探测系统当前语言
            try
            {
                CultureInfo uiCulture = CultureInfo.CurrentUICulture;
                string sysLangName = uiCulture.Name; // 如 zh-CN, en-US
                string sysTwoLetter = uiCulture.TwoLetterISOLanguageName; // 如 zh, en

                // 精确匹配
                foreach (LanguageInfo info in available)
                {
                    if (string.Equals(info.Code, sysLangName, StringComparison.OrdinalIgnoreCase))
                    {
                        return info.Code;
                    }
                }

                // 前缀/两字母匹配
                foreach (LanguageInfo info in available)
                {
                    if (info.Code.StartsWith(sysTwoLetter, StringComparison.OrdinalIgnoreCase))
                    {
                        return info.Code;
                    }
                }
            }
            catch { }

            // 3. 兜底回退：查看是否存在 en-US 或 en 语言包，否则返回列表第一个或 en-US
            foreach (LanguageInfo info in available)
            {
                if (string.Equals(info.Code, "en-US", StringComparison.OrdinalIgnoreCase) ||
                    info.Code.StartsWith("en", StringComparison.OrdinalIgnoreCase))
                {
                    return info.Code;
                }
            }

            return available.Count > 0 ? available[0].Code : "en-US";
        }

        public static bool LoadLanguage(string langCode)
        {
            try
            {
                Dictionary<string, string> dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                // 1. 先加载内嵌资源作为默认基底
                LoadEmbeddedLocale(langCode, dict);

                // 2. 再加载磁盘上的语言文件覆盖/补充
                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                string localesDir = Path.Combine(baseDir, "locales");
                string targetFile = Path.Combine(localesDir, langCode + ".ini");

                if (!File.Exists(targetFile) && Directory.Exists(localesDir))
                {
                    string[] files = Directory.GetFiles(localesDir, "*.ini");
                    foreach (string f in files)
                    {
                        if (string.Equals(Path.GetFileNameWithoutExtension(f), langCode, StringComparison.OrdinalIgnoreCase))
                        {
                            targetFile = f;
                            break;
                        }
                    }
                }

                if (File.Exists(targetFile))
                {
                    ParseIniFile(targetFile, dict);
                }

                if (dict.Count > 0)
                {
                    currentStrings.Clear();
                    foreach (KeyValuePair<string, string> kvp in dict)
                    {
                        currentStrings[kvp.Key] = kvp.Value;
                    }

                    CurrentLanguageCode = dict.ContainsKey("language_code") ? dict["language_code"] : langCode;
                    CurrentLanguageName = dict.ContainsKey("language_name") ? dict["language_name"] : langCode;

                    Logger.Log(T("log_i18n_loaded", CurrentLanguageName, CurrentLanguageCode, currentStrings.Count));

                    if (LanguageChanged != null)
                    {
                        LanguageChanged();
                    }
                    return true;
                }
            }
            catch (Exception ex)
            {
                Logger.Log(T("log_i18n_load_ex", langCode, ex.Message));
            }
            return false;
        }

        private static Stream GetResourceStream(string relativePath)
        {
            Assembly asm = Assembly.GetExecutingAssembly();
            Stream stream = asm.GetManifestResourceStream(relativePath);
            if (stream == null)
            {
                stream = asm.GetManifestResourceStream(relativePath.Replace('/', '.'));
            }
            if (stream == null)
            {
                string leaf = Path.GetFileName(relativePath);
                stream = asm.GetManifestResourceStream(leaf);
            }
            return stream;
        }

        private static void LoadEmbeddedLocale(string langCode, Dictionary<string, string> target)
        {
            try
            {
                string resourceName = "locales/" + langCode + ".ini";
                Stream stream = GetResourceStream(resourceName);
                if (stream != null)
                {
                    using (stream)
                    using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
                    {
                        ParseIniText(reader.ReadToEnd(), target);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log(T("log_i18n_load_embedded_ex", langCode, ex.Message));
            }
        }

        private static void ParseIniFile(string filePath, Dictionary<string, string> target)
        {
            string content = File.ReadAllText(filePath, Encoding.UTF8);
            ParseIniText(content, target);
        }

        private static void ParseIniText(string content, Dictionary<string, string> target)
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
                        string key = trimmed.Substring(0, idx).Trim();
                        string value = trimmed.Substring(idx + 1).Trim();
                        // 替换常见的转义字符如 \n
                        value = value.Replace("\\n", "\n").Replace("\\t", "\t");
                        target[key] = value;
                    }
                }
            }
        }

        public static string GetCurrentStringsJson()
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("{");
            bool first = true;
            // 综合 fallback 与 currentStrings
            var merged = new Dictionary<string, string>(fallbackStrings, StringComparer.OrdinalIgnoreCase);
            foreach (var kvp in currentStrings)
            {
                merged[kvp.Key] = kvp.Value;
            }

            foreach (var kvp in merged)
            {
                if (!first) sb.Append(",");
                first = false;
                string escapedVal = kvp.Value
                    .Replace("\\", "\\\\")
                    .Replace("\"", "\\\"")
                    .Replace("\r", "\\r")
                    .Replace("\n", "\\n")
                    .Replace("\t", "\\t");
                sb.AppendFormat("\"{0}\":\"{1}\"", kvp.Key, escapedVal);
            }
            sb.Append("}");
            return sb.ToString();
        }

        public static string T(string key, params object[] args)
        {
            string val = null;
            if (currentStrings.TryGetValue(key, out val))
            {
                if (args != null && args.Length > 0)
                {
                    try { return string.Format(val, args); } catch { return val; }
                }
                return val;
            }

            if (fallbackStrings.TryGetValue(key, out val))
            {
                if (args != null && args.Length > 0)
                {
                    try { return string.Format(val, args); } catch { return val; }
                }
                return val;
            }

            return key;
        }
    }
}
