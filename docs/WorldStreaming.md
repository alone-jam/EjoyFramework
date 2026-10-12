# World Streaming — 世界分区流送

> WS3-M2（2026-09-22）。Core：`Runtime/Core/Streaming/`，Unity：`Runtime/Core.Unity/Streaming/WorldStreamingComponent.cs`，
> 编辑器：`Editor/Core.Unity.Editor/Streaming/ChunkVisualizer.cs`（Scene 视图叠加）。

## 模型（一句话）

世界按整数单元 `(cx, cz)` 分区、分**层**（地形 / 建筑 / 植被……各有半径与 LOD 表），
**观察者**位置驱动期望集，期望集与活动集**差分**产生加载/卸载，加载**最近优先**并受预算限制，
业务经 `IWorldStreamingHandler` 做 IO 并异步回报。

## 主路径

```csharp
// 1. 组件配置（或直接 Framework.GetModule<IWorldStreamingManager>()）
var ws = GameEntry.WorldStreaming;                 // WorldStreamingComponent
ws.ConfigureLayer(layer: 0, loadRadius: 200f, unloadRadius: 260f, lodDistances: new[] { 80f, 160f });
ws.ConfigureLayer(layer: 1, loadRadius: 400f, unloadRadius: 480f, priorityBias: 5);   // 远景层，后加载

// 2. 登记单元（通常从 ConfigBlob 的 WorldCells 表批量登记；contentKey = 表行 Id）
foreach (var row in worldCells) ws.Streaming.RegisterCell(row.Layer, row.Cx, row.Cz, row.Id);

// 3. 观察者：玩家 Transform（分屏 / 观战再绑一个 id）
ws.BindObserver(observerId: 1, player.transform);

// 4. IO 处理器：每单元一个附加场景（内置），或自定义 IWorldStreamingHandler
ws.UseSceneHandler(new MySceneResolver());        // contentKey → "World/L0_12_7"

// 5. 玩家已站在未加载的地面上：同步兜底
int cell = ws.Streaming.FindCell(0, cx, cz);
ws.Streaming.RequireLoaded(cell);

// 6. 浮动原点：业务把场景整体平移 -delta 时
ws.ShiftOrigin(delta);
```

## 语义要点

| 主题 | 行为 |
|---|---|
| 期望集 | 任一观察者 `LoadRadius` 内（到单元中心）的单元；多观察者取并集 |
| 滞回 | 已加载单元只有到最近观察者距离 > `UnloadRadius` 才卸载（`UnloadRadius` 必须 > `LoadRadius`） |
| 优先级 | 键 = 最近距离 + `PriorityBias × CellSize`；每次评估排队单元按新距离重排（旧堆项凭入队戳丢弃） |
| 预算 | `MaxLoadStartsPerFrame` / `MaxLoadsInFlight` / `MaxUnloadsPerFrame`；预算不足的动作顺延到后续帧 |
| 评估触发 | 观察者移动超过 `ReevaluateMoveThreshold`（默认 CellSize/4）、配置/登记变化、`ForceReevaluate` |
| 取消 | 加载中离开半径 → `CancelLoad`，状态 `Cancelling`；迟到的成功结果自动转为卸载。若取消期间观察者已返回，失败回报完成后会重新评估并加载，不需再次移动观察者 |
| 失败 | `NotifyLoaded(id, false)` → 回到 Unloaded 并计数；下次评估自动重试 |
| 注销 | 已加载先卸载、加载中先取消；槽位在最终回报后释放，旧 id 查询得到 `None` |
| 浮动原点 | 内部一律世界坐标；`ShiftOrigin` 累加偏移，观察者本地坐标按新原点换算，单元不动 |
| 复杂度 | 评估只枚举半径覆盖的整数 cell 范围，与世界总单元数无关；稳态零分配（1681 单元巡逻断言） |

## IWorldStreamingHandler 契约

- 全部回调在主线程；`BeginLoad` / `BeginUnload` 可同步完成（回调内直接 `Notify*`）。
- 回调抛异常：加载按失败处理、卸载按已卸载处理，不会卡死状态机。
- `CancelLoad` 后**仍必须**回报 `NotifyLoaded`（成功或失败），管理器据此收尾；不能取消的 IO（场景）直接等结果。
- 一次 `BeginLoad` 必须配对恰好一次 `NotifyLoaded`，一次 `BeginUnload` 配对一次 `NotifyUnloaded`。管理器忽略不符合当前状态的回报并告警，但**这不是完整的重复/旧回报防护**：回报只有 cellId，没有操作代数，旧回报若在该 id 重新加载后到达，无法与新操作的合法回报区分。处理器必须遵守单次完成契约；需要容忍此类错误生产者时，须端到端扩展带操作 token 的协议，不能仅靠本地计数器补救。

## 内置 SceneStreamingHandler

每单元一个附加场景。`ISceneNameResolver.Resolve(layer, cx, cz, contentKey)` 返回场景资源名（null = 该单元无场景，立即视为加载成功）。
同一场景名映射到两个单元是配置错误：第二个单元按失败处理并报错。场景卸载失败会记录错误并按已卸载处理，避免单元永久卡在 Unloading。

## 装饰器链（WS3-M3）

三个内置装饰器都同时实现 `IWorldStreamingHandler`（向内转发动作）与 `IWorldStreamingNotifier`（向外转发回报）：

| 装饰器 | 动作侧（BeginUnload 前） | 回报侧（NotifyLoaded 成功时） |
|---|---|---|
| `PersistentStreamingHandler` | `ICellStateSerializer.Capture` → `WorldStateStore` | 有记录则 `Restore` |
| `PopulationStreamingHandler`（GamePlay） | `PopulationManager.OnCellUnloaded`（回收实例） | `OnCellLoaded`（按存亡生成） |
| `NavMeshStreamingHandler` | 卸 tile + 归还资产 | 加载 navmesh 资产 → `INavigationManager.AddNavMeshTile` |

**推荐接线**（加载时先恢复存亡再生成；卸载时先采集再回收）：

```csharp
var population  = new PopulationStreamingHandler(mgr, downstream: mgr, populationManager);
var persistence = new PersistentStreamingHandler(mgr, downstream: population, store, populationManager /* ICellStateSerializer */);
var scene       = new SceneStreamingHandler(persistence /* 回报链头 */, sceneManager, resolver);
persistence.Inner = population; population.Inner = scene;
mgr.SetHandler(persistence);
// 动作：mgr → persistence → population → scene ；回报：scene → persistence → population → mgr
```

`WorldStateStore` 与 Save 模块组合：`store.ToArray()` 放进存档结构（或 `WorldStateSaveData`），读档后 `store.Load(bytes)`；
`IsDirty` 供自动存档节流。格式带魔数与版本，损坏数据读入失败时存储保持为空。

## ECS 流送桥

`EcsStreamingHandler` 将 IO 完成与 ECS 分帧生成/销毁分离。Core 显式单向引用 Core.Ecs；桥借用同一 EcsLoop 的 World，不拥有 Loop、World、Inner 或 populator。所有入口必须在构造线程调用；Loop/World 的生命周期必须覆盖桥的全部活动操作。

```csharp
// 动作：manager -> persistence -> bridge -> IO
// 回报：IO -> bridge -> persistence -> manager
var persistence = new PersistentStreamingHandler(manager, manager, store, serializer);
var bridge = new EcsStreamingHandler(persistence, ecsDriver.Loop, populator);
persistence.Inner = bridge;
bridge.Inner = new MyIoHandler(bridge); // IO 的 Notify* 目标必须是 bridge。
manager.SetHandler(persistence);
ecsDriver.UpdateMode = EcsUpdateMode.Manual;

// 在统一主线程驱动点，每帧按此顺序；不要再由另一处重复推进 manager/driver。
manager.Update(deltaTime, deltaTime);
int creates = 32, despawnRequests = 64;
bridge.Pump(ref creates, ref despawnRequests);
ecsDriver.Step(deltaTime);
```

- `BeginLoad` 在调用 Inner 前安装操作，捕获只读 `CellPopulationContext`（CellId、Layer、Cx/Cz、ContentKey、InitialLod、Generation、PartitionKey），每次尝试使用 World.ReservePartitionKey 的新键。清理不查询 manager.TryGetCell，因此注销后元数据仍有效。`TryGetContext` 可供 serializer/业务访问当前操作的键。
- IO 回报只记录状态、排队；同步 NotifyLoaded(true) 不生成实体，也不立即回报下游。Pump 在结构安全点调用 `ICellEntityPopulator.Populate(World, in context, ref remainingCreateBudget, ref cursor)`，每次 Pump 中每个已排队操作最多执行一次，共享创建/新增销毁请求预算。0 预算仍能完成无需创建/请求的终结记账。创建预算为 0 时不调用 populator，即使它可能产生空内容。
- populator 的 cursor 从 0 开始且不得回退；预算必须为非负且不得增加，每创建一个实体消耗一个预算。必须在 CreateEntity 后**立即** SetPartition 到 context.PartitionKey，再做可能失败的初始化；返回前初始化本次创建的实体。不得保留借用 ref、释放借用 World、移动/清空分区关系假装已清理或释放桥持有的键。返回 true 才表示整个代数完成。异常或不合法输出会耗尽该次剩余创建预算并进入取消清理，避免把未知消耗再次分配出去。
- **部分实体仍可见。** 推迟 NotifyLoaded 不会让 ECS 查询自动隐藏已创建的实体。业务应使用 readiness 组件/查询约定（例如生成时 Ready=false，常规模拟跳过它，整个分区完成后统一设为 true）；失败或取消不得宣布 Ready。持久化 Restore 在整个生成完成后、manager 得到成功前执行；正常卸载先由 persistence Capture，再进入桥清理。取消和生成失败只向外回报失败，不把部分内容送入 Restore/Capture。
- CancelLoad 先标记取消再调用 Inner.CancelLoad，仍等待加载的终结回报。迟到成功不向外发成功：先清理 ECS，再调用 Inner.BeginUnload，等卸载回报后才发 NotifyLoaded(false)。正常卸载同样先预算化请求销毁，等待 GetPartitionCount 为 0（包含 pending，不能提前清空关系），才开始 IO 卸载并最终 NotifyUnloaded。后续 LOD 仅在 Loaded 时透传，不重建代数。
- 操作在实体物理清除、IO 终结及旧工作令牌失效后才释放键。内部队列携带代数并验证当前操作身份；外部调用后不覆盖同步重入造成的取消、卸载或新加载。成功/终结状态在下游回调前设置，回调中的 Pump 重入被拒绝，但正常取消/卸载/下一代加载动作可被记录。
- Inner 调用若在可能启动 IO 后抛异常，桥内部记录错误并保留操作，不让 manager 把异常误当终结。`TryGetFault`/`LastError` 用于诊断；仍需真实终结回报，桥不会猜测 IO 已结束或重试可能重复启动的卸载。下游回调抛异常也不自动重发，因为其副作用可能已执行。
- Pump 必须位于 ISystem 外，拒绝正在运行或故障态的 Loop，也拒绝外部回调重入。Inner 只能在无活动操作时替换；Dispose 也要求无加载中、已加载或清理中操作，不释放借用对象。操作对象按加载尝试分配，不做危险的提前复用；队列/字典容量、首次组件/实体/分区扩容属于冷分配，预热后的 Pump 自身无稳态分配目标仍待执行测试验证。
- **v1 只支持上文 exactly-once 的合规 IO。** 本地代数仅保护桥队列；旧重复 IO 回报若在相同 cellId 新加载后到达仍无法识别。这里没有增加端到端 IO token，也不宣称容忍该违约行为。

桥回归测试已编写，涵盖同步回调、共享/零预算、取消、重入、元数据捕获、持久化顺序、异常清理与分配检测；尚未编译或执行，不代表已通过验证。

## 未做 / 后续

- 四叉树 / 层级分区（超大世界的多级 LOD 单元）：当前统一网格已覆盖常规开放世界规模，按需再加。
