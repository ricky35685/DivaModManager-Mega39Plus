# DivaModManager 1.5.0 社区版

本版为 MEGA39+ 歌曲管理器增加“废案资源”识别。歌曲的可用内容以
`mod_pv_db.txt`、`mod_nc_pv_db.txt` 和 `nc_db.toml` 的实际声明为准，
目录中没有被数据库引用的歌曲文件会单独列出，不再混入正式难度和运行判断。

本独立社区项目的开发、规则研究、测试与发布整理由 `gpt5.6-sol` 辅助完成。

> [!IMPORTANT]
> 仅支持 Windows PC 版 MEGA39+ / Mega Mix+，不要用于 AFT、Future Tone、
> Project DIVA Arcade、PPD 或其他数据库格式。

## 新增功能

- 自动检查 `rom/script`、`rom/script_nc`、`rom/sound/song`、`rom/movie`、
  `rom/2d` 和 `rom/add_param` 中可安全归属的歌曲资源。
- 同 PVID 已有歌曲数据库条目时，未声明文件显示在该歌曲的“废案资源”清单中。
- 完全没有数据库条目的自定义 PVID 会生成只读废案条目。
- 歌曲列表增加废案数量列和“废案资源”筛选，可按文件名、相对路径或完整路径搜索。
- 每个废案文件均可直接在 Windows 资源管理器中定位。

## 不参与的判断

废案文件不会加入正式难度，不参与歌曲完整性、PVID 冲突、歌曲补丁匹配，
也不会加入删除歌曲所使用的独占资源集合。废案条目不能改名、替换图片、
保存人工运行状态或执行整首删除。

跨模组实际为已启用歌曲提供的音频和视频会被视为已使用资源；
`mod_spr_db.bin` 明确引用的自定义图片 FARC 也不会误报。只有音频、没有歌曲
数据库或谱面的 Cover 替换不会被创建为废案歌曲。

## 官曲谱面例外

未在 `nc_db.toml` 登记、却指向 MEGA39+ 官曲 PVID 的 DSC 仍显示红色阻断提示。
这类物理文件会通过 DivaModLoader 的虚拟文件系统直接覆盖官曲，缺少歌曲
数据库记录并不能消除风险。New Classics 明确声明的官曲扩展和已核实的
Eden Project 扩展继续使用原有例外规则。

## 下载与升级

下载 `DivaModManager-v1.5.0-win-x64.zip`，完整解压后运行
`DivaModManager.exe`。需要 Microsoft .NET 6 Desktop Runtime x64，并应保持
`x64`、`x86` 目录与 EXE 位于同一目录。升级时不要覆盖自己的 `Config.json`。

本版本建立在 1.4.0 社区版之上，仍包含 TekkaGB/DivaModManager 的原始代码、
PR #56 更新检查补丁以及 1.4.0 的 MEGA39+ 歌曲管理功能；它不是 TekkaGB、
SEGA 或 Crypton Future Media 的官方发布。
