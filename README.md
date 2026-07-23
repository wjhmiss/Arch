# Unity Arch ECS 框架新手学习教程

> 一份系统、全面、面向新手的 Unity Arch ECS 框架学习教程，包含 23 章详细文档与 21 个 Unity 演示示例。

## 交付物总览

```
d:\Unity\Arch\
├── Arch\                     # Arch 核心源码（参考资料）
├── Arch.Extended\            # Arch 扩展模块（参考资料）
├── Arch.Docs\                # 官方文档（参考资料）
├── unity\                    # 教程配套项目（交付物）
│   ├── Tutorial\             # 教程文档（24份Markdown，约6万字）
│   │   ├── 00-目录与导读.md
│   │   ├── 01-安装与环境搭建.md
│   │   ├── 02-ECS核心概念.md
│   │   ├── 03-第一个Arch程序.md
│   │   ├── 04-World世界源码解析.md
│   │   ├── 05-Entity实体源码解析.md
│   │   ├── 06-Component组件与ComponentRegistry.md
│   │   ├── 07-Archetype与Chunk源码解析.md
│   │   ├── 08-Query查询系统源码解析.md
│   │   ├── 09-事件系统Events.md
│   │   ├── 10-CommandBuffer命令缓冲.md
│   │   ├── 11-多线程与Jobs.md
│   │   ├── 12-批量与批量操作.md
│   │   ├── 13-PureECS与性能优化.md
│   │   ├── 14-Arch.System系统框架.md
│   │   ├── 15-SourceGenerator源生成器.md
│   │   ├── 16-Arch.LowLevel低级集合.md
│   │   ├── 17-Arch.Relationships关系建模.md
│   │   ├── 18-Arch.Persistence持久化.md
│   │   ├── 19-Arch.EventBus事件总线.md
│   │   ├── 20-Unity集成指南.md
│   │   ├── 21-最佳实践与陷阱.md
│   │   ├── 22-调试技巧与工具.md
│   │   └── 23-FAQ常见问题.md
│   └── Assets\
│       ├── Scripts\          # 演示脚本（21章节+5个公共脚本）
│       │   ├── Common\       # 公共基础设施
│       │   │   ├── IDemo.cs
│       │   │   ├── DemoRunner.cs
│       │   │   ├── DebugHUD.cs
│       │   │   ├── GameMain.cs
│       │   │   └── VisualFactory.cs
│       │   ├── Chapter01\    # 各章节演示
│       │   ├── Chapter03\
│       │   ├── ...
│       │   └── Chapter22\
│       ├── Scenes\
│       │   └── SampleScene.unity
│       └── README.md         # Unity项目说明
└── README.md                 # 本文件
```

## 教程结构

教程采用渐进式学习路径，分为五个部分：

### 第一部分：入门篇（章节 1-3）

- 安装与环境搭建
- ECS核心概念
- 第一个Arch程序

### 第二部分：核心篇（章节 4-8）

- World世界源码解析
- Entity实体源码解析
- Component组件与ComponentRegistry
- Archetype与Chunk源码解析
- Query查询系统源码解析

### 第三部分：进阶篇（章节 9-13）

- 事件系统Events
- CommandBuffer命令缓冲
- 多线程与Jobs
- 批量与批量操作
- PureECS与性能优化

### 第四部分：扩展篇（章节 14-19）

- Arch.System系统框架
- SourceGenerator源生成器
- Arch.LowLevel低级集合
- Arch.Relationships关系建模
- Arch.Persistence持久化
- Arch.EventBus事件总线

### 第五部分：实践篇（章节 20-23）

- Unity集成指南
- 最佳实践与陷阱
- 调试技巧与工具
- FAQ常见问题

## 教程特点

1. **源码逐行解析**：基于 `d:\Unity\Arch\Arch` 与 `d:\Unity\Arch\Arch.Extended` 真实源码，对 World、Entity、Archetype、Chunk、Query 等核心类的关键字段与方法进行逐行分析。
2. **理论+实践**：每个章节配有对应的 Unity 演示脚本（共21个），可在编辑器中直接运行观察。
3. **新手友好**：包含详细安装步骤、常见错误解答、调试技巧、最佳实践建议。
4. **格式规范**：统一的 Markdown 格式，使用 💡/⚠️/🔥/📖 图标标记重点，源码引用使用 `[文件名](file:///绝对路径#L行号)` 可点击直达。
5. **覆盖完整**：从安装到核心 API、从扩展模块到 Unity 集成、从最佳实践到调试技巧，构成完整学习闭环。

## 快速开始

### 1. 阅读教程

打开 [unity/Tutorial/00-目录与导读.md](unity/Tutorial/00-目录与导读.md) 开始阅读。

新手推荐路线： 

```
第01章 → 第02章 → 第03章 → 第04章 → 第05章 → 第06章 → 第08章 → 第20章
```

### 2. 运行演示

1. 用 Unity 2021.3 LTS+ 打开 `d:\Unity\Arch\unity` 项目
2. 按 [第01章：安装与环境搭建](unity/Tutorial/01-安装与环境搭建.md) 安装 Arch 运行时
3. 打开 `Assets/Scenes/SampleScene.unity`
4. 确保场景中有挂载 `GameMain` 脚本的 GameObject
5. 点击运行，使用键盘快捷键切换章节：
   - `Space` / `N`：下一章
   - `B` / `P`：上一章
   - `R`：重启本章
   - `1`-`9`：跳到对应章节
   - `F1`：切换 HUD 显示

### 3. 学习源码

教程中的所有源码引用均使用 `[文件名](file:///绝对路径#L行号)` 格式，可在 IDE 中点击直达。建议在 Visual Studio 2022 或 JetBrains Rider 中打开 `d:\Unity\Arch\Arch\Arch.sln` 与 `d:\Unity\Arch\Arch.Extended\Arch.Extended.sln` 阅读源码。

## 技术栈

- **框架**：Arch 2.1.0-beta（基于 Archetype + Chunk 的高性能 ECS）
- **运行时**：.NET Standard 2.1 / .NET 6+ / Unity 2021.3 LTS+
- **依赖库**：CommunityToolkit.HighPerformance、Collections.Pooled、Schedulers
- **IDE**：Visual Studio 2022 / JetBrains Rider / VS Code

## 学习目标

完成本教程后，您将能够：

1. 理解 ECS 架构模式与数据导向设计的优势
2. 在 Unity / .NET 项目中安装与配置 Arch
3. 熟练使用 World、Entity、Component、Query 核心 API
4. 理解 Archetype + Chunk 的内存布局与性能优势
5. 使用 CommandBuffer、Jobs、批量操作等进阶特性
6. 根据场景选择合适的查询方式与优化策略
7. 使用 Arch.System、SourceGenerator 等扩展提升开发效率
8. 调试 Arch 应用并避免常见陷阱

## 致谢

本教程基于以下开源项目整理：

- [Arch](https://github.com/genaray/Arch) - 核心框架
- [Arch.Extended](https://github.com/genaray/Arch.Extended) - 扩展模块
- [Arch.Docs](https://arch-ecs.gitbook.io/arch) - 官方文档

感谢 Arch 的作者 genaray 与所有贡献者。

## License

本教程文档遵循 CC-BY-4.0 协议。源码引用部分遵循原始 Apache 2.0 协议。
