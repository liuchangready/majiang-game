# 千术麻将开发入口

当前产品实现为 Unity + C#/.NET（Unity 6000.3.27f1，.NET 8）。旧 Kotlin/libGDX 七模块和 Gradle 入口已从当前树移除；历史报告中的代码/命令仅描述当时状态。当前 V5 G2 尚未验收。

- 架构与边界：[doc/CURRENT_ARCHITECTURE.md](doc/CURRENT_ARCHITECTURE.md)
- 玩法和规则：[doc/GAMEPLAY_RULES.md](doc/GAMEPLAY_RULES.md)
- 构建与出包：[doc/BUILD_AND_PACKAGE.md](doc/BUILD_AND_PACKAGE.md)
- 测试覆盖与缺口：[doc/TESTING.md](doc/TESTING.md)

当前验证统一入口为 `tools/verify-v5.ps1`。修改 `dotnet/` 后先运行 `tools/unity/build-shared.ps1` 并核对插件来源，再使用 Unity Editor/Player 工具。运行真实命令和工具路径前，先阅读构建与测试说明。

规则合同仍包含 42 个稳定役种码、136 张物理实体、三大绝技（落汗、袖里藏花、偷梁换柱）及开局码牌；不得因清理旧平台改写规则或弱化既有探针。
