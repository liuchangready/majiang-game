#requires -Version 5.1
[CmdletBinding()]
param([string]$OutputRoot = '')

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$source = Join-Path $repoRoot 'unity\Mahjong.Unity\Assets\Scripts\UI\Prototype\MahjongWallGeometry.cs'
if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { throw "Geometry source missing: $source" }
if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $repoRoot ('build\ui_proto\geometry-check\' + [guid]::NewGuid().ToString('N'))
}
$runRoot = [IO.Path]::GetFullPath($OutputRoot)
if (Test-Path -LiteralPath $runRoot) { throw "Refusing to overwrite geometry check output: $runRoot" }
$temp = Join-Path $runRoot 'temp'
[IO.Directory]::CreateDirectory($temp) | Out-Null
$env:TEMP = $temp
$env:TMP = $temp
$resultPath = Join-Path $runRoot 'geometry-result.txt'
try {
    Add-Type -Path $source
    $result = [Mahjong.Unity.UI.Prototype.MahjongWallGeometry]::RunSelfCheck()
    if ($result -notmatch '^GEOMETRY_SELF_CHECK_PASS assertions=\d+ triangles=\d+ zeroArea=0 seats=3 positiveRows=3 sharedCorners=2 negativeCases=3 North=140:0 West=110:0 East=110:0$') {
        throw "Unexpected geometry self-check output: $result"
    }
    Set-Content -LiteralPath $resultPath -Value $result -Encoding utf8
    Write-Output $result
    Write-Output "GEOMETRY_EVIDENCE=$resultPath"
    exit 0
}
catch {
    Set-Content -LiteralPath $resultPath -Value ("GEOMETRY_SELF_CHECK_FAIL`n" + $_.ToString()) -Encoding utf8
    Write-Error $_
    exit 1
}
