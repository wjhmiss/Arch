# Unity Arch ECS 教程演示项目

这是与《Unity Arch ECS 框架新手学习教程》配套的演示项目。

## 快速开始

### 1. 安装 Arch 运行时

由于 Unity 不能直接使用 NuGet，请按以下任一方式安装 Arch：

#### 方式A：NuGetForUnity（推荐）

1. 打开 `Window > Package Manager`
2. `+` → `Add package from git URL...` → `https://github.com/GlitchEnzo/NuGetForUnity.git`
3. 菜单 `NuGet > Manage NuGet Packages`，搜索并安装：
   - `Arch`
   - `CommunityToolkit.HighPerformance`
   - `Collections.Pooled`
   - `Schedulers`

#### 方式B：拷贝源码（学习源码推荐）

将 `d:\Unity\Arch\Arch\src\Arch\` 下所有 `.cs` 文件拷贝到 `Assets/ArchSource/`。
仍然需要通过 NuGetForUnity 安装运行时依赖（`CommunityToolkit.HighPerformance` 等）。

> 详细安装步骤请阅读 [Tutorial/01-安装与环境搭建.md](../Tutorial/01-安装与环境搭建.md)

### 2. 打开场景

打开 `Assets/Scenes/SampleScene.unity`（已内置 GameMain）。

如果场景为空：
1. 创建一个空 GameObject，重命名为 `[GameMain]`
2. 添加组件 `GameMain`（脚本位于 `Assets/Scripts/Common/GameMain.cs`）
3. 运行场景

### 3. 操作演示

| 按键 | 功能 |
|-----|------|
| `Space` / `N` | 跳到下一章 |
| `B` / `P` | 返回上一章 |
| `R` | 重启当前章节 |
| `1` - `9` | 直接跳到对应章节（按注册顺序） |
| `F1` | 切换 DebugHUD 显示 |

## 项目结构

```
Assets/
├── Scripts/
│   ├── Common/                  # 公共基础设施
│   │   ├── IDemo.cs             # 演示接口
│   │   ├── DemoRunner.cs        # 演示调度器（键盘控制）
│   │   ├── DebugHUD.cs          # 调试HUD（OnGUI显示）
│   │   ├── GameMain.cs          # 入口MonoBehaviour
│   │   └── VisualFactory.cs     # 可视化工厂
│   ├── Chapter01/               # 第01章：安装验证
│   ├── Chapter03/               # 第03章：第一个Arch程序
│   ├── Chapter04/               # 第04章：World
│   ├── Chapter05/               # 第05章：Entity
│   ├── Chapter06/               # 第06章：Component
│   ├── Chapter07/               # 第07章：Archetype
│   ├── Chapter08/               # 第08章：Query
│   ├── Chapter09/               # 第09章：Events
│   ├── Chapter10/               # 第10章：CommandBuffer
│   ├── Chapter11/               # 第11章：多线程
│   ├── Chapter12/               # 第12章：批量操作
│   ├── Chapter13/               # 第13章：性能优化
│   ├── Chapter14/               # 第14章：System
│   ├── Chapter15/               # 第15章：SourceGenerator
│   ├── Chapter16/               # 第16章：LowLevel
│   ├── Chapter17/               # 第17章：Relationships
│   ├── Chapter18/               # 第18章：Persistence
│   ├── Chapter19/               # 第19章：EventBus
│   ├── Chapter20/               # 第20章：Unity集成
│   ├── Chapter21/               # 第21章：最佳实践
│   └── Chapter22/               # 第22章：调试
├── Scenes/
│   └── SampleScene.unity
└── Plugins/                     # 第三方DLL（NuGetForUnity会自动放这里）

../Tutorial/                     # 教程文档（与unity项目同级）
├── 00-目录与导读.md
├── 01-安装与环境搭建.md
├── ...
└── 23-FAQ常见问题.md
```

## 章节演示列表

| 章节 | 演示类 | 演示要点 |
|-----|--------|---------|
| 01 | `InstallationDemo` | World创建、实体创建、查询、销毁 |
| 03 | `FirstArchDemo` | 冒险者移动 - Position+Velocity |
| 04 | `WorldDemo` | 多World支持、World.WorldSize |
| 05 | `EntityDemo` | Id、Version、相等性、复用 |
| 06 | `ComponentDemo` | Add/Set/Get/Remove/Has、ComponentRegistry.Size |
| 07 | `ArchetypeDemo` | 相同组件组合归入同一Archetype |
| 08 | `QueryDemo` | WithAll/WithNone/WithAny 过滤 |
| 09 | `EventDemo` | 事件系统概念性演示 |
| 10 | `CommandBufferDemo` | 延迟执行结构变更 |
| 11 | `MultithreadingDemo` | JobScheduler + ParallelQuery |
| 12 | `BatchDemo` | 批量创建、批量添加组件 |
| 13 | `PerformanceDemo` | 单个vs批量创建性能对比 |
| 14 | `SystemDemo` | 手动实现系统模式 |
| 15 | `SourceGeneratorDemo` | 模拟源生成器生成代码 |
| 16 | `LowLevelDemo` | ArrayPool与非托管集合概念 |
| 17 | `RelationshipDemo` | 手动实现父子关系 |
| 18 | `PersistenceDemo` | JSON序列化World状态 |
| 19 | `EventBusDemo` | C#事件模拟事件总线 |
| 20 | `UnityIntegrationDemo` | MonoBehaviour与Arch桥接 |
| 21 | `BestPracticeDemo` | 最佳实践vs反例对比 |
| 22 | `DebugDemo` | World状态转储与调试 |

## 学习建议

1. **先读教程**：从 `Tutorial/00-目录与导读.md` 开始
2. **按章节运行**：用快捷键切换章节，观察运行结果
3. **修改代码**：在每个 ChapterXX 文件中尝试修改参数
4. **断点调试**：在 Demo 的 OnEnter/OnUpdate 中打断点

## 常见问题

### Q: 运行时报错 "TypeLoadException: CommunityToolkit.HighPerformance"
A: 通过 NuGetForUnity 安装 `CommunityToolkit.HighPerformance`。

### Q: 运行时报错 "Could not load type Schedulers.JobScheduler"
A: 安装 `Schedulers` NuGet包。

### Q: 章节切换无响应
A: 确保点击了 Game 视图使其获得焦点，再按快捷键。

### Q: HUD 文字太小看不清
A: 修改 `DebugHUD.cs` 中的 `RichTextStyle()` 字体大小，或在 Game 视图左上角调整 Scale 滑块。

### Q: 想启用 PURE_ECS 模式
A: `Edit > Project Settings > Player > Other Settings > Scripting Define Symbols` 添加 `PURE_ECS`。
但注意 Chapter05（EntityDemo）会因 WorldId 字段不存在而无法编译，需自行适配。

## 与源码的关系

本项目的演示代码全部基于 `d:\Unity\Arch\Arch` 与 `d:\Unity\Arch\Arch.Extended` 真实源码。
教程文档中的所有源码引用均使用 `[文件名](file:///绝对路径#L行号)` 格式，可在 IDE 中点击直达。

如需深入学习 Arch 内部实现，请直接阅读源码并配合教程第二、三部分章节。
