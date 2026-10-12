# EjoyFramework 自研 ECS

运行时全部由本仓库实现，仅依赖 .NET 基础类库。没有引用、包装或复制 Unity Entities、第三方 ECS 的实现。独立项目以 `netstandard2.1` 编译，Unity 程序集设置 `noEngineReferences: true`。

## 模块与所有权

| 模块 | 职责 |
|---|---|
| `EjoyFramework.Core.Ecs` | Entity、World、Sparse Set 组件存储、Query、CommandBuffer、SystemGroup、TickClock、EcsLoop |
| `EjoyFramework.Core.Unity`（`Ecs/` 子目录） | 场景世界生命周期、Update 输入帧时间的固定 tick / 手动驱动、表现对象绑定 |
| 游戏层 | 业务组件、系统、Transform 同步、物理/渲染/网络接入 |

每个 `World` 独立拥有实体和组件。`SystemGroup` 拥有命令缓冲，不拥有 World 或系统实例。Unity 中 `EcsWorldComponent` 拥有 World、TickClock 和 EcsLoop；禁用暂停自动推进，销毁释放世界和循环。外部代码不要单独释放 World/Loop，也不要直接驱动或重置借用的 Clock。系统实例的业务资源由创建者释放。

ECS 不作为全局单例注册到 Framework：一个进程可以同时模拟战斗世界、预览世界、服务端世界。现有 `Core.Entity` 继续管理资源与表现对象，现有 `UnitWorld` 可继续使用；本模块不改变这些 API，也不迁移现有游戏数据。

## 数据与查询

```csharp
using EjoyFramework.Core.Ecs;

public struct Position { public float X; }
public struct Velocity { public float X; }
public struct Sleeping { }

using var world = new World();
var entity = world.CreateEntity();
world.Set(entity, new Position { X = 0 });
world.Set(entity, new Velocity { X = 2 });

// 缓存查询和回调，避免每帧构造描述或闭包。
var moving = world.Query().WithNone<Sleeping>();
ComponentAction<Position, Velocity> step =
    (Entity e, ref Position p, ref Velocity v) => p.X += v.X * 0.02f;
moving.ForEach(step);
```

- `Set<T>` 插入或覆盖组件，`Get<T>` 返回原位引用，`TryGet<T>` 返回值副本。
- `TryGetComponentLifetime<T>` 返回组件成员代数：只在新增时改变，覆盖/ref 写入/扩容不改变；移除重加获得新代数，销毁中的实体在真正释放前仍有代数。缺失/外来/失效句柄返回 false/0，不创建组件池。该 token 只可结合 World、组件类型、完整 Entity 做相等判断，不能作为网络 ID 或变化 tick。
- 组件约束为 `struct`，无需注册、继承或特性；空结构体可以作 Tag。包含托管引用的结构体也能存储，但复制不深拷贝其引用字段。
- Entity 包含世界标识、索引、版本。销毁/取消预留后旧句柄失效，版本耗尽的槽位永久退役；不允许版本回绕复活旧实体。
- `Has/TryGet/IsAlive` 可用于判断过期实体；`Get/Set/Remove/DestroyEntity` 对无效实体抛异常。Dispose 后除 `IsAlive` 返回 false 外，数据操作抛 `ObjectDisposedException`。
- Query 描述不可变。`WithAll<A>().WithAll<B>()` 要求全部存在；`WithAny<A>().WithAny<B>()` 要求至少一种；`WithNone<C>()` 排除 C。三组条件之间是 AND。
- `ForEach<T1,...,T4>` 自动将泛型组件加入本次遍历的必需条件；Query 本身的 `Count` 只统计描述中显式声明的过滤条件。
- 默认 Query（包括类型化 ForEach）立即排除已请求销毁的实体。`IncludeDespawning()` 包含普通与待销毁实体，`OnlyDespawning()` 只匹配待销毁实体；两者返回不可变新描述，后续 WithAll/Any/None 保留该模式。清理阶段内 OnlyDespawning 只匹配本轮固定批次。
- 缓存的 Query 可看到后续新增实体/组件，纯 Any 查询不会重复返回实体。
- 从数量最少的必需组件集合开始遍历；没有必需组件时遍历活跃实体。遍历顺序不构成契约，删除采用末项交换。

**ref 的有效期止于下一次结构变更或世界释放。** 不跨调用保存 ref，不跨线程使用。ForEach 回调和系统执行期间允许修改已有值；直接创建/销毁实体、增删组件、释放世界会抛异常。嵌套只读查询及值修改可用。

## 组件类型查找

World 使用内部 `ComponentType<T>.Id` 直接索引本世界的组件池数组，不再在 Get/Has/TryGet/Remove/Set 热路径中执行 Dictionary<Type> 查找。泛型静态初始化保证每个 T 只分配一次 ID；不同类型同时首次使用时由原子 CAS 分配单调、不复用的 ID，耗尽时拒绝分配而不是回绕。没有反射扫描或类型发现。

- ID 仅用于当前运行时生命周期，取决于首次使用顺序，不可写入存档或网络协议，也没有重置入口。不同 World 共享类型 ID，但绝不共享组件池或组件内容；World 本身仍须单线程使用。
- 缺失的 Has/TryGet/Remove/Get 只探测本世界已有表，不创建池、不扩容表；首次探测某个 T 仍可能初始化其全局 ID。表只在实际注册池时增长，因此引用表大小取决于本世界注册过的最高类型 ID，而非本世界组件类型数量。
- World 另持有紧凑的已注册池列表；Destroy/Dispose 只遍历本世界实际注册的池，不扫描全局 ID 范围。Dispose 清空池内容并释放两个注册容器，其他世界及缓存的类型 ID 不受影响。
- 创建池和准备扩容表先在局部完成，已注册列表成功追加后才发布表入口；分配失败不留下表/列表不一致。组件容量分配失败时可能留下合法的空注册池，但不会破坏已有数据。首次注册/扩容仍会分配，应在热路径前预留或预热。

## 容量预留

批量创建前，在结构安全点显式预留，避免实体/组件数组在填充过程中扩容：

```csharp
const int entityIndexCapacity = 10000; // 绝对范围 [0, 10000)，不是“再创建 10000 个”。
world.EnsureEntityCapacity(entityIndexCapacity);
world.EnsureCapacity<Position>(entityIndexCapacity);
world.EnsureCapacity<Velocity>(entityIndexCapacity);
```

- `EnsureEntityCapacity(int capacity)` 只预留实体槽位和 alive 数组，不创建实体，不预留组件池。
- `EnsureCapacity<T>(int capacity)` 只预留 T 的 sparse 索引以及至少 capacity 个 dense 组件/实体索引，不改变实体数组。两者应按需要分别调用。
- capacity 表示**排他的实体索引上界**。若稀有组件只挂在 Index=9999 的一个实体上，仍需传 10000，而不是组件数量 1；从最高索引计算上界时使用 `checked(highestIndex + 1)`。当前单参数接口也将 dense 容量预留到这一上界，会为稀有高索引组件多分配存储。
- 仅接受 `0..0x7FEFFFFF`，越界抛 `ArgumentOutOfRangeException`，不进行可能溢出的倍增/加一计算。0 和较小请求不缩容；已释放世界或正在遍历/运行系统时，即使不需要扩容也拒绝调用，因为扩容会使借用的 ref 失效。
- 预留不改变实体/组件逻辑计数、句柄代数、现有值、命令缓冲预留句柄或待销毁队列。扩容先准备新数组，再替换该组数组；实际分配仍受元素大小、平台与可用内存限制，分配失败会抛异常，不能将允许的元素上限当作内存保证。
- 不预留命令缓冲、销毁队列、查询描述或未指定的组件类型；首次注册 T 时同时建立本世界的类型 ID 表入口。超过预留范围仍沿用原有按需扩容。测试使用单独世界预热后测量目标世界的首次填充，避免用目标世界预热掩盖缺失的预留。

## Struct 访问器：热路径查询

委托入口仍保留，已缓存的回调可继续使用。要把局部参数、计数等状态带入查询而不创建闭包，用具体 struct 实现 `IEntityVisitor` 或 `IEntityVisitor<T1,...,T4>`：

```csharp
public struct MoveVisitor : IEntityVisitor<Position, Velocity>
{
    public float DeltaTime;
    public int Moved;
    public void Visit(Entity entity, ref Position position, ref Velocity velocity)
    {
        position.X += velocity.X * DeltaTime;
        Moved++;
    }
}

// query 在初始化时构造并缓存；泛型参数顺序为访问器类型，然后是组件类型。
var visitor = new MoveVisitor { DeltaTime = 0.02f };
int matched = moving.ForEach<MoveVisitor, Position, Velocity>(ref visitor);
// visitor.Moved 已写回；实现非泛型 IEntityVisitor 的访问器另用 ForEach(ref entityVisitor)。
```

- 支持纯实体与 1–4 组件，组件泛型参数自动加入必需条件；与委托入口复用同一最小池遍历、All/Any/None 条件和销毁过滤。
- 以具体 struct + 泛型约束调用，不将访问器存入 interface/object，也不创建委托；组件池首次使用/扩容和查询描述构造仍可能分配，应在初始化期预热。
- 实现用访问器工作副本，在 finally 中写回。回调抛异常时，已发生的组件修改和访问器状态保留、遍历守卫释放，但不返回匹配计数。执行期间不要通过其他别名修改原访问器，也不要将同一访问器重入传给嵌套查询；嵌套查询使用独立访问器。
- 回调可以修改已有组件、记录命令或请求销毁，不能直接改变结构；组件 ref 不能保留到 Visit 返回后。
- `matched` 是正常完成的 Visit 数量；查询描述的 Count 不包含访问器额外要求的组件类型，语义与现有类型化委托查询一致。

## 结构变更与失败规则

```csharp
using var commands = new CommandBuffer(world);
moving.ForEach<Position>((Entity e, ref Position p) =>
{
    if (p.X > 100) commands.DestroyEntity(e);
});

var child = commands.CreateEntity(); // 已预留句柄，暂不可查询或直接 Set
commands.Set(child, new Position());
commands.Playback();                 // 顺序执行，child 此时活跃；Destroy 只登记请求
world.ProcessDespawns();             // 独立宿主的同步点；需反初始化时改用 EcsLoop 的 Despawn 阶段
```

预留句柄可以写入其他组件作为实体引用，无需回放时重映射。只有创建它的缓冲可以记录针对它的命令；激活之后其他缓冲可正常使用。`Clear/Dispose` 取消未激活预留并使旧句柄失效。

缓冲按记录顺序执行。`CommandBuffer.DestroyEntity(e)` 回放为 RequestDespawn：重复/回放时已失效的销毁请求幂等忽略；随后 Set/Remove 仍可操作尚未物理销毁的组件。记录时仍校验世界、存活或本缓冲预留资格；无效的 Set/Remove 在回放时继续报错。错误发生时：

1. 已完成的命令保留结果，不回滚。
2. 当前失败及后续命令丢弃，尚未激活的预留句柄释放。
3. 缓冲清空后可以复用，异常交给调用方处理。

在遍历中尝试 Playback 会被拒绝，待执行命令保留，以便在合法同步点重试。组件值分类型存放在复用列表中，记录 Set 不对每条命令装箱；首次使用或扩容仍会分配。

## 两阶段销毁

`world.RequestDespawn(entity)` 是非结构操作，可在遍历中调用：第一次返回 true，重复、失效、外来世界、default 或尚未激活的预留句柄返回 false。世界已释放时抛出。请求后 `IsAlive` 仍为 true，`EntityCount` 仍包含该实体，组件可读写；默认查询立即隐藏它，`PendingDespawnCount` 统计仍存活的请求数量。

- 单独使用 World/CommandBuffer：在结构安全点调用 `world.ProcessDespawns()`，按请求顺序立即释放当前批次，返回实际销毁数。该入口不运行反初始化系统。
- 单独使用 SystemGroup：全部系统成功后自动 ProcessDespawns；失败时已提交的请求保留，不提前释放。
- 使用 EcsLoop：tick 结算后、每帧表现后各设同步点。每轮先固定批次，再运行 Despawn 阶段，最后只释放这一批。清理系统用预先缓存的 `OnlyDespawning()` 查询读组件、释放资源。清理期间的新请求不进入当前批次，下一轮才能被这些系统看到，避免跳过反初始化。
- 每个同步点最多 `EcsLoop.MaxDespawnPassesPerSync`（4）轮；超出的请求保留到下一同步点。无 tick 的帧也有帧末清理。
- 清理失败时，未释放的选中实体恢复为 pending，保持在新请求之前；循环进入故障态。ResetClock 不清除这些请求，也不回滚已发生的清理副作用，清理系统应可幂等重试。
- `World.DestroyEntity` 仍是显式、立即生效的结构 API，绕过反初始化；不在遍历中调用。队列保留完整 Entity 代数，旧请求不会误删复用槽位的新实体。

## 实体分区索引

每个实体最多属于一个分区。键接受 `0..int.MaxValue`，使用稀疏字典保存首尾与计数，不按最大键分配数组。可以直接指定手工键；多个桥或加载操作应通过 `ReservePartitionKey()` 为各次生命周期保留独立键，而不是直接复用 cellId。

```csharp
int partitionKey = world.ReservePartitionKey();
world.SetPartition(entity, partitionKey);
int aliveMembers = world.GetPartitionCount(partitionKey);
var visitor = new SaveVisitor(); // 调用方实现 IEntityVisitor 的 struct，可通过 World 读取组件。
int visited = world.ForEachInPartition(partitionKey, ref visitor);
int newlyRequested = world.RequestDespawnPartition(partitionKey, maxRequests: 64);
// 不限新请求数量的便捷入口：world.RequestDespawnPartition(partitionKey)
// 显式取消成员关系：bool removed = world.ClearPartition(entity)
// IO 和 ECS 清理均结束且成员数为 0 后：world.ReleasePartitionKey(partitionKey)
```

- `ReservePartitionKey()` 在结构安全点从单调 long 游标寻找未占用键，跳过手工分区与空预留；字典插入成功后才推进游标。int.MaxValue 用尽后可表示耗尽位置并抛 OverflowException，不回绕，也不自动复用已释放键。
- `ReleasePartitionKey(key)` 同样要求结构安全点。未知/非预留键返回 false，负键被拒绝；预留键仍有任何存活成员（含 pending despawn）时抛异常，空预留删除后返回 true，不重置游标。清空最后成员不会自动释放预留，因此操作可一直保留键到 IO 与 ECS 清理全部结束。
- 预留是**合作式所有权**：只有预留者或其授权委托应向该键分配成员或释放它；裸整数不是安全 capability，不阻止不遵守约定的代码抢用或释放。框架不尝试判断外部 IO 是否已完成。

- `SetPartition` 尾插，移动时先从旧分区摘除再追加到目标尾部；赋相同键为保序 no-op。Clear 后重新 Set 视为新的尾插。每实体保存前后链，移动、删除成员为 O(1) 链操作；空手工分区自动移除，空预留分区保留到 ReleasePartitionKey。
- `GetPartitionCount` 与 `ForEachInPartition` 包含**所有仍存活的成员，包括已请求销毁的实体**，直至物理销毁、移动或 Clear。即使在 Despawn 阶段，分区遍历也不限制为当前清理批次；清理系统仍须遵守 OnlyDespawning 的批次约定，不能把分区遍历当作批次筛选。
- `ForEachInPartition<TVisitor>(key, ref visitor)` 以插入顺序调用具体 struct 的 IEntityVisitor.Visit，正常返回匹配数；异常时保留工作副本的访问器状态并释放遍历守卫，不返回计数。可用独立访问器嵌套遍历。Set/Clear 成员关系和其他结构变更在任意 World 遍历/系统执行期间均被拒绝，即使是 no-op；读取已有组件及请求销毁仍允许。
- `RequestDespawnPartition(key, maxRequests)` 一次前向扫描，按顺序跳过已 pending 的成员，达到新请求上限即停止；返回新增请求数。负预算被拒绝，0 不请求。它可在遍历中调用，不修改成员链。预算限制的是**新增请求**，不是扫描次数、实际释放数或 Despawn 清理预算；大量已 pending 成员仍可能被扫描。无预算重载等同于 int.MaxValue。
- Set/Clear 拒绝 foreign、stale、default、未激活预留句柄；负键和已释放 World 被拒绝。未知有效键的计数、遍历及请求均返回 0。新增目标分区记录先分配成功才改变旧成员关系；首次出现分区或字典扩容可能分配，重复操作可预热。
- 直接 Destroy、排队销毁最终释放、World.Dispose 自动释放成员关系；槽位代数仍校验句柄，旧请求或旧句柄不会重新绑定复用实体。Clear 只删除分区关系，不撤销已提交的销毁请求。本节仅提供索引，未实现流送桥。

## 系统执行

```csharp
public sealed class MovementSystem : ISystem
{
    private readonly Query m_Query;
    private readonly ComponentAction<Position, Velocity> m_Move;
    private float m_DeltaTime;

    public MovementSystem(World world)
    {
        m_Query = world.Query();
        m_Move = Move;
    }

    public void Update(World world, CommandBuffer commands, in EcsTime time)
    {
        if (time.DeltaTime > float.MaxValue)
            throw new System.ArgumentOutOfRangeException(nameof(time),
                "This float-based sample requires DeltaTime at most float.MaxValue.");
        m_DeltaTime = (float)time.DeltaTime; // 本示例的坐标/速度使用 float。
        m_Query.ForEach(m_Move);
    }

    private void Move(Entity entity, ref Position p, ref Velocity v)
        => p.X += v.X * m_DeltaTime;
}

using var systems = new SystemGroup(world);
systems.Add(new MovementSystem(world), order: 0);
var frame = EcsTime.ForFrame(0.02);
systems.Update(in frame);
// systems.Update(0.02f) 仍是便捷入口，仅构造无 tick 的帧时间。
```

`ISystem` 只有 `Update(World, CommandBuffer, in EcsTime)` 一个契约。`SystemGroup.Update(in EcsTime)` 原样传递时间，不累加或保存时钟；`DeltaTime`、`TickInterval`、`Alpha`、`ElapsedTime` 均为 double。`EcsTime.ForFrame(deltaTime)` 与 `default(EcsTime)` 不表示模拟 tick：Tick、TickInterval、Alpha、ElapsedTime 为 0，IsReplay 为 false（default 的 DeltaTime 也为 0）。TickInterval 为 0 时不允许非零 tick、插值或重放状态。只有已知 tick 网格的调用方才构造正 TickInterval 的快照。

固定 tick 的阶段驱动使用 `EcsLoop`，显式注入 `TickClock`（默认 30 Hz）。初始化时注册系统，之后每帧调用 RunFrame：

```csharp
var clock = new TickClock();
using var loop = new EcsLoop(world, clock);
loop.Add(EcsPhase.TickSimulation, new MovementSystem(world));
// 每帧：内部先 Accumulate，再逐个 TryAdvance；不是一次性预推进所有 tick。
int completedTicks = loop.RunFrame(realDeltaSeconds);
```

阶段固定为 `TickInput → TickSimulation → TickResolve → Despawn 同步点 → TickCompletedSink`（每个消费的 tick），随后 `Interpolation → Presentation → Despawn 同步点`（每帧，即使没有 tick）。没有待清理批次时不调用 Despawn 系统。阶段内沿用 SystemGroup 的 order 升序、同 order 注册顺序及每系统后命令回放，但循环内的组不自行释放待销毁实体。`Add/Remove/SetEnabled` 显式指定阶段，同一系统实例可注册到不同阶段，但不能重复注册到同一阶段；私有组不暴露给调用方。

`TickCompletedSink` 是借用的可选采集回调，只能在帧间替换；多 tick 帧会逐个回调，零 tick 帧不回调。它在遍历之外、循环执行之内运行，失败按现有规则使循环进入故障态，不回退已消费时间。这不是强制只读阶段：采集者必须不修改 World，排除仍 pending 的销毁实体，并先填好全部行再发布一个完整采集批次。

Tick 阶段收到当前消费的 tick、固定 DeltaTime/TickInterval、Alpha=0；表现阶段收到最新消费 tick、钳制到 MaxFrameDelta 的帧 DeltaTime 及余量 Alpha。ElapsedTime 始终为 tick 网格时间。EcsTime 不携带阶段元数据，系统按注册位置确定职责。Interpolation 只是调度阶段，历史状态与实际插值仍由调用方系统实现。

时钟配置不可变；MaxFrameDelta 先钳制帧时长，DroppedTicks 只统计随后追赶预算丢弃的整 tick，不统计帧时长钳制丢弃的时间。循环和时钟必须在同一线程构造/使用；使用循环期间不要从外部驱动、重置或共享该时钟。循环拥有各阶段组，不拥有 World、时钟或系统对象，Dispose 只释放组；运行时禁止重入、注册修改、重置或 Dispose。

系统执行或命令回放失败时立即停止后续阶段，SystemGroup 清理尚未回放的命令；之前已提交的世界修改和已消费 tick 不回滚。循环进入 IsFaulted 状态，剩余 pending ticks 保留供诊断。宿主修复世界状态后必须调用 `loop.ResetClock(explicitTick)`，清空 pending、余量和丢弃计数并解除故障，再运行新帧；该方法不恢复世界、不重放部分失败的 tick。非法输入和入口条件失败不累加时间，也不使循环进入故障状态。

复制 schema、基线/插值历史与精确组件恢复工具见 [Replication](Replication/README.md)；它们尚不是完整网络同步或回滚系统。流送桥由 Core 层消费本内核，接线见 [WorldStreaming](../../../docs/WorldStreaming.md)。Interpolation 阶段本身只负责调度，表现系统仍须显式采样并应用结果。

order 升序执行，同 order 按注册顺序；每个系统之后回放命令，下一个系统能查询到创建和组件变更，而销毁请求只隐藏实体、保留组件至同步点。支持启停与移除，同一实例不允许重复加入一个组。缓存了 World/Query 的系统实例只用于对应世界。

系统异常停止本次后续系统，丢弃该系统尚未回放的命令；已经发生的值修改和前面系统的结果保留。不允许重入、执行中修改系统组或释放它。时间由调用方传入，必须为有限非负值。

Update 收到的命令缓冲由 SystemGroup 拥有，系统可以记录或 Clear，不能 Dispose；系统组负责释放。世界释放时，即使外部仍缓存 Query，也会释放组件池的存储数组。

## 显式只改值的重放

```csharp
loop.Add(EcsPhase.TickSimulation, simulation, replayable: true); // 默认 false，仅 tick 阶段可开启。
loop.ValidateReplay(authorityTick, clock.Tick, maxReplayTicks: 128);
// 调用方继续检查全部基线、预测历史、输入与时间线；恢复状态并截断/替换预测锚点后：
int replayed = loop.Replay(authorityTick, clock.Tick, maxReplayTicks: 128);
```

- 区间为 `(baselineTick, targetTick]`，基线 tick 非零，目标必须等于真实时钟当前 tick，范围不回绕且不超过正预算。ValidateReplay 还检查循环空闲/非故障、World 无遍历/重放、时钟无待消费 tick，以及所有循环自有组无待执行命令；它不验证业务输入、权威来源或基线完整性，也不保留事务资格。Replay 会重新检查。
- 只执行已启用且显式允许重放的 TickInput/Simulation/Resolve 系统，使用固定间隔、Alpha=0、IsReplay=true。真实时钟、插值/表现阶段、Despawn 同步都不推进；空合法区间不调度系统或采集。
- 允许已有组件的直接/ref/延迟值写入；创建、销毁、增删成员、分区修改、请求销毁、预留/扩容与释放等结构操作被拒绝。禁止的命令在记录前报错；循环内部的重放回放仅处理已有组件 Set。
- 每个重放 tick 在采集前后检查所有循环自有缓冲，向跳过/已执行组残留命令会使循环故障并清理，不能泄漏到后续正常帧。独立外部缓冲及任意外部状态不由该循环管理。
- 每个成功重放 tick 仍调用 TickCompletedSink，位于遍历之外、重放守卫之内，允许本地预测历史重新记录。sink 仍须遵守不修改 World 的契约；不是强制只读视图。
- 异常停止后续工作、清除未回放的自有命令，并在 finally 解除守卫。之前已写的值/历史不会回滚，真实时钟不回退；调用方必须显式恢复。重放资格不是 IO 沙箱，也不保证原生指针安全或跨平台确定性。

## Unity 接入与示例

`EcsWorldComponent` 在 Unity `Update` 中输入帧时长，内部由 `TickClock` 驱动固定 tick，默认 30 Hz、每帧最多追赶 4 tick、帧时长上限 0.25 秒。`TimeSource` 可选 Scaled / Unscaled，默认 Scaled。Inspector 时钟配置只在 Initialize 时生效，修改后需 Shutdown 再 Initialize。组件必须在 Unity 主线程初始化和使用，不在字段初始化器中绑定线程。

```csharp
var driver = GetComponent<EcsWorldComponent>();
driver.Loop.Add(EcsPhase.TickSimulation, new MovementSystem(driver.World));
// 需要手动输入帧时间时：
driver.UpdateMode = EcsUpdateMode.Manual;
int ticks = driver.Step(1.0 / 60); // double 入口返回本帧完成的 tick 数；可能为 0
```

禁用组件只暂停自动更新，仍可显式 Step。Step 的 float 重载保留，但同样累积固定 tick；不再把可变帧 dt 直接传给模拟系统。旧的 `Systems.Add(...)` 改为 `Loop.Add(EcsPhase.TickSimulation, ...)`，原本只更新画面的系统应注册到 Presentation。未合并阶段引入的 `Step(in EcsTime)` 已移除，避免绕过组件时钟；需要自定义时间的独立宿主仍可直接使用 SystemGroup。

旧场景序列化的 `EcsUpdateMode.FixedUpdate`（值 2）映射到 Update 驱动，并标记过时。组件不再提供 FixedUpdate 回调，不会重复推进。调度/参数异常会禁用自动更新并保留世界；若 Loop.IsFaulted，调用方先恢复世界状态，再用 `driver.ResetClock(baseline)` 解除故障，最后自行启用组件。ResetClock 不回滚世界，也不自动启用。执行期间或外部 Query 遍历期间请求 Shutdown 被拒绝，世界与循环保持完整。系统实例的业务资源仍由创建者释放。

`EcsEntityView.Bind(world, entity)` 只绑定有效实体。`IsBound` 会识别销毁、版本变化和世界释放；`Unbind` 或销毁视图不会销毁模拟实体。接入原 Entity 对象池时在 OnShow 中 Bind，在 OnHide 中 Unbind。业务层明确选择模拟驱动 Transform 或 Transform 驱动模拟，避免双向覆盖。

包内示例位于 `Samples~/EcsDemo/`，程序集和命名空间为 `EjoyGame.Samples.EcsDemo`。Unity 示例通过 `ProjectileScenario(world, loop)` 将移动注册到 TickSimulation，命中/伤害/死亡注册到 TickResolve；独立 SystemGroup 的示例入口仍保留。宿主游戏若保留 `Assets/GameMain/Scripts/Samples/EcsDemo/` 副本，必须同步接口签名、`time.DeltaTime` 与 `driver.Systems` → `driver.Loop` 迁移；本仓库不包含该宿主，无法代为更新。将 `EcsDemoBehaviour` 挂到空物体，会创建方块目标与球形投射物；球到达目标后扣血，两者消失。需要场景自己的 Camera/光照。示例只演示一维命中，不提供物理碰撞或 Transform 插值算法。

示例的 `MovementSystem` 使用 float 坐标和速度，要求 `DeltaTime <= float.MaxValue`；超过时在转换、查询遍历和坐标修改之前抛出 `ArgumentOutOfRangeException`，避免转换为 Infinity 后使静止物体得到 NaN。该限制只属于示例的 float 边界，`EcsTime` 仍接受有限非负 double 时长。调用方仍需保证坐标、速度及位移计算结果不溢出 float 范围。宿主游戏的示例副本也应同步此校验。

另有 [封闭预测纠错示例](../../../Samples~/EcsDemo/PredictionCorrection.md)，以单实体、双组件和精确 tick 输入记录演示“预检 → 成对恢复 → 双历史锚点重建 → 重放采集”。它使用示例专有的 8Hz 整数模拟，不改变框架默认值；仅作为明确范围的组合用法，不是通用多实体回滚或网络会话实现。

## 验证

**M3 当前状态：代码与回归测试已编写，已做静态审查；尚未编译或运行本轮测试。** 初次独立 .NET 检查因缺少还原资产（NETSDK1004）未进入编译，后续未执行还原或安装。Unity EditMode / PlayMode、零分配断言和覆盖率均待环境就绪后验证；不能沿用旧验证记录作为本轮结果。

在项目根目录运行（测试依赖 NUnit 等测试工具；运行时不依赖它们）：

```powershell
rtk dotnet test Tools~/Ecs/EjoyFramework.Core.Ecs.Tests.csproj --configuration Release
rtk dotnet run --project Tools~/Ecs/EjoyFramework.Core.Ecs.Benchmark.csproj --configuration Release
```

Unity EditMode 测试位于 `Tests/Editor/Core.Tests/Ecs/`，并入 `EjoyFramework.Tests`，按 `EjoyFramework.Tests.Ecs` 过滤。
Unity PlayMode 测试位于 `Tests/Runtime/Core.Tests.PlayMode/Ecs/`，并入 `EjoyFramework.Tests.PlayMode`，按 `EjoyFramework.Tests.PlayMode.Ecs` 过滤。
独立 .NET 测试还会直接编译 `Samples~/EcsDemo/ProjectileScenario.cs`，验证移动、命中、伤害、死亡和预热后的分配。示例的 MovementSystem 已采用 struct 访问器；`QueryStructVisitorTests` 覆盖 0–4 组件、异常状态写回与销毁过滤，分配检测在 Unity 下使用 GC.Alloc 约束、CoreCLR 下使用线程分配计数器，并设已知分配的阳性对照。测试代码具备不等于已验证，本轮尚未执行。

基准测试使用 1千/1万/10万个实体，仅测串行标量移动，输出 200 次更新的中位数、P95 和当前线程托管分配；不包含初始化、渲染、物理或结构变更。该结果不能代替 Unity Mono/IL2CPP、移动设备或实际游戏性能验证。

## 实现边界

这是单线程 Sparse Set ECS，不提供并行读写调度、Burst 编译、Archetype/Chunk、自动序列化、回滚快照或网络同步。组件引用与结构变更规则为当前实现设计，不声称可直接交给 Unity Jobs。

Sparse Set 的组件查找与末项交换删除为 O(1)，扩容摊销；销毁实体需检查已注册的每种组件池。稀疏数组的内存随该组件曾使用的最大实体索引增长。因此大量稀有组件配合极高实体索引时，应测量内存后再选择分段索引或其他存储布局。
