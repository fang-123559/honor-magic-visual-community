$ErrorActionPreference='Stop'
$projectRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$statePath=Join-Path $projectRoot 'runtime/desktop/install-state.json'
if (Test-Path -LiteralPath $statePath) { throw 'An integration baseline already exists; inspect it instead of overwriting.' }
$stamp=Get-Date -Format 'yyyyMMdd-HHmmss'
$evidence=Join-Path $projectRoot "evidence/desktop/$stamp"
New-Item -ItemType Directory -Path (Join-Path $evidence 'before') -Force | Out-Null
& (Join-Path $PSScriptRoot 'p0/snapshot_seelen_state.ps1') -OutputDirectory (Join-Path $evidence 'before') | Out-Null
$before=Get-Content -LiteralPath (Join-Path $evidence 'before/system-state.json') -Raw -Encoding UTF8 | ConvertFrom-Json
if ($before.issues.Count -ne 0 -or $before.installed_packages.Count -ne 0 -or $before.tasks.Count -ne 0 -or $before.processes.Count -ne 0) { throw 'Unexpected pre-existing Seelen state; installation not changed.' }
Add-Type -TypeDefinition @'
using System;using System.Runtime.InteropServices;
public static class MagicDesktopBaseline {
 [StructLayout(LayoutKind.Sequential)] public struct R{public int Left,Top,Right,Bottom;}
 [StructLayout(LayoutKind.Sequential)] public struct A{public uint Size;public IntPtr Hwnd;public uint Callback,Edge;public R Rect;public IntPtr Param;}
 [DllImport("shell32.dll")]public static extern UIntPtr SHAppBarMessage(uint msg,ref A data);
 public static ulong State(){var d=new A{Size=(uint)Marshal.SizeOf<A>()};return SHAppBarMessage(4,ref d).ToUInt64();}
}
'@
$asset=Join-Path $projectRoot 'references/seelen-v2.8.9/downloads/Seelen.UI_2.8.9.0_x64.Msix'
if ((Get-FileHash -LiteralPath $asset -Algorithm SHA256).Hash -ne 'EA1B513E1C95AF06A5765EF662FC664A9EA97C5E449A7D5E846ED83798747F4B') { throw 'MSIX hash mismatch' }
if ((Get-AuthenticodeSignature -LiteralPath $asset).Status -ne 'Valid') { throw 'MSIX signature is not valid' }
$receipt=[ordered]@{startedAt=(Get-Date).ToString('o');evidence=$evidence;before=(Join-Path $evidence 'before/system-state.json');nativeTaskbarState=[MagicDesktopBaseline]::State();packagePreexisted=$false;purpose='User-authorized persistent desktop experience; not replay of P0 experiment';installed=$false}
New-Item -ItemType Directory -Path (Split-Path $statePath) -Force | Out-Null
$receipt | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $statePath -Encoding UTF8
Add-AppxPackage -Path $asset -ErrorAction Stop
$package=Get-AppxPackage -Name 'Seelen.SeelenUI'
if ($package.Version -ne '2.8.9.0' -or $package.Status -ne 'Ok') { throw 'Pinned MSIX registration not healthy' }
$receipt.installed=$true;$receipt['packageFullName']=$package.PackageFullName;$receipt['installLocation']=$package.InstallLocation
$receipt | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $statePath -Encoding UTF8
$receipt | ConvertTo-Json -Depth 5
