# 袖里藏花执行状态

2026-10-13：岭上候选规则修订已完成，Codex独立验收通过，详见[当前验收](ACCEPTANCE-RINSHAN.md)。

- 最新规则：杠后只在原岭上补牌与下一岭上牌中二选一，选第二张时原牌写回下一岭上槽；不动普通牌山、替补列表或指针。第四次杠补牌窗口禁止施术且不扣费。
- 分工：Anti修改生产代码并执行开发验证；Codex编写/维护测试、修正文档并独立检视。Codex独立复测1115项全过，0失败/错误/跳过；桌面classes、Android APK、1280/960 GL通过。35项领域专项覆盖本次规则，新增5项；Android实体机触控未验证。
- 首轮测试夹具时钟遗漏已由Codex修正，断言未放宽，见[反馈](FEEDBACK-RINSHAN-INITIAL.md)。Anti网络错误后按用户要求续接原会话，已完成开发并回调。
- 下发包含 --mode accept-edits --dangerously-skip-permissions；完成回调见[SB-RINSHAN](CALLBACK-ANTI-RINSHAN.json)。
- 初版1110项、双端构建和GL历史证据见[初版验收](ACCEPTANCE.md)，不作为本次修订的通过记录。
- 本次提交包含岭上候选规则修订的代码、测试和文档。用户原有 lwjgl3/build.gradle、SnapshotLauncher.kt、snapshot/ 改动保留在工作区，不纳入提交。
