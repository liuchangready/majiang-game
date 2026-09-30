package com.majiang.application

import com.majiang.state.ActionResult
import java.util.concurrent.ConcurrentHashMap

/**
 * 请求回执（记录客户端提交的选择载荷与返回结果，支持幂等重放与冲突检测）
 */
data class ChoiceReceipt(
    val choicePayload: ActionChoice,
    val result: ActionResult
)

/**
 * 客户端选择请求幂等注册表
 *
 * 作用域限定在各个座位 (0..3)，保证同一个 requestId 在相同载荷下幂等返回原回执，
 * 在不同载荷下能够识别并拒绝冲突请求。
 * 存储与返回均进行深拷贝，消除外部可变引用泄露。
 */
class RequestRegistry {
    private val registries = Array(4) { ConcurrentHashMap<String, ChoiceReceipt>() }

    fun getReceipt(seat: Int, requestId: String): ChoiceReceipt? {
        if (seat !in 0..3) return null
        val receipt = registries[seat][requestId] ?: return null
        return ChoiceReceipt(
            choicePayload = receipt.choicePayload.copy(),
            result = receipt.result.deepCopy()
        )
    }

    fun registerReceipt(seat: Int, choice: ActionChoice, result: ActionResult) {
        if (seat !in 0..3) return
        registries[seat][choice.requestId] = ChoiceReceipt(
            choicePayload = choice.copy(),
            result = result.deepCopy()
        )
    }

    fun clear() {
        for (map in registries) {
            map.clear()
        }
    }
}
