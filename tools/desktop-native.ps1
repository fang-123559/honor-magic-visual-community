. (Join-Path $PSScriptRoot 'file-hash.ps1')
. (Join-Path $PSScriptRoot 'system-runtime.ps1')
if (-not ('MagicDesktopProcessIdentity' -as [type])) {
Add-Type -TypeDefinition @'
using System;using System.Text;using System.Runtime.InteropServices;
public static class MagicDesktopProcessIdentity {
 [DllImport("kernel32.dll",SetLastError=true)]static extern IntPtr OpenProcess(uint access,bool inherit,uint pid);
 [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]static extern bool QueryFullProcessImageName(IntPtr process,uint flags,StringBuilder name,ref uint size);
 [DllImport("kernel32.dll")]static extern bool CloseHandle(IntPtr handle);
 public static string Path(uint pid){var p=OpenProcess(0x1000,false,pid);if(p==IntPtr.Zero)throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());try{var b=new StringBuilder(32768);uint n=32768;if(!QueryFullProcessImageName(p,0,b,ref n))throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());return b.ToString();}finally{CloseHandle(p);}}
}
'@
}
function Get-MagicLiveProcessPath([int]$ProcessId) {
    try { return [MagicDesktopProcessIdentity]::Path($ProcessId) }
    catch {
        # Enumeration is only a snapshot: a service can exit before path lookup.
        # An unreadable process that is still alive remains an error.
        if(-not (Get-Process -Id $ProcessId -ErrorAction SilentlyContinue)){return $null}
        throw
    }
}
function Assert-MagicHostStatic([string]$Folder) {
    $base=[IO.Path]::GetFullPath($Folder).TrimEnd('\','/')+'\'
    $expected=[Collections.Generic.Dictionary[string,string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach($line in [IO.File]::ReadLines((Join-Path $Folder 'SHA256SUMS'))){
        if($line -notmatch '^([0-9a-fA-F]{64})  (static/.+)$'){throw 'Invalid signed static manifest entry.'}
        $hash=$Matches[1];$relative=$Matches[2];$path=[IO.Path]::GetFullPath((Join-Path $base $relative))
        if(-not $path.StartsWith($base+'static\',[StringComparison]::OrdinalIgnoreCase) -or $relative.Split('/') -contains '..' -or $expected.ContainsKey($path)){throw 'Invalid signed static path.'}
        $expected.Add($path,$hash)
    }
    if($expected.Count -eq 0){throw 'Signed static manifest is empty.'}
    $actual=@(Get-ChildItem -LiteralPath (Join-Path $base 'static') -Recurse -Force)
    if(@($actual|Where-Object {$_.Attributes -band [IO.FileAttributes]::ReparsePoint}).Count){throw 'Static resources contain reparse points.'}
    $files=@($actual|Where-Object {-not $_.PSIsContainer})
    $problems=[Collections.Generic.List[string]]::new()
    foreach($file in $files){if(-not $expected.ContainsKey($file.FullName)){$problems.Add('extra: '+$file.FullName.Substring($base.Length))}}
    foreach($entry in $expected.GetEnumerator()){if(-not(Test-Path -LiteralPath $entry.Key -PathType Leaf)){$problems.Add('missing: '+$entry.Key.Substring($base.Length))}elseif((Get-MagicSHA256 $entry.Key) -ne $entry.Value){$problems.Add('mismatch: '+$entry.Key.Substring($base.Length))}}
    if($problems.Count){throw ('Native host static integrity failed before launch: '+($problems -join '; '))}
}
function Get-MagicDesktopHost($Package,[string]$ProjectRoot,[switch]$ForShutdown) {
    if($Package.Independent -eq $true){return $Package.HostExecutable}
    $original=Join-Path $Package.InstallLocation 'seelen-ui.exe'
    $pointer=Join-Path $ProjectRoot 'runtime/desktop/host.current.json'
    if(-not(Test-Path -LiteralPath $pointer)){return $original}
    $selection=Get-Content -LiteralPath $pointer -Raw -Encoding UTF8|ConvertFrom-Json
    if($selection.enabled -ne $true){return $original}
    if($selection.schemaVersion -ne 1 -or $selection.owner -ne $ProjectRoot -or (-not $ForShutdown -and $selection.package -ne $Package.PackageFullName)){throw 'Community host ownership or baseline changed.'}
    $path=[IO.Path]::GetFullPath($selection.executable)
    $prefix=[IO.Path]::GetFullPath((Join-Path $ProjectRoot 'profiles/native-host'))+'\'
    if(-not $path.StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($path) -ne 'seelen-ui.exe'){throw 'Community host path escapes versioned profile.'}
    $folder=Split-Path $path
    if((Get-Item -LiteralPath $folder).Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Community host profile is a reparse point.'}
    if((Get-MagicSHA256 $path) -ne $selection.sha256){throw 'Community host binary changed.'}
    if($selection.startupResources -eq $true){
        if($selection.bootstrapSha256 -notmatch '^[0-9a-f]{64}$' -or (Get-MagicSHA256 (Join-Path $folder 'community-boot.json')) -ne $selection.bootstrapSha256){throw 'Community startup resource manifest changed.'}
    }
    # The native host performs the complete signed static resource verification.
    if($ForShutdown){return $path} # Local identity/hash above suffice to stop; do not compare against a replacement Store package.
    foreach($name in @('SHA256SUMS','SHA256SUMS.sig','sluhk.dll')){
        if((Get-MagicSHA256 (Join-Path $folder $name)) -ne (Get-MagicSHA256 (Join-Path $Package.InstallLocation $name))){throw ('Original signed resource boundary changed: '+$name)}
    }
    return $path
}
function Stop-MagicGuiForHandoff([int]$ProcessId,[string]$ExpectedPath) {
    # Capture the real process handle before termination; bounded waiting avoids
    # declaring failure while Windows is still releasing a WebView GUI.
    $process=Get-Process -Id $ProcessId -ErrorAction SilentlyContinue
    if(-not $process){return}
    try {
        if([MagicDesktopProcessIdentity]::Path($ProcessId) -ne $ExpectedPath){throw 'GUI identity changed before handoff.'}
        $started=$process.StartTime.ToFileTimeUtc();[void]$process.Handle
        Stop-Process -InputObject $process -Force
        if(-not $process.WaitForExit(15000)){throw ('GUI termination is still pending: pid='+$ProcessId+' created='+$started)}
        if(-not $process.HasExited){throw 'GUI handoff did not confirm process exit.'}
    } finally {$process.Dispose()}
}
if (-not ('MagicNativeTaskbarSafety' -as [type])) {
Add-Type -TypeDefinition @'
using System;using System.Runtime.InteropServices;
public static class MagicNativeTaskbarSafety {
 [DllImport("user32.dll",CharSet=CharSet.Unicode,EntryPoint="FindWindowW")]static extern IntPtr FindWindow(string cls,string name);
 [DllImport("user32.dll")]static extern bool IsWindowVisible(IntPtr hwnd);
 public static bool PrimaryVisible(){var hwnd=FindWindow("Shell_TrayWnd",null);return hwnd!=IntPtr.Zero&&IsWindowVisible(hwnd);}
}
'@
}

# A single bounded lifecycle transaction covers the animation, host, adapter and
# teardown. The application mutex remains responsible for its own instance.
function Enter-MagicLifecycle([string]$Root,[int]$WaitMilliseconds=0) {
    $hash=[Security.Cryptography.SHA256]::Create()
    try{$key=([BitConverter]::ToString($hash.ComputeHash([Text.Encoding]::UTF8.GetBytes([IO.Path]::GetFullPath($Root).ToUpperInvariant())))).Replace('-','').Substring(0,24)}finally{$hash.Dispose()}
    $lease=[Threading.Mutex]::new($false,('Local\MagicDesktopLifecycle-'+$key))
    try{$owns=$false;try{$owns=$lease.WaitOne($WaitMilliseconds)}catch [Threading.AbandonedMutexException]{$owns=$true}
        if($owns){return $lease};$lease.Dispose();return $null
    }catch{$lease.Dispose();throw}
}
function Exit-MagicLifecycle($Lease) {if($null -ne $Lease){try{$Lease.ReleaseMutex()}finally{$Lease.Dispose()}}}
