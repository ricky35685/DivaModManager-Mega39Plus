# DivaModManager 1.5.0 社区版

DivaModManager 是《初音未来 Project DIVA MEGA39+ / Hatsune Miku: Project DIVA Mega Mix+》PC 版的模组管理器。本社区版在原项目的模组安装、排序、更新和启动功能之上，加入了本地模组类别识别，以及面向 Custom Songs 的歌曲、封面和运行状态管理。

> [!NOTE]
> 这是由项目维护者在自己的 GitHub 账号独立发布的新项目，开发、规则研究、测试与发布整理均由 `gpt5.6-sol` 辅助完成。最终发布与维护责任由项目维护者承担。

> [!IMPORTANT]
> 本项目只支持 Windows PC 版 MEGA39+ / Mega Mix+。不支持 Arcade Future Tone（AFT）、Future Tone、Project DIVA Arcade、PPD 或其他版本；请勿用它修改这些游戏的数据库或模组。

## 项目来源

- 上游项目：[TekkaGB/DivaModManager](https://github.com/TekkaGB/DivaModManager)
- 模组加载器：[blueskythlikesclouds/DivaModLoader](https://github.com/blueskythlikesclouds/DivaModLoader)
- 本分支纳入的补丁：[TekkaGB/DivaModManager#56](https://github.com/TekkaGB/DivaModManager/pull/56)，作者为 [UnixNight](https://github.com/UnixNight)

截至 2026-07-11，PR #56 仍为开放状态，**尚未合并到 TekkaGB 的上游仓库**。本项目是在上游 `1.3.1` 及 PR #56 补丁基础上继续开发的独立新项目，不是 GitHub fork 合并请求，也不代表 TekkaGB 或 PR 作者发布的官方版本。

## 下载与安装

1. 从当前仓库的 [Releases](../../releases) 下载 `DivaModManager-v1.5.0-win-x64.zip`。
2. 将 ZIP **完整解压**到一个可写目录。不要只从压缩包内运行 EXE。
3. 保持 `x64`、`x86` 目录与 `DivaModManager.exe` 位于同一目录；它们包含程序解压 7z/RAR 所需的原生库。
4. 安装 [Microsoft .NET 6 Desktop Runtime x64](https://dotnet.microsoft.com/download/dotnet/6.0/runtime)，然后运行 `DivaModManager.exe`。
5. 首次启动时按提示选择 MEGA39+ 的 `DivaMegaMix.exe`。管理器会安装或检查 DivaModLoader，并读取现有 `Mods` 目录。

升级旧版本时，先关闭管理器，再用新发布包替换程序文件。不要把其他人的 `Config.json` 放进自己的安装目录；该文件包含本机设置，应由程序在本地创建或沿用。

## 主要功能

### 模组管理

- 从 GameBanana 或 Diva Mod Archive 浏览、安装和更新模组。
- 拖放本地模组压缩包，调整启用状态和加载顺序。
- 创建、重命名、配置模组，并维护多个 loadout。
- 根据模组实际目录结构辅助识别 Custom Song、New Classics、Additional Difficulty、Module、Accessory、UI、Sound、Cover 和 Plugin 等类别。作者或下载站提供的 Category 会被保留，本地识别结果不会回写模组文件。

详细识别依据见 [docs/ModClassification.md](docs/ModClassification.md)。

### MEGA39+ 歌曲管理

在主界面点击音乐图标，或右键模组后选择“管理自定义歌曲”，即可打开歌曲管理器。

- 解析 Legacy `mod_pv_db.txt`，以及 New Classics 使用的数据库和 `nc_db.toml`。
- 将同一 PVID 的多个难度组织为一首歌曲，共用其音频、视频和三类歌曲图片。
- 按 PVID、歌名、作者、模组或资源文件名搜索，并按 Easy、Normal、Hard、Extreme、Extra Extreme、星级、运行状态和废案资源筛选。超出 1-10 或无法解析的星级显示为 `?`。
- 以 `mod_pv_db.txt`、`mod_nc_pv_db.txt` 和 `nc_db.toml` 的精确声明为歌曲资源清单；未声明的谱面、音频、视频、歌曲图片 FARC 和附加参数会单独标为“废案资源”，可查看路径并在资源管理器中定位。
- 编辑歌名、英文名和读音/排序名；写入时保留数据库编码、换行、注释和无关字段。
- 预览并替换小图标（Thumbnail）、封面（Jacket）和背景（Background）。预览会校正 MEGA39+ 贴图中常见的上下翻转；替换支持 PNG、JPEG 和 BMP，并保留同一 FARC/图集中的其他 Sprite。
- 导入完整的 ZIP、7z 或 RAR 歌曲包；删除歌曲时仅移除经完整反向引用确认属于该歌曲的资源。
- 修改数据库、图片或删除歌曲前，在 `%LocalAppData%\DivaModManager\Backups\Songs` 创建备份。

废案资源不参与歌曲难度、完整性、PVID 冲突或独占资源删除计算。完全没有歌曲数据库条目的自定义 PVID 会显示为只读废案条目；跨模组实际被已启用歌曲引用的媒体，以及 `mod_spr_db.bin` 声明的图片 FARC，不会被误标。仅有音频的 Cover 替换也不会被当成废案歌曲。

歌曲补丁、废案条目、共享数据库和 Eden Project 的受保护记录会限制歌名编辑或整首删除，避免破坏其他歌曲。替换图片也必须先找到可验证的现有 Sprite/FARC；程序不会凭一张 PNG 猜测并生成新的 FARC。

## 运行状态判定

歌曲管理器使用三种最终状态，并以中文显示诊断和操作错误：

| 状态 | 含义 | 常见情况 |
| --- | --- | --- |
| 可运行 | 未发现阻断问题 | 必需资源存在，或同 PVID 定义的难度互不重复 |
| 勉强运行 | 可以加载，但内容不完整或组合存在风险 | 部分谱面缺失、任一歌曲图片缺失、可选演唱音频缺失、同难度但已知星级不重复 |
| 无法运行 | 已发现明确阻断问题 | 主音频缺失、全部谱面缺失、数据库明确声明的视频缺失、真实 PVID 冲突、非法占用官曲 PVID |

没有声明视频的完整歌曲会按 3D PV 处理，不会误报“缺少视频”。Additional Difficulty 会继承目标歌曲的媒体，不属于这一例外。

同一 PVID、同一配置模组名、作者信息不矛盾且歌名相同的多份定义，会进一步比较**实际存在的谱面**：

| 比较结果 | 自动判定 |
| --- | --- |
| 难度不重复 | 保持原状态，通常为可运行 |
| 难度重复，但已知星级不重复 | 勉强运行 |
| 难度和星级重复 | 无法运行 |
| 难度重复且任一星级未知/无效 | 无法运行 |

无谱面的歌曲补丁可以与对应歌曲共用 PVID，管理器会提示补丁及其路径，而不会把它当作完整歌曲冲突。`nc_db.toml` 明确登记的 New Classics 官曲扩展也可以复用 MEGA39+ 官曲 PVID；Legacy 或未登记谱面仍会被判定为冲突。

“废案资源”不是第四种运行状态。普通自定义歌曲中的未声明文件不改变可运行、勉强运行或无法运行的判断；但未登记且指向 MEGA39+ 官曲 PVID 的 DSC 会通过虚拟文件系统覆盖官曲，因此仍以红色提示实际覆盖风险。

用户可以把最终状态人工改为“可运行”“勉强运行”或“无法运行”，也可以恢复“自动判断”。人工覆盖保存在 `%LocalAppData%\DivaModManager\song-run-status-overrides.json`，只改变显示和筛选，**不会补齐缺失资源、修复谱面或消除真实 PVID 冲突**；自动诊断会继续保留在详情中。

## Eden Project

Eden Project 被识别为由 Eden Core、曲包、Module Pack 和资源补丁组成的依赖体系，而不是普通的单一曲包。曲包需要已启用且结构完整的 Eden Core；Module Pack 的官曲 PVID 记录和已知 Extra Extreme 扩展按其实际用途处理。管理器会保护共享 Core/Module/图片数据库，并对已知版本混装、缺失 Core 和真实跨模组冲突给出诊断。

这些规则针对研究过的 MEGA39+ Eden Project 结构，不应套用于名称相似的其他游戏或移植版。完整规则和资料来源见 [docs/ModClassification.md](docs/ModClassification.md)。

## 使用前须知

- 运行状态是静态检查结果，不等同于实际启动游戏、执行谱面或验证所有插件兼容性。
- 导入、改名、替换图片和删除都会写入模组目录。程序会创建备份，但仍建议在首次使用前另行备份整个 `Mods` 目录。
- 不要在管理器写入过程中移动文件、关闭进程或同时用其他工具修改同一数据库。
- 只从可信来源下载模组。歌曲导入会检查路径穿越、符号链接、资源声明和 PVID，但任何管理器都不能证明未知 DLL 插件安全。

## 从源码构建

需要 Windows、Git 和 [.NET 6 SDK](https://dotnet.microsoft.com/download/dotnet/6.0)。仓库已包含构建图片编辑功能所需的 MikuMikuLibrary 程序集及其许可证。

```powershell
dotnet restore DivaModManager.sln
dotnet test DivaModManager.sln -c Release
dotnet publish DivaModManager/DivaModManager.csproj -c Release -p:PublishSingleFile=true --self-contained false
```

发布结果位于：

```text
DivaModManager/bin/Release/net6.0-windows/win10-x64/publish/
```

分发时应包含 `DivaModManager.exe`、`x64/7z.dll`、`x86/7z.dll`、`LICENSE`、`README.md`、`RELEASE-NOTES.md` 和 `THIRD-PARTY-NOTICES.md`。这是依赖 .NET 6 Desktop Runtime 的单文件发布，不是 self-contained 发布。

社区 fork 若要启用自更新，必须在构建时显式提供自己的 GitHub Release 仓库：

```powershell
dotnet publish DivaModManager/DivaModManager.csproj -c Release `
  -p:PublishSingleFile=true --self-contained false `
  -p:DmmUpdateOwner=YOUR_GITHUB_OWNER `
  -p:DmmUpdateRepository=YOUR_REPOSITORY
```

未提供这两个属性时，自更新检查会安全地保持关闭，不会把 TekkaGB 上游的 `1.3.1` 误当作本社区版更新。

## 发布与许可

- 版本记录：[CHANGELOG.md](CHANGELOG.md)
- 1.5.0 发布说明：[docs/RELEASE_NOTES_1.5.0.md](docs/RELEASE_NOTES_1.5.0.md)
- 第三方组件：[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)
- 项目许可证：[GNU GPL v3](LICENSE)

原项目及其资源归各自作者所有。“Hatsune Miku”“Project DIVA”“MEGA39+”“Mega Mix+”及相关名称和素材的权利属于其各自权利人；本社区版与 SEGA、Crypton Future Media 或游戏发行方无隶属关系。
