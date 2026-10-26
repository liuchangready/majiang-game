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

$resolvedLog = if ([System.IO.Path]::IsPathRooted($LogFile)) {
    $LogFile
} else {
    Join-Path (Get-Location).Path $LogFile
}

Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms

$win32Def = @"
using System;
using System.Drawing;
using System.Runtime.InteropServices;

public class Win32Capture {
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT {
        public int Left, Top, Right, Bottom;
    }

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool MoveWindow(IntPtr hWnd, int X, int Y, int nWidth, int nHeight, bool bRepaint);

    [DllImport("user32.dll")]
    public static extern bool SetCursorPos(int X, int Y);

    [DllImport("user32.dll")]
    public static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, int dwExtraInfo);

    [DllImport("user32.dll")]
    public static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, int dwExtraInfo);

    public const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    public const uint MOUSEEVENTF_LEFTUP = 0x0004;
    public const byte VK_ESCAPE = 0x1B;
    public const uint KEYEVENTF_KEYUP = 0x0002;

    public static void ClickAt(int screenX, int screenY) {
        SetCursorPos(screenX, screenY);
        System.Threading.Thread.Sleep(50);
        mouse_event(MOUSEEVENTF_LEFTDOWN, (uint)screenX, (uint)screenY, 0, 0);
        System.Threading.Thread.Sleep(50);
        mouse_event(MOUSEEVENTF_LEFTUP, (uint)screenX, (uint)screenY, 0, 0);
    }

    public static void SendEscape() {
        keybd_event(VK_ESCAPE, 0, 0, 0);
        System.Threading.Thread.Sleep(50);
        keybd_event(VK_ESCAPE, 0, KEYEVENTF_KEYUP, 0);
    }

    [DllImport("user32.dll")]
    public static extern bool PrintWindow(IntPtr hWnd, IntPtr hdcBmp, uint nFlags);

    public static bool CaptureWindow(IntPtr hWnd, string savePath) {
        RECT rect;
        if (!GetWindowRect(hWnd, out rect)) {
            return false;
        }
        int width = rect.Right - rect.Left;
        int height = rect.Bottom - rect.Top;
        if (width <= 0 || height <= 0) return false;

        using (Bitmap bmp = new Bitmap(width, height)) {
            using (Graphics g = Graphics.FromImage(bmp)) {
                IntPtr hdc = g.GetHdc();
                PrintWindow(hWnd, hdc, 2);
                g.ReleaseHdc(hdc);
            }
            bmp.Save(savePath, System.Drawing.Imaging.ImageFormat.Png);
        }
        return true;
    }
}
"@
if (-not ([System.Management.Automation.PSTypeName]'Win32Capture').Type) {
    Add-Type -TypeDefinition $win32Def -Language CSharp -ReferencedAssemblies "System.Drawing"
}

Write-Host "Launching standalone player: $resolvedExe"
$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = $resolvedExe
$psi.Arguments = "-screen-width 1280 -screen-height 720 -screen-fullscreen 0 -logfile `"$resolvedLog`""
$psi.UseShellExecute = $false

$proc = [System.Diagnostics.Process]::Start($psi)

try {
    # Wait for process to finish loading and window to be ready
    $waited = 0
    $hWnd = [IntPtr]::Zero
    while ($waited -lt 30) {
        Start-Sleep -Seconds 1
        $proc.Refresh()
        $hWnd = $proc.MainWindowHandle
        if ($hWnd -ne [IntPtr]::Zero -and -not [string]::IsNullOrEmpty($proc.MainWindowTitle)) {
            Write-Host "Found ready window '$($proc.MainWindowTitle)' (Handle: $hWnd) at second $waited"
            [Win32Capture]::SetForegroundWindow($hWnd)
            [Win32Capture]::MoveWindow($hWnd, 50, 50, 1280, 720, $true)
            Start-Sleep -Seconds 4
            break
        }
        $waited++
    }

    if ($hWnd -eq [IntPtr]::Zero) {
        throw "Could not acquire MainWindowHandle within 30 seconds."
    }

    Write-Host "Player window handle: $hWnd, ProcessId: $($proc.Id)"

    # 1. Capture Menu screen
    $menuShot = Join-Path $resolvedScreenshotDir "01-menu.png"
    [Win32Capture]::CaptureWindow($hWnd, $menuShot)
    Write-Host "Captured: $menuShot"

    # Click "进入大厅" (PlayButton)
    # Window top-left is at (50, 50). Center of window is (50 + 640, 50 + 360) = (690, 410)
    # PlayButton pos is anchored at y=20 (slightly above center), so ~ (690, 390)
    [Win32Capture]::SetForegroundWindow($hWnd)
    [Win32Capture]::ClickAt(690, 390)
    Start-Sleep -Seconds 2

    # 2. Capture Lobby screen
    $lobbyShot = Join-Path $resolvedScreenshotDir "02-lobby.png"
    [Win32Capture]::CaptureWindow($hWnd, $lobbyShot)
    Write-Host "Captured: $lobbyShot"

    # Send Back/Escape to return to Menu
    [Win32Capture]::SetForegroundWindow($hWnd)
    [Win32Capture]::SendEscape()
    Start-Sleep -Seconds 2

    # 3. Capture Menu screen again
    $menuReturnShot = Join-Path $resolvedScreenshotDir "03-menu-returned.png"
    [Win32Capture]::CaptureWindow($hWnd, $menuReturnShot)
    Write-Host "Captured: $menuReturnShot"

    # Send Escape to open Quit Confirmation Modal
    [Win32Capture]::SetForegroundWindow($hWnd)
    [Win32Capture]::SendEscape()
    Start-Sleep -Seconds 1

    # 4. Capture Quit Modal
    $modalShot = Join-Path $resolvedScreenshotDir "04-quit-modal.png"
    [Win32Capture]::CaptureWindow($hWnd, $modalShot)
    Write-Host "Captured: $modalShot"

    # Dismiss modal by pressing Escape again (Cancel)
    [Win32Capture]::SetForegroundWindow($hWnd)
    [Win32Capture]::SendEscape()
    Start-Sleep -Seconds 1

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
