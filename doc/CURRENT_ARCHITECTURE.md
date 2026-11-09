# 当前整体架构

核对日期：2026-11-09；清理前代码快照：`e074806a39a674b898120c828038efcd1b5c58d7`。本文描述清理后的当前实现边界，V5 G2 尚未验收。原目标方案见 [V5 架构设计](v5-重构/V5-ARCHITECTURE-DESIGN.md)；原方案中的“目标/未实现”文字不能作为当前交付结论。

## 技术与运行入口

`tests/v5-golden/catalog-v1.json` 是早期六组 fixture 的历史索引，保留原始摘要且不再作为当前门禁；当前黄金来源由 `source-provenance-v1.json` 和 companion verifier 校验，行为由现有 C# golden 测试校验。

- 当前开发客户端：Unity `6000.3.27f1`，项目 `unity/Mahjong.Unity`。
- 共享核心：C#，`.NET Standard 2.1` / C# 9；服务与 .NET 测试使用 `.NET 8`。
- Unity 通过 `Assets/Plugins/Mahjong/` 下的共享 DLL 消费规则和客户端；修改 C# 核心后按[构建说明](BUILD_AND_PACKAGE.md)同步。
- 本地对局：Unity 组合根装配进程内 LocalHost，使用真实客户端会话、命令、投影和回执。
- 服务端：LobbyService / TableService、HTTPS/WSS 接入及 PostgreSQL 持久化已有代码与组件验证。Unity 当前默认工厂接入本地对局，不代表完整公网游戏链路已验收。
- 旧 Kotlin/libGDX 七模块、Gradle wrapper 和旧启动器已从当前树删除。V5 C#/.NET 是当前唯一可构建/运行实现；历史 Kotlin 源码身份仅记录在 `tests/v5-golden/source-provenance-v1.json`，验证不依赖历史源码或 Git 历史。
- 删除 V4 源码不表示 V5 平台或 G2 验收通过，也不表示 V5 已上线。历史迁移报告保留原意，按其记录的旧源码身份理解。

## 模块职责与依赖

| 目录 / 工程 | 当前职责与边界 |
|---|---|
| `dotnet/src/Mahjong.Domain` | 136 张实体牌、规则/算分、单局和整场、三绝技及账本；纯规则，不依赖 Unity、网络或数据库 |
| `Mahjong.Contracts` / `Mahjong.Contracts.Wire` | 命令、回执、投影及 protobuf 线路契约；领域对象不直接下发 |
| `Mahjong.TableRuntime` | 每桌串行权威、决策/超时/控制代次、投影及持久化契约；依赖 Domain / Contracts |
| `Mahjong.Ai` | 基于授权视图的决策，不直接改写权威状态 |
| `Mahjong.Client` | TableSession、命令/回执、未决请求与 outbox、视图绑定及本地/WSS 通道；业务门面依赖 Contracts |
| `Mahjong.LocalHost` | 进程内宿主、本地房间、存档恢复与本地整场；复用 TableRuntime |
| `Mahjong.LobbyService` / `Mahjong.TableService` | 大厅与远端对局服务，身份/票据、接入和权威服务装配 |
| `Mahjong.Infrastructure` | PostgreSQL、持久回执、检查点、事件和结算 outbox 等基础设施 |
| `unity/Mahjong.Unity/Assets/Scripts` | App 组合根、UI、Table、Spectator、Platform；业务画面读取 Client 视图与门面，宿主由 App 装配 |

## 一次玩家动作的数据流

```text
输入 / Presenter
  → 客户端命令意图与 SessionCoordinator / TableSession
  → ITableChannel（本地通道；远端实现另有 WSS）
  → LocalHost 或 TableService
  → TableRuntime 串行裁决 → Domain
  → 持久化结果、请求回执及按身份投影
  → Client 接收、绑定与缓存 → UI / 动画
```

同一请求的回执、事件和后态必须绑定同一局、身份、版本及作用域；后台继续推进时不能用“最新快照”替代目标提交状态。动画不接管权威 deadline。控制权变更后旧玩家/AI 决策失效，重连后须显式收回。

## Unity 场景

Build Settings 启用 6 个场景：`Boot`（初始化）、`Menu`（菜单）、`Lobby`（大厅）、`Table`（对局）、`Spectator`（观战）、`Settlement`（结算）。

工程另有 `SampleScene`、`Prototypes/V4UiPrototype`、`Settings/Scenes/URP2DSceneTemplate`，共 9 个场景文件。V4UiPrototype 名称保留为 Unity 当前导入/预览资源，不是旧 Kotlin 运行实现。碰、吃、立直、杠共用 Table，通过不同权威牌局状态呈现。

## 指定状态与当前限制

已有固定初态 `pon`、`chi`、`riichi`、`closed-kan`、`dealer-tsumo`，以及三绝技、隐私、控制权、恢复专项探针。动作初态由 LocalPlayerActionScenario 构造完整合法牌山，前置动作经过领域规则，待测动作走真实客户端通道。

这些是显式启用的测试入口，Player 探针通常自动执行、取证并退出；当前没有统一的手动选局面、暂停计时/AI、任意跳转状态的调试面板。UI 原型预览不等于真实牌局。

## 当前验收边界

V5 G2 整体验收仍未通过。Windows 已有组件与专项 Player 证据，仍缺完整玩法分支、真实断线接管、物理输入/显示矩阵及最终同候选同包覆盖；Android 已有构建和模拟器安装证据，不能据此判定玩法、触屏、生命周期或真机通过。跨物理网络、iOS 真机及完整公网运营能力不得从组件测试推断。具体用例和证据口径见[测试说明](TESTING.md)。
