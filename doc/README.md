# 千术麻将文档

当前唯一可构建和运行的产品实现为 **V5 Unity + C#/.NET**。V5 G2 尚未验收；移除旧 Kotlin/libGDX 源码不构成平台验收或上线结论。

## 当前入口

- [当前架构](CURRENT_ARCHITECTURE.md)：Unity 客户端、共享 C# 核心、.NET 服务和权威边界。
- [玩法与规则](GAMEPLAY_RULES.md)：规则合同、42 个役种稳定码、136 张实体、三绝技与码牌约束。
- [构建与出包](BUILD_AND_PACKAGE.md)：当前 .NET、共享 DLL、Unity Windows/Android 构建入口。
- [测试与覆盖](TESTING.md)：当前 .NET/Unity 验证、覆盖口径和未完成项。
- [V5 规则合同](v5-重构/V5-0-RULE-CONTRACT.md)：版本化规则语义及当前 C# 实现边界。

## 设计与历史记录

下列 V4 架构、迁移、验收及阶段报告保留其编写时的原意，只作为历史材料阅读。它们提到的 Kotlin 源码、Gradle、七模块 libGDX 工程和可靠 UDP 不属于当前构建或运行入口：

- [V4 架构设计](GAME_ARCHITECTURE_V4.md)
- [V4 迁移验收记录](v4-migration/ACCEPTANCE-R6.md)
- [V4 迁移合同](v4-migration/CONTRACT.md)
- [历史归档目录](archive/)

视觉原型 [ui_demo](../ui_demo/README.md) 是独立静态 HTML 展示，不是产品客户端。素材说明以各资源目录的当前用途和 Unity 导入配置为准；历史 V4 接入记录不会要求恢复旧运行链。
