# 强力自愈式 Windows 宿主根 CA 证书注册与 SSL 端口强制重绑脚本
# 本脚本需要管理员权限执行。

Write-Host "=== 1. 检查并强制创建高精度、免冲突、多域根 CA 证书 ==="
# 查找专属于我们服务器的 Root CA 证书
$cert = Get-ChildItem -Path cert:\LocalMachine\My | Where-Object { 
    $_.Subject -eq 'CN=LocalDiskServer Root CA'
} | Select-Object -First 1

if (-not $cert) {
    Write-Host "未找到标准根 CA 证书，开始强制生成..."
    # 强制添加 OID 2.5.29.19 (Basic Constraints, ca=true) 并开启 CertSign 根证书指纹签名许可
    # 主体为独一无二的 "CN=LocalDiskServer Root CA"，DNSName (SAN) 包含 localhost, 127.0.0.1, *.localhost.test
    $cert = New-SelfSignedCertificate -Subject "CN=LocalDiskServer Root CA" -DnsName localhost,127.0.0.1,*.localhost.test `
        -CertStoreLocation cert:\LocalMachine\My `
        -NotAfter (Get-Date).AddYears(10) `
        -TextExtension @("2.5.29.19={text}ca=true") `
        -KeyUsage CertSign,CRLSign,DigitalSignature `
        -FriendlyName "LocalDiskServer Root CA"
    Write-Host "新 CA 证书生成成功！指纹: $($cert.Thumbprint)"
} else {
    Write-Host "已存在标准根 CA 证书。指纹: $($cert.Thumbprint)"
}

Write-Host "=== 2. 强制导入 LocalMachine\Root (受信任的根证书颁发机构) ==="
$rootStore = New-Object System.Security.Cryptography.X509Certificates.X509Store('Root', 'LocalMachine')
$rootStore.Open('ReadWrite')
$rootStore.Add($cert)
$rootStore.Close()
Write-Host "导入本地计算机受信任的根证书颁发机构成功！"

Write-Host "=== 3. 清理内核 HTTP.sys 端口绑定并进行全新 SSL 锁定 ==="
$hash = $cert.Thumbprint

# 清理并撤销内核在 1235 端口的任何历史绑定
netsh http delete sslcert ipport=0.0.0.0:1235 2>$null
netsh http delete sslcert ipport=[::]:1235 2>$null

# 执行全新的 SSL 强锁绑定
netsh http add sslcert ipport=0.0.0.0:1235 certhash=$hash appid='{ec8d2a6a-d9dc-4c48-b4b1-8b0933333333}'
netsh http add sslcert ipport=[::]:1235 certhash=$hash appid='{ec8d2a6a-d9dc-4c48-b4b1-8b0933333333}'

# 写入哈希文件，以便 C# 后端自适应感知
$projectRoot = Split-Path -Parent $PSScriptRoot
$hashDir = Join-Path $projectRoot "bin"
if (-not (Test-Path $hashDir)) { New-Item -ItemType Directory -Path $hashDir -Force }
$hash | Out-File -FilePath "$hashDir\ssl_hash.txt" -Encoding ascii -Force

Write-Host "=== 4. SSL 证书物理注册与内核重绑 100% 完成！ ==="
Write-Host "请关闭本窗口，并在浏览器中重新访问 https://localhost:1235/ 体验极具安全的绿色安全锁！"
Start-Sleep -Seconds 3
