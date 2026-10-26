param(
    [string]$PlayerExe = "build/p42/player/Mahjong.exe",
    [string]$ScreenshotDir = "build/p42/screenshots",
    [string]$LogFile = "build/unity-logs/player-run.log"
)

$ErrorActionPreference = "Stop"

$resolvedExe = (Resolve-Path $PlayerExe).Path
if (-not (Test-Path $resolvedExe)) {
    throw "Player executable not found: $resolvedExe"
}

$resolvedScreenshotDir = if ([System.IO.Path]::IsPathRooted($ScreenshotDir)) {
    $ScreenshotDir
} else {
    Join-Path (Get-Location).Path $ScreenshotDir
}
New-Item -ItemType Directory -Force -Path $resolvedScreenshotDir | Out-Null

# Clean any existing screenshots in target directory
Get-ChildItem -Path $resolvedScreenshotDir -Filter "*.png" | Remove-Item -Force

$resolvedLog = if ([System.IO.Path]::IsPathRooted($LogFile)) {
    $LogFile
} else {
    Join-Path (Get-Location).Path $LogFile
}
if (Test-Path $resolvedLog) {
    Remove-Item $resolvedLog -Force
}

Write-Host "Launching standalone player: $resolvedExe"
Write-Host "Screenshots target: $resolvedScreenshotDir"
Write-Host "Log target: $resolvedLog"

$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = $resolvedExe
$psi.Arguments = "-screen-width 1280 -screen-height 720 -screen-fullscreen 0 -logfile `"$resolvedLog`" -smoke-test `"$resolvedScreenshotDir`""
$psi.UseShellExecute = $false

$proc = [System.Diagnostics.Process]::Start($psi)

try {
    Write-Host "Waiting for player process $($proc.Id) to complete automated smoke sequence..."
    $cleanExit = $proc.WaitForExit(45000)
    if ($cleanExit) {
        Write-Host "Player exited cleanly with ExitCode: $($proc.ExitCode)"
        if ($proc.ExitCode -ne 0) {
            throw "Player process exited with error ExitCode: $($proc.ExitCode)"
        }
    } else {
        throw "Player smoke test timed out after 45 seconds."
    }

    # Assertions on captured screenshots
    $menuShot = Join-Path $resolvedScreenshotDir "01-menu.png"
    $lobbyShot = Join-Path $resolvedScreenshotDir "02-lobby.png"
    $menuReturnShot = Join-Path $resolvedScreenshotDir "03-menu-returned.png"
    $modalShot = Join-Path $resolvedScreenshotDir "04-quit-modal.png"
    $shots = @($menuShot, $lobbyShot, $menuReturnShot, $modalShot)
    foreach ($shot in $shots) {
        if (-not (Test-Path $shot)) {
            throw "Screenshot was not generated: $shot"
        }
        $len = (Get-Item $shot).Length
        if ($len -lt 10000) {
            throw "Screenshot is suspiciously small ($len bytes): $shot"
        }
    }

    $hashMenu = (Get-FileHash -Algorithm SHA256 $menuShot).Hash
    $hashLobby = (Get-FileHash -Algorithm SHA256 $lobbyShot).Hash
    $hashMenuReturn = (Get-FileHash -Algorithm SHA256 $menuReturnShot).Hash
    $hashModal = (Get-FileHash -Algorithm SHA256 $modalShot).Hash

    Write-Host "01-menu.png          SHA256: $hashMenu"
    Write-Host "02-lobby.png         SHA256: $hashLobby"
    Write-Host "03-menu-returned.png SHA256: $hashMenuReturn"
    Write-Host "04-quit-modal.png    SHA256: $hashModal"

    if ($hashMenu -eq $hashLobby) {
        throw "Menu and Lobby screenshots are identical (hash: $hashMenu). Visual navigation failed to render distinct scene."
    }
    if ($hashMenu -eq $hashModal) {
        throw "Menu and Modal screenshots are identical (hash: $hashMenu). Modal overlay failed to render."
    }

    # Log assertions
    if (Test-Path $resolvedLog) {
        $logContent = Get-Content $resolvedLog -Raw
        if (-not $logContent.Contains("SMOKE_SUCCESS")) {
            throw "Log file does not contain SMOKE_SUCCESS marker."
        }
        if (-not $logContent.Contains("SCENE_LOAD_COMPLETED:Menu")) {
            throw "Log file does not contain Menu scene load confirmation."
        }
        if (-not $logContent.Contains("SCENE_LOAD_COMPLETED:Lobby")) {
            throw "Log file does not contain Lobby scene load confirmation."
        }
    }

    Write-Host "Player smoke test completed successfully!"
}
finally {
    if (-not $proc.HasExited) {
        Write-Host "Stopping player process $($proc.Id)..."
        $proc.CloseMainWindow() | Out-Null
        $proc.WaitForExit(3000)
        if (-not $proc.HasExited) {
            $proc.Kill()
        }
    }
}
