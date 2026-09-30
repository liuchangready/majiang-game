# Codex 第二轮精确修复 — 只修改 U 的生产文件

当前 F 版本整体进步，以下真实错误需要修复（不要改测试/构建，不执行命令，仅内置文件工具）：

1. SettlementDetailRenderer.wrapRowText 的 for(i in 1 until text.length) 没检查完整字符串前缀。若仅最后一个字超出 maxWidth，bestSplit 保持 text.length，后续 text[j] 直接越界。请用正确的最大可容纳前缀计算，标点回退扫描只访问 0..length-1，且不能取 bestSplit+1 导致第一行又超宽。每段文字不丢失、不截断；确保每行 GlyphLayout.width <= maxWidth。Codex GL harness 会构造“测”重复至仅最后一个字超限的字符串，在840/650/626px三宽测试并检查每行宽度。
2. CALC 目前把 FuBreakdown(subtotal=20,roundedUpTo=20,finalFu=30,specialNote="荣和 30符保底") 展示成“20向上取整至十位30”，数学错误。进位行应使用 roundedUpTo，最终计符/保底另行说明，不能用 finalFu 代替 roundedUpTo。七对25/平和自摸20分支保留。新验收 open ron minimum 专门核对。
3. 检查 snapshotScoreBreakdown 等 R 文件不得改，既定接口直接用即可。本次修正限定 Presenter/Renderer/GameScreen。所有生产开发依然由 Anti 完成。

完成写 delivery-G.md；无需运行任何测试，Codex 负责构建和截图。
