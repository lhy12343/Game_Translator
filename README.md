# Game Translator

现代最强 AI 游戏实时翻译工具。

## 快速开始

（待补充）

## 项目结构

```
Game_Translator/
├── docs/                        # 项目文档
├── src/
│   ├── translator/              # 翻译进程 (C# .NET 8)
│   │   ├── Core/                # 调度引擎、共享内存通信、LRU缓存
│   │   ├── Api/                 # 第三方翻译 API 调用层（用户自填密钥+URL）
│   │   ├── RichText/            # 富文本标记提取/回填
│   │   ├── OCR/                 # OCR 封装（ONNX Runtime + DirectML）
│   │   └── Program.cs           # 翻译进程入口
│   ├── gui/                     # GUI (C# Avalonia)
│   ├── unity-plugin/            # Unity 插件（基于 XUnity.AutoTranslator 二次开发）
│   ├── injector/                # 注入 DLL (C++ + MinHook，非 Unity 游戏用)
│   ├── overlay/                 # 透明窗口叠加 (C++ DWM 硬件合成)
│   ├── hooks/                   # 各引擎 Hook 脚本
│   │   ├── renpy/               # Renpy Hook (Python monkey-patch)
│   │   ├── rpgmaker/            # RPGMaker Hook (JavaScript)
│   │   └── frida/               # Frida 通用 Hook 脚本 (JavaScript)
│   └── shared/                  # 跨语言共享定义
│       ├── SharedMemory.h       # 共享内存结构体
│       └── Protocol.md          # 通信协议
├── assets/
│   └── fonts/                   # NotoSansCJK 字体
├── tests/                       # 测试
└── tools/                       # 构建/部署工具脚本
```

## 技术栈

| 组件 | 语言 | 选型 |
|------|------|------|
| 翻译进程 + GUI | C# (.NET 8) | 自研调度引擎 + Avalonia |
| Unity 插件 | C# | XUnity.AutoTranslator + MelonLoader |
| 注入 DLL | C++ | MinHook |
| Frida 脚本 | JavaScript | Frida 动态插桩 |
| Renpy Hook | Python | monkey-patch |

详见 [技术方案文档](游戏实时翻译工具-技术方案.md)。
