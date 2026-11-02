# G2-C04-D05-R1 bridge 接续返修任务卡

日期：2026-11-02，Asia/Shanghai。用户明确要求将已复现的 D05 返修作为任务通过 bridge 下发给 Luna；当前调用方独立验收。此卡接续原生 D05，不另开并行重复实现。

## 身份与资源

- 执行模型：用户指定 **gpt-6-luna**。bridge 不允许当前 Codex 对自身派发，因此使用已公开支持此模型的 **deepseek_harness** 路由；若 bridge 模型确认未批准或实际模型不是 Luna，不以默认 DeepSeek 模型继续本任务，报告真实状态。
- 独立接续树：`E:/majiang-review/g2-c04-d05-bridge-luna6-r1-20261102`；分支 `wt/v5-g2-c04-d05-bridge-luna6-r1-20261102`；mainPath `D:/majiang`。
- 继承被审交付：`390aa95f430dd6a7de7081f3de477beae502de86`，其中代码 `47ecf6a6a888f3e167a348393d8aa200b1230282`。派发前只增加本任务卡和独立复核报告，都是调用方 docs-only；不假称新功能已通过。
- 原生执行者 `/root/g2_c04_d05_luna6` 已交付、停止；不唤醒或修改其原树。旧证据只读保留于原树 `E:/majiang-review/g2-c04-d05-luna6-20261102-c04d05a1`。
- 独立验收全文：`CODEX-V5-G2-C04-D05-REVIEW-R1-20261102.md`；完整证据 `E:/majiang-review/caller-d05-review-20261102-r1/build/caller-review/`，audit SHA `8BCB44FB92881AC3E9DB24D0DB205A5E65566D41CD40E96CA8EAA354B8353B13`。
- 原30文件+补充授权1文件维持；精确范围读取原实施任务卡 §6，再加 `unity/Mahjong.Unity/Assets/Tests/PlayMode/SpectatorChannelPlayModeTests.cs`。额外文件只允许保留原授权的两个 UI fixture 字段注入修复；不得改 authority 反射、断言/跳过或 asmdef。两份新的任务/复核文档只读，不列为接收方修改路径。
- 原实施卡、边界合同、AGENTS、通用模板与复核协议必读。允许在忽略的独占 `build/<GUID>` 写任务阶段、探针和证据。Domain/Client/proto/TableActor/存档 schema/asmdef/其他owner文件仍只读；需要额外 tracked 文件给精确复现与最小范围请求。

## 完整返修要求

【复核完整性要求】
本轮必须一次性给出全部已知缺陷，不得只报一条或把已知可复现问题留到下一轮。
每条缺陷须含：ID、严重级别、位置、复现命令/脚本、实际 vs 期望、证据路径、修复要求、验收断言。
同类缺陷合并，给出统一修复要求与统一负例矩阵。
返修方须在同一轮内批量修复全部 BLOCKER/MAJOR，并逐条给出修复映射与验证证据；
不得用 skip、空测试、放宽断言、刷新摘要或旧产物获得绿色。
仅当出现新信息时才允许追加缺陷，并须说明此前为何不可发现。

本轮 **4组：1 BLOCKER + 3 MAJOR**，须全文读复核报告：

| ID | 返修组 | 来源 |
| --- | --- | --- |
| D05-R1-01 BLOCKER | verifier 实际解码 wire/Welcome 与身份、公私内容，拒绝无效/空/跨座位字节及旁路元数据篡改 | 本轮独立发现 |
| D05-R1-02 MAJOR | 恢复 controller 优先于初始计划，已 release AI 无真人绑定可推进；默认 owner、viewer quarantine、ticket 有界退役 | 本轮实测与源码发现 |
| D05-R1-03 MAJOR | 完成 N/U/P 固定矩阵、合法非空袖牌/费用、慢队列饱和、真实旧 raw 投递和 Windows 原请求恢复对账 | 原交付已知遗留 |
| D05-R1-04 MAJOR | runner 冻结输入、XML/marker严格门禁、有界超时/取消/停止writer后封存与资源释放；纠正 exe/package长度口径 | 本轮独立发现 |

保留 D03/C01/C02/C03 和 D05 前置已接受范围，不重做旧专项。通过合法公共命令建立隐私正对照已授权；不修改、接管或重开 D04 三绝技人工范围。若存在真实外部功能阻断，记录精确子项及最小复现，其余独立工作继续，不宣称整体完成。

## 分阶段接续

| 阶段 | 进入条件与工作 | 验收与时限 |
| --- | --- | --- |
| A | 验证 HEAD/clean、文件范围、工具/空间/当前进程许可；读全部原件，写独占阶段卡 | 短预检默认每项120秒；许可查询和冷导入分开，不修改机器许可 |
| B | 同轮批修R1-01..04产品/工具已知问题；先实际room、恢复、队列、verifier及runner短负例 | 外部组件命令默认上限180秒，必要整套.NET上限600秒；输出/native/XML完备 |
| C | 真实序列化与验证端对齐、全部无效字节/元数据/身份/XML/marker/取消负例 | 所有已知廉价缺陷修复且负例真实拒绝，不能只选一条通过 |
| D | 冻结源码/输入和包闭包，再跑固定N1–N8/U1–U3/P1–P2、G5三行所需真实Unity/Windows | 共享锁最多等待600秒；Unity冷导入参考900秒，Player build1200秒，各场景按实际有限预算写卡；每30秒心跳 |
| E | 停止自有writer，稳定summary/manifest；提交代码和docs-only报告，逐项映射 | 未验证不能PASS；父代理独立验收。无需每阶段另等人工批准 |

锁为 `Local\MahjongV5P45UnityValidation`；E盘独占输出、缓存/TEMP/Library/Player；不绕锁、不杀他人进程、不改写旧证据。超时/取消先正常退出并严格界定自有进程身份，按报告完成收尾和UNKNOWN边界。已有有效且输入不变检查不机械重复；最终合同要求同候选同包时必须重新绑定。

原独立单测154/154和483/483、旧包2496/205项hash检查仅是被审版本成果，不能声称返修版本已通过。只在具体原因修复、相关输入或资源改变后重试。

## 交付

仍用原唯一实施报告 `V5-G2-C04-D05-PRIVACY-DELIVERY-20261102.md`，保留历史，追加本轮接续：四组修复映射、固定矩阵每行实际状态、命令/原生退出/计数/XML/日志/SHA、源码/输入/包身份、首次失败、未覆盖平台与依赖、clean/diff-check。证据生成物不入Git，代码与报告提交分开。

执行者不得再委派、approve、push、建改PR、merge、deploy、改scheduler/complete.flag、回写旧bridge或原生会话状态。正式D05/C04/G2–G5在父代理最终独立验收前均未批准。
