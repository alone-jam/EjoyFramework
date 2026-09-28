# EjoyFramework 开放世界完整解决方案路线图

> **恢复协议**：任何会话开始时先读本文件 + `git log -10`，找「状态追踪」里第一个未勾选的里程碑继续。
> 每个里程碑完成 = 代码 + 测试（含零 GC 断言）+ 文档 + 一次提交。验证统一走 unity-cli
> （命令见 `docs/PackageMigration.md` 与本文末尾），不接受"静态编译通过"替代运行。
>
> 制定：2026-09-21。依据：`FrameworkAnalysis.md`（隐患 10 项 / 零 GC 缺口 13 项）、`EXPANSION_PLAN.md`、
> `ConfigBlob.md`、`DOTS-Research.md`、代码规模与测试计数实测（下表）。

## 0. 目标与质量标准

**目标**：框架成为**大型开放世界游戏的完整解决方案**——(a) 游戏开发（首要），(b) 性能基建，
(c) 上线后的性能检测与分析（LiveOps）。

**每个模块的"专业级"定义（验收口径，逐条可检）**：

| 维度 | 口径 |
|---|---|
| 完整 | 覆盖该领域 AAA 团队的常规需求，不留"能跑但用不了"的半成品 API |
| 性能 | 热路径零托管分配（`AllocatingGCMemory` 断言，被测委托先执行一遍再断言）；有基准数字；无每帧字符串键查找/闭包/LINQ/装箱 |
| 易用 | 一条主路径 API、命名与生命周期与全框架一致、错误信息可执行（说明怎么修） |
| 健壮 | 引用计数/配对/重入/生命周期有断言与测试；异常路径不留半状态；线程契约写在类头且有验证 |
| 统一流程 | 模块 = `IXxxManager`(Core) + `XxxComponent`(Core.Unity) + Helper 注入 + 生成注册；诊断接入 Debugger；配置走 ConfigBlob |

## 1. 现状盘点（2026-09-21 实测）

规模：Core 42 模块 ~34k 行；Core.Unity 39 目录 ~15k 行；GamePlay 26 系统 ~34k 行；Editor ~14.6k 行。
测试：Core.Tests 890、GamePlay.Tests 1304、Pathfinding.Tests 31、Jobs.Tests 8、PlayMode 22（共 2255）。

成熟度 0-5（5 = 专业级）。「估」= 依据规模/测试/既有审计估计，未逐行复核。

| 模块 | 层 | 行数 | 成熟度 | 开放世界关键度 | 结论 |
|---|---|---|---|---|---|
| Base（模块系统/ReferencePool/EventPool/TempText/Log） | Core | 5217 | 4 | 高 | 已过多轮审查；缺 CollectionPool/BufferPool/零分配格式化 |
| ObjectPool | Core | 1231 | 3 | **高** | 能用；缺预热/预算/收缩/诊断，Release 走快照数组 + 委托，Spawn 按 string 名 |
| Pool（SpawnPool，GameObject） | Core.Unity | 516 | 2.5 | **高** | string 键字典、每次加载闭包、无预热/预算/收缩、与 ObjectPool 语义未统一 |
| Resource（Manager/Loader/AssetBatch/AssetRef） | Core+Unity | 3538 | 3.5 | **高** | AssetBatch 已零 GC 定案；缺取消/帧预算调度/内存预算与淘汰/区域加载卸载/目录标签 |
| Blobs（ConfigBlob） | Core | 2651 | 4.5 | 高 | 95 测试、零 GC 断言、13× 基准；缺真机验证与业务迁移 |
| Streaming | Core | 339 | 1.5 | **极高** | 骨架：string chunkId、逐块距离判定、事件通知；无分区网格/预算/异步调度/LOD 编排/浮动原点/持久化 |
| Spatial（AoiGrid） | GamePlay | 421 | 2.5 | 高 | 仅 AOI 网格；需通用空间索引（网格/四叉树/BVH）供流送/索敌/寻路共用 |
| Navigation | Core | 245 | 1.5 | 高 | 接口壳；无流式 navmesh、动态障碍、off-mesh、群体 |
| Pathfinding | GamePlay | 836 | 3 | 中 | A*/网格；31 测试 |
| AI（BT+黑板） | Core | 351 | 2 | 高 | 最小 BT；无 Utility/感知/群体/调试器；Blackboard 类型不匹配静默 |
| Ecs | Core | 838 | 3 | 中 | 自研 SparseSet；未与流送/世界分区集成 |
| Jobs | Core.Jobs.Unity | 小 | 3 | 中 | 封装可用；JobScope 句柄覆盖隐患（隐患#6） |
| Entity / Scene / Sound / Video / UI(+MVVM) | Core+Unity | 大 | 3.5-4 | 中 | UI/MVVM 成熟；MVVM 嵌套路径不刷新（隐患#1） |
| Save | Core+Unity | 1500 | 3.5 | 高 | HMAC+AES+迁移；缺**流式世界状态**（分块增量）持久化 |
| Network / Netcode | Core / GamePlay | 1117 / 4965 | 3 | 中 | 每连接一线程、全量快照、Unreliable 假语义（隐患#5） |
| Performance / Diagnostics | Core+Unity | 1329+387 | 3 | **高（LiveOps）** | 计数器/分级/告警/崩溃接口具备；缺采样上报、ANR、覆盖层、设备分级自动画质、日志上传 |
| LiveOps（成就/邮件/任务/活动/榜/公会/聊天/匹配/云存/AB） | GamePlay | 7487 | 3.5 | 中 | 面广；后端为 Null Object，需接口契约测试 |
| Combat / Units / Attributes / Targeting | GamePlay | 7000 | 3 | 高 | 双轨重复（隐患#3）；Units 无测试（隐患#10） |
| Atmosphere / Worldmap / Dialogue / Quest / Items / Loot / Crafting | GamePlay | 各 0.5-1.7k | 3 | 高 | 存在但薄；需与流送/存档/ConfigBlob 打通 |
| Input | Core+Unity | 511 | 2 | 中 | 抽象浅；无重映射/上下文互斥/录制 |
| HotUpdate(iFix) / Patch / Download / Http | Core+Unity | ~2.5k | 3.5 | 中 | 已审查；Patch 头注释过期 |
| Editor（codegen 7 线/构建/校验） | Editor | 14.5k | 4 | 高 | 齐全；缺基准基建与 unity-cli CI 脚本 |

## 2. 缺口清单（按优先级）

### P0 —— 没有它做不了开放世界

| # | 缺口 | 现状 | 形态 | 工作量 |
|---|---|---|---|---|
| P0-1 | **对象池专业化** | ObjectPool/SpawnPool 见上表 | Prewarm/Trim/Budget/Metrics；Spawn 按 int id；零 GC；SpawnPool 与 ObjectPool 统一语义与诊断 | M |
| P0-2 | **CollectionPool + BufferPool** | 缺口#1/#2 | `CollectionPool<List<T>>` 等静态池 + `using` 作用域；2 的幂分桶 `BufferPool<T>`；接入 Network/Download/Save | M |
| P0-3 | **资源调度**：取消/优先级/帧预算 | LoadAsset(priority, callbacks)，IAssetLoadHandle 存在但未贯通 | 统一 `LoadHandle`（可取消、可查询）、按优先级出队、每帧毫秒预算、同步回退规则 | L |
| P0-4 | **资源内存预算与淘汰** | 无 | 预算（MB）+ LRU/引用计数联合淘汰 + 预警事件 + 诊断 | M |
| P0-5 | **世界分区与流送** | Streaming 骨架 | 网格/层级分区（cell 键 = 整数坐标）、玩家位置驱动的 in/out 集合差分、预算化异步加载队列、LOD/HLOD 编排、浮动原点、区域级 Resource 加载/卸载 | XL |
| P0-6 | **空间索引** | AoiGrid | 通用 `SpatialGrid`/`Quadtree`（Core，纯 C#），流送/索敌/感知/寻路共用，零 GC 查询 | M |
| P0-7 | **帧预算调度器 + 主线程派发** | 缺口#10/#11 | `FrameBudgetScheduler`（毫秒预算跑任务队列）、`MainThreadDispatcher`（下沉现有手写） | M |
| P0-8 | **性能遥测上报** | Performance 有计数器 | 帧时间分位/内存/GPU/温度/加载时长采样 → 批量离线队列 → 上报接口；采样率与隐私开关 | M |
| P0-9 | **崩溃/ANR/日志采集** | ICrashReporter 接口 | 托管异常 + 原生崩溃钩子 + ANR（主线程看门狗）+ 日志环形缓冲上传 + 符号表出包脚本 | M |

### P1 —— 专业级必需

| # | 缺口 | 形态 | 工作量 |
|---|---|---|---|
| P1-1 | 零分配格式化 / 字符串哈希 ID / Span 解析（缺口#3/#7/#8） | ValueStringBuilder、StringHash + 编辑期碰撞检查、DataTable/Localization 走 Span | M |
| P1-2 | Struct 事件通道 / 无 GC 集合（缺口#4/#5） | `Event<T:struct>`、RingBuffer/Deque/PriorityQueue/BitSet/SlotMap | M |
| P1-3 | 流式世界状态存档 | 分块增量（chunk delta）+ 全局状态；与 Save 迁移链集成 | M |
| P1-4 | AI：Utility/感知/群体 + BT 调试器 | 感知系统（视觉/听觉，走空间索引）、Utility 选择器、群体避让钩子、编辑器可视化 | L |
| P1-5 | Navigation：流式 navmesh 与动态障碍 | 分块 navmesh 加载/卸载、off-mesh、Helper 接 Unity Navigation | L |
| P1-6 | 生成/种群管理（Spawn & Population） | 密度/预算驱动的 NPC/怪物生成，与流送与对象池联动 | M |
| P1-7 | 战斗双轨合并 / Units 测试 / MVVM 嵌套刷新 / JobScope（隐患#1#3#6#10） | 见 FrameworkAnalysis 隐患清单 | M |
| P1-8 | 设备分级与自动画质 / 游戏内性能覆盖层 | Performance 分级 → 画质档位表（ConfigBlob）；IMGUI/UITK 覆盖层 | M |
| P1-9 | 基准基建 | `Benchmark` 测试类别 + 结果落盘 + unity-cli 脚本；计数器死值自校准（已在 ConfigBlob 基准示范） | S |
| P1-10 | 交互系统 / 相机框架（ThreeC）/ 角色移动与动画框架 | 交互体积+提示；相机模式栈；locomotion 状态机 + 动画参数桥 | L |

### P2 —— 完整性

| # | 缺口 | 工作量 |
|---|---|---|
| P2-1 | 确定性 Random/定点数（缺口#12）、帧临时分配器（#9）、分配监控（#13） | M |
| P2-2 | Netcode：AOI 接入下发、真 Unreliable、状态 schema、连接线程池化 | L |
| P2-3 | Input：重映射/上下文互斥/录制回放 | M |
| P2-4 | LiveOps 后端契约测试与远程配置/AB 与画质分级联动 | M |
| P2-5 | 过场/Timeline 编排、天气与时间对流送/AI 的驱动 | M |
| P2-6 | 文档与样例：每模块 README（用法主路径 + 反例）、开放世界样例场景 | M |

## 3. 工作流与顺序

```
WS1 基础（P0-1/2/7）──► WS2 资源（P0-3/4）──► WS3 世界流送（P0-5/6 + P1-3/5/6）
        │                                                │
        └──► WS4 性能与 LiveOps（P0-8/9 + P1-8/9）        └──► WS5 玩法（P1-4/7/10）──► WS6 完整性（P2）
```

WS1 与 WS4 可并行；WS3 依赖 WS1/WS2。每个 WS 拆里程碑（M），见状态追踪。

## 4. 状态追踪

- [x] **WS1-M1 ObjectPool 专业化**（2026-09-21）：Prewarm/Trim/Metrics(O(1))/非分配 GetAllObjectInfos；
      Release/Trim/Manager.Release 零分配（缓存筛选委托 + `NoAllocSort` 堆排序，Mono 的 List.Sort 两个重载都会分配）；
      重入释放安全；重复 Unspawn 先校验再回调；Debugger 面板显示指标。测试 +19（对象池 28 + NoAllocSort 4），
      全量 2251/2252 绿。Spawn 按 int id 移到 WS1-M2 与 SpawnPool 一并做。
- [x] **WS1-M2 SpawnPool 统一**（2026-09-21）：重写为 `SpawnPoolEntry`（每键条目，业务缓存引用即无字符串哈希）+
      `SpawnPoolInstance`（实例标记：缓存 ISpawnCallback 数组、状态、外部 Destroy 回报计数）。
      Prewarm/Trim/TrimAll/MaxIdle/ExpireTime 周期清扫/指标（复用 ObjectPoolMetrics）/RegisterPrefab 直登 prefab；
      Despawn 走 GetComponent 定位；首加载委托缓存、等待者双列表交换；Debugger 新增 SpawnPool 页。
      旧实现每次 Spawn/Despawn 的 `GetComponentsInChildren` 数组分配与每次 Despawn 全表扫描已消除。
      PlayMode 测试 +14（含 Spawn/Despawn 零分配断言），全量 PlayMode 39/39 绿。
- [x] **WS1-M3 CollectionPool / BufferPool**（2026-09-21）：`Core/Pooling/`（R1 前在 `Core/Base/`）—— `CollectionPool<TColl,TItem>`
      （ListPool/HashSetPool/DictionaryPool 便利入口，ThreadStatic 无锁、using 作用域、每线程保留上限、
      编辑器下重复归还检测、Interlocked 指标）、`StringBuilderPool`（超容量不回池）、`BufferPool<T>`
      （2 的幂分桶 ArrayPool 语义、每桶一锁、跨线程租还、clearArray 选项、超 16M 直分配）。
      接入：ByteBuffer 构造/扩容/收缩走 BufferPool；TCP 发送帧租借 + 写出后归还（每包零托管分配）。
      接收 body 仍 new（所有权经 CreatePacket 转移给派生类，改契约会破坏业务子类，留 WS2）。
      测试 +34（CollectionPool 12 / BufferPool 13 / AllocProbe 8 + 1 合并）。
      **方法论发现**：Unity Mono 下同一代码路径进程内首次执行（JIT/泛型实例化）会被计为 GC.Alloc——
      零分配断言必须先执行一遍同一个委托再 Assert（AllocProbeTests 固化了这条结论）。
- [x] **WS1-M4 FrameBudgetScheduler + MainThreadDispatcher**（2026-09-21）：`Core/Scheduling/`，两个新 FrameworkModule
      （已登记生成注册表）。`IMainThreadDispatcher`：Post(Action) / Post(IMainThreadWork 池化工作项，执行后自动归还)，
      双缓冲数组 + 单次交换（ConcurrentQueue 每 32 项分配一段，不满足稳态零分配；自投递项留到下一帧），
      每帧 drain 毫秒上限，异常隔离。`IFrameBudgetScheduler`：IBudgetedTask.Step 切片，优先级 + FIFO，
      每帧毫秒预算且至少推进一步，版本号句柄防 ABA，Cancel/RunToCompletion，Step 内自取消安全。
      测试 +17（含两个零分配断言）。TCP helper 的自有主线程队列**保留**：其"连接回调先于首包派发"的顺序
      约束依赖自有队列，迁到全局派发器会破坏该保证——留 WS2 网络重构时按包体所有权一并处理。
- [x] **WS2-M1 请求调度**（2026-09-21）：ResourceManager 内所有加载（旧回调式 + 句柄式）统一经池化 `LoadRequest` 进入：
      `MaxConcurrentRequests`（默认 16）内同步派发（行为不变），超出按 `BinaryHeap` 优先级（高者先、同级 FIFO）排队，
      完成一个补派一个；排队中取消零 IO；`IAssetLoadHandle.SetPriority` 在队内重排；共享回调 + userData 携带请求
      → 每次加载不再分配闭包；同步完成 loader 下 5000 深队列无递归；Shutdown 排队句柄 → Cancelled、回调 → NotReady。
      新增 `BinaryHeap<T>`（Core 根，零分配，DecreaseKey/IncreaseKey/RemoveAt）。测试 +17。
      未做：主线程完成回调的帧预算（loader 协程内派发，需 WS2-M2 一并改 loader）。
- [x] **WS2-M2 内存预算与淘汰**（2026-09-22）：`ResidentBudget`（Core，纯 C#）——bundle 引用归零后进入温缓存而非立即卸载，
      常驻超预算才按 LRU 淘汰（开放世界来回走动时"刚离开的区块"直接命中）；主动收缩 `TrimResident(targetBytes)`；
      "超预算且全在引用中"压力状态告警一次 + 计数器。AssetBundleLoader 薄接入（OnLoaded/OnReferenced/OnUnreferenced/
      OnUnloaded 四个钩子），预算 0 时行为与原延迟卸载完全一致；ResourceComponent Inspector `m_MemoryBudgetMB` +
      `MemoryBudgetBytes/ResidentBytes/TrimResident`；Debugger 资源页显示常驻/预算。测试 +8（含零分配）。
      大小来源是 manifest 的 BundleInfo.Size（磁盘大小，非解压后内存）——精确内存需 WS4 接 Profiler 采样校准。
- [x] **WS3-M1 SpatialGrid（Core）**（2026-09-22）：`Core/Spatial/SpatialGrid`——XZ 平面空间哈希，槽位数组 + 每 cell
      侵入式链表（不再为每个 cell 分配 HashSet），插入/移动/删除 O(1)；圆形/矩形/cell 半径/最近邻（环扩张 + 早停）/
      回调式（ISpatialVisitor 可早停）查询全部零分配。AoiGrid 改为其薄包装（API 不变，暴露 `Grid`），删除 CellCoord。
      **实测坑**：Mono 以更高精度求值 float 乘法，`v * (1/cellSize)` 会把恰在边界的坐标 floor 到相邻 cell，
      必须用除法；已固化为回归测试。测试 +11，AOI/Units/Targeting 相关 228/228。四叉树未做（网格已满足流送/索敌，
      非均匀密度场景再补）。
- [x] **WS3-M2 WorldPartition + Streaming 重写**（2026-09-22）：`IWorldStreamingManager` 全新契约（旧 chunk/string/事件模型无消费者，整体替换）——
      整数单元 (layer, cx, cz)、多观察者并集、代数戳差分、BinaryHeap 最近优先 + 层偏置、每次评估重排、
      启动/在途/卸载三预算、滞回、加载中取消 + 迟到结果转卸载、失败重试、槽位版本号、浮动原点、
      `RequireLoaded` 同步兜底、`IWorldStreamingHandler` 单接收者零分配。评估只枚举半径内 cell 范围（与世界规模无关），
      1681 单元巡逻零分配断言。内置 `SceneStreamingHandler`（每单元一个附加场景）；`WorldStreamingComponent`
      （Inspector 预算 + 观察者 Transform 绑定 + 原点桥接）；Debugger Streaming 页；Scene 视图可视化改为 cell 模型。
      文档 `docs/WorldStreaming.md`。测试：WorldStreamingTests 16 + SceneStreamingHandlerTests 7；全量 2351/2352 + PlayMode 39/39。
- [x] **WS3-M3 流式世界存档 + 种群管理 + 流式 navmesh**（2026-09-22）：
      `IWorldStreamingNotifier` 回报链抽象（业务 handler → 装饰器 → 管理器）；`WorldStateStore`（按 (layer,cx,cz) 的字节记录 +
      全局段，BufferPool 承载、脏标记、带魔数/版本的事务性序列化、`WorldStateSaveData` 接 Save）；
      `PersistentStreamingHandler`（卸载前 Capture / 加载成功后 Restore，取消的迟到结果不恢复）；
      `INavigationManager.AddNavMeshTile/RemoveNavMeshTile` + `UnityNavMeshHelper` 接 `NavMesh.AddNavMeshData`，
      `NavMeshStreamingHandler` 按单元加载/卸载 navmesh 资产；`PopulationManager`（GamePlay：生成点/预算/击杀复活/
      卸载期间时间冻结/按局部序号持久化）+ `PopulationStreamingHandler`。ByteBuffer 补 ReadRawBytes/Skip。
      测试：WorldStreamingDecoratorTests 9 + PopulationManagerTests 10（含整链"击杀后离开再回来不复活"）。
- [x] **WS4-M1 性能遥测采样与上报**（2026-09-23）：`Core/Telemetry`——`ITelemetryManager`（会话级可播种采样、
      定长 `TelemetryRecord`（8 float + 8 int）零分配攒批、BatchSize/FlushInterval 封批、带魔数/版本的二进制批次、
      单批在途、后端回调任意线程→主线程结算、失败重排队、离线队列上限丢最旧、后端抛异常视为失败）；
      `TelemetryComponent`（帧时间窗口 avg/p95/p99/max/spike/分级、Profiler 内存、电量、加载耗时、自定义 Kind≥1000、
      默认 `FileTelemetryBackend` 落盘 `persistentDataPath/Telemetry/*.ejtm` 并限文件数）；`GameEntry.Telemetry`。
      测试：TelemetryManagerTests 10（含 Record 零分配断言）+ FileTelemetryBackendTests 3。同时所有测试的临时文件
      改到工程内 `Temp/EjoyTests`（`TestTempPaths`），不再写系统临时目录。全量 2383/2384 + PlayMode 39/39。
- [x] **WS4-M2 崩溃/ANR/日志采集**（2026-09-23）：`ICrashManager`（Core/Diagnostics，同时是 ILogSink）——
      `CrashFingerprint`（异常短类型名 + 前 12 帧，去掉行号 / IL 偏移 / "(at …)"，Unity 与 Mono 两种栈格式同指纹；无栈时去数字消息）
      会话内去重计次 + 每会话上限；面包屑环（日志 + 业务）；阶段 / 用户键；ANR 看门狗线程（心跳原子写，检测即落盘、恢复补记时长，
      Suspend/Resume 与前后台免判）；会话标记（阶段 / 前后台 / 低内存 / 卡死 / 已报致命 + 面包屑，原子替换写）→ 下次启动分类为
      AbnormalExit / BackgroundKill / LowMemoryKill / HangKill；`EJCR` 二进制报告（损坏检测）、盘上上限删最旧、`ICrashUploader`
      单个在途上传（成功删、连续失败本会话停）；每份报告写 `TelemetryKind.Crash` 遥测。`DiagnosticsComponent` 接线
      （persistentDataPath/Crash、lowMemory、OnApplicationPause/Quit、未处理异常 fatal 升级、编辑器默认关 ANR），
      `AppSession` 让遥测与崩溃共用会话 id。测试：CrashManagerTests 24（含真实看门狗线程与 4 线程并发上报）；全量 2407/2408 + PlayMode 39/39。
- [x] **WS4-M3 设备分级/自动画质/覆盖层**（2026-09-23）：`Core/Quality`——`DeviceTierClassifier`（覆盖规则先写先得 →
      硬件加权评分（移动 / 桌面分别参考值与阈值）→ 上限规则，规则文本带行号报错）；`IQualityManager`（五档、每档旋钮表、
      上限 = min(玩家档位或设备档位, 热上限)、应用器、Tier / QualityChange 遥测）；`AutoQualityController`（先动态分辨率后换档、
      降快升慢、升档失败冷却翻倍、卡顿帧忽略、零分配）；`IWorldStreamingManager.RadiusScale` + `StreamingQualityApplier`。
      Unity：`QualityComponent`（SystemInfo 分级、FrameTimingManager 工作耗时喂自动画质、PlayerPrefs 持久化玩家档位、
      与 PerformanceComponent 旧自适应互斥警告）、`UnityQualityApplier`；`PerfOverlayComponent`（自建 UGUI 画布、内置字体、
      `OverlayTextBuffer` + `OverlayTextGraphic` 零分配文字、`FrameGraphGraphic` 帧时间柱状图、F3 / 三指开关）。
      借鉴 FPSSample `DebugOverlay`/`GameStatistics` 的"字符格 + 数值零分配写入 + 帧图"思路，按框架风格改用 UGUI 实现、无需自带着色器与字库贴图。
      文档 `docs/LiveOps.md`。测试：DeviceTierAndAutoQualityTests 18 + QualityManagerTests 12 + PerfOverlayPlayModeTests 1；
      全量 2437/2438 + PlayMode 40/40。
- [x] **WS4-M4 基准基建**（2026-09-23）：`Core/Benchmarking`——`BenchmarkRunner`（预热、每样本操作数自适应到最短时长、
      多样本统计：最小 / 中位 / 均值 / P95 / 标准差、分配在计时外单独测）、`IAllocationProbe`、`BenchmarkReport`
      （带环境头的制表符文本，可解析 / 可 diff）、`BenchmarkComparison`（抗噪：变慢需中位比超阈值且最快样本仍慢于基线中位；
      分配增加即回归）、`PerfCapture`（0.1ms 桶直方图分位、卡顿计数、内存峰值、标记，零分配）。Unity：`UnityAllocationProbe`
      （Profiler GC.Alloc 事件，Boehm 下有效，已用已知分配自校准）、`FrameWorkTimeSampler`（QualityComponent 共用）、
      `PerfCaptureComponent`（时长自动停、写报告、PerfCapture 遥测、采集期间暂停自动画质并标记崩溃阶段）。
      `FrameworkBenchmarks` 9 项热路径（全部实测 0 分配并硬断言）+ `Tools~/Benchmarks/RunBenchmarks.ps1`（结果写工程内
      `TestResults/Benchmarks`，基线保存与对比）。测试：BenchmarkInfraTests 9 + FrameworkBenchmarks 9 + PerfCapturePlayModeTests 1；
      全量 2455/2456 + PlayMode 41/41。**WS4 完成。**
- [x] **WS5-M1 零分配格式化/StringHash/Span 解析**（2026-09-25）：`Core/Text/`——`TempText.AppendFormat/TryAppendFormat`
      （`string.Format` 语法、与 InvariantCulture 输出逐字一致、值类型不装箱；非法格式串回滚，前者抛 FrameworkException、后者返回 false）、
      `TextFormatter`（静态泛型缓存分派 + 可注册 `ITextFormatter<T>`、枚举名字表缓存）、`TextFormat`（紧凑数字截断不进位、时长四式、
      百分比 <1 绝不显示 100% 且无 "-0%"）、`NumberStrings`、`StringHash`（FNV-1a-32 over UTF-8，与服务端 / StableHash 同值，0 保留）+
      `StringHashRegistry`（开发版同哈希不同名即报错）、`StringMap<T>`（string / span / 哈希三种查找，uniqueHashes 模式）、`TextSpans`
      （切行 / 切列与 Split 同语义同行号）、`SpanParse`。消费方：Config（StringMap + StringHash 重载）、Localization（AppendFormat /
      AppendPlural，俄语复数 int.MinValue 溢出修复）、DataTable（`ISpanDataRow` + 生成器切片解析，坏行带行号跳过而非中断整表）、
      DataNode、RedDot、BindableText（泛型 SetValue）、ConfigBlobHash / StableHash 改为委托 StringHash。Unity：`UnityTextFormatters`；
      编辑器：`StringHashValidator` 菜单扫描字面量碰撞。文档 `docs/Text.md`。基准 +4（全部硬断言 0 分配）。
      **坑**：基准 sink 不能对同一值反复异或——偶数次归零，断言随采样次数奇偶随机失败，改为计数。测试 +89；全量 2544/2545 + PlayMode 41/41。
- [x] **R1 目录重构：取消与层同义的 `Base` 目录**（2026-09-25）：`Runtime/Core/Base`、`Runtime/Core.Unity/Base`、
      `Editor/Core.Unity.Editor/Base` 的 130 个条目上移到层根（git 重命名，`.meta` GUID 全部保留，仅删 3 个无引用的文件夹 `.meta`）；
      两个编辑器类型命名空间 `EjoyFramework.Core.Unity.Editor.Base` → 层根 `EjoyFramework.Core.Unity.Editor`（无外部引用、无序列化资产）。
      规则写入 `DirectoryConventions.md`。全量 2544/2545 + PlayMode 41/41，与搬迁前一致。
- [ ] **WS5-M2 Struct 事件与无 GC 集合**（RingBuffer/Deque/BitSet/SlotMap——M3/M4 的前置）：**代码完成（2026-09-28，Mac），Unity 验证后再勾选**。
      `Core/Collections/`——`RingBuffer<T>`（Grow / Overwrite / Reject；int 回绕安全的连续序号，按 tick 取历史，`DiscardBefore` 确认、
      `DiscardFrom` 回滚）、`Deque<T>`、`BitSet`（固定 / 可增长，O(1) Count，de Bruijn 位扫描，And / Or / Xor / AndNot / 超集匹配）、
      `SlotMap<T>` + `SlotHandle`（代际句柄，奇偶区分占用，代数用尽的槽位退役）；原有 BinaryHeap / NoAllocSort / GameLinkedList /
      MultiDictionary 移入（GUID 保留）。`Core/Event/EventChannel<T>`（struct 事件按 `in` 传递；所属线程契约；Fire 任意线程排队；
      派发中退订留墓碑；异常隔离）+ `IEventManager.GetChannel<T>()`（EventManager 每帧派发、关停统一关闭）。EventPool 线程契约落地：
      订阅表加锁，Update 单线程且不可重入（检查始终生效），关停后 Fire 回收参数、退订不抛，去掉 `Enum.HasFlag` 装箱。
      文档 `docs/Collections.md`。测试 +81（5 个零分配断言），基准 +7。
      验证：Core 用 dotnet 独立编译通过；97 个纯逻辑测试在 CoreCLR 上通过（含 EventPool / EventManager 原有测试回归）。
      **未做**：Unity EditMode / PlayMode 全量、Mono 下的零分配与基准数值。分支 `ws5/m2-collections`，未推送。
- [ ] **WS5-M3 ECS 深化**（用户 2026-09-24 指定重点：先分析 FPSSample，再按框架风格吸收；ECS 内核仍自研）：
      ① 按游戏循环阶段显式排序的系统组（服务端模拟 / 客户端预测 / 插值 / 表现，对应 FPSSample
      `ServerGameLoop`/`ClientGameLoop` 的显式 Update 顺序）；② 固定 tick 时间与 tick 驱动的系统；③ 结构变更只在同步点生效
      （对应 `GameWorld` 的延迟 Despawn，落到现有 CommandBuffer）；④ 复制组件契约（序列化 / 预测态校验 / 插值态），供 M4 使用；
      ⑤ 与 WorldStreaming 单元集成（按 cell 批量创建 / 卸载实体）；查询零分配。
- [ ] **WS5-M4 帧同步 Netcode**（用户指定重点；参考 FPSSample `Networking/`（62 个文件）与 `Game/Main` 游戏循环）：
      先对比现有 GamePlay/Netcode（已有快照 + 预测 + 插值，但全量快照、每连接一线程、Unreliable 假语义），分析阶段定案
      "确定性锁步"与"状态同步 + 预测回滚"的取舍（锁步依赖 P2-1 确定性随机 / 定点数）。候选吸收点：固定 tick 命令帧、
      按客户端已确认基线的快照差分（`DeltaWriter`/`NetworkSchema` 字段级差分与量化）、位打包 + 可选 Huffman 压缩模型、
      UDP 连接的序号 / 确认与真正的可靠 / 不可靠通道（`NetworkConnection`/`SequenceBuffer`）、预测回滚与重放校验
      （`NetworkPrediction`）、插值缓冲、延迟补偿状态历史、AOI 驱动下发。P2-2 并入本项。
- [ ] **WS5-M5 AI 感知/Utility/调试器**；**WS5-M6 战斗双轨合并 + Units 测试 + MVVM 嵌套 + JobScope**；
      **WS5-M7 交互/相机/移动动画框架**
- [ ] **WS6 P2 其余项（P2-2 已并入 WS5-M4）+ 每模块 README + 开放世界样例**

## 5. 验证命令（unity-cli；严禁路径超出项目，报告/日志放 `TestResults/unity-cli/`，不能放会被清空的 `Temp/`）

```powershell
$env:HTTP_PROXY="http://127.0.0.1:7897"; $env:HTTPS_PROXY=$env:HTTP_PROXY; $env:NO_PROXY="localhost,127.0.0.1"
& "C:\Program Files\Unity\unity-cli.exe" --no-banner --non-interactive --json test E:\1_code_new\EjoyGame `
  --mode EditMode --filter <Fixture> --output E:\1_code_new\EjoyGame\TestResults\unity-cli\<name>.xml --timeout 1500 `
  -- -nographics -logFile E:\1_code_new\EjoyGame\TestResults\unity-cli\<name>.log
```
