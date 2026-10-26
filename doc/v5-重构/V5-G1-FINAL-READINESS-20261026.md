# V5 G1 最终就绪汇合判定矩阵（GLM 独立复核）

日期：2026-10-26
任务：GLM 唯一槽独立验证——把已接受的 G1 复核输入在同一最终头汇合，形成 caller 最终 G1 判定矩阵与推荐。
冻结头：`71a1fbb0eeeedf19adebb7931e22e1c22a9994d3`（branch `wt/v5-g1-final-readiness-glm-20261026`，独占 worktree `D:/majiang-wt/g1-final-readiness-glm-20261026`，全程 tracked clean）。
性质：**汇合/增量适用性核对，非重开 592 分母、非重复全仓 review、非重跑整套重型 gate。**

---

## 0. 输入清单与祖先关系（read-only git 判定）

全部已接受输入头均为冻结头祖先（`git merge-base --is-ancestor` 实测）：

| 输入 | 头 | 祖先关系 | 覆盖内容 |
|---|---|---|---|
| 八门运行时 review | `08c4a75` | ANCESTOR | 门 1/3/4/5/6/7/8/9（门 2 NOT_RUN） |
| 门 2 黄金 anti review | `289969a` | ANCESTOR | 门 2 PASS、catalog/companion 钉 |
| 门 2 caller 验收 | 同上报告内 | — | REC-G2-01/02 非阻断 |
| P5.4a 验收 | `bfc0839` | ANCESTOR | 4 文件（3 src + 1 test），bfc0839..HEAD 字节等同 |
| 978a 双 CI | `978a156` | ANCESTOR | 1,829/0/0（exact + merge-ref） |
| P2.2 平台复验 | 记录在 `71a1fbb` 内 `V5-P2.2-PLATFORM-RECHECK-20261026.md` | 已在头内 | 平台分列 + wrapper |
| AuthorityUnavailable 回归 | `d9d17d8` | ANCESTOR | TableRuntime 397→400 |

`08c4a75..71a1fbb` 完整差异 = **恰 13 文件**（`git -c core.quotePath=false diff --name-status 08c4a75 71a1fbb`，+6,841/−23），逐路径全列：

```text
A	doc/v5-重构/CODEX-V5-G1-GOLDEN-REVIEW-ANTI-ACCEPTANCE.md
A	doc/v5-重构/CODEX-V5-G1-RUNTIME-REVIEW-ACCEPTANCE.md
A	doc/v5-重构/CODEX-V5-P54A-SESSION-OPERATIONS-ACCEPTANCE.md
A	doc/v5-重构/V5-978A-INTEGRATED-CI-REVIEW-20261026.md
A	doc/v5-重构/V5-G1-GOLDEN-REVIEW-ANTI-20261026.md
A	doc/v5-重构/V5-G1-RUNTIME-REVIEW-20261026.md
A	doc/v5-重构/V5-P2.2-PLATFORM-RECHECK-20261026.md
M	dotnet/src/Mahjong.Client/TableSession.cs
A	dotnet/src/Mahjong.Client/TableSessionOperationQueue.cs
M	dotnet/src/Mahjong.Client/TableSessionRunner.cs
A	dotnet/tests/Mahjong.Client.Tests/TableSessionOperationQueueTests.cs
A	dotnet/tests/Mahjong.TableRuntime.Tests/SettlementAuthorityUnavailableTests.cs
A	tools/unity/test-wire-aot-platform-recheck.ps1
```

即 7 docs（P5.4a 3 src + 1 test、AuthorityUnavailable 1 test、P2.2 wrapper 1 分列其后）+ 零 catalog/ledger/verifier/manifest/生产 engine/server/protocol 改动。

---

## 1. 九门矩阵

规则原文（`V5-0-1-IMPLEMENTATION-TASKS.md:110`）：①dotnet test 独立全绿；②已导出黄金样本对齐率100%（未导出单列）；③依赖方向静态测试通过；④Domain 不含隐式随机/时钟/浮点权威；⑤三绝技与 136 守恒通过；⑥Wire 可重复生成；⑦按座位/观战私有性通过；⑧TableRuntime 并发版本恢复合同通过；⑨Unity/IL2CPP 可用平台构建日志和缺口分列。另加 V5-1-REVIEW.md 记录要求。

| 门 | 规则要点 | 当前证据头 | 祖先/字节等同 | 本轮核对动作 | 判定 |
|---|---|---|---|---|---|
| 1 | dotnet test 独立全绿 | `71a1fbb`（精确头 CI） | push=精确头证据 | 只读复核远端产物 + fresh 定向实跑（见 §2/§3） | **PASS** |
| 2 | 已导出黄金 100% 对齐（未导出单列） | `289969a`（祖先）+ HEAD 字节等同 | catalog/companion 字节等同 | 轻量只读 verifier 实跑 exit 0 | **PASS** |
| 3 | 依赖方向静态测试 | `08c4a75`（祖先） | 祖先 | 确认套件内静态测试随 1,832 CI 全绿 | **PASS** |
| 4 | Domain 无隐式随机/时钟/浮点 | `08c4a75`（祖先） | 祖先 | 13 文件差异含零 Domain 源改动 | **PASS** |
| 5 | 三绝技 + 136 守恒 | `08c4a75` + N3 收口（祖先） | 祖先 | 同上 | **PASS** |
| 6 | Wire 可重复生成 | `08c4a75` + HEAD CI | 祖先 | Contracts 149 于 1,832 CI 内全绿（含 ProtocGenerationIsRepeatable） | **PASS** |
| 7 | 座位/观战私有性 | `08c4a75`（祖先） | 祖先 | 同门 4 | **PASS** |
| 8 | TableRuntime 并发版本恢复合同 | `71a1fbb` | 精确头 CI + fresh 实跑 | fresh Release 定向 3/0/0 | **PASS** |
| 9 | Unity/IL2CPP 平台日志 + 缺口分列 | `71a1fbb`（P2.2 记录在头内） | wrapper 字节等同 | 哈希复算 | **PASS（缺口分列口径）** |
| 记录 | V5-1-REVIEW.md 记录要求 | 旧记录 `8ee22eb` 背景 + 本报告 | — | 本报告构成最终汇合记录 | 记录齐备，**批准权在 caller** |

**口径声明**：1,829（978a）不是本轮新全量；1,832 为冻结头精确 push CI。本地 1,832 全量未重跑（依约以精确头 CI 代替），仅对最新增量关联类做 fresh 定向实跑。

---

## 2. 门 1 证据链（三代计数 + 远端产物只读复核）

### 2.1 计数谱系（全部 success）

| 代 | 头 | run（类型/checkout） | URL（`https://github.com/liuchangready/majiang-game/actions/runs/<id>`） | 计数 |
|---|---|---|---|---|
| 1 | `08c4a75` | 40266677478（push/精确头）+ 40266679969（PR/merge-ref `9bfa01b014e34e70c58235f2b2817a9b75e8664a`） | 同上规则 | 1,808/0/0 |
| 2 | `978a156` | 40273525993（push/精确头）+ 40273530505（PR/merge-ref `a3ac2efd38c09dba6bf6c057752d28fc1461d6b`） | 同上 | 1,829/0/0 |
| — | `7eedc5a`/`d9d17d8` | 40277058655+40277063431；40277609019+40277615389（P2.2 / AuthorityUnavailable 集成） | 同上 | success |
| 3 | `71a1fbb` | **40279597103（push/精确头）**+ 40279601879（PR/merge-ref `3c89a010213a99251f898d6771b17d525b1d98b2`，parents=[`457274c`,`71a1fbb`]） | 同上 | **1,832/0/0** |

push run=精确头证据；PR run=merge-ref 证据，两者分开不计混同。

### 2.2 对 40279597103（精确冻结头）的只读复核（本轮）

- `run-info.txt` head=`71a1fbb…` 精确（SHA `5892727ced919fe09840451b33c879c5ad747c3a9bc06ff428f998a240c4d0c7`）。
- verify 脚本 SHA 不变：`fa8d539bb6e036a7fd002011f001f5d5fdb27ba8b20cd5a9089098e87f3ce307`（HEAD 工作树复算一致）。
- `summary.log`（SHA `16876162b295e04587b3a7ac86af142942151a5329a57b3a2d901afc5ddfc3fe`）：`OVERALL: PASS (project rows: 9 PASS, 0 FAIL, 0 BLOCKED, 0 NOT_RUN)`；明细 Ai 444 / Client 153 / Wss 80 / Contracts 149 / Domain 517 / Lobby 89 / TableRuntime 400 = **1,832/0/0**。
- `wrapper-summary.json`（SHA `9177d71a417ab6b4df8f1cb5bc2eff7502ad93300f4941db532f25824e93d67e`）：head 精确、11 个 `exitCode: 0`。
- PG 阶段 `evidence-sha256.txt` 12/12 条目 `Get-FileHash` 逐一复核 **12 OK / 0 bad**。
- 框架日志（纯文本实读，非摘要替代）：`TableRuntime: Passed! - Failed: 0, Passed: 400, Skipped: 0, Total: 400`；`Client: … Passed: 153 … Total: 153`。
- 978a 原始缓存原址复算：`summary.log`=`81d794f34a53e5ee6eb4d01497c6683f9e0b112ac506815ae3f1f8ebda755e58`、`pg/wrapper-summary.json`=`3a227756b1bff86fcf63cbdf63c73c9be757f4e0f09e001095368cd1e320a992`（均与接受值一致）。

### 2.3 本轮 fresh 实跑（最新增量关联，非全量替代）

环境：`D:/Unity/dotnet/dotnet.exe` 8.0.426，`DOTNET_CLI_UI_LANGUAGE=en`，TEMP/TMP 仅进程级指向自有 `artifacts/g1-final-readiness-20261026/tmp`。未用 `dotnet test --filter`（历史已证被忽略）。

| # | 原样命令 | native exit | 实测 |
|---|---|---:|---|
| 1 | `dotnet build dotnet/tests/Mahjong.TableRuntime.Tests/Mahjong.TableRuntime.Tests.csproj -c Release --no-incremental --nologo` | **0** | Build succeeded，0 Warning(s)/0 Error(s)；DLL `c2a18396c9dac16b280cf5d03a269f27d8188240e620d575dabe7ea05dbe3d7e`（1,098,240 B） |
| 2 | `dotnet <TableRuntime DLL> -class '*SettlementAuthorityUnavailableTests' -result-xml artifacts/g1-final-readiness-20261026/authority-unavailable.xml` | **0** | `Total: 3, Errors: 0, Failed: 0, Skipped: 0, Not Run: 0`；XML `total="3" passed="3" failed="0" skipped="0"`（SHA `bba67cdc44fa69a99ebc7300f8983a841e93419ea01deee8ac4661fb7b5033a5`），恰含 3 个用例名 |
| 3 | 对照：`-class '*NoSuchClassControlG1Final'` | 0 | XML `Total 0`（SHA `010e34d7838d26c0159978d66e3412a8675eb957079741fc84976befb739393c`）——证明 `-class` 真实过滤，非"全跑" |

用例名（XML 原文）：`MissingFactsPortRejectsAValidTsumoWithoutAdvancingAnyState`、`TheSameValidTsumoIsAcceptedWhenFactsPortIsConfigured`、`DurableRejectionLeavesNoEventsReceiptsOrSettlementOutboxAndStaysRepeatable`。

### 2.4 门 2 / N-3 台账本轮复核

- `powershell -NoProfile -ExecutionPolicy Bypass -File tools/verify-v5-golden-catalog.ps1` → exit **0**，`PASS catalog=…catalog-v1.json sha256=236C8E0EE9B7FFB86187CB7D3AB650C9EA13FC0A4C47675E044B64BB0C93116D fixtures=6 requirements=73 cases=10389 random=10000 facts=45 coverage=full:17/partial:21/none:32/v5-only:3`。catalog SHA 与接受钉一致；4 companion 字节等同复算：movement-v1 `5927f44a650db963813c352b9786147a5953772862ca33722fdd9ce3e17fa3c5`、shuffle-reversed-v1 `81d21f0a0c0c6fda6effb029364d5bc8f4ee30c84f1431246fdd9db5309f5902`、visibility-v1 `6506c0729a36e4625e157a5e6ea2ccb79b9c2ff2e4f3f57988bbb9535cc52b94`、ai-discard-flags-v1 `db1a872f324a8fb994020fb90700e5f64e4e90ecaa63e1cb3fc3a660cae10aee`。592=411 bound+181 classified+0 pending 口径未触碰。
- `tools/verify-v5-n3-closeout-ledger.ps1`（只读）→ exit **1**，15 CHECK-OK + 842 FAIL-TOKEN。**根因已查清，非台账缺陷**：
  - 核心完整性层 15/15 全过：census 钉（instances `049a097b…`、rules `24959db1…`）、1516 paths、655,145 nodes、542 rules、592 gap 重推导、17 UNA、27 mixed、14 包向量、ledger 集合等价、17 列 schema、592 行历史保真、bound 义务、14 包 adjudication 摘要。
  - 失败全部集中在证据存活层：7 条 `EV-R1-*` 指向原任务工作树 `artifacts/TEMP/codex-review-20261025/`（本独占树按约束不存在）；其余为本工作树 `core.autocrlf=true` 把 LF blob 检出为 CRLF 后按原始字节哈希导致的 SHA "漂移"。
  - **blob 层零漂移证明（终表）**：对全部 35 条 in-tree EV 钉逐条 `git show 71a1fbb:<path>` 取原始 bytes → 规范 LF（`tr -d '\r'`）→ LF 与 LF→CRLF 双形态各算 SHA256，**35/35 与 pin 一致（25 条 CRLF 形态 + 10 条 LF 形态，0 条 NO_MATCH）**；另 9 条 NON_TREE（7 条 EV-R1-\* 指原任务工作树 TEMP + 2 条外部 census `EV-CENSUS`/`EV-RULES`，后者由脚本自身 census-pin CHECK-OK 验证）。生成脚本与终表：`artifacts/g1-final-readiness-20261026/ev-eol-proof-generate.sh`（SHA256 `6c6ee46cd2820350d7c3e906a55499524e0452ec7fb0ef16e832763782676018`，纯 git/tr/unix2dos 字节管道无文本再编码）、`artifacts/g1-final-readiness-20261026/ev-eol-proof.csv`（44 行 = 35 in-tree + 9 NON_TREE；SHA256 `bb69177f62eb73e54ac3b517d6fdbebcc2a0ddcd086eaf66b597f15085e4b680`，列 id,path,pin,lfHash,crlfHash,match）。早期单形态对照表已改名 `ev-blob-check.intermediate-single-form.txt` 保留，**仅为中间产物，不作为最终证明**。内容与冻结头字节等同，仅 EOL 表示差异；842 个 FAIL-TOKEN 为证据目录失败的级联（519 bound-evidence + 306 classified-evidence + 17 evidence-catalog）。
  - **口径声明**：上述 35/35 仅为 EOL 内容核对；原 verifier 在本树 exit 1、7 条 TEMP 证据文件缺失的事实**未被"变绿"**，维持如实记录，verifier/ledger/catalog/fixtures 未做任何修改。
  - 定性：校验器证据层按原任务工作树的 EOL/TEMP 布局设计，在本独占树**结构性无法 exit 0**；不得据此重开 592 分母，也不得为凑绿修改 verifier（约束禁止）。

---

## 3. 门 9 / P2.2 平台分列（维持原规则口径）

`V5-P2.2-PLATFORM-RECHECK-20261026.md`（记录在冻结头内；wrapper `tools/unity/test-wire-aot-platform-recheck.ps1` SHA `C2E4EB7C99498413BFCFF48A8EAD3A2869535438DB6B71B38E6E45A9805CC014` 与接受钉字节等同）：

| 平台 | 状态 | 说明 |
|---|---|---|
| .NET（Wire AOT 语义面） | PASS | 18 messages / 20 assertions |
| Unity Editor | PASS | — |
| Windows IL2CPP | PASS | player exit 0 |
| Android APK 构建 | **FAIL（BLOCKER 开）** | D-01 sdkmanager `0xC0000409`；D-02 Ninja 缺失——均为本机工具链环境缺陷，非代码缺陷 |
| iOS | BLOCKED | 无 macOS 构建环境 |
| 真机/跨物理网络 | NOT_RUN | 本任务不重跑、不宣称 |

门 9 规则原文要求"可用平台构建**日志和缺口分列**"，P2.2 记录如实分列 → 门 9 按其自身口径 PASS；Android/iOS 缺口保持单列，**不升级为 G1 新硬门，也不写假 PASS**。

---

## 4. 远端状态与历史事项

- `v5` = `457274c9b74643a9e7dc73cabfb64cc8a2eed223`（未变，PR19 未合入）。
- `feat/v5-1-integration` 在冻结头之后推进到 `bc00bd31914fdadc930feaeb9ae490b65672d750`（`804f173` 依赖方向守卫测试 + `2d6eb6a` 反绕过修订 + merge；DS S-1 集成，**不在冻结头内、不纳入本矩阵**，其验收走自身轨道）。`71a1fbb` 是 `bc00bd3` 祖先（`git merge-base --is-ancestor` 实测），冻结头证据不受影响。
- 历史 run `39970580589` 根因保持 **UNRESOLVED**，绿 run 不推出修复。
- 本轮未触发任何 CI/push/MR；所有 gh 调用为只读 API。

---

## 5. 最终 G1 建议（供 caller 裁决，不自行批准）

**建议：按任务书批准 G1。**

依据：九门中门 1/2/3/4/5/6/7/8 全部 PASS（不可变产物祖先或字节等同 + 冻结头精确 CI 1,832/0/0 + 本轮增量 fresh 定向 3/0/0 与只读哈希复核），门 9 按"日志和缺口分列"原文口径成立（P2.2 记录在头内、Android/iOS 如实单列），V5-1-REVIEW.md 记录要求由历史记录 + 本汇合报告满足。N-3 收口 592 分母核心完整性在本头 15/15 复核通过。

**非门未闭项（如实单列，均不构成 G1 硬门、不新增门槛）**：
1. Android 工具链 D-01（sdkmanager `0xC0000409`）/ D-02（Ninja 缺失）BLOCKER 开——影响 Android APK 本机复验，不阻塞 G1（此前未设为 G1 硬门）。
2. iOS BLOCKED（无 macOS）。
3. I-2：run `39970580589` 根因 UNRESOLVED 保留。
4. `Mahjong.LocalHost.dll` 仍为 3,584 B 空壳——P4 未启动的**计划内**边界。
5. `TableSessionOperationQueue` 未接入生产组合根——P4.2 接线边界（计划内）。
6. S-2 插值 format 解析回归——新增可选测试工具缺陷，回原线程返修，非生产 Domain 缺陷。
7. DS S-1 已在冻结头后并入 PR19（`bc00bd3`），本矩阵未复验其内容。

若 caller 选择先闭 Android 再批 G1，最小剩余动作 = 在健康 sdkmanager/Ninja 环境重跑 P2.2 Android 段并出对数报告（其余七门半证据无需任何重跑）。

---

## 6. 首批 P4.1/P4.2/P5.1/P5.2 依赖图与文件边界（仅建议，不实现、不调度）

依据：`V5-0-1-IMPLEMENTATION-TASKS.md`（:168-200 任务定义、:252 批次表）、已接受 `V5-P4.1-CONTRACT-ADDENDUM.md` §3.1（:309 起）、已冻结 `V5-P4.2-SHELL-PREPARATION.md` §8.1–8.2，并经实仓核对（csproj/sln 存在性见下）。

**实仓状态核对（本轮 `ls`/`grep` 实测）**：`dotnet/src/Mahjong.LocalHost/Mahjong.LocalHost.csproj` **已存在**（目录内仅此一文件，纯空壳）且**已登记**于 `dotnet/Mahjong.sln:14`；`dotnet/src/Mahjong.TableService/` 与 `dotnet/src/Mahjong.Infrastructure/` **不存在**（真新增）；票据合同面已冻结存在（`dotnet/src/Mahjong.Client/TableTicketProvider.cs`、`dotnet/src/Mahjong.LobbyService/LobbyApplication.Tickets.cs`）。

```
批 A（G1 批准后立即可发，P4.1 与 P4.2 核心 并行，互不等待）：
  P4.1 LocalHost 组合根     在既有已登记工程 dotnet/src/Mahjong.LocalHost/ 内实现（不新增 csproj、不动 sln）
       契约（accepted addendum §3.1，旧"LocalTableHost : ITableChannel 实例继承"草案已废除）：
         - 静态工厂门面：LocalTableHost.CreateChannelAsync(...) 成功返回 ITableChannel（New 状态，零物理资源）
         - internal sealed class LocalTableChannel : ITableChannel（ITableChannel 保持 4 方法 + IsOpen，
           严禁加 IDisposable/IAsyncDisposable；严禁引用 Unity 程序集/实现 ITableChannelFactory）
         - 资源获取延迟至首次 OpenAsync；New 关闭为无操作；PermanentlyClosed 原子化；等待者取消分流
       依赖：G1 Domain/TableRuntime/Contracts/Wire（已备）+ P5.4a Client 接口基线（已备）
  P4.2 核心（步骤 0–5）      unity/Mahjong.Unity/Assets/Scripts/{App,UI,Platform}/
       依赖：G1 Contracts（已备）+ P5.4a 基线（已备）+ P2.2 AOT 结论（已备，缺口单列）
       用 UnavailableLocalTableChannelFactory + 测试假通道独立完成；退出条件="启动到大厅可用、
       UI 不绕过 Client"，非"本地整局可用"；只消费 ITableChannel/ITableChannelFactory/ISessionView/路径根，
       不定义 ILocalSaveStore/LocalSave 字段（§8.2）
  P4.2-B（步骤 6）           等待 P4.1 真实工厂交付后接线（本地会话只读集成），非批 A 阻塞项

批 B（P5.1 与 P5.2 并行；P5.3 与 TableService 并行，任务书 :168/:252）：
  P5.1 TableService         新增 dotnet/src/Mahjong.TableService/（真新增 csproj）
       依赖：G1 TableRuntime/Contracts.Wire（已备）+ 已冻结的票据合同面（已存在，上列两文件）
             + 假签发器（fake signer）先测——不等待 P5.3 整项实现；真实票据集成等到 P5.3 相应交付
  P5.2 Infrastructure       新增 dotnet/src/Mahjong.Infrastructure/（真新增 csproj）
       依赖：仅 G1 的 P3.4 端口与事件 schema（已备）——**不依赖票据、不等待 P5.1/P5.3**，可立即并行
  P5.3（参照项，非首批四槽）与 P5.1/P5.2 并行（任务书 :186），现有 LobbyService 工程继续
```

**same-source 争用点（本轮实仓审计后如实列出，不做"唯一"绝对断言）**：
- sln 编辑：P5.1/P5.2 各自真新增 csproj 都需登记进 `dotnet/Mahjong.sln` → 同文件争用，需串行化 sln 编辑或由集成方统一登记；P4.1 用既有已登记工程，**无 sln 争用**。
- P4.1 与 P4.2 核心源文件零交集（dotnet 既有工程 vs unity 树）；P5.1 与 P5.2 源文件零交集。
- CI 按惯例每批一双（push 精确头 + PR merge-ref）。Android 工具链缺口影响 P4.2 的设备验证面，不阻塞其代码工作。未设计任何新功能/新门槛。

---

## 7. 兄弟任务非阻断注记

- DS S-1（module dependency）：已返回并并入 PR19（`bc00bd3`，冻结头之后），不在本矩阵，不阻断。
- DS S-2（domain scanner）：未返回不阻断；插值 format 解析回归回原线程返修，属新增可选测试工具，非生产 Domain 缺陷，不扩大 G1 门槛。
- Anti REC-G2-02 统一 checker：非门槛维护，未返回不阻断。
- 三者均只改新增路径、未纳入冻结头；其未提交产物本任务未读取。未为凑槽重做任一维护任务。

---

## 8. 缺陷清单（一次性给全）

**R1 轮 caller 返修 3 组已在本轮全部修复**（修复映射）：

| ID | 级别 | 缺陷 | 修复位置与方式 |
|---|---|---|---|
| D-R1-01 | MAJOR | §6 与已接受契约不一致（废除的 `LocalTableHost : ITableChannel` 继承草案仍被推荐；未拆 P4.2 核心/P4.2-B；P5.2 被错误串行在 tickets 后；未区分票据合同与 P5.3 整项；csproj/sln 状态未实仓核对） | §6 全面重写：采纳 addendum §3.1 静态工厂门面 + `internal sealed LocalTableChannel`；P4.2 核心（0–5 步）用 `UnavailableLocalTableChannelFactory` 独立、P4.2-B（6 步）等 P4.1 真实工厂；P5.2 仅依赖 G1 P3.4 端口/schema 不等 tickets；P5.1 用已冻结票据合同面 + 假签发器先测、真实集成等 P5.3；实仓核对 LocalHost csproj 已存在且已登记（`Mahjong.sln:14`）、TableService/Infrastructure 真新增；删除"唯一争用"未审计断言 |
| D-R1-02 | MINOR | §0 13 文件未全列；正文 EOL 分布 26+9 笔误（caller 实测 25 CRLF + 10 LF）；交付的是早期单形态中间表而非双形态终表 | §0 全列 13 路径（与 `git diff --name-status` 逐条一致）；§2.4 改 25 CRLF + 10 LF + 9 NON_TREE 并给终表 `ev-eol-proof.csv`（SHA `bb69177f…`）与生成脚本（SHA `6c6ee46c…`）；旧表改名 `ev-blob-check.intermediate-single-form.txt` 标记中间产物；明确 35/35 仅 EOL 内容核对、exit 1 与 TEMP 缺失不被变绿 |
| D-R1-03 | MINOR | 报告路径不符合 expectedFiles（`…GLM-20261026.md` 应为 `V5-G1-FINAL-READINESS-20261026.md`） | `git mv` 归位；对 71a1fbb 累计 diff 恰一个 `A doc/v5-重构/V5-G1-FINAL-READINESS-20261026.md`，无 GLM 副本残留 |

无上一轮遗留缺陷。**本轮无新增生产/测试/fixture/ledger/catalog/verifier 缺陷——没有。**（本轮全部改动仅限本报告文件与自有 ignored artifacts；九门判定、G1 推荐结论不变。）

观察项（非缺陷，不计入缺陷清单）：
- O-1：`verify-v5-n3-closeout-ledger.ps1` 在本独占树 exit 1——环境根因（autocrlf 检出 + 原 TEMP 树不在本树），blob 层 35/35 零漂移已证（§2.4，终表与脚本可复算）。修复建议（非本轮执行）：verifier 证据层如需跨工作树复用，可在哈希前做 EOL 归一或改钉 git blob ID；不得为凑绿现改 verifier。
- O-2：远端 `feat/v5-1-integration` 已前进到 `bc00bd3`（S-1），超出冻结头——事实注记，非缺陷。

---

## 9. 未覆盖与回滚

**未覆盖（如实声明）**：本地 1,832 全量未重跑（以精确头 CI 40279597103 代替）；真实 PG 数据库行为未重跑（仅复核 CI 内 PG wrapper 产物与 12 条证据哈希）；Unity Editor/Windows IL2CPP/Android/iOS/真机/跨物理网络未重跑；PR merge-ref run 40279601879 仅核结论状态（success）未逐文件复算；S-1/S-2/REC-G2-02 产物未读取；框架历史 UTF-16 日志的逐用例明细未重推导（以 summary.log + meta.txt + 纯文本框架日志计数为准）。

**回滚**：本任务仅新增 1 个 tracked 报告文件（本文件）+ ignored `artifacts/`。回滚 = `git revert` 单 commit 或删报告文件；worktree 可随时 `git worktree remove`；无任何生产/测试/工具/远端改动，无需其他回滚步骤。

**证据根**（本树 ignored）：`artifacts/g1-final-readiness-20261026/`（fresh 构建日志、定向 XML×2、catalog/ledger verifier 日志、双形态终表 `ev-eol-proof.csv` + 生成脚本 `ev-eol-proof-generate.sh`、中间产物 `ev-blob-check.intermediate-single-form.txt`）+ `artifacts/ci-71a1fbb-20261026/run-40279597103/`（远端产物下载，62 文件）。TEMP/TMP 仅进程级。

**交付路径**：本报告为任务 expectedFiles 唯一新增 tracked 文件 `doc/v5-重构/V5-G1-FINAL-READINESS-20261026.md`；对冻结头 71a1fbb 的累计 diff 恰此一个 `A` 条目，`git diff --check` 干净，commit 后 `git status --porcelain` 为空。不 push、不建 MR、不合 v5。
