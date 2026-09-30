# 开发前验收基线

2026-09-29，Codex 编写并执行 ArchitectureSafetyRegressionTest。

命令：`./gradlew :core:test --tests '*ArchitectureSafetyRegressionTest' --console=plain`

结果：11 项，1 项通过、10 项失败、0 错误。唯一通过项为正常发牌的独立 136 张枚举；失败项为实际丢牌/重复牌审计、归属记录、非法重复摸牌、窗口外 Pass、局中重开、弃牌伪造、资金守恒、供托清零及重复和了。

这是修复前的预期 RED，不表示新增功能通过。旧基线在上一轮完整执行为 80 项通过。

留存：

- `build/architecture-v2/baseline-tests.xml`
- `build/architecture-v2/baseline-tests.log`
- `build/architecture-v2/pre-development-baseline.zip`（开发派发前源码及文档备份）

后续验收必须保留这些行为断言；允许因公开 API 重设计由 Codex 迁移夹具和调用方式，不允许实现者改弱测试。
