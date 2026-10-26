param(
    [string]$PlayerExe = "build/p42/player/Mahjong.exe",
    [string]$ScreenshotDir = "build/p42/screenshots",
    [string]$LogFile = "build/unity-logs/player-run.log"
)

$ErrorActionPreference = "Stop"

Add-Type -AssemblyName System.Drawing

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
    $pngMagic = [byte[]]@(0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A)
    foreach ($shot in $shots) {
        if (-not (Test-Path $shot)) {
            throw "Screenshot was not generated: $shot"
        }
        $len = (Get-Item $shot).Length
        if ($len -lt 1000) {
            throw "Screenshot is suspiciously small ($len bytes): $shot"
        }
        $bytes = [System.IO.File]::ReadAllBytes($shot)
        if ($bytes.Length -lt 8) {
            throw "Screenshot file is too small to be a valid PNG: $shot"
        }
        for ($i = 0; $i -lt 8; $i++) {
            if ($bytes[$i] -ne $pngMagic[$i]) {
                throw "File is not a valid PNG image (invalid magic header): $shot"
            }
        }

        # Validate image decoding and resolution (1280x720)
        $img = $null
        try {
            $img = [System.Drawing.Image]::FromFile($shot)
            if ($img.Width -ne 1280 -or $img.Height -ne 720) {
                throw "Screenshot resolution is $($img.Width)x$($img.Height), expected 1280x720: $shot"
            }
        }
        catch {
            throw "Failed to decode screenshot as valid image (1280x720): $shot. Error: $_"
        }
        finally {
            if ($img -ne $null) {
                $img.Dispose()
            }
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
    if (-not (Test-Path $resolvedLog)) {
        throw "Player log file was not generated: $resolvedLog"
    }
    $logContent = Get-Content $resolvedLog -Raw
    if ([string]::IsNullOrWhiteSpace($logContent)) {
        throw "Player log file is empty: $resolvedLog"
    }

    # Strict lifecycle sequence assertion
    $requiredSteps = @(
        "SCENE_LOAD_COMPLETED:Menu",
        "SMOKE_STEP:NAVIGATE_TO_LOBBY",
        "SCENE_LOAD_COMPLETED:Lobby",
        "SMOKE_STEP:LOBBY_LOADED",
        "SMOKE_STEP:NAVIGATE_TO_MENU",
        "SCENE_LOAD_COMPLETED:Menu",
        "SMOKE_STEP:MENU_RETURNED",
        "SMOKE_STEP:MODAL_OPEN",
        "SMOKE_STEP:MODAL_CANCEL_CLICKED",
        "SMOKE_STEP:MODAL_REOPENED",
        "SMOKE_STEP:MODAL_CONFIRM_CLICKED",
        "QUIT_CONFIRMED: Application quit confirmed by user.",
        "QUIT_REQUESTED: Initiating application shutdown.",
        "SMOKE_SUCCESS: Application shutdown completed cleanly."
    )

    $lastIndex = -1
    foreach ($step in $requiredSteps) {
        $idx = $logContent.IndexOf($step, [Math]::Max(0, $lastIndex))
        if ($idx -lt 0) {
            throw "Player log missing required lifecycle step or step out of order: '$step' after index $lastIndex"
        }
        $lastIndex = $idx + $step.Length
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
