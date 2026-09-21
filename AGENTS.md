# AGENTS.md — LocalDiskServer

Windows 托盘应用：纯 C# (.NET Framework 4.x) 单文件 exe，将本地磁盘映射为 Web 站点。
无 .sln / .csproj / NuGet——所有构建由 `scripts/build.ps1` 直接调用系统自带 csc.exe 完成。

## 常用命令

```powershell
./scripts/build.ps1      # 编译 dist/LocalDiskServer.exe（构建前自动终止 dist 下运行中的实例）
./scripts/run_test.ps1   # 唯一测试入口：--test 模式起 18080/18443 隔离实例，断言 HTTP 200 后自动清理
./scripts/deploy.ps1     # 本机部署：覆盖 D:\apps\portable-apps\LocalDiskServer 并重启（机器专用路径）
./scripts/run_repair.ps1 # 需管理员：重建根 CA 证书并重绑 HTTP.sys SSL 端口
```

改动验证闭环：`build.ps1` → `run_test.ps1`（后者在无产物时会自动触发编译）。

## 构建铁律（最容易踩的坑）

- 编译器为 .NET Framework 自带老版 csc.exe，**仅支持 C# 5 语法**：禁用字符串插值 `$""`、`?.`、`nameof`、表达式体成员等；统一使用 `string.Format`。
- `build.ps1` 用**非递归** glob 收集 `src/*.cs`：新增 .cs 文件必须直接放在 `src/` 根目录，放入子目录会被静默漏编。
- `resources/` 下所有文件（递归）在构建时内嵌为程序集资源，资源名 = 正斜杠相对路径；新增前端文件放进 `resources/` 即可，无需改构建脚本。
- 引用集固定：System / System.Core / System.Drawing / System.Windows.Forms / System.Web / Microsoft.CSharp；新增程序集引用需改 build.ps1。

## 版本与发布

- 版本号唯一来源：`src/Program.cs` 的 `public const string APP_VERSION = "x.y.z";`，`AssemblyInfo.cs` 引用它生成 FileVersion。
- CI（`.github/workflows/release.yml`）在推送 `v*` tag 时用正则 `(APP_VERSION = ")[\d.]+(")` 注入版本并校验 exe FileVersion——**勿改动该行书写格式**，否则发布必挂。
- 发版流程 = 打 `v*` tag 推送，CI 自动构建并附 changelog + sha256 发布 Release。

## 架构速览

- `src/Program.cs` — 入口 + WinForms 托盘（ApplicationContext）+ `server_config.ini` 读写 + 单实例转发（第二进程仅开浏览器即退出）
- `src/HttpServer.cs` — HTTP/HTTPS 监听（HTTP.sys），从程序集内嵌资源输出前端页面
- `src/FileExplorer.cs` / `GradleExplorer.cs` / `MavenExplorer.cs` / `NpmExplorer.cs` / `PnpmExplorer.cs` — 目录路由与各包管理生态缓存浏览器
- `src/I18nManager.cs` / `src/SslManager.cs` / `src/Logger.cs` — 多语言、证书自愈、内存滚动日志
- `resources/` — 无构建步骤的原生前端（header/footer 片段 + 每工具一页 html + app.js + style.css）

## 约定与坑

- 用户可见文案一律走 `I18nManager.T("key", args)`；key 必须同时补进 `resources/locales/zh-CN.ini` 与 `en-US.ini`（格式 `key=值`，占位符 `{0}`）。
- 运行时产物（`server_config.ini`、`locales/`、`bin/ssl_hash.txt`、`crash.txt`）均生成在 **exe 同目录**，全部已 gitignore。
- 默认端口 HTTP 1234 / HTTPS 1235；测试 CLI：`--test --port 18080 --https-port 18443 --no-browser`（短参 `-p` / `-sp`）。
- HTTPS 依赖 HTTP.sys `netsh sslcert` 绑定与自建根 CA `CN=LocalDiskServer Root CA`（需提权，SslManager 会自动拉起提权 PowerShell 自愈）；端口绑定异常时用 `run_repair.ps1`。
- `scratch/` 为 AI 会话方案文档目录（已 gitignore）。

## 插件系统

- 架构：每插件一个子进程 `plugins/<id>/backend.exe`，与宿主经 stdin/stdout 字节帧协议（v1）通信；核心在 `src/PluginHost.cs`（发现/生命周期/帧路由），协议规范全文见 README「插件协议 v1」。
- 挂接点固定三处：`HttpServer.ProcessRequest`（`/plugin/<id>` 页面）、`HttpServer.HandleApiRequest` 尾部（`/api/plugin/<id>/*`）、`FileExplorer.RenderSidebar` 插件分组（受 `enable_dev_ecosystem` 门控）；托盘菜单 + 启动初始化 + 退出 ShutdownAll 在 `Program.cs`。
- 新增插件 = 在 `plugins/<id>/` 放 `plugin.json` + `backend.cs`（C# 5，同规则）+ 可选 `index.html`/`assets/`/`lang/`；build.ps1 自动编译并同步到 `dist/plugins/`——**无需改构建脚本**。
- 插件 id 保留字：`gradle` / `npm` / `pnpm` / `maven`；插件词条 ini 由 `I18nManager.RegisterStrings` 以 `plugin_<id>_` 前缀注册，语言切换自动重灌。
- 宿主侧新增插件相关文案仍走双 locale ini（`plugin_*` / `log_plugin_*` 前缀）。
