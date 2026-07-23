# 第14章 Arch.System 系统框架

## 14.1 为什么需要系统？

在前面的章节中，我们学习了如何使用 `World`、`Entity` 和 `Component` 来组织数据。但是 ECS 中的 "S"（System，系统）才是真正承载游戏逻辑的地方。一个真实的游戏会包含大量并行的逻辑：

- 角色移动系统
- 碰撞检测系统
- 渲染系统
- AI 决策系统
- 输入处理系统
- UI 更新系统

如果把这些逻辑全部塞进一个 `Update` 方法里，代码很快就会变得难以维护。**Arch.System** 模块就是为此而生——它提供了一组抽象，让你把每一类逻辑封装成独立的"系统"，由统一的调度器按顺序执行。

📖 Arch.System 源码位于 [Systems.cs](file:///d:/Unity/Arch/Arch.Extended/Arch.System/Systems.cs)，整个模块只有不到 400 行代码，简洁而强大。

💡 Arch.System 是可选模块。如果你只想用最原始的 `World.Query`，完全可以不用它。但项目一旦复杂，引入系统框架会让代码组织清晰得多。

## 14.2 ISystem 接口

最基础的抽象是 `ISystem<T>` 接口，定义见 [Systems.cs#L20](file:///d:/Unity/Arch/Arch.Extended/Arch.System/Systems.cs#L20)：

```csharp
public interface ISystem<T> : IDisposable
{
    void Initialize();
    void BeforeUpdate(in T t);
    void Update(in T t);
    void AfterUpdate(in T t);
}
```

泛型参数 `T` 表示每一帧要传给系统的数据，最常见的用法是传入 `GameTime` 或 `float deltaTime`。在 Unity 中通常使用 `BaseSystem<World, float>`，把 deltaTime 作为帧数据传入。

接口里有四个生命周期方法，作用如下：

| 方法 | 调用时机 | 典型用途 |
|------|----------|----------|
| `Initialize` | 系统首次运行前 | 缓存查询、订阅事件、加载资源 |
| `BeforeUpdate` | `Update` 之前 | 开启 `SpriteBatch`、清空临时缓冲 |
| `Update` | 每帧主循环 | 调用查询、修改组件 |
| `AfterUpdate` | `Update` 之后 | 提交绘制、刷新统计 |

⚠️ 注意 `ISystem<T>` 继承了 `IDisposable`，意味着每个系统都拥有 `Dispose` 方法用于清理资源。

## 14.3 BaseSystem 基类

直接实现接口需要写一堆空方法，因此官方提供了 `BaseSystem<W, T>` 抽象类（[Systems.cs#L51](file:///d:/Unity/Arch/Arch.Extended/Arch.System/Systems.cs#L51)）：

```csharp
public abstract class BaseSystem<W, T> : ISystem<T>
{
    public W World { get; private set; }

    protected BaseSystem(W world)
    {
        World = world;
    }

    public virtual void Initialize(){}
    public virtual void BeforeUpdate(in T t) { }
    public virtual void Update(in T t){}
    public virtual void AfterUpdate(in T t){}
    public virtual void Dispose(){}
}
```

两个泛型参数：

- `W`：World 类型（绝大多数情况就是 `Arch.Core.World`）
- `T`：每帧传入的数据类型（如 `float`、`GameTime`）

基类已经把所有方法标记为 `virtual`，你只需 `override` 关心的方法即可。`World` 属性在构造时被赋值，系统内部随时可通过 `this.World` 访问。

🔥 推荐做法：每个系统只重写一个 `Update` 方法。`BeforeUpdate`/`AfterUpdate` 通常只在需要包围外部 API（如 `Graphics.RenderBegin`/`RenderEnd`）时才使用。

## 14.4 系统的生命周期

下面是一个完整的系统生命周期示例，覆盖 `Initialize` → `Update` → `Dispose`：

```csharp
using Arch.Core;
using Arch.System;

public partial class MovementSystem : BaseSystem<World, float>
{
    private QueryDescription _moveQuery;

    public MovementSystem(World world) : base(world) { }

    public override void Initialize()
    {
        // 在系统首次运行前缓存查询描述
        _moveQuery = new QueryDescription().WithAll<Position, Velocity>();
    }

    public override void Update(in float dt)
    {
        // 每帧执行查询并修改组件
        World.Query(in _moveQuery, (ref Position pos, ref Velocity vel) =>
        {
            pos.X += vel.X * dt;
            pos.Y += vel.Y * dt;
        });
    }

    public override void Dispose()
    {
        // 释放系统持有的非托管资源（若有）
    }
}
```

💡 你也可以让系统 `partial` 并配合源生成器自动生成 `XxxQuery` 方法，下一章会详细讲解。

## 14.5 Group：组合多个系统

单个系统作用有限，真实项目通常会有十几个甚至几十个系统。`Group<T>` 类（[Systems.cs#L89](file:///d:/Unity/Arch/Arch.Extended/Arch.System/Systems.cs#L89)）就是用来组合它们的容器。

`Group<T>` 自己也实现了 `ISystem<T>`，所以**Group 可以嵌套 Group**——这是一个非常强大的特性。它的 `Update` 会按添加顺序依次调用每个子系统的对应方法：

```csharp
public class Group<T> : ISystem<T>, IEnumerable<ISystem<T>>
{
    private readonly List<SystemEntry> _systems = new();

    public Group(string name, params ISystem<T>[] systems)
    {
        Name = name;
        foreach (var system in systems)
            Add(system);
    }

    public void Update(in T t)
    {
        for (var index = 0; index < _systems.Count; index++)
        {
            var entry = _systems[index];
            entry.System.Update(in t);
        }
    }
}
```

📖 注意 `Group<T>` 的构造函数第一个参数是 `name`——这个名字不只是标识，还用于性能监控（在 .NET 6+ 上会创建 `Meter` 来统计每个子系统的耗时）。

### 14.5.1 添加系统

`Group` 提供了多种 `Add` 重载：

```csharp
var root = new Group<float>("Root");

// 1. 直接添加实例
root.Add(new MovementSystem(world));
root.Add(new RenderSystem(world));

// 2. 添加多个实例
root.Add(sys1, sys2, sys3);

// 3. 通过泛型添加（要求无参构造）
root.Add<MovementSystem>();

// 4. 嵌套 Group
var physics = new Group<float>("Physics",
    new MovementSystem(world),
    new CollisionSystem(world)
);
root.Add(physics);
```

### 14.5.2 查找系统

`Group` 提供了两个查找方法（[Systems.cs#L186](file:///d:/Unity/Arch/Arch.Extended/Arch.System/Systems.cs#L186)）：

- `Get<G>()`：返回层级中第一个匹配类型的系统
- `Find<G>()`：返回所有匹配类型的系统（支持跨嵌套层级）

```csharp
var movement = root.Get<MovementSystem>();
foreach (var sys in root.Find<IGameSystem>())
{
    Console.WriteLine(sys.GetType().Name);
}
```

⚠️ `Get<G>()` 找不到时返回 `default`（即 `null`），使用前要判空。

## 14.6 完整示例：游戏根系统

下面是一个典型的"游戏根系统"组织方式：

```csharp
public class GameRoot
{
    private Group<float> _root;
    private World _world;

    public void Start()
    {
        _world = World.Create();

        _root = new Group<float>("GameRoot",
            new Group<float>("Simulation",
                new MovementSystem(_world),
                new CollisionSystem(_world),
                new AISystem(_world)
            ),
            new Group<float>("Presentation",
                new RenderSystem(_world),
                new AudioSystem(_world)
            )
        );

        _root.Initialize();
    }

    public void Tick(float dt)
    {
        _root.BeforeUpdate(in dt);
        _root.Update(in dt);
        _root.AfterUpdate(in dt);
    }

    public void Shutdown()
    {
        _root.Dispose();
        _world.Dispose();
    }
}
```

🔥 这种"Group of Groups"的结构，让你可以方便地控制子系统的执行顺序，例如：

- 暂停模拟但继续渲染：跳过 Simulation 组的 `Update`
- 只更新音频：只调用 AudioSystem 的 `Update`

## 14.7 自定义系统示例

下面演示一个稍微完整的自定义系统——`SpawnSystem`，它每秒生成一个新实体：

```csharp
using Arch.Core;
using Arch.System;

public partial class SpawnSystem : BaseSystem<World, float>
{
    private float _timer;
    private readonly float _interval;

    public SpawnSystem(World world, float interval = 1f) : base(world)
    {
        _interval = interval;
    }

    public override void Update(in float dt)
    {
        _timer += dt;
        if (_timer < _interval) return;

        _timer = 0;
        World.Create(new Position { X = 0, Y = 0 }, new Velocity { X = 1, Y = 0 });
    }
}
```

可以看到，系统的 `Update` 内部完全不用关心"被谁调用"——它只关注自己的逻辑。这种解耦是 Arch.System 的核心价值。

📖 官方示例代码可参考 [Arch.Extended.Sample/Systems.cs](file:///d:/Unity/Arch/Arch.Extended/Arch.Extended.Sample/Systems.cs)，里面实现了完整的 `MovementSystem`、`ColorSystem`、`DrawSystem`、`DebugSystem` 四个系统。

## 14.8 配套示例

本章的配套 Unity 示例代码位于 `Assets/Scripts/Chapter14/SystemDemo.cs`，其中包含：

- 一个 `MovementSystem`：演示基础系统结构
- 一个 `SpawnSystem`：演示状态保持的系统
- 一个 `GameRoot`：演示如何用 `Group` 组合多个系统
- 一个 `MonoBehaviour` 入口：把 Arch.System 接入 Unity 主循环

运行该示例后，你会在 Game 视图中看到实体在屏幕上移动并定时生成新实体。

## 本章小结

| 概念 | 关键 API | 说明 |
|------|----------|------|
| 系统接口 | `ISystem<T>` | 提供 `Initialize`/`BeforeUpdate`/`Update`/`AfterUpdate` 四个生命周期方法 |
| 系统基类 | `BaseSystem<W, T>` | 抽象类，封装 World 引用，方法默认空实现 |
| 系统组合 | `Group<T>` | 容器型系统，按顺序执行子系统，支持嵌套 |
| 系统查找 | `Get<G>()` / `Find<G>()` | 在层级中按类型查找系统 |
| 生命周期 | `Initialize` → `Update` → `Dispose` | 与 Unity 的 `Awake` → `Update` → `OnDestroy` 类似 |
| 性能监控 | `Meter` + `Stopwatch`（.NET 6+） | 自动统计每个子系统耗时，导出为 OpenTelemetry 指标 |

下一章我们将学习 **Arch.System.SourceGenerator**——通过编译时源生成器，自动生成系统中的查询代码，彻底消除手写 `World.Query` 的样板。
