# Game Translator

Game Translator 是一款面向 Windows Unity 单机游戏的实时翻译工具。程序通过 XUnity.AutoTranslator 获取游戏文本，经本机回环桥接调用 OpenAI Chat Completions 兼容接口，并将译文写回游戏界面。

当前版本：`0.2.0`

发布包包含 `GameTranslator-Setup.exe` 安装器。双击后可选择任意可写磁盘目录，安装程序会解压正式版文件并在桌面创建快捷方式，安装完成后可直接启动。

## 项目状态

- 已完成 `0.2.0` 版本的功能开发与测试。
- 已在两款 Unity Mono x64 游戏中完成实际翻译测试，文本提取、批量翻译、缓存和界面回填均可正常工作。
- 当前版本以维护和问题修复为主，暂无继续扩展功能的计划。
- 不保证兼容所有 Unity 游戏；IL2CPP、反作弊游戏及特殊 Loader 环境尚未适配。

## 主要功能

- 选择或拖入 Unity 游戏 EXE，一键安装并配置 BepInEx、XUnity.AutoTranslator 和匹配字体。
- 支持英语、日语翻译为简体中文。
- 支持 OpenAI Chat Completions 兼容接口，可配置 Base URL、API Key 和模型名称。
- XUnity 批量端点单次最多处理 100 条文本。
- 使用内存缓存和 SQLite 持久缓存，按游戏、语言、接口、模型及提示词版本隔离译文。
- API Key 使用 Windows DPAPI 按当前用户加密保存。
- 提供翻译延迟、缓存命中率、队列长度、翻译数量及目标游戏资源占用监控。

## 系统要求

- Windows 10/11 x64
- Unity Mono x64 单机游戏
- 可用的 OpenAI Chat Completions 兼容 API
- 首次安装组件时需要网络连接

> 请仅用于你有权修改且不含反作弊组件的游戏。WPF 和注入组件只能在 Windows 环境运行。

## 使用方法

1. 启动 `GameTranslator.exe`。
2. 打开“翻译配置”，填写 Base URL、API Key 和模型名称，选择原文语言。
3. 点击“认证测试”，成功后点击“安全保存”。
4. 回到首页，选择或拖入游戏 EXE。
5. 点击“启动并翻译”。首次运行会下载并校验 BepInEx、XUnity.AutoTranslator 和字体文件，后续启动会复用已安装组件。

配置保存在软件目录的 `Data\` 中，翻译缓存保存在 `Cache\` 中。清除缓存前应先关闭游戏，避免 XUnity 的内存缓存再次写回。

## 资源占用与性能

发布版为 Windows x64 自包含程序，无需用户另行安装 .NET Runtime。实际 CPU 和内存占用会受到游戏规模、同屏文本数量、缓存命中率以及 API 响应速度影响，因此项目不提供脱离具体游戏环境的固定数值。

软件“性能监控”页面每秒采样并显示：

- 目标游戏进程 CPU 使用率；
- 目标游戏进程工作集内存；
- 最近一次翻译延迟；
- 缓存命中率；
- 当前翻译队列长度；
- 已完成翻译数量。

翻译缓存限制：

- 进程内缓存约 32 MiB；
- SQLite 文本载荷约 224 MiB；
- SQLite 主数据库硬上限 256 MiB。

## 开发与构建

开发环境：

- .NET 10 SDK
- Visual Studio 2022、Rider 或 VS Code
- Windows，或用于交叉编译的 WSL2

编译 GUI：

```bash
dotnet build src/gui/Gui.csproj -c Release
```

发布 Windows x64 自包含版本：

```bash
bash tools/publish.sh
```

发布目录同时包含：

- `GameTranslator-Setup.exe`：图形化安装器；
- `GameTranslator.exe`：正式版程序；
- `GameTranslatorDebug.exe`：调试版程序。

安装器使用 Inno Setup 构建，不需要预先安装 .NET Runtime。安装目录需要具备写入权限；如安装到 `Program Files`，Windows 可能会弹出管理员权限确认。

指定输出目录：

```bash
bash tools/publish.sh /tmp/GameTranslator
```

发布脚本会从源码重新构建 `CustomTranslate.dll`，然后生成 Release 和 Debug 版本。默认输出到 Windows 桌面的 `GameTranslator` 目录。

## 开发检查

```bash
dotnet build Game_Translator.sln -c Release
dotnet publish tests/RuntimeChecks/RuntimeChecks.csproj -c Release -r win-x64 --self-contained true
```

`RuntimeChecks.exe` 需要在 Windows 上运行，覆盖配置校验、请求重试与取消、批量翻译、SQLite 缓存、数据库迁移及 DPAPI 密钥保存等核心检查。

## 项目结构

```text
Game_Translator/
├── src/
│   ├── gui/                  # WPF 界面、翻译运行时与本机桥接
│   └── xunity-batch/         # XUnity 批量翻译端点
├── tests/RuntimeChecks/      # 核心运行检查
├── tools/publish.sh          # Windows x64 发布脚本
└── 游戏实时翻译工具-技术方案.md
```

## 技术栈

| 组件 | 技术 |
|------|------|
| 桌面界面 | C#、.NET 10、WPF、MVVM |
| 翻译接口 | HttpClient、OpenAI Chat Completions 兼容协议 |
| 游戏文本链路 | BepInEx、XUnity.AutoTranslator、自定义批量端点 |
| 本机通信 | TCP 回环 HTTP |
| 持久缓存 | SQLite |
| 密钥保护 | Windows DPAPI |

更多实现细节参见[技术方案文档](游戏实时翻译工具-技术方案.md)。
