# 牌局自动记录与回放

## 使用

每局自动记录默认开启，没有玩家关闭选项。开发时从 Unity 的 Boot 场景启动，进入主菜单后选择“牌局记录”；发布版启动后也从主菜单进入。选中最近一局即可开始回放。回放面板支持自动播放/暂停、单步和 1x/2x/3x 调速；关闭回放返回菜单。记录读取中、空列表和读取失败均在面板显示状态，失败类型和原因同时写入 Unity 日志。

## 记录范围与持久化

当前默认 Unity 产品组合使用进程内 LocalHost。每条已提交的权威牌局事件随 LocalHost 原有存档原子持久化；回放直接读取存档检查点的完整已提交事件历史及其后的事件日志，不重新提交动作、不启动 AI、不运行或重开 deadline。检查点使用现有 `TableCheckpointValidator`，尾日志必须与 durable operations、命令日志及当前状态 high-water 一致。事件序号沿用房间日志，恢复检查点与命令重试沿用原有幂等语义。主菜单最多列出最近 20 个可读本地牌局，日期取存档文件最后写入时间。

存储位于 Unity `Application.persistentDataPath/local` 下的 LocalHost `.save` 存档。默认单局模式只查询 factory 固定的 `local-main` 房间；显式启用的整场模式只查询同一 match 下 `local-main-rN` 轮次房间。不额外复制事件流或维护第二套事件真相。记录键由受信任的 match scope 与检查点中恢复出的真实 `roundId` 构成；同局物理副本按键合并，若提交历史分叉则列表失败并给出诊断。当前稳定本地房间会继续恢复原存档，因此其后续对局仍属于该本地房间记录；删除或覆盖旧存档的策略由 LocalHost 当前生命周期决定。

## 隐私与支持边界

回放投影只输出公开时间线事件和座位；对摸牌只显示动作，不显示牌面。发牌牌面、私有候选、千术私有事实、账本、种子、事件载荷及事件详情不会进入回放 DTO。缺少完整提交历史、尾部事件不达 durable high-water、命令版本/ordinal/binding 不一致或同 round 存档冲突时，不会伪装为完整回放。菜单通过当前 `LocalTableChannelFactory` 的受信任本地 match/room scope 读取；不同 room 和身份范围的记录不会混入列表。当前 LocalHost 默认玩家身份仍是设备内本地座位身份，没有远端账号同步。

远端 TableService 目前没有面向玩家的授权近期列表/回放读取 API；它的事件历史用于服务端恢复和对账，本功能不宣称支持远端牌局。存档损坏、不可读或不完整时，列表/回放显示失败并记录诊断日志；读操作不改写存档，也不参与正在进行牌局的权威裁决。

LocalHost API 和 Unity App 源码变更后，Unity 插件 DLL 需按[共享 DLL 构建说明](BUILD_AND_PACKAGE.md#net-编译与共享-dll)从源码重新生成，并使用 `tools/unity/check-shared-plugins.ps1` 核对插件来源。当前 Unity 支持范围是 LocalHost 单局与显式 opt-in 的 LocalHost 整场轮次；远端牌局未接入该读取能力。
