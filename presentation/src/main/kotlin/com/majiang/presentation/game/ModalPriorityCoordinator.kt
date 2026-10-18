package com.majiang.presentation.game

/**
 * 表现层模态类别及其显式优先级。
 * 数值越高代表模态显示与输入消费的优先级越高。
 */
enum class ModalKind(val priority: Int) {
    NONE(0),
    MELD(10),
    RIICHI(20),
    BEAM_SWAP(30),
    SLEEVE_BLOOM(40),
    TILE_MARK(50),
    SHUFFLE_PREPARATION(60),
    SETTLEMENT(70),
    FINAL_RESULT(80);
}

/**
 * 模态状态快照，用于捕获输入事件处理前后的模态状态，检测失效与输入穿透。
 */
data class ModalStateSnapshot(
    val isFinalResult: Boolean,
    val isSettlement: Boolean,
    val isShufflePreparation: Boolean,
    val isTileMark: Boolean,
    val isSleeveBloom: Boolean,
    val isBeamSwap: Boolean,
    val isRiichi: Boolean,
    val isMeld: Boolean,
    val activeModal: ModalKind
)

/**
 * ESC / BACK 快捷返回路由。
 * 由业务调用方（如 GameScreen）提供具体的业务关闭或千术保留/放弃请求逻辑，
 * 严格禁止使用通用无害关闭替代千术的特定动作请求。
 */
data class ModalEscapeRoutes(
    val onDismissBeamSwap: () -> Unit,
    val onDismissSleeveBloom: () -> Unit,
    val onDismissRiverHint: () -> Unit,
    val onDismissTileMark: () -> Unit,
    val onDismissMeld: () -> Unit,
    val onDismissRiichi: () -> Unit,
    val onDismissSettlementToFinalResult: () -> Unit,
    val onExitToMenu: () -> Unit
)

/**
 * 显式模态优先级协调器 (ModalPriorityCoordinator)。
 *
 * 核心职责：
 * 1. 模态优先级裁决：统领终局总成绩 (FINAL_RESULT)、单局结算 (SETTLEMENT)、码牌准备 (SHUFFLE_PREPARATION)、
 *    三大千术 (TILE_MARK, SLEEVE_BLOOM, BEAM_SWAP) 与动作选牌 (RIICHI, MELD) 的显示和输入优先级；
 * 2. 状态查询与遮罩拦截：判定是否有任意模态激活 (isModalActive)、当前最高优先级活跃模态 (activeModal)，
 *    以及是否阻断主牌桌/提示层交互 (shouldBlockGameplayInput, shouldSuppressRiverHint)；
 * 3. 模态互斥与准入检测 (canOpen)：根据当前模态互斥规则裁决新模态是否被允许开启；
 * 4. 输入时序与失效防护 (captureSnapshot / wasModalInvalidated)：
 *    在输入事件触发前捕获快照，在状态刷新后检测活跃模态是否已被超时/断线等机制失效，
 *    若失效则拦截点击防止穿透到底层牌桌或普通操作；
 * 5. ESC / BACK 路由裁决 (handleEscapeOrBack)：严格依照优先级执行调用方提供的针对性业务关闭，
 *    确保千术退出时仍能发送合法的保留/放弃请求。
 */
class ModalPriorityCoordinator(
    val isFinalResultActive: () -> Boolean,
    val isSettlementActive: () -> Boolean,
    val isShufflePreparationActive: () -> Boolean,
    val isTileMarkActive: () -> Boolean,
    val isSleeveBloomActive: () -> Boolean,
    val isBeamSwapActive: () -> Boolean,
    val isRiichiActive: () -> Boolean,
    val isMeldActive: () -> Boolean,
    val isRiverHintOpen: () -> Boolean = { false },
    val isMatchFinished: () -> Boolean = { false }
) {
    /**
     * 当前处于最高优先级的活跃模态。若无模态激活则返回 ModalKind.NONE。
     *
     * 优先级顺序（自高至低）：
     * FINAL_RESULT > SETTLEMENT > SHUFFLE_PREPARATION > TILE_MARK > SLEEVE_BLOOM > BEAM_SWAP > RIICHI > MELD
     */
    fun activeModal(): ModalKind = when {
        isFinalResultActive() -> ModalKind.FINAL_RESULT
        isSettlementActive() -> ModalKind.SETTLEMENT
        isShufflePreparationActive() -> ModalKind.SHUFFLE_PREPARATION
        isTileMarkActive() -> ModalKind.TILE_MARK
        isSleeveBloomActive() -> ModalKind.SLEEVE_BLOOM
        isBeamSwapActive() -> ModalKind.BEAM_SWAP
        isRiichiActive() -> ModalKind.RIICHI
        isMeldActive() -> ModalKind.MELD
        else -> ModalKind.NONE
    }

    /**
     * 是否存在任意活跃模态。
     */
    val isModalActive: Boolean
        get() = activeModal() != ModalKind.NONE

    /**
     * 判定指定模态是否正处于当前最高优先级活跃状态。
     */
    fun hasModal(kind: ModalKind): Boolean = activeModal() == kind

    /**
     * 判定是否应阻断主牌桌操作（出牌、托管、普通鸣牌/立直等）。
     */
    fun shouldBlockGameplayInput(): Boolean = isModalActive

    /**
     * 判定是否应抑制/关闭牌河辅助提示面板。
     */
    fun shouldSuppressRiverHint(): Boolean = isModalActive

    /**
     * 判定目标模态是否允许打开（即当前没有任何活跃模态或互斥冲突）。
     */
    fun canOpen(target: ModalKind): Boolean {
        if (isFinalResultActive() || isSettlementActive() || isShufflePreparationActive()) {
            return false
        }
        return when (target) {
            ModalKind.TILE_MARK -> !isRiichiActive() && !isMeldActive() && !isSleeveBloomActive() && !isBeamSwapActive() && !isTileMarkActive()
            ModalKind.SLEEVE_BLOOM -> !isRiichiActive() && !isMeldActive() && !isTileMarkActive() && !isBeamSwapActive() && !isSleeveBloomActive()
            ModalKind.BEAM_SWAP -> !isRiichiActive() && !isMeldActive() && !isTileMarkActive() && !isSleeveBloomActive() && !isBeamSwapActive()
            ModalKind.RIICHI -> !isMeldActive() && !isTileMarkActive() && !isSleeveBloomActive() && !isBeamSwapActive()
            ModalKind.MELD -> !isRiichiActive() && !isTileMarkActive() && !isSleeveBloomActive() && !isBeamSwapActive()
            ModalKind.SHUFFLE_PREPARATION -> !isModalActive
            ModalKind.SETTLEMENT -> true
            ModalKind.FINAL_RESULT -> true
            ModalKind.NONE -> true
        }
    }

    /**
     * 捕获当前所有模态的瞬时激活快照。
     */
    fun captureSnapshot(): ModalStateSnapshot = ModalStateSnapshot(
        isFinalResult = isFinalResultActive(),
        isSettlement = isSettlementActive(),
        isShufflePreparation = isShufflePreparationActive(),
        isTileMark = isTileMarkActive(),
        isSleeveBloom = isSleeveBloomActive(),
        isBeamSwap = isBeamSwapActive(),
        isRiichi = isRiichiActive(),
        isMeld = isMeldActive(),
        activeModal = activeModal()
    )

    /**
     * 检查先前捕获为活跃的模态是否在本次刷新中被失效（例如权威超时或断线关闭导致面板退选）。
     * 若失效，应拦截本次点击以防止意外穿透到底层牌桌。
     */
    fun wasModalInvalidated(before: ModalStateSnapshot): Boolean {
        return (before.isMeld && !isMeldActive()) ||
            (before.isRiichi && !isRiichiActive()) ||
            (before.isTileMark && !isTileMarkActive()) ||
            (before.isSleeveBloom && !isSleeveBloomActive()) ||
            (before.isBeamSwap && !isBeamSwapActive())
    }

    /**
     * 处理 ESC / BACK 按键输入。
     *
     * 依照显式优先级路由调用方提供的专属业务处理：
     * 1. 偷梁换柱二选一模态 -> 提交保留原牌请求 (KEEP_ORIGINAL)；
     * 2. 袖里藏花二选一模态 -> 提交保留第一张请求 (FIRST)；
     * 3. 牌河提示展开面板 -> 关闭提示面板；
     * 4. 落汗选牌模态 -> 取消选牌；
     * 5. 副露选牌模态 -> 取消选牌；
     * 6. 立直选牌模态 -> 取消选牌；
     * 7. 结算模态且整场对局已结束 -> 切换至终局总成绩模态；
     * 8. 默认情况 -> 退出牌桌返回主菜单。
     *
     * @return 恒返回 true 表示按键已被消费。
     */
    fun handleEscapeOrBack(routes: ModalEscapeRoutes): Boolean {
        if (isBeamSwapActive()) {
            routes.onDismissBeamSwap()
            return true
        }
        if (isSleeveBloomActive()) {
            routes.onDismissSleeveBloom()
            return true
        }
        if (isRiverHintOpen()) {
            routes.onDismissRiverHint()
            return true
        }
        if (isTileMarkActive()) {
            routes.onDismissTileMark()
            return true
        }
        if (isMeldActive()) {
            routes.onDismissMeld()
            return true
        }
        if (isRiichiActive()) {
            routes.onDismissRiichi()
            return true
        }
        if (isSettlementActive() && isMatchFinished()) {
            routes.onDismissSettlementToFinalResult()
            return true
        }
        routes.onExitToMenu()
        return true
    }
}
