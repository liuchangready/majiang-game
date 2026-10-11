# Anti: E1FIX1 局次边界修复交付文档

## 1. 任务概述与改动说明

- **任务 ID**: E1FIX1
- **目标**: 修复 `TileMarkGroup` 局次边界校验，防止创建局次非正数以及创建局次 + 3 导致整数溢出。
- **修改文件**: [`engine/src/main/kotlin/com/majiang/cheat/mark/TileMarkModels.kt`](../../engine/src/main/kotlin/com/majiang/cheat/mark/TileMarkModels.kt)
- **改动详情**:
  - 在 `TileMarkGroup` 的 `init` 构造校验块中，增加 `createdRoundOrdinal in 1..(Int.MAX_VALUE - 3)` 校验。
  - 保留 `expiresAfterRoundOrdinal == createdRoundOrdinal + 3` 规则。
  - 保证非正数（<= 0）及过大值（>= Int.MAX_VALUE - 2）被严格拒绝，边界值 `Int.MAX_VALUE - 3` 正常通过且对应到期局次为 `Int.MAX_VALUE`。

## 2. 实际验证命令与退出状态

### 2.1 Codex 模型契约测试
- **命令**: `.\gradlew :engine:test --tests com.majiang.cheat.mark.TileMarkModelContractTest --console=plain`
- **退出码**: 0 (BUILD SUCCESSFUL)
- **执行测试用例**:
  - `feature is opt in for existing engine callers()` - PASSED
  - `group rejects non player owners()` - PASSED
  - `group cannot be created before first deal or with overflowing expiry()` - PASSED
  - `river position uses zero based index for all four seats()` - PASSED
  - `last effective ordinal includes current round and exactly three future rounds()` - PASSED
  - `pending selection cannot change when caller edits its list()` - PASSED
  - `four copies of the same face remain four distinct physical marks including red five()` - PASSED
  - `group rejects incomplete duplicate and out of deck identities()` - PASSED
  - `installed group cannot be retargeted through source or returned collection()` - PASSED

### 2.2 完整 engine 模块测试
- **命令**: `.\gradlew :engine:test --console=plain`
- **退出码**: 0 (BUILD SUCCESSFUL)

## 3. 测试报告 XML 核对结果

已核对 `engine/build/test-results/test/` 下生成的实际 JUnit XML 报告文件：

1. **TileMarkModelContractTest 报告 (`TEST-com.majiang.cheat.mark.TileMarkModelContractTest.xml`)**:
   - 总用例数 (tests): 9
   - 失败数 (failures): 0
   - 错误数 (errors): 0
   - 跳过数 (skipped): 0

2. **全量 engine 测试报告汇总 (共 49 个测试套件 XML 文件)**:
   - 总测试用例数 (TotalTests): 349
   - 失败数 (Failures): 0
   - 错误数 (Errors): 0
   - 跳过数 (Skipped): 0

## 4. 交付约束遵循情况

- 未修改任何测试代码或断言。
- 未跳过任何测试。
- 未扩大生产代码修改范围。
- 未执行 Git 提交。
- 交付后等待 Codex 独立复核验收。
