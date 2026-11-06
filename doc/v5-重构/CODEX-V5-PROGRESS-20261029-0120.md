# V5 进度检查与 LocalHost 汇合预检

2026-10-29，Asia/Shanghai。G1 APPROVED；G2–G5 未批准；V4 继续生产，181 classified 仍未覆盖。

## 基线与账本

本轮 fetch 后 origin/v5 仍为 8fd7c6cb701dfbb41c28938de4186749bdd1d6c7；PR19 已合并。唯一后续批次 PR20 源 feat/v5-post-g1-batch，检查时头 17808cca932269ac8058270a5684b33d02d82abd。主目录仍在旧集成分支，CLAUDE.md、PROMPTS.md 和两份未跟踪 V5 文档全部保留。

只读取一次跨工作区最近100条 bridge 账本；未轮询不变任务。活动 DeepSeek 1/2：thread_20261029_001657_f4f1（P4.2-B 独立审查输入 R2 reworking）；GLM 1/1：thread_20261027_230302_93fb（P5.4b-A 持久宿主 R2 reworking）；Anti 0/1，总计2/4。原 GLM P4.2-B thread_20261028_223624_e4ae waiting_review，已有独立审查输入正在修订，未重复派单或提前批准。P4.1d 原线程 thread_20261027_154500_f0b7 已完成并记录既有 PR21；a/b/c/d 组件接受不等于完整 LocalHost 接受。历史失败 P4.1 线程由上述子任务承接，本轮未恢复或复制其实现。

本轮模型发现确认 DeepSeek 配置默认 deepseek/deepseek-v4.1-flash；Anti 配置默认 gemini-3.8-flash-high。mesh 当前返回 Anti exhausted / 429，reset 2026-11-01T16:54:27.999Z（上海时间11月2日00:54）；该状态携带历史限流记录，不是本轮新发模型请求得到429。Anti 席位 BLOCKED，不以第三个 DeepSeek/第二个 GLM 替代。准备一个 DeepSeek 独立门禁任务后，预期2/2、1/1、0/1，总3/4；此处是准备记录，dispatch是否成功与实际thread ID以本轮最终工具结果为准。

## 隔离汇合候选与真实预检

调用方从 origin/v5 创建 D:/majiang-wt/codex-localhost-integration-20261029-0120 / wt/v5-codex-localhost-integration-20261029-0120，再 fast-forward 至17808cc，合并 PR21 已分别验收组件头865ca0a455bda625c660c6b7e1df049f88d4591e。自动合并无冲突，未修改receiver工作树；暂存差异检查通过，候选提交95b47547b535f38a1fc6328445e209e7d402adcc，双parent精确17808cc和865ca0a。该候选尚未接受或发布，仅保留用于汇合门禁审查；不将其推送触发远端CI，不将黄金pin变化默认为获准。

本轮在候选自己的工作树 fresh 执行：

```powershell
D:/Unity/dotnet/dotnet.exe build dotnet/tests/Mahjong.Architecture.Tests/Mahjong.Architecture.Tests.csproj -c Release --no-incremental --nologo -m:1 -nr:false -p:UseSharedCompilation=false
D:/Unity/dotnet/dotnet.exe dotnet/tests/Mahjong.Architecture.Tests/bin/Release/net8.0/Mahjong.Architecture.Tests.dll -noLogo -xml build/caller-integration-preflight/architecture.xml
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/verify-v5-golden-catalog.ps1
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools/verify-v5-companion-fixtures.ps1
```

| 检查 | 实际结果 |
|---|---|
| Architecture fresh Release build | PASS，exit0，0 warnings/errors，65.75s |
| Architecture 原生XML与console | PASS，exit0，130/130，failed/errors/skipped/not-run均0 |
| 四伴随黄金 CLI | FAIL，exit1，visibility-v1 raw hash和render pin两项不符 |
| 六份黄金 catalog CLI | FAIL，exit1；原样保留行尾导致的raw hash/bytes差异 |
| 内存行尾诊断 | 6/6旧fixture转换为CRLF后hash与length精确匹配catalog pin；未写回文件 |

伴随失败可直接归于候选净diff：visibility-v1.json 源码摘要和render pin被改，raw SHA为1852B55F4EA51F6A7F1A5CED8228D52B2364095CCD31162D2B55015913C58522，141007 bytes；verifier接受的LF SHA为30291EC270EFB6F2A7D8C6984D53E7AE2DBD5AE45A41A7114EE69288749F346E。render期待642cfdda1c11587c63ae9f8587c24c4959f7ed3e8bea5bd51d70a36a7b4cb4b8，实际e8d8f1a543f0554adeed88383060f49ea0392e56e8e8648a0cb7caab1edce502。没有修改verifier、刷新pin、删除断言或覆盖失败。

catalog另有既有checkout行尾敏感性：本机core.autocrlf=false，六份旧fixture Git内容均未改变，catalog冻结raw pin为CRLF，当前工作树为LF。内存计算6/6对应原pin只用于定位，不能把原CLI exit1改成PASS。唯一fixture净改动是visibility-v1。该问题与候选修改冻结visibility的阻断分别记录，完整审查应保留历史来源、真实值级断言与负例，不靠刷新摘要放行。

证据根：D:/majiang-wt/codex-localhost-integration-20261029-0120/build/caller-integration-preflight。含build/runner原始日志、architecture.xml、两份黄金失败日志、catalog-line-ending-diagnosis.json和逐文件manifest。Architecture XML SHA256=4D6AA5D9BC44164C464BED529207EB235511B83A561C198CA116383BDCD21D11；companion.log SHA256=F70A9EFB6D119204E4C90EE23DA2EE494785C3C8E55139042CF6B543BE0EC73F。第一次生成manifest时输出流枚举到manifest自身造成读锁错误；随后明确排除自身、先收集后导出并逐项重验，原测试日志未改。

## 依赖、远端与交接

原PR21整体仍Draft。旧统一Architecture3失败在本次汇合候选130/130中未复现；PG12失败、furiten-read旧exit3和当前visibility完整性阻断仍需完整汇合门禁核验。准备的DeepSeek任务只在独占快照运行完整真实PostgreSQL统一入口、独立OS冷恢复、黄金完整性及跨模块回归，提交审查输入，由caller完整复核；不接管a/b/c/d源代码返修，不复制P4.2-B Unity审查。

本轮仅查询远端元数据：PR20头17808cc的push40730035457和PR40730044657均success；此前3afefd5的PR40729458798为failure，push40729444653为success。尚未下载这些run的完整artifact/XML，本轮不声明已独立认证CI通过，也不把新头绿色当作旧失败已归因。PR21的40732675017/40732456930检查时仍进行中，未等待或盲重试。

完整LocalHost接受与P4.2-B真实工厂激活完成后，才可进入P4.5/G2；P5.4b-A返修验收后才继续真实远端完整集成/G3。P6等待G2+G3，P7等待G4。Unity/Player/Android/iOS/真实设备/跨物理网络/整场本轮NOT_RUN；历史Android失败与后续构建证据分别保留，iOS环境BLOCKED，不扩大为真机通过。

scheduler本轮核实Running、PT30M；complete.flag不存在。没有批准G2–G5、合并到v5或部署。本文只同步既有PR20；本地候选不随本文发布。
