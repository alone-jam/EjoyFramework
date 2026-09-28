# EjoyFramework 功能与实现分析（2026-08-10）

> 由三路并行代码审读汇总：Core 层 / Core.Unity 层 / GamePlay 层。
> 框架位置：`Packages/com.ejoy.framework/`，约 4 万+ 行 C#。

## 0. 总体架构

生产程序集按平台边界严格分层，依赖单向：

```
EjoyFramework.Core            纯 C#（noEngineReferences），35 个模块 + Base 基建
EjoyFramework.Core.Blobs      纯 C# ConfigBlob 容器、零分配读取与平台无关数据源
EjoyFramework.Core.Unity      Unity 适配：~35 个 XxxComponent + MVVM Binder + 启动引导
EjoyFramework.Core.Jobs.Unity Unity Job System 适配（IJobSchedulerManager + JobScope + Burst 工具）
EjoyFramework.GamePlay(.*)    纯 C# 玩法层（Core/Combat/Units/Netcode/LiveOps 5 个子 asmdef）
EjoyFramework.GamePlay.Unity  玩法 Unity 驱动壳（18 个 MonoBehaviour）
```

- 入口：`Framework.GetModule<IXxxManager>()`（旧称 EjoyFrameworkEntry）。
- 一个 `EjoyFramework.prefab` 拖入即完成全部装配（19 组件 + 8 层 UI Canvas）。
- **零反射启动链**：模块工厂 / Helper 工厂 / MVVM PropertyAccessor 三张表全部 codegen
  （`Generated/*.g.cs`），反射兜底已删除，未登记直接抛异常（IL2CPP 裁剪安全）。

## 1. Core 层

### 1.1 模块系统（Base/Framework.cs）

- 静态工厂注册（`FrameworkModuleRegistrations.g.cs`，35 个接口→new）。
- `GetModule<T>` per-T 静态缓存，热路径 O(1) 无锁；首次创建 lock + 双重检查。
- 循环依赖检测（创建链跟踪）；优先级降序 Update / 倒序 Shutdown；
  版本化快照遍历允许 Update 中懒加载模块；逐模块 try/catch 隔离。
- 优先级：Coroutine 100 > Event 90 > Timer 70 > Performance 60 > ObjectPool 50
  > Resource 20 > Fsm 10 > Scene/Streaming 5 > 业务 0 > Procedure −100。
- `EnsureMainThread` 为 Editor/Dev 条件编译；`ResetForEnterPlayMode` 处理关闭 Domain Reload。

### 1.2 基建（Base/）

| 组件 | 要点 |
|---|---|
| ReferencePool | 每类型 Queue + 反射-free 泛型工厂；Interlocked 统计；MaxCapacity 防无界；严格模式 O(1) 重复 Release 检测 |
| EventPool<T> | 双队列 swap 锁外分发；handler 快照迭代；ThreadStatic scratch 零分配；Fire 移交 args 所有权自动 Release；订阅表加锁、Update 单线程不可重入（WS5-M2） |
| FrameworkLog | ILogHelper 注入；1/2/3 参重载避免 params 装箱；Debug 级 Conditional 编译期裁剪；日志路径永不崩溃 |
| Utility | Text/Json 注入式 helper；Timestamp 基于 Stopwatch 单调时钟 |
| Variable | 引用池化的黑板值容器（14 个别名），FSM 数据用 |
| DataStruct | TypeNamePair 复合键；GameLinkedList 节点池 + 安全遍历删除；MultiDictionary。均非线程安全 |
| Serialization | ByteBuffer：小端、零装箱、单缓冲双游标、IReference 池化，codegen 序列化底座 |

### 1.3 模块清单（摘要）

- 基础调度：Event / Fsm / Procedure（复用 FSM）/ ObjectPool / Coroutine / Timer
- 资源内容：Resource（Loader 注入，自身无 IO）/ Entity / UI（SerialId+实例池+加载取消，UIStack/UIController）
  / Config / DataTable / Localization（共享 AssetLoadCoordinator）/ Scene / Sound / Video / Streaming / Navigation / Input
- 网络数据：Network（TCP+心跳+纯函数退避）/ Rpc / Http（重试纯函数、非幂等不重试）
  / Download（多 agent 断点续传）/ Save（HMAC-SHA256 framing + AES + 版本迁移）/ DataNode
- 热更发布：HotUpdate（方法级，iFix）/ Patch（资源热更：.tmp→MD5→File.Replace 原子替换）
- 安全运维：Security（SecureInt/防重放/CRC32+HMAC 恒时比较）/ RedDot（聚合树，仅真变更通知）
  / Analytics / Purchase / Notification / RemoteConfig（统一 Null Object 后端）
  / Diagnostics（多 Sink+防日志递归）/ Performance（防振荡分级）/ ServerTime（单调钟+服务器锚点）/ Setting
- AI：行为树 + 黑板（非模块）

## 2. Core.Unity 层

- 启动：`FrameworkModuleBootstrap`（BeforeSceneLoad 代打注册）→ `BaseComponent`
  （ExecutionOrder −10000，唯一驱动 Framework.Update；主线程打点、lowMemory 释放池、
  s_Quitting 门控防第二次 Play 误 Shutdown）。
- 桥接模式统一：Awake 取模块+订事件，Start 注入 Helper，API 纯转发。
  代表：ResourceComponent（路径+Loader+manifest 协程初始化）、UIComponent（8 组 Canvas + UIFormDef 注册 + UIController）、
  DebuggerComponent（F1 IMGUI，静态 GUIContent、ConcurrentQueue 收线程日志）。
- MVVM：Core 侧 BindableObject/ObservableList/RelayCommand/BindingPath/PropertyAccessor
  （codegen 委托表，Player 零反射）；Unity 侧 15 个 Binder + MvvmView<TVM> + 脚手架
  （`B_属性_控件` 命名自动接线）。BinderBase 刻意只在 OnDestroy 解绑（兼容 OnPause SetActive(false)）。
- Core.Jobs："早调度晚完成"（LateUpdate 统一 CompleteAll）、JobScope 作用域容器、Burst Fill/Copy。

## 3. GamePlay 层（31 系统）

- 战斗：GAS 风格 AbilitySystem（per-owner，Instant/Duration/Periodic + 授予标签引用计数）；
  BuffSystem/SkillSystem（全局模块）；回合制 BattleEngine（事件链上限 1000）；AttributeSet；
  UnitWorld（阵营桶 + AOI 网格 + 索敌；"可选生命"设计复用机关物件）。
- Netcode（~5000 行）：Transport 抽象（TCP 长度前缀，后台线程收发主线程 Poll 派发）、
  服务器权威 + 客户端预测回滚、快照插值、AOI 双缓冲差分。
- 世界内容：Worldmap / Atmosphere（昼夜+天气状态机）/ Dialogue / Guide / Tweening /
  Music（交叉淡变+闪避）/ TouchInput（手势+摇杆，新旧 InputSystem 切换）/ Pathfinding。
- LiveOps（独立 asmdef，与战斗解耦）：成就 / 邮件（注入时间源、领取幂等）/ Quest /
  Activities / Leaderboard / Guild/Party / Chat / Matchmaking / CloudSave / A/B 实验（稳定哈希分桶）。

## 4. 亮点

1. 分层纪律极严：纯 C# 程序集全部 noEngineReferences，引擎能力一律 Helper 注入 + 首帧配置自检。
2. 六条 codegen 线（模块工厂/EventArgs/DeepCopy/PacketCodec/二进制序列化/MVVM Accessor）
   统一服务于 IL2CPP 裁剪安全 + 零 GC。
3. 快照迭代、重入防护、异常隔离、Null Object 后端等模式全库写法一致；注释可当设计文档读。
4. 纯函数抽离（ReconnectBackoff/HttpRetryPolicy/ManifestComparer/SaveMigrator/PluralRules）便于单测。

## 5. 隐患清单（按影响排序）

1. **MVVM 嵌套路径不刷新**：BinderBase 只比对根段，`User.Profile.Name` 的 Name 变化不触发刷新，
   与注释声称的"冒泡"不符（BindableObject 无冒泡机制）。
2. **Player 构建静默失败面**：PropertyAccessor / GeneratedHelperFactory 未登记返回 null 无日志，
   Editor 反射兜底掩盖问题，只在真机暴露。
3. **战斗双轨重复**：AbilitySystem（per-owner）与 BuffSystem/SkillSystem（全局模块）语义重叠，易双算。
4. **线程契约靠约定**：EventPool 订阅表无锁但 Fire 承诺跨线程；EnsureMainThread Release 下编译期消除。
   → WS5-M2 已处理：订阅表加锁，Update 同一时刻只允许一个线程且不可重入（检查始终生效），关停后 Fire 回收参数、退订不抛（见 `docs/Collections.md`）。
5. **Netcode 扩展性**：每连接一线程；全量快照广播（AOI 未接入下发）；权威状态为无 schema 的 float[]；
   NetDeliveryMethod 的 Unreliable 语义是假的（全按可靠有序）。
6. JobScope.CompleteOnDispose 单句柄字段，重复调用覆盖前句柄 → 容器提前释放风险；
   Jobs CompleteAll 完全依赖 BaseComponent 存活。
8. UnitWorld 阵营/空间需手动同步（改 FactionId 不重分桶、移动需显式 SyncSpatial），忘调无报错。
9. GamePlay 根下 11 个空占位目录（代码已迁 Combat/LiveOps）待清理；PatchManager 头注释已过期（仍提 Activator）。
10. Units（3000+ 行）缺测试；MvvmView binder 索引一次性，运行时动态挂的 binder 不会被 Bind；
    Framework.EnsureSnapshot 尾部槽位强引用滞留；EventPool.ClearAllHandlers 不逐个通知（WS5-M2：关停时逐个事件记录未退订的处理器，晚到的退订不再抛出）；
    Blackboard.Get 类型不匹配静默 default；AntiCheat/SecureInt 仅防休闲作弊。

## 6. 基建缺口（高性能 / 零 GC 方向）

现有基建盘点：ReferencePool（IReference 类池）、EventPool、GameLinkedList（节点池）、
ByteBuffer、FrameworkLog（免装箱重载）、Jobs 封装。缺口按优先级：

> **实施进展（2026-08-10，四线并行开发 + 交叉审查，全部 PASS）**
> - `Core/Text/`（R1 前为 `Core/Base/Text/`）：**TempText**（readonly ref struct 构建器 + [ThreadStatic] 分级 CharBufferPool，
>   版本号校验常编译，稳态实测 0 分配，26 用例）；**MutableString**（unsafe 可变 string，
>   `EJOY_UNSAFE_STRING && (ENABLE_MONO || ENABLE_IL2CPP)` 双重闸门——压缩式 GC（CoreCLR）下改写
>   length 会崩堆已实测确认，仅 Unity Boehm 获准启用，CoreCLR/未定义时自动退化为分配实现）。
> - `Core/Resource/`：**AssetBatch**（批量预载、Pin 引用计数缓存、同步 GetCachedAsset、
>   readonly struct 版本号轮询句柄、零闭包回调；三轮审查关闭 3C/2H，Pin↔Unload 严格 1:1，
>   31 用例含引用池收支断言）+ ResourceComponent 转发。定案不做 awaiter（async 状态机挂起必装箱）。
> - `UI/Mvvm`：**BindableText**（char[]+TryFormat 免 string 通知）+ TMPTextBinder 走
>   `TMP_Text.SetCharArray` 直通；BinderBase 新增 SupportsSourceWrites 归一化（Bind 订阅前纠正
>   误配的 TwoWay）；PropertyAccessor 玩家构建未登记成员改为告警（原静默 null）。
> - **待人工闭环**：在 Unity Editor Test Runner 全量跑一次新测试（尤其两个 AllocatingGCMemory
>   断言——Mono 的 TryFormat 分配行为无法在 CoreCLR 侧代证）；VM 新增 BindableText 属性后须重跑
>   MVVM codegen。

| # | 缺口 | 说明 | 优先级 |
|---|---|---|---|
| 1 | CollectionPool | List/Dictionary/HashSet/StringBuilder 的静态池（Get/Release + using 作用域），当前只能池化 IReference 类 | P0 |
| 2 | BufferPool（byte[] 租借） | 按 2 的幂分桶的 ArrayPool；Network/Download/Save/ByteBuffer 路径当前各自 new byte[] | P0 |
| 3 | 零分配字符串格式化 | Utility.Text 仍走 string.Format 产生分配；需 ValueStringBuilder / 缓存式 number→string（UI 数字高频）| P0 |
| 4 | Struct 泛型事件通道 | EventPool 基于 class BaseEventArgs（池化但仍是引用+虚调）；补 `Event<T> where T:struct` 无装箱直发通道用于每帧高频事件 → WS5-M2：`EventChannel<T>` | P1 |
| 5 | 无 GC 集合 | RingBuffer/Deque、二叉堆 PriorityQueue（Timer/AI 调度用）、BitSet、SlotMap/SparseSet（实体 id 索引）→ WS5-M2：`Core/Collections/`（PriorityQueue 即已有的 BinaryHeap；SparseSet 即 ECS 内核的 `ComponentPool<T>`，internal） | P1 |
| 6 | 零 GC 资源异步 | 定案（2026-08-10）：不做 awaiter/Task（async 状态机挂起时必装箱）。采用 AssetBatch 批量预载 + Pin 缓存同步 Get<T>（玩法热路径无异步）+ struct 版本号句柄轮询（流程状态机）+ 缓存委托回调补充 | P0 |
| 7 | 字符串哈希 ID | FNV1a StringHash + 编辑期碰撞检查；RedDot/DataNode/Config 热路径当前用 string 键 | P1 |
| 8 | Span 化解析 | DataTable/CSV/Localization 解析走 string.Split；改 ReadOnlySpan<char> 切片 + span Parse 零分配 | P1 |
| 9 | 帧临时分配器 | FrameAllocator/scratch arena，每帧 Reset，供索敌/寻路等临时集合 | P2 |
| 10 | 时间切片调度 | 帧预算迭代执行器（毫秒预算跑 IEnumerator/委托队列），大批量加载/生成摊帧 | P2 |
| 11 | 主线程派发器 | 后台线程→主线程回调队列（Diagnostics/Network 已各自手写，应下沉为公共设施） | P2 |
| 12 | 确定性 Random/数学 | xoshiro 可播种 Random + 定点数（Netcode 预测回滚的确定性基础） | P2 |
| 13 | 分配监控 | 每模块 GC.Alloc 采样 + IProfilerHook 抽象（Core 无引擎依赖，Unity 层接 ProfilerMarker） | P2 |
