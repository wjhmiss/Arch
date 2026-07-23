# 第17章 Arch.Relationships 关系建模

## 17.1 实体关系的需求

ECS 的核心是"实体 + 组件"——但现实世界里的对象往往**互相联系**：

- **父子关系**：武器跟随角色，角色站在车上
- **引用关系**：子弹的目标、Buff 的施加者
- **组合关系**：一个 Boss 由多个部件组成
- **聚合关系**：玩家拥有多个宠物

如果用传统方式建模，我们会陷入两难：

- 把目标实体直接放进组件？不行——`Entity` 是个值类型，但目标可能被销毁后产生悬空引用
- 把目标的 `Entity.Id` 存进组件？容易，但维护关系时双向更新很麻烦
- 给每个目标都加一个反向组件？组件类型会爆炸式增长

**Arch.Relationships** 模块提供了一个优雅的方案：用一个泛型 `Relationship<T>` 组件存储"我有哪些关系目标"，并用 `InRelationship` 组件反向追踪"我是哪些关系的目标"。

📖 整个模块只有 5 个源文件，位于 [Arch.Relationships 目录](file:///d:/Unity/Arch/Arch.Extended/Arch.Relationships)。

## 17.2 Relationship 组件

最核心的类型是 `Relationship<T>`（[Relationship.cs#L41](file:///d:/Unity/Arch/Arch.Extended/Arch.Relationships/Relationship.cs#L41)）：

```csharp
public class Relationship<T> : IRelationship
{
    internal readonly SortedList<Entity, T> Elements;

    public int Count => Elements.Count;

    // 添加一对关系
    internal void Add(in T relationship, Entity target)
    {
        Elements.Add(target, relationship);
    }

    // 获取与某个目标的关系数据
    public T Get(Entity entity) => Elements[entity];

    // 检查是否存在与某个目标的关系
    public bool Contains(Entity entity) => Elements.ContainsKey(entity);

    // 移除关系
    public void Remove(Entity target) => Elements.Remove(target);

    // 遍历
    public SortedListEnumerator<T> GetEnumerator()
        => new SortedListEnumerator<T>(Elements);
}
```

`T` 是"关系数据"的类型，可以是：

- `ParentOf`（空结构体，只表达"是父节点"）
- `int`（如伤害值、距离）
- `string`（如关系类型描述）
- 任意自定义结构

🔥 关键设计：`T` 是**关系附带的数据**，而 `Entity` 是关系的**目标**。一个 `Relationship<T>` 实例代表"本实体到多个目标的同类关系集合"。

## 17.3 InRelationship 反向引用

只有正向关系还不够。假设实体 A 是 B 的父节点，那 B 需要知道"我是某个 ParentOf 关系的目标"——这样才能在 B 销毁时清理 A 的关系。

`InRelationship` 就是这个反向引用（[InRelationship.cs#L12](file:///d:/Unity/Arch/Arch.Extended/Arch.Relationships/InRelationship.cs#L12)）：

```csharp
internal readonly struct InRelationship
{
    // 关系组件类型的 ID（如 ParentOf 的 ComponentType.Id）
    public readonly int ComponentTypeId;

    internal InRelationship(ComponentType targetRelation)
    {
        ComponentTypeId = targetRelation.Id;
    }
}
```

⚠️ `InRelationship` 是 `internal` 的，你不会直接使用它，但它由 `AddRelationship` 自动维护。

## 17.4 Entity 扩展方法

为了让 API 更自然，Arch 提供了一组针对 `Entity` 的扩展方法（[EntityRelationshipExtensions.cs#L13](file:///d:/Unity/Arch/Arch.Extended/Arch.Relationships/EntityRelationshipExtensions.cs#L13)）：

```csharp
public static class EntityRelationshipExtensions
{
    public static void AddRelationship<T>(this in Entity source, Entity target, T relationship = default!);
    public static void SetRelationship<T>(this in Entity source, Entity target, T relationship = default!);
    public static bool HasRelationship<T>(this in Entity source, Entity target);
    public static bool HasRelationship<T>(this in Entity source);
    public static T GetRelationship<T>(this in Entity source, Entity target);
    public static ref Relationship<T> GetRelationships<T>(this in Entity source);
    public static bool TryGetRelationship<T>(this in Entity source, Entity target, out T relationship);
    public static void RemoveRelationship<T>(this in Entity source, Entity target);
}
```

这些扩展方法内部会通过 `World.Worlds[source.WorldId]` 拿到 World，再委托给 World 的扩展方法。所以即使你拿到一个 `Entity` 值，也能直接调用关系 API。

📖 注意类被 `#if !PURE_ECS` 包裹，意味着在 PURE_ECS 模式下不启用——PURE_ECS 禁止实体持有任何引用类型的组件。

## 17.5 World 扩展方法

真正的关系逻辑在 `WorldRelationshipExtensions` 中（[WorldRelationshipExtensions.cs#L15](file:///d:/Unity/Arch/Arch.Extended/Arch.Relationships/WorldRelationshipExtensions.cs#L15)）：

### 17.5.1 AddRelationship

```csharp
public static void AddRelationship<T>(this World world, Entity source, Entity target, in T relationship = default!)
{
    // 1. 在 source 上添加（或获取）Relationship<T> 缓冲
    ref var buffer = ref world.AddOrGetRelationships<T>(source);
    buffer.Add(in relationship, target);

    // 2. 在 target 上添加反向引用，告知"你是某个 Relationship<T> 的目标"
    var targetComp = new InRelationship(Component<Relationship<T>>.ComponentType);
    ref var targetBuffer = ref world.AddOrGetRelationships<InRelationship>(target);
    targetBuffer.Add(in targetComp, source);
}
```

🔥 这段代码揭示了一个核心机制：每次建立关系，**两边都会被写入组件**：

- `source` 被加上 `Relationship<T>` 组件
- `target` 被加上 `InRelationship` 组件（指向 `Relationship<T>` 的类型 ID）

这样，无论从哪一端查找，都能 O(1) 找到对应关系。

### 17.5.2 其他常用方法

```csharp
// 检查存在性
public static bool HasRelationship<T>(this World world, Entity source, Entity target);
public static bool HasRelationship<T>(this World world, Entity source);

// 获取关系数据
public static T GetRelationship<T>(this World world, Entity source, Entity target);
public static bool TryGetRelationship<T>(this World world, Entity source, Entity target, out T relationship);

// 获取整个关系缓冲
public static ref Relationship<T> GetRelationships<T>(this World world, Entity source);

// 移除单条关系
public static void RemoveRelationship<T>(this World world, Entity source, Entity target);
```

### 17.5.3 自动清理（需要 EVENTS 编译符号）

当 target 实体被销毁时，source 上的关系不会自动清理。Arch.Relationships 提供了一个 `HandleRelationshipCleanup` 方法（[WorldRelationshipExtensions.cs#L23](file:///d:/Unity/Arch/Arch.Extended/Arch.Relationships/WorldRelationshipExtensions.cs#L23)）：

```csharp
public static void HandleRelationshipCleanup(this World world)
{
    world.SubscribeEntityDestroyed((in Entity entity) => 
        CleanupRelationships(world, in entity));
}
```

⚠️ 这个功能需要 `EVENTS` 编译符号。Arch 默认编译时启用了 EVENTS，所以 Unity 中可以直接使用。

## 17.6 创建父子关系示例

让我们用 Arch.Relationships 建模一个典型的"角色-装备"父子关系：

```csharp
using Arch.Core;
using Arch.Relationships;

// 1. 定义关系类型（空结构，仅作标记）
public record struct ParentOf;  // "我是父节点"
public record struct ChildOf;   // "我是子节点"

// 2. 创建实体
var world = World.Create();
var player = world.Create(new Position { X = 0, Y = 0 });
var sword  = world.Create(new Position { X = 1, Y = 1 });
var shield = world.Create(new Position { X = -1, Y = 1 });

// 3. 建立父子关系（双向）
player.AddRelationship<ParentOf>(sword);
player.AddRelationship<ParentOf>(shield);

sword.AddRelationship<ChildOf>(player);
shield.AddRelationship<ChildOf>(player);

// 4. 检查关系
Console.WriteLine(player.HasRelationship<ParentOf>(sword));   // True
Console.WriteLine(player.HasRelationship<ParentOf>(shield));   // True
Console.WriteLine(sword.HasRelationship<ChildOf>(player));     // True

// 5. 遍历所有子节点
ref var parentRels = ref player.GetRelationships<ParentOf>();
foreach (var (child, _) in parentRels.Elements)
{
    Console.WriteLine($"子节点: {child}");
}
```

🔥 这段代码完全没有手写"双向同步"逻辑——`AddRelationship<ParentOf>(sword)` 自动同时建立了：

- player 上的 `Relationship<ParentOf>` → sword
- sword 上的 `Relationship<InRelationship>` → player

## 17.7 带数据的关系

关系不只是父子标记，可以附带数据：

```csharp
// 用 int 表示"伤害值"——表示攻击者对目标造成的关系
attacker.AddRelationship(target, 100);  // T 推断为 int

// 获取关系数据
int damage = attacker.GetRelationship<int>(target);

// 修改关系数据
attacker.SetRelationship(target, 200);
```

也可以用复杂结构：

```csharp
public record struct Aggro
{
    public float Value;
    public DateTime LastSeen;
}

enemy.AddRelationship(player, new Aggro { Value = 80f, LastSeen = DateTime.Now });
```

## 17.8 遍历关系链

一个常见的场景是从根节点遍历整棵关系树。下面是一个递归遍历所有子节点的示例：

```csharp
static void TraverseTree(World world, Entity root, int depth = 0)
{
    Console.WriteLine($"{new string(' ', depth * 2)}{root}");

    if (!root.HasRelationship<ParentOf>())
        return;

    ref var children = ref root.GetRelationships<ParentOf>();
    foreach (var (child, _) in children.Elements)
    {
        TraverseTree(world, child, depth + 1);
    }
}
```

### 17.8.1 通过 Query 找出所有父节点

```csharp
var query = new QueryDescription().WithAll<Relationship<ParentOf>>();

world.Query(in query, (ref Relationship<ParentOf> parentOf) =>
{
    foreach (var (child, _) in parentOf.Elements)
    {
        Console.WriteLine($"父节点拥有子节点 {child}");
    }
});
```

📖 这个写法在官方测试 [RelationshipTest.cs#L199](file:///d:/Unity/Arch/Arch.Extended/Arch.Relationships.Tests/RelationshipTest.cs#L199) 里有完整示例。

### 17.8.2 查询子树大小

```csharp
static int CountSubtree(Entity root)
{
    if (!root.HasRelationship<ParentOf>())
        return 1;

    int count = 1;
    ref var children = ref root.GetRelationships<ParentOf>();
    foreach (var (child, _) in children.Elements)
    {
        count += CountSubtree(child);
    }
    return count;
}
```

## 17.9 关系删除与级联清理

### 17.9.1 手动删除单条关系

```csharp
player.RemoveRelationship<ParentOf>(sword);
// 现在 player 不再拥有 sword 这个子节点
// 同时 sword 上的反向 InRelationship 也会被移除
```

📖 实现见 [WorldRelationshipExtensions.cs#L269](file:///d:/Unity/Arch/Arch.Extended/Arch.Relationships/WorldRelationshipExtensions.cs#L269)。

### 17.9.2 当 Relationship 为空时自动移除组件

```csharp
public static void RemoveRelationship<T>(this World world, Entity source, Entity target)
{
    ref var buffer = ref world.GetRelationships<T>(source);
    buffer.Remove(target);

    if (buffer.Count == 0)  // ⭐ 关系为空时，移除整个 Relationship<T> 组件
    {
        world.Remove<Relationship<T>>(source);
    }
    // ... 反向同理
}
```

🔥 这是一个聪明的内存优化：如果一个实体没有任何 ParentOf 关系，它就不会持有 `Relationship<ParentOf>` 组件，避免内存浪费。

### 17.9.3 级联清理实体销毁

如果 `EVENTS` 符号启用，调用 `world.HandleRelationshipCleanup()` 后，销毁实体会自动清理所有相关关系：

```csharp
world.HandleRelationshipCleanup();  // 一次性注册

// 此后每次销毁实体都会自动清理
world.Destroy(sword);
// → player 上的 ParentOf 关系被移除
// → sword 上的 ChildOf 关系被移除
// → 若 player 不再有任何子节点，Relationship<ParentOf> 组件也被移除
```

## 17.10 配套示例

本章的配套 Unity 示例代码位于 `Assets/Scripts/Chapter17/RelationshipDemo.cs`，其中包含：

- 一个简单的层级关系演示：角色 → 装备 → 子部件
- 关系遍历：递归打印整棵实体树
- 关系数据演示：用 `int` 表示仇恨值
- 级联销毁：删除父节点时观察子节点的状态变化

运行该示例后，控制台会输出完整的实体层级树形结构。

## 本章小结

| 概念 | API | 说明 |
|------|------|------|
| 正向关系 | `Relationship<T>` | 存储实体到多个目标的关系数据 |
| 反向引用 | `InRelationship` | 标记实体是某些关系的目标（自动维护） |
| 添加关系 | `entity.AddRelationship<T>(target)` | 自动双向更新 |
| 获取数据 | `entity.GetRelationship<T>(target)` | 取出关系数据 |
| 整组遍历 | `entity.GetRelationships<T>()` | 返回 `Relationship<T>` 引用 |
| 查询关系 | `QueryDescription().WithAll<Relationship<T>>()` | 通过 ECS 查询找出所有有某种关系的实体 |
| 自动清理 | `world.HandleRelationshipCleanup()` | 订阅销毁事件，自动清理 |
| 关系数据 | `T` 可为任意结构 | 标记（空 struct）、值、复杂结构均可 |
| 关系为空时 | 组件自动移除 | 节省内存 |

下一章我们将学习 **Arch.Persistence**——把整个 World 序列化到文件并加载回来。
