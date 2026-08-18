# LocalDiskServer 自动化构建脚本 (PowerShell)
# 功能：自动查找 csc.exe 编译器，将 src/ 源代码与 resources/ 前端静态资产编译为独立的 dist/LocalDiskServer.exe

$ErrorActionPreference = "Stop"

$rootDir = Split-Path -Parent $PSScriptRoot
$srcDir = Join-Path $rootDir "src"
$resDir = Join-Path $rootDir "resources"
$distDir = Join-Path $rootDir "dist"
$outputExe = Join-Path $distDir "LocalDiskServer.exe"

Write-Host "==========================================" -ForegroundColor Cyan
Write-Host "   LocalDiskServer 自动化构建过程启动     " -ForegroundColor Cyan
Write-Host "==========================================" -ForegroundColor Cyan

# 1. 查找 csc.exe 编译器
$cscCandidates = @(
    "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe",
    "C:\Windows\Microsoft.NET\Framework\v4.0.30319\csc.exe"
)

$cscPath = $null
foreach ($candidate in $cscCandidates) {
    if (Test-Path $candidate) {
        $cscPath = $candidate
        break
    }
}

if (-not $cscPath) {
    $commandCheck = Get-Command "csc.exe" -ErrorAction SilentlyContinue
    if ($commandCheck) {
        $cscPath = $commandCheck.Source
    }
}

if (-not $cscPath) {
    Write-Error "错误：未在系统中找到 .NET Framework 4.0/4.8 的 csc.exe 编译器！"
    exit 1
}

Write-Host "[1/4] 编译器定位成功: $cscPath" -ForegroundColor Green

# 2. 确保输出目录存在与文件未被占用
if (-not (Test-Path $distDir)) {
    New-Item -ItemType Directory -Path $distDir -Force | Out-Null
} else {
    $runningDev = Get-Process -Name "LocalDiskServer" -ErrorAction SilentlyContinue | Where-Object {
        $_.Path -eq $outputExe
    }
    if ($runningDev) {
        Write-Host "[!] 检测到 dist/ 下运行中的 LocalDiskServer，正在安全终止..." -ForegroundColor Yellow
        $runningDev | Stop-Process -Force
        Start-Sleep -Milliseconds 600
    }
}

# 3. 收集 C# 源代码
$csFiles = Get-ChildItem -Path $srcDir -Filter "*.cs" | Select-Object -ExpandProperty FullName
if ($csFiles.Count -eq 0) {
    Write-Error "错误：src/ 目录下未找到任何 C# 源文件！"
    exit 1
}
Write-Host "[2/4] 收集源代码文件 ($($csFiles.Count) 个):" -ForegroundColor Green
$csFiles | ForEach-Object { Write-Host "   - $(Split-Path $_ -Leaf)" -ForegroundColor Gray }

# 4. 收集并组装静态资源 (打包为程序集内嵌资源)
$resFiles = Get-ChildItem -Path $resDir -File -Recurse
$resourceArgs = @()
Write-Host "[3/4] 准备内嵌前端与多语言资源 ($($resFiles.Count) 个):" -ForegroundColor Green
foreach ($res in $resFiles) {
    $relativePath = $res.FullName.Substring($resDir.Length).TrimStart('\', '/').Replace('\', '/')
    $resourceArgs += "/resource:`"$($res.FullName)`",$relativePath"
    Write-Host "   - $relativePath" -ForegroundColor Gray
}

# 5. 组装编译参数
$references = @(
    "System.dll",
    "System.Core.dll",
    "System.Drawing.dll",
    "System.Windows.Forms.dll",
    "System.Web.dll",
    "Microsoft.CSharp.dll"
)
$refArgs = $references | ForEach-Object { "/r:$_" }

$compilerArgs = @(
    "/target:winexe",
    "/optimize+",
    "/utf8output",
    "/out:`"$outputExe`""
) + $refArgs + $resourceArgs + ($csFiles | ForEach-Object { "`"$_`"" })

Write-Host "[4/4] 正在执行编译打包..." -ForegroundColor Yellow

# 调用 csc.exe 编译
$process = Start-Process -FilePath $cscPath -ArgumentList $compilerArgs -NoNewWindow -Wait -PassThru

if ($process.ExitCode -eq 0 -and (Test-Path $outputExe)) {
    $exeSize = (Get-Item $outputExe).Length / 1KB
    Write-Host "==========================================" -ForegroundColor Green
    Write-Host " [√] 编译构建成功！" -ForegroundColor Green
    Write-Host " 输出目标: $outputExe" -ForegroundColor White
    Write-Host " 文件大小: $([Math]::Round($exeSize, 2)) KB" -ForegroundColor White
    Write-Host "==========================================" -ForegroundColor Green
} else {
    Write-Host "==========================================" -ForegroundColor Red
    Write-Host " [×] 编译构建失败，退出代码: $($process.ExitCode)" -ForegroundColor Red
    Write-Host "==========================================" -ForegroundColor Red
    exit $process.ExitCode
}
