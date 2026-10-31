#Requires -Version 5.1
[CmdletBinding()]
param([Parameter(Mandatory=$true)][ValidateSet('Devices','Install','Start','Stop','Capture','DeviceInfo')][string]$Action,
      [string]$Serial,[Parameter(Mandatory=$true)][string]$Apk,[string]$EvidenceDir,[string]$PackageId='com.majiang.game')
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$android=if ($env:C05_ADB_PATH) { [IO.Path]::GetFullPath($env:C05_ADB_PATH) } else { 'D:\Unity\Hub\Editor\6000.3.27f1\Editor\Data\PlaybackEngines\AndroidPlayer\SDK\platform-tools\adb.exe' }
if (-not (Test-Path $android)) { throw 'BLOCKED: pinned Unity Android SDK adb.exe is unavailable.' }
function Invoke-Adb([string[]]$AdbArgs) {
  $psi=New-Object Diagnostics.ProcessStartInfo; $psi.FileName=$android; $psi.UseShellExecute=$false; $psi.CreateNoWindow=$true; $psi.WindowStyle='Hidden'; $psi.RedirectStandardOutput=$true; $psi.RedirectStandardError=$true
  $quoted=@(); foreach($arg in $AdbArgs) { $q=[string]$arg; $q=$q -replace '(\\*)"','$1$1\"'; $q=$q -replace '(\\+)$','$1$1'; $quoted+='"'+$q+'"' }; $psi.Arguments=$quoted -join ' '
  $proc=New-Object Diagnostics.Process; $proc.StartInfo=$psi; [void]$proc.Start(); $outTask=$proc.StandardOutput.ReadToEndAsync(); $errTask=$proc.StandardError.ReadToEndAsync(); $proc.WaitForExit(); $stdout=$outTask.Result; $stderr=$errTask.Result; $parts=@(); if($stdout){$parts+=$stdout.TrimEnd()}; if($stderr){$parts+=$stderr.TrimEnd()}; [pscustomobject]@{exit=$proc.ExitCode;output=($parts -join "`n")}
}
function Get-ActionEvidenceDir {
  $base=[IO.Path]::GetFullPath((Join-Path $root 'build'))+ [IO.Path]::DirectorySeparatorChar
  if ($EvidenceDir) { $parent=[IO.Path]::GetFullPath($EvidenceDir); if (-not $parent.StartsWith($base,[StringComparison]::OrdinalIgnoreCase)) { throw 'Refusing evidence output outside this worktree build directory.' }; if (-not (Test-Path $parent)) { New-Item -ItemType Directory -Force -Path $parent | Out-Null }; $candidate=Join-Path $parent ((Get-Date -Format 'yyyyMMdd-HHmmss')+'-'+[guid]::NewGuid().ToString('N').Substring(0,8)) } else { $candidate=Join-Path $root ('build\p45-android-game-device\'+(Get-Date -Format 'yyyyMMdd-HHmmss')+'-'+[guid]::NewGuid().ToString('N').Substring(0,8)) }
  if (-not $candidate.StartsWith($base,[StringComparison]::OrdinalIgnoreCase)) { throw 'Refusing evidence output outside this worktree build directory.' }
  if (Test-Path $candidate) { throw 'Refusing to reuse an existing device evidence directory.' }
  New-Item -ItemType Directory -Force -Path $candidate | Out-Null; return $candidate
}
function Save-ActionResult([string]$Dir,[string]$Name,[object]$Value) { [IO.File]::WriteAllText((Join-Path $Dir $Name),($Value|ConvertTo-Json -Depth 8),[Text.Encoding]::UTF8) }
$devices=Invoke-Adb @('devices','-l'); if ($devices.exit -ne 0) { throw "adb devices failed exit=$($devices.exit): $($devices.output)" }
$rows=@($devices.output -split "`r?`n" | Select-String '^([^ ]+)\s+(device|unauthorized|offline)\b' | ForEach-Object { if ($_.Matches[0].Groups[2].Value -eq 'device') { $_.Matches[0].Groups[1].Value } })
if ($Action -eq 'Devices') { $devices.output; if ($rows.Count -eq 0) { Write-Host 'DEVICE_STATUS=NOT_RUN (no authorized device)' }; exit 0 }
if (-not $Serial) { if ($rows.Count -eq 1) { $Serial=$rows[0] } elseif ($rows.Count -gt 1) { throw 'BLOCKED: multiple authorized devices; pass -Serial explicitly.' } else { if ($devices.output -match 'unauthorized') { throw 'BLOCKED: device unauthorized; authorize USB debugging and retry.' }; throw 'NOT_RUN: no authorized device is connected.' } }
if ($rows -notcontains $Serial) { if ($devices.output -match ('(?m)^' + [regex]::Escape($Serial) + '\s+unauthorized\b')) { throw "BLOCKED: serial '$Serial' is unauthorized; no device action taken." }; if ($devices.output -match ('(?m)^' + [regex]::Escape($Serial) + '\s+offline\b')) { throw "BLOCKED: serial '$Serial' is offline; no device action taken." }; throw "BLOCKED: serial '$Serial' is not in authorized device list; no device action taken. $($devices.output)" }
if ($Action -eq 'DeviceInfo') {
  $dir=Get-ActionEvidenceDir; $items=@(); foreach ($cmd in @(@('shell','wm','size'),@('shell','wm','density'),@('shell','dumpsys','display'),@('shell','settings','get','system','user_rotation'),@('shell','dumpsys','window','displays'))) { $r=Invoke-Adb (@('-s',$Serial)+$cmd); $items+= [pscustomobject]@{command=($cmd -join ' ');exit=$r.exit;output=$r.output}; "exit=$($r.exit) $($r.output)" }; Save-ActionResult $dir 'device-info.json' ([pscustomobject]@{serial=$Serial;observations=$items}); Write-Host 'DEVICE_EVIDENCE=build-relative'; exit 0
}
switch ($Action) {
 'Install' {
   $dir=Get-ActionEvidenceDir
   $apkPath=[IO.Path]::GetFullPath($Apk); if (-not (Test-Path $apkPath -PathType Leaf)) { throw 'FAIL: APK does not exist.' }
   $r=Invoke-Adb @('-s',$Serial,'install','-r',$apkPath); $r.output; Save-ActionResult $dir 'install-result.json' ([pscustomobject]@{serial=$Serial;exit=$r.exit;output=$r.output;apkSha256=(Get-FileHash $apkPath -Algorithm SHA256).Hash}); if ($r.exit -ne 0) { throw "Install failed exit=$($r.exit); raw adb output saved under build-relative evidence." }; exit 0
 }
 'Start' {
   $dir=Get-ActionEvidenceDir
   $r=Invoke-Adb @('-s',$Serial,'shell','monkey','-p',$PackageId,'-c','android.intent.category.LAUNCHER','1'); $r.output
   if ($r.exit -ne 0 -or $r.output -match 'No activities found') { Save-ActionResult $dir 'launch-result.json' ([pscustomobject]@{serial=$Serial;exit=$r.exit;output=$r.output}); throw "Launch failed exit=$($r.exit); raw adb output saved under build-relative evidence." }
   $pidResult=Invoke-Adb @('-s',$Serial,'shell','pidof',$PackageId); "pid=$($pidResult.output.Trim())"; Save-ActionResult $dir 'launch-result.json' ([pscustomobject]@{serial=$Serial;exit=$r.exit;output=$r.output;pid=$pidResult.output.Trim()}); if (-not $pidResult.output.Trim()) { throw 'Launch command returned but target process is not observable.' }; exit 0
 }
 'Stop' { $dir=Get-ActionEvidenceDir; $r=Invoke-Adb @('-s',$Serial,'shell','am','force-stop',$PackageId); $r.output; Save-ActionResult $dir 'stop-result.json' ([pscustomobject]@{serial=$Serial;package=$PackageId;exit=$r.exit;output=$r.output}); if ($r.exit -ne 0) { throw "Stop failed exit=$($r.exit); raw adb output saved under build-relative evidence." }; exit 0 }
 'Capture' {
   $dir=Get-ActionEvidenceDir
   $stamp=Get-Date -Format 'yyyyMMdd-HHmmss'; $png=Join-Path $dir "$stamp-screen.png"; $log=Join-Path $dir "$stamp-logcat.txt"; $info=Join-Path $dir "$stamp-device.txt"
   $psi=New-Object Diagnostics.ProcessStartInfo; $psi.FileName=$android; $psi.Arguments="-s `"$Serial`" exec-out screencap -p"; $psi.UseShellExecute=$false; $psi.CreateNoWindow=$true; $psi.RedirectStandardOutput=$true; $psi.RedirectStandardError=$true
   $capture=New-Object Diagnostics.Process; $capture.StartInfo=$psi; [void]$capture.Start(); $fs=[IO.File]::Create($png); try { $capture.StandardOutput.BaseStream.CopyTo($fs) } finally { $fs.Dispose() }; $stderr=$capture.StandardError.ReadToEnd(); $capture.WaitForExit(); if ($capture.ExitCode -ne 0) { throw "Screenshot capture failed exit=$($capture.ExitCode): $stderr" }
   if ((Get-Item $png).Length -lt 256) { throw 'FAIL: screenshot is empty or too small to inspect.' }
   $pidResult=Invoke-Adb @('-s',$Serial,'shell','pidof',$PackageId); $pidText=$pidResult.output.Trim(); $logResult=if($pidText){Invoke-Adb @('-s',$Serial,'logcat','-d','-v','threadtime','--pid',$pidText)}else{[pscustomobject]@{exit=-1;output='NOT_RUN: target package process not running; no logcat PID filter available.'}}
   [IO.File]::WriteAllText($log,$logResult.output,[Text.Encoding]::UTF8)
   foreach ($cmd in @(@('shell','wm','size'),@('shell','wm','density'),@('shell','settings','get','system','user_rotation'),@('shell','dumpsys','window','displays'))) { $v=Invoke-Adb (@('-s',$Serial)+$cmd); Add-Content -Path $info -Value ($v.output) }
   $bytes=Get-Item $png; $hash=(Get-FileHash $png -Algorithm SHA256).Hash
   $visual='UNASSESSED'; try { Add-Type -AssemblyName System.Drawing; $bitmap=[Drawing.Bitmap]::FromFile($png); $nonBlack=$false; for ($x=0;$x -lt $bitmap.Width -and -not $nonBlack;$x+= [Math]::Max(1,[int]($bitmap.Width/40))) { for ($y=0;$y -lt $bitmap.Height;$y+= [Math]::Max(1,[int]($bitmap.Height/40))) { $c=$bitmap.GetPixel($x,$y); if ($c.R -gt 8 -or $c.G -gt 8 -or $c.B -gt 8) { $nonBlack=$true; break } } }; $bitmap.Dispose(); if (-not $nonBlack) { $visual='BLACK_OR_NEAR_BLACK_REQUIRES_INVESTIGATION' } } catch { $visual='DECODE_FAILED_REQUIRES_INVESTIGATION' }
   [pscustomobject]@{status='CAPTURED_REQUIRES_HUMAN_VISUAL_REVIEW';visualSample=$visual;serial=$Serial;screenshot=(Split-Path $png -Leaf);bytes=$bytes.Length;sha256=$hash;logcat=(Split-Path $log -Leaf);deviceInfo=(Split-Path $info -Leaf);screenshotIsGamePass=$false} | ConvertTo-Json | Set-Content -Encoding UTF8 (Join-Path $dir "$stamp-capture-summary.json")
   Write-Host "CAPTURED bytes=$($bytes.Length) SHA256=$hash; inspect screenshot semantics manually."; exit 0
 }
}
