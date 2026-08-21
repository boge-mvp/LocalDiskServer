# LocalDiskServer

<div align="center">

**极速、单文件、全自包含的 Windows 本地磁盘 Web 服务器与开发者百宝箱**  
*Ultra-lightweight, single-binary, fully self-contained local disk Web server & developer toolbox for Windows.*

[简体中文](#简体中文) | [English](#english)

</div>

---

<span id="简体中文"></span>

## 🇨🇳 简体中文说明

`LocalDiskServer` 是一个使用原生 C# 编写的高性能、超轻量（~200KB）Windows 托盘应用程序。它能够将你的 Windows 本地驱动器和文件目录秒级映射为现代化、响应式的 Web 站点，同时内置了丰富的开发者辅助工具。

### ✨ 核心特性

- 🚀 **极速轻量，单文件自包含**：编译产物仅一个 `LocalDiskServer.exe` 文件（~200KB），无需安装 Node.js/Python/.NET Core 等庞大运行时，随拷随用。
- 🔒 **HTTPS 双通道与证书自愈**：一键开启 HTTPS 双通道支持，程序内部自动调用 Windows 底层接口生成并注册受信任的本地根 CA 证书，免去繁琐配置。
- 🌐 **多语言国际化 (i18n)**：
  - 启动后自动将内嵌语言包释放至 `locales/` 目录。
  - 智能识别 Windows 系统语言；支持自定义扩展（如添加西班牙语 `es-ES.ini`）。
  - 托盘右键菜单支持动态扫描与无缝热切换。
- 📦 **Gradle 缓存与 Wrapper 浏览器**：后台异步扫描并可视化展现本地 Gradle Wrapper、KMP/Android 依赖包、缓存体积与物理路径。
- 💻 **多终端一键直达**：自动探测 Windows Terminal、PowerShell 7、Git Bash、CMD 等终端环境，支持在任意网页目录下一键拉起终端。
- 👁️ **实时预览与代码高亮**：内置各类文本、代码文件、音视频预览与多格式渲染支持。
- 🌟 **书签收藏夹**：Web 界面支持快速收藏任意常用目录或磁盘分区。

### 📂 目录结构

```text
LocalDiskServer/
├── src/                # C# 后端源代码
│   ├── Program.cs          # 托盘图标与程序生命周期
│   ├── HttpServer.cs       # 高性能多线程 HTTP/HTTPS 监听服务
│   ├── I18nManager.cs      # 国际化多语言管理器
│   ├── SslManager.cs       # SSL 证书生成、信任与绑定
│   ├── FileExplorer.cs     # 磁盘与文件目录路由
│   ├── GradleExplorer.cs   # Gradle 缓存与 Wrapper 扫描器
│   └── Logger.cs           # 内存滚动日志与 API
├── resources/          # 内嵌静态资产（构建时自动打包进 exe）
│   ├── header.html
│   ├── footer.html
│   ├── gradle.html
│   ├── style.css
│   ├── app.js
│   └── locales/            # 预置多语言文件
│       ├── zh-CN.ini
│       └── en-US.ini
├── scripts/            # 自动化脚本
│   ├── build.ps1           # 一键编译脚本
│   ├── deploy.ps1          # 自动化部署脚本
│   └── run_repair.ps1      # 端口死锁救援脚本
├── dist/               # 构建输出目录
│   └── LocalDiskServer.exe
└── .github/workflows/  # CI/CD 工作流
    └── release.yml         # 发布标签时自动打包 Release
```

### 🔨 本地编译构建

在 Windows PowerShell 中直接执行：
```powershell
./scripts/build.ps1
```
脚本会自动寻找系统内置的 `csc.exe` 编译器，并将所有源码及静态资源递归内嵌，输出单文件 `dist/LocalDiskServer.exe`。

---

<span id="english"></span>

## 🇺🇸 English Description

`LocalDiskServer` is a high-performance, ultra-lightweight (~200KB) Windows system tray application written in pure C#. It transforms your local drives and directories into a modern, responsive Web interface in seconds while providing essential developer utilities.

### ✨ Key Features

- 🚀 **Ultra-lightweight & Single Binary**: The entire application compiles into a single standalone `LocalDiskServer.exe` (~200KB) without requiring Node.js, Python, or heavy runtimes.
- 🔒 **Dual-Channel HTTPS with Auto SSL Healing**: Built-in automated local CA generation and kernel-level SSL binding via `HTTP.sys` for effortless HTTPS encryption.
- 🌐 **Full Internationalization (i18n)**:
  - Automatically extracts embedded locale bundles into the `locales/` directory upon launch.
  - Automatically detects the Windows system UI language and supports dynamic switching from tray menu.
  - Easy extension by simply dropping custom `.ini` files (e.g. `es-ES.ini` for Spanish) into `locales/`.
- 📦 **Gradle Cache & Wrapper Explorer**: Scans, indexes, and visualizes local Gradle Wrappers, KMP/Android dependencies, cache sizes, and local storage paths.
- 💻 **Integrated Multi-Terminal Launch**: Auto-detects Windows Terminal, PowerShell 7, Git Bash, and CMD to open any directory in your favorite shell with one click.
- 👁️ **File Previews & Code Highlighting**: In-browser viewing and syntax highlighting for common text, source code, and media files.
- 🌟 **Favorites & Quick Bookmarks**: Bookmark frequently accessed paths and disk partitions directly in the Web UI.

### 🔨 Build from Source

Run the PowerShell build script:
```powershell
./scripts/build.ps1
```
The script will locate the system `.NET Framework` compiler (`csc.exe`) and package everything into `dist/LocalDiskServer.exe`.

### 🚀 Continuous Integration & Releases

Automated releases are powered by GitHub Actions. Whenever a new Git tag matching `v*` (e.g., `v1.0.0`) is pushed to the repository, a clean Windows runner builds and publishes the binary release automatically.

---

## 📄 License

MIT License.
