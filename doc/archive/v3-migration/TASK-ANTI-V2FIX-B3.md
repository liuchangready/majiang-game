# B3 三个实际失败，精确小批返修

上一批B2再次CLI超时，仅部分改动 GameScreenTest，且引入不存在的 addSnapshotListener。此次只改 GameScreenTest.kt、MeldChoiceScreenContractTest.kt、RiichiPacingAcceptanceTest.kt；不要修改其它文件，不读超过所需定义，不调查旧架构。

1. GameScreenTest：删除三个 addSnapshotListener 及其future订阅等待；本地 GameClient 默认dispatch立即执行，TableHost 操作同步完成，点击start后 flushUi 即可获得当前snapshot。submitActionAsync().get(3,SECONDS) 后 flushUi。flushUi 禁止 catch 吞异常，两层 postRunnable barrier即可。回执可能是 RECORDED：TableHost:972 响应窗口未决返回RECORDED。应 assertNotEquals(REJECTED,receipt.status)，同时保留13张闭手、牌河+1和实体Tile全等断言，不只检查回执。
2. MeldChoiceScreenContractTest:128 过期候选失败：stale分支 base.update(latest)主动推送更新，提前退出了选择，confirm点击被拒，和“silently stale”原意矛盾。该分支只修改装饰器的latest，不广播base.update；让确认时屏幕重新读client.snapshot识别过期。拒绝与过期均保留尝试次数、无提交、退出选择断言。flush显式输入后，无sleep。
3. RiichiPacingAcceptanceTest最后E2E在199行 steps++<80 超限（v2-core-pass3.log）。请定位fixture的AI策略、host clock、controllers和draw/pass，保留8次按0,1,2,3顺序、动画busy不推进、队列无预计算断言。当前passAndDiscard没有 DrawTile 会卡在DRAW，请核对合法actions并按真实V3需要补合法摸牌fallback；旧coordinator eager链自动摸牌，V3每步是显式合法动作。不能延长超时凑数，不放宽断言，不清动画。其他6个测试保留。

立即逐文件保存，禁止shell。报告delivery-ANTI-V2FIX-B3.md。
