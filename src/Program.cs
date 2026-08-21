using System;
using System.IO;
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
        
        public static NotifyIcon trayIcon;
        public static MenuItem statusMenuItem;
        public static MenuItem openHomeMenuItem;
        public static MenuItem viewLogsMenuItem;
        public static MenuItem configTextExtMenuItem;
        public static MenuItem plainPortMenuItem;
        public static MenuItem sslPortMenuItem;
        public static MenuItem httpsToggleMenuItem;
        public static MenuItem languageSubMenu;
        public static MenuItem startupMenuItem;
        public static MenuItem exitMenuItem;

        // 持久化控制与证书状态变量
        public static int port = 1234;
        public static int https_port = 1235;
        public static bool use_https = false;
        public static string ssl_hash = "";
        public static int last_bound_https_port = 1235;
        public static string language = "";

        public static string configFile = "server_config.ini";
        public static string textExtensionsStr = "txt,md,log,ini,conf,cfg,json,js,css,html,htm,xml,bat,sh,py,java,cs,go,rs,cpp,h,c,properties,yaml,yml,sql,ts";
        public static string favoritesStr = "";

        // 命令行控制与测试模式变量
        public static bool isTestMode = false;
        public static bool noBrowser = false;
        public static int? overridePort = null;
        public static int? overrideHttpsPort = null;

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

            // 启动后台线程异步扫描 Gradle 缓存与 Wrapper
            GradleExplorer.TriggerGradleScanAsync();
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

        public void SaveConfig()
        {
            SaveConfigStatic();
        }

        public static void SaveConfigStatic()
        {
            try
            {
                string exeDir = AppDomain.CurrentDomain.BaseDirectory;
                string configPath = Path.Combine(exeDir, configFile);
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("port=" + port);
                sb.AppendLine("https_port=" + https_port);
                sb.AppendLine("use_https=" + use_https);
                sb.AppendLine("ssl_hash=" + ssl_hash);
                sb.AppendLine("last_bound_https_port=" + last_bound_https_port);
                sb.AppendLine("text_extensions=" + textExtensionsStr);
                sb.AppendLine("favorites=" + favoritesStr);
                sb.AppendLine("language=" + (language ?? ""));
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

            statusMenuItem = new MenuItem(I18nManager.T("menu_status_stopped"));
            statusMenuItem.Enabled = false;
            trayMenu.MenuItems.Add(statusMenuItem);

            trayMenu.MenuItems.Add(new MenuItem("-"));

            openHomeMenuItem = new MenuItem(I18nManager.T("menu_open_home"), OpenBrowser);
            viewLogsMenuItem = new MenuItem(I18nManager.T("menu_view_logs"), OpenLogs);
            configTextExtMenuItem = new MenuItem(I18nManager.T("menu_config_text_ext"), ChangeTextExtensions);

            trayMenu.MenuItems.Add(openHomeMenuItem);
            trayMenu.MenuItems.Add(viewLogsMenuItem);
            trayMenu.MenuItems.Add(configTextExtMenuItem);

            plainPortMenuItem = new MenuItem(I18nManager.T("menu_config_plain_port", port), ChangePort);
            sslPortMenuItem = new MenuItem(I18nManager.T("menu_config_ssl_port", https_port), ChangeHttpsPort);
            httpsToggleMenuItem = new MenuItem(I18nManager.T("menu_toggle_https"), ToggleHttps);
            httpsToggleMenuItem.Checked = use_https;
            
            trayMenu.MenuItems.Add(plainPortMenuItem);
            trayMenu.MenuItems.Add(sslPortMenuItem);
            trayMenu.MenuItems.Add(httpsToggleMenuItem);

            // 动态构建多语言二级子菜单
            languageSubMenu = new MenuItem(I18nManager.T("menu_language"));
            BuildLanguageSubMenu();
            trayMenu.MenuItems.Add(languageSubMenu);

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
            if (openHomeMenuItem != null) openHomeMenuItem.Text = I18nManager.T("menu_open_home");
            if (viewLogsMenuItem != null) viewLogsMenuItem.Text = I18nManager.T("menu_view_logs");
            if (configTextExtMenuItem != null) configTextExtMenuItem.Text = I18nManager.T("menu_config_text_ext");
            if (plainPortMenuItem != null) plainPortMenuItem.Text = I18nManager.T("menu_config_plain_port", port);
            if (sslPortMenuItem != null) sslPortMenuItem.Text = I18nManager.T("menu_config_ssl_port", https_port);
            if (httpsToggleMenuItem != null)
            {
                httpsToggleMenuItem.Text = I18nManager.T("menu_toggle_https");
                httpsToggleMenuItem.Checked = use_https;
            }
            if (languageSubMenu != null)
            {
                languageSubMenu.Text = I18nManager.T("menu_language");
                BuildLanguageSubMenu();
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
            string input = ShowInputDialog(I18nManager.T("dialog_text_ext_title"), I18nManager.T("dialog_text_ext_prompt"), textExtensionsStr, true);
            if (input == null) return;
            textExtensionsStr = input.Trim();
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
                    return true;
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(I18nManager.T("dialog_startup_fail", ex.Message), I18nManager.T("dialog_tip"), MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
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

            form.Text = title;
            label.Text = promptText;
            textBox.Text = defaultValue;

            buttonOk.Text = I18nManager.T("dialog_ok");
            buttonCancel.Text = I18nManager.T("dialog_cancel");
            buttonOk.DialogResult = DialogResult.OK;
            buttonCancel.DialogResult = DialogResult.Cancel;

            label.AutoSize = true;
            label.SetBounds(9, 15, 372, 15);

            if (multiline)
            {
                textBox.Multiline = true;
                textBox.ScrollBars = ScrollBars.Vertical;
                textBox.WordWrap = true;
                textBox.AcceptsReturn = true;
                textBox.SetBounds(12, 40, 372, 180);

                buttonOk.SetBounds(228, 235, 75, 23);
                buttonCancel.SetBounds(309, 235, 75, 23);

                form.ClientSize = new Size(396, 270);
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
                textBox.Anchor = textBox.Anchor | AnchorStyles.Bottom;
            }
            buttonOk.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;
            buttonCancel.Anchor = AnchorStyles.Bottom | AnchorStyles.Right;

            form.Controls.AddRange(new Control[] { label, textBox, buttonOk, buttonCancel });
            form.ClientSize = new Size(Math.Max(396, label.Right + 10), form.ClientSize.Height);
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
