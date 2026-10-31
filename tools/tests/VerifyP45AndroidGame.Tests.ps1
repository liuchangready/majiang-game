#Requires -Version 5.1
$ErrorActionPreference='Continue'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$runner=Join-Path $root 'tools\unity\run-p45-android-game.ps1'
$build=Join-Path $root 'tools\unity\build-p45-android-game.ps1'
$temp=Join-Path ([IO.Path]::GetTempPath()) ('c05-tool-fixture-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temp | Out-Null
try {
  $fixture=Join-Path $temp 'adb-fixture.exe'
  $fixtureApk=Join-Path $temp 'ignored.apk'; [IO.File]::WriteAllBytes($fixtureApk,[byte[]]@(1,2,3))
  $fixtureSource=@'
using System; using System.IO; using System.Linq;
public static class AdbFixture { public static int Main(string[] a) {
 var state=Environment.GetEnvironmentVariable("C05_FIXTURE_STATE")??"none";
 if(a.Length>0 && a[0]=="devices") { Console.WriteLine("List of devices attached"); if(state=="unauthorized") Console.WriteLine("emulator-5554 unauthorized usb:1-1"); if(state=="single"||state=="multiple") Console.WriteLine("fixture-01 device product:test model:test transport_id:1"); if(state=="multiple") Console.WriteLine("fixture-02 device product:test model:test transport_id:2"); return 0; }
 if(a.Contains("exec-out")) { if((Environment.GetEnvironmentVariable("C05_FIXTURE_SCREEN")??"")=="black") using(var f=File.OpenRead(Environment.GetEnvironmentVariable("C05_FIXTURE_PNG"))) f.CopyTo(Console.OpenStandardOutput()); return 0; }
 if(a.Contains("install")) { Console.Error.WriteLine("INSTALL_FAILED_USER_RESTRICTED"); return 12; }
 if(a.Contains("logcat")) { Console.WriteLine("fixture logcat"); return 0; }
 if(a.Contains("pidof")) { Console.WriteLine("24680"); return 0; }
 Console.WriteLine("fixture device info"); return 0;
} }
'@
  Add-Type -TypeDefinition $fixtureSource -OutputAssembly $fixture -OutputType ConsoleApplication
  $env:C05_ADB_PATH=$fixture
  $env:C05_FIXTURE_STATE='unauthorized'
  $r=& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $runner -Action Install -Serial emulator-5554 -Apk ignored.apk 2>&1; $code=$LASTEXITCODE
  if ($code -eq 0 -or ($r -join "`n") -notmatch 'unauthorized') { throw 'unauthorized device was not fail-closed' }
  $env:C05_FIXTURE_STATE='none'
  $r=& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $runner -Action Install -Apk ignored.apk 2>&1; $code=$LASTEXITCODE
  if ($code -eq 0 -or ($r -join "`n") -notmatch 'no authorized device') { throw 'no-device case was not reported' }
  $env:C05_FIXTURE_STATE='multiple'
  $r=& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $runner -Action Install -Apk ignored.apk 2>&1; $code=$LASTEXITCODE
  if ($code -eq 0 -or ($r -join "`n") -notmatch 'multiple authorized devices') { throw 'ambiguous serial was not rejected' }
  $env:C05_FIXTURE_STATE='single'
  $r=& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $runner -Action Install -Serial fixture-01 -Apk $fixtureApk 2>&1; $code=$LASTEXITCODE
  if ($code -eq 0 -or ($r -join "`n") -notmatch 'Install failed exit=12') { throw 'adb install failure was swallowed' }
  $outRoot=Join-Path $root 'build\p45-android-game-device-tool-fixture'; $blackDir=Join-Path $outRoot 'black'; $emptyDir=Join-Path $outRoot 'empty'
  if (Test-Path $outRoot) { throw 'Refusing to reuse fixture evidence output.' }; New-Item -ItemType Directory -Path $outRoot | Out-Null
  Add-Type -AssemblyName System.Drawing; $bmp=New-Object Drawing.Bitmap(512,512); try { $bmp.Save((Join-Path $temp 'black.png'),[Drawing.Imaging.ImageFormat]::Png) } finally { $bmp.Dispose() }
  $env:C05_FIXTURE_PNG=Join-Path $temp 'black.png'; $env:C05_FIXTURE_SCREEN='black'
  $r=& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $runner -Action Capture -Serial fixture-01 -Apk $fixtureApk -EvidenceDir $blackDir 2>&1; $code=$LASTEXITCODE
  $blackSummary=Get-ChildItem $blackDir -Filter '*capture-summary.json' -Recurse | Select-Object -First 1
  if ($code -ne 0 -or -not $blackSummary -or (Get-Content $blackSummary.FullName -Raw) -notmatch 'BLACK_OR_NEAR_BLACK_REQUIRES_INVESTIGATION' -or (Get-Content $blackSummary.FullName -Raw) -match '"screenshotIsGamePass"\s*:\s*true') { throw 'black screenshot was not kept non-passing' }
  $r=& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $runner -Action Capture -Serial fixture-01 -Apk $fixtureApk -EvidenceDir $blackDir 2>&1; $code=$LASTEXITCODE
  if ($code -ne 0 -or @(Get-ChildItem $blackDir -Filter '*capture-summary.json' -Recurse).Count -ne 2) { throw 'repeat capture overwrote or rejected existing evidence parent' }
  $env:C05_FIXTURE_SCREEN='empty'
  $r=& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $runner -Action Capture -Serial fixture-01 -Apk $fixtureApk -EvidenceDir $emptyDir 2>&1; $code=$LASTEXITCODE
  if ($code -eq 0 -or ($r -join "`n") -notmatch 'empty or too small') { throw 'empty screenshot was accepted' }
  $r=& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $build -UnityPath (Join-Path $temp 'missing\Unity.exe') 2>&1; $code=$LASTEXITCODE
  if ($code -eq 0) { throw 'missing Unity executable was accepted' }
  $r=& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $build -OutputFixtureTest 2>&1; $code=$LASTEXITCODE
  if ($code -ne 0 -or ($r -join "`n") -notmatch 'OUTPUT_FIXTURE PASS existing-apk-output-rejected') { throw 'old APK output directory was reused' }
  $r=& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $build -ProcessFixtureTest 2>&1; $code=$LASTEXITCODE
  if ($code -ne 0 -or ($r -join "`n") -notmatch 'PROCESS_FIXTURE PASS stale-identity-preserved exact-owned-instance-stopped') { throw 'process timeout ownership fixture failed' }
  $r=& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $build -VerdictFixtureTest 2>&1; $code=$LASTEXITCODE
  if ($code -ne 0 -or ($r -join "`n") -notmatch 'VERDICT_FIXTURE PASS missing-marker failed-report stale-apk-rejected valid-fixture-accepted') { throw 'BuildReport/APK verdict fixture failed' }
  "TOOL_FIXTURE PASS scenarios=10 (unauthorized, no-device, multi-device, install-exit-propagation, black-capture-nonpass, empty-capture-reject, missing-unity, old-output-reject, pid-starttime-ownership, report-apk-verdicts)"
}
finally {
  Remove-Item Env:C05_ADB_PATH -ErrorAction SilentlyContinue; Remove-Item Env:C05_FIXTURE_STATE -ErrorAction SilentlyContinue; Remove-Item Env:C05_FIXTURE_PNG -ErrorAction SilentlyContinue; Remove-Item Env:C05_FIXTURE_SCREEN -ErrorAction SilentlyContinue
  $tempFull=[IO.Path]::GetFullPath($temp); $tempBase=[IO.Path]::GetFullPath([IO.Path]::GetTempPath())
  if (-not $tempFull.StartsWith($tempBase,[StringComparison]::OrdinalIgnoreCase)) { throw 'Refusing fixture cleanup outside temp root.' }
  Remove-Item -LiteralPath $tempFull -Recurse -Force
  $fixtureOutput=[IO.Path]::GetFullPath((Join-Path $root 'build\p45-android-game-device-tool-fixture')); $buildRoot=[IO.Path]::GetFullPath((Join-Path $root 'build'))+[IO.Path]::DirectorySeparatorChar
  if ($fixtureOutput.StartsWith($buildRoot,[StringComparison]::OrdinalIgnoreCase) -and (Test-Path $fixtureOutput)) { Remove-Item -LiteralPath $fixtureOutput -Recurse -Force }
}
