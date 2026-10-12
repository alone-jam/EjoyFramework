# Collections — 无 GC 集合与 struct 事件通道

> WS5-M2（2026-09-28，代码完成，Unity 验证待补，见文末）。集合：`Runtime/Core/Collections/`（命名空间 `EjoyFramework.Core`），
> struct 事件：`Runtime/Core/Event/EventChannel.cs`（`EjoyFramework.Core.Event`），Unity 入口 `GameEntry.Event.GetChannel<T>()`。

## 选哪个（一张表）

| 场景 | 用法 | 分配 |
|---|---|---|
| 按 tick 的输入 / 快照历史、帧时间采样、日志尾巴 | `RingBuffer<T>`：Overwrite 固定窗口；`Clear(tick)` 对齐序号后按 tick 取 | 0（Grow 策略写满时翻倍） |
| 两端进出：BFS、撤销栈、单调队列 | `Deque<T>` | 0（写满时翻倍） |
| 优先级队列：定时器、A* open 表、资源请求 | `BinaryHeap<T>`（已有） | 0 |
| 原地排序 | `NoAllocSort`（已有） | 0 |
| 槽位占用、脏标记、组件掩码匹配 | `BitSet`（固定长度，或 `growable: true`） | 0 |
| 对象 / 实体表：句柄稳定、删除后旧句柄失效、连续遍历 | `SlotMap<T>` + `SlotHandle` | 0（容量内） |
| 每帧高频事件：命中、伤害、网络输入、系统间通知 | `EventChannel<T>`（struct 事件，按 `in` 传递） | 0 |
| 低频、要 sender / 多态参数 / 按 id 订阅的事件 | `GameEventArgs` + `GameEntry.Event.Subscribe/Fire`（原有） | 引用池往返 |

## 主路径

```csharp
// 输入历史：序号对齐 tick；确认后丢弃，回滚时丢弃预测
var inputs = new RingBuffer<PlayerInput>(128, RingBufferOverflow.Overwrite);
inputs.Clear(firstTick);
inputs.PushRef() = input;                          // 每 tick 一次，大结构体原地写
if (inputs.TryGetAtSequence(ackTick, out PlayerInput acked)) Reconcile(acked);
inputs.DiscardBefore(ackTick);
inputs.DiscardFrom(rollbackTick);

// 槽位表：用句柄代替引用 / 裸下标
var units = new SlotMap<UnitState>(1024);
SlotHandle h = units.Add(state);
ref UnitState live = ref units[h];                 // 句柄失效时抛出；不确定时用 TryGetValue / Contains
foreach (ref UnitState u in units) u.Tick(dt);     // 稠密遍历（顺序随删除变化）
units.Remove(h);                                   // 之后 h 永远查不到，即使槽位被复用

// 位集：掩码匹配与边遍历边清
if (entityMask.IsSupersetOf(queryMask)) { /* ... */ }
for (int i = dirty.FirstSet(); i >= 0; i = dirty.NextSet(i + 1)) { Flush(i); dirty.Clear(i); }

// struct 事件：通道与处理器委托都缓存到字段（方法组写法每次都会新建委托）
public struct HitEvent { public int Target; public float Damage; }

private EventChannel<HitEvent> m_Hits;
private EventChannelHandler<HitEvent> m_OnHit;

void Awake()
{
    m_Hits = GameEntry.Event.GetChannel<HitEvent>();
    m_OnHit = OnHit;
    m_Hits.Subscribe(m_OnHit);
}

void OnDestroy() { m_Hits.Unsubscribe(m_OnHit); }   // 框架已关停时不抛

void OnHit(in HitEvent e) { /* e 只读，不复制 */ }

m_Hits.FireNow(new HitEvent { Target = id, Damage = 12f });   // 同步，所属线程
m_Hits.Fire(hit);                                               // 任意线程排队，下一次 Update 派发
```

## 语义要点

| 主题 | 行为 |
|---|---|
| RingBuffer 序号 | 每个元素一个序号，连续递增，int 回绕安全（一律差值比较）。`HeadSequence` 是最旧元素，`NextSequence` 是下一个写入的序号。`Clear(n)` 对齐到外部编号；`DiscardBefore` 超过最新元素时清空但不跳号；`PopBack` / `DiscardFrom` 回退序号，下一次写入复用 |
| RingBuffer 写满 | Grow：翻倍（唯一分配路径）；Overwrite：挤掉最旧，`HeadSequence` 前进；Reject：`TryPush` 返回 false、`Push` / `PushRef` 抛出。任何策略下都可 `EnsureCapacity` 显式扩容 |
| 稀疏序号 | RingBuffer 只存连续序号，没有空洞。乱序到达、按包号确认的场景见下文 FPSSample 对照，属于 WS5-M4 连接层 |
| Deque | 环形数组，两端 O(1)、下标 O(1)；只增不减，`TrimExcess` 显式收缩（会分配） |
| BitSet 长度 | 固定长度：越界 `Set` 抛出；growable：越界 `Set` 自动扩容。两种模式下越界 `Test` 返回 false、越界 `Clear` 是空操作、负下标抛出。`Length` 之外的位恒为 0；长度上限 `BitSet.MaxLength`（`int.MaxValue - 63`），超出抛出 |
| BitSet 批量 | And / AndNot / Or / Xor / CopyFrom / Intersects / IsSupersetOf / SetEquals 逐字进行。参数更长时，Or / Xor / CopyFrom 在固定长度下遇到越界置位抛出，growable 则扩长；SetEquals 只比置位集合、与长度无关 |
| BitSet 扫描 | `Count` 增量维护，O(1)；FirstSet / NextSet / FirstClear / NextClear 逐字跳过，字内 de Bruijn 位扫描（.NET Standard 2.1 没有 `BitOperations`） |
| SlotMap 句柄 | (下标, 代数)：奇数代表占用，偶数代表空闲；default 句柄无效；删除后旧句柄失配。某槽位代数用尽（2^31 次复用）后永久退役，不会回绕误配。`Packed` / `FromPacked` 与 ulong 互转（存档、网络、日志）。句柄不带元素类型，不同 SlotMap 的句柄不要混用 |
| SlotMap 遍历 | 值稠密存放；删除是 swap-remove，稠密顺序会变，不要依赖。边遍历边删：按稠密下标倒序 `RemoveAt` |
| 枚举 | 全部是 struct 枚举器，`foreach (ref var x in c)` 可原地修改元素；枚举期间做结构修改会抛出（原地改元素不算） |
| 大容量寻址 | RingBuffer / Deque 的物理下标先判断回绕再相加，避免合法大容量下 `head + index` 的 int 溢出；不改变数组分配上限 |
| 集合线程 | 全部非线程安全，与 `List<T>` 相同 |
| EventChannel 线程 | 通道属于一个线程：`GetChannel` 取的属于 Framework 主线程，自建通道在第一次调用订阅 / FireNow 等 API 时绑定。只有 `Fire` 可在任意线程调用，其余 API 在其他线程调用会抛出。检查始终生效，不随构建配置裁剪 |
| EventChannel 派发 | 按订阅顺序调用；排队事件按 Fire 的先后派发。派发中新订阅的处理器不参与本轮（但参与处理器内嵌套的 FireNow）；派发中退订、本轮还没轮到的处理器不再调用（与 GameEventArgs 的快照语义不同）；处理器内 Fire 的事件留到下一次派发；`DispatchQueued` 不可重入 |
| EventChannel 异常 | 单个处理器抛出的异常记到 `FrameworkLog.Error`，不影响其余处理器，也不打断排队事件的派发 |
| EventChannel 订阅 | 重复订阅同一处理器（同目标 + 同方法）抛出；`Unsubscribe` 找不到时抛出，`TryUnsubscribe` 返回 false。关停后 Fire 被丢弃、订阅被忽略（打一条警告）、退订不抛出 |
| EventChannel 队列 | 按 2 倍扩容后复用，不丢事件；突发量级超出预期时会分配一次，之后稳态为 0 |
| EventManager 驱动 | `Update` 先派发 GameEventArgs 队列，再按通道创建顺序派发各通道的排队事件；`Shutdown` 一并关闭；关停后才取的通道直接处于关停态 |
| GameEventArgs 线程契约 | M2 起真实生效，不再依赖 Release 下被裁掉的 `EnsureMainThread`：Fire 任意线程；Subscribe / Unsubscribe / TryUnsubscribe / Check / Count / SetDefaultHandler 任意线程（订阅表加锁；另一线程退订返回后，已拍好快照的那一轮仍可能再调用该处理器一次）；Update 同一时刻只允许一个线程且不可在处理器内重入，违反时抛出；FireNow 在调用线程同步执行（EventManager 在编辑器 / 开发构建下断言主线程）；关停后 Fire 直接回收参数、Unsubscribe 不再抛出、Subscribe / SetDefaultHandler 忽略注册（Subscribe 的 null 参数仍抛出），等待订阅锁的工作线程也不能重新安装处理器；关停时逐个事件记录未退订的处理器（Debug 级） |

## FPSSample 对照

- `Networking/SequenceBuffer.cs`：稀疏结构，槽位取 `seq % capacity`，每个槽位另存序号判断有效性，元素用工厂预分配（class）；配套
  `Sequence.FromUInt16` 根据基线把线上的 16 位序号还原成 32 位。它服务于包级确认与乱序到达。RingBuffer 负责的是连续的按 tick 历史
  （struct 元素、`ref` 原地写、差值比较回绕安全）。稀疏版在 WS5-M4 的连接层按框架风格吸收（struct 元素、不用工厂委托）。
- 伤害事件：FPSSample 没有全局事件总线，而是在目标实体上挂 `DynamicBuffer<DamageEvent>`，由技能系统追加、伤害系统在固定阶段消费；
  缓冲写满时记错误并丢弃这次伤害。EventChannel 是全局按类型的队列，写满时扩容、不丢事件。"事件挂在实体上、按阶段消费"属于 ECS 的做法，
  在 WS5-M3 评估。

## 性能与零分配

- 零分配断言：`RingBufferTests` / `DequeTests` / `BitSetTests` / `SlotMapTests` / `EventChannelTests` 各有一个 `SteadyState_*` 用例（`ZeroAlloc.Assert`）。
- 基准（`FrameworkBenchmarks`，均硬断言 0 分配）：`Collections.RingBuffer.PushLookup`、`Collections.Deque.BothEnds`、`Collections.BitSet.Scan4096`、
  `Collections.SlotMap.AddLookupRemove`、`Event.Channel.FireNow4`、`Event.Channel.FireDispatch64`；对照组 `Event.GameEventArgs.FireNow4`（同样 4 个处理器，只记录不断言）。

## 验证状态

| 项 | 状态 |
|---|---|
| `EjoyFramework.Core` 独立编译（netstandard2.1 / C# 9，dotnet） | 通过（2026-09-28） |
| 纯逻辑测试在 CoreCLR + NUnit 上运行：M2 新增 81 例 + EventPool / EventManager 原有 16 例回归，共 97 例 | 通过（2026-09-28）；零分配按 `GC.GetAllocatedBytesForCurrentThread` 计，不能代替 Mono 的 GC.Alloc recorder |
| 本次审查修复：关停后的注册及锁竞争、RingBuffer / Deque 大容量寻址回归测试 | 已添加，执行待补；上面的 97 例是修复前的历史结果，不覆盖本次改动 |
| Unity EditMode / PlayMode 全量（unity-cli） | 待补：Mac 上尚未安装 Unity |
| Mono 下的零分配断言与基准数值 | 待补 |
| IL2CPP 真机 | 待补 |
