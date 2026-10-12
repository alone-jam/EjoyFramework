# Replication — 组件复制契约

属于 `EjoyFramework.Core.Ecs`，命名空间仍为 `EjoyFramework.Core.Ecs`，不新增程序集或第三方依赖。

当前实现包括**行为接口、冻结的 schema 目录、权威基线、插值历史与显式状态拷贝/恢复工具**，尚不是完整联网、跨组件快照提交或回滚系统。

## 主路径

```csharp
public struct PredictedPosition : IPredicted<PredictedPosition>
{
    public float X;
    public bool VerifyPrediction(in PredictedPosition predicted)
        => System.Math.Abs(X - predicted.X) <= 0.001f;
}

var schema = new ReplicationSchema();
schema.RegisterPredicted<PredictedPosition>(schemaId: 17);
schema.Freeze();
if (schema.TryGet<PredictedPosition>(out var descriptor))
{
    // descriptor.SchemaId == 17；它不是内部 ComponentType<T>.Id。
}
foreach (var descriptor in schema.Descriptors)
{
    // 始终按显式 schema ID 升序，和注册顺序无关。
}
```

## 契约

- `RegisterReplicated<T>`、`RegisterPredicted<T>`、`RegisterInterpolated<T>` 都要求 `unmanaged`。后两者额外要求对应行为接口。
- Schema ID 是显式非零 ushort。重复 ID、重复类型和跨模式重复注册都报错，不留下部分注册；同样的 ID 本身不证明字段布局或 codec 版本兼容。
- 创建线程完成注册与 Freeze；Count、Descriptors、GetAt 和 TryGet 都要求先 Freeze。冻结后可并发只读，不能继续注册；创建线程重复 Freeze 是空操作。
- `Descriptors` 是只读 span，元素为 readonly struct，仅暴露外部 SchemaId/Mode，不暴露内部类型 ID。缺失查询返回 false/default（SchemaId=0）。
- `IPredicted<T>.VerifyPrediction` 的接收者是权威值；容差由组件定义。`IInterpolated<T>.Interpolate` 的接收者是较早样本，alpha 为有限 `[0,1]` 权重，返回新值。调用方必须在本地副本上调用，不能把存储中的可写 ref 交给行为方法。
- Schema 目录不调用组件行为、不扫描程序集、不反射创建类型。注册由手写或未来生成的具体调用完成；插值历史仅在显式 TrySample 时对样本副本调用 Interpolate。

## 性能与边界

注册是冷路径：单列表检测唯一性，单次 O(N)，避免多索引注册时的部分成功；冻结排序 O(N log N)，创建只读 ID 有序数组与内部类型索引数组。热路径类型查询 O(1)、schema ID 二分查询 O(log N)、span 遍历零额外容器；实际分配需在目标运行时验证。

内部类型索引数组大小取决于目录中最高的进程内类型 ID；首次泛型初始化与 Freeze 可以分配，不属于热路径承诺。初始化失败不隐藏异常，Freeze 的数组全部准备好后才发布，失败可重试。

`unmanaged` 只排除托管引用，**不代表可直接上传原始字节**。指针、native handle、本地 Entity 等仍需显式编码/身份策略；禁止用裸内存比较代替通用预测校验。

## 最新权威基线

`AuthoritativeBaselineStore<T>` 为每种 unmanaged 组件保存独立值副本，不修改 World。构造时借用一个 World；全部访问限创建线程，修改操作必须位于结构安全点。

```csharp
using var store = new AuthoritativeBaselineStore<PredictedPosition>(world);
var binding = store.Bind(entity); // 目标必须存活、未请求销毁，并已有组件。
var state = new PredictedPosition { X = 5 };
BaselineWriteResult result = store.TrySet(in binding, tick: 10, in state);
if (store.TryGet(in binding, out uint tick, out var copy)) { /* 使用副本 */ }
```

- Binding 包含存储实例、时间线、完整 Entity 身份与绑定代数；每次 Bind 都生成新代数并清空该实体旧基线。旧 token 不能读取、写入或解绑新绑定。
- TrySet 拒绝 tick 0；相同 tick 保留第一次值并返回 DuplicateTick，更旧返回 OlderTick。按不回绕模拟时间比较；`uint.MaxValue → 1` 不属于新 tick。
- 目标消失、换代、请求销毁或缺组件时，写入返回 TargetUnavailable，读取返回 false/default。Unbind 仍可释放匹配的已消失目标。
- 组件池为每次新增生成独立的 membership lifetime；移除重加即使发生在两次读取之间，也会自动使旧绑定的读写返回不可用。需要重新 Bind 才能接收新生命周期的数据，Unbind 可用于主动释放旧槽位。这里只是访问失效，不会主动清空所有外部存储。
- `World.TryGetComponentLifetime<T>` 的 token 与 World、组件类型、完整 Entity 一起才有意义；普通值更新、ref 写入、预留容量和请求销毁不会改变 token。它不是变化版本、tick 或网络 ID。
- ResetTimeline 清空全部绑定/基线、保留容量；旧排队数据必须继续携带原 token，不能给旧数据套上新 token。该方法不重置时钟或 World。
- EnsureCapacity 按绝对实体索引上界预留。热读写没有逐实体对象；构造、绑定时扩容和预留可以分配。Dispose 只释放自身数组，World 已释放后仍可 Dispose。

它只保存每个绑定的最新值，不构成跨组件一致的基线提交，也不执行恢复或重放。

## 插值历史

`InterpolatedHistoryStore<T>` 要求 `unmanaged, IInterpolated<T>`。每个类型使用平铺计数/样本数组，每实体最多保留 HistoryLength 个不同的非零 tick，不创建逐实体存储对象。

- `Bind(entity)` 生成新绑定并清空该实体历史；同一组件生命周期内的值更新不影响绑定。所有写入和采样都经过基线层的身份、组件 lifetime 与存活校验。
- `TryAdd` 允许有空间时的旧 tick 或满缓冲窗口内的乱序插入，重复 tick 保留首次样本。满窗口中新样本插入会淘汰最旧值；比最旧值还早则返回 OutsideWindow。权威最新基线不会因乱序样本倒退。
- `TrySample(binding, targetTick, fraction, out value)` 要求 fraction 有限且在 `[0,1)`；采样 tick 0 可表示最早样本之前的时间。空/无效绑定返回 false/default；单样本直接返回，范围外夹到相应端点，精确 tick 返回原样本，其余按实际 tick 间距插值。
- Interpolate 在局部样本副本上调用，即使组件方法错误修改 this，也不会污染历史。局部 TickClock.Alpha 不能直接视作任意远程样本之间的插值权重。
- ResetTimeline 清空全部绑定和样本、保留容量；旧数据必须携带原绑定，不得重新贴上新时间线身份。容量乘积在分配前做宽整数检查。

## 完成 tick 采集与精确恢复

`loop.TickCompletedSink` 在每个 tick 的结算和有界 Despawn 同步之后调用一次，包括同一帧内的每个 tick。无 tick 帧不调用。该 sink 由调用方拥有；执行期间不可替换、重入或释放循环。异常进入现有故障恢复流程，不回退已消费 tick。

这是**采集边界，不是强制只读阶段**：sink 必须遵守不修改 World 的契约，排除仍 pending 的销毁实体，并将行写入预分配存储后再发布完整批次。最新基线存储仅保存一个 tick，不能把重复覆盖它称为保留了所有预测历史。

`ComponentStateTransfer.TryCapture<T>` 返回存活、非销毁中组件的值副本与 lifetime；调用方确定采集时机。`TryRestoreExact<T>` 只在结构安全点、绑定和 lifetime 有效、同一个 World、存储 tick 精确等于请求 tick 时覆盖已有组件；不新增组件、不采样、不退回其他 tick，不改变源基线或时钟。false 时不写 World。

单组件恢复不等于跨组件事务。`TryRestoreExact<T1,T2>` 提供立即执行的两目标重载：先读取并验证两份基线及同一个精确 tick，全部成功后才覆盖两个已有组件；拒绝两个目标指向同一 Entity 的同一组件，同类型但不同实体则可用。它不暴露可延迟 Commit 的 token，也不回滚整个 World。

调用方必须保证两份基线来自同一权威时间线；两个独立存储的 tick 数值相等本身不能证明共同远端来源。更大范围的恢复仍需先将全部必需值读取到局部变量/预分配 scratch，全部校验成功后再写入，最后发布范围的已提交 tick；不能顺序调用单组件恢复后假设第二个失败能回滚第一个。

## 本地预测历史

`PredictionHistoryStore<T>` 要求 `unmanaged, IPredicted<T>`，用于本地可重建的预测分支，不能与远端插值的“重复样本首次生效”语义混用。

- `TryRecord` 追加较新 tick，也可替换当前最新 tick。更早的记录必须先 `TruncateAfter(anchorTick)`；精确读取缺失/被淘汰的 tick 返回失败，不插值或夹到其他 tick。
- 截断 0 抛出；低于保留边界返回 OutsideWindow 且不改状态；空历史、相等/未来锚点为无操作。保留窗口内的空洞可作为锚点，后续记录重建该分支。保留下界不倒退，未来槽位被清除，已淘汰数据不会因回退重新出现。
- `VerifyPrediction` 先校验权威/预测绑定对应同一当前组件生命周期，再取权威值所在 tick 的精确预测副本，在局部权威副本上调用 VerifyPrediction。结果区分 Match、Mismatch、MissingAuthority、MissingHistory 和 InvalidBinding；方法抛异常不修改保留值。
- 写历史使用只读身份校验，不调用权威基线的 TrySet 来冒充校验，也不向 World 写值。记录/截断必须在遍历之外，但不要求结构变更权限，供后续重放采集使用。
- 重绑、时间线重置、销毁及组件移除重加都会使旧访问失效。两个独立存储的相同数值 tick 不证明同一权威来源，调用方仍负责协调时间线。

## 封闭纠错示例

[PredictionCorrectionScenario](../../../../Samples~/EcsDemo/PredictionCorrection.md) 已将精确输入、双组件权威/预测基线、成对恢复、历史重建与重放采集接成一条代码路径。它拥有私有 World/Loop 和单个实体；所有前置条件包括匹配路径都先检查，缺失返回 ResyncRequiredNoChange。恢复开始后的异常保留原异常并设置 RequiresRecovery，不伪称事务回滚。

示例使用固定 8Hz 的整数模拟，属于示例策略，不改变框架默认 30Hz。它不是多实体通用回滚协调器，也不认证网络来源；详见示例文档的范围限制。代码与测试已静态审查，尚未编译/运行。

## 尚未实现

- 较大范围一致基线提交、网络输入/权威来源验证、完整多实体纠错编排和所有权转换策略。
- 强制只读采集阶段，以及 codec/schema 版本协商和网络传输。

## 验证

`Tests/Editor/Core.Tests/Ecs/ReplicationSchemaTests.cs` 覆盖目录；`AuthoritativeBaselineStoreTests.cs`、`InterpolatedHistoryStoreTests.cs`、`PredictionHistoryStoreTests.cs` 覆盖状态所有权、顺序、绑定、采样及预测分支重建；`ComponentLifetimeTests.cs` 覆盖成员代数；`EcsTickCaptureTests.cs` 和 `ComponentStateTransferTests.cs` 覆盖逐 tick 采集、精确 tick 与两目标预校验恢复。已完成对应独立静态审查，**未编译或运行测试**，不宣称并发/OOM/零分配已动态验证。

设计理由与后续边界见 [DESIGN.md](DESIGN.md)。
