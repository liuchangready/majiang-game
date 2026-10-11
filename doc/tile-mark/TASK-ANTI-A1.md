# Anti：A1 线路契约与编解码

E3/E3FIX1 已通过独立验收（落汗 37 项、engine 377 项）。本任务现在派发，Codex 的 10 项 `ProtocolTileMarkContractTest` 已移入 protocol 测试源集。

范围仅 protocol 生产源码。遵循 DESIGN.md，保留 V4 帧头、分片和可靠窗口。Codex 编写测试，Anti 只实现生产代码、运行测试、修复问题并回调；不得修改测试、Git 提交或启动其他代理。

## 固定接口

- `com.majiang.transport.api` 独立定义 `ProtocolTileMarkConfig(enabled=false)`、`ProtocolTileMarkRequest`、`ProtocolRiverPosition`、`ProtocolTileMarkArea`、`ProtocolMarkedTileView`、`ProtocolTileMarkGroupView`、`ProtocolTileMarkView`。字段与已验收 engine 契约逐一对应；牌面使用 `ProtocolTile`，不得引用 engine。
- 请求字段固定为 requestId/sessionId/roundId/decisionId/controlEpoch/tileIds。座位由认证绑定提供；客户端不得提交费用、到期值或目标牌所在位置。
- `ProtocolMatchConfig.tileMarkConfig` 默认关闭，开启时要求经济开启且 TILE_MARK 明确为 10。手写 copy/equals/hashCode/deepCopy 必须保留新增字段。
- `ProtocolAccessCapabilities.tileMarkSupported` 缺省 false；当前仅提供字段，服务宿主接线完成后才宣告 true。
- `ProtocolPlayerSnapshot.tileMark` 可空，缺省 null；公开快照、公开结果及观战接口不增加私有字段。
- `SessionApi.submitTileMark(request): CompletableFuture<ProtocolActionReceipt>`。为未接线实现提供明确失败的默认实现（failed future / UnsupportedOperationException），不得返回成功回执；后续宿主与客户端传输将覆盖该方法。
- `ApiTags.SUBMIT_TILE_MARK` 和末尾追加的 `ApiRequest.tileMark`。不改变旧位置参数的含义。

## 编解码与验证

所有新类型使用与现有严格 DTO 相同的构造验证和只读集合。Gson 边界不得绕过构造器约束。检查 API 已知标签、字段白名单、请求/响应/PUSH 适配器、拷贝与相等性、结构错误路径。信封 requestId 必须与 tileMark.requestId 一致；不得混入 choice/shufflePlan 等其他业务载荷，也不得在其他 API 中夹带 tileMark。观战请求不能携带落汗写操作。

整数只能接受实际整数 JSON 数字；拒绝小数、溢出和字符串数字。未知枚举及伪造的 ownerSeat/cost/expiry 字段拒绝。旧配置/能力/快照缺少新增字段仍能读为关闭/不支持/null。

DISCARD 必须有位置，位置座位与条目一致，索引非负；其他区域不能带牌河位置；隐藏区域不能带座位。快照级验证还须保证位置 roundId 与当前快照一致、索引在该座位当前牌河范围内且实体 ID 匹配。组恰好四张、组内及同一观察者组间不重复、总数最多十二，与 activeTileCount 一致。未知和过期缓存清理由后续客户端阶段验证。

## 交付

运行 Codex 的定向编解码用例及完整 `:protocol:test`；失败先修生产代码，再重跑至通过。如测试本身有编译或夹具错误，报告 Codex 修正，不能自行改测试。提交实际 XML 数字和文件清单到 `DELIVERY-ANTI-A1.md`、`CALLBACK-ANTI-A1.json`，更新进度，输出 `CODEX_CALLBACK A1 ready_for_review` 后退出。本阶段尚不宣称真实链路或 UI 已可用。
