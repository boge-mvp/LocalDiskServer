# LocalDiskServer 自动化部署脚本 (PowerShell)
# 功能：确保生成最新 LocalDiskServer.exe，并将其自动发布部署至 D:\apps\portable-apps\LocalDiskServer 目录

$ErrorActionPreference = "Stop"

$rootDir = Split-Path -Parent $PSScriptRoot
$distExe = Join-Path $rootDir "dist\LocalDiskServer.exe"
$buildScript = Join-Path $rootDir "scripts\build.ps1"
$repairScript = Join-Path $rootDir "scripts\run_repair.ps1"
$targetDir = "D:\apps\portable-apps\LocalDiskServer"

Write-Host "==========================================" -ForegroundColor Cyan
Write-Host "   LocalDiskServer 部署到便携目录          " -ForegroundColor Cyan
Write-Host "==========================================" -ForegroundColor Cyan

# 1. 检查是否已编译，若无则自动触发编译
if (-not (Test-Path $distExe)) {
    Write-Host "[1/3] 未检测到构建产物，自动调用 build.ps1 编译..." -ForegroundColor Yellow
    & $buildScript
    if ($LASTEXITCODE -ne 0 -and -not (Test-Path $distExe)) {
        Write-Error "错误：自动化编译失败，终止部署！"
        exit 1
    }
} else {
    Write-Host "[1/3] 检测到最新构建产物: $distExe" -ForegroundColor Green
}

# 2. 检查并创建目标目录
Write-Host "[2/3] 检查并准备目标目录: $targetDir" -ForegroundColor Green
if (-not (Test-Path $targetDir)) {
    New-Item -ItemType Directory -Path $targetDir -Force | Out-Null
    Write-Host "   - 目标目录创建成功！" -ForegroundColor Gray
} else {
    Write-Host "   - 目标目录已存在。" -ForegroundColor Gray
}

# 3. 复制运行文件及运维工具
Write-Host "[3/3] 正在复制文件至便携目录..." -ForegroundColor Green
Copy-Item -Path $distExe -Destination (Join-Path $targetDir "LocalDiskServer.exe") -Force
if (Test-Path $repairScript) {
    Copy-Item -Path $repairScript -Destination (Join-Path $targetDir "run_repair.ps1") -Force
}

$deployedExe = Join-Path $targetDir "LocalDiskServer.exe"
if (Test-Path $deployedExe) {
    $item = Get-Item $deployedExe
    $sizeKb = [Math]::Round($item.Length / 1KB, 2)
    Write-Host "==========================================" -ForegroundColor Green
    Write-Host " [√] 部署发布成功！" -ForegroundColor Green
    Write-Host " 目标文件: $deployedExe" -ForegroundColor White
    Write-Host " 文件大小: $sizeKb KB" -ForegroundColor White
    Write-Host " 更新时间: $($item.LastWriteTime.ToString('yyyy-MM-dd HH:mm:ss'))" -ForegroundColor White
    Write-Host "==========================================" -ForegroundColor Green
} else {
    Write-Host "==========================================" -ForegroundColor Red
    Write-Host " [×] 部署失败：目标文件未生成！" -ForegroundColor Red
    Write-Host "==========================================" -ForegroundColor Red
    exit 1
}
