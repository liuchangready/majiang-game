#requires -Version 5.1
# Exercise the production launcher without starting a database or full suite.
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$source = Join-Path $PSScriptRoot 'verify-v5-table-postgres.ps1'
$tokens = $null
$parseErrors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile($source, [ref]$tokens, [ref]$parseErrors)
$fn = $ast.Find({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Invoke-Native' }, $true)
if (-not $fn -or $parseErrors.Count) { throw 'Cannot extract production Invoke-Native' }
. ([scriptblock]::Create($fn.Extent.Text))
$evidenceDir = Join-Path $repoRoot ('artifacts/native-exit-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $evidenceDir | Out-Null
$child = Join-Path $evidenceDir 'child with spaces.ps1'
Set-Content -LiteralPath $child -Encoding UTF8 -Value @'
param([int]$Code, [int]$Delay = 0)
Write-Output "stdout code=$Code"
[Console]::Error.WriteLine("stderr code=$Code")
if ($Delay) { Start-Sleep -Seconds $Delay }
exit $Code
'@
$hostExe = [System.Diagnostics.Process]::GetCurrentProcess().MainModule.FileName
$results = foreach ($code in @(0, 1, 2, 17)) {
    $log = Join-Path $evidenceDir "exit-$code.log"
    $actual = Invoke-Native -FilePath $hostExe -Arguments @('-NoProfile','-NonInteractive','-File',$child,'-Code',"$code") -LogPath $log
    $output = Get-Content -LiteralPath $log -Raw
    [pscustomobject]@{ Case="exit-$code"; Pass=($actual -eq $code -and $output.Contains("stdout code=$code") -and $output.Contains("stderr code=$code")); Expected=$code; Actual=$actual }
}
$timedOut = $false
try {
    $null = Invoke-Native -FilePath $hostExe -Arguments @('-NoProfile','-NonInteractive','-File',$child,'-Delay','20') -LogPath (Join-Path $evidenceDir 'timeout.log') -TimeoutSeconds 1
} catch {
    if ($_.Exception.Message -notlike 'Command timed out*') { throw }
    $timedOut = $true
}
$results += [pscustomobject]@{ Case='timeout'; Pass=$timedOut; Expected='throw'; Actual=$timedOut }
foreach ($name in @('Invoke-WriterAndKill', 'Get-Sha256')) {
    $functionAst = $ast.Find({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name }, $true)
    if (-not $functionAst) { throw "Missing production function $name" }
    . ([scriptblock]::Create($functionAst.Extent.Text))
}
$writer = Join-Path $evidenceDir 'writer.ps1'
$marker = Join-Path $evidenceDir 'writer.marker'
# These literals contain only this run's generated local path. No DB is used.
$writerScript = "[IO.File]::WriteAllText('" + $marker.Replace("'", "''") + "', 'ready'); Start-Sleep -Seconds 30"
Set-Content -LiteralPath $writer -Encoding UTF8 -Value $writerScript
$DotnetPath = $hostExe
$writerLog = Join-Path $evidenceDir 'writer.log'
$killResult = Invoke-WriterAndKill -WorkerDll $writer -MarkerPath $marker -LogPath $writerLog -TimeoutSeconds 10
$killRecord = Get-Content -LiteralPath ($writerLog + '.kill.json') -Raw | ConvertFrom-Json
$results += [pscustomobject]@{ Case='forced-writer'; Pass=($killResult.killed -and $killResult.exitCode -ne 0 -and $killRecord.exitCode -eq $killResult.exitCode -and $killRecord.pid -eq $killResult.pid -and -not (Get-Process -Id $killResult.pid -ErrorAction SilentlyContinue)); Expected='nonzero'; Actual=$killResult.exitCode }
$results | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $evidenceDir 'results.json') -Encoding UTF8
$results | Format-Table -AutoSize
Write-Output "Evidence: $evidenceDir"
if (@($results | Where-Object { -not $_.Pass }).Count) { exit 1 }
exit 0
