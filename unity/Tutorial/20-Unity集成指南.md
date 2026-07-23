# 第20章 Unity集成指南

## 20.1 概述

前面十九章我们都在学习 Arch 的核心 API 与扩展库，本章把它们**真正搬进 Unity**。Unity 是 Arch 最常见的宿主引擎之一——它本身不是 ECS（Unity DOTS 是另一套体系），而 Arch 通过 .NET Standard 2.1 兼容的 DLL 即可在 Unity 中使用。

本章覆盖：

1. Unity 中安装 Arch 的三种方式回顾
2. 推荐的项目结构
3. MonoBehaviour 与 Arch 的桥接
4. 生命周期管理（Awake / Update / OnApplicationQuit）
5. 使用 Arch.Unity 扩展库
6. 在 Editor 中调试 Arch — EntityDebugView
7. 与 GameObject 的转换策略 — Hybrid ECS 模式
8. 性能建议
9. 配套示例 `Assets/Scripts/Chapter20/UnityIntegrationDemo.cs`

> 💡 本章假设你已经完成 [第01章 安装与环境搭建](01-安装与环境搭建.md) 的所有步骤。

## 20.2 安装方式回顾

Arch 在 Unity 中的安装有三种方式，各有取舍：

| 方式 | 难度 | 升级 | 调试 | 适用场景 |
|------|------|------|------|----------|
| NuGetForUnity | ⭐ | 容易 | 仅元数据 | 生产项目 |
| DLL 拷贝 | ⭐⭐ | 手动 | 仅元数据 | 不想装 NuGet 插件 |
| 源码引入 | ⭐⭐⭐ | git pull | 可断点 | 学习/二次开发 |

📖 详细步骤见 [01-安装与环境搭建.md](01-安装与环境搭建.md) 的 1.3 节，本教程配套的 `d:\Unity\Arch\unity` 项目采用**源码引入**方式，便于源码学习。

### 20.2.1 常见踩坑

⚠️ 以下三个问题在 Unity 中最常见：

1. **API 兼容级别**：必须在 `Player Settings → Other → Api Compatibility Level` 设置为 `.NET Standard 2.1` 或 `.NET 4.x`，否则编译报 `unsafe` 错误
2. **依赖 DLL 缺失**：Arch 依赖 `CommunityToolkit.HighPerformance`、`Collections.Pooled`、`ZeroAllocJobScheduler`，少装一个都会运行崩溃
3. **源生成器 DLL 未设为 Roslyn analyzer**：使用 `Arch.System.SourceGenerator` 时，必须在 Inspector 勾选 `Roslyn Analyzer`

## 20.3 推荐的项目结构

在 Unity 中组织 Arch 项目，建议如下目录划分：

```
Assets/
├── Plugins/
│   └── Arch/                     # Arch 运行时 DLL 或源码
├── Scripts/
│   ├── Components/               # 所有组件定义（struct）
│   │   ├── Movement.cs
│   │   ├── Combat.cs
│   │   └── ...
│   ├── Systems/                  # 所有系统（BaseSystem 派生）
│   │   ├── MovementSystem.cs
│   │   ├── CombatSystem.cs
│   │   └── ...
│   ├── GameMain/                 # 游戏入口与生命周期
│   │   ├── GameBootstrap.cs      # MonoBehaviour 入口
│   │   └── WorldManager.cs
│   ├── ChapterXX/               # 本教程配套示例
│   └── Common/                   # 工具类与扩展
└── Scenes/
    └── Main.unity
```

💡 **核心原则**：`Components` 只放纯数据 struct，`Systems` 只放逻辑，`GameMain` 负责 World 生命周期。这能让团队协作时职责清晰。

### 20.3.1 命名约定

```csharp
// Components/Movement.cs
namespace MyGame.Components
{
    public struct Position { public float X, Y, Z; }
    public struct Velocity { public float X, Y, Z; }
}

// Systems/MovementSystem.cs
namespace MyGame.Systems
{
    public partial class MovementSystem : BaseSystem<World, float>
    {
        public MovementSystem(World world) : base(world) { }

        [Query]
        public void Move(ref Position pos, ref Velocity vel)
        {
            pos.X += vel.X;
            pos.Y += vel.Y;
            pos.Z += vel.Z;
        }
    }
}
```

## 20.4 MonoBehaviour 与 Arch 的桥接

Unity 的脚本系统基于 MonoBehaviour，而 Arch 的 World 是纯 C# 对象。**最简单的桥接方式**是在 MonoBehaviour 中持有 World 引用：

```csharp
using Arch.Core;
using UnityEngine;

public class GameBootstrap : MonoBehaviour
{
    private World _world;

    private void Awake()
    {
        _world = World.Create();
    }

    private void Update()
    {
        // 每帧驱动所有系统
        var dt = Time.deltaTime;
        _world.Query(in _moveQuery, (ref Position p, ref Velocity v) =>
        {
            p.X += v.X * dt;
            p.Y += v.Y * dt;
        });
    }

    private void OnApplicationQuit()
    {
        _world?.Dispose();
    }

    private static readonly QueryDescription _moveQuery =
        new QueryDescription().WithAll<Position, Velocity>();
}
```

🔥 关键点：

1. **World 是 C# 对象**：不继承 `UnityEngine.Object`，不需要 `ScriptableObject`
2. **QueryDescription 缓存为 static readonly**：避免每帧分配
3. **Update 中使用 `Time.deltaTime`**：Unity 提供的帧时间直接传给系统

### 20.4.1 多 World 场景

如果你需要把不同场景的实体隔离（如 UI 实体与战斗实体），可以创建多个 World：

```csharp
public class WorldManager : MonoBehaviour
{
    public World GameWorld { get; private set; }
    public World UiWorld { get; private set; }

    private void Awake()
    {
        GameWorld = World.Create();
        UiWorld = World.Create();
    }

    private void Update()
    {
        GameWorld.Tick(Time.deltaTime);
        UiWorld.Tick(Time.deltaTime);
    }

    private void OnDestroy()
    {
        GameWorld?.Dispose();
        UiWorld?.Dispose();
    }
}

// 扩展方法：统一调用一组系统
public static class WorldExtensions
{
    public static void Tick(this World world, float dt)
    {
        // 这里调用注册到 world 的系统
        // 详见第14章 Arch.System
    }
}
```

📖 World 的静态注册表见 [World.cs#L75](file:///d:/Unity/Arch/Arch/src/Arch/Core/World.cs#L75)：`public static World[] Worlds`。每个 World 都有唯一 ID，可通过 `World.Worlds[id]` 访问。

## 20.5 生命周期管理

Unity MonoBehaviour 的生命周期与 World 的对应关系如下：

| MonoBehaviour | Arch 对应操作 | 说明 |
|---------------|--------------|------|
| `Awake` | `World.Create()` | 创建 World，初始化组件 |
| `Start` | 创建初始实体 | 在所有 Awake 后执行 |
| `Update` | `world.Query(...)` | 每帧驱动系统 |
| `FixedUpdate` | 物理相关查询 | 固定步长 |
| `OnApplicationQuit` | `world.Dispose()` | 释放 World |
| `OnDestroy` | 二次 Dispose 检查 | 兜底 |

### 20.5.1 Awake 中 World.Create

[World.cs#L119](file:///d:/Unity/Arch/Arch/src/Arch/Core/World.cs#L119) 定义了 `World.Create`：

```csharp
public static World Create(
    int chunkSizeInBytes = 16_384,
    int minimumAmountOfEntitiesPerChunk = 100,
    int archetypeCapacity = 2,
    int entityCapacity = 64)
```

🔥 4 个参数都很重要，根据项目规模调整：

- `chunkSizeInBytes`：Chunk 字节数，默认 16KB
- `minimumAmountOfEntitiesPerChunk`：每 Chunk 最少实体数，默认 100
- `archetypeCapacity`：初始 Archetype 容量
- `entityCapacity`：初始实体容量

```csharp
private void Awake()
{
    // 大型项目：预分配更大容量避免运行时扩容
    _world = World.Create(
        chunkSizeInBytes: 16_384,
        minimumAmountOfEntitiesPerChunk: 128,
        archetypeCapacity: 32,
        entityCapacity: 4096);
}
```

### 20.5.2 Update 中 world.Query

Unity 的 `Update` 每帧调用一次，是驱动所有系统的入口：

```csharp
private void Update()
{
    var dt = Time.deltaTime;

    // 推荐使用 Arch.System 的 Group 统一调度
    _systems.Update(dt);
}
```

⚠️ **不要在 Update 中创建 World**！详见第21章。World 创建开销很大（涉及多个数组分配），应只在 Awake 中调用一次。

### 20.5.3 OnApplicationQuit 中 world.Dispose

World 实现了 `IDisposable`，[World.cs#L539](file:///d:/Unity/Arch/Arch/src/Arch/Core/World.cs#L539)：

```csharp
[StructuralChange]
public void Dispose()
{
    Dispose(true);
    GC.SuppressFinalize(this);
}
```

⚠️ 必须在退出时 Dispose，否则：

1. 静态 `World.Worlds` 数组中残留引用，下次启动可能冲突
2. Entity 的非托管资源（Chunk 数组）不会及时回收
3. Editor 模式下场景重载可能引发内存泄漏

```csharp
private void OnApplicationQuit()
{
    _world?.Dispose();
    _world = null;
}

private void OnDestroy()
{
    // 兜底：编辑器下停止 Play 也会触发 OnDestroy
    _world?.Dispose();
}
```

💡 **Editor 模式特殊处理**：Unity Editor 中每次进入 Play Mode 都会触发 Awake，退出时触发 OnApplicationQuit。如果两次 Dispose 同一个 World 会怎样？查看 [World.cs#L551](file:///d:/Unity/Arch/Arch/src/Arch/Core/World.cs#L551) 的 `_isDisposed` 守卫——重复 Dispose 会被安全忽略。

## 20.6 使用 Arch.Unity 扩展库

除了手动桥接，社区维护了一个 [Arch.Unity](https://github.com/AnnulusGames/Arch.Unity) 扩展库，提供：

1. **`WorldManager` MonoBehaviour**：自动管理 World 生命周期
2. **Entity Authoring 组件**：在 Inspector 中可视化添加组件
3. **GameObject ↔ Entity 转换器**：一键互转
4. **Editor 窗口**：可视化查看 World 状态

### 20.6.1 安装

通过 Unity Package Manager 安装：

```
https://github.com/AnnulusGames/Arch.Unity.git
```

或修改 `Packages/manifest.json`：

```json
{
  "dependencies": {
    "com.annulusgames.arch.unity": "https://github.com/AnnulusGames/Arch.Unity.git"
  }
}
```

### 20.6.2 使用 WorldManager

```csharp
using AnnulusGames.Arch.Unity;

public class GameBootstrap : MonoBehaviour
{
    private void Awake()
    {
        // 创建并注册 World
        WorldManager.CreateWorld("GameWorld");
    }

    private void Update()
    {
        var world = WorldManager.GetWorld("GameWorld");
        var dt = Time.deltaTime;
        world.Query(in _moveQuery, (ref Position p, ref Velocity v) =>
        {
            p.X += v.X * dt;
        });
    }
}
```

📖 Arch.Unity 会自动在 `OnApplicationQuit` 中 Dispose 所有 World，省去手动管理。

### 20.6.3 Entity Authoring

可以在 GameObject 上挂一个 `EntityAuthoring` 组件，在 Inspector 中可视化添加 Arch 组件：

```csharp
[RequireComponent(typeof(EntityAuthoring))]
public class PlayerAuthoring : MonoBehaviour
{
    public float speed = 5f;
    public int maxHealth = 100;

    private void Awake()
    {
        var authoring = GetComponent<EntityAuthoring>();
        authoring.AddData(new Position { X = transform.position.x, Y = transform.position.y });
        authoring.AddData(new Velocity { X = 0, Y = 0 });
        authoring.AddData(new Health { Value = maxHealth });
    }
}
```

## 20.7 在 Editor 中调试 Arch — EntityDebugView

Arch 内置了一个调试视图 [EntityDebugView.cs](file:///d:/Unity/Arch/Arch/src/Arch/Core/Utils/EntityDebugView.cs)，让 IDE 在调试时可以展开 Entity 查看其全部信息。

查看源码：

```csharp
internal sealed class EntityDebugView
{
    public EntityDebugView(Entity entity)
    {
        _entity = entity;
        Components = IsAlive ? entity.GetAllComponents() : null;
    }

    public int Id { get => _entity.Id; }
    public bool IsAlive { get => _entity.IsAlive(); }
    public int Version { get => IsAlive ? _entity.Version : -1; }
    public object?[]? Components { get; }
    public World? World { get => IsAlive ? World.Worlds[_entity.WorldId] : null; }
    public Archetype? Archetype { get => IsAlive ? World.Worlds[_entity.WorldId].GetArchetype(_entity) : null; }
    public Chunk Chunk { get => IsAlive ? World.Worlds[_entity.WorldId].GetChunk(_entity) : default; }
}
```

🔥 这个类是 `internal` 且用 `DebuggerTypeProxy` 特性绑定到 `Entity` 上——**在 VS / Rider 中将鼠标悬停在 Entity 变量上**，即可展开查看：

- `Id`、`Version`：实体身份
- `IsAlive`：是否存活
- `Components`：所有组件值（装箱显示）
- `World`：所属 World
- `Archetype`：所在 Archetype
- `Chunk`：所在 Chunk

⚠️ **注意**：`EntityDebugView` 在 `PURE_ECS` 模式下不可用（被 `#if !PURE_ECS` 包裹，见 [EntityDebugView.cs#L1](file:///d:/Unity/Arch/Arch/src/Arch/Core/Utils/EntityDebugView.cs#L1)）。

### 20.7.1 在 Unity Editor 中查看 Entity

如果你在 Inspector 中想显示一个 Entity 的内容，可以编写一个 Editor 脚本：

```csharp
#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;

[CustomEditor(typeof(EntityDebugProxy))]
public class EntityDebugEditor : Editor
{
    public override void OnInspectorGUI()
    {
        var proxy = (EntityDebugProxy)target;
        if (!proxy.Entity.IsAlive())
        {
            EditorGUILayout.LabelField("Entity", "(not alive)");
            return;
        }

        EditorGUILayout.LabelField("Id", proxy.Entity.Id.ToString());
        EditorGUILayout.LabelField("Version", proxy.Entity.Version.ToString());
        EditorGUILayout.Space();

        var components = proxy.Entity.GetAllComponents();
        foreach (var comp in components)
        {
            if (comp == null) continue;
            EditorGUILayout.LabelField(comp.GetType().Name, comp.ToString());
        }
    }
}
#endif
```

## 20.8 与 GameObject 的转换策略 — Hybrid ECS 模式

Unity 是 GameObject-Component 模型，无法完全避免。常见的混合策略：

### 20.8.1 策略一：GameObject 作为 View，Arch 作为 Model

将数据与逻辑放在 Arch，GameObject 只负责渲染：

```csharp
public class ViewBinder : MonoBehaviour
{
    public Entity target;

    private void LateUpdate()
    {
        if (!target.IsAlive()) return;
        ref var pos = ref target.Get<Position>();
        transform.position = new Vector3(pos.X, pos.Y, pos.Z);
    }
}
```

💡 这是推荐模式——**Arch 是真理之源**，GameObject 只是显示层。

### 20.8.2 策略二：批量创建 GameObject

当需要把大量 Arch 实体同步到 GameObject 时（如 UI 列表），使用批量创建：

```csharp
// 一次创建，避免每帧 new GameObject
public void SyncEntitiesToGameObjects(World world)
{
    var query = new QueryDescription().WithAll<Position, RenderTag>();
    var count = world.CountEntities(in query);
    var entities = new Entity[count];
    world.GetEntities(in query, entities);

    for (int i = 0; i < entities.Length; i++)
    {
        var go = Instantiate(_prefab);
        go.GetComponent<ViewBinder>().target = entities[i];
    }
}
```

### 20.8.3 策略三：GameObject → Entity 一次性转换

启动时把场景中已有的 GameObject 转换为 Arch 实体：

```csharp
public void ConvertSceneToEntities(World world)
{
    var gameObjects = FindObjectsByTag<MonoBehaviour>("Player");
    foreach (var go in gameObjects)
    {
        var entity = world.Create(
            new Position { X = go.transform.position.x, Y = go.transform.position.y },
            new Velocity { X = 0, Y = 0 }
        );
        go.GetComponent<ViewBinder>().target = entity;
    }
}
```

⚠️ **不要每帧同步**：GameObject ↔ Entity 转换会引发 GC，应只在场景加载/卸载时执行。

## 20.9 性能建议

### 20.9.1 避免每帧创建 World

World 创建涉及多个大数组分配，必须只调用一次：

```csharp
// ❌ 错误：每帧创建
private void Update()
{
    using var world = World.Create();
    world.Create(new Position(), new Velocity());
}

// ✅ 正确：Awake 创建，OnDestroy 释放
private World _world;
private void Awake() => _world = World.Create();
private void OnDestroy() => _world?.Dispose();
```

### 20.9.2 缓存 QueryDescription

`QueryDescription` 内部会计算组件签名，缓存为 `static readonly`：

```csharp
private static readonly QueryDescription _moveQuery =
    new QueryDescription().WithAll<Position, Velocity>();

private void Update()
{
    _world.Query(in _moveQuery, (ref Position p, ref Velocity v) =>
    {
        // ...
    });
}
```

📖 World 内部还有 `QueryCache` 字典缓存 Query，见 [World.cs#L203](file:///d:/Unity/Arch/Arch/src/Arch/Core/World.cs#L203)。

### 20.9.3 使用 InlineQuery 避免委托分配

```csharp
// ❌ 委托分配
_world.Query(in query, (ref Position p) => { p.X++; });

// ✅ IForEach 结构体内联
public struct MoveJob : IForEach
{
    public float Dt;
    public void Update(Entity entity) { /* ... */ }
}

private MoveJob _job;
private void Update()
{
    _job.Dt = Time.deltaTime;
    _world.InlineQuery<MoveJob>(in _query);
}
```

### 20.9.4 合理配置 Chunk 大小

Unity 中通常场景实体数量可控，可参考下表：

| 场景 | 实体数 | 推荐 chunkSizeInBytes |
|------|--------|---------------------|
| UI 系统 | <100 | 4_096 |
| 战斗角色 | <1000 | 16_384（默认） |
| 弹幕/粒子 | >10000 | 32_768 |

### 20.9.5 关闭 Editor 时的 InEditor 检查

Editor 模式下，Unity 的 `Time.deltaTime` 在未聚焦窗口时可能为 0，需要保护：

```csharp
private void Update()
{
    if (Time.deltaTime == 0f) return;  // 编辑器暂停
    _systems.Update(Time.deltaTime);
}
```

## 20.10 配套示例

本章的配套 Unity 示例代码位于 `Assets/Scripts/Chapter20/UnityIntegrationDemo.cs`，其中包含：

- 一个完整的 `GameBootstrap` MonoBehaviour
- 一个使用 `BaseSystem` 的 `MovementSystem`
- 一个 `ViewBinder` 用于实体→GameObject 同步
- 一个 Editor 脚本用于可视化调试

运行后效果：

1. 场景中创建 1000 个 GameObject，每个对应一个 Arch 实体
2. 实体按 Velocity 移动，每帧同步到 GameObject 位置
3. Inspector 中可实时查看每个实体的组件

```csharp
// Assets/Scripts/Chapter20/UnityIntegrationDemo.cs 摘录
public class GameBootstrap : MonoBehaviour
{
    public GameObject prefab;
    public int spawnCount = 100;
    private World _world;
    private Group<float> _systems;

    private static readonly QueryDescription _query =
        new QueryDescription().WithAll<Position, Velocity>();

    private void Awake()
    {
        _world = World.Create();
        _systems = new Group<float>("Game",
            new MovementSystem(_world));

        for (int i = 0; i < spawnCount; i++)
        {
            var go = Instantiate(prefab);
            var entity = _world.Create(
                new Position { X = go.transform.position.x, Y = go.transform.position.y },
                new Velocity { X = Random.Range(-1f, 1f), Y = Random.Range(-1f, 1f) });
            go.GetComponent<ViewBinder>().target = entity;
        }
    }

    private void Update() => _systems?.Update(Time.deltaTime);

    private void OnApplicationQuit() => _world?.Dispose();
}
```

## 本章小结

| 要点 | 说明 |
|------|------|
| 安装方式 | NuGetForUnity / DLL / 源码三种，按需选择 |
| 项目结构 | Components / Systems / GameMain 三层分离 |
| 桥接方式 | MonoBehaviour 持有 World 引用即可 |
| 生命周期 | Awake 创建 → Update 查询 → OnApplicationQuit 释放 |
| Arch.Unity | 提供 WorldManager / EntityAuthoring / Editor 工具 |
| EntityDebugView | IDE 中悬停即可查看 Entity 内部状态 |
| Hybrid ECS | Arch 作为 Model，GameObject 作为 View |
| 性能要点 | 缓存 QueryDescription、InlineQuery、合理 Chunk 大小 |

至此，你已经掌握了在 Unity 中**实战使用 Arch** 的全部要点。下一章我们将深入讨论**最佳实践与陷阱**，帮助你写出更稳健的代码。
