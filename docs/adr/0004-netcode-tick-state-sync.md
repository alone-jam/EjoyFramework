# ADR 0004 — 权威状态同步与版本化 tick 命令协议

Status: Accepted direction; offline codec implemented and statically reviewed, runtime acceptance pending (2026-10-03)

## Context

框架目标是大型开放世界。流送客户端只持有相关子集，现有 Netcode 已采用服务端状态、客户端预测与纠正，但服务端当前按接收的命令推进处理、使用客户端提供的时长，tick 更多用于快照调度。现有 float[] 快照也不是 M3 的完整类型化基线。

FPSSample 的服务端固定 tick 后消费命令、客户端回滚后重放记录输入、按已确认基线选择差分等设计可借鉴。它的 Networking/NetworkPrediction 是快照字段压缩预测器，不能误当成玩法回滚协调器。

## Decision

**新协议方向默认采用服务端权威状态同步、本地预测重放和远端插值。** 该选择来自现有代码和开放世界子集需求，不是把“帧同步”一词直接解释为确定性锁步。全世界跨平台确定性尚无证据，因此锁步保留为独立论证的未来模式，不作为默认前提。

M3 提供时间、生命周期、schema 分类、值存储、恢复与有界重放机制。M4 负责网络会话/世界 epoch、远程实体身份、字段 codec、命令准入、快照确认与差分、AOI、权威验证和完整纠错范围。应用必须说明全部可变预测依赖，不能把双组件恢复看作世界回滚。

### M4 第一切片：离线协议基础

新增的限长 bit reader/writer 与 versioned tick-input frame codec 已实现并完成 C# / 安全静态审查，尚未运行验证；**不接入网络，不修改旧会话/消息 ID，不自动降级旧协议**。

- 字节序为 little-endian，字节内 bit 顺序为 LSB-first。
- 固定 32 字节头：magic `EJTI`；协议版本 ushort（1）；头长 ushort（32）；session epoch ulong；world epoch uint；command tick uint；命令 schema ID ushort；codec revision ushort；payload bit length uint。
- Epoch、tick、schema ID、revision 非零；tick 不回绕。命令 schema 与 ECS 组件 schema 是不同概念，不能从 float[] 长度或 CLR 布局推断。
- Payload 字节数必须精确等于 ceil(bitLength/8)，尾部未使用高位为零。读写双方受同一最大 payload 限制（默认 8192 bits，可配置上限 1048576 bits），拒绝超长、截断、多余字节、非规范 padding 和不匹配的版本/上下文。
- 使用调用方拥有的有界缓冲；全部校验完成才发布解码结果。解码 payload 借用输入内存，调用方必须遵守其生命周期。写入失败不污染目标，正确处理重叠输入/输出。
- 新头不携带客户端模拟时长。服务端固定 tick 准入和模拟是后续切片，旧接收驱动路径仍未因此升级。

## Compatibility and security

- 保留 INetcodeClient/INetcodeServer、已有消息编号和 float[] 所有权，后续通过显式 opt-in profile 接线。
- Epoch/schema 匹配不是认证；必须将认证会话与权限、实体代数、命令窗口、重复和速率限制绑定。Codec 仅提供结构验证。
- Packet 序号、命令 tick、快照身份与已处理命令确认分开，不能混用。
- 旧路径的每消息时长上限不等于每 tick 的服务端模拟预算；它仍需后续准入层改造，不能作为新默认安全保证。
- M3 最新基线不是 per-client acknowledged snapshot history。缺少差分基线必须完整重同步，不能猜测状态。
- TCP 仍提供可靠有序语义；真正 unreliable、拥塞控制和 UDP 生命周期稍后单独实现。

## Next slices

1. 有界命令准入与服务端拥有的固定 tick 模拟。
2. 显式字段 codec / 完整快照和兼容性协商。
3. 保留并确认的基线、快照差分与 AOI 下发。
4. 完整预测范围的纠错接线、真实可靠/不可靠通道、安全/拥塞控制和延迟补偿。

本记录接受的是实施方向，不宣称协议已上线、旧会话已迁移或测试已通过。当前开发仍按约定延后编译、测试和 Unity/IL2CPP 验证。
