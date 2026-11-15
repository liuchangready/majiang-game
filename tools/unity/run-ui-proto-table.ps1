#requires -Version 5.1
[CmdletBinding()]
param(
    [string]$RunRoot = '',
    [ValidateSet('v1','v2','v3')][string]$PrototypeVersion = 'v1'
)

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
$size = if ($PrototypeVersion -in @('v2','v3')) { @('1200', '690') } else { @('1280', '720') }
$switch = switch ($PrototypeVersion) {
    'v2' { '--prototype-mahjong-table-v2' }
    'v3' { '--prototype-mahjong-table-v3' }
    default { '--prototype-mahjong-table' }
}
$process = Start-Process -FilePath $exe -WorkingDirectory (Split-Path $exe) `
    -ArgumentList @('-screen-width', $size[0], '-screen-height', $size[1], '-screen-fullscreen', '0', $switch) -PassThru
Write-Host "UI_PROTO_TABLE_STARTED version=$PrototypeVersion pid=$($process.Id) exe=$exe"
