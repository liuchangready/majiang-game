# V5 Android 私有工具链与 Wire AOT 探针独立验收

日期：2026-10-28（Asia/Shanghai）。结论：**APPROVED，仅本工具链组件范围**。

任务 `thread_20261027_194656_c897` 第 2 轮，接受
`0e7dd70931024f66fe5bdf5c262469b1199bb043`，冻结起点
`36014038bcbe61f7b9acdd4fad4694f329bbd4d2`。
Caller 已执行 approve_task，桥接返回 COMPLETED。

## 实现与关闭项

工具把 Unity Android CLI 的只读发现请求转给现有 sdkmanager 16.0，透传真实输出和退出码；
官方 Ninja 1.13.2 经 ZIP/EXE 双 SHA 校验后仅置于任务私有 build 目录，进程环境负责选择工具。
共享 Editor、SDK、JDK、全局 PATH、注册表和 EditorPrefs 不作修改。
原始 Android CLI 崩溃与缺 Ninja 的失败证据保留。

- D-R1-01 关闭：repair runner 合并进程等待与输出收集 deadline，未收齐输出明确超时失败；
  清理校验 PID/creation 实例，未知或不匹配身份保留，移除 taskkill /T；正式测试加载实际生产函数。
- D-R1-02 关闭：范围守卫读取 porcelain -z，rename/copy 同时检查源和目标；
  已提交差异使用 --no-renames，防止丢失源路径。Legacy profile 的限制保留。
- D-R1-03 关闭：测试回到授权路径 tools/tests/VerifyV5AndroidToolchain.Tests.ps1。
  冻结到接受候选只有四个 expectedFiles，tracked clean、diff-check exit 0。

## Caller 实际验证

源任务树 `D:/majiang-wt/android-toolchain-ds-20261027`：

| 验证 | 结果 |
| --- | --- |
| 正式脚本测试（含真实 CheckOnly、CLI、CMake/Ninja、APK 解析） | 55/55，exit 0 |
| 原进程探针，断言不变 | 5/5，exit 0 |
| 原范围探针，断言不变 | 3/3，exit 0 |
| 补充实际 Invoke-GitRaw -z + 真实独立 Git fixture | 3/3，exit 0 |
| 当前交付 HEAD 的真实 wrapper .NET 入口 | guard/build/run/依赖闭包 PASS，18 messages / 1417 bytes |

正式测试 fail/skipped/NOT_RUN 均 0；真实 CheckOnly 退出 0、共享指纹变化 0。
wrapper 使用 -Target DotNet -SkipEditor -TaskProfile AndroidToolchain；总退出码 2 仅因 iOS BLOCKED，
不把总退出码写成全通过。其新运行目录为
build/android-toolchain-repair/recheck/20261028-025347-eb83688f9509。

源树 caller 证据：build/codex-android-review-r2-0e7dd70，SHA manifest：
`43A29319865A9020E8152CBB5D3B4C10710386B8AFBA349C9D7C9354E4F39DFA`。
正式套件证据：build/android-toolchain-tests/20261028-025122-c775312ca734。

补充 raw Git 探针初版未逐 case 重置 fixture，错误拒绝 allowed 控制；修正仅涉及 caller fixture，
断言未放松，初版源码/JSON/日志保留。R1 失败 JSON 曾被 receiver 重放脚本覆盖，
原 caller 回执 SHA 与失败日志仍保留，不把重放 JSON 冒充原失败证据。

## 原真实 Android 构建证据

独立重验原产物 SHA 与日志、JSON、APK 内容一致：

- APK 33,978,023 bytes，SHA256
  `F03A13B4573DD5AABEFAD24B6B83440B34E34ED342B6873287A64C6127308019`。
- aapt2 解析 com.mahjong.aotsmoke / arm64-v8a；ZIP 内有 ARM64 libil2cpp.so 与 global-metadata.dat。
- 原 Unity build native exit 0，marker target=Android/backend=IL2CPP/result=Succeeded/errors=0。
- 原 Unity 日志 SHA256
  `95DB618BE28B53D4DD833EC38A36173E8B4196FBE3F16619A81F3847316458A1`；
  summary SHA256 `7BB88E35AE222D85AA96690C5748FC3EFF137C1A6904D7857FC5CE365A86DCC1`。

原运行在任务树 build/android-toolchain-repair/recheck/20261028-013201-d856a5bc11f6。
R2 仅改 helper/guard/测试路径，Unity probe/build 逻辑未变；本轮未重建 APK，未把旧构建当作 fresh。
该 APK 是精简 Wire AOT 探针；完整游戏 Android 构建/整场端到端、真机运行不在通过范围。
Android runtime NOT_RUN；iOS BLOCKED/NOT_RUN。

## PR20 集成

隔离树 D:/majiang-wt/codex-android-accepted-20261028 从 PR20 已接受
`c7001a8395ceb5016ef0e57721f4ea2424feef8e` 建立；merge 完整接受候选形成
`bd5d453bb130e9d8e58165663a312f5eba002f91`。
保留此前 Client 事件供给及原生 CI 退出码修复；dotnet、unity、tools/ci、统一验证入口和工作流零改动。
三份工具/测试源码与接受候选逐文件零差异，未纳入其它在途任务。

集成树先校验并复制已 pin 的 Ninja EXE 到自身 ignored build，再实际运行 repair，
生成该树独立 manifest/CLI 适配器：23 checks、failures=0、blockers=0、native exit 0，
8 个共享工具指纹不变。随后完整测试再次 55/55，exit 0，fail/skipped/NOT_RUN 均 0。
APK 解析使用上述原已验证产物，不宣称集成树重新构建 Android。

集成证据：build/codex-android-integrated；
其 evidence-sha256.csv SHA256 为
`3CC844FB9E3C0EBEFFC0C9C9CDA21B5241FCF65D47D32157E00564049921632E`；
repair 运行 build/android-toolchain-repair/20261028-025549-8fe0ddc2f9a3；
正式测试 build/android-toolchain-tests/20261028-025603-426c74d0a158；
其 CheckOnly build/android-toolchain-repair/20261028-025624-431f1732fd69。

复现：在对应树运行 repair-android-toolchain.ps1，再执行
tools/tests/VerifyV5AndroidToolchain.Tests.ps1 -ApkPath 上述 APK 绝对路径。
工具包、APK 与原始日志均不入 Git。源码允许范围和 guard 证据在交付报告及 caller 原始日志。

更新唯一草稿 [PR20](https://github.com/liuchangready/majiang-game/pull/20)，目标 v5，
不创建组件 PR、不合并到 v5、不部署、不改 scheduler 或创建 complete.flag。
新提交远端 CI 待另行核实；保留 467b64b 两次 2286/2286 的历史证据和原 b5c1c17 的原始失败记录。
G1 APPROVED，G2–G5 未批准；V4 继续生产。
