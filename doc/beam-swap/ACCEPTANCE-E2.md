# BS-E2 独立验收

2026-10-13，Codex。Anti已提交`CALLBACK-ANTI-E2.json`并退出；本结论仅覆盖协议、宿主、客户端阶段，完整功能仍待E3。

## 独立执行

`./gradlew :protocol:test :client:test :server:test :engine:test --rerun-tasks --no-build-cache`

退出码0，BUILD SUCCESSFUL，18项Gradle任务全部执行。engine 471、protocol 172、client 146、server 151，共940项测试，0失败、0错误、0跳过。日志：`build/beam-swap-codex-e2.log`；计数：`build/beam-swap-codex-e2-results.json`。

E2新增33项由Codex编写：协议14、客户端10、宿主7、真实UDP集成1、UDP观战隔离1。测试文件哈希未被Anti修改；Codex修正过宿主测试费用事件订阅夹具，未放宽生产公开投影。`git diff --check`通过。

## 检视与修复闭环

- 候选数组null元素不再绕过适配器；slot必须为非空对象，盲牌不携带实体或牌面，严格请求分支与互斥载荷保持。
- 新beam路径缓存首次权威拒绝的完整回执，重放保持原cursor；不同载荷的REQUEST_ID_CONFLICT不命中或污染原回执。连接绑定校验在串行队列内先执行。
- 本地及UDP大厅/会话能力声明补全；模型缺失能力默认false保持。
- 宿主状态比较包含beam视图，无领域事件的KEEP、交换或清理也会推送。
- 客户端两入口共用在途锁，断线、关闭、重绑定会结束旧future；不乐观修改扣费与选择状态。
- 本人私有候选、其他座位与公开投影隔离；真实UDP验证激活、替换连接恢复、交换、推送和重放。观战两入口均拒绝。

交付报告中错误码、映射方法名及默认接口实现的表述已由Codex按源码校正。未发现阻止进入E3的问题。E3的UI、产品启用、桌面/Android构建和GL验收尚未完成。
