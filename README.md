# Game Translator

现代最强 AI 游戏实时翻译工具。

## 开发环境

- **OS**: Windows 11（游戏运行环境）
- **开发**: WSL2 / Windows 均可
- **SDK**: .NET 8.0 SDK
- **IDE**: Visual Studio 2022 / Rider / VSCode

## 快速开始

### 编译 GUI

```bash
# WSL2 内交叉编译（需安装 .NET 8 SDK）
cd src/gui
dotnet build                          # 编译
dotnet publish -c Release -r win-x64 --self-contained true  # 发布自包含 exe
```

### 运行 GUI

**方式一：Windows 直接运行**
```bash
# 在 Windows 终端（PowerShell/CMD）中
cd src\gui
dotnet run                            # 开发模式运行
```

**方式二：WSL2 发布后运行**
```bash
# WSL2 中编译发布
dotnet publish -c Release -r win-x64 --self-contained true
# 复制到 Windows 本地路径后运行
cp -r bin/Release/net8.0-windows/win-x64/publish /mnt/c/Users/<用户名>/Desktop/GameTranslator
# 在 Windows 桌面双击 GameTranslator.exe
```

> ⚠️ WPF 是 Windows 专用框架，无法在 Linux 上直接运行。WSL2 可以交叉编译，但运行必须在 Windows 侧。

## 项目结构

```
Game_Translator/
├── src/
│   ├── gui/                     # GUI (C# WPF .NET 8) ← 已完成基础框架
│   │   ├── App.xaml             #   应用入口 + 全局样式/调色板
│   │   ├── Gui.csproj           #   WPF 项目文件
│   │   ├── ViewModels/          #   MVVM ViewModel 层
│   │   │   ├── MainViewModel.cs       # 侧边栏导航
│   │   │   ├── HomePageViewModel.cs   # 进程选择+注入
│   │   │   ├── ApiConfigPageViewModel.cs  # API密钥/URL配置
│   │   │   ├── TranslationPageViewModel.cs # 翻译设置
│   │   │   ├── MonitorPageViewModel.cs  # 性能监控
│   │   │   └── GlossaryPageViewModel.cs # 术语管理
│   │   └── Views/               #   XAML 视图层
│   │       ├── MainWindow.xaml        # 主窗口（无边框+侧边栏）
│   │       ├── HomePage.xaml          # 首页
│   │       ├── ApiConfigPage.xaml     # 翻译配置页
│   │       ├── TranslationPage.xaml   # 翻译设置页
│   │       ├── MonitorPage.xaml       # 性能监控页
│   │       └── GlossaryPage.xaml      # 术语管理页
│   ├── translator/              # 翻译进程 (C# .NET 8) ← 待开发
│   │   ├── Core/                #   调度引擎、共享内存通信、LRU缓存
│   │   ├── Api/                 #   第三方翻译 API 调用层
│   │   ├── RichText/            #   富文本标记提取/回填
│   │   └── OCR/                 #   OCR 封装（ONNX Runtime + DirectML）
│   ├── unity-plugin/            # Unity 插件（基于 XUnity.AutoTranslator）← 待开发
│   ├── injector/                # 注入 DLL (C++ + MinHook) ← 待开发
│   ├── overlay/                 # 透明窗口叠加 (C++ DWM) ← 待开发
│   ├── hooks/                   # 各引擎 Hook 脚本 ← 待开发
│   │   ├── renpy/               #   Renpy Hook (Python)
│   │   ├── rpgmaker/            #   RPGMaker Hook (JavaScript)
│   │   └── frida/               #   Frida 通用 Hook (JavaScript)
│   └── shared/                  # 跨语言共享定义 ← 已完成
│       ├── SharedMemory.h       #   共享内存结构体
│       └── Protocol.md          #   通信协议
├── assets/fonts/                # NotoSansCJK 字体 ← 待添加
├── tests/                       # 测试
├── tools/                       # 构建/部署工具脚本
└── 游戏实时翻译工具-技术方案.md    # 完整技术方案
```

## 技术栈

| 组件 | 语言 | 选型 |
|------|------|------|
| GUI | C# (.NET 8) | **WPF** (DirectX 硬件加速，Windows 原生) |
| 翻译进程 | C# (.NET 8) | 自研调度引擎 |
| Unity 插件 | C# | XUnity.AutoTranslator + MelonLoader |
| 注入 DLL | C++ | MinHook |
| Frida 脚本 | JavaScript | Frida 动态插桩 |
| Renpy Hook | Python | monkey-patch |

详见 [技术方案文档](游戏实时翻译工具-技术方案.md)。

## 开发进度

| 阶段 | 内容 | 状态 |
|------|------|------|
| 项目框架 | 目录结构 + 共享内存定义 + 技术方案 | ✅ 完成 |
| GUI 基础 | WPF 5 页面暗色主题界面 | ✅ 完成 |
| 翻译调度引擎 | 共享内存队列 + LRU缓存 + 批量聚合 | ⬜ 待开发 |
| Unity 核心链路 | XUnity.AutoTranslator 二次开发 | ⬜ 待开发 |
| 富文本 + 排版 | 标记提取/回填 + 排版自适应 | ⬜ 待开发 |
| 多引擎支持 | Renpy/RPGMaker/Frida Hook | ⬜ 待开发 |
| OCR 兜底 | ONNX Runtime + DirectML | ⬜ 待开发 |
