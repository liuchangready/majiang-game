# R4B2 WIREFIX：完成未交付的 PUBLIC codec（明确授权执行）

原WIRE于15:46退出，CLI SUCCESS但response为空，没有delivery，也没有完成codec/测试。不是quota。保留所有部分实现，不重写已完成DTO。原任务TASK-ANTI-R4B2WIRE.md和FEEDBACK-R4B2WIRE.md仍是契约。本批只完成生产，测试由并行WIRETEST负责；两者绝不跨写。

## 独占范围

仅protocol/src/main：ApiMessage.kt、ProtocolPublicModels.kt以及必要的新SpectatorApi请求/绑定文件。禁止改protocol/src/test、所有其他模块、reviewer、build。只用文件工具，禁止RunCommand/shell/build/再委派。Codex负责测试；整个R4通过后暂停，不进R5/R6。

## 已实测证据与必须完成项

1. `R4B2-WIRE-first-review.log`真实独立12项中2过10失败。大多缺少实际ApiMessageCodec函数，补齐原任务冻结入口：encode/decodePublicSnapshot、encode/decodePublicUpdatePush、encode/decodePublicResendRequest。已有注册adapter复用，不另造Gson。异常统一JsonParseException。
2. 本批明确冻结分类入口`ApiMessageCodec.classifyPush(json: String): PushType`，使用已存在enum PLAYER/ROOM/PUBLIC。严格拒绝未知或非字符串type、非object/非法JSON；现有无type PLAYER仍正确归类。PUBLIC_UPDATE不能落入ROOM；明确旧isRoomUpdateEnvelope的后续替代路由，不破坏已有ROOM测试。PUBLIC PUSH与PUBLIC_RESEND完整格式见原任务。
3. 实测spectatorRequestsRejectMalformedIdentityAndMixedPlayerAuthorityFields失败：新ApiRequest五种tag需要各自必填/类型/非空检查，观战请求拒绝混入preferSeat/choice等玩家载荷；只收紧新增观战tag。不能丢失checkpoint或允许裸cursor恢复。
4. 实测spectatorBindingRoundTripRedactsTokenAndPublicResponsesRejectMixedPlayerPayload失败：ApiResponse构造及adapter两处hasPlayer遗漏旧顶层resumeToken。观战binding内token合法，旧纯PLAYER顶层token仍合法。
5. FEEDBACK其余内容继续适用，包括publicEvents standalone白名单和不变性、公开校验集合不可被外部修改、真实有限lifecycle/reason。原生产已编译通过，避免扩大改动。
6. `R4B2-WIRE-first-protocol.log`模块测试编译失败只因FakeServerConnection未实现六新方法，由并行WIRETEST负责，本批不修改fake/测试，不新增生产默认unsupported壳。

独立测试在tools/architecture/v4-review/V4PublicWireAcceptanceTest.kt，属于Codex只读。若测试暴露真实契约缺口修实现，不能删测试。生产完成后写delivery-ANTI-R4B2WIREFIX.md，给出精确入口与SERVER/CLIENT接线位置，明确未自行运行测试。末尾读取FEEDBACK-R4B2WIREFIX.md（若有）。不要只写计划或再留下未完成入口。
