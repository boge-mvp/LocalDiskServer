# LocalDiskServer 自动化构建脚本 (PowerShell)
# 功能：自动查找 csc.exe 编译器，将 src/ 源代码与 resources/ 前端静态资产编译为独立的 dist/LocalDiskServer.exe

param(
    [Alias("v")]
    [string]$Version
)

$ErrorActionPreference = "Stop"

$rootDir = Split-Path -Parent $PSScriptRoot
$srcDir = Join-Path $rootDir "src"
$resDir = Join-Path $rootDir "resources"
$distDir = Join-Path $rootDir "dist"
$outputExe = Join-Path $distDir "LocalDiskServer.exe"
$programCs = Join-Path $srcDir "Program.cs"

Write-Host "==========================================" -ForegroundColor Cyan
Write-Host "   LocalDiskServer 自动化构建过程启动     " -ForegroundColor Cyan
Write-Host "==========================================" -ForegroundColor Cyan

# 0. 动态解析版本号 (优先级: 显式参数 > APP_VERSION环境变量 > GIT_TAG环境变量 > 本地Git Tag > Program.cs现有版本)
$resolvedVersion = $null
$versionSource = "default"

if ($Version -and $Version.Trim() -ne "") {
    $resolvedVersion = $Version.Trim() -replace '^v',''
    $versionSource = "CLI param (-Version)"
} elseif ($env:APP_VERSION -and $env:APP_VERSION.Trim() -ne "") {
    $resolvedVersion = $env:APP_VERSION.Trim() -replace '^v',''
    $versionSource = "Environment variable (APP_VERSION)"
} elseif ($env:GIT_TAG -and $env:GIT_TAG.Trim() -ne "") {
    $resolvedVersion = $env:GIT_TAG.Trim() -replace '^v',''
    $versionSource = "Environment variable (GIT_TAG)"
} else {
    try {
        $gitTag = git describe --tags --abbrev=0 2>$null
        if ($gitTag -and $gitTag.Trim() -ne "") {
            $resolvedVersion = $gitTag.Trim() -replace '^v',''
            $versionSource = "Git Tag ($gitTag)"
        }
    } catch {
        # ignore git failure
    }
}

if (-not $resolvedVersion) {
    if (Test-Path $programCs) {
        $srcContent = Get-Content $programCs -Raw
        if ($srcContent -match 'public const string APP_VERSION = "([^"]+)";') {
            $resolvedVersion = $matches[1]
            $versionSource = "Fallback (src/Program.cs)"
        }
    }
    if (-not $resolvedVersion) {
        $resolvedVersion = "1.0.0"
        $versionSource = "Default fallback"
    }
}

# 动态注入版本号至 src/Program.cs (保持与 GitHub Actions release.yml 一致的注入规则)
if (Test-Path $programCs) {
    $srcContent = [System.IO.File]::ReadAllText($programCs, [System.Text.Encoding]::UTF8)
    $newSrcContent = $srcContent -replace '(APP_VERSION = ")[\d.]+(")', "`${1}$resolvedVersion`${2}"
    if ($newSrcContent -ne $srcContent) {
        [System.IO.File]::WriteAllText($programCs, $newSrcContent, [System.Text.Encoding]::UTF8)
        Write-Host "[0/4] 动态注入版本号: $resolvedVersion (来源: $versionSource)" -ForegroundColor Green
    } else {
        Write-Host "[0/4] 版本号已对齐: $resolvedVersion (来源: $versionSource)" -ForegroundColor Green
    }
}

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
    $item = Get-Item $outputExe
    $exeSize = $item.Length / 1KB
    $actualVersion = $item.VersionInfo.FileVersion
    Write-Host "==========================================" -ForegroundColor Green
    Write-Host " [√] 编译构建成功！" -ForegroundColor Green
    Write-Host " 输出目标: $outputExe" -ForegroundColor White
    Write-Host " 产物版本: $actualVersion" -ForegroundColor White
    Write-Host " 文件大小: $([Math]::Round($exeSize, 2)) KB" -ForegroundColor White
    Write-Host "==========================================" -ForegroundColor Green

    if ($actualVersion -ne $resolvedVersion) {
        Write-Error "版本校验失败: 预期版本为 $resolvedVersion，但生成 exe 实际报告为 $actualVersion。"
        exit 1
    }
} else {
    Write-Host "==========================================" -ForegroundColor Red
    Write-Host " [×] 编译构建失败，退出代码: $($process.ExitCode)" -ForegroundColor Red
    Write-Host "==========================================" -ForegroundColor Red
    exit $process.ExitCode
}

# ── 插件编译段: 遍历 plugins/*/backend.cs，用同一 csc 编出 backend.exe 并同步到 dist/plugins/ ──
$pluginsDir = Join-Path $rootDir "plugins"
if (Test-Path $pluginsDir) {
    # 终止运行中的插件后端进程（防止文件占用）
    Get-Process -Name "backend" -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$pluginsDir*" } | Stop-Process -Force
    Get-Process -Name "backend" -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$distDir*" } | Stop-Process -Force

    $built = 0
    Get-ChildItem -Path $pluginsDir -Directory | ForEach-Object {
        $pluginDir = $_.FullName
        $backendCs = Join-Path $pluginDir "backend.cs"
        if (Test-Path $backendCs) {
            $pluginOut = Join-Path $pluginDir "backend.exe"
            Write-Host ""
            Write-Host "[plugin] 编译 $($_.Name)..." -ForegroundColor Cyan
            $pluginArgs = @("/target:exe", "/optimize+", "/utf8output", "/out:`"$pluginOut`"", "/r:System.dll", "/r:System.Core.dll", "/r:System.Web.dll", "/r:System.Xml.dll", "`"$backendCs`"")
            $pp = Start-Process -FilePath $cscPath -ArgumentList $pluginArgs -NoNewWindow -Wait -PassThru
            if ($pp.ExitCode -ne 0 -or -not (Test-Path $pluginOut)) {
                Write-Host " [×] 插件 $($_.Name) 编译失败，退出代码: $($pp.ExitCode)" -ForegroundColor Red
                exit 1
            }
            $built++
        }
    }

    # 同步 plugins/ → dist/plugins/（运行时从 exe 同目录加载）
    $distPlugins = Join-Path $distDir "plugins"
    if (Test-Path $distPlugins) { Remove-Item -Recurse -Force $distPlugins }
    Copy-Item -Path $pluginsDir -Destination $distPlugins -Recurse -Force
    if ($built -gt 0) {
        Write-Host " [√] 已编译 $built 个插件并同步到 dist/plugins/" -ForegroundColor Green
    }
}
