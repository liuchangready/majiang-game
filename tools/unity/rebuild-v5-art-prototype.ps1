param(
    [string]$Method = "Mahjong.Unity.EditorTools.V5ArtPrototypeBuilder.RebuildV5ArtPrototype"
)

$mutexName = 'Local\MahjongV5P45UnityValidation'
$mutex = New-Object System.Threading.Mutex($false, $mutexName)
$acquired = $false

try {
    Write-Host "Waiting for mutex $mutexName..."
    $acquired = $mutex.WaitOne([TimeSpan]::FromMinutes(5))
    if (-not $acquired) {
        Write-Error "Failed to acquire mutex within 5 minutes"
        exit 124
    }
    Write-Host "Mutex acquired! Launching Unity batchmode for $Method..."

    $unityPath = "D:\game_dev_tools\unity\Editor\Unity.exe"
    $projectPath = (Resolve-Path "unity/Mahjong.Unity").Path
    $logDir = Join-Path $projectPath "build"
    if (-not (Test-Path $logDir)) { New-Item -ItemType Directory -Path $logDir -Force | Out-Null }
    $logPath = Join-Path $logDir ("unity_v5_build_" + [Guid]::NewGuid().ToString('N') + ".log")

    $unityArgs = @(
        "-batchmode",
        "-nographics",
        "-projectPath", $projectPath,
        "-executeMethod", $Method,
        "-quit",
        "-logFile", $logPath
    )

    $process = Start-Process -FilePath $unityPath -ArgumentList $unityArgs -PassThru -Wait -WindowStyle Hidden
    Write-Host "Unity exited with code: $($process.ExitCode)"

    if (Test-Path $logPath) {
        Write-Host "=== Unity Log Tail ==="
        Get-Content $logPath -Tail 80 | Write-Host
    }

    if ($process.ExitCode -ne 0) {
        exit $process.ExitCode
    }
}
finally {
    if ($acquired) {
        $mutex.ReleaseMutex()
        Write-Host "Mutex released."
    }
}
