# R6 删除 V3 旧链并完成最终架构收口

仅在 R5 功能验收通过后执行。用户明确无需入口兼容。Anti 实施清理与模块测试迁移；Codex 检视删除范围并独立完成所有验收。

用户现已授权继续本阶段；先读[R5/R6准备审计](R5-R6-PREPARATION-AUDIT.md)的实际保留引用与替代证据清单。engine/access、session、host下部分模型/AI仍为V4所用，不能按目录批量删除；最终门槛还包括完全限定引用、实际类归属及真实依赖图。尚未通过R5前不执行清理。

先逐项核对 [旧链测试保留清单](R6-TEST-PRESERVATION-INVENTORY.md)。已有同名测试不自动等价；特别补齐真实UDP四客户端私密发牌、码牌恢复及终局/资源/暂停时钟恢复。清单仍是源码审计，不是测试通过报告。

## 授权范围

各模块中已被 V4 替代的主源码/接口及相应测试接线，模块 Gradle 最终依赖，运行脚本和非审查工具的失效引用。删除前列出文件、引用方、替代实现和验收，不清理未归属迁移的工作区文件。Codex 审查测试只读，历史文档不批量删除。

## 删除与依赖

- 引用审计删除旧 TCP ClientTransport/NetworkSessionAccess/RemoteSession/MallSession、旧服务端 handler/room/启动路径、旧 protocol codec、V3GameClientAdapter、旧 GameClient/SessionAccess 产品链及不再使用的 PlayerEndpoint/LocalTableRuntime/GameSession 兼容入口。
- 清理旧包、废弃重载、typealias、旧配置入口、只为 V3 留下的辅助器；任何保留项必须证明属于 V4 实际功能，不能以“兼容旧调用者”为由留下。
- engine 主源集只含领域，不依赖 protocol/client/server/libGDX/Gson。protocol 主源集无 engine/client/server/presentation 依赖；client 无 engine/server 实现依赖；presentation.menu/game/common 仅 client.api+自身+展示库。组合根是单独例外，不能扩散到画面包。
- 包扫描使用各模块实际声明的 package 集合，不能只禁根本不存在的 com.majiang.engine 前缀。检查 import、完全限定名及公开方法/属性的递归泛型类型，防 Any/反射或 api 传递依赖绕过边界。
- 清理 Gradle 的旧主源依赖，server 网络联调测试可以 testImplementation client，不能误报为生产反向依赖。纯规则 testFixtures 不依赖新 server。

## 测试与交付

逐项记录旧测试保留/迁移/替代。仅测试旧接口形状的用例可被等价 V4 契约用例替代；不得删除有效规则、守恒、算分、轮次、计时、并发与隐私断言。清除的是废弃路径，不是失败证据。

新增模块架构测试、真实本地/UDP 同契约测试、默认启动链检查、无旧链引用检查。Codex 执行不筛选的全量 test、独立审查回归、桌面编译、Android APK 及真实 GL/UDP 产品检查。未经执行不得写已通过。

交付 delivery-ANTI-R6.md、逐文件清理清单与未完成项。若需要文件系统批量移动/删除，生成 PowerShell 脚本，逐个 Resolve-Path 校验绝对路径位于 D:/majiang，再使用原生 Move-Item/Remove-Item -LiteralPath；由 Codex 审查执行。禁止 git reset/clean、跨 shell 拼删除命令或对未验证目标递归删除。
