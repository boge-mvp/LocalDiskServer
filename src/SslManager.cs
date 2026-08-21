using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace LocalDiskServer
{
    public static class SslManager
    {
        public static void BindSslCertificate(int newPort, int oldPort)
        {
            try
            {
                string baseDir = AppDomain.CurrentDomain.BaseDirectory;
                string scratchDir = Path.Combine(baseDir, "bin");
                if (!Directory.Exists(scratchDir))
                {
                    Directory.CreateDirectory(scratchDir);
                }

                string hashFile = Path.Combine(scratchDir, "ssl_hash.txt");
                if (File.Exists(hashFile))
                {
                    try { File.Delete(hashFile); } catch { }
                }

                // 构造 PowerShell 脚本，利用双大括号进行 C# string.Format 转义
                string psScript = string.Format(
                    "$cert = Get-ChildItem -Path cert:\\LocalMachine\\My | Where-Object {{ $_.Subject -eq 'CN=LocalDiskServer Root CA' }} | Select-Object -First 1; " +
                    "if (-not $cert) {{ $cert = New-SelfSignedCertificate -Subject 'CN=LocalDiskServer Root CA' -DnsName localhost,127.0.0.1,*.localhost.test -CertStoreLocation cert:\\LocalMachine\\My -NotAfter (Get-Date).AddYears(10) -TextExtension @('2.5.29.19={{text}}ca=true') -KeyUsage CertSign,CRLSign,DigitalSignature -FriendlyName 'LocalDiskServer Root CA' }}; " +
                    "$rootStore = New-Object System.Security.Cryptography.X509Certificates.X509Store('Root', 'LocalMachine'); " +
                    "$rootStore.Open('ReadWrite'); " +
                    "$rootStore.Add($cert); " +
                    "$rootStore.Close(); " +
                    "$hash = $cert.Thumbprint; " +
                    "if ({0} -ne 0 -and {0} -ne {1}) {{ " +
                    "   netsh http delete sslcert ipport=0.0.0.0:{0} 2>$null; " +
                    "   netsh http delete sslcert ipport=[::]:{0} 2>$null; " +
                    "}}; " +
                    "netsh http delete sslcert ipport=0.0.0.0:{1} 2>$null; " +
                    "netsh http delete sslcert ipport=[::]:{1} 2>$null; " +
                    "netsh http add sslcert ipport=0.0.0.0:{1} certhash=$hash appid='{{ec8d2a6a-d9dc-4c48-b4b1-8b0933333333}}'; " +
                    "netsh http add sslcert ipport=[::]:{1} certhash=$hash appid='{{ec8d2a6a-d9dc-4c48-b4b1-8b0933333333}}'; " +
                    "$hash | Out-File -FilePath '{2}' -Encoding ascii -Force",
                    oldPort, newPort, hashFile.Replace("'", "''")
                );

                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = "powershell.exe";
                psi.Arguments = "-NoProfile -ExecutionPolicy Bypass -Command \"" + psScript + "\"";
                psi.Verb = "runas";
                psi.UseShellExecute = true;
                psi.WindowStyle = ProcessWindowStyle.Hidden;

                Logger.Log(I18nManager.T("log_ssl_bind_start", newPort));
                using (Process proc = Process.Start(psi))
                {
                    proc.WaitForExit(30000); // 最多等 30 秒
                }

                // 轮询等待指纹文件生成
                int attempts = 0;
                while (!File.Exists(hashFile) && attempts < 10)
                {
                    Thread.Sleep(500);
                    attempts++;
                }

                if (File.Exists(hashFile))
                {
                    string thumbprint = File.ReadAllText(hashFile).Trim();
                    if (!string.IsNullOrEmpty(thumbprint))
                    {
                        ServerApplicationContext.ssl_hash = thumbprint;
                        ServerApplicationContext.last_bound_https_port = newPort;
                        ServerApplicationContext.SaveConfigStatic();
                        Logger.Log(I18nManager.T("log_ssl_bind_success", thumbprint));
                        return;
                    }
                }
                Logger.Log(I18nManager.T("log_ssl_bind_failed"));
            }
            catch (Exception ex)
            {
                Logger.Log(I18nManager.T("log_ssl_bind_ex", ex.Message));
            }
        }

        public static void UnbindSslCertificate(int port)
        {
            try
            {
                string psScript = string.Format(
                    "netsh http delete sslcert ipport=0.0.0.0:{0} 2>$null; " +
                    "netsh http delete sslcert ipport=[::]:{0} 2>$null",
                    port
                );

                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = "powershell.exe";
                psi.Arguments = "-NoProfile -ExecutionPolicy Bypass -Command \"" + psScript + "\"";
                psi.Verb = "runas";
                psi.UseShellExecute = true;
                psi.WindowStyle = ProcessWindowStyle.Hidden;

                Logger.Log(I18nManager.T("log_ssl_unbind_start", port));
                using (Process proc = Process.Start(psi))
                {
                    proc.WaitForExit(15000);
                }
                Logger.Log(I18nManager.T("log_ssl_unbind_success", port));
            }
            catch (Exception ex)
            {
                Logger.Log(I18nManager.T("log_ssl_unbind_ex", ex.Message));
            }
        }
    }
}
