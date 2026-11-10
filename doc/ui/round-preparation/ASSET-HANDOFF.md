# 素材与拼接检查记录

2026-10-22，Codex 已完成，待 DeepSeek Flash 接入正式 APP。

- 七张生产素材已保存到 `assets/ui/table/`，逐张查看内容，六张透明素材的角像素 Alpha 为0。
- 已用 `node tools/ui-assets/build-table-design.cjs` 验证 PNG 尺寸、所有 atlas 源区域边界，生成 SHA256 和纹理预算约42 MiB（无 mipmap）。
- 四张 ASSEMBLY PNG 已从对应 SVG 在1280×720渲染并逐张查看，素材引用正常，没有遗漏或显示整张图集的问题。
- SVG XML 检查通过；设计源、manifest 与预览使用同一坐标。
- 此阶段没有修改正式 APP Kotlin 代码，也没有把设计图当作实机验收。开发后仍需真实 GL 截图及行为回归。
- 本地截图浏览器缓存 `snapshot/table-design-edge/` 约7 MB。自动审批规则以 blocked by policy 拒绝清理该目录，故保留；它不是素材，不得交付为 APP 资源或提交 Git。其他截图浏览器配置目录放在系统临时目录。

最终生成提示词和内置 imagegen 使用记录见 `assets/ui/table/generation-prompts.json`；源图均已保存到工作区。
