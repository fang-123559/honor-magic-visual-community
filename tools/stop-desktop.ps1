$ErrorActionPreference='Stop'
$projectRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
. (Join-Path $PSScriptRoot 'dev/assert-release-workspace.ps1')
Assert-MagicReleaseWorkspace -Root $projectRoot
$nativeSelection=Join-Path $projectRoot 'runtime/system/selection.json'
if(Test-Path -LiteralPath $nativeSelection){
    if((Get-Item -LiteralPath $nativeSelection).Length -gt 8192){throw 'System selection exceeds bound.'}
    $selectedSystem=Get-Content -LiteralPath $nativeSelection -Raw -Encoding UTF8|ConvertFrom-Json
    if($selectedSystem.enabled -eq $true -and $selectedSystem.protocol -eq 'Magic.System.Native/1'){
        & (Join-Path $PSScriptRoot 'native-system-lifecycle.ps1') -Action Stop
        return
    }
}

. (Join-Path $PSScriptRoot 'desktop-native.ps1')
$lifecycle=Enter-MagicLifecycle -Root $projectRoot -WaitMilliseconds 30000
if($null -eq $lifecycle){throw 'Desktop lifecycle operation is still in progress; concurrent shutdown refused.'}
try {
$package=Get-MagicSystemProvider $projectRoot -ForShutdown
$owned=Read-MagicSystemJson (Join-Path $projectRoot $(if($package.Independent){'runtime/system/install-state.json'}else{'runtime/desktop/install-state.json'}))
if($package.Independent){if($owned.instanceId -ne $package.InstanceId){throw 'Independent runtime identity changed; no processes stopped.'}}elseif($package -and -not $package.RecoveryOnly -and $package.PackageFullName -ne $owned.packageFullName){throw 'Package identity changed; no processes stopped.'}
$hostProcessName=$package.HostProcessName;$serviceProcessName=$package.ServiceProcessName
if($owned.nativeTaskbarState -notin @(0,1,2,3)){throw 'Unknown taskbar rollback state.'}
$sessionPath=Join-Path $projectRoot 'runtime/desktop/session.json'
$session=if(Test-Path -LiteralPath $sessionPath){Get-Content -LiteralPath $sessionPath -Raw -Encoding UTF8 | ConvertFrom-Json}else{$null}
. (Join-Path $PSScriptRoot 'desktop-transition.ps1')
$transition=Start-MagicTransition -Root $projectRoot -Mode exit
$transitionReady=$false
try {
if ($package) {
    $selectedGui=Get-MagicDesktopHost $package $projectRoot -ForShutdown
    if ($session -and $session.metadata -and (Test-Path -LiteralPath $session.metadata)) {
        $unload=Start-Process -FilePath ($package.CliExecutable) -ArgumentList ('resource unload widget "'+$session.metadata+'"') -WindowStyle Hidden -PassThru
        if (-not $unload.WaitForExit(10000)) { throw 'Widget unload timed out; no forced termination performed.' }
    }
    $servicePath=$package.ServiceExecutable
    if (@(Get-CimInstance Win32_Process -Filter "Name='$serviceProcessName'" | Where-Object {(Get-MagicLiveProcessPath $_.ProcessId) -eq $servicePath}).Count) {
        $stop=Start-Process -FilePath $servicePath -ArgumentList 'stop' -WindowStyle Hidden -PassThru
        if (-not $stop.WaitForExit(15000) -or $stop.ExitCode -ne 0) { throw 'Seelen service stop failed; GUI not forcibly stopped.' }
        for ($attempt=0;$attempt -lt 15;$attempt++) { if (-not @(Get-CimInstance Win32_Process -Filter "Name='$serviceProcessName'" | Where-Object {(Get-MagicLiveProcessPath $_.ProcessId) -eq $servicePath}).Count) {break};Start-Sleep -Seconds 1 }
        if (@(Get-CimInstance Win32_Process -Filter "Name='$serviceProcessName'" | Where-Object {(Get-MagicLiveProcessPath $_.ProcessId) -eq $servicePath}).Count) {throw 'Service remains active; GUI not stopped.'}
    }
    foreach ($row in @(Get-CimInstance Win32_Process -Filter "Name='$hostProcessName'" | Where-Object {$_.ExecutablePath -in @($selectedGui,$package.HostExecutable)})) {
        $process=Get-Process -Id $row.ProcessId
        if ($process.Path -ne $row.ExecutablePath) { throw 'GUI process identity changed.' }
        Stop-Process -Id $process.Id -Force
    }
}
$algorithm=[Security.Cryptography.SHA256]::Create()
try{$key=[BitConverter]::ToString($algorithm.ComputeHash([Text.Encoding]::UTF8.GetBytes($projectRoot.ToUpperInvariant()))).Replace('-','').Substring(0,24)}finally{$algorithm.Dispose()}
try {
    $pipe=New-Object IO.Pipes.NamedPipeClientStream('.',('MagicCommunity-'+$key),[IO.Pipes.PipeDirection]::Out)
    $pipe.Connect(2000);$writer=New-Object IO.StreamWriter($pipe);$writer.AutoFlush=$true;$writer.WriteLine('quit');$writer.Dispose();$pipe.Dispose()
} catch { if ($session -and (Get-Process -Id $session.pid -ErrorAction SilentlyContinue)) { throw 'Local adapter could not be stopped gracefully.' } }
Add-Type -TypeDefinition @'
using System;using System.Runtime.InteropServices;
public static class MagicDesktopRestore {
 [StructLayout(LayoutKind.Sequential)]public struct R{public int L,T,Ri,B;}
 [StructLayout(LayoutKind.Sequential)]public struct A{public uint Size;public IntPtr Hwnd;public uint Callback,Edge;public R Rect;public IntPtr Param;}
 [DllImport("shell32.dll")]public static extern UIntPtr SHAppBarMessage(uint msg,ref A data);
 public static void Restore(long state){var d=new A{Size=(uint)Marshal.SizeOf<A>(),Param=new IntPtr(state)};SHAppBarMessage(10,ref d);}
 public static ulong State(){var d=new A{Size=(uint)Marshal.SizeOf<A>()};return SHAppBarMessage(4,ref d).ToUInt64();}
}
'@
Start-Sleep -Seconds 1
[MagicDesktopRestore]::Restore([long]$owned.nativeTaskbarState)
Start-Sleep -Seconds 1
$remaining=@(Get-CimInstance Win32_Process -Filter "Name='$hostProcessName' OR Name='$serviceProcessName'" | Where-Object {$image=(Get-MagicLiveProcessPath $_.ProcessId);$image -and ($image -eq $selectedGui -or $image -eq $package.ServiceExecutable)})
$brokerRemaining=if($session){Get-CimInstance Win32_Process -Filter ("ProcessId="+$session.pid)}else{$null}
if ($remaining.Count -ne 0 -or ($brokerRemaining -and $brokerRemaining.Name -eq 'MagicCommunity.exe') -or [MagicDesktopRestore]::State() -ne $owned.nativeTaskbarState) {throw 'Desktop exit postconditions failed; completion not claimed.'}
$receipt=[ordered]@{at=(Get-Date).ToString('o');returnedToWindows=$true;seelenRegistrationRetained=$true;layoutRetained=$true;nativeTaskbarStateRestored=$owned.nativeTaskbarState;note='Exit current session; package and login setting retained; no user files or settings deleted'}
$receipt | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $projectRoot 'runtime/desktop/exit-receipt.json') -Encoding UTF8
$receipt | ConvertTo-Json
$transitionReady=$true
} finally { Complete-MagicTransition $transition $transitionReady }

} finally {Exit-MagicLifecycle $lifecycle}
