# V5-N3 最终裁决台账独立复核报告（GLM 线程）

日期：2026-10-26
复核方：独立 GLM 线程（与 DeepSeek P2.2/P5.4a 及 Anti P4.1 在途线程互不读取、互不等待）
对象：`eb54476ec7bdc08ef570d94f3313a8f68f64b2c5`（分支 `wt/v5-n3-final-adjudication-review-glm-20261026`，工作树 clean，HEAD 与报告 commit 前一致）
基线（已接受 parent）：`7606c5c1daf2523f1bdb653e439fcc4f3f278cca`；`merge-base --is-ancestor` 双向核验通过（含 `a24b491` 为 HEAD 祖先）。
diff 范围：恰 3 文件——`doc/v5-重构/V5-G1-N3-CLOSEOUT-LEDGER.md`、`.tsv`、`tools/verify-v5-n3-closeout-ledger.ps1`。无越界文件。
caller 原始证据（只读引用）：`D:/majiang-wt/codex-n3-final-adjudication-20261026-1045/artifacts/TEMP/caller-final-adjudication/`。

## 结论（先行）

**候选范围内未发现任何缺陷（0 BLOCKER / 0 MAJOR / 0 MINOR）。本轮独立复核 PASS，供 caller 裁决。**
不因任何 PASS 直接认定运行时行为：本候选为台账+校验器改动，行为等价性仅引用已接受提交的证据链（Domain 517/517、TableRuntime 397/397、AI-discard 6/6 等），本轮未重跑重型运行时。

## 1. 六行裁决矩阵（pending→classified）

六行前 9 列身份（id/package/fixture/caseId/path/idx/kind/parent/histClass）与 parent 逐值一致；其余 586 行字节不变（diff 恰 6 对行）；411 条 bound 行 BOUND-IDENTICAL。

| # | 行（pkg\|fixture\|case\|path\|idx） | hist | 变更 | 裁决锚点 |
|---|---|---|---|---|
| 1 | N3-B1\|cheat-v1\|mv-swap-drawn-with-wall-backfill\|$.tileMovement[*].note\|1 | UNBOUND | pending→classified | 0955 决策：alias/materialisation 边界；canonical C# 唯一牌位、旧槽空闲；可达行为已锚定（discard29/seat1 draw33）；revisit=2026-11-30+触发条件；**非规则豁免、never full** |
| 2 | N3-B6\|ai-v1\|-\|$.v5Observation.mappingLimits[*]\|1 | UNBOUND | pending→classified | 伴随 fixture ai-discard-flags-v1（EV-B6F-DOC/SUB/ACC）+ PlayerView 字段边界保留；**冻结 ai-v1 仍空（`visibleDiscards: []`、`Assert.Empty` 在位）；classified 不升 bound/full** |
| 3 | N3-B6\|ai-v1\|-\|$.v5Observation.mappingLimits[*]\|2 | UNBOUND | pending→classified | 同上（isClaimed/isRiichi 旗语义维度） |
| 4 | N3-B8\|cheat-v1\|bm-activate-deadwall-empty\|$.beamSwapHandler[*].note\|1 | UNBOUND_HC | pending→classified | 空 filter ≠ 物理空墙；pointer 4 候选 14/9、13 实体拒绝为可达行为；**empty-wall 等价表述被明确拒绝** |
| 5 | N3-B8\|cheat-v1\|tm-capacity-tiles\|$.tileMarkHandler[*].note\|1 | UNBOUND_HC | pending→classified | 12+4 真实算术 + 8+4 成功对照；笔误保留且**不立错误规则** |
| 6 | N3-B8\|cheat-v1\|tm-decision-mismatch\|$.tileMarkHandler[*].note\|1 | UNBOUND_HC | pending→classified | null-round 构造拒绝与 no-open-decision 独立断言互不替代；**no-round 不可表述成 stale-round 等价** |

四条出千行与 `CODEX-V5-PROGRESS-20261026-0955.md` 四项明文裁决、`V5-N3-CHEAT-RESIDUAL-BOUNDARIES.md/.tsv`（proposal=pending、callerAcceptanceRequired=yes-representation-boundary）、`CODEX-V5-N3-CHEAT-RESIDUAL-ACCEPTANCE.md` 一一吻合；evidence 追加 `EV-RES-DOC;EV-RES-SUB;EV-RES-ACC;EV-RES-DEC`，revisit 均保留 2026-11-30+触发条件+owner（P1.4/P1.6 + caller）。B6 两行 evidence 追加 `EV-B6F-DOC;EV-B6F-SUB;EV-B6F-ACC`，csRead 如实标注 companion fixture only + 冻结 ai-v1 仍空。

分母闭合：14 包 592 = 411 bound + 181 classified + 0 pending（包向量 B1 18/6/0、B6 0/11/0、B8 108/83/0、其余 11 包 285/81/0；与校验器 `pkgVerdictWant`/`verdictTotalWant` pins 全等；旧向量 411/175/6 保留于 §14 历史）。

## 2. 校验器不弱化审计

`tools/verify-v5-n3-closeout-ledger.ps1`（562 行）diff 仅 5 组：`pkgVerdictWant`（B1 5→6/1→0、B6 9→11/2→0、B8 80→83/3→0）、`verdictTotalWant`（411/181/0）、新增 7 个 evidence pin（EV-RES-DOC=58170199…、EV-RES-SUB=64e4195a…、EV-RES-ACC=9e8a8b0d…、EV-RES-DEC=16470058…、EV-B6F-DOC=e1319ad2…、EV-B6F-SUB=9648e976…、EV-B6F-ACC=edc06cad…）、3 个 adjudication digest 更新（B1=df6b28a9…、B6=53a1f30e…、B8=20c47c09…，其余 11 包不变）、OK 文案 411/181/0。**原 37 registry pin 不变；全部 24 项 CHECK 算法、schema、ancestor、evidence、history、mandatory、blocked-set（四案：tm-capacity-tiles/tm-decision-mismatch/bm-activate-deadwall-empty/bm-activate-deadwall-dora-only）无一弱化。**

## 3. 运行时验证（fresh + 负例，独占 ignored artifacts/TEMP）

环境构建（全在 `artifacts/TEMP/glm-n3-final-review-20261026/mirror/`，不触碰真实工作树）：`git -c core.autocrlf=false -c core.eol=lf archive --format=tar eb54476 | tar -xf -` 得原始 blob-LF 镜像（抽样 4 文件与 `git cat-file blob` 哈希一致）；`.git` 指针文件供 `git cat-file -e`/`merge-base --is-ancestor`；7 份 R1 历史日志从 `D:/majiang-wt/codex-progress-20261026-1010/...` 只读复制到位。

**环境观察（非缺陷，两条）**：
1. 44 个 evidence pin 为混合 EOL 口径：25 pin = 工作树 CRLF 字节哈希（`*.cs text` 属性 + autocrlf=true 的 checkout 形态），19 pin = 原始 blob LF 字节哈希。这是既有轮次延续的惯例，caller 以换行恢复达到自洽（其首跑 exit1/1911 级联已如实记录于被审 .md），校验器+pins+声明环境自洽。镜像按 pin 逐文件取匹配字节复现该状态（precheck 44/44）。
2. 纯 LF 文件在 PS5.1 `Get-Content`（默认 GBK）下发生"GBK 前导字节+LF 吞行"（`PhysicalGoldenTests.cs`：LF 视图 1246 行 vs CRLF 视图 1258 行，16 候选吞 12，行号漂移导致 bound-anchor-content 误报；CRLF 下 928 行为真代码行）。caller 工作树为 CRLF 故不受影响。镜像代码文件统一转 CRLF 后伪差消失。**此为审计镜像的构建问题，非候选缺陷；未据此计入缺陷清单。**

### 3.1 fresh 完整校验（一次）

命令：`powershell -NoProfile -ExecutionPolicy Bypass -File <mirror>/tools/verify-v5-n3-closeout-ledger.ps1 -RepoRoot <mirror>`
结果：**native exit=0，24 项 CHECK-OK，0 FAIL-TOKEN**，`VERIFY-OK n3-closeout-ledger 592=411bound+181classified+0pending, 14 packages, pins/digests/registry intact`。
覆盖：evidence-catalog 44/44 hash-match、census-pin（instances 049a097b…/rules 24959db1…）、rules-count 542、paths-pin 1516、units-pin 655145、gap-pin 592、una-pin 17、mixed-pin 27、pkg-vector 14、ledger-size 592、unique-set、set-equality、ledger-schema（17 列）、history-fidelity（592 全量）、fixture-discipline、closure、bound-anchors、bound-obligation、evidence-resolution、classified-discipline、pending-discipline、adjudication-pin 14、blocked-set 4 案。
日志：`fresh-verify-2.log` SHA256=`4d3544201645c16e3343361a3584fb53c30b2162337b69b46d6c29dbb1d454b9`；44-pin 预检 `precheck-pins-5.log` SHA256=`58b3436316fbd64086325890c9cb5e969b7c9c587fa91aed0870441a24cb78f8`。

### 3.2 负例（字节保真副本 + `-LedgerPath`，均 exit=1 且命中具体 FAIL-TOKEN，无缺依赖假拒绝）

| 负例 | 变异 | 结果 | 日志/副本 SHA256 |
|---|---|---|---|
| NegA：新 classified 理由删除 | B1 第 1 行 reason 整串→`-`（恰 1 处替换） | exit=1，**恰 1 个 `FAIL-TOKEN classified-reason`**（failures=1；adjudication-pin 仍 CHECK-OK，证明 reason 不在 digest 六列、检查定位精准） | `negA-run-2.log`=ca2e3e73756… `negA-…tsv`=1d09cf81ce02… |
| NegB：B6 原说明串伪升 bound | 两条 B6 行 verdict classified→bound（保留散文 csRead/asrt，恰 2 处替换） | exit=1，failures=11：`bound-anchor`×4（散文不可解析）+`bound-commit`×2（commit='-'）+`verdict-vector`×2 +`verdict-total`×2 +`adjudication-pin`×1（B6 digest 漂移）——**伪升 bound 被四层独立检查拒绝** | `negB-run.log`=c74e4fda489e… `negB-…tsv`=ac1a249ce18a… |

## 4. 复核检查清单（协议 §1.2 八维）

1. 范围与路径：✅ diff 恰 3 文件、无越界、工作树 clean、ancestry 成立。
2. 假绿与误 PASS：✅ fresh 为本镜像全新运行（非引用旧产物）；负例证明检查真实生效；无 skip/空断言引入；`ledger-final.log`（SHA256=0A67C483…）与 `patch-summary.json`（SHA256=1ACADE34…）仅作 caller 历史记录交叉引用。
3. 功能正确性：✅ 六行身份与分母保真（§1）；四条出千分类严格贴 0955 裁决边界，无一升格为等价/豁免表述。
4. 并发、时序与恢复：✅（适用范围内）无源代码改动；verifier 对 RepoRoot 只读（仅 `git cat-file -e`/`merge-base --is-ancestor`）。
5. 安全与隐私：✅ B6 伴随 fixture/6 Facts 仅公开牌面投影（无 wall/seed/ura 等字段；C# 只读断言同界）；台账新增文本不含私有信息。
6. 证据与口径：✅ 全部命令、native exit、计数、SHA 如实列出；classified 一律不计 full；`complete.flag` 未创建。
7. 环境与平台：✅ PG/WSS/Unity/IL2CPP/设备缺口不变且未冒充通过；CI 仅只读引用（push 40260010463 实际 head=7606c5c、PR 40260015059 实际 head=6dfc93a，各 1808/0/0），**不作为本地候选行为 PASS 的证据**；历史 39970580589 根因保持 UNRESOLVED，不称 flaky/fixed。
8. 文档一致性：✅ §15 与 TSV 六行、校验器 pins、0955 裁决、residual/acceptance/B6F 文档相互一致；§14 历史 411/175/6 未改写。

## 5. PASS / FAIL / BLOCKED / NOT_RUN

- **PASS**：六行身份/分母保真静态复核；四条出千+B6 两条证据链对照；校验器不弱化审计；fresh 24 项校验（exit=0）；NegA/NegB 两个有意义负例（exit=1 且命中预期 FAIL-TOKEN）；44-pin 预检。
- **FAIL**：无。
- **BLOCKED**：无。
- **NOT_RUN**：PG/WSS/Unity/Gradle/dotnet 全量测试重跑（依约引用已接受 6 正例+五翻旗证据）；G1 九门重审；新伴随 fixture 的 catalog 增量登记检查（见下）。

## 6. 缺陷清单

无。一轮一次给全：本轮未发现任何需返修项，无遗留缺陷计数。

## 7. 精确下一依赖

1. **catalog 增量登记检查**：`ai-discard-flags-v1` 作为新伴随 fixture，其 catalog/manifest 增量登记仍须按既有规则单独检查——不因本台账零 pending 而跳过，也不得借零 pending 混入 592 冻结分母（`fixture-discipline` 现状即拒绝增量 fixture 进 ledger，该边界应保持）。
2. **G1 九门重审**：在最终集成头（feat/v5-1-integration 合入本候选后）对同一最终头一次 G1 九门重审；本轮 PASS 仅覆盖上述 3 文件候选范围，零 pending ≠ G1 通过。
3. 在途线程边界：N-2 宿主（2760c6e，已经 379be5f 统一批次接受）；P4.1/P5.4a 及 P2.2 补验仍在途，均受 G1 门槛约束。V4 仍是唯一生产基线；G5 未完成。

## 附：本轮独占证据路径

基目录 `artifacts/TEMP/glm-n3-final-review-20261026/`（git-ignored）：`mirror/`（审计镜像）、`precheck-pins.ps1/.log`、`fix-mirror-eol.ps1/.log`、`normalize-mirror-eol.ps1/.log`、`probe-anchor-eol.ps1/.log`、`make-negatives.ps1/.log`、`fresh-verify-2.log`、`negA-run-2.log`、`negB-run.log`、`negA-classified-reason-removed.tsv`、`negB-b6-pseudo-bound.tsv`（各文件 SHA256 见 §3）。caller 原始日志未覆盖、未修改。
