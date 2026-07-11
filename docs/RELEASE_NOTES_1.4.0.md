# DivaModManager 1.4.0 社区版

这是面向 Windows PC 版《初音未来 Project DIVA MEGA39+ / Hatsune Miku: Project DIVA Mega Mix+》的社区版本，新增 Custom Songs 歌曲、图片和运行状态管理。

本 Release 是项目维护者在自己的 GitHub 账号发布的独立新项目，开发、规则研究、测试与发布整理由 `gpt5.6-sol` 辅助完成。

> [!IMPORTANT]
> 仅支持 PC 版 MEGA39+ / Mega Mix+。不支持 AFT、Future Tone、Project DIVA Arcade、PPD 或其他数据库格式。

## 下载

下载 `DivaModManager-v1.4.0-win-x64.zip`，完整解压后运行 `DivaModManager.exe`。请保持 `x64`、`x86` 目录与 EXE 位于同一目录，并预先安装 Microsoft .NET 6 Desktop Runtime x64。

升级前请关闭 DivaModManager。不要从 ZIP 内直接运行，也不要用发布包中的文件覆盖自己的 `Config.json`。

发布资产应包含：

- `DivaModManager-v1.4.0-win-x64.zip`
- `DivaModManager-v1.4.0-symbols.zip`
- `SHA256SUMS.txt`

可用以下 PowerShell 命令核对下载文件：

```powershell
Get-FileHash .\DivaModManager-v1.4.0-win-x64.zip -Algorithm SHA256
```

## 本版重点

- 新增 MEGA39+ 歌曲管理器，统一显示 Legacy、New Classics 和 Additional Difficulty 歌曲及其多难度谱面。
- 支持 PVID、歌名、作者和模组搜索，以及难度、星级和运行状态筛选。
- 支持修改歌名、英文名和读音/排序名，导入完整 ZIP/7z/RAR 歌曲包，并安全删除独占资源。
- 支持预览和替换小图标、封面、背景；自动校正 MEGA39+ Sprite 常见的上下翻转，同时保护共享 FARC/图集中的其他 Sprite。
- 新增中文健康诊断：可运行、勉强运行、无法运行。冲突和补丁来源均显示完整数据库路径，并可快速在资源管理器中打开。
- 支持持久化人工状态覆盖，并保留原始自动诊断。
- 新增本地结构化 Category 识别，减少作者元数据不规范导致的误分类。
- 专门识别 New Classics 官曲扩展、歌曲补丁和 Eden Project 的 Core/曲包/Module 依赖关系。

## 关键运行规则

- 主音频缺失、全部谱面缺失、数据库明确声明的视频缺失、目标歌曲缺失或真实 PVID 冲突：**无法运行**。
- 至少一个谱面可用但其他声明谱面缺失，或任一小图标/封面/背景缺失：**勉强运行**。
- 没有声明视频的完整歌曲：按 **3D PV** 处理，不要求视频文件。
- 同来源同名同 PVID：难度不重复时兼容；相同难度但已知星级不重复时为勉强运行；星级重复或未知时为无法运行。
- 无谱面的对应歌曲补丁不算 PVID 冲突，但会提示补丁路径。
- `nc_db.toml` 明确登记的 New Classics 扩展可以使用 MEGA39+ 官曲 PVID；Legacy 或未登记官曲谱面不能运行。

人工覆盖只改变最终显示和筛选，不会修复缺失文件或实际冲突。选择歌曲后仍可查看“自动判断”和完整中文诊断。

## Eden Project

本版把 Eden Project 作为依赖生态处理：曲包要求已启用且完整的 Eden Core，Module Pack 和资源补丁不会被误判成普通歌曲；已知 Core Extra Extreme 扩展和恢复歌曲按实际结构识别。共享 Core、Module 和图片数据库中的补丁条目为只读，防止误删其他歌曲所需资源。

Eden 版本混装、缺失 Core 和真实跨模组 PVID 重复仍会报告。详细依据见 [`docs/ModClassification.md`](https://github.com/ricky35685/DivaModManager-Mega39Plus/blob/v1.4.0/docs/ModClassification.md)。

## 数据安全

- 歌名、图片和删除操作会在 `%LocalAppData%\DivaModManager\Backups\Songs` 中创建备份。
- 导入会拒绝路径穿越、符号链接/联接点、无效资源声明和非法官曲 PVID 谱面。
- 删除只处理完整反向引用扫描后确认独占的文件；共享资源和未知的显式 `rom/...` 资源会保留。
- 写入期间若数据库或 FARC 被其他程序修改，操作会停止并尝试回滚。

运行状态属于静态检查，不是对游戏、插件和所有运行时组合的完整模拟。首次使用前仍建议另行备份整个 `Mods` 目录。

## 来源与版本说明

本版基于 [TekkaGB/DivaModManager](https://github.com/TekkaGB/DivaModManager) `1.3.1`，并纳入 [PR #56](https://github.com/TekkaGB/DivaModManager/pull/56) 中 UnixNight 提交的 Diva Mod Archive 更新检查补丁。

截至 2026-07-11，PR #56 仍未合并到 TekkaGB 上游。本 Release 是独立社区项目，不是 GitHub fork 合并请求，也不是 TekkaGB、UnixNight、SEGA 或 Crypton Future Media 的官方发布。

完整变更见 [`CHANGELOG.md`](https://github.com/ricky35685/DivaModManager-Mega39Plus/blob/v1.4.0/CHANGELOG.md)，许可证和第三方组件见 [`LICENSE`](https://github.com/ricky35685/DivaModManager-Mega39Plus/blob/v1.4.0/LICENSE) 与 [`THIRD-PARTY-NOTICES.md`](https://github.com/ricky35685/DivaModManager-Mega39Plus/blob/v1.4.0/THIRD-PARTY-NOTICES.md)。
