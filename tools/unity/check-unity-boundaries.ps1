<#
.SYNOPSIS
    Static boundary and architectural policy checker for Unity assemblies in V5.
.DESCRIPTION
    Enforces DAG integrity, exact asmdef references/precompiledReferences sets,
    source-level namespace isolation, and reflection/dynamic API usage constraints.
    Supports -TestNegativeMatrix to verify rejection of 11 negative boundary cases.
#>
[CmdletBinding()]
param(
    [string]$Project = 'unity/Mahjong.Unity',
    [string]$Policy = 'tools/unity/p42-boundary-policy.json',
    [string]$Report = 'build/p42/boundary-report.json',
    [switch]$TestNegativeMatrix
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$projectPath = Join-Path $repoRoot $Project
$policyPath = Join-Path $repoRoot $Policy

function Assert-NoCycle([hashtable]$graph, [string[]]$nodes) {
    $visited = @{}
    $inStack = @{}

    function Dfs([string]$u, [System.Collections.Generic.List[string]]$path) {
        $visited[$u] = $true
        $inStack[$u] = $true
        $path.Add($u)

        if ($graph.ContainsKey($u)) {
            foreach ($v in $graph[$u]) {
                if ($inStack.ContainsKey($v) -and $inStack[$v]) {
                    $cycle = ($path | ForEach-Object { $_ }) -join ' -> '
                    throw "Cycle detected in asmdef DAG: $cycle -> $v"
                }
                if (-not ($visited.ContainsKey($v) -and $visited[$v])) {
                    Dfs $v $path
                }
            }
        }

        $path.RemoveAt($path.Count - 1)
        $inStack[$u] = $false
    }

    foreach ($n in $nodes) {
        if (-not ($visited.ContainsKey($n) -and $visited[$n])) {
            $p = New-Object System.Collections.Generic.List[string]
            Dfs $n $p
        }
    }
}

function Test-SingleBoundaryRules([string]$projDir, [string]$policyFile) {
    if (-not (Test-Path $policyFile)) {
        throw "Policy file not found: $policyFile"
    }
    $policy = Get-Content $policyFile -Raw | ConvertFrom-Json
    $errors = New-Object System.Collections.Generic.List[string]

    $asmdefFiles = Get-ChildItem -Path $projDir -Recurse -Filter "*.asmdef"
    $asmdefsByName = @{}

    foreach ($f in $asmdefFiles) {
        try {
            $json = Get-Content $f.FullName -Raw | ConvertFrom-Json
            $name = $json.name
            $asmdefsByName[$name] = @{
                File = $f
                Data = $json
            }
        }
        catch {
            $errors.Add("Failed to parse asmdef JSON at $($f.FullName): $_")
        }
    }

    # 1. Exact set and flags check
    $graph = @{}
    $projectNodes = @()

    foreach ($asmName in $policy.projectAsmdefs) {
        $projectNodes += $asmName
        if (-not $asmdefsByName.ContainsKey($asmName)) {
            $errors.Add("Missing required project asmdef: $asmName")
            continue
        }

        $asm = $asmdefsByName[$asmName]
        $data = $asm.Data
        $spec = $policy.assemblies.$asmName

        if ($null -eq $spec) {
            $errors.Add("Asmdef '$asmName' present in projectAsmdefs but missing spec in policy.")
            continue
        }

        # Check flags
        if ($data.overrideReferences -ne $true) {
            $errors.Add("Asmdef '$asmName' must have overrideReferences=true.")
        }
        if ($data.autoReferenced -ne $false) {
            $errors.Add("Asmdef '$asmName' must have autoReferenced=false.")
        }
        if ($data.allowUnsafeCode -ne $false) {
            $errors.Add("Asmdef '$asmName' must have allowUnsafeCode=false.")
        }

        # Test define constraints
        if ($asmName -like "*Tests") {
            $constraints = @($data.defineConstraints)
            if (-not ($constraints -contains "UNITY_INCLUDE_TESTS")) {
                $errors.Add("Test asmdef '$asmName' must include UNITY_INCLUDE_TESTS in defineConstraints.")
            }
        }

        # Check references: exact match
        $actualRefs = if ($data.references) { @($data.references) | Sort-Object } else { @() }
        $expectedRefs = if ($spec.references) { @($spec.references) | Sort-Object } else { @() }
        $diff1 = if ($actualRefs.Count -ne $expectedRefs.Count) { $true } elseif ($actualRefs.Count -gt 0) { Compare-Object $actualRefs $expectedRefs } else { $null }
        if ($diff1) {
            $errors.Add("Asmdef '$asmName' references mismatch. Expected: [$(($expectedRefs)-join ', ')], Actual: [$(($actualRefs)-join ', ')]")
        }

        # Check precompiledReferences: exact match
        $actualPre = if ($data.precompiledReferences) { @($data.precompiledReferences) | Sort-Object } else { @() }
        $expectedPre = if ($spec.precompiledReferences) { @($spec.precompiledReferences) | Sort-Object } else { @() }
        $diff2 = if ($actualPre.Count -ne $expectedPre.Count) { $true } elseif ($actualPre.Count -gt 0) { Compare-Object $actualPre $expectedPre } else { $null }
        if ($diff2) {
            $errors.Add("Asmdef '$asmName' precompiledReferences mismatch. Expected: [$(($expectedPre)-join ', ')], Actual: [$(($actualPre)-join ', ')]")
        }

        # Build graph for project asmdef references only
        $projRefs = @()
        foreach ($r in $data.references) {
            if ($policy.projectAsmdefs -contains $r) {
                $projRefs += $r
            }
        }
        $graph[$asmName] = $projRefs
    }

    # 2. DAG Cycle check
    try {
        Assert-NoCycle $graph $projectNodes
    }
    catch {
        $errors.Add($_.Exception.Message)
    }

    # 3. Source code namespace isolation check
    $allowedReflection = @($policy.reflectionAllowedFiles | ForEach-Object { (Join-Path $projDir $_).Replace('/', '\') })

    foreach ($asmName in $policy.projectAsmdefs) {
        if (-not $asmdefsByName.ContainsKey($asmName)) { continue }
        $asm = $asmdefsByName[$asmName]
        $asmDir = $asm.File.Directory.FullName
        $spec = $policy.assemblies.$asmName
        $forbidden = @($spec.forbiddenNamespaces)

        if ($forbidden.Count -gt 0) {
            $csFiles = Get-ChildItem -Path $asmDir -Recurse -Filter "*.cs"
            foreach ($cs in $csFiles) {
                $isAllowed = $false
                foreach ($whitelisted in $allowedReflection) {
                    if ($cs.FullName -ieq $whitelisted) {
                        $isAllowed = $true
                        break
                    }
                }
                if ($isAllowed) { continue }

                $content = Get-Content $cs.FullName -Raw
                foreach ($forb in $forbidden) {
                    $pattern = "\busing\s+$([regex]::Escape($forb))\b|\b$([regex]::Escape($forb))\."
                    if ($content -match $pattern) {
                        $errors.Add("Forbidden namespace/type '$forb' referenced in '$($cs.FullName)'.")
                    }
                }
            }
        }
    }

    # 4. Source reflection / dynamic API scan
    $allCs = Get-ChildItem -Path (Join-Path $projDir "Assets/Scripts") -Recurse -Filter "*.cs"
    $allowedReflection = @($policy.reflectionAllowedFiles | ForEach-Object { (Join-Path $projDir $_).Replace('/', '\') })

    $reflectionPatterns = @(
        'Assembly\.Load',
        'Assembly\.LoadFrom',
        'Type\.GetType',
        'Activator\.CreateInstance',
        'Reflection\.Emit',
        '\bdynamic\b'
    )

    foreach ($cs in $allCs) {
        $isAllowed = $false
        foreach ($whitelisted in $allowedReflection) {
            if ($cs.FullName -ieq $whitelisted) {
                $isAllowed = $true
                break
            }
        }
        if ($isAllowed) { continue }

        $content = Get-Content $cs.FullName -Raw
        foreach ($pat in $reflectionPatterns) {
            if ($content -match $pat) {
                $errors.Add("Prohibited dynamic/reflection API '$pat' found in '$($cs.FullName)'.")
            }
        }
    }

    return $errors
}

function Run-NegativeMatrix() {
    Write-Host "Running 11 negative boundary test cases..." -ForegroundColor Cyan
    $buildDir = (Join-Path (Get-Location).Path "build")
    New-Item -ItemType Directory -Force -Path $buildDir | Out-Null
    $tempBase = Join-Path $buildDir ("p42-neg-" + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Force -Path $tempBase | Out-Null

    try {
        $baseProj = Join-Path $tempBase "proj"
        New-Item -ItemType Directory -Force -Path $baseProj | Out-Null
        Copy-Item -Path (Join-Path $projectPath "Assets") -Destination (Join-Path $baseProj "Assets") -Recurse -Force
        if (Test-Path (Join-Path $projectPath "ProjectSettings")) {
            Copy-Item -Path (Join-Path $projectPath "ProjectSettings") -Destination (Join-Path $baseProj "ProjectSettings") -Recurse -Force
        }

        $cases = @(
            @{
                Name = "Case01: Self-loop"
                Mutate = {
                    param($p)
                    $file = Join-Path $p "Assets/Scripts/UI/Mahjong.Unity.UI.asmdef"
                    $json = Get-Content $file -Raw | ConvertFrom-Json
                    $json.references = @($json.references) + "Mahjong.Unity.UI"
                    $json | ConvertTo-Json -Depth 10 | Set-Content $file -Encoding utf8
                }
            },
            @{
                Name = "Case02: 2-node cycle (App <-> UI)"
                Mutate = {
                    param($p)
                    $file = Join-Path $p "Assets/Scripts/UI/Mahjong.Unity.UI.asmdef"
                    $json = Get-Content $file -Raw | ConvertFrom-Json
                    $json.references = @($json.references) + "Mahjong.Unity.App"
                    $json | ConvertTo-Json -Depth 10 | Set-Content $file -Encoding utf8
                }
            },
            @{
                Name = "Case03: 3-node cycle (App -> UI -> Platform -> App)"
                Mutate = {
                    param($p)
                    $fUi = Join-Path $p "Assets/Scripts/UI/Mahjong.Unity.UI.asmdef"
                    $jUi = Get-Content $fUi -Raw | ConvertFrom-Json
                    $jUi.references = @($jUi.references) + "Mahjong.Unity.Platform"
                    $jUi | ConvertTo-Json -Depth 10 | Set-Content $fUi -Encoding utf8

                    $fPl = Join-Path $p "Assets/Scripts/Platform/Mahjong.Unity.Platform.asmdef"
                    $jPl = Get-Content $fPl -Raw | ConvertFrom-Json
                    $jPl.references = @($jPl.references) + "Mahjong.Unity.App"
                    $jPl | ConvertTo-Json -Depth 10 | Set-Content $fPl -Encoding utf8
                }
            },
            @{
                Name = "Case04: Missing required reference in UI"
                Mutate = {
                    param($p)
                    $file = Join-Path $p "Assets/Scripts/UI/Mahjong.Unity.UI.asmdef"
                    $json = Get-Content $file -Raw | ConvertFrom-Json
                    $json.references = @("Unity.TextMeshPro", "UnityEngine.UI") # Missing Unity.InputSystem
                    $json | ConvertTo-Json -Depth 10 | Set-Content $file -Encoding utf8
                }
            },
            @{
                Name = "Case05: Extra prohibited reference in Platform"
                Mutate = {
                    param($p)
                    $file = Join-Path $p "Assets/Scripts/Platform/Mahjong.Unity.Platform.asmdef"
                    $json = Get-Content $file -Raw | ConvertFrom-Json
                    $json.references = @($json.references) + "Mahjong.Unity.UI"
                    $json | ConvertTo-Json -Depth 10 | Set-Content $file -Encoding utf8
                }
            },
            @{
                Name = "Case06: Precompiled DLL in references"
                Mutate = {
                    param($p)
                    $file = Join-Path $p "Assets/Scripts/UI/Mahjong.Unity.UI.asmdef"
                    $json = Get-Content $file -Raw | ConvertFrom-Json
                    $json.references = @($json.references) + "Mahjong.Client.dll"
                    $json | ConvertTo-Json -Depth 10 | Set-Content $file -Encoding utf8
                }
            },
            @{
                Name = "Case07: Forbidden Domain DLL in precompiledReferences of UI"
                Mutate = {
                    param($p)
                    $file = Join-Path $p "Assets/Scripts/UI/Mahjong.Unity.UI.asmdef"
                    $json = Get-Content $file -Raw | ConvertFrom-Json
                    $json.precompiledReferences = @($json.precompiledReferences) + "Mahjong.Domain.dll"
                    $json | ConvertTo-Json -Depth 10 | Set-Content $file -Encoding utf8
                }
            },
            @{
                Name = "Case08: Missing precompiledReferences in App"
                Mutate = {
                    param($p)
                    $file = Join-Path $p "Assets/Scripts/App/Mahjong.Unity.App.asmdef"
                    $json = Get-Content $file -Raw | ConvertFrom-Json
                    $json.precompiledReferences = @()
                    $json | ConvertTo-Json -Depth 10 | Set-Content $file -Encoding utf8
                }
            },
            @{
                Name = "Case09: overrideReferences set to false"
                Mutate = {
                    param($p)
                    $file = Join-Path $p "Assets/Scripts/App/Mahjong.Unity.App.asmdef"
                    $json = Get-Content $file -Raw | ConvertFrom-Json
                    $json.overrideReferences = $false
                    $json | ConvertTo-Json -Depth 10 | Set-Content $file -Encoding utf8
                }
            },
            @{
                Name = "Case10: LocalHost namespace used in UI source"
                Mutate = {
                    param($p)
                    $file = Join-Path $p "Assets/Scripts/UI/MenuPresenter.cs"
                    "using Mahjong.LocalHost;`n" + (Get-Content $file -Raw) | Set-Content $file -Encoding utf8
                }
            },
            @{
                Name = "Case11: Forbidden reflection API in App source"
                Mutate = {
                    param($p)
                    $file = Join-Path $p "Assets/Scripts/App/AppRoot.cs"
                    "// Test violation`npublic class BadReflection { public void Test() { System.Reflection.Assembly.Load(`"Mahjong.Domain`"); } }`n" + (Get-Content $file -Raw) | Set-Content $file -Encoding utf8
                }
            }
        )

        $negPassed = 0
        foreach ($c in $cases) {
            $caseDir = Join-Path $tempBase ([Guid]::NewGuid().ToString('N'))
            Copy-Item -Path $baseProj -Destination $caseDir -Recurse -Force
            & $c.Mutate $caseDir

            $errs = Test-SingleBoundaryRules $caseDir $policyPath
            if ($errs.Count -gt 0) {
                Write-Host "  [PASS] $($c.Name): correctly rejected with $($errs.Count) errors." -ForegroundColor Green
                $negPassed++
            }
            else {
                Write-Host "  [FAIL] $($c.Name): mutation was NOT rejected!" -ForegroundColor Red
            }
        }

        if ($negPassed -eq $cases.Count) {
            Write-Host "All 11 negative test cases passed successfully!" -ForegroundColor Green
            return 0
        }
        else {
            Write-Host "Negative test matrix failed: $negPassed / $($cases.Count) passed." -ForegroundColor Red
            return 1
        }
    }
    finally {
        if (Test-Path $tempBase) {
            try { Remove-Item -Path $tempBase -Recurse -Force -ErrorAction SilentlyContinue } catch {}
        }
    }
}

if ($TestNegativeMatrix) {
    $exitCode = Run-NegativeMatrix
    exit $exitCode
}

# Run regular boundary check on the workspace project
Write-Host "Running Unity boundary checks against $projectPath..." -ForegroundColor Cyan
$errors = Test-SingleBoundaryRules $projectPath $policyPath

$reportDir = Split-Path -Parent (Join-Path $repoRoot $Report)
if (-not (Test-Path $reportDir)) {
    New-Item -ItemType Directory -Force -Path $reportDir | Out-Null
}

$reportData = [ordered]@{
    timestamp = (Get-Date).ToString("yyyy-MM-ddTHH:mm:ssZ")
    project = $Project
    policy = $Policy
    errorCount = $errors.Count
    errors = $errors
    passed = ($errors.Count -eq 0)
}

$reportJson = $reportData | ConvertTo-Json -Depth 10
Set-Content -Path (Join-Path $repoRoot $Report) -Value $reportJson -Encoding utf8

if ($errors.Count -gt 0) {
    Write-Host "Boundary check FAILED with $($errors.Count) errors:" -ForegroundColor Red
    foreach ($err in $errors) {
        Write-Host "  - $err" -ForegroundColor Red
    }
    exit 1
}

Write-Host "Boundary check PASSED. All asmdefs and source boundaries satisfy policy." -ForegroundColor Green
exit 0
