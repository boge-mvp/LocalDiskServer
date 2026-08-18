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

        public static event Action LanguageChanged;

        static I18nManager()
        {
            CurrentLanguageCode = "en-US";
            CurrentLanguageName = "English";
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
                Logger.Log("I18nManager 初始化异常: " + ex.Message);
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
                Assembly asm = Assembly.GetExecutingAssembly();

                foreach (string localeFile in defaultLocales)
                {
                    string targetPath = Path.Combine(localesDir, localeFile);
                    if (!File.Exists(targetPath))
                    {
                        // 尝试从内嵌资源读取
                        string resourceName = "locales/" + localeFile;
                        Stream stream = asm.GetManifestResourceStream(resourceName);
                        if (stream == null)
                        {
                            // 尝试不带斜杠或点分割的名称
                            resourceName = "locales." + localeFile;
                            stream = asm.GetManifestResourceStream(resourceName);
                        }
                        if (stream == null)
                        {
                            resourceName = localeFile;
                            stream = asm.GetManifestResourceStream(resourceName);
                        }

                        if (stream != null)
                        {
                            using (stream)
                            using (FileStream fs = new FileStream(targetPath, FileMode.Create, FileAccess.Write))
                            {
                                stream.CopyTo(fs);
                            }
                            Logger.Log("已释放内嵌语言包到: " + targetPath);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Log("EnsureLocalesExtracted 异常: " + ex.Message);
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
                Logger.Log("GetAvailableLanguages 异常: " + ex.Message);
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
                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                string localesDir = Path.Combine(baseDir, "locales");
                string targetFile = Path.Combine(localesDir, langCode + ".ini");

                if (!File.Exists(targetFile))
                {
                    // 尝试匹配不区分大小写的文件名
                    if (Directory.Exists(localesDir))
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
                }

                Dictionary<string, string> dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                if (File.Exists(targetFile))
                {
                    ParseIniFile(targetFile, dict);
                }
                else
                {
                    // 尝试从内嵌资源读取
                    Assembly asm = Assembly.GetExecutingAssembly();
                    string resourceName = "locales/" + langCode + ".ini";
                    Stream stream = asm.GetManifestResourceStream(resourceName);
                    if (stream == null) stream = asm.GetManifestResourceStream("locales." + langCode + ".ini");
                    if (stream == null) stream = asm.GetManifestResourceStream(langCode + ".ini");

                    if (stream != null)
                    {
                        using (stream)
                        using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
                        {
                            ParseIniText(reader.ReadToEnd(), dict);
                        }
                    }
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

                    Logger.Log(string.Format("多语言加载成功: {0} ({1}), 翻译条目: {2}", CurrentLanguageName, CurrentLanguageCode, currentStrings.Count));

                    if (LanguageChanged != null)
                    {
                        LanguageChanged();
                    }
                    return true;
                }
            }
            catch (Exception ex)
            {
                Logger.Log("LoadLanguage 异常 (" + langCode + "): " + ex.Message);
            }
            return false;
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
