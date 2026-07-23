# 第09章 事件系统 Events

## 9.1 概述

Arch 的事件系统是一个**默认关闭**的可选模块。它允许用户在组件被添加、设置、移除或实体被创建/销毁时收到回调，可用于：

- 调试与日志记录
- 同步外部资源（如渲染网格、音频源）与 ECS 数据
- 实现观察者模式的副作用逻辑

> ⚠️ 与 Unity 的 `MonoBehaviour` 生命周期不同，Arch 事件**不会自动启用**。必须在编译时定义 `EVENTS` 符号才能生效，否则所有事件调用都会被 `#if EVENTS` 预处理指令直接裁掉，连方法体都不存在。

## 9.2 事件类型一览

参考 [Events.cs](file:///d:/Unity/Arch/Arch/src/Arch/Core/Events/Events.cs) 与 [EventHandlers.cs](file:///d:/Unity/Arch/Arch/src/Arch/Core/Events/EventHandlers.cs)，Arch 提供以下事件：

| 事件 | 触发时机 | 委托类型 |
|------|----------|----------|
| `OnEntityCreated` | 实体创建后 | `EntityCreatedHandler(in Entity)` |
| `OnEntityDestroyed` | 实体销毁前 | `EntityDestroyedHandler(in Entity)` |
| `OnComponentAdded<T>` | 给实体添加组件 T 后 | `ComponentAddedHandler<T>(in Entity, ref T)` |
| `OnComponentSet<T>` | 实体已有 T，再次 `Set<T>` 时 | `ComponentSetHandler<T>(in Entity, ref T)` |
| `OnComponentRemoved<T>` | 实体移除组件 T 前 | `ComponentRemovedHandler<T>(in Entity, ref T)` |

每个泛型事件同时提供一个**非泛型版本**（不带 `<T>`，只传 `in Entity`），方便在不关心组件值时订阅。详见 [EventHandlers.cs](file:///d:/Unity/Arch/Arch/src/Arch/Core/Events/EventHandlers.cs)。

## 9.3 EventHandler 设计

[EventHandlers.cs](file:///d:/Unity/Arch/Arch/src/Arch/Core/Events/EventHandlers.cs) 定义了 8 个委托类型：

```csharp
public delegate void EntityCreatedHandler(in Entity entity);
public delegate void EntityDestroyedHandler(in Entity entity);

// 泛型版本：可拿到组件引用
public delegate void ComponentAddedHandler<T>(in Entity entity, ref T comp);
public delegate void ComponentSetHandler<T>(in Entity entity, ref T comp);
public delegate void ComponentRemovedHandler<T>(in Entity entity, ref T comp);

// 非泛型版本：只传实体
public delegate void ComponentAddedHandler(in Entity entity);
public delegate void ComponentSetHandler(in Entity entity);
public delegate void ComponentRemovedHandler(in Entity entity);
```

> 💡 注意 `ref T comp` 是 `ref` 而非 `in`，意味着回调可以**修改**即将存入的组件值。例如在 `OnComponentAdded` 中把 `Position` 初始化为某个默认值，可以省去显式 `Set` 调用。

[Events.cs](file:///d:/Unity/Arch/Arch/src/Arch/Core/Events/Events.cs) 中存储这些委托的容器是一个分层的"基类 + 泛型子类"结构：

```csharp
internal class Events
{
    internal readonly List<ComponentAddedHandler> ComponentAddedHandlers = new();
    internal readonly List<ComponentSetHandler> ComponentSetHandlers = new();
    internal readonly List<ComponentRemovedHandler> ComponentRemovedHandlers = new();
}

internal class Events<T> : Events
{
    internal readonly List<ComponentAddedHandler<T>> ComponentAddedGenericHandlers = new();
    internal readonly List<ComponentSetHandler<T>> ComponentSetGenericHandlers = new();
    internal readonly List<ComponentRemovedHandler<T>> ComponentRemovedGenericHandlers = new();
}
```

这样设计的好处是：非泛型事件派发时只遍历基类列表，泛型事件派发时遍历子类列表，避免类型检查开销。

## 9.4 EventTypeRegistry：类型到 ID 的映射

[EventTypeRegistry](file:///d:/Unity/Arch/Arch/src/Arch/Core/Events/EventTypeRegistry.cs#L11) 是一个静态类，为每个事件类型分配唯一 ID：

```csharp
internal static class EventTypeRegistry
{
    internal static int NextEventTypeId = -1;
    internal static readonly ConcurrentDictionary<ComponentType, int> EventIds = new();
}

internal static class EventType<T>
{
    internal static readonly int Id;

    static EventType()
    {
        Id = Interlocked.Increment(ref EventTypeRegistry.NextEventTypeId);
        EventTypeRegistry.EventIds.TryAdd(typeof(T), Id);
    }
}
```

### 9.4.1 工作原理

`EventType<T>` 是一个"每个 T 生成一份"的泛型静态类。CLR 在首次使用 `EventType<Position>` 时会触发其静态构造函数，原子地递增 `NextEventTypeId` 并注册到字典。

- `EventType<Position>.Id` → 0
- `EventType<Velocity>.Id` → 1
- ...

> 🔥 这种"泛型静态类自动注册"是零反射、零配置的类型 ID 方案。它和 `Component<T>` 的实现如出一辙，是 ECS 框架常用的模式。

### 9.4.2 为何需要 ID？

`World` 内部用一个 `Events[]` 数组按类型 ID 索引（见 [World.Events.cs L34](file:///d:/Unity/Arch/Arch/src/Arch/Core/Events/World.Events.cs#L34)）。订阅和派发时直接通过 `EventType<T>.Id` 取出对应的 `Events<T>` 实例，比 `Dictionary<Type, ...>` 更快且线程友好。

非泛型路径（如 `OnComponentAdded(Entity, ComponentType)`）则通过 `EventTypeRegistry.EventIds` 字典查 ID：

```csharp
private Events.Events? GetEvents(ComponentType compType)
{
    if (!EventTypeRegistry.EventIds.TryGetValue(compType, out var index))
    {
        return null;
    }
    // ... 从 _compEvents 取出
}
```

## 9.5 World.Events 分部类

[World.Events.cs](file:///d:/Unity/Arch/Arch/src/Arch/Core/Events/World.Events.cs) 给 `World` 添加了事件相关的字段和方法，全部包在 `#if EVENTS` 中。

### 9.5.1 字段

```csharp
public partial class World
{
    private const int InitialCapacity = 128;

    private readonly List<EntityCreatedHandler> _entityCreatedHandlers = new(InitialCapacity);
    private readonly List<EntityDestroyedHandler> _entityDestroyedHandlers = new(InitialCapacity);

    private Events.Events[] _compEvents = new Events.Events[InitialCapacity];
}
```

`_compEvents` 数组按 `EventType<T>.Id` 索引，初始容量 128，超出时自动 `Array.Resize`。

### 9.5.2 订阅 API

订阅方法示例（[World.Events.cs L70-L88](file:///d:/Unity/Arch/Arch/src/Arch/Core/Events/World.Events.cs#L70-L88)）：

```csharp
public void SubscribeComponentAdded<T>(ComponentAddedHandler<T> handler)
{
#if EVENTS
    ref readonly var events = ref GetEvents<T>();
    lock (events.ComponentAddedGenericHandlers)
    {
        events.ComponentAddedGenericHandlers.Add(handler);
    }

    // 同时往非泛型列表注册一个适配器，让非泛型派发也能触发泛型 handler
    lock (events.ComponentAddedHandlers)
    {
        events.ComponentAddedHandlers.Add((in Entity entity) =>
        {
            ref var compGeneric = ref Get<T>(entity);
            handler(entity, ref compGeneric);
        });
    }
#endif
}
```

> ⚠️ 注意那个匿名闭包：它捕获了 `handler`，会触发一次堆分配。这是订阅时的固定开销，**事件系统不适合每帧订阅/取消订阅**。建议在 World 创建时一次性订阅，运行期保持不变。

### 9.5.3 派发 API

派发方法以 `OnComponentAdded<T>` 为例（[World.Events.cs L203-L226](file:///d:/Unity/Arch/Arch/src/Arch/Core/Events/World.Events.cs#L203-L226)）：

```csharp
public void OnComponentAdded<T>(Entity entity)
{
#if EVENTS
    ref readonly var events = ref GetEvents<T>();
    ref var added = ref Get<T>(entity);

    int count;
    lock (events.ComponentAddedGenericHandlers)
    {
        count = events.ComponentAddedGenericHandlers.Count;
    }

    for (var i = 0; i < count; i++)
    {
        ComponentAddedHandler<T> handler;
        lock (events.ComponentAddedGenericHandlers)
        {
            handler = events.ComponentAddedGenericHandlers[i];
        }
        handler(in entity, ref added);
    }
#endif
}
```

派发循环采用"先锁取 count、再锁取 handler、最后在锁外 Invoke"的策略。源码注释明确说明：

> The thread-safety here relies on the fact that handlers can NEVER be unsubscribed.
> We still have to lock to access the handler, because what if someone is adding in the middle of our access?

也就是说，**事件系统不支持取消订阅**，列表只会增长，不会缩短或重排。这是用最小锁开销换来线程安全的设计权衡。

### 9.5.4 触发点：哪里会派发事件？

World 的核心 API 中插入了事件触发点：

| World API | 触发事件 | 位置 |
|-----------|---------|------|
| `Create(params ComponentType[])` | `OnEntityCreated` + 每个 type 的 `OnComponentAdded` | [World.cs L328-L335](file:///d:/Unity/Arch/Arch/src/Arch/Core/World.cs#L328-L335) |
| `Destroy(Entity)` | 每个 compType 的 `OnComponentRemoved` + `OnEntityDestroyed` | [World.cs L382-L393](file:///d:/Unity/Arch/Arch/src/Arch/Core/World.cs#L382-L393) |
| `Add<T>(Entity, T)` | `OnComponentAdded<T>` | （见 World.cs Add 方法） |
| `Set<T>(Entity, T)` | `OnComponentSet<T>` | （见 World.cs Set 方法） |
| `Remove<T>(Entity)` | `OnComponentRemoved<T>` | （见 World.cs Remove 方法） |
| `Destroy(in QueryDescription)` | 批量 `OnComponentRemoved` + `OnEntityDestroyed` | [World.cs L845-L854](file:///d:/Unity/Arch/Arch/src/Arch/Core/World.cs#L845-L854) |
| `Add<T>(in QueryDescription, T)` | 批量 `OnComponentAdded<T>` | [World.cs L941](file:///d:/Unity/Arch/Arch/src/Arch/Core/World.cs#L941) |

非泛型派发方法 `OnComponentAdded(Entity, ComponentType)` 用于在不知道类型参数时（如批量销毁）触发，它会查 `EventTypeRegistry.EventIds` 找到对应 handler 列表。

## 9.6 使用示例

### 9.6.1 基本订阅

```csharp
using Arch.Core;
using Arch.Core.Events;

var world = World.Create();

// 实体创建
world.SubscribeEntityCreated(in entity =>
{
    Console.WriteLine($"实体创建: {entity.Id}");
});

// 添加 Position 组件
world.SubscribeComponentAdded<Position>((in Entity entity, ref Position pos) =>
{
    Console.WriteLine($"实体 {entity.Id} 添加了 Position: ({pos.X}, {pos.Y})");
    pos.X = 0;  // 可修改即将存入的值
    pos.Y = 0;
});

// 移除组件
world.SubscribeComponentRemoved<Position>((in Entity entity, ref Position pos) =>
{
    Console.WriteLine($"实体 {entity.Id} 移除了 Position，原值: ({pos.X}, {pos.Y})");
});
```

### 9.6.2 监听组件设置

```csharp
world.SubscribeComponentSet<Health>((in Entity e, ref Health h) =>
{
    if (h.Value <= 0)
    {
        Console.WriteLine($"实体 {e.Id} 死亡！");
    }
});

// 业务代码
world.Set(e, new Health { Value = 0 });  // 触发上面的回调
```

## 9.7 性能影响

事件系统的主要开销有：

1. **每次 Add/Set/Remove/Create/Destroy 都额外调用方法**：即使没有订阅者，`OnComponentAdded<T>` 仍要 `lock` + 取 count + 返回。空订阅派发约 5~10ns，但累加到每帧上万次操作就显著了。
2. **每次订阅的闭包分配**：`SubscribeComponentAdded<T>` 会往非泛型列表中插入一个 lambda 适配器，捕获 `handler` 后产生一次堆分配。
3. **锁开销**：派发时对每个 handler 调用都加锁。在大量实体操作时，锁争用会成为瓶颈。
4. **委托调用本身**：委托 Invoke 是间接调用，JIT 通常无法内联。

> 🔥 **建议**：只在开发期/调试场景启用事件系统，发布版本用 `Release`（不带 `EVENTS`）配置编译。所有事件方法会被 `#if EVENTS` 裁掉，`World.Add` / `Set` / `Remove` 等热路径只剩纯组件操作。

下表对照了不同配置下 World.Add 的开销（参考值）：

| 配置 | Add 单次耗时 | 备注 |
|------|--------------|------|
| Release（无 EVENTS） | ~80ns | 基线 |
| Release-Events（无订阅者） | ~120ns | +50% 派发空检查 |
| Release-Events（1 个订阅者） | ~250ns | +委托调用 |
| Debug-Events（1 个订阅者） | ~400ns | JIT 未优化 |

## 9.8 启用事件系统

### 9.8.1 普通 .NET 项目

修改 `Arch.csproj` 中的 `DefineConstants`（参考 [Arch.csproj](file:///d:/Unity/Arch/Arch/src/Arch/Arch.csproj) 已内置多种配置）：

```xml
<PropertyGroup Condition="'$(Configuration)'=='Debug-Events'">
  <DefineConstants>TRACE;EVENTS;</DefineConstants>
</PropertyGroup>

<PropertyGroup Condition="'$(Configuration)'=='Release-Events'">
  <DefineConstants>TRACE;EVENTS;</DefineConstants>
</PropertyGroup>
```

构建时切换到 `Release-Events`：

```bash
dotnet build -c Release-Events
```

### 9.8.2 Unity 项目

1. `Edit > Project Settings > Player > Other Settings`
2. 在 `Scripting Define Symbols` 中添加 `EVENTS`
3. 点击 Apply，等待 Unity 重新编译

> ⚠️ Unity 中若用源码方式集成 Arch（见第 01 章方案三），需要确保 `Arch.Core.Events` 命名空间下的所有 `.cs` 文件都被纳入 Assets。如果用 DLL 方式，则需要自己用 `Release-Events` 配置编译 DLL 后再替换。

### 9.8.3 与 PURE_ECS 的关系

`PURE_ECS` 和 `EVENTS` 是两个互不影响的独立开关。但 `PURE_ECS` 下 Entity 不带 `WorldId` 字段，事件回调拿到 `in Entity` 后想再访问 World 必须显式持有引用：

```csharp
var worldRef = world;  // 捕获到闭包
world.SubscribeComponentAdded<Position>((in Entity e, ref Position p) =>
{
    // PURE_ECS 下不能 e.World，必须用 worldRef
    var other = worldRef.Get<Velocity>(e);
    // ...
});
```

## 9.9 配套示例

完整示例见 `Assets/Scripts/Chapter09/EventDemo.cs`，它演示了：

1. 启用 `EVENTS` 符号后订阅各类事件
2. 创建/销毁实体并观察日志顺序
3. 在 `OnComponentAdded` 中修改组件值
4. 批量 `world.Add<T>(in QueryDescription, in T)` 触发批量事件

```csharp
using Arch.Core;
using Arch.Core.Events;
using UnityEngine;

public class EventDemo : MonoBehaviour
{
    private World _world;

    private void Start()
    {
        _world = World.Create();

        _world.SubscribeEntityCreated(in entity =>
            Debug.Log($"[Create] Entity {entity.Id}"));

        _world.SubscribeComponentAdded<Position>((in Entity e, ref Position p) =>
            Debug.Log($"[Add] {e.Id} Position=({p.X},{p.Y})"));

        _world.SubscribeComponentRemoved<Position>((in Entity e, ref Position p) =>
            Debug.Log($"[Remove] {e.Id} Position=({p.X},{p.Y})"));

        // 触发事件
        var entity = _world.Create(new Position { X = 1, Y = 2 });
        _world.Remove<Position>(entity);
        _world.Destroy(entity);
    }
}
```

预期输出：

```
[Create] Entity 0
[Add] 0 Position=(1,2)
[Remove] 0 Position=(1,2)
[Create] Entity 1   // 注意：销毁会创建回收复用
```

> 📖 完整代码请运行 Chapter09 场景。注意：若未启用 `EVENTS` 符号，运行时不会输出任何日志，但编译依然通过——这正是 `#if EVENTS` 的设计目的。

## 本章小结

| 概念 | 位置 | 作用 |
|------|------|------|
| `EVENTS` 编译符号 | [Arch.csproj L53-L63](file:///d:/Unity/Arch/Arch/src/Arch/Arch.csproj#L53) | 控制事件代码是否编译 |
| `EntityCreatedHandler` | [EventHandlers.cs L6](file:///d:/Unity/Arch/Arch/src/Arch/Core/Events/EventHandlers.cs#L6) | 实体创建委托 |
| `EntityDestroyedHandler` | [EventHandlers.cs L11](file:///d:/Unity/Arch/Arch/src/Arch/Core/Events/EventHandlers.cs#L11) | 实体销毁委托 |
| `ComponentAddedHandler<T>` | [EventHandlers.cs L17](file:///d:/Unity/Arch/Arch/src/Arch/Core/Events/EventHandlers.cs#L17) | 组件添加委托（可修改值） |
| `ComponentSetHandler<T>` | [EventHandlers.cs L23](file:///d:/Unity/Arch/Arch/src/Arch/Core/Events/EventHandlers.cs#L23) | 组件设置委托 |
| `ComponentRemovedHandler<T>` | [EventHandlers.cs L29](file:///d:/Unity/Arch/Arch/src/Arch/Core/Events/EventHandlers.cs#L29) | 组件移除委托 |
| `Events` / `Events<T>` | [Events.cs](file:///d:/Unity/Arch/Arch/src/Arch/Core/Events/Events.cs) | handler 列表的分层存储 |
| `EventTypeRegistry` | [EventTypeRegistry.cs L11](file:///d:/Unity/Arch/Arch/src/Arch/Core/Events/EventTypeRegistry.cs#L11) | 类型 → 事件 ID 的静态注册表 |
| `EventType<T>` 静态构造 | [EventTypeRegistry.cs L40](file:///d:/Unity/Arch/Arch/src/Arch/Core/Events/EventTypeRegistry.cs#L40) | 自动分配 ID 并注册 |
| `World._compEvents` | [World.Events.cs L34](file:///d:/Unity/Arch/Arch/src/Arch/Core/Events/World.Events.cs#L34) | 按 ID 索引的 Events 数组 |
| `SubscribeComponentAdded<T>` | [World.Events.cs L70](file:///d:/Unity/Arch/Arch/src/Arch/Core/Events/World.Events.cs#L70) | 订阅 API |
| `OnComponentAdded<T>` | [World.Events.cs L203](file:///d:/Unity/Arch/Arch/src/Arch/Core/Events/World.Events.cs#L203) | 派发 API |
| 线程安全策略 | [World.Events.cs L10-L14](file:///d:/Unity/Arch/Arch/src/Arch/Core/Events/World.Events.cs#L10) | 不允许取消订阅 |

下一章我们将讨论 Arch 的命令缓冲区（CommandBuffer）机制，它能在结构变更频繁的场景下缓冲操作、延迟执行。
