# R2 独立协议模型与客户端边界

仅在 R1 经 Codex 验收后执行。Anti 开发，只文件工具；Codex 构建、独立测试和验收。先读 CONTRACT、PLAN、R1 交付和当前源码声明。

## 授权范围

protocol 的 V4 契约/DTO/编解码及测试，server 的 V4 映射与 udp/inprocess 调用点，client/api 与 client/transport/udp 及对应模块测试；各模块 build.gradle 仅在本任务确实需要依赖调整时修改，不升级版本。不得改 tools/architecture/v4-review 或旧领域规则算法。

## 交付

1. 为 V4 定义不含 engine 类型的协议请求、回执、完整快照、决策、事件、结算、配置、码牌与控制 DTO。使用稳定基础编号/枚举，物理牌 ID 0..135；所有可见字段和隐私投影保真，集合防御性复制。不能复制 engine 算法或用 Any/原始 Map/Gson JsonElement 逃避类型隔离。
2. server 实现显式 EngineProtocolMapper：输入协议意图转换为领域请求，输出已脱敏领域投影转换为协议 DTO；协议编解码不 import engine。为事件/动作多态显式标记类型，严格字段/枚举/范围验证，不依赖 Gson 绕过 init 的不安全默认行为。
3. client 自有 MatchConfig/ShufflePlan/ActionChoice/Receipt/Capabilities 等输入输出视图；GameSession/LobbySession 公共方法及字段全部 client.api 类型或基础类型。将 EngineDtoMapper 改成 ProtocolViewMapper，不保留旧映射名的兼容壳。新增 claimControl/releaseControl，去除 V4 对外 takeover Boolean；server 边界继续正确翻译领域控制语义。
4. 同步修改 InProcessBinding、UdpRoom、UdpTableServer、UdpClientBinding 与 V4 模块测试，完整走同一协议接口。不能只创建未使用的新 DTO，让产品继续交换旧领域类型。
5. 136 张牌、红五、所有动作/事件、结算符番役种/宝牌/支付、历史/终局、控制状态、准备期私有数据有映射保真测试；序列化往返、非法输入、深拷贝、防 engine 泄漏测试齐全。

阶段约束：旧 GameClient/旧 TCP 及尚未迁移的展示可以暂时保留 engine 依赖，记入 R6 清理清单；不为它们新增兼容 API。新 V4 契约和新门面必须已无领域类型依赖。旧 TCP 共用的 ProtocolJson 可以暂留原处承载旧依赖，V4 不再使用其 engine adapters；protocol 模块最终 Gradle 脱离 engine 在 R6 同步删除旧编解码时强制完成。不要为短期编译新增一个长期 legacy 模块。

Codex 按新 API 接线迁移独立测试，保留 R1 行为断言后执行全部检查。交付 delivery-ANTI-R2.md：列准确公共类型/方法变更、源文件和暂留旧链依赖。未完成映射/调用点不算交付完成。

交付前读取 [FEEDBACK-R2.md](FEEDBACK-R2.md)：Codex 在开发期间发现的具体模型别名与映射保真问题，须一并修正。
