using System;
using System.IO;
using System.Net;
using System.Threading;
using System.Windows.Forms;
using System.Drawing;
using System.Diagnostics;
using Microsoft.Win32;
using System.Text;
using System.Collections.Generic;

namespace LocalDiskServer
{
    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            try
            {
                ServerApplicationContext.ParseCommandLineArgs(args);

                // 右键菜单唤起：若已有实例在运行，直接用浏览器打开目标目录后退出
                if (!string.IsNullOrEmpty(ServerApplicationContext.openTargetPath))
                {
                    // 第二进程即将退出，内存日志对其无效：日志落盘 launch_log.txt（先初始化语言保证日志语言一致）
                    I18nManager.Initialize(null);
                    int existingPort = ServerApplicationContext.QuickReadConfigPort();
                    if (ServerApplicationContext.IsPortAlive(existingPort))
                    {
                        string url = string.Format("http://localhost:{0}{1}", existingPort, ServerApplicationContext.BuildFolderUrl(ServerApplicationContext.openTargetPath));
                        try
                        {
                            Process.Start(url);
                            ServerApplicationContext.AppendLaunchLog(I18nManager.T("log_open_via_running_instance", existingPort, url));
                        }
                        catch (Exception ex)
                        {
                            ServerApplicationContext.AppendLaunchLog(I18nManager.T("log_open_browser_fail", ex.Message));
                        }
                        return;
                    }
                }
                if (!string.IsNullOrEmpty(ServerApplicationContext.openTargetInvalidPath))
                {
                    // 显式指定的打开目标目录无效：记录具体原因后忽略，继续正常启动
                    I18nManager.Initialize(null);
                    ServerApplicationContext.AppendLaunchLog(I18nManager.T("log_open_target_invalid", ServerApplicationContext.openTargetInvalidPath));
                }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new ServerApplicationContext());
            }
            catch (Exception ex)
            {
                try
                {
                    File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "crash.txt"), ex.ToString() + "\nStack: " + ex.StackTrace);
                }
                catch {}
            }
        }
    }

    public class ShellInfo
    {
        public string Name { get; set; }
        public string ExePath { get; set; }
    }

    public class ServerApplicationContext : ApplicationContext
    {
        public static readonly List<ShellInfo> availableShells = new List<ShellInfo>();
        
        public const string APP_VERSION = "1.1.0";

        public static NotifyIcon trayIcon;
        public static MenuItem versionMenuItem;
        public static MenuItem statusMenuItem;
        public static MenuItem openHomeMenuItem;
        public static MenuItem openConfigFileMenuItem;
        public static MenuItem openAppDirMenuItem;
        public static MenuItem viewLogsMenuItem;
        public static MenuItem configTextExtMenuItem;
        public static MenuItem plainPortMenuItem;
        public static MenuItem sslPortMenuItem;
        public static MenuItem httpsToggleMenuItem;
        public static MenuItem devEcosystemMenuItem;
        public static MenuItem languageSubMenu;
        public static MenuItem shellMenuMenuItem;
        public static MenuItem classicMenuMenuItem;
        public static MenuItem startupMenuItem;
        public static MenuItem exitMenuItem;

        // 持久化控制与证书状态变量
        public static int port = 1234;
        public static int https_port = 1235;
        public static bool use_https = false;
        public static bool enable_dev_ecosystem = false;
        public static string ssl_hash = "";
        public static int last_bound_https_port = 1235;
        public static string language = "";

        public static string configFile = "server_config.ini";
        public static string textExtensionsStr = "txt,md,log,ini,conf,cfg,json,js,css,xml,bat,sh,py,java,cs,go,rs,cpp,h,c,properties,yaml,yml,sql,ts";
        public static string favoritesStr = "";

        // 命令行控制与测试模式变量
        public static bool isTestMode = false;
        public static bool noBrowser = false;
        public static int? overridePort = null;
        public static int? overrideHttpsPort = null;
        public static string openTargetPath = null;
        public static string openTargetInvalidPath = null;

        public static void ParseCommandLineArgs(string[] args)
        {
            if (args == null || args.Length == 0) return;

            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i];
                if (string.Equals(arg, "--test", StringComparison.OrdinalIgnoreCase) || 
                    string.Equals(arg, "-test", StringComparison.OrdinalIgnoreCase))
                {
                    isTestMode = true;
                    configFile = "server_config_test.ini";
                    port = 18080;
                    https_port = 18443;
                    last_bound_https_port = 18443;
                }
                else if (string.Equals(arg, "--no-browser", StringComparison.OrdinalIgnoreCase))
                {
                    noBrowser = true;
                }
                else if ((string.Equals(arg, "--port", StringComparison.OrdinalIgnoreCase) || string.Equals(arg, "-p", StringComparison.OrdinalIgnoreCase)) && i + 1 < args.Length)
                {
                    int p;
                    if (int.TryParse(args[++i], out p))
                    {
                        overridePort = p;
                    }
                }
                else if ((string.Equals(arg, "--https-port", StringComparison.OrdinalIgnoreCase) || string.Equals(arg, "-sp", StringComparison.OrdinalIgnoreCase)) && i + 1 < args.Length)
                {
                    int p;
                    if (int.TryParse(args[++i], out p))
                    {
                        overrideHttpsPort = p;
                    }
                }
                else if ((string.Equals(arg, "--open", StringComparison.OrdinalIgnoreCase) || string.Equals(arg, "-open", StringComparison.OrdinalIgnoreCase)) && i + 1 < args.Length)
                {
                    string p = args[++i];
                    if (Directory.Exists(p) || File.Exists(p))
                    {
                        openTargetPath = Path.GetFullPath(p);
                    }
                    else
                    {
                        openTargetInvalidPath = p;
                    }
                }
                else if (!arg.StartsWith("-") && !arg.StartsWith("/") && (Directory.Exists(arg) || File.Exists(arg)))
                {
                    // 兼容直接位置参数（右键菜单 verb 传参场景）
                    openTargetPath = Path.GetFullPath(arg);
                }
            }
        }

        public class CachedDependency
        {
            public string Group { get; set; }
            public string Artifact { get; set; }
            public string Version { get; set; }
            public bool IsKmp { get; set; }
            public string FriendlySize { get; set; }
            public string LocalPath { get; set; }
        }

        public ServerApplicationContext()
        {
            DetectAvailableShells();
            LoadConfig();
            
            // 初始化多语言体系并订阅语言变更通知
            I18nManager.Initialize(language);
            I18nManager.LanguageChanged += OnLanguageChanged;

            InitTrayIcon();

            // 启动标准多线程 HTTP 服务器
            HttpServer.StartServer();

            // 右键菜单唤起：服务启动后自动打开目标目录
            if (!string.IsNullOrEmpty(openTargetPath))
            {
                try
                {
                    Log(I18nManager.T("log_open_target", openTargetPath));
                    Process.Start(string.Format("http://localhost:{0}{1}", port, BuildFolderUrl(openTargetPath)));
                }
                catch (Exception ex)
                {
                    Log(I18nManager.T("log_open_browser_fail", ex.Message));
                }
            }

            // 若启用了开发者生态管理，才启动后台线程异步扫描
            if (enable_dev_ecosystem)
            {
                GradleExplorer.TriggerGradleScanAsync();
                NpmExplorer.TriggerNpmScanAsync();
                PnpmExplorer.TriggerPnpmScanAsync();
                MavenExplorer.TriggerMavenScanAsync();
            }
        }

        public static void Log(string msg)
        {
            Logger.Log(msg);
        }

        private void DetectAvailableShells()
        {
            availableShells.Clear();

            // 1. Windows Terminal (wt.exe)
            string wtPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Microsoft\WindowsApps\wt.exe");
            if (File.Exists(wtPath))
            {
                availableShells.Add(new ShellInfo { Name = "Windows Terminal", ExePath = wtPath });
            }

            // 2. PowerShell 7 (pwsh.exe)
            string pwshPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"PowerShell\7\pwsh.exe");
            if (File.Exists(pwshPath))
            {
                availableShells.Add(new ShellInfo { Name = "PowerShell 7", ExePath = pwshPath });
            }
            else
            {
                string pwshPathX86 = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"PowerShell\7\pwsh.exe");
                if (File.Exists(pwshPathX86))
                {
                    availableShells.Add(new ShellInfo { Name = "PowerShell 7", ExePath = pwshPathX86 });
                }
            }

            // 3. Git Bash
            string gitBash1 = @"C:\Program Files\Git\bin\bash.exe";
            string gitBash2 = @"C:\Program Files\Git\git-bash.exe";
            if (File.Exists(gitBash1))
            {
                availableShells.Add(new ShellInfo { Name = "Git Bash", ExePath = gitBash1 });
            }
            else if (File.Exists(gitBash2))
            {
                availableShells.Add(new ShellInfo { Name = "Git Bash", ExePath = gitBash2 });
            }

            // 4. Windows PowerShell (powershell.exe)
            string psPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe");
            if (File.Exists(psPath))
            {
                availableShells.Add(new ShellInfo { Name = "PowerShell", ExePath = psPath });
            }

            // 5. Command Prompt (cmd.exe)
            string cmdPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
            if (File.Exists(cmdPath))
            {
                availableShells.Add(new ShellInfo { Name = "Command Prompt", ExePath = cmdPath });
            }

            // Fallback
            if (availableShells.Count == 0)
            {
                availableShells.Add(new ShellInfo { Name = "Command Prompt", ExePath = "cmd.exe" });
            }
        }

        private void LoadConfig()
        {
            try
            {
                string exeDir = AppDomain.CurrentDomain.BaseDirectory;
                string configPath = Path.Combine(exeDir, configFile);
                if (File.Exists(configPath))
                {
                    string[] lines = File.ReadAllLines(configPath);
                    foreach (string line in lines)
                    {
                        if (line.StartsWith("port=", StringComparison.OrdinalIgnoreCase))
                        {
                            string portStr = line.Substring(5).Trim();
                            int parsedPort;
                            if (int.TryParse(portStr, out parsedPort))
                            {
                                port = parsedPort;
                            }
                        }
                        else if (line.StartsWith("https_port=", StringComparison.OrdinalIgnoreCase))
                        {
                            string httpsPortStr = line.Substring(11).Trim();
                            int parsedPort;
                            if (int.TryParse(httpsPortStr, out parsedPort))
                            {
                                https_port = parsedPort;
                            }
                        }
                        else if (line.StartsWith("use_https=", StringComparison.OrdinalIgnoreCase))
                        {
                            string useHttpsStr = line.Substring(10).Trim();
                            bool parsedUseHttps;
                            if (bool.TryParse(useHttpsStr, out parsedUseHttps))
                            {
                                use_https = parsedUseHttps;
                            }
                        }
                        else if (line.StartsWith("ssl_hash=", StringComparison.OrdinalIgnoreCase))
                        {
                            ssl_hash = line.Substring(9).Trim();
                        }
                        else if (line.StartsWith("last_bound_https_port=", StringComparison.OrdinalIgnoreCase))
                        {
                            string lastBoundPortStr = line.Substring(22).Trim();
                            int parsedPort;
                            if (int.TryParse(lastBoundPortStr, out parsedPort))
                            {
                                last_bound_https_port = parsedPort;
                            }
                        }
                        else if (line.StartsWith("text_extensions=", StringComparison.OrdinalIgnoreCase))
                        {
                            textExtensionsStr = line.Substring(16).Trim();
                        }
                        else if (line.StartsWith("favorites=", StringComparison.OrdinalIgnoreCase))
                        {
                            favoritesStr = line.Substring(10).Trim();
                        }
                        else if (line.StartsWith("language=", StringComparison.OrdinalIgnoreCase))
                        {
                            language = line.Substring(9).Trim();
                        }
                        else if (line.StartsWith("enable_dev_ecosystem=", StringComparison.OrdinalIgnoreCase))
                        {
                            string devEcoStr = line.Substring(21).Trim();
                            bool parsedEco;
                            if (bool.TryParse(devEcoStr, out parsedEco))
                            {
                                enable_dev_ecosystem = parsedEco;
                            }
                        }
                    }
                    Log(I18nManager.T("log_config_loaded", port, https_port, string.IsNullOrEmpty(language) ? I18nManager.T("common_auto_match") : language));
                }
                else
                {
                    SaveConfig();
                }

                // 命令行优先级高于配置文件
                if (overridePort.HasValue)
                {
                    port = overridePort.Value;
                    Log(I18nManager.T("log_cmd_override_http", port));
                }
                if (overrideHttpsPort.HasValue)
                {
                    https_port = overrideHttpsPort.Value;
                    Log(I18nManager.T("log_cmd_override_https", https_port));
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(I18nManager.T("dialog_load_config_fail", ex.Message), I18nManager.T("dialog_warning"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        public static string GetConfigFilePath()
        {
            return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, configFile);
        }

        public void SaveConfig()
        {
            SaveConfigStatic();
        }

        public static void SaveConfigStatic()
        {
            try
            {
                string configPath = GetConfigFilePath();
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("port=" + port);
                sb.AppendLine("https_port=" + https_port);
                sb.AppendLine("use_https=" + use_https);
                sb.AppendLine("ssl_hash=" + ssl_hash);
                sb.AppendLine("last_bound_https_port=" + last_bound_https_port);
                sb.AppendLine("text_extensions=" + textExtensionsStr);
                sb.AppendLine("favorites=" + favoritesStr);
                sb.AppendLine("language=" + (language ?? ""));
                sb.AppendLine("enable_dev_ecosystem=" + enable_dev_ecosystem);
                File.WriteAllText(configPath, sb.ToString());
                Log(I18nManager.T("log_config_saved"));
            }
            catch (Exception ex)
            {
                MessageBox.Show(I18nManager.T("dialog_save_config_fail", ex.Message), I18nManager.T("dialog_warning"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void InitTrayIcon()
        {
            ContextMenu trayMenu = new ContextMenu();

            versionMenuItem = new MenuItem(string.Format("LocalDiskServer v{0}", APP_VERSION));
            versionMenuItem.Enabled = false;
            trayMenu.MenuItems.Add(versionMenuItem);

            statusMenuItem = new MenuItem(I18nManager.T("menu_status_stopped"));
            statusMenuItem.Enabled = false;
            trayMenu.MenuItems.Add(statusMenuItem);

            trayMenu.MenuItems.Add(new MenuItem("-"));

            openHomeMenuItem = new MenuItem(I18nManager.T("menu_open_home"), OpenBrowser);
            openConfigFileMenuItem = new MenuItem(I18nManager.T("menu_open_config"), OpenConfigFile);
            openAppDirMenuItem = new MenuItem(I18nManager.T("menu_open_app_dir"), OpenAppDirectory);
            viewLogsMenuItem = new MenuItem(I18nManager.T("menu_view_logs"), OpenLogs);
            configTextExtMenuItem = new MenuItem(I18nManager.T("menu_config_text_ext"), ChangeTextExtensions);

            trayMenu.MenuItems.Add(openHomeMenuItem);
            trayMenu.MenuItems.Add(openConfigFileMenuItem);
            trayMenu.MenuItems.Add(openAppDirMenuItem);
            trayMenu.MenuItems.Add(viewLogsMenuItem);
            trayMenu.MenuItems.Add(configTextExtMenuItem);

            plainPortMenuItem = new MenuItem(I18nManager.T("menu_config_plain_port", port), ChangePort);
            sslPortMenuItem = new MenuItem(I18nManager.T("menu_config_ssl_port", https_port), ChangeHttpsPort);
            httpsToggleMenuItem = new MenuItem(I18nManager.T("menu_toggle_https"), ToggleHttps);
            httpsToggleMenuItem.Checked = use_https;
            
            trayMenu.MenuItems.Add(plainPortMenuItem);
            trayMenu.MenuItems.Add(sslPortMenuItem);
            trayMenu.MenuItems.Add(httpsToggleMenuItem);

            devEcosystemMenuItem = new MenuItem(I18nManager.T("menu_dev_ecosystem"), ToggleDevEcosystem);
            devEcosystemMenuItem.Checked = enable_dev_ecosystem;
            trayMenu.MenuItems.Add(devEcosystemMenuItem);

            // 动态构建多语言二级子菜单
            languageSubMenu = new MenuItem(I18nManager.T("menu_language"));
            BuildLanguageSubMenu();
            trayMenu.MenuItems.Add(languageSubMenu);

            shellMenuMenuItem = new MenuItem(I18nManager.T("menu_shell_menu"), ToggleShellMenu);
            shellMenuMenuItem.Checked = IsShellMenuRegistered();
            trayMenu.MenuItems.Add(shellMenuMenuItem);
            classicMenuMenuItem = new MenuItem(I18nManager.T("menu_classic_menu"), ToggleClassicMenu);
            classicMenuMenuItem.Checked = IsClassicMenuEnabled();
            trayMenu.MenuItems.Add(classicMenuMenuItem);

            startupMenuItem = new MenuItem(I18nManager.T("menu_startup"), ToggleStartup);
            startupMenuItem.Checked = IsStartupEnabled();
            trayMenu.MenuItems.Add(startupMenuItem);

            trayMenu.MenuItems.Add(new MenuItem("-"));
            exitMenuItem = new MenuItem(I18nManager.T("menu_exit"), Exit);
            trayMenu.MenuItems.Add(exitMenuItem);

            Icon appIcon = null;
            try
            {
                using (Bitmap bmp = new Bitmap(16, 16))
                {
                    using (Graphics g = Graphics.FromImage(bmp))
                    {
                        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                        g.Clear(Color.Transparent);
                        g.FillEllipse(new SolidBrush(Color.FromArgb(41, 128, 185)), 1, 1, 14, 14);
                        g.DrawEllipse(new Pen(Color.White, 1.5f), 1, 1, 14, 14);
                        g.FillEllipse(Brushes.White, 5, 5, 6, 6);
                    }
                    appIcon = Icon.FromHandle(bmp.GetHicon());
                }
            }
            catch
            {
                appIcon = SystemIcons.Application;
            }

            trayIcon = new NotifyIcon()
            {
                Icon = appIcon,
                ContextMenu = trayMenu,
                Text = I18nManager.T("tray_tooltip"),
                Visible = true
            };

            trayIcon.DoubleClick += OpenBrowser;
        }

        private static void BuildLanguageSubMenu()
        {
            if (languageSubMenu == null) return;
            languageSubMenu.MenuItems.Clear();

            List<LanguageInfo> languages = I18nManager.GetAvailableLanguages();
            string currentCode = I18nManager.CurrentLanguageCode;

            foreach (LanguageInfo lang in languages)
            {
                string targetCode = lang.Code;
                MenuItem langItem = new MenuItem(lang.Name, (s, e) =>
                {
                    SetLanguage(targetCode);
                });

                if (string.Equals(targetCode, currentCode, StringComparison.OrdinalIgnoreCase))
                {
                    langItem.Checked = true;
                }

                languageSubMenu.MenuItems.Add(langItem);
            }
        }

        public static void SetLanguage(string langCode)
        {
            if (I18nManager.LoadLanguage(langCode))
            {
                language = langCode;
                SaveConfigStatic();
                UpdateMenuTexts();
            }
        }

        private static void OnLanguageChanged()
        {
            UpdateMenuTexts();
        }

        public static void UpdateMenuTexts()
        {
            if (versionMenuItem != null) versionMenuItem.Text = string.Format("LocalDiskServer v{0}", APP_VERSION);
            if (openHomeMenuItem != null) openHomeMenuItem.Text = I18nManager.T("menu_open_home");
            if (openConfigFileMenuItem != null) openConfigFileMenuItem.Text = I18nManager.T("menu_open_config");
            if (openAppDirMenuItem != null) openAppDirMenuItem.Text = I18nManager.T("menu_open_app_dir");
            if (viewLogsMenuItem != null) viewLogsMenuItem.Text = I18nManager.T("menu_view_logs");
            if (configTextExtMenuItem != null) configTextExtMenuItem.Text = I18nManager.T("menu_config_text_ext");
            if (plainPortMenuItem != null) plainPortMenuItem.Text = I18nManager.T("menu_config_plain_port", port);
            if (sslPortMenuItem != null) sslPortMenuItem.Text = I18nManager.T("menu_config_ssl_port", https_port);
            if (httpsToggleMenuItem != null)
            {
                httpsToggleMenuItem.Text = I18nManager.T("menu_toggle_https");
                httpsToggleMenuItem.Checked = use_https;
            }
            if (devEcosystemMenuItem != null)
            {
                devEcosystemMenuItem.Text = I18nManager.T("menu_dev_ecosystem");
                devEcosystemMenuItem.Checked = enable_dev_ecosystem;
            }
            if (languageSubMenu != null)
            {
                languageSubMenu.Text = I18nManager.T("menu_language");
                BuildLanguageSubMenu();
            }
            if (shellMenuMenuItem != null)
            {
                shellMenuMenuItem.Text = I18nManager.T("menu_shell_menu");
                shellMenuMenuItem.Checked = IsShellMenuRegistered();
            }
            if (classicMenuMenuItem != null)
            {
                classicMenuMenuItem.Text = I18nManager.T("menu_classic_menu");
                classicMenuMenuItem.Checked = IsClassicMenuEnabled();
            }
            if (startupMenuItem != null) startupMenuItem.Text = I18nManager.T("menu_startup");
            if (exitMenuItem != null) exitMenuItem.Text = I18nManager.T("menu_exit");

            if (trayIcon != null)
            {
                trayIcon.Text = I18nManager.T("tray_tooltip");
            }

            // 更新服务运行状态菜单项
            if (statusMenuItem != null)
            {
                if (HttpServer.listener != null && HttpServer.listener.IsListening)
                {
                    string status = string.Format("http://localhost:{0}", port);
                    if (use_https && HttpServer.httpsListener != null && HttpServer.httpsListener.IsListening)
                    {
                        status += string.Format(" & https://localhost:{0}", https_port);
                    }
                    statusMenuItem.Text = I18nManager.T("menu_status_running", status);
                }
                else
                {
                    statusMenuItem.Text = I18nManager.T("menu_status_stopped");
                }
            }
        }

        private void OpenLogs(object sender, EventArgs e)
        {
            try
            {
                string logUrl = string.Format("http://localhost:{0}/api/logs", port);
                Process.Start(logUrl);
            }
            catch (Exception ex)
            {
                MessageBox.Show(I18nManager.T("dialog_open_logs_fail", ex.Message), I18nManager.T("dialog_error"), MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void OpenBrowser(object sender, EventArgs e)
        {
            try
            {
                string url = string.Format("http://localhost:{0}/", port);
                Process.Start(url);
            }
            catch (Exception ex)
            {
                MessageBox.Show(I18nManager.T("dialog_open_browser_fail", ex.Message), I18nManager.T("dialog_error"), MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void OpenConfigFile(object sender, EventArgs e)
        {
            string configPath = GetConfigFilePath();
            if (!File.Exists(configPath))
            {
                SaveConfig();
            }
            try
            {
                Process.Start(new ProcessStartInfo(configPath) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show(I18nManager.T("dialog_open_file_fail", ex.Message), I18nManager.T("dialog_warning"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void OpenAppDirectory(object sender, EventArgs e)
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            try
            {
                Process.Start(new ProcessStartInfo("explorer.exe", "\"" + baseDir + "\"") { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show(I18nManager.T("dialog_open_dir_fail", ex.Message), I18nManager.T("dialog_warning"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void ChangePort(object sender, EventArgs e)
        {
            string input = ShowInputDialog(I18nManager.T("dialog_port_title"), I18nManager.T("dialog_port_prompt"), port.ToString());
            if (string.IsNullOrEmpty(input)) return;
            int newPort;
            if (int.TryParse(input, out newPort) && newPort >= 1 && newPort <= 65535)
            {
                if (newPort != port)
                {
                    port = newPort;
                    SaveConfig();
                    Log(I18nManager.T("log_restarting_http", port));
                    HttpServer.StartServer();
                    UpdateMenuTexts();
                }
            }
            else
            {
                MessageBox.Show(I18nManager.T("dialog_port_invalid"), I18nManager.T("dialog_tip"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void ChangeHttpsPort(object sender, EventArgs e)
        {
            string input = ShowInputDialog(I18nManager.T("dialog_https_port_title"), I18nManager.T("dialog_https_port_prompt"), https_port.ToString());
            if (string.IsNullOrEmpty(input)) return;
            int newPort;
            if (int.TryParse(input, out newPort) && newPort >= 1 && newPort <= 65535)
            {
                if (newPort != https_port)
                {
                    int oldPort = last_bound_https_port;
                    https_port = newPort;
                    SaveConfig();
                    
                    if (use_https)
                    {
                        Log(I18nManager.T("log_rebinding_https", oldPort, newPort));
                        SslManager.BindSslCertificate(newPort, oldPort);
                    }
                    
                    Log(I18nManager.T("log_restarting_https", https_port));
                    HttpServer.StartServer();
                    UpdateMenuTexts();
                }
            }
            else
            {
                MessageBox.Show(I18nManager.T("dialog_port_invalid"), I18nManager.T("dialog_tip"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void ToggleHttps(object sender, EventArgs e)
        {
            bool target = !use_https;
            if (target)
            {
                // 开启 HTTPS
                Log(I18nManager.T("log_enabling_https"));
                SslManager.BindSslCertificate(https_port, 0);
                use_https = true;
            }
            else
            {
                // 关闭 HTTPS
                Log(I18nManager.T("log_disabling_https", https_port));
                SslManager.UnbindSslCertificate(https_port);
                use_https = false;
            }

            SaveConfig();
            HttpServer.StartServer();
            UpdateMenuTexts();
            Log(I18nManager.T("log_https_updated"));
        }

        private void ChangeTextExtensions(object sender, EventArgs e)
        {
            string[] exts = (textExtensionsStr ?? "").Split(new char[] { ',', '，', ';', '；', '\r', '\n', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            string defaultDisplay = string.Join(", ", exts);

            string input = ShowInputDialog(I18nManager.T("dialog_text_ext_title"), I18nManager.T("dialog_text_ext_prompt"), defaultDisplay, true);
            if (input == null) return;

            string[] rawParts = input.Split(new char[] { ',', '，', ';', '；', '\r', '\n', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            List<string> cleanList = new List<string>();
            foreach (string raw in rawParts)
            {
                string trimmed = raw.Trim().TrimStart('.').ToLower();
                if (!string.IsNullOrEmpty(trimmed) && !cleanList.Contains(trimmed))
                {
                    cleanList.Add(trimmed);
                }
            }

            textExtensionsStr = string.Join(",", cleanList.ToArray());
            SaveConfig();
            Log(I18nManager.T("log_text_ext_updated"));
        }

        private void ToggleStartup(object sender, EventArgs e)
        {
            bool current = IsStartupEnabled();
            bool target = !current;
            if (SetStartup(target))
            {
                startupMenuItem.Checked = target;
                Log(I18nManager.T("log_startup_updated", target ? I18nManager.T("common_enabled") : I18nManager.T("common_disabled")));
            }
        }

        private bool IsStartupEnabled()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", false))
                {
                    if (key == null) return false;
                    object val = key.GetValue("LocalDiskServer");
                    if (val == null) return false;
                    return val.ToString().Equals(Application.ExecutablePath, StringComparison.OrdinalIgnoreCase);
                }
            }
            catch { return false; }
        }

        private bool SetStartup(bool enable)
        {
            return SetStartupStatic(enable);
        }

        public static bool IsStartupEnabledStatic()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", false))
                {
                    if (key == null) return false;
                    object val = key.GetValue("LocalDiskServer");
                    if (val == null) return false;
                    return val.ToString().Equals(Application.ExecutablePath, StringComparison.OrdinalIgnoreCase);
                }
            }
            catch { return false; }
        }

        public static bool SetStartupStatic(bool enable)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true))
                {
                    if (key == null) return false;
                    if (enable)
                    {
                        key.SetValue("LocalDiskServer", Application.ExecutablePath);
                    }
                    else
                    {
                        key.DeleteValue("LocalDiskServer", false);
                    }
                    if (startupMenuItem != null) startupMenuItem.Checked = enable;
                    return true;
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(I18nManager.T("dialog_startup_fail", ex.Message), I18nManager.T("dialog_tip"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }
        }

        // ==================== 文件夹右键菜单集成 ====================

        public static void AppendLaunchLog(string message)
        {
            try
            {
                string logPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "launch_log.txt");
                if (File.Exists(logPath) && new FileInfo(logPath).Length > 1024 * 1024)
                {
                    File.WriteAllText(logPath, "");
                }
                File.AppendAllText(logPath, string.Format("[{0:yyyy-MM-dd HH:mm:ss}] {1}\r\n", DateTime.Now, message));
            }
            catch { }
        }

        public static string BuildFolderUrl(string dir)
        {
            if (string.IsNullOrEmpty(dir)) return "/";
            try
            {
                string fullPath = Path.GetFullPath(dir);
                string root = Path.GetPathRoot(fullPath);
                if (string.IsNullOrEmpty(root) || root.Length < 2 || !char.IsLetter(root[0])) return "/";

                StringBuilder sb = new StringBuilder();
                sb.Append('/').Append(char.ToUpper(root[0]));

                string rest = fullPath.Substring(root.Length).TrimEnd('\\', '/');
                if (!string.IsNullOrEmpty(rest))
                {
                    string[] segments = rest.Split('\\', '/');
                    foreach (string seg in segments)
                    {
                        if (string.IsNullOrEmpty(seg)) continue;
                        sb.Append('/').Append(Uri.EscapeDataString(seg));
                    }
                }
                if (File.Exists(fullPath))
                {
                    return sb.ToString();
                }
                sb.Append('/');
                return sb.ToString();
            }
            catch { return "/"; }
        }

        public static int QuickReadConfigPort()
        {
            try
            {
                string configPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, configFile);
                if (File.Exists(configPath))
                {
                    string[] lines = File.ReadAllLines(configPath);
                    foreach (string line in lines)
                    {
                        if (line.StartsWith("port=", StringComparison.OrdinalIgnoreCase))
                        {
                            int p;
                            if (int.TryParse(line.Substring(5).Trim(), out p)) return p;
                        }
                    }
                }
            }
            catch { }
            return port;
        }

        public static bool IsPortAlive(int targetPort)
        {
            try
            {
                using (System.Net.Sockets.TcpClient client = new System.Net.Sockets.TcpClient())
                {
                    IAsyncResult result = client.BeginConnect("127.0.0.1", targetPort, null, null);
                    bool connected = result.AsyncWaitHandle.WaitOne(400);
                    if (connected)
                    {
                        client.EndConnect(result);
                        return true;
                    }
                    return false;
                }
            }
            catch { return false; }
        }

        public static bool IsShellMenuRegistered()
        {
            try
            {
                using (RegistryKey dirKey = Registry.CurrentUser.OpenSubKey(@"Software\Classes\Directory\shell\LocalDiskServer", false))
                {
                    if (dirKey != null) return true;
                }
                using (RegistryKey fileKey = Registry.CurrentUser.OpenSubKey(@"Software\Classes\*\shell\LocalDiskServer", false))
                {
                    if (fileKey != null) return true;
                }
                return false;
            }
            catch { return false; }
        }

        public static bool SetShellMenuRegistered(bool enable)
        {
            try
            {
                if (enable)
                {
                    using (RegistryKey key = Registry.CurrentUser.CreateSubKey(@"Software\Classes\Directory\shell\LocalDiskServer"))
                    {
                        if (key == null) return false;
                        key.SetValue("", I18nManager.T("shell_menu_text"));
                        key.SetValue("Icon", Application.ExecutablePath);
                        using (RegistryKey cmd = key.CreateSubKey("command"))
                        {
                            if (cmd == null) return false;
                            cmd.SetValue("", "\"" + Application.ExecutablePath + "\" --open \"%1\"");
                        }
                    }
                    using (RegistryKey key = Registry.CurrentUser.CreateSubKey(@"Software\Classes\*\shell\LocalDiskServer"))
                    {
                        if (key == null) return false;
                        key.SetValue("", I18nManager.T("shell_menu_text"));
                        key.SetValue("Icon", Application.ExecutablePath);
                        using (RegistryKey cmd = key.CreateSubKey("command"))
                        {
                            if (cmd == null) return false;
                            cmd.SetValue("", "\"" + Application.ExecutablePath + "\" --open \"%1\"");
                        }
                    }
                    Log(I18nManager.T("log_shell_menu_registered"));
                }
                else
                {
                    using (RegistryKey shell = Registry.CurrentUser.OpenSubKey(@"Software\Classes\Directory\shell", true))
                    {
                        if (shell != null) shell.DeleteSubKeyTree("LocalDiskServer", false);
                    }
                    using (RegistryKey shell = Registry.CurrentUser.OpenSubKey(@"Software\Classes\*", true))
                    {
                        if (shell != null) shell.DeleteSubKeyTree("LocalDiskServer", false);
                    }
                    Log(I18nManager.T("log_shell_menu_unregistered"));
                }
                if (shellMenuMenuItem != null) shellMenuMenuItem.Checked = enable;
                return true;
            }
            catch (Exception ex)
            {
                Log(I18nManager.T("log_shell_menu_fail", ex.Message));
                MessageBox.Show(I18nManager.T("log_shell_menu_fail", ex.Message), I18nManager.T("dialog_tip"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }
        }

        private void ToggleShellMenu(object sender, EventArgs e)
        {
            SetShellMenuRegistered(!IsShellMenuRegistered());
        }

        public static bool IsClassicMenuEnabled()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(@"Software\Classes\CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}\InprocServer32", false))
                {
                    if (key == null) return false;
                    object v = key.GetValue("");
                    return v != null && string.IsNullOrEmpty(v.ToString());
                }
            }
            catch { return false; }
        }

        public static void SetClassicMenu(bool enable)
        {
            try
            {
                string keyPath = @"Software\Classes\CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}";
                if (enable)
                {
                    using (RegistryKey key = Registry.CurrentUser.CreateSubKey(keyPath + "\\InprocServer32"))
                    {
                        if (key == null) throw new Exception("CreateSubKey failed");
                        key.SetValue("", "");
                    }
                }
                else
                {
                    using (RegistryKey key = Registry.CurrentUser.OpenSubKey(keyPath, true))
                    {
                        if (key != null) key.DeleteSubKeyTree("InprocServer32", false);
                    }
                }
                RestartExplorer();
                Log(I18nManager.T(enable ? "log_classic_menu_enabled" : "log_classic_menu_disabled"));
            }
            catch (Exception ex)
            {
                Log(I18nManager.T("log_classic_menu_fail", ex.Message));
                MessageBox.Show(I18nManager.T("log_classic_menu_fail", ex.Message), I18nManager.T("dialog_tip"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            finally
            {
                if (classicMenuMenuItem != null) classicMenuMenuItem.Checked = IsClassicMenuEnabled();
            }
        }

        private static void RestartExplorer()
        {
            try
            {
                foreach (Process p in Process.GetProcessesByName("explorer"))
                {
                    try { p.Kill(); } catch { }
                }
                System.Threading.Thread.Sleep(800);
                Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true });
                System.Threading.Thread.Sleep(2000);
                if (trayIcon != null)
                {
                    trayIcon.Visible = false;
                    trayIcon.Visible = true;
                }
            }
            catch { }
        }

        private void ToggleClassicMenu(object sender, EventArgs e)
        {
            SetClassicMenu(!IsClassicMenuEnabled());
        }

        public static bool HandleSettingsApi(string rawPath, HttpListenerRequest request, HttpListenerResponse response)
        {
            if (rawPath.Equals("api/settings", StringComparison.OrdinalIgnoreCase) && request.HttpMethod == "GET")
            {
                StringBuilder sb = new StringBuilder();
                sb.Append("{");
                sb.Append("\"success\":true,");
                sb.AppendFormat("\"version\":\"{0}\",", HttpServer.EscapeJson(APP_VERSION));
                sb.AppendFormat("\"port\":{0},", port);
                sb.AppendFormat("\"https_port\":{0},", https_port);
                sb.AppendFormat("\"use_https\":{0},", use_https ? "true" : "false");
                sb.AppendFormat("\"enable_dev_ecosystem\":{0},", enable_dev_ecosystem ? "true" : "false");
                sb.AppendFormat("\"text_extensions\":\"{0}\",", HttpServer.EscapeJson(textExtensionsStr ?? ""));
                sb.AppendFormat("\"language\":\"{0}\",", HttpServer.EscapeJson(language ?? ""));
                sb.AppendFormat("\"startup_enabled\":{0},", IsStartupEnabledStatic() ? "true" : "false");
                
                sb.Append("\"languages\":[");
                var langList = I18nManager.GetAvailableLanguages();
                for (int i = 0; i < langList.Count; i++)
                {
                    if (i > 0) sb.Append(",");
                    sb.AppendFormat("{{\"code\":\"{0}\",\"name\":\"{1}\"}}", HttpServer.EscapeJson(langList[i].Code), HttpServer.EscapeJson(langList[i].Name));
                }
                sb.Append("]");
                sb.Append("}");
                HttpServer.ServeJson(response, 200, sb.ToString());
                return true;
            }

            if (rawPath.Equals("api/settings/open-config", StringComparison.OrdinalIgnoreCase) && request.HttpMethod == "POST")
            {
                string configPath = GetConfigFilePath();
                if (!File.Exists(configPath))
                {
                    SaveConfigStatic();
                }
                try
                {
                    Process.Start(new ProcessStartInfo(configPath) { UseShellExecute = true });
                    HttpServer.ServeJson(response, 200, "{\"success\":true}");
                }
                catch (Exception ex)
                {
                    HttpServer.ServeJson(response, 500, "{\"success\":false,\"message\":\"" + HttpServer.EscapeJson(ex.Message) + "\"}");
                }
                return true;
            }

            if (rawPath.Equals("api/settings/open-app-dir", StringComparison.OrdinalIgnoreCase) && request.HttpMethod == "POST")
            {
                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                try
                {
                    Process.Start(new ProcessStartInfo("explorer.exe", "\"" + baseDir + "\"") { UseShellExecute = true });
                    HttpServer.ServeJson(response, 200, "{\"success\":true}");
                }
                catch (Exception ex)
                {
                    HttpServer.ServeJson(response, 500, "{\"success\":false,\"message\":\"" + HttpServer.EscapeJson(ex.Message) + "\"}");
                }
                return true;
            }

            if (rawPath.Equals("api/settings/cache-info", StringComparison.OrdinalIgnoreCase) && request.HttpMethod == "GET")
            {
                try
                {
                    string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                    string cacheDir = Path.Combine(baseDir, "cache");
                    long totalBytes = 0;
                    if (Directory.Exists(cacheDir))
                    {
                        var di = new DirectoryInfo(cacheDir);
                        foreach (var fi in di.GetFiles("*", SearchOption.AllDirectories))
                        {
                            totalBytes += fi.Length;
                        }
                    }
                    string formattedSize = HttpServer.FormatFileSize(totalBytes);
                    string respJson = string.Format("{{\"success\":true,\"bytes\":{0},\"size\":\"{1}\",\"cacheDir\":\"{2}\"}}",
                        totalBytes, HttpServer.EscapeJson(formattedSize), HttpServer.EscapeJson(cacheDir));
                    HttpServer.ServeJson(response, 200, respJson);
                }
                catch (Exception ex)
                {
                    HttpServer.ServeJson(response, 500, "{\"success\":false,\"message\":\"" + HttpServer.EscapeJson(ex.Message) + "\"}");
                }
                return true;
            }

            if (rawPath.Equals("api/settings/open-cache-dir", StringComparison.OrdinalIgnoreCase) && request.HttpMethod == "POST")
            {
                try
                {
                    string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                    string cacheDir = Path.Combine(baseDir, "cache");
                    if (!Directory.Exists(cacheDir))
                    {
                        Directory.CreateDirectory(cacheDir);
                    }
                    Process.Start(new ProcessStartInfo("explorer.exe", "\"" + cacheDir + "\"") { UseShellExecute = true });
                    HttpServer.ServeJson(response, 200, "{\"success\":true}");
                }
                catch (Exception ex)
                {
                    HttpServer.ServeJson(response, 500, "{\"success\":false,\"message\":\"" + HttpServer.EscapeJson(ex.Message) + "\"}");
                }
                return true;
            }

            if (rawPath.Equals("api/settings/clear-cache", StringComparison.OrdinalIgnoreCase) && request.HttpMethod == "POST")
            {
                try
                {
                    string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                    string cacheDir = Path.Combine(baseDir, "cache");
                    if (Directory.Exists(cacheDir))
                    {
                        var di = new DirectoryInfo(cacheDir);
                        foreach (var file in di.GetFiles())
                        {
                            try { file.Delete(); } catch { }
                        }
                        foreach (var dir in di.GetDirectories())
                        {
                            try { dir.Delete(true); } catch { }
                        }
                    }

                    // 重新从程序集内嵌资源提取预置语言文件并重载
                    I18nManager.ForceExtractDefaultLocales();
                    I18nManager.LoadLanguage(I18nManager.CurrentLanguageCode);

                    // 释放开发者生态内存缓存
                    if (enable_dev_ecosystem)
                    {
                        GradleExplorer.ClearCacheAndReleaseResources();
                        NpmExplorer.ClearCacheAndReleaseResources();
                        PnpmExplorer.ClearCacheAndReleaseResources();
                        MavenExplorer.ClearCacheAndReleaseResources();
                    }

                    Log(I18nManager.T("log_cache_cleared"));
                    HttpServer.ServeJson(response, 200, "{\"success\":true,\"message\":\"" + HttpServer.EscapeJson(I18nManager.T("settings_cache_cleared")) + "\"}");
                }
                catch (Exception ex)
                {
                    HttpServer.ServeJson(response, 500, "{\"success\":false,\"message\":\"" + HttpServer.EscapeJson(I18nManager.T("settings_cache_clear_fail", ex.Message)) + "\"}");
                }
                return true;
            }

            if (rawPath.Equals("api/settings/save", StringComparison.OrdinalIgnoreCase) && request.HttpMethod == "POST")
            {
                try
                {
                    string body = "";
                    using (var reader = new StreamReader(request.InputStream, Encoding.UTF8))
                    {
                        body = reader.ReadToEnd();
                    }

                    int newPort = port;
                    int newHttpsPort = https_port;
                    bool newUseHttps = use_https;
                    bool newDevEcosystem = enable_dev_ecosystem;
                    string newTextExt = textExtensionsStr;
                    string newLang = language;
                    bool newStartup = IsStartupEnabledStatic();

                    var jsonPairs = ExtractSimpleJsonPairs(body);
                    if (jsonPairs.ContainsKey("port")) int.TryParse(jsonPairs["port"], out newPort);
                    if (jsonPairs.ContainsKey("https_port")) int.TryParse(jsonPairs["https_port"], out newHttpsPort);
                    if (jsonPairs.ContainsKey("use_https")) bool.TryParse(jsonPairs["use_https"], out newUseHttps);
                    if (jsonPairs.ContainsKey("enable_dev_ecosystem")) bool.TryParse(jsonPairs["enable_dev_ecosystem"], out newDevEcosystem);
                    if (jsonPairs.ContainsKey("text_extensions")) newTextExt = jsonPairs["text_extensions"];
                    if (jsonPairs.ContainsKey("language")) newLang = jsonPairs["language"];
                    if (jsonPairs.ContainsKey("startup_enabled")) bool.TryParse(jsonPairs["startup_enabled"], out newStartup);

                    if (newPort < 1 || newPort > 65535) newPort = port;
                    if (newHttpsPort < 1 || newHttpsPort > 65535) newHttpsPort = https_port;

                    string[] rawParts = (newTextExt ?? "").Split(new char[] { ',', '，', ';', '；', '\r', '\n', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                    List<string> cleanList = new List<string>();
                    foreach (string raw in rawParts)
                    {
                        string trimmed = raw.Trim().TrimStart('.').ToLower();
                        if (!string.IsNullOrEmpty(trimmed) && !cleanList.Contains(trimmed))
                        {
                            cleanList.Add(trimmed);
                        }
                    }
                    textExtensionsStr = string.Join(",", cleanList.ToArray());

                    if (!string.IsNullOrEmpty(newLang) && !string.Equals(newLang, language, StringComparison.OrdinalIgnoreCase))
                    {
                        SetLanguage(newLang);
                    }

                    SetStartupStatic(newStartup);

                    if (newDevEcosystem != enable_dev_ecosystem)
                    {
                        enable_dev_ecosystem = newDevEcosystem;
                        if (devEcosystemMenuItem != null) devEcosystemMenuItem.Checked = enable_dev_ecosystem;
                        if (enable_dev_ecosystem)
                        {
                            Log(I18nManager.T("log_dev_ecosystem_updated", I18nManager.T("common_enabled")));
                GradleExplorer.TriggerGradleScanAsync();
                NpmExplorer.TriggerNpmScanAsync();
                PnpmExplorer.TriggerPnpmScanAsync();
                MavenExplorer.TriggerMavenScanAsync();
                        }
                        else
                        {
                            Log(I18nManager.T("log_dev_ecosystem_updated", I18nManager.T("common_disabled")));
                            GradleExplorer.ClearCacheAndReleaseResources();
                            NpmExplorer.ClearCacheAndReleaseResources();
                            PnpmExplorer.ClearCacheAndReleaseResources();
                        }
                    }

                    bool portChanged = (newPort != port || newHttpsPort != https_port || newUseHttps != use_https);
                    int oldHttpsPort = last_bound_https_port;

                    if (newUseHttps && !use_https)
                    {
                        Log(I18nManager.T("log_enabling_https"));
                        SslManager.BindSslCertificate(newHttpsPort, 0);
                    }
                    else if (!newUseHttps && use_https)
                    {
                        Log(I18nManager.T("log_disabling_https", https_port));
                        SslManager.UnbindSslCertificate(oldHttpsPort);
                    }
                    else if (newUseHttps && use_https && newHttpsPort != https_port)
                    {
                        Log(I18nManager.T("log_rebinding_https", oldHttpsPort, newHttpsPort));
                        SslManager.BindSslCertificate(newHttpsPort, oldHttpsPort);
                    }

                    port = newPort;
                    https_port = newHttpsPort;
                    use_https = newUseHttps;

                    SaveConfigStatic();
                    UpdateMenuTexts();

                    string respJson = string.Format("{{\"success\":true,\"portChanged\":{0},\"newPort\":{1},\"newHttpsPort\":{2},\"useHttps\":{3}}}",
                        portChanged ? "true" : "false", port, https_port, use_https ? "true" : "false");
                    HttpServer.ServeJson(response, 200, respJson);

                    if (portChanged)
                    {
                        ThreadPool.QueueUserWorkItem(state =>
                        {
                            Thread.Sleep(300);
                            HttpServer.StartServer();
                            UpdateMenuTexts();
                        });
                    }
                    return true;
                }
                catch (Exception ex)
                {
                    HttpServer.ServeJson(response, 500, "{\"success\":false,\"message\":\"" + HttpServer.EscapeJson(ex.Message) + "\"}");
                    return true;
                }
            }

            return false;
        }

        public static Dictionary<string, string> ExtractSimpleJsonPairs(string json)
        {
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(json)) return dict;
            json = json.Trim();
            if (json.StartsWith("{") && json.EndsWith("}"))
            {
                json = json.Substring(1, json.Length - 2);
            }

            var matches = System.Text.RegularExpressions.Regex.Matches(json, @"\""([^\""\\]*(?:\\.[^\""\\]*)*)\""\s*:\s*(?:\""([^\""\\]*(?:\\.[^\""\\]*)*)\""|([^,\}\s]+))");
            foreach (System.Text.RegularExpressions.Match m in matches)
            {
                string key = m.Groups[1].Value;
                string val = m.Groups[2].Success ? m.Groups[2].Value.Replace("\\\"", "\"").Replace("\\\\", "\\").Replace("\\n", "\n").Replace("\\r", "\r") : m.Groups[3].Value;
                dict[key] = val;
            }
            return dict;
        }

        private void ToggleDevEcosystem(object sender, EventArgs e)
        {
            enable_dev_ecosystem = !enable_dev_ecosystem;
            if (devEcosystemMenuItem != null)
            {
                devEcosystemMenuItem.Checked = enable_dev_ecosystem;
            }
            SaveConfig();
            UpdateMenuTexts();

            if (enable_dev_ecosystem)
            {
                Log(I18nManager.T("log_dev_ecosystem_updated", I18nManager.T("common_enabled")));
                GradleExplorer.TriggerGradleScanAsync();
                NpmExplorer.TriggerNpmScanAsync();
                PnpmExplorer.TriggerPnpmScanAsync();
                MavenExplorer.TriggerMavenScanAsync();
            }
            else
            {
                Log(I18nManager.T("log_dev_ecosystem_updated", I18nManager.T("common_disabled")));
                GradleExplorer.ClearCacheAndReleaseResources();
                NpmExplorer.ClearCacheAndReleaseResources();
                PnpmExplorer.ClearCacheAndReleaseResources();
            }
        }

        private void Exit(object sender, EventArgs e)
        {
            HttpServer.StopServer();
            if (trayIcon != null)
            {
                trayIcon.Visible = false;
                trayIcon.Dispose();
            }
            Application.Exit();
        }

        private static string ShowInputDialog(string title, string promptText, string defaultValue)
        {
            return ShowInputDialog(title, promptText, defaultValue, false);
        }

        private static string ShowInputDialog(string title, string promptText, string defaultValue, bool multiline)
        {
            Form form = new Form();
            Label label = new Label();
            TextBox textBox = new TextBox();
            Button buttonOk = new Button();
            Button buttonCancel = new Button();
            Button buttonToggle = null;
            Label hintLabel = null;

            form.Text = title;
            label.Text = promptText;
            textBox.Text = defaultValue;

            buttonOk.Text = I18nManager.T("dialog_ok");
            buttonCancel.Text = I18nManager.T("dialog_cancel");
            buttonOk.DialogResult = DialogResult.OK;
            buttonCancel.DialogResult = DialogResult.Cancel;

            label.AutoSize = true;
            label.SetBounds(12, 12, 416, 18);

            if (multiline)
            {
                textBox.Multiline = true;
                textBox.ScrollBars = ScrollBars.Vertical;
                textBox.WordWrap = false;
                textBox.AcceptsReturn = true;
                try
                {
                    textBox.Font = new Font("Consolas", 9.5f, FontStyle.Regular);
                }
                catch {}
                textBox.SetBounds(12, 34, 416, 220);

                buttonToggle = new Button();
                buttonToggle.Text = I18nManager.T("dialog_text_ext_toggle_format");
                buttonToggle.SetBounds(12, 262, 180, 26);
                buttonToggle.Anchor = AnchorStyles.Bottom | AnchorStyles.Left;
                buttonToggle.Click += (s, e) =>
                {
                    string current = textBox.Text;
                    if (current.Contains("\n"))
                    {
                        string[] p = current.Split(new char[] { ',', '，', ';', '；', '\r', '\n', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                        List<string> list = new List<string>();
                        foreach (string raw in p)
                        {
                            string t = raw.Trim().TrimStart('.').ToLower();
                            if (!string.IsNullOrEmpty(t) && !list.Contains(t)) list.Add(t);
                        }
                        textBox.Text = string.Join(", ", list.ToArray());
                    }
                    else
                    {
                        string[] p = current.Split(new char[] { ',', '，', ';', '；', '\r', '\n', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                        List<string> list = new List<string>();
                        foreach (string raw in p)
                        {
                            string t = raw.Trim().TrimStart('.').ToLower();
                            if (!string.IsNullOrEmpty(t) && !list.Contains(t)) list.Add(t);
                        }
                        textBox.Text = string.Join(Environment.NewLine, list.ToArray());
                    }
                };

                hintLabel = new Label();
                hintLabel.Text = I18nManager.T("dialog_text_ext_hint");
                hintLabel.ForeColor = Color.Gray;
                hintLabel.Font = new Font(form.Font.FontFamily, 8f);
                hintLabel.SetBounds(12, 296, 416, 18);
                hintLabel.Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;

                buttonOk.SetBounds(268, 262, 75, 26);
                buttonCancel.SetBounds(353, 262, 75, 26);

                form.ClientSize = new Size(440, 324);
            }
            else
            {
                textBox.SetBounds(12, 36, 372, 20);
                buttonOk.SetBounds(228, 72, 75, 23);
                buttonCancel.SetBounds(309, 72, 75, 23);

                form.ClientSize = new Size(396, 107);
            }

            textBox.Anchor = textBox.Anchor | AnchorStyles.Right;
            if (multiline)
            {
                textBox.Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right;
            }
            buttonOk.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            buttonCancel.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;

            if (multiline && buttonToggle != null && hintLabel != null)
            {
                form.Controls.AddRange(new Control[] { label, textBox, buttonToggle, buttonOk, buttonCancel, hintLabel });
            }
            else
            {
                form.Controls.AddRange(new Control[] { label, textBox, buttonOk, buttonCancel });
            }
            form.FormBorderStyle = FormBorderStyle.FixedDialog;
            form.StartPosition = FormStartPosition.CenterScreen;
            form.MinimizeBox = false;
            form.MaximizeBox = false;

            if (!multiline)
            {
                form.AcceptButton = buttonOk;
            }
            form.CancelButton = buttonCancel;

            DialogResult dialogResult = form.ShowDialog();
            return dialogResult == DialogResult.OK ? textBox.Text : null;
        }
    }
}
