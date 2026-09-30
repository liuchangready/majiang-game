# Task I 交付
1. 修复 GameScreen.legacyYakuRows：移除 game::font / this::game 反射校验，改为根据 Gdx.graphics 与宽度安全初始化中文字体，无头环境保持 null；isCustom 直接判断 font != null。
2. 修复 GameScreen.wrapLegacyText：font 非空委托 SettlementDetailRenderer.wrapRowText，font 为空时使用 12px 估宽确定性 chunked 切分，空文本返回 listOf("")。
3. 仅修改上述两个函数，未触碰其他表现层逻辑、测试及构建文件。
