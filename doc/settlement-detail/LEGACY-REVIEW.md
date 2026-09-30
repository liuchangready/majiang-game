# H — 旧事件回退布局修正，严格只改 GameScreen.kt

另一个 Anti G 正在修复 Presenter/Renderer，不得修改这两文件。你只负责 GameScreen.kt 的 null breakdown 和牌回退，Codex 已复查实际截图发现和了牌覆盖“旧记录无明细”文字、yakuLines.take(3) 静默丢掉役种，必须修正。生产修改不要交还 Codex 代写。

- 新完整 breakdown 三标签路径保持不动。无 breakdown 的和牌按原700×480 modalRect渲染，不伪造公式或指标，仍写“旧记录无完整推导明细”。和了牌移右上角 x930,y535(30×42)，远离标题/文字。
- 旧 yakuList 每一项独立一行，不用 take(3)，六项一页。正文基线世界 y450,424,398,372,346,320，x350，可用宽580；实际役名+番数足够一行。特殊长字符串按测量拆行后再分页，不丢任何文字。
- 旧页按钮：prev Rect(350,260,100,30)，next Rect(810,260,100,30)，中间页码。正文上方获胜者、旧记录提示，正文下方 y300 显示总番/符/点数；玩家分数两行 y240,y218，列x350与650。确保所有区域不互遮，不改NEXT ROUND等原按钮 y145..195。
- 使用已有 settlementPage 属性；settlementPageCount 对旧记录返回总页数至少1；visibleSettlementRows() 对旧记录返回当前页；新事件/新局已有重置保留。只有回退和牌点击这两个按钮才翻页，不提交任何 GameAction。当前三标签按钮不得处理旧记录。
- 中文用 settlementDetailRenderer.getOrInitChineseFont()，资源失败显示英文可读提示；正常Noto正常显示。流局旧流程保留。既有 old-yaku formatter API不改。
- 可新增 GameScreen 内部简短 helper，避免复制整个新renderer；禁止修改 tests/Gradle/权限，禁止shell/terminal/RunCommand/MCP/其他代理。
- 交付写 doc/settlement-detail/delivery-H.md（最多15行）。Codex 执行旧14条役名翻页、无动作提交、真实GL截图及回归。
