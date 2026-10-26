<#
.SYNOPSIS
    Static boundary and architectural policy checker for Unity assemblies in V5.
.DESCRIPTION
    Enforces DAG integrity, exact asmdef references/precompiledReferences sets,
    source-level namespace isolation, plugin closure, and reflection/dynamic API constraints.
    Supports -TestNegativeMatrix to verify rejection of negative boundary cases.
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
$projectPath = if ([System.IO.Path]::IsPathRooted($Project)) { $Project } else { Join-Path $repoRoot $Project }
$policyPath = if ([System.IO.Path]::IsPathRooted($Policy)) { $Policy } else { Join-Path $repoRoot $Policy }

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

    # 1. Plugin Closure Check
    $pluginsDir = Join-Path $projDir "Assets/Plugins/Mahjong"
    if (-not (Test-Path $pluginsDir)) {
        $errors.Add("MISSING_PLUGIN_CLOSURE: Assets/Plugins/Mahjong directory is missing.")
    } else {
        if ($policy.requiredPluginDlls) {
            foreach ($dll in $policy.requiredPluginDlls) {
                $dllPath = Join-Path $pluginsDir $dll
                if (-not (Test-Path $dllPath)) {
                    $errors.Add("MISSING_PLUGIN_CLOSURE: Required plugin DLL '$dll' is missing from '$pluginsDir'.")
                }
            }
        }
    }

    # 2. Asmdef Parsing, Unregistered & Duplicate Check (within Assets directory)
    $assetsDir = Join-Path $projDir "Assets"
    $asmdefFiles = if (Test-Path $assetsDir) { Get-ChildItem -Path $assetsDir -Recurse -Filter "*.asmdef" } else { @() }
    $asmdefsByName = @{}
    $seenNames = @{}

    foreach ($f in $asmdefFiles) {
        try {
            $json = Get-Content $f.FullName -Raw | ConvertFrom-Json
            $name = $json.name

            if ($seenNames.ContainsKey($name)) {
                $errors.Add("DUPLICATE_ASMDEF_NAME: Duplicate asmdef name '$name' found at $($f.FullName) and $($seenNames[$name]).")
            } else {
                $seenNames[$name] = $f.FullName
            }

            if (-not ($policy.projectAsmdefs -contains $name) -and -not ($policy.packageAsmdefs -contains $name)) {
                $errors.Add("UNREGISTERED_ASMDEF: Asmdef '$name' at $($f.FullName) is not registered in boundary policy.")
            }

            $asmdefsByName[$name] = @{
                File = $f
                Data = $json
            }
        }
        catch {
            $errors.Add("Failed to parse asmdef JSON at $($f.FullName): $_")
        }
    }

    # 3. Exact set, references, precompiledReferences and flags check
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

        # Check asmdef path against policy specification
        if ($spec.path) {
            $normProj = [System.IO.Path]::GetFullPath($projDir).TrimEnd('\', '/') + '\'
            $normFile = [System.IO.Path]::GetFullPath($asm.File.FullName)
            $actualRel = if ($normFile.StartsWith($normProj, [System.StringComparison]::OrdinalIgnoreCase)) {
                $normFile.Substring($normProj.Length).Replace('\', '/')
            } else {
                $asm.File.FullName
            }
            $expectedRel = $spec.path.Replace('\', '/')
            if ($actualRel -ne $expectedRel) {
                $errors.Add("ASMDEF_PATH_MISMATCH: Asmdef '$asmName' is located at '$actualRel', but policy requires '$expectedRel'.")
            }
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

    # 4. DAG Cycle check
    try {
        Assert-NoCycle $graph $projectNodes
    }
    catch {
        $errors.Add($_.Exception.Message)
    }

    # 5. Source code namespace isolation check
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

    # 6. Forbidden Concrete Client Implementations in UI/Platform
    $uiPlatformDirs = @(
        (Join-Path $projDir "Assets/Scripts/UI"),
        (Join-Path $projDir "Assets/Scripts/Platform")
    )
    $forbiddenImpls = @($policy.forbiddenClientImplementationsInUI)
    foreach ($dir in $uiPlatformDirs) {
        if (Test-Path $dir) {
            $csFiles = Get-ChildItem -Path $dir -Recurse -Filter "*.cs"
            foreach ($cs in $csFiles) {
                $content = Get-Content $cs.FullName -Raw
                if ($content -match '\bnew\s+Mahjong\.Client\.' -or $content -match '\bMahjong\.Client\.(FileOutboxStore|TableSession|TableSessionOperationQueue)\b') {
                    $errors.Add("FORBIDDEN_CLIENT_IMPLEMENTATION: Concrete Mahjong.Client implementation instantiated or referenced in '$($cs.FullName)'.")
                }
                foreach ($fImpl in $forbiddenImpls) {
                    if ($content -match "\bnew\s+$([regex]::Escape($fImpl))\b") {
                        $errors.Add("FORBIDDEN_CLIENT_IMPLEMENTATION: Forbidden client concrete class '$fImpl' instantiated in '$($cs.FullName)'.")
                    }
                }
            }
        }
    }

    # 7. Source reflection / dynamic API scan
    $allCs = Get-ChildItem -Path (Join-Path $projDir "Assets/Scripts") -Recurse -Filter "*.cs"
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
        # Strip #if UNITY_EDITOR ... #endif blocks so editor-only reflection is ignored, but production runtime reflection is caught
        $cleanContent = [regex]::Replace($content, '(?s)#if\s+UNITY_EDITOR.*?#endif', '')
        foreach ($pat in $reflectionPatterns) {
            if ($cleanContent -match $pat) {
                $errors.Add("Prohibited dynamic/reflection API '$pat' found in '$($cs.FullName)'.")
            }
        }
    }

    # 8. Assembly Metadata Verification (IGameSession, ITableChannel, Wire DTO)
    if (Test-Path $pluginsDir) {
        $clientDll = Join-Path $pluginsDir "Mahjong.Client.dll"
        if (Test-Path $clientDll) {
            $bytes = [System.IO.File]::ReadAllBytes($clientDll)
            $str = [System.Text.Encoding]::UTF8.GetString($bytes)
            $requiredTypes = @("IGameSession", "ITableChannel", "TableSession")
            foreach ($req in $requiredTypes) {
                if (-not $str.Contains($req)) {
                    $errors.Add("METADATA_CHECK_FAILED: Required type or contract '$req' was not found in '$clientDll'.")
                }
            }
        }
        $probeCsproj = Join-Path $repoRoot "build/tools/metadata-probe/MetadataProbe.csproj"
        $dotnetExe = "D:\Unity\dotnet\dotnet.exe"
        if (-not (Test-Path $dotnetExe)) {
            $dotnetExe = "dotnet"
        }
        if (Test-Path $probeCsproj) {
            $metaOut = & $dotnetExe run --project $probeCsproj -- $pluginsDir 2>&1
            if ($LASTEXITCODE -ne 0) {
                $errors.Add("METADATA_CHECK_FAILED: $metaOut")
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
                    $json.references = @($json.references | Where-Object { $_ -ne "Unity.TextMeshPro" })
                    $json | ConvertTo-Json -Depth 10 | Set-Content $file -Encoding utf8
                }
            },
            @{
                Name = "Case05: Extra prohibited reference in Platform"
                Mutate = {
                    param($p)
                    $file = Join-Path $p "Assets/Scripts/Platform/Mahjong.Unity.Platform.asmdef"
                    $json = Get-Content $file -Raw | ConvertFrom-Json
                    $json.references = @($json.references) + "UnityEngine.UI"
                    $json | ConvertTo-Json -Depth 10 | Set-Content $file -Encoding utf8
                }
            },
            @{
                Name = "Case06: Precompiled DLL in references"
                Mutate = {
                    param($p)
                    $file = Join-Path $p "Assets/Scripts/App/Mahjong.Unity.App.asmdef"
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
                    $json.precompiledReferences = @($json.precompiledReferences | Where-Object { $_ -ne "Mahjong.Client.dll" })
                    $json | ConvertTo-Json -Depth 10 | Set-Content $file -Encoding utf8
                }
            },
            @{
                Name = "Case09: overrideReferences set to false"
                Mutate = {
                    param($p)
                    $file = Join-Path $p "Assets/Scripts/UI/Mahjong.Unity.UI.asmdef"
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
                    $file = Join-Path $p "Assets/Scripts/App/SceneId.cs"
                    (Get-Content $file -Raw) + "`n// System.Reflection.Assembly.LoadFrom(`"bad.dll`");" | Set-Content $file -Encoding utf8
                }
            }
        )

        $passCount = 0
        foreach ($case in $cases) {
            $caseDir = Join-Path $tempBase ("case-" + [Guid]::NewGuid().ToString('N'))
            Copy-Item -Path $baseProj -Destination $caseDir -Recurse -Force
            & $case.Mutate $caseDir

            $errs = Test-SingleBoundaryRules $caseDir $policyPath
            if ($errs.Count -gt 0) {
                Write-Host "  [PASS] $($case.Name): correctly rejected with $($errs.Count) errors." -ForegroundColor Green
                $passCount++
            }
            else {
                Write-Host "  [FAIL] $($case.Name): falsely passed boundary checks!" -ForegroundColor Red
                throw "Negative case '$($case.Name)' did not trigger any errors."
            }
        }

        Write-Host "All $($cases.Count) negative test cases passed successfully!" -ForegroundColor Green
    }
    finally {
        if (Test-Path $tempBase) {
            Remove-Item $tempBase -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}

# Execution Entry Point
if ($TestNegativeMatrix) {
    Run-NegativeMatrix
    exit 0
}

Write-Host "Running Unity boundary checks against $projectPath..."
$errors = Test-SingleBoundaryRules $projectPath $policyPath

$reportData = @{
    Timestamp = (Get-Date).ToString("o")
    ProjectPath = $projectPath
    PolicyPath = $policyPath
    Passed = ($errors.Count -eq 0)
    ErrorCount = $errors.Count
    Errors = $errors
}

$reportDir = Split-Path -Parent $Report
if ($reportDir -and (-not (Test-Path $reportDir))) {
    New-Item -ItemType Directory -Force -Path $reportDir | Out-Null
}
$reportData | ConvertTo-Json -Depth 5 | Set-Content -Path $Report -Encoding utf8

if ($errors.Count -gt 0) {
    Write-Host "Boundary check FAILED with $($errors.Count) errors:" -ForegroundColor Red
    foreach ($e in $errors) {
        Write-Host "  - $e" -ForegroundColor Red
    }
    exit 1
}

Write-Host "Boundary check PASSED. All asmdefs and source boundaries satisfy policy." -ForegroundColor Green
exit 0
