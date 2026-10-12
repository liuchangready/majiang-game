# Anti / agy：袖里藏花 E2 线路、宿主与客户端

状态：2026-10-12 正式下发；E1 已由 Codex 独立验收，见 ACCEPTANCE-E1.md。继续遵守 AGENTS.md、DESIGN.md 与 E1 的文件所有权、用户改动保护、禁止改测/删测/跳过/自行提交/委派代理规则。

## 范围

修改 protocol/src/main、server/src/main、client/src/main，必要的接口文档及本阶段进度/交付/回调文档。engine 只读，确有领域缺陷先报告 Codex 转 E1 返修。presentation 在本阶段不接线。

按设计完整接入：独立强类型 DTO、费用配置与能力集缺省、两类请求的严格 TypeAdapter/白名单/互斥矩阵、快照交叉校验、EngineProtocolMapper/ProtocolViewMapper、SessionApi/GameSession、同桌串行 ServerTableHost、SharedRoom、本地 InProcessBinding、UDP 分发与观战拒绝、客户端共用在途锁及绑定代次。选择成功无费用事件也必须推送完整私有快照，所有私有信息只发授权座位。

冻结名称按设计：ProtocolSleeveBloomConfig/DrawKind/Keep/Request/ChoiceRequest/OfferView/View；ApiTags.SUBMIT_SLEEVE_BLOOM / SUBMIT_SLEEVE_BLOOM_CHOICE；ApiRequest.sleeveBloom / sleeveBloomChoice；ProtocolPlayerSnapshot.sleeveBloom；ProtocolMatchConfig.sleeveBloomConfig；ProtocolAccessCapabilities.sleeveBloomSupported。构造参数顺序与 engine 对应类型一致（Tile 改用 ProtocolTile），客户端对应类型为 client.api 的独立类型。门面均提供 submitSleeveBloom 与 submitSleeveBloomChoice；ServerTableHost 采用 seat、领域请求、可选 sessionValidator，与 submitTileMark 同式。

Codex 已准备16项协议、宿主、客户端、本地及真实UDP闭环测试，见 TESTS-READY-E2.md；须先 E1 验收并正式下发 E2 才运行共享 Gradle。新方法造成旧测试 fake 必须调整时通知 Codex，不自行修改测试。

## 测试与回调

任务正式下发后开工写 PROGRESS-ANTI-E2.md，接口与编译问题及时记录。测试就绪后运行：

```powershell
.\gradlew.bat :protocol:test :server:test :client:test --console=plain
.\gradlew.bat test --console=plain
```

必须等待命令结束并检查 XML 真实数量、失败、错误、跳过。先修复生产问题，专项和完整相关模块通过后再提交检视；禁止以旧报告或跳过测试宣称通过。

完成写 DELIVERY-ANTI-E2.md 与 CALLBACK-ANTI-E2.json（taskId=SB-E2、recipient=codex、status=ready_for_review 或 blocked、modifiedFiles、tests、remainingIssues）。最后输出 CODEX_CALLBACK SB-E2 ready_for_review（或 blocked）并退出，由当前 CLI 结果回调当前 Codex，不递归启动 Codex。Codex 独立检视发现问题后，按返修单修复、重测、再次交付。
