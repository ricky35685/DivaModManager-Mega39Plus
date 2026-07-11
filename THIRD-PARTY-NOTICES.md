# 第三方组件声明

DivaModManager 社区版使用或随二进制发布以下第三方组件。下表是便于审阅的摘要，不替代各项目的完整许可证。源码发布中的 NuGet 元数据、链接到的许可证正文，以及各组件目录内的许可证文件共同构成完整声明。

## 运行时组件

| 组件 | 版本/来源 | 许可证 | 用途 |
| --- | --- | --- | --- |
| [FontAwesome5](https://github.com/MartinTopfstedt/FontAwesome5) | 2.1.11 | MIT | WPF 图标控件 |
| [GongSolutions.WPF.DragDrop](https://github.com/punker76/gong-wpf-dragdrop) | 3.1.1 | BSD-3-Clause | 模组列表拖放 |
| [Octokit.net](https://github.com/octokit/octokit.net) | 0.51.0 | MIT | GitHub Release API |
| [Onova](https://github.com/Tyrrrz/Onova) | 2.6.2 | LGPL-3.0-only | 应用更新 |
| [SevenZipExtractor](https://github.com/adoconnection/SevenZipExtractor) | 1.0.17 | MIT | 7-Zip 托管封装 |
| [7-Zip](https://www.7-zip.org/) | 随 SevenZipExtractor 提供的 `x64/7z.dll`、`x86/7z.dll` | LGPL-2.1-or-later；部分代码适用 BSD-3-Clause 和 unRAR restriction | 压缩包解压 |
| [SharpCompress](https://github.com/adamhathcock/sharpcompress) | 0.49.1 | MIT | ZIP/7z/RAR 读取 |
| [BCnEncoder.NET](https://github.com/Nominom/BCnEncoder.NET) | 2.3.0 | MIT OR Unlicense | BCn 贴图解码和编码 |
| [System.Drawing.Common](https://github.com/dotnet/runtime) | 6.0.0 | MIT | Windows 位图处理 |
| [CommunityToolkit.HighPerformance](https://github.com/CommunityToolkit/dotnet) | 8.4.0（传递依赖） | MIT | 高性能内存处理辅助 |
| [Microsoft.Win32.SystemEvents](https://github.com/dotnet/runtime) | 6.0.0（传递依赖） | MIT | Windows 系统事件 |
| [System.Runtime.CompilerServices.Unsafe](https://github.com/dotnet/maintenance-packages) | 6.1.0（传递依赖） | MIT | 底层内存操作辅助 |
| [Tomlyn](https://github.com/xoofx/Tomlyn) | 0.14.3 | BSD-2-Clause | TOML 读取 |
| [WpfAnimatedGif](https://github.com/XamlAnimatedGif/WpfAnimatedGif) | 2.0.2 | Apache-2.0 | GIF 预览 |
| [MikuMikuLibrary](https://github.com/blueskythlikesclouds/MikuMikuLibrary) | commit `82380f1`，为 .NET 6 重新构建 | MIT | MEGA39+ FARC、SpriteSet 和 SpriteDatabase 读取/写入 |

MikuMikuLibrary 的本地来源说明、许可证和程序集哈希位于 [`DivaModManager/ThirdParty/MikuMikuLibrary`](DivaModManager/ThirdParty/MikuMikuLibrary)。

7-Zip 的完整许可说明见 [7-Zip License](https://www.7-zip.org/license.txt)。尤其应注意，7-Zip 中用于 RAR 解压的部分代码带有 unRAR restriction；本项目仅调用其解压能力，不授予超出原许可的权利。

## 字体与资源

| 资源 | 来源/许可证 |
| --- | --- |
| Anek Latin | [Google Fonts / OFL-1.1](https://github.com/google/fonts/tree/main/ofl/aneklatin) |
| Roboto Mono | [Google Fonts / Apache-2.0](https://github.com/googlefonts/RobotoMono) |
| 上游 DivaModManager 图片、图标和品牌资源 | 随 [TekkaGB/DivaModManager](https://github.com/TekkaGB/DivaModManager) 源码提供；项目整体许可证见根目录 `LICENSE` |

Font Awesome 名称和图标可能还受其自身商标或图标许可约束；使用这些资源不表示第三方项目为本社区版背书。

## 许可证文本与版权

- DivaModManager 源码按根目录 [GNU GPL v3](LICENSE) 发布。
- MikuMikuLibrary 的 MIT 正文随源码保存在 [`LICENSE.md`](DivaModManager/ThirdParty/MikuMikuLibrary/LICENSE.md)。
- GongSolutions.WPF.DragDrop 的 BSD-3-Clause、Tomlyn 的 BSD-2-Clause、WpfAnimatedGif 的 Apache-2.0、Onova 的 LGPL-3.0-only，以及 NuGet 组件的其他许可证正文，可从上表链接和相应 NuGet 包内获取。
- Microsoft、GitHub、SEGA、Crypton Future Media 和其他名称、标志及商标均属于其各自权利人。

二进制分发者应把本文件、根目录 `LICENSE` 和 MikuMikuLibrary 的 `LICENSE.md` 与发布包一同提供，并保留所有原有版权和许可证声明。
