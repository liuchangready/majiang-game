# E4 只修一个Java泛型错误

只修改 tools/architecture/SmokeScreenClient.java 的 createSnapshot。PlayerDecision 第4参数是 List<ActionOption>，不是 List<GameAction>，当前55行传legal导致两个工具无法编译。

该view-only fake不经过GameClient.choose按optionId匹配，所以第四参数恢复 `Collections.emptyList()`；移除为该构造器新增的局部 `List<GameAction> legal` 变量即可。GameScreenClient.choose仍根据view.getLegalActions检查，不更改。

不读其它文件，不修改其它内容，不执行shell。立即保存并写简短delivery-ANTI-V2FIX-E4.md。
