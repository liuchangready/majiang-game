# I — 两个旧记录帮助函数的精确修复

只修改 core/src/main/kotlin/com/majiang/screen/GameScreen.kt 中 legacyYakuRows 和 wrapLegacyText，不改其他文件/测试/构建。其他所有任务已停止编辑。只用内置文件读取/编辑，不运行任何命令、不调用代理/MCP、不读无关文档。

H 交付已实际编译失败：GameScreen.kt:144 this::game.isInitialized 不能用于普通val；game::font.isInitialized 的 backing field 不可访问。try/catch 不能解决编译错误。

1. legacyYakuRows 完全去掉 this::game.isInitialized / game::font.isInitialized，不需要读 game.font。只有 Gdx.graphics != null && Gdx.graphics.width > 0 时才调用 settlementDetailRenderer.getOrInitChineseFont()，否则本地 font=null（保证无头测试不加载FreeType/GL）；isCustom 只需 font != null。
2. wrapLegacyText 不要再次复制旧的越界算法。G 已在 SettlementDetailRenderer 的 companion object 提供修好的 fun wrapRowText(font: BitmapFont,text: String,maxWidth: Float): List<String>。font 非空时直接委托 SettlementDetailRenderer.wrapRowText(font,text,maxWidth)。font 为空时，用固定12px估宽的 chunked(maxOf(1,(maxWidth/12f).toInt())) 作为无头/字体失败时的确定性回退；空字符串返回listOf("")，不丢文字。移除旧手写循环（它也会在仅最后字超限时越界）。
3. 不改分页区域、画面、事件、NEXT ROUND、不碰 Renderer/Presenter、不改测试；Codex 随后运行完整测试和图形检查。

只读这两个函数所在片段，精确修改后写 delivery-I.md 最多5行。不需要全文重读整个GameScreen或输出大报告。
