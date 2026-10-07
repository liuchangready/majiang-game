# 任务 E3 交付报告：migrate-v3.ps1 脚本修复与表现层归属分类

- **交付日期**：2026-10-01
- **工作区**：`D:/majiang`
- **执行角色**：Antigravity (Task E3 生产工具修复)
- **发起方 / 检视方**：Codex (设计、测试编写、编译构建、BOM 归一化与机械执行)
- **关联设计与契约**：[GAME_ARCHITECTURE_V3.md](../GAME_ARCHITECTURE_V3.md)、[V3 执行计划与契约](PLAN.md)

---

## 1. 交付声明与范围控制

根据任务 E3 契约规定：
1. **唯一受控修改文件**：仅对 [`tools/architecture/migrate-v3.ps1`](file:///D:/majiang/tools/architecture/migrate-v3.ps1) 进行极窄范围修复，未改动任何其它业务代码、Gradle 或测试文件；
2. **零命令执行与权限合规**：本任务未执行任何 Shell 命令行、终端、Gradle 进程或多 Agent 调用；
3. **零生产源码物理移动**：严格遵守机械迁移前置原则，未执行任何物理文件移动或目录重排；
4. **无无关重写**：针对性使用块替换工具修补语法与分类逻辑，保留原有所有设计与注释，等待后续由 Codex 统一进行 UTF-8 BOM 编码归一化。

---

## 2. 变更详细记录

### 2.1 PowerShell 5.1 语法解析错误修复

在 Windows PowerShell 5.1 中，`Test-Path variable:PSScriptRoot -and $PSScriptRoot` 会将 `-and` 误解析为 `Test-Path` cmdlet 的参数，导致语法或参数绑定解析失败。

修复方式：将 `Test-Path` cmdlet 调用严格置于括号表达式内优先求值，再进行 `-and` 逻辑与运算：

```powershell
# 修复前:
$scriptDir = if (Test-Path variable:PSScriptRoot -and $PSScriptRoot) {
    $PSScriptRoot
} elseif (Test-Path variable:PSCommandPath -and $PSCommandPath) {
    Split-Path -Parent $PSCommandPath

# 修复后:
$scriptDir = if ((Test-Path variable:PSScriptRoot) -and $PSScriptRoot) {
    $PSScriptRoot
} elseif ((Test-Path variable:PSCommandPath) -and $PSCommandPath) {
    Split-Path -Parent $PSCommandPath
```

### 2.2 表现层 `presentation/**` 模块归属补全

针对正在进行的 Task F（负责 UI 适配器、展示桥接等实现，位于 `presentation/**`），更新机械迁移分类规则，确保新表现层组件被准确保留在 `core` 模块中，防止误判迁入纯 JVM 的 `engine` 模块。

#### 1) 生产源码判定规则（第 151-161 行）
```powershell
# 归属判定:
# 1) core 仅保留: MahjongGame.kt、screen/**、render/**、ui/**、presentation/**、*adapter*.kt
if ($relNorm -eq "MahjongGame.kt" -or
    $relNorm -like "screen/*" -or
    $relNorm -like "render/*" -or
    $relNorm -like "ui/*" -or
    $relNorm -like "presentation/*" -or
    $relNorm -match '(?i)adapter\.kt$') {
    $targetModule = "core"
    $action = "KEEP_IN_CORE"
    $dstPath = Assert-SafePath (Join-Path $rootDir "core/src/main/kotlin/com/majiang/$relPath") $rootDir
}
```

#### 2) 测试源码判定规则（第 225-236 行）
```powershell
# 2) 表现层与 Headless 测试留在 core:
# 引用 com.badlogic、GdxHeadlessTestRunner、com.majiang.screen、com.majiang.render、com.majiang.presentation、com.majiang.MahjongGame
# 以及 GdxHeadlessTestRunner.kt 自身
$isCoreTest = ($file.Name -eq "GdxHeadlessTestRunner.kt") -or
              ($relNorm -like "com/majiang/presentation/*") -or
              ($content -match 'com\.badlogic') -or
              ($content -match '\bGdxHeadlessTestRunner\b') -or
              ($content -match 'com\.majiang\.screen') -or
              ($content -match 'com\.majiang\.render') -or
              ($content -match 'com\.majiang\.presentation') -or
              ($content -match 'com\.majiang\.MahjongGame')
```

---

## 3. 交付物清单

| 文件路径 | 变更类型 | 说明 |
|---|---|---|
| `tools/architecture/migrate-v3.ps1` | 修改 | 修复 PS5.1 下 `Test-Path` 参数解析缺陷，补全 `presentation/**` 在 `core` 模块的保留判定 |
| `doc/v3-migration/delivery-E3.md` | 新增 | 本交付报告 |

---

## 4. 后续交接说明 (面向 Codex)

1. **UTF-8 BOM 归一化**：请在检视后对 `tools/architecture/migrate-v3.ps1` 执行 UTF-8 with BOM 保存，以确保 Windows PowerShell 5.1 在中文环境变量下原生加载脚本中的汉字字符串与注释不发生乱码；
2. **迁移演练 (DRY-RUN)**：归一化后可直接在 PS5.1 或 PowerShell 7+ 中运行 `.\tools\architecture\migrate-v3.ps1` 执行演练验证。
