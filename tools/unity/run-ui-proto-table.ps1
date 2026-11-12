#requires -Version 5.1
[CmdletBinding()]
param([string]$RunRoot = '')

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
if ([string]::IsNullOrWhiteSpace($RunRoot)) {
    $candidate = Get-ChildItem -LiteralPath (Join-Path $repoRoot 'build\ui_proto') -Directory -ErrorAction Stop |
        ForEach-Object {
            $summaryPath = Join-Path $_.FullName 'summary.json'
            if (Test-Path -LiteralPath $summaryPath -PathType Leaf) {
                $summary = Get-Content -LiteralPath $summaryPath -Raw | ConvertFrom-Json
                $exePath = Join-Path $_.FullName 'player\ui_proto.exe'
                if ($summary.status -eq 'PASS' -and (Test-Path -LiteralPath $exePath -PathType Leaf)) {
                    [pscustomobject]@{ RunRoot = $_.FullName; Created = $_.LastWriteTime }
                }
            }
        } | Sort-Object Created -Descending | Select-Object -First 1
    if ($null -eq $candidate) { throw 'No successful ui_proto Windows Player build was found.' }
    $RunRoot = $candidate.RunRoot
}

$exe = Join-Path ([IO.Path]::GetFullPath($RunRoot)) 'player\ui_proto.exe'
if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) { throw "Player executable not found: $exe" }
$process = Start-Process -FilePath $exe -WorkingDirectory (Split-Path $exe) `
    -ArgumentList @('-screen-width', '1280', '-screen-height', '720', '-screen-fullscreen', '0', '--prototype-mahjong-table') -PassThru
Write-Host "UI_PROTO_TABLE_STARTED pid=$($process.Id) exe=$exe"
