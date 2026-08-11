# Game Translator

现代最强 AI 游戏实时翻译工具。

## 开发环境

- **OS**: Windows 11（游戏运行环境）
- **开发**: WSL2 / Windows 均可
- **SDK**: .NET 10 SDK
- **IDE**: Visual Studio 2022 / Rider / VSCode

## 快速开始

### 编译 GUI

```bash
# WSL2 内交叉编译（需安装 .NET 10 SDK）
cd src/gui
dotnet build                          # 编译
dotnet publish -c Release -r win-x64 --self-contained true  # 发布自包含 exe
```

### 运行 GUI

**方式一：Windows 直接运行**
```bash
# 在 Windows 终端（PowerShell/CMD）中
cd src/gui
dotnet run                            # 开发模式运行
```

**方式二：WSL2 发布后运行**
```bash
# WSL2 中一键打包并复制到桌面目录
bash tools/publish.sh
# 输出：桌面 GameTranslator/GameTranslator.exe
# 打包脚本第二个参数可指定目标目录，如：bash tools/publish.sh /tmp/out
```

> ⚠️ WPF 是 Windows 专用框架，无法在 Linux 上直接运行。WSL2 可以交叉编译，但运行必须在 Windows 侧。

## 项目结构

```
Game_Translator/
├── src/
│   ├── gui/                     # WPF GUI + 翻译运行时宿主
│   │   ├── App.xaml             #   应用入口 + 全局样式/调色板
│   │   ├── Gui.csproj           #   WPF 项目文件
│   │   ├── Services/            #   XUnity 本机桥接
│   │   │   └── Translation/     #   运行时协调器、API、SQLite、配置与模型
│   │   ├── ViewModels/          #   MVVM ViewModel 层
│   │   │   ├── MainViewModel.cs       # 侧边栏导航
│   │   │   ├── HomePageViewModel.cs   # Windows 进程选择
│   │   │   ├── ApiConfigPageViewModel.cs  # API密钥/URL配置
│   │   │   ├── TranslationPageViewModel.cs # 真实翻译测试
│   │   │   └── MonitorPageViewModel.cs  # 性能监控
│   │   └── Views/               #   XAML 视图层
│   │       ├── MainWindow.xaml        # 主窗口（无边框+侧边栏）
│   │       ├── HomePage.xaml          # 首页
│   │       ├── ApiConfigPage.xaml     # 翻译配置页
│   │       ├── TranslationPage.xaml   # 真实翻译测试页
│   │       └── MonitorPage.xaml       # 性能监控页
├── tests/RuntimeChecks/           # 无第三方测试框架的核心逻辑检查
└── 游戏实时翻译工具-技术方案.md  # 当前实施方案
```

## 技术栈

| 组件 | 语言 | 选型 |
|------|------|------|
| GUI + 翻译运行时 | C# (.NET 10) | WPF、HttpClient、Channels |
| Unity 文本链路 | C# | XUnity.AutoTranslator 官方扩展点 |
| 首个游戏桥接 | C# | XUnity 内置 CustomTranslate + 本机回环 HTTP |
| 持久缓存 | SQL | SQLite |
| 后续引擎 | 按需选择 | 有真实目标游戏后再开发 |

详见 [技术方案文档](游戏实时翻译工具-技术方案.md)。

## 配置翻译 API

API 信息不写在源码中，由每位用户在软件内自行提供：

1. 打开“翻译配置”，填写服务商的 Base URL、API Key、模型，并选择英语或日语原文；译文固定为简体中文。
2. 点击“认证测试”，确认地址、密钥和模型可用。
3. 点击“安全保存”，再到“翻译测试”输入真实文本。

配置保存在软件目录的 `Data\config.json`；翻译缓存统一保存在 `Cache\`，并可从“性能监控”清除。API Key 使用 Windows DPAPI 当前用户加密后与配置一次原子写入。

翻译缓存按实际内容字节限制：进程内约 32 MiB，SQLite 文本载荷约 224 MiB，主数据库硬上限 256 MiB。

## Unity 游戏测试

仅用于你有权修改、且不含反作弊的单机 Unity 游戏。首轮优先测试 Unity Mono：

1. 启动 Game Translator，在“翻译配置”完成 API 认证和保存。
2. 回到首页，将 Unity Mono x64 游戏 EXE 拖入窗口，点击“启动并翻译”。首次运行会下载并校验官方 BepInEx 与 XUnity 包，安装、配置桥接后自动启动游戏；之后直接复用已有组件。

桥接会使用 XUnity 请求中的 `FromLanguage`/`Language`，没有提供时才回退到软件内保存的语言设置。

不同游戏的 Unity 版本、Mono/IL2CPP、位数和 Loader 组合不同；无法启动时不要继续注入，先记录目标游戏信息再适配。

## 开发检查

```bash
dotnet build Game_Translator.sln -c Release
dotnet publish tests/RuntimeChecks/RuntimeChecks.csproj -c Release -r win-x64 --self-contained true
```

`RuntimeChecks.exe` 需在 Windows 运行，检查配置边界、重试/取消/非法响应、SQLite 迁移与碰撞防护、重启缓存和 DPAPI 密钥。

## 开发进度

| 阶段 | 内容 | 状态 |
|------|------|------|
| 技术方案 | 最小架构 + 验收指标 + 升级条件 | ✅ 完成 |
| GUI 基础 | WPF 4 页面暗色主题界面 | ✅ 完成 |
| 游戏监控 | 直接启动所选 EXE、记录 PID、采样真实 CPU 与工作集内存 | ✅ 完成 |
| 真实翻译核心 | 用户 API 配置 + DPAPI + HttpClient + 有界队列 + SQLite 缓存 + 手动翻译 | ✅ 代码完成，待真实 API 实测 |
| Unity 纵向闭环 | XUnity 内置 CustomTranslate + 本机安全桥接 | 🚧 桥接已完成，待真实游戏验收 |
| 翻译质量 | 上下文 + 标记保护 | ⬜ 待开发 |
| 兼容矩阵与发布 | Mono/IL2CPP 实测 + 安装/卸载 | ⬜ 待开发 |
| 其他引擎/OCR | 满足技术方案升级条件后按需开发 | ⏸ 延后 |
