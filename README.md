# PCL2-MC-Download-Patcher

懒人化修补 PCL2：把官方名义上关掉的 Minecraft 下载入口恢复出来。

## 背景

PCL2 从 2.99.0.0 起，因为一些不可抗力，在代码里加入了：

```vb
Public Const RestrictMcDownload As Boolean = True
```

名义上 PCL2 不再提供 MC 下载服务。由于它是 `Const`，编译期就被折叠进了各个调用点，下载相关的入口和分支也随之被裁掉——下载页还能打开，但版本列表、整合包安装等入口要么消失，要么直接弹出“为遵守相关法律法规……”。

## 它能做什么

把官方 PCL2 的 exe 丢进来，它会自动找出被裁掉的 5 处，在 IL 层原样补回去，输出一个下载功能正常的 exe。

- 不需要手动找偏移、不需要反编译重建、不需要改配置
- 只改下载相关的 5 个方法，其余字节、资源、元数据原样保留
- 自动检测状态，已经正常的版本或已经修补过的文件不会重复改
- 支持单文件、`--scan` 体检、`--batch` 批量、`--diff` 对比

## 支持情况

- **仅支持 PCL2 官方项目**（Plain Craft Launcher 2）的官方构建。
- **不支持 PCL-CE 及其他独立分支、二次开发版**。这些分支的类名、方法名、调用链与官方不同，工具会报“不是 PCL2”或把对应点标为 `[未定位]`，不会硬改出一个坏文件。
- 已在 **PCL2 2.99.0.0** 上完整验证；2.13.1.1 作为“本来就没有限制”的对照版本。
- 对未来版本：暂时无法验证，结构识别是尽力而为，官方如果重写相关方法，可能需要补充新的锚点规则，不能保证是开箱即用的通用化方案。

## 工作原理

### 非反编译破解

本项目**没有走反编译、重建工程、重新编译的路线**，也不涉及任何破解行为：

- 不重建整个程序，只读取 exe 中 5 个目标方法的 IL，把被裁掉的方法体/分支补回去，再写回同一个程序集。
- 不绕过授权、账号、付费或身份验证，不修改登录、加密、更新与签名校验逻辑。
- 不包含、也不分发 PCL2 本体；工具本身只有几十 MB 的 patcher，不附带任何官方程序。
- 只作用于使用者自己持有的本地程序副本，属于“本地修补”，不改变官方分发渠道。

它的作用范围仅限于恢复官方在编译期裁掉的下载入口。请在各官方条款允许的范围内使用。

### 5 个补丁点

| # | 方法 | 2.99 中被改成 | 还原为 |
|---|------|---------------|--------|
| 1 | `PCL.ModModpack::ModpackInstall()` | 只剩一句 `Hint("为遵守相关法律法规……")` | `Dialogs.SelectFile("选择整合包压缩文件", …{rar,zip,mrpack}…)` → 写入闭包 → `RunInThread` |
| 2 | `PCL.ModModpack::ModpackInstall(String,String,String)` | 识别完压缩包类型后 `throw new Exception("为遵守……")` | `Select Case PackType` 分发到 `InstallPackCurseForge / HMCL / MMC / MCBBS / Modrinth / LauncherPack / Compress` |
| 3 | `PCL.PageDownloadInstall::LoaderInit()` | `new LoaderTask("关闭下载", Sub() Throw …)` | `PageLoaderInit(…, ModDownload.DlClientListLoader, …)` |
| 4 | `PCL.PageDownloadModify::LoadInstanceFinished(Task)` | `CardMinecraft.Visibility = Collapsed` | `Visible`（`ldc.i4.2` → `ldc.i4.0`） |
| 5 | `PCL.PageDownloadResourceDetail::InitializeStackResourceVersions(StackPanel)` | 版本项只挂 `Save_Click` | 整合包版本额外挂安装按钮 |

### 自动检测

不是按版本号或文件偏移写死，而是先判断每个点当前是“被裁”还是“正常”：

- 补丁 1：是限制提示，还是 `Dialogs.SelectFile` 调用
- 补丁 2：是 `throw`，还是 `InstallPack*` 调用
- 补丁 3：有没有 `DlClientListLoader` 字段引用
- 补丁 4：`CardMinecraft.Visibility` 被赋的是 `Collapsed(2)` 还是 `Visible(0)`
- 补丁 5：版本项 `ToListItem` 前挂了 1 个还是 2 个委托

确实要改时再按指令形状找锚点；写出前还会做 IL 自检、与原文件逐方法比对（只允许 5 个预期方法变化）、锚点匹配。任何一步失败都会直接报错，不产出半成品。

### 不会影响账号登录保存以及主题使用

补丁只替换上述 5 个方法的指令，模块里的其他字节、嵌入资源和元数据保持原样。因此登录凭据、账号缓存、主题隐藏列表等依赖的加密链路完全按官方原样保留：

- 设备识别码仍由官方 `PCL.ModSecret.GetIdentify()` 生成；
- 设置项的加密/解密仍走官方 `PCL.Settings.DesEncrypt/DesDecrypt`；
- 登录 token、Nide 账号密码、主题隐藏列表等标记为 `Encrypted` 的设置项，仍由官方逻辑读写；
- 独立配置使用的 AES 通道（`MeloongCore.CryptographyUtils`）也没有改动。

**出于账号安全考虑，本项目 README 不列出密钥、IV、哈希盐值、拼接格式等具体参数。这些属于 PCL2 的内部实现细节，且 PCL2 官方开源页面也没有在公开页面展示；本项目没有修改、也没有重新实现它们。**

`--diff` 会同时比对嵌入资源的 SHA-256（内嵌的 `MeloongCore.dll`、`PCLCS.dll`、`Newtonsoft.Json.dll` 等也逐个核对过），原版与补丁版完全一致。

## 下载

到 [Releases](../../releases) 下载 `PCL2-MC-Download-Patcher-win-x64.zip`，解压后是单个 exe，不需要安装 .NET 运行时。

也可以自己构建，见[构建](#构建)。

## 使用

```powershell
# 自动检测并修补（输出参数可省略）
.\PCL2-MC-Download-Patcher.exe "C:\Path\PCL2.exe" "C:\Path\PCL2-Restored.exe"

# 只体检，不写文件
.\PCL2-MC-Download-Patcher.exe --scan "C:\Path\PCL2.exe"

# 批量：目录里所有 exe 过一遍，没限制的自动跳过
.\PCL2-MC-Download-Patcher.exe --batch "D:\Launchers" "D:\Launchers\out"

# 逐方法比较原版和补丁版
.\PCL2-MC-Download-Patcher.exe --diff "C:\Path\PCL2.exe" "C:\Path\PCL2-Restored.exe"

# 版本 / 帮助
.\PCL2-MC-Download-Patcher.exe --version
```

把 exe 直接拖到 patcher 上等价于单文件模式。输出默认放在输入文件同目录，文件名加 `-McDownloadRestored`。

退出码：

| 码 | 含义 |
|---|---|
| 0 | 成功，或无需处理 |
| 1 | 失败（读取失败 / 锚点不匹配 / 校验失败） |
| 2 | 参数错误 |
| 3 | `--diff` 发现差异 |
| 4 | 已写出，但仍有未能定位的限制点（同时打印 `[WARN]`） |

PCL2 的 exe 是自包含的（`PCLCS`、`MeloongCore`、`Newtonsoft.Json` 等都内嵌在 `PCL.Resources.resources` 中），输出文件放哪儿都能运行。想直接替换的话，先备份原 exe，再把输出改名即可。

## 构建

需要 .NET 10 SDK：

```powershell
# 普通构建
dotnet build -c Release

# 生成单文件 exe
dotnet publish -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true -p:DebugType=None -o dist
```

或者用附带的脚本：

```powershell
.\build-release.ps1          # 生成 dist\PCL2-MC-Download-Patcher.exe 并打包 zip
.\patch.ps1 "C:\Path\PCL2.exe" "C:\Path\PCL2-Restored.exe"
```

推送 `v*` 标签时，GitHub Actions 会自动构建并把 zip 附到 Release。

## 项目结构

```
PCL2-MC-Download-Patcher/
├── Program.cs                  # 检测、5 个补丁、IL 校验、--diff
├── PCL2-MC-Download-Patcher.csproj
├── patch.ps1                   # 本地构建 + 打补丁
├── build-release.ps1           # 发布单文件 + 打包
├── .github/workflows/build.yml # CI / Release
├── LICENSE
└── README.md
```

## 局限

再次提醒：

- **只针对 PCL2 官方项目**。PCL-CE 及其他独立分支、二次开发版的方法结构不同，本项目明确不支持；强行套用只会得到 `[未定位]` 或失败退出。
- **验证范围有限**。目前只在 2.99.0.0 上完整验证过。检测逻辑虽然尽量按结构而不是版本号匹配，但**不能保证对未来更新通用**；官方一旦重写相关方法，就需要补充锚点。
- 补丁后的 PCL2 是否还能下载，仍取决于官方服务端与版本策略，本项目不提供任何下载源。

## VibeCoding声明
本人不作掩饰，本项目代码主要由 AI 在提示驱动下生成，本人只负责方向和验收，而非逐行手写。

## 许可

本项目代码采用 [MIT License](LICENSE)。

PCL2（Plain Craft Launcher 2）的版权归其原作者所有。本项目不包含、也不分发 PCL2 本体；请勿传播修改后的 PCL2，使用本工具产生的一切后果由使用者自行承担。
