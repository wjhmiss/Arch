# 第16章 Arch.LowLevel 低级集合

## 16.1 为什么需要非托管集合？

C# 标准库提供的 `List<T>`、`Dictionary<K,V>`、`Queue<T>` 等集合都是**托管对象**——它们分配在托管堆上，由 GC 自动回收。但在 ECS 中，我们经常需要：

- 把集合作为组件的字段（组件是 `struct`，不能包含引用类型字段，否则无法被 Arch 高效存储）
- 在热路径中频繁分配释放，但又不想触发 GC
- 在多线程并行查询时避免 GC 锁

**Arch.LowLevel** 模块提供了一组**非托管集合**（unmanaged collections），它们：

- 都是 `struct` 或 `sealed class`，但内部数据存储在非托管内存中
- 类型参数约束为 `unmanaged`，意味着不能存引用类型
- 实现了 `IDisposable`，使用完必须手动释放
- 完全避开 GC，可在 Burst/IL2CPP 中安全使用

🔥 整个模块的源码位于 [Arch.LowLevel 目录](file:///d:/Unity/Arch/Arch.Extended/Arch.LowLevel)，包含 7 个核心文件。

## 16.2 UnsafeArray

最基础的非托管集合是 `UnsafeArray<T>`，它就是一个**指针 + 长度**的薄封装（[UnsafeArray.cs#L14](file:///d:/Unity/Arch/Arch.Extended/Arch.LowLevel/UnsafeArray.cs#L14)）：

```csharp
public readonly unsafe struct UnsafeArray<T> : IDisposable where T : unmanaged
{
    internal readonly T* _ptr;

    public UnsafeArray(int count)
    {
        _ptr = (T*)NativeMemory.Alloc((nuint)(sizeof(T) * count));
        Count = count;
    }

    public ref T this[int i]
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => ref _ptr[i];
    }

    public void Dispose()
    {
        NativeMemory.Free(_ptr);
    }
}
```

### 16.2.1 基本用法

```csharp
using Arch.LowLevel;

// 分配
var array = new UnsafeArray<int>(100);

// 访问（按引用返回，可修改）
array[0] = 42;
ref var first = ref array[0];
first = 100;

// 转 Span 与现有 API 互通
Span<int> span = array.AsSpan();
Array.Sort(span);

// 释放！
array.Dispose();
```

### 16.2.2 静态工具方法

`UnsafeArray`（非泛型版）提供了几个静态工具方法：

```csharp
// 复制
UnsafeArray.Copy(ref source, srcIndex, ref dest, dstIndex, length);

// 填充
UnsafeArray.Fill(ref array, 0);

// 调整容量（注意：会释放旧数组，返回新数组）
array = UnsafeArray.Resize(ref array, newCapacity);
```

⚠️ `Resize` 会**释放原数组**并返回新数组。如果你还持有旧指针的引用，会读到已释放内存——这是经典的 use-after-free 错误。

📖 完整定义见 [UnsafeArray.cs#L211](file:///d:/Unity/Arch/Arch.Extended/Arch.LowLevel/UnsafeArray.cs#L211) 至末尾。

## 16.3 UnsafeList

`UnsafeArray` 容量固定，不能动态增减。`UnsafeList<T>` 在其基础上实现了 `IList<T>`，支持动态扩容（[UnsafeList.cs#L14](file:///d:/Unity/Arch/Arch.Extended/Arch.LowLevel/UnsafeList.cs#L14)）：

```csharp
public unsafe struct UnsafeList<T> : IList<T>, IDisposable where T : unmanaged
{
    private UnsafeArray<T> _array;
    public int Count { get; }
    public int Capacity { get; }
}
```

### 16.3.1 基本用法

```csharp
var list = new UnsafeList<int>(capacity: 16);

list.Add(1);
list.Add(2);
list.Add(3);

list.Insert(0, 0);  // [0, 1, 2, 3]

list.RemoveAt(2);    // [0, 1, 3]

// ref 访问，零开销
ref var item = ref list[1];
item = 100;

// 释放
list.Dispose();
```

### 16.3.2 扩容机制

```csharp
public void Add(T item)
{
    if (Count == Capacity)
    {
        EnsureCapacity(Capacity * 2);  // 两倍扩容
    }
    _array[Count] = item;
    Count++;
}
```

`EnsureCapacity` 会分配新数组、复制旧数据、释放旧数组：

```csharp
public void EnsureCapacity(int min)
{
    if (min <= Count) return;

    var oldArray = _array;
    var newArray = new UnsafeArray<T>(min);
    UnsafeArray.Copy(ref oldArray, 0, ref newArray, 0, Count);
    oldArray.Dispose();  // ⚠️ 释放旧的

    _array = newArray;
    Capacity = min;
}
```

🔥 关键点：**扩容时的旧数组会被立即释放**，不会等 GC，所以没有内存碎片问题。

### 16.3.3 与 Span 互通

`UnsafeList` 实现了 `AsSpan()` 和 `GetEnumerator()`：

```csharp
foreach (ref var item in list)  // ref 迭代器，零拷贝
{
    item *= 2;
}

Span<int> span = list.AsSpan();  // 仅包含 Count 个元素
```

## 16.4 UnsafeQueue 与 UnsafeStack

两者结构与 `UnsafeList` 类似，分别实现 FIFO 和 LIFO。

### 16.4.1 UnsafeQueue

定义见 [UnsafeQueue.cs#L15](file:///d:/Unity/Arch/Arch.Extended/Arch.LowLevel/UnsafeQueue.cs#L15)：

```csharp
public unsafe struct UnsafeQueue<T> : IEnumerable<T>, IDisposable where T : unmanaged
{
    private UnsafeArray<T> _queue;
    private int _capacity;
    private int _frontIndex;  // 队首指针
    private int _count;
}
```

使用：

```csharp
var queue = new UnsafeQueue<int>(capacity: 16);
queue.Enqueue(1);
queue.Enqueue(2);

int first = queue.Dequeue();  // 1
int peek = queue.Peek();      // 2
```

💡 `UnsafeQueue` 采用**环形缓冲区**实现，`Dequeue` 不移动元素，只前移 `_frontIndex`，所以入队出队都是 O(1)。

### 16.4.2 UnsafeStack

定义见 [UnsafeStack.cs#L14](file:///d:/Unity/Arch/Arch.Extended/Arch.LowLevel/UnsafeStack.cs#L14)：

```csharp
var stack = new UnsafeStack<int>(capacity: 16);
stack.Push(1);
stack.Push(2);

int top = stack.Pop();   // 2
int peek = stack.Peek(); // 1
```

🔥 一个小细节：`UnsafeStack` 的迭代器是**反向的**（从栈顶到栈底），见 [UnsafeStack.cs#L216](file:///d:/Unity/Arch/Arch.Extended/Arch.LowLevel/UnsafeStack.cs#L216) 返回 `UnsafeReverseEnumerator`，符合栈的语义。

## 16.5 JaggedArray 与 UnsafeSparseJaggedArray

当我们需要按"实体 ID"随机访问大量元素时，普通的 `UnsafeArray` 会有问题：实体 ID 可能很大（比如 100 万），即使我们只用其中一小部分，也得分配整个 100 万的数组。**锯齿数组**（Jagged Array）就是为此而生。

它把整个地址空间分成多个固定大小的 **Bucket**，每个 Bucket 独立分配。访问时通过两次位运算定位到 Bucket 和 Bucket 内索引，**O(1) 时间，O(实际使用) 空间**。

### 16.5.1 JaggedArray

定义见 [JaggedArray.cs#L105](file:///d:/Unity/Arch/Arch.Extended/Arch.LowLevel/Jagged/JaggedArray.cs#L105)：

```csharp
public class JaggedArray<T>
{
    private readonly int _bucketSize;          // Bucket 大小（必须是 2 的幂）
    private readonly int _bucketSizeShift;     // 位移数（用于替代除法）
    private readonly int _bucketSizeMinusOne;  // 用于位与运算（替代取模）
    private Array<Bucket<T>> _buckets;         // Bucket 数组

    public void Add(int index, in T item)
    {
        IndexToSlot(index, out var bucketIndex, out var itemIndex);
        ref var bucket = ref GetBucket(bucketIndex);
        bucket[itemIndex] = item;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void IndexToSlot(int id, out int bucketIndex, out int itemIndex)
    {
        bucketIndex = id >> _bucketSizeShift;          // 除以 bucketSize
        itemIndex = id & _bucketSizeMinusOne;          // 取模 bucketSize
    }
}
```

🔥 关键性能优化：`_bucketSize` 强制为 **2 的幂**，因此可以用**位运算**替代除法和取模。这是一个非常经典的优化技巧。

### 16.5.2 UnsafeSparseJaggedArray：稀疏版本

`UnsafeSparseJaggedArray<T>`（[UnsafeSparseJaggedArray.cs#L142](file:///d:/Unity/Arch/Arch.Extended/Arch.LowLevel/Jagged/UnsafeSparseJaggedArray.cs#L142)）是它的非托管版本，并且**真正稀疏**——未使用的 Bucket 完全不分配内存：

```csharp
public struct UnsafeSparseJaggedArray<T> : IDisposable where T : unmanaged
{
    private readonly int _bucketSize;
    private UnsafeArray<UnsafeSparseBucket<T>> _bucketArray;
    private readonly T _filler;  // 默认填充值，用于标记"空槽"

    public void Add(int index, in T item)
    {
        IndexToSlot(index, out var bucketIndex, out var itemIndex);
        ref var bucket = ref GetBucket(bucketIndex);
        bucket.EnsureCapacity();   // ⭐ 懒分配！
        bucket[itemIndex] = item;
        bucket.Count++;
    }
}
```

`EnsureCapacity` 方法只会在第一次写入时分配数组：

```csharp
internal void EnsureCapacity()
{
    if (Array != UnsafeArray.Empty<T>())
        return;

    Array = new UnsafeArray<T>(Capacity);
    Clear();
}
```

📖 `UnsafeSparseBucket` 定义见 [UnsafeSparseJaggedArray.cs#L13](file:///d:/Unity/Arch/Arch.Extended/Arch.LowLevel/Jagged/UnsafeSparseJaggedArray.cs#L13)。

### 16.5.3 使用场景

```csharp
// 创建：bucket size 16，总容量 64
var sparse = new UnsafeSparseJaggedArray<int>(bucketSize: 16, capacity: 64);

// 用稀疏索引访问
sparse.Add(0, 10);    // 仅 Bucket 0 被分配
sparse.Add(100, 20);  // 仅 Bucket 0 和 Bucket 6 被分配

if (sparse.TryGetValue(0, out var value))
{
    Console.WriteLine(value);  // 10
}

sparse.Dispose();
```

⚠️ 因为是稀疏的，必须用 `TryGetValue` 检查，不能用 `this[]`（找不到会读到 filler 默认值）。

## 16.6 Resources 资源池

`Resources<T>`（[Resources.cs#L49](file:///d:/Unity/Arch/Arch.Extended/Arch.LowLevel/Resources.cs#L49)）解决了一个常见问题：**如何在 ECS 组件中引用托管对象？**

例如，你想给一个 `Sprite` 组件附加一个 `Texture2D`（Unity 的 `UnityEngine.Object`）。但 `Texture2D` 是引用类型，不能直接放在 `unmanaged` 组件里。解决方案：

1. 把 `Texture2D` 注册到 `Resources<Texture2D>` 池子
2. 拿到一个 `Handle<Texture2D>`（值类型，可以放在组件里）
3. 需要时通过 handle 取回引用

### 16.6.1 类型定义

```csharp
public readonly record struct Handle<T>
{
    public readonly int Id = -1;
    public static readonly Handle<T> NULL = new(-1);
}

public sealed class Resources<T> : IDisposable
{
    private JaggedArray<T> _array;
    internal Queue<int> _ids;  // 回收的 id 队列

    public Handle<T> Add(in T item);
    public ref T Get(in Handle<T> handle);
    public void Remove(in Handle<T> handle);
    public bool IsValid(in Handle<T> handle);
}
```

### 16.6.2 实战示例

```csharp
using Arch.LowLevel;

// 1. 创建资源池
var textures = new Resources<Texture2D>();

// 2. 注册资源，拿到 handle
var tex1 = textures.Add(Resources.Load<Texture2D>("cat"));
var tex2 = textures.Add(Resources.Load<Texture2D>("dog"));

// 3. handle 可以安全地存在 ECS 组件中
public struct Sprite : IComponent
{
    public Handle<Texture2D> Texture;  // ✅ 值类型，可以放在组件里
}

// 4. 使用时取回
ref Texture2D tex = ref textures.Get(entity.Get<Sprite>().Texture);
Graphics.DrawTexture(rect, tex);

// 5. 删除时回收
textures.Remove(tex1);
```

🔥 关键设计：

- `Handle<T>` 是 `readonly record struct`，只有 4 字节
- 内部用 `Queue<int>` 回收已删除的 ID，避免空洞
- `Remove` 不是真的删除，而是把 ID 放入回收队列，下次 `Add` 时复用

📖 完整代码见 [Resources.cs#L49](file:///d:/Unity/Arch/Arch.Extended/Arch.LowLevel/Resources.cs#L49)。

## 16.7 使用场景总结

| 场景 | 推荐集合 | 理由 |
|------|----------|------|
| 固定容量数组 | `UnsafeArray<T>` | 最简单，零开销 |
| 动态列表 | `UnsafeList<T>` | 标准 `IList<T>` 实现 |
| 任务队列 | `UnsafeQueue<T>` | 环形缓冲，O(1) 入队出队 |
| 撤销栈 / DFS | `UnsafeStack<T>` | LIFO |
| 实体按 ID 存储 | `UnsafeSparseJaggedArray<T>` | 稀疏分配，省内存 |
| 托管资源引用 | `Resources<T>` + `Handle<T>` | 解锁 unmanaged 组件持有引用 |

## 16.8 在 Unity 中使用注意事项

⚠️ Unity + IL2CPP 平台对 `unsafe` 代码有要求：

1. `Player Settings` → `Other Settings` → 勾选 **Allow 'unsafe' Code**
2. 如果用 Burst 编译，需要 `[BurstCompile]` 配合 `Unity.Burst.BurstCompiler` 编译时启用 unsafe
3. `NativeMemory.Alloc` 在 .NET 6+ 才有，Unity .NET Framework 4.x 下会回退到 `Marshal.AllocHGlobal`（见 [UnsafeArray.cs#L34](file:///d:/Unity/Arch/Arch.Extended/Arch.LowLevel/UnsafeArray.cs#L34)）

💡 这意味着 Arch.LowLevel 在 Unity 上**开箱即用**，不需要额外的 native 插件。

## 16.9 配套示例

本章的配套 Unity 示例代码位于 `Assets/Scripts/Chapter16/LowLevelDemo.cs`，其中包含：

- 用 `UnsafeList<int>` 实现一个简单的粒子池
- 用 `UnsafeSparseJaggedArray<int>` 演示稀疏存储
- 用 `Resources<Texture2D>` + `Handle<Texture2D>` 演示如何在组件中引用 Unity 资源
- 一个 `MonoBehaviour` 入口：跑分对比非托管集合 vs `List<T>` 的 GC 表现

运行后你会看到：在 10 万次插入/删除循环中，`List<int>` 产生 100+ MB GC 分配，而 `UnsafeList<int>` 0 GC。

## 本章小结

| 集合 | 容量 | 特点 | 适用场景 |
|------|------|------|----------|
| `UnsafeArray<T>` | 固定 | 最薄封装，0 开销 | 已知大小的连续存储 |
| `UnsafeList<T>` | 动态 | 实现 `IList<T>` | 替代 `List<T>` |
| `UnsafeQueue<T>` | 动态 | 环形缓冲，O(1) | 任务队列、消息队列 |
| `UnsafeStack<T>` | 动态 | LIFO + 反向迭代 | 撤销栈、DFS |
| `JaggedArray<T>` | 动态 | Bucket 数组 + 位运算 | 按 ID 索引 |
| `UnsafeSparseJaggedArray<T>` | 动态 | 懒分配 Bucket | 实体 ID 稀疏存储 |
| `Resources<T>` | 动态 | ID 回收 + Handle 抽象 | 在 unmanaged 组件中持有引用 |

下一章我们将学习 **Arch.Relationships**——如何在 ECS 中建模实体之间的父子、引用等关系。
