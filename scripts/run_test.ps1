# LocalDiskServer 项目内自动化测试脚本 (PowerShell)
# 功能：使用隔离的测试端口 (18080/18443) 启动临时服务实例并验证接口连通性，测试完成后干净退出

param(
    [int]$testPort = 18080,
    [int]$testHttpsPort = 18443
)

$ErrorActionPreference = "Stop"

$rootDir = Split-Path -Parent $PSScriptRoot
$distExe = Join-Path $rootDir "dist\LocalDiskServer.exe"
$buildScript = Join-Path $rootDir "scripts\build.ps1"

Write-Host "==========================================" -ForegroundColor Cyan
Write-Host "   LocalDiskServer 项目内自动化测试       " -ForegroundColor Cyan
Write-Host "==========================================" -ForegroundColor Cyan

# 1. 确保最新可执行程序已编译
if (-not (Test-Path $distExe)) {
    Write-Host "[1/4] 未检测到构建产物，正在触发编译..." -ForegroundColor Yellow
    & $buildScript
} else {
    Write-Host "[1/4] 检测到构建产物: $distExe" -ForegroundColor Green
}

# 2. 清理可能残留的测试进程
$oldTest = Get-Process -Name "LocalDiskServer" -ErrorAction SilentlyContinue | Where-Object {
    $_.Path -eq $distExe
}
if ($oldTest) {
    Write-Host "[!] 清理残留的测试进程..." -ForegroundColor Yellow
    $oldTest | Stop-Process -Force
    Start-Sleep -Milliseconds 400
}

# 3. 启动隔离测试实例
Write-Host "[2/4] 正在以测试模式启动服务 (HTTP 端口: $testPort, HTTPS 端口: $testHttpsPort)..." -ForegroundColor Yellow
$proc = Start-Process -FilePath $distExe -ArgumentList "--test --port $testPort --https-port $testHttpsPort --no-browser" -WorkingDirectory (Join-Path $rootDir "dist") -PassThru

try {
    # 等待服务初始化与端口监听
    Start-Sleep -Milliseconds 1200

    # 4. 执行 HTTP 接口连通性测试
    Write-Host "[3/4] 正在对测试端口发起 HTTP 请求验证..." -ForegroundColor Yellow
    $testUrl = "http://127.0.0.1:$testPort/"
    
    $response = Invoke-WebRequest -Uri $testUrl -UseBasicParsing -TimeoutSec 5
    if ($response.StatusCode -eq 200) {
        Write-Host "   [√] HTTP GET $testUrl -> 响应状态码: $($response.StatusCode) OK" -ForegroundColor Green
        Write-Host "   [√] 页面长度: $($response.Content.Length) 字符" -ForegroundColor Gray
    } else {
        throw "测试失败：HTTP 请求未返回 200，实际为 $($response.StatusCode)"
    }

    Write-Host "==========================================" -ForegroundColor Green
    Write-Host " [√] 项目内测试端口验证全部通过！" -ForegroundColor Green
    Write-Host "==========================================" -ForegroundColor Green
}
finally {
    # 5. 测试完成，干净终止测试实例并清理测试配置
    Write-Host "[4/4] 正在关闭测试实例并清理环境..." -ForegroundColor Gray
    if ($proc -and -not $proc.HasExited) {
        $proc | Stop-Process -Force
        Start-Sleep -Milliseconds 300
    }
    $testConfig = Join-Path $rootDir "dist\server_config_test.ini"
    if (Test-Path $testConfig) {
        Remove-Item -Path $testConfig -Force -ErrorAction SilentlyContinue
    }
    Write-Host "   - 测试实例已正常退出，测试环境已清理干净。" -ForegroundColor Gray
}
