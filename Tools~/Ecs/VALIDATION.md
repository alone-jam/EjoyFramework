# 自研 ECS 验证记录

本文区分当前待验证改动与历史基线。历史报告路径属于 Game 仓库，不作为本轮独立包测试结果；迁移记录及 unity-cli 命令见 [PackageMigration](../../docs/PackageMigration.md)。

## WS5-M3 当前验证状态（2026-10-03）

本轮在 Mac 上继续开发，**未进行编译或测试执行**。最初独立 .NET 检查因缺少还原资产（NETSDK1004）停在编译之前；之后按用户要求不安装/还原环境，先实现和静态审查。下方旧测试通过记录不适用于本轮改动。

- 已独立静态审查：时间/固定 tick、接口迁移、阶段循环、Unity 驱动、两阶段销毁、struct 查询访问器、容量预留与扩容失败发布顺序、组件类型索引、实体分区及分区键预留、流送桥及异常预算修复、schema/基线/插值存储、组件成员代数、逐 tick 采集与单/双组件精确恢复。
- 静态审查发现的示例 double→float 溢出、组件稠密数组扩容半发布及测试 `Is` 名称冲突已修复并复核；这不证明真实 OOM、线程竞争或 Unity 生命周期已运行正确。
- 分配断言：较早的时钟/系统组/循环/销毁测试已切换到 `EcsAllocationTests`，Unity 使用 GC.Alloc 约束，CoreCLR 使用线程分配计数器；精确预热被测委托，测量前重置计数，阳性对照应能观察已知分配。不得把 Boehm 上恒零的计数器当作零分配证据。
- 尚待运行：完整独立 ECS 套件、Unity EditMode/PlayMode 全量、Mono 分配断言、基准与 IL2CPP 真机验证、宿主游戏示例伴随迁移后的编译。
- 本地预测历史、显式只改值重放及封闭双组件纠错示例已编写、静态审查并集成；重放跨组缓冲命令泄漏也已修复复核。M2 源码和后续修复已合流到此开发 worktree。所有新增/合流内容仍待编译及运行验证，不以未执行的用例数推算覆盖率。
- 恢复运行验证时，报告放宿主项目内 `TestResults/unity-cli/`，以 JSON/XML 结果为准。该阶段的最新实现进度见 [路线图](../../docs/OpenWorldRoadmap.md)。

## 历史基线

日期：2026-09-05。Windows；独立运行时 .NET 8.0.19；内核编译目标 .NET Standard 2.1；Unity 6000.4.8f1。

## 实现范围

内核源码位于 `Packages/com.ejoy.framework/Runtime/EjoyFramework.Core/Ecs`。运行时没有 NuGet PackageReference，Unity asmdef 引用为空且禁止引擎引用。NUnit/Test SDK/Adapter 仅属于测试项目。

已实现多世界及版本实体句柄、分类型 Sparse Set、All/Any/None 查询、1～4 组件 ref 遍历、顺序命令缓冲及预留实体、稳定顺序的串行系统组、Unity 驱动和视图绑定。示例使用同一内核实现移动、命中、扣血和销毁。

## 初次实现验证（目录迁移前的历史记录）

以下报告保留原始程序集名称和路径，不作为目录迁移后的验证证据。迁移后的复验见后续独立记录。

| 检查 | 结果 | 证据位置（仓库根目录相对路径） |
|---|---|---|
| 独立 .NET 测试 | 32 通过，0 失败 | `Tools/Ecs/TestResults/Ecs.Tests.trx` |
| Unity EditMode | 27 通过，0 失败 | `Tools/Ecs/TestResults/EditModeFinal.xml` |
| Unity PlayMode | 3 通过，0 失败 | `Tools/Ecs/TestResults/PlayModeFinal.xml` |
| Unity 示例及适配层编译 | 0 错误，0 警告 | 独立编译检查直接引用上述 Unity 安装的 CoreModule/PhysicsModule 程序集（临时编译项目） |
| 独立源码审查 | 两路审查通过，发现的三个生命周期问题已修复 | 对应回归见下文 |

.NET 和 Unity 的内核用例有重叠，不能相加为独立覆盖数量。Unity PlayMode 验证手动/自动推进、禁用暂停、销毁释放、视图绑定/复用，以及被拒绝的关闭不破坏驱动状态。未声称已验证示例场景画面或设备构建。

三个问题均先验证失败，再修复通过：

- 缓存 Query 导致已释放世界仍保留组件存储数组：`DisposalTests.RetainedQuery_DoesNotRetainComponentStorageAfterWorldDisposal`。
- 系统误释放借用的 CommandBuffer，破坏后续执行：`SystemGroupTests.BorrowedCommandBuffer_CannotBeDisposedBySystemOrRetainedCaller`。
- 遍历期间拒绝 Shutdown 之前提前释放了系统组：`EcsWorldComponentTests.RejectedShutdown_DoesNotPartiallyDisposeDriver`。Unity 失败证据保存在 `Tools/Ecs/TestResults/ShutdownRed.xml`。

## 目录规范迁移后的复验

迁移将内核移入 `Core/Ecs`，Unity 适配并入 `Core.Unity`，测试并入既有 Core 测试程序集，示例归入游戏 Samples。ECS 的存储和执行算法未改动。

| 检查 | 结果 | 当前证据位置 |
|---|---|---|
| 重命名后的独立 .NET 项目 | 32 通过，0 失败 | `Tools/Ecs/TestResults/DirectoryMigration.trx` |
| `EjoyFramework.Tests` 中的 ECS EditMode 用例 | 27 通过，0 失败 | `Tools/Ecs/TestResults/DirectoryMigration.EditMode.xml` |
| `EjoyFramework.Tests.PlayMode` 中的 ECS 生命周期用例 | 3 通过，0 失败 | `Tools/Ecs/TestResults/DirectoryMigration.PlayMode.xml` |
| Unity 示例实际编译 | `EjoyGame.Samples.EcsDemo.dll` 已生成 | 迁移后的 Unity 编译日志及 `Library/ScriptAssemblies/` |
| Unity 资源标识 | 20 个保留的文件 `.meta` 和 5 个文件夹 `.meta` 哈希与迁移前相同 | `Tools/Ecs/TestResults/DirectoryMigration.before.json` |
| 独立迁移审查 | 程序集、命名空间、引用及 GUID 核对通过 | 重命名后的项目编译、格式检查及测试均通过 |

合并后移除了 3 个不再需要的 asmdef 及对应 metadata，原有脚本 GUID 保留。活动源码、项目文件及文档中未发现旧 ECS 根目录、旧 Unity 适配程序集或旧示例路径引用。既有 Jobs.Unity 等历史布局没有迁移。

## 微基准（初次实现时采集）

每组先预热 100 次，随后采集 200 次串行标量移动更新；位置结果同时校验。以下为本机一次运行，未隔离操作系统和其他进程负载。

| 实体数量 | 中位数 ms | P95 ms | 测量区间当前线程托管分配 |
|---:|---:|---:|---:|
| 1,000 | 0.0176 | 0.0219 | 0 B |
| 10,000 | 0.0697 | 0.0714 | 0 B |
| 100,000 | 0.5999 | 0.7857 | 0 B |

不包含初始化、结构变更、渲染、物理、Unity Mono/IL2CPP 或移动设备。数据不能作为整场战斗耗时、硬性性能预算或与其他 ECS 的性能排名。

## 复现

在仓库根目录：

```powershell
rtk dotnet test Tools~/Ecs/EjoyFramework.Core.Ecs.Tests.csproj --configuration Release
rtk dotnet run --project Tools~/Ecs/EjoyFramework.Core.Ecs.Benchmark.csproj --configuration Release
```

Unity Test Runner 选择对应程序集及命名空间运行。批处理使用 `-batchmode -nographics -projectPath <本仓库绝对路径> -runTests -testPlatform EditMode -assemblyNames EjoyFramework.Tests -testFilter EjoyFramework.Tests.Ecs -testResults <结果路径> -logFile <日志路径>`；PlayMode 改用 `-testPlatform PlayMode -assemblyNames EjoyFramework.Tests.PlayMode -testFilter EjoyFramework.Tests.PlayMode.Ecs`。不要同时对同一个项目启动多个编辑器进程。
