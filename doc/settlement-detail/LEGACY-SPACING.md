# J：仅两行坐标微调

只用内置文件编辑修改 GameScreen.kt 两个 Rectangle 常量：btnLegacyPrevPageRect 的 y 从260f改250f，btnLegacyNextPageRect 的 y 从260f改250f。其他参数及全部其他生产代码不动。不读无关文件、不运行命令、不改测试。Codex 最终截图发现 y300 的番符点数文字下沿碰到旧分页按钮顶边，向下10px可留出间距，点击验收 y275 仍在按钮内。所有其他测试364项已通过，其他Agent已结束。完成写 delivery-J.md 一行即可。
