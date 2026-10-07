# R4B2 并行 A：私有玩家展示预览端到端

R4B1已由Codex接受，证据ACCEPTANCE-R4.md最新节。现在明确启动本任务。总要求TASK-ANTI-R4B2.md和CONTRACT.md仍适用；另一个Anti只新增engine/projection的PUBLIC投影文件，不改本范围。Anti文件工具开发，Codex测试；禁止shell/build/提交/再委派/reviewer改动。额度耗尽自动转GLM。

## 独占范围

engine新增access/PlayerPresentationPreview.kt、session/PlayerPresentationPreviewProvider.kt（或同用途明确命名文件），并修改access/PlayerSnapshot.kt、authority/TableAuthority.kt接入；尽量不改原TenpaiPreviewProvider/RiichiPreviewProvider/MeldChoiceProvider已验证算法。protocol新增transport/api/ProtocolPreviewModels.kt，修改ProtocolModels.kt玩家快照和ApiMessage.kt真实V4 codec；server/mapping/EngineProtocolMapper.kt；client/api新增PreviewModels.kt，修改ViewModels.kt（以及实际包含GameSnapshotView的现有模型文件）、ProtocolViewMapper.kt、必要旧EngineDtoMapper仅最小消费者适配。对应模块测试。不得改engine/projection、server房间/宿主/绑定、client runtime/UDP、presentation、启动器、reviewer或构建文件。范围确需扩大先报告，不能跨写。

## 必须交付的行为

1. 从同一次TableAuthority.querySnapshot产生的真实PlayerView与PlayerDecision生成展示数据。复用TenpaiPreviewProvider.waits、RiichiPreviewProvider.options、MeldChoiceProvider.options，不复制和牌/振听/鸣牌算法，不在client计算合法性。
2. 新私有预览DTO绑定roundId、decisionId、controlEpoch。包含待牌、立直弃牌方案及waits/furiten、鸣牌各组(chi/pon/open-kan/closed-kan/added-kan)的精确物理tiles/handTiles/calledTile/addedTile/fromPlayer/type。每个可提交预览必须带当前decision.options中真实optionId；按完整GameAction等值匹配，不能按牌种或动作名猜ID，不能失去赤五/同牌种不同物理副本。
3. 领域、protocol、client是三套独立类型。推荐字段名snapshot.presentationPreview，领域PlayerPresentationPreview、ProtocolPlayerPresentationPreview、client.PlayerPresentationPreviewView。外层nullable允许无有效局/旧fixture，但权威快照有当前局时必须携带一致预览；决策无候选时不能杜撰可提交动作。回合/代次不一致的wire预览必须拒绝。
4. snapshot/deepCopy/copy/equals/hashCode、PUSH与RESUME/full recovery、Gson adapter都保持完整。严格解码不得绕过构造器校验，嵌套list构造与getter均不可变，不能用Any/Map透传领域对象。不为旧入口添加兼容壳。
5. 准备/终局/非等待形不显示过期待牌；auto-control等情况下只显示真实可用候选。预览计算由权威队列里的snapshot投影执行，不能在UDP接收/UI调用线程同步计算。
6. 对照SettlementDetailRenderer核对已有client评分DTO完整性，若已齐全写证据，不复制新账本。记录R5将如何替换GameScreen/RiichiSelectionModel/MeldSelectionModel的engine provider依赖；本任务不改展示消费者。

## 模块证据

使用原provider的确定性fixture比较：13张等待形/摸牌前等待、赤五与普通五物理弃牌、立直方案waits和振听、chi/pon/各类kan选择、optionId一致及拒绝不匹配元数据、codec往返、不可变集合。至少一个通过真实TableAuthority生成->server mapper->codec->client mapper的链路用例，不能只手工造相同DTO自证。

交付delivery-ANTI-R4B2PREVIEW.md，列实际文件、最终DTO字段、验证建议与R5消费说明，明确自己没运行测试；结束前查FEEDBACK-R4B2PREVIEW.md。不扩到观战网络/R5/R6。
