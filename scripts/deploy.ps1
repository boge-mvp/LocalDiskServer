# LocalDiskServer 自动化部署脚本 (PowerShell)
# 功能：确保生成最新 LocalDiskServer.exe，安全关闭运行中的实例，发布覆盖至便携目录并自动重启服务

$ErrorActionPreference = "Stop"

$rootDir = Split-Path -Parent $PSScriptRoot
$distExe = Join-Path $rootDir "dist\LocalDiskServer.exe"
$buildScript = Join-Path $rootDir "scripts\build.ps1"
$targetDir = "D:\apps\portable-apps\LocalDiskServer"
$targetExe = Join-Path $targetDir "LocalDiskServer.exe"

Write-Host "==========================================" -ForegroundColor Cyan
Write-Host "   LocalDiskServer 部署与无缝重启流程      " -ForegroundColor Cyan
Write-Host "==========================================" -ForegroundColor Cyan

# 1. 检查是否已编译，若无则自动触发编译
if (-not (Test-Path $distExe)) {
    Write-Host "[1/5] 未检测到构建产物，自动调用 build.ps1 编译..." -ForegroundColor Yellow
    & $buildScript
    if ($LASTEXITCODE -ne 0 -and -not (Test-Path $distExe)) {
        Write-Error "错误：自动化编译失败，终止部署！"
        exit 1
    }
} else {
    Write-Host "[1/5] 检测到最新构建产物: $distExe" -ForegroundColor Green
}

# 2. 检查并创建目标目录
Write-Host "[2/5] 检查并准备目标目录: $targetDir" -ForegroundColor Green
if (-not (Test-Path $targetDir)) {
    New-Item -ItemType Directory -Path $targetDir -Force | Out-Null
    Write-Host "   - 目标目录创建成功！" -ForegroundColor Gray
} else {
    Write-Host "   - 目标目录已存在。" -ForegroundColor Gray
}

# 3. 部署前：安全终止所有运行中的 LocalDiskServer 实例，释放文件句柄
$runningProcesses = Get-Process -Name "LocalDiskServer" -ErrorAction SilentlyContinue
if ($runningProcesses) {
    Write-Host "[3/5] 检测到正在运行的 LocalDiskServer 实例 (共 $($runningProcesses.Count) 个)，正在安全关闭..." -ForegroundColor Yellow
    $runningProcesses | Stop-Process -Force
    Start-Sleep -Milliseconds 800
} else {
    Write-Host "[3/5] 无正在运行的旧实例，无需关闭。" -ForegroundColor Green
}

# 4. 复制覆盖运行文件至便携目录
Write-Host "[4/5] 正在发布最新文件至便携目录..." -ForegroundColor Green
Copy-Item -Path $distExe -Destination $targetExe -Force

if (Test-Path $targetExe) {
    $item = Get-Item $targetExe
    $sizeKb = [Math]::Round($item.Length / 1KB, 2)
    Write-Host "   - 文件复制成功 (大小: $sizeKb KB, 时间: $($item.LastWriteTime.ToString('yyyy-MM-dd HH:mm:ss')))" -ForegroundColor Gray
} else {
    Write-Host "==========================================" -ForegroundColor Red
    Write-Host " [×] 部署失败：目标文件未生成！" -ForegroundColor Red
    Write-Host "==========================================" -ForegroundColor Red
    exit 1
}

# 5. 部署后：在便携目录重新启动 LocalDiskServer 服务
Write-Host "[5/5] 正在便携目录重启 LocalDiskServer 常驻服务..." -ForegroundColor Yellow
$newProc = Start-Process -FilePath $targetExe -WorkingDirectory $targetDir -PassThru
Start-Sleep -Milliseconds 600

if ($newProc -and -not $newProc.HasExited) {
    Write-Host "==========================================" -ForegroundColor Green
    Write-Host " [√] 部署并自动重启成功！" -ForegroundColor Green
    Write-Host " 运行路径: $targetExe" -ForegroundColor White
    Write-Host " 进程 PID: $($newProc.Id)" -ForegroundColor White
    Write-Host " 工作目录: $targetDir" -ForegroundColor White
    Write-Host "==========================================" -ForegroundColor Green
} else {
    Write-Host "==========================================" -ForegroundColor Yellow
    Write-Host " [!] 文件已成功发布，但自动重启可能仍在后台初始化。" -ForegroundColor Yellow
    Write-Host "==========================================" -ForegroundColor Yellow
}
