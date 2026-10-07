<#
.SYNOPSIS
    V3 架构物理模块机械迁移工具 (Mahjong Cheater V3 Module Migration Tool - PS 5.1 / .NET Framework 兼容版).
.DESCRIPTION
    按照 GAME_ARCHITECTURE_V3.md 与 V3 迁移执行计划契约，将 core 模块中的纯引擎、客户端协议与领域测试
    机械迁移至 engine 与 client 独立模块，core 仅保留 UI/libGDX 与场景展示逻辑。
    - 兼容 Windows PowerShell 5.1 (.NET Framework 4.x) 与 PowerShell 7+ (pwsh)。
    - 纯 PowerShell 实现，无外部命令、无子 shell、无递归删除、单文件安全移动。
    - 默认 DRY-RUN 模拟模式，显式传入 -Apply 执行实际迁移。
    - 严格保证所有源与目标路径在工作区根目录下，防路径遍历越权。
    - 计算迁移前后 SHA-256 哈希，核验字节完全一致，并输出清单与报告至 build/v3-migration/。
    - 目标冲突严格拦截 (Fail on conflicting destination)，严禁覆盖不同内容目标。
.PARAMETER Apply
    执行开关。若未指定则仅做 DRY-RUN 模拟演练与清单生成；指定后执行物理移动。
.PARAMETER VerboseLog
    输出逐文件详细迁移日志。
#>
[CmdletBinding()]
param(
    [switch]$Apply,
    [switch]$VerboseLog
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# 1. 解析并强校验工作区根目录 (兼容 Windows PowerShell 5.1 与 pwsh)
$scriptDir = if ((Test-Path variable:PSScriptRoot) -and $PSScriptRoot) {
    $PSScriptRoot
} elseif ((Test-Path variable:PSCommandPath) -and $PSCommandPath) {
    Split-Path -Parent $PSCommandPath
} elseif ($MyInvocation -and $MyInvocation.MyCommand -and $MyInvocation.MyCommand.Path) {
    Split-Path -Parent $MyInvocation.MyCommand.Path
} else {
    [System.IO.Path]::GetFullPath(".")
}
$rootDir = [System.IO.Path]::GetFullPath((Join-Path $scriptDir "../.."))

function Assert-SafePath([string]$path, [string]$workspaceRoot) {
    $fullPath = [System.IO.Path]::GetFullPath($path)
    $sepChars = [char[]]@([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar)
    $normalizedRoot = [System.IO.Path]::GetFullPath($workspaceRoot).TrimEnd($sepChars)
    $sep = [System.IO.Path]::DirectorySeparatorChar
    if (-not ($fullPath.StartsWith($normalizedRoot + $sep, [System.StringComparison]::OrdinalIgnoreCase) -or
              $fullPath.Equals($normalizedRoot, [System.StringComparison]::OrdinalIgnoreCase))) {
        throw "安全拦截: 路径 '$path' ($fullPath) 超出工作区根目录 '$workspaceRoot' 范围！"
    }
    return $fullPath
}

# 验证根目录合法性
$settingsFile = Join-Path $rootDir "settings.gradle"
if (-not [System.IO.File]::Exists($settingsFile)) {
    throw "致命错误: 工作区根目录 '$rootDir' 未找到 settings.gradle，根路径定位异常！"
}

# 2. 相对路径计算函数 (Windows PowerShell 5.1 / .NET Framework 4.x 兼容实现)
# 替代 .NET Core 专属的 [System.IO.Path]::GetRelativePath，并对工作区范围进行强校验
function Get-RelativePath([string]$baseDirectory, [string]$targetPath, [string]$workspaceRoot = $null) {
    if ($workspaceRoot) {
        $baseDirectory = Assert-SafePath $baseDirectory $workspaceRoot
        $targetPath = Assert-SafePath $targetPath $workspaceRoot
    } else {
        $baseDirectory = [System.IO.Path]::GetFullPath($baseDirectory)
        $targetPath = [System.IO.Path]::GetFullPath($targetPath)
    }

    $sep = [System.IO.Path]::DirectorySeparatorChar
    $sepChars = [char[]]@([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar)

    $fullBase = $baseDirectory.TrimEnd($sepChars)
    $fullTarget = $targetPath

    $prefix = $fullBase + $sep

    # 1) 快速匹配: targetPath 位于 baseDirectory 之内
    if ($fullTarget.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        return $fullTarget.Substring($prefix.Length)
    }

    # 2) 相同路径
    if ($fullTarget.Equals($fullBase, [System.StringComparison]::OrdinalIgnoreCase)) {
        return "."
    }

    # 3) 通用相对路径算法 (同一盘符/根节点)
    $baseParts = $fullBase.Split($sepChars, [System.StringSplitOptions]::RemoveEmptyEntries)
    $targetParts = $fullTarget.Split($sepChars, [System.StringSplitOptions]::RemoveEmptyEntries)

    if ($baseParts.Length -eq 0 -or $targetParts.Length -eq 0 -or
        -not $baseParts[0].Equals($targetParts[0], [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "无法计算跨驱动器/根节点的相对路径: '$baseDirectory' -> '$targetPath'"
    }

    $commonLen = 0
    $minLen = [System.Math]::Min($baseParts.Length, $targetParts.Length)
    while ($commonLen -lt $minLen -and $baseParts[$commonLen].Equals($targetParts[$commonLen], [System.StringComparison]::OrdinalIgnoreCase)) {
        $commonLen++
    }

    $segments = [System.Collections.Generic.List[string]]::new()
    for ($i = $commonLen; $i -lt $baseParts.Length; $i++) {
        $segments.Add("..")
    }
    for ($i = $commonLen; $i -lt $targetParts.Length; $i++) {
        $segments.Add($targetParts[$i])
    }

    return ($segments -join $sep)
}

# 3. SHA-256 哈希计算函数
function Get-FileSha256([string]$filePath) {
    if (-not [System.IO.File]::Exists($filePath)) {
        return $null
    }
    $sha256 = [System.Security.Cryptography.SHA256]::Create()
    $stream = [System.IO.File]::OpenRead($filePath)
    try {
        $hashBytes = $sha256.ComputeHash($stream)
        return [System.BitConverter]::ToString($hashBytes).Replace('-', '').ToLowerInvariant()
    }
    finally {
        $stream.Close()
        $sha256.Dispose()
    }
}

# 4. 确定核心扫描目录
$coreMainDir = Assert-SafePath (Join-Path $rootDir "core/src/main/kotlin/com/majiang") $rootDir
$coreTestDir = Assert-SafePath (Join-Path $rootDir "core/src/test/kotlin") $rootDir

Write-Host "================================================================" -ForegroundColor Cyan
Write-Host "  🀄 千术麻将 (Mahjong Cheater) - V3 模块机械迁移工具" -ForegroundColor Cyan
Write-Host "================================================================" -ForegroundColor Cyan
Write-Host "工作区根目录 : $rootDir"
Write-Host "执行模式     : $(if ($Apply) { '【APPLY 实际执行】' } else { '【DRY-RUN 模拟演练】(请传入 -Apply 执行实际物理迁移)' })"
Write-Host ""

$migrationPlan = [System.Collections.Generic.List[object]]::new()
$plannedDestinations = [System.Collections.Generic.Dictionary[string, string]]::new([System.StringComparer]::OrdinalIgnoreCase)

# 5. 动态扫描生产源码 (core/src/main/kotlin/com/majiang/**)
if ([System.IO.Directory]::Exists($coreMainDir)) {
    $mainFiles = Get-ChildItem -LiteralPath $coreMainDir -Filter "*.kt" -Recurse -File | Where-Object { $_.Extension -eq '.kt' }
    foreach ($file in $mainFiles) {
        $srcPath = Assert-SafePath $file.FullName $rootDir
        $relPath = Get-RelativePath -baseDirectory $coreMainDir -targetPath $srcPath -workspaceRoot $rootDir
        $relNorm = $relPath.Replace('\', '/')

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
        # 2) client 模块: sync/** 与 client/GameClient.kt
        elseif ($relNorm -like "sync/*" -or $relNorm -eq "client/GameClient.kt") {
            $targetModule = "client"
            $action = "MOVE_TO_CLIENT"
            $dstPath = Assert-SafePath (Join-Path $rootDir "client/src/main/kotlin/com/majiang/$relPath") $rootDir
        }
        # 3) engine 模块: model, state, round, match, rules, settlement, event, ai, cheat,
        #                 application, session, economy, timing, control, host, access,
        #                 client/PlayerEndpoint.kt, client/LocalPlayerEndpoint.kt 等全部领域与宿主包
        else {
            $targetModule = "engine"
            $action = "MOVE_TO_ENGINE"
            $dstPath = Assert-SafePath (Join-Path $rootDir "engine/src/main/kotlin/com/majiang/$relPath") $rootDir
        }

        if ($plannedDestinations.ContainsKey($dstPath)) {
            $prevSrc = $plannedDestinations[$dstPath]
            throw "迁移计划冲突: 目标路径 '$dstPath' 同时被两个源文件指向！(源1: '$prevSrc', 源2: '$srcPath')"
        }
        $plannedDestinations[$dstPath] = $srcPath

        $migrationPlan.Add([PSCustomObject]@{
            Category        = "PRODUCTION"
            RelativePath    = "com/majiang/$relNorm"
            SourcePath      = $srcPath
            DestinationPath = $dstPath
            TargetModule    = $targetModule
            Action          = $action
            PreSha256       = $null
            PostSha256      = $null
            Status          = "PENDING"
            Verified        = $false
        })
    }
}

# 6. 动态扫描测试源码与测试夹具 (core/src/test/kotlin/**)
if ([System.IO.Directory]::Exists($coreTestDir)) {
    $testFiles = Get-ChildItem -LiteralPath $coreTestDir -Filter "*.kt" -Recurse -File | Where-Object { $_.Extension -eq '.kt' }
    foreach ($file in $testFiles) {
        $srcPath = Assert-SafePath $file.FullName $rootDir
        $relPath = Get-RelativePath -baseDirectory $coreTestDir -targetPath $srcPath -workspaceRoot $rootDir
        $relNorm = $relPath.Replace('\', '/')
        $content = [System.IO.File]::ReadAllText($srcPath)

        # 1) 测试夹具: core/src/test/kotlin/com/majiang/testing/** 迁至 engine/src/testFixtures/kotlin/com/majiang/testing/**
        if ($relNorm -like "com/majiang/testing/*") {
            # 强校验: 禁止夹具包含 libGDX/UI 相关依赖，确保纯 JVM 引擎可用
            if ($content -match 'com\.badlogic' -or
                $content -match 'com\.majiang\.screen' -or
                $content -match 'com\.majiang\.render' -or
                $content -match 'com\.majiang\.MahjongGame') {
                throw "合规校验失败: 测试夹具 '$relNorm' 包含被禁用的 libGDX/UI 依赖！engine testFixtures 必须为纯 JVM。"
            }

            $targetModule = "engine"
            $category = "TEST_FIXTURE"
            $action = "MOVE_TO_ENGINE_TEST_FIXTURES"
            $dstPath = Assert-SafePath (Join-Path $rootDir "engine/src/testFixtures/kotlin/$relPath") $rootDir
        }
        else {
            $category = "TEST"
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

            if ($isCoreTest) {
                $targetModule = "core"
                $action = "KEEP_IN_CORE"
                $dstPath = Assert-SafePath (Join-Path $rootDir "core/src/test/kotlin/$relPath") $rootDir
            }
            else {
                # 3) 客户端及同步测试迁至 client: 引用 GameClient 或 sync
                $isClientTest = ($content -match 'com\.majiang\.client\.GameClient') -or
                                ($content -match '\bGameClient\b') -or
                                ($content -match 'com\.majiang\.sync')

                if ($isClientTest) {
                    $targetModule = "client"
                    $action = "MOVE_TO_CLIENT"
                    $dstPath = Assert-SafePath (Join-Path $rootDir "client/src/test/kotlin/$relPath") $rootDir
                }
                # 4) 其余纯引擎、领域、host、规则、计分及架构测试迁至 engine
                else {
                    $targetModule = "engine"
                    $action = "MOVE_TO_ENGINE"
                    $dstPath = Assert-SafePath (Join-Path $rootDir "engine/src/test/kotlin/$relPath") $rootDir
                }
            }
        }

        if ($plannedDestinations.ContainsKey($dstPath)) {
            $prevSrc = $plannedDestinations[$dstPath]
            throw "迁移计划冲突: 目标路径 '$dstPath' 同时被两个源文件指向！(源1: '$prevSrc', 源2: '$srcPath')"
        }
        $plannedDestinations[$dstPath] = $srcPath

        $migrationPlan.Add([PSCustomObject]@{
            Category        = $category
            RelativePath    = $relNorm
            SourcePath      = $srcPath
            DestinationPath = $dstPath
            TargetModule    = $targetModule
            Action          = $action
            PreSha256       = $null
            PostSha256      = $null
            Status          = "PENDING"
            Verified        = $false
        })
    }
}

# 7. 预检目标状态并计算 SHA-256
$movedCount = 0
$keptCount = 0
$dedupCount = 0
$conflictCount = 0

foreach ($item in $migrationPlan) {
    $src = $item.SourcePath
    $dst = $item.DestinationPath
    $srcHash = Get-FileSha256 $src
    $item.PreSha256 = $srcHash

    # 留在 core 模块无需移动
    if ($src -eq $dst) {
        $item.PostSha256 = $srcHash
        $item.Status = "KEPT_IN_CORE"
        $item.Verified = $true
        $keptCount++
        if ($VerboseLog) {
            Write-Host "  [保留 core] $($item.RelativePath)" -ForegroundColor DarkGray
        }
        continue
    }

    # 跨模块迁移目标检查
    $dstExists = [System.IO.File]::Exists($dst)
    $dstHash = if ($dstExists) { Get-FileSha256 $dst } else { $null }

    if ($dstExists) {
        $item.PostSha256 = $dstHash
        if ($dstHash -eq $srcHash) {
            $item.Status = if ($Apply) { "PENDING_DEDUPLICATION" } else { "DRY_RUN_TARGET_IDENTICAL" }
            $item.Verified = $true
            $dedupCount++
            if ($VerboseLog) {
                Write-Host "  [目标已就绪] $($item.RelativePath) 目标哈希完全一致" -ForegroundColor Yellow
            }
        } else {
            $item.Status = "CONFLICT_DIFFERING_HASH"
            $item.Verified = $false
            $conflictCount++
            Write-Host "  [冲突错误]   $($item.RelativePath) -> $($item.TargetModule) 目标已存在且哈希不一致！" -ForegroundColor Red
            Write-Host "               源: $src ($srcHash)" -ForegroundColor Red
            Write-Host "               目标: $dst ($dstHash)" -ForegroundColor Red
        }
    } else {
        $item.PostSha256 = $null
        $item.Status = if ($Apply) { "PENDING_MOVE" } else { "DRY_RUN_PENDING_MOVE" }
        $item.Verified = $null
        $movedCount++
        if ($VerboseLog) {
            Write-Host "  [计划移动]   $($item.RelativePath) -> $($item.TargetModule)" -ForegroundColor Green
        }
    }
}

# 8. 输出迁移清单与审计报告至 build/v3-migration
$outDir = Assert-SafePath (Join-Path $rootDir "build/v3-migration") $rootDir
if (-not [System.IO.Directory]::Exists($outDir)) {
    [System.IO.Directory]::CreateDirectory($outDir) | Out-Null
}

$manifestPath = Join-Path $outDir "migration-manifest.json"
$reportPath = Join-Path $outDir "migration-report.txt"

$jsonContent = $migrationPlan | ConvertTo-Json -Depth 5
[System.IO.File]::WriteAllText($manifestPath, $jsonContent, [System.Text.Encoding]::UTF8)

$reportLines = [System.Collections.Generic.List[string]]::new()
$reportLines.Add("================================================================")
$reportLines.Add("千术麻将 (Mahjong Cheater) - V3 模块机械迁移审计报告")
$reportLines.Add("================================================================")
$reportLines.Add("执行时间   : $([System.DateTime]::Now.ToString('yyyy-MM-dd HH:mm:ss'))")
$reportLines.Add("工作区     : $rootDir")
$reportLines.Add("模式       : $(if ($Apply) { 'APPLY (实际物理迁移)' } else { 'DRY-RUN (演练模拟)' })")
$reportLines.Add("文件总数   : $($migrationPlan.Count)")
$reportLines.Add("保留在 core: $keptCount")
$reportLines.Add("跨模块移动 : $movedCount")
$reportLines.Add("幂等/已就绪: $dedupCount")
$reportLines.Add("冲突/异常  : $conflictCount")
$reportLines.Add("----------------------------------------------------------------")
$reportLines.Add("模块归属分布统计:")
$byModule = $migrationPlan | Group-Object TargetModule
foreach ($grp in $byModule) {
    $reportLines.Add("  模块 $([string]$grp.Name.PadRight(10)): $($grp.Count) 文件")
}
$reportLines.Add("----------------------------------------------------------------")
$reportLines.Add("源码分类分布统计:")
$byCat = $migrationPlan | Group-Object Category
foreach ($grp in $byCat) {
    $reportLines.Add("  类别 $([string]$grp.Name.PadRight(14)): $($grp.Count) 文件")
}
$reportLines.Add("----------------------------------------------------------------")
$reportLines.Add("详细明细列表:")
foreach ($item in $migrationPlan) {
    $statusText = if ($item.Verified -eq $true) { "[VERIFIED]" } elseif ($item.Verified -eq $false) { "[CONFLICT]" } else { "[PLANNED]" }
    $reportLines.Add(("{0,-10} {1,-14} {2,-8} -> {3,-8} : {4}" -f $statusText, $item.Category, $item.Status, $item.TargetModule, $item.RelativePath))
}
$reportLines.Add("================================================================")

[System.IO.File]::WriteAllLines($reportPath, $reportLines.ToArray(), [System.Text.Encoding]::UTF8)

# 9. 冲突强行拦截 (无论是 DRY-RUN 还是 APPLY，若有目标哈希不一致冲突均严厉中止)
if ($conflictCount -gt 0) {
    Write-Host ""
    Write-Host "[致命错误] 检测到 $conflictCount 处目标文件冲突！严禁覆盖不同内容目标。" -ForegroundColor Red
    Write-Host "           审计清单已写入: $manifestPath" -ForegroundColor Red
    Write-Host "           审计报告已写入: $reportPath" -ForegroundColor Red
    throw "迁移安全中止: 存在 $conflictCount 处目标文件内容冲突！请查阅 $reportPath 并手动解决冲突。"
}

# 10. APPLY 实际物理迁移执行
if ($Apply) {
    Write-Host ""
    Write-Host ">>> 正在执行物理文件迁移与去重..." -ForegroundColor Cyan

    foreach ($item in $migrationPlan) {
        $src = $item.SourcePath
        $dst = $item.DestinationPath
        $srcHash = $item.PreSha256

        if ($src -eq $dst) {
            continue
        }

        $dstExists = [System.IO.File]::Exists($dst)
        if ($dstExists) {
            # 目标已存在且哈希一致，清理冗余源文件（单文件安全删除，无递归）
            Remove-Item -LiteralPath $src -Force
            $item.Status = "ALREADY_MOVED_DEDUPLICATED"
            $item.Verified = $true
            if ($VerboseLog) {
                Write-Host "  [幂等清理] $($item.RelativePath) (已移除源副本)" -ForegroundColor Yellow
            }
        } else {
            # 单文件物理移动
            $dstDir = Split-Path -Parent $dst
            if (-not [System.IO.Directory]::Exists($dstDir)) {
                [System.IO.Directory]::CreateDirectory($dstDir) | Out-Null
            }

            Move-Item -LiteralPath $src -Destination $dst -Force

            # 移动后即时哈希比对核验 (SHA-256 Post)
            $newDstHash = Get-FileSha256 $dst
            if ($newDstHash -ne $srcHash) {
                throw "迁移致命异常: 文件移动后哈希不匹配！'$src' -> '$dst' (预期: $srcHash, 实际: $newDstHash)。"
            }
            $item.PostSha256 = $newDstHash
            $item.Status = "MOVED_AND_VERIFIED"
            $item.Verified = $true
            if ($VerboseLog) {
                Write-Host "  [移动成功] $($item.RelativePath) -> $($item.TargetModule) (字节校验通过)" -ForegroundColor Green
            }
        }
    }

    # 更新最终完成清单与报告
    $jsonContentFinal = $migrationPlan | ConvertTo-Json -Depth 5
    [System.IO.File]::WriteAllText($manifestPath, $jsonContentFinal, [System.Text.Encoding]::UTF8)

    $finalLines = [System.Collections.Generic.List[string]]::new()
    $finalLines.Add("================================================================")
    $finalLines.Add("千术麻将 (Mahjong Cheater) - V3 模块机械迁移审计报告 (执行完成)")
    $finalLines.Add("================================================================")
    $finalLines.Add("完成时间   : $([System.DateTime]::Now.ToString('yyyy-MM-dd HH:mm:ss'))")
    $finalLines.Add("工作区     : $rootDir")
    $finalLines.Add("模式       : APPLY (已执行实际物理迁移)")
    $finalLines.Add("文件总数   : $($migrationPlan.Count)")
    $finalLines.Add("保留在 core: $keptCount")
    $finalLines.Add("已移动文件 : $movedCount")
    $finalLines.Add("幂等清理数 : $dedupCount")
    $finalLines.Add("冲突/异常  : $conflictCount")
    $finalLines.Add("================================================================")
    foreach ($item in $migrationPlan) {
        $finalLines.Add(("[VERIFIED]  {0,-14} {1,-26} -> {2,-8} : {3}" -f $item.Category, $item.Status, $item.TargetModule, $item.RelativePath))
    }
    $finalLines.Add("================================================================")
    [System.IO.File]::WriteAllLines($reportPath, $finalLines.ToArray(), [System.Text.Encoding]::UTF8)
}

# 11. 控制台汇总输出
Write-Host ""
Write-Host "----------------- 迁移扫描与执行摘要 -----------------" -ForegroundColor Cyan
Write-Host "扫描总文件数 : $($migrationPlan.Count)"
Write-Host "保留在 core  : $keptCount"
Write-Host "计划/执行移动: $movedCount"
Write-Host "幂等/已就绪  : $dedupCount"
Write-Host "冲突/异常    : $conflictCount"
Write-Host "JSON 清单文件: $manifestPath"
Write-Host "文本审计报告 : $reportPath"
Write-Host "------------------------------------------------------" -ForegroundColor Cyan

if (-not $Apply) {
    Write-Host "[提示] 本次为 DRY-RUN 模拟模式，未做任何物理移动。" -ForegroundColor Yellow
    Write-Host "      如需实际执行，请传入 -Apply 参数：" -ForegroundColor Yellow
    Write-Host "      powershell -NoProfile -ExecutionPolicy Bypass -File tools/architecture/migrate-v3.ps1 -Apply" -ForegroundColor White
} else {
    Write-Host "[成功] 物理模块迁移执行完成，所有目标文件哈希与源文件 100% 一致！" -ForegroundColor Green
}
