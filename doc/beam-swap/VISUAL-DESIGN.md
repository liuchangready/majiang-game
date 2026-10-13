# 偷梁换柱界面实施稿

2026-10-13，Codex在UI开发前固定交互和验收边界。实现依据[领域设计](DESIGN.md)与[接口约定](INTERFACES.md)。现已实现并通过双视口桌面GL，截图及验证边界见[最终验收](ACCEPTANCE.md)；Android真机触摸未验证。

## 牌桌入口整体布局

统一使用现有1280×720逻辑坐标和Viewport映射。右侧操作列统一宽140、高44、纵向间隔6，x=1080：从下往上托管y=6、理牌y=56、袖里藏花y=106、偷梁换柱y=156、落汗y=206。五个矩形必须集中定义或复用同一布局，不能由各面板独立添加相互覆盖的按钮。桌面960×540按viewport等比缩放，点击经过unproject，触摸与鼠标共用命中区域。

偷梁换柱按钮文案「偷梁换柱 · 10」，权威canApply、连接、本人控制、暂停、共享isPending及其他模态共同决定能否点击。旧服务能力缺失或房间关闭该功能时不提供可点击入口。其他模态打开时所有千术入口禁用。

## 选择面板

全桌半透明遮罩，中心面板逻辑矩形(110,115,1060,510)。标题「偷梁换柱」，副标题「选一张原有暗手牌，再选一个目标槽位」，右上显示权威剩余秒数。说明固定显示「已花费10点；保持原样不退还」。

- 候选区 y=395..485：最多13张，单张52×72，间隔18；整排宽892，居中。普通牌山按顺序标「下1…下8」。死山槽标「岭上1…4」「暗区5…14」「替补1…4」，以实际绝对槽索引生成，必要时小号字体，不暴露牌面。暗区中的已翻宝牌不出现。可按类型使用短分组标题，但不得增加出界的行。
- 手牌区 y=245..335：仅绘制offer.selectableHandTileIds对应的原有暗手牌，单张52×72、间隔10，最多13张；不把当前摸牌或任何副露纳入可选矩形。旁文「刚摸／刚补牌及副露不可交换」。牌桌原始手牌区域在模态期间整块锁定。
- 底部y=155..203：左侧「保持原样」(300,155,200,48)，右侧「互换」(780,155,200,48)。保持原样无需选择；互换要求手牌和目标各选一项。选中项用描边和轻微上浮区分，鼠标与触摸都采用点击单选；不得依赖悬停操作。
- 未标记目标始终纯牌背，悬停、辅助文字和确认文案不泄露tileId或牌名。仅本人有效落汗命中的候选绘制半透明牌面（复用现有落汗透明度）与汗滴标识；亮面不改变可点击区域。

所有元素含文字应落在面板内。两种视口必须验证13候选与13暗手牌并存，不能只用普通8张窗口作为最大布局验收。具体字体尺寸可沿用现有资源，但应补齐新增中文字形。

## 本地选择与生命周期

新建独立BeamSwapChoiceModel与BeamSwapPanel，presentation仅使用client.api。模型契约：update(snapshot, connected, isPending, nowMillis)、canActivate、isChoosing、canConfirm、selectedHandTileId、selectedTargetSlot、remainingMillis、selectHand(tileId)、selectTarget(slot)、buildActivateRequest(requestId)、buildChoiceRequest(requestId, action)。构造器可注入单调时钟，与SleeveBloomChoiceModel一致。请求由当前权威快照身份生成，绝不保存旧请求待恢复后补发。

面板提供可检验的当前布局：btnOpenRect、btnConfirmRect、btnKeepRect、handCardRects: Map<Int, Rectangle>、targetCardRects: Map<BeamSwapSlot, Rectangle>、isDisposed；GameScreen.beamSwapPanel可访问。矩形在快照update后即可查询，不依赖实际GL render调用，保证无头输入测试和真实渲染使用相同命中区。布局读者不得修改面板内部状态。

局、session、syncEpoch、decision、controlEpoch、offerId、可选手牌或候选内容任一变化清空两个选择。同一个快照逐帧更新不得延长倒计时。截止后禁止提交但维持模态占用，等待权威超时推送关闭；暂停、断线、托管隐藏并清空选择，恢复时从当前offer重建。共享在途锁期间保持面板可见、全部提交禁用。

面板注册isModalActive；普通手牌、理牌、落汗、鸣牌、立直、袖里藏花、快捷键、触摸拖动均不得穿透。Esc/返回键等同请求保持原样，不能仅本地关闭。重复输入由共享isPending门控；成功与拒绝回执均以最新快照决定面板是否仍存在。拒绝原因走中文toast。交换成功后可对原目标槽短暂高亮去向，不附加隐藏实体信息。

## 验收

Codex编写模型和Screen集成测试，Anti实现后先运行通过再回调。真实GL冒烟覆盖1280×720与960×540、13候选、盲选和标记亮面、合法双选后提交、保持原样、普通手牌输入锁及超时关闭，保存截图。Android需构建APK；未做真机触摸验证时明确记录，不能以桌面触摸事件或APK构建替代真机结论。
