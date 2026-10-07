# 任务 E2 交付报告：Windows PowerShell 5.1 机械迁移脚本兼容性改造

- **交付日期**：2026-10-01
- **工作区**：`D:/majiang`
- **执行角色**：Antigravity (Task E2 迁移工具开发)
- **发起 / 检视角色**：Codex (测试设计、构建验证、代码检视与机械执行)
- **目标设计文档**：[GAME_ARCHITECTURE_V3.md](../GAME_ARCHITECTURE_V3.md) 与 V3 迁移执行计划契约

---

## 1. 交付声明与执行原则

根据任务 E2 契约与跨 Agent 协作规范：
1. **单一写入范围**：本任务仅拥有并修改 `tools/architecture/migrate-v3.ps1`，以及输出本文档 `doc/v3-migration/delivery-E2.md`；
2. **零生产代码破坏**：未触碰、移动或修改任何生产 Kotlin 源码或测试代码；
3. **零命令执行**：严格使用只读与文件编辑工具完成，未运行 Shell、未执行任何终端命令，未调用外部 Agent；
4. **运行环境适配**：针对宿主环境仅有 Windows PowerShell 5.1（运行于 .NET Framework 4.x）且未安装 `pwsh` 的客观事实，完成了纯 JVM 引擎及客户端模块迁移脚本的语法与 API 兼容性彻底重构。

---

## 2. 问题诊断与核心改造

### 2.1 根因分析：.NET Core 专有 API 缺失
原脚本中第 84 与 133 行调用了 `[System.IO.Path]::GetRelativePath($baseDir, $path)`：
- `Path.GetRelativePath` 属于 .NET Core 2.1+ / .NET Standard 2.1+ 新增 API；
- Windows PowerShell 5.1 依托于 .NET Framework 4.x 运行，调用该静态方法会触发 `MethodInvocationException: Method 'GetRelativePath' not found on 'System.IO.Path'` 导致脚本崩溃；
- 环境中未安装 PowerShell 7 (`pwsh`)，必须保证可通过原生 `powershell.exe` 直接运行。

### 2.2 解决方案：工作区安全校验与原生算法实现 `Get-RelativePath`
在脚本中新增纯 PowerShell 5.1 / .NET Framework 兼容的 `Get-RelativePath` 函数：
1. **边界安全断言**：接受可选参数 `$workspaceRoot`，在计算前通过 `Assert-SafePath` 强断言基准路径与目标路径均位于工作区根目录（`D:/majiang`）以内，防止路径遍历或越权攻击；
2. **快速子路径匹配**：规范化基准目录后追加系统路径分隔符（`\`），若目标路径以该前缀开头（`StartsWith(..., OrdinalIgnoreCase)`），直接通过截取子串（`Substring`）高效返回相对路径；
3. **同级与跨目录通用分解**：对基准路径与目标路径按目录分隔符拆分，核验根驱动器一致性，计算公共前缀长度（`commonLen`），按深度回退合成 `..` 相对段，完整还原相对路径语义；
4. **跨平台兼容**：拆分与拼接严格基于 `[System.IO.Path]::DirectorySeparatorChar` 与 `AltDirectorySeparatorChar`。

---

## 3. Windows PowerShell 5.1 与 .NET Framework 4.x 深度兼容性检查

除 `GetRelativePath` 之外，对全脚本进行了系统级审查与加固：

| 检查项 / 潜在陷阱 | 原写法风险 | E2 加固方案 |
|---|---|---|
| **字符串 TrimEnd 重载** | `$path.TrimEnd('\', '/')` 在 PS 5.1 的方法绑定器中传入两个 string 字面量可能出现参数重载解析失败 | 显式传入字符数组：`TrimEnd([char[]]@([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar))` |
| **脚本根目录定位** | 原逻辑 fallback 到 `$PSScriptRoot` 后直接取 `Split-Path -Parent` 会导致定位到 `tools` 导致工作区解析错误 | 优先判断 `Test-Path variable:PSScriptRoot`，若为直接运行脚本则 `$PSScriptRoot` 即为 `tools/architecture`，可靠解析根目录 |
| **路径通配符与大小写** | `.StartsWith("screen/")` 受当前区域性影响且需严格区分大小写 | 采用 PowerShell 原生、大小写不敏感且文化无关的通配匹配：`-like "screen/*"`、`-like "sync/*"`、`-like "com/majiang/testing/*"` |
| **文件流哈希与资源释放** | `ComputeHash` 与文件流关闭 | 保留 `[System.IO.File]::OpenRead` 与 `try { ... } finally { $stream.Close(); $sha256.Dispose() }`，兼容 .NET 2.0+ 垃圾回收与即时句柄释放 |
| **WriteAllLines 重载** | 传递泛型 `List<string>` 在某些旧版 .NET Framework 绑定器中可能有歧义 | 显式调用 `$reportLines.ToArray()`，精确绑定 `File.WriteAllLines(string, string[], Encoding)` |
| **调用提示命令** | 控制台提示使用 `pwsh ./tools/architecture/migrate-v3.ps1 -Apply` | 更新为标准 `powershell -NoProfile -ExecutionPolicy Bypass -File tools/architecture/migrate-v3.ps1 -Apply` |

---

## 4. 关键安全不变式核验

改造后的脚本严格遵循 V3 架构迁移安全铁律：

1. **默认 DRY-RUN（演练模拟）**：
   - 默认执行不带 `-Apply` 参数时，仅进行全面扫描、分类归属、哈希计算、冲突探测，并输出清单与报告；
   - 绝对不执行任何文件系统写入、移动或删除操作。
2. **显式 -Apply（物理执行）**：
   - 必须显式传入 `-Apply` 开关才会进入物理迁移阶段。
3. **严禁递归删除（No Recursive Delete）**：
   - 不调用任何带有 `-Recurse` 参数的 `Remove-Item`；
   - 仅对已确认目标已存在且 SHA-256 完全一致的冗余源文件执行单文件 `Remove-Item -LiteralPath $src -Force` 清理。
4. **SHA-256 前后双重比对**：
   - 迁移前记录源文件 `PreSha256`；
   - 移动完成后即时重新计算目标文件的 `PostSha256`，若有 1 bit 偏差立即抛出致命异常中止流程。
5. **目标冲突严格拦截（Fail on Conflicting Destination）**：
   - **计划内唯一性检查**：通过 `$plannedDestinations` 字典登记所有目标路径，若两个源文件意外映射到同一目标，立即在生成阶段阻断；
   - **内容冲突事务性拦截**：在任何文件被移动或删除之前，预先比对所有已存在目标的 SHA-256。若目标已存在且哈希不同（`CONFLICT_DIFFERING_HASH`），在预检阶段即刻记录完整报告并**直接抛出致命异常阻断**，无论是在 DRY-RUN 还是在 APPLY 模式下均保证严禁覆盖不同内容目标。

---

## 5. 检视与执行指引 (面向 Codex)

Codex 在检视完脚本后，可直接在 PowerShell 5.1 环境中通过以下命令执行：

### 5.1 模拟演练 (DRY-RUN)
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools/architecture/migrate-v3.ps1
```
- 检查控制台输出的扫描总数、各模块分流分布与冲突数（应为 0）；
- 检查生成的 `build/v3-migration/migration-manifest.json` 与 `build/v3-migration/migration-report.txt`。

### 5.2 实际执行物理迁移 (APPLY)
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools/architecture/migrate-v3.ps1 -Apply
```
- 逐文件原子移动至 `engine/` 与 `client/`，完成 SHA-256 比对核验与幂等清理。

### 5.3 后续验证与全量构建
```bash
./gradlew test :lwjgl3:classes :android:assembleDebug
```
