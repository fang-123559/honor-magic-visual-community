param()
$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.Drawing
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class MenuIconExport {
 [DllImport("shell32.dll",CharSet=CharSet.Unicode)] public static extern int SHDefExtractIcon(string file,int index,uint flags,out IntPtr large,out IntPtr small,uint size);
 [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr icon);
}
'@
$out=Join-Path $PSScriptRoot '../../assets/windows-shell-menu'
New-Item -ItemType Directory -Force -Path $out | Out-Null
$names=@{cut='Windows.cut';copy='Windows.copy';paste='Windows.paste';rename='Windows.rename';delete='Windows.delete';properties='Windows.properties';copyaspath='Windows.copyaspath';pintohome='Windows.PinToHome';openas='Windows.OpenWith';extract='Windows.CompressedFile.extract';compress='Windows.zip'}
$manifest=@()
foreach($pair in $names.GetEnumerator()){
 $v=Get-ItemProperty -LiteralPath ('HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\CommandStore\shell\'+$pair.Value)
 $resource=[Environment]::ExpandEnvironmentVariables([string]$v.Icon)
 if(!$resource){if($pair.Key -eq 'compress'){$resource='imageres.dll,-174'}else{throw ('Missing icon '+$pair.Value)}}
 $at=$resource.LastIndexOf(',');$index=[int]$resource.Substring($at+1);$file=$resource.Substring(0,$at).Trim('"');if(![IO.Path]::IsPathFullyQualified($file)){$file=Join-Path ([Environment]::SystemDirectory) $file}
 $large=[IntPtr]::Zero;$small=[IntPtr]::Zero
 try{
  $hr=[MenuIconExport]::SHDefExtractIcon($file,$index,0,[ref]$large,[ref]$small,64);if($hr -lt 0 -or $large -eq [IntPtr]::Zero){throw ('Icon extraction failed '+$pair.Key)}
  $icon=[Drawing.Icon]::FromHandle($large);$bitmap=$icon.ToBitmap();try{$bitmap.Save((Join-Path $out ($pair.Key+'.png')),[Drawing.Imaging.ImageFormat]::Png)}finally{$bitmap.Dispose();$icon.Dispose()}
  $manifest+=@{key=$pair.Key;command=$pair.Value;module=[IO.Path]::GetFileName($file);resourceIndex=$index;size=64}
 }finally{if($large -ne [IntPtr]::Zero){[void][MenuIconExport]::DestroyIcon($large)};if($small -ne [IntPtr]::Zero -and $small -ne $large){[void][MenuIconExport]::DestroyIcon($small)}}
}
$manifest|ConvertTo-Json|Set-Content (Join-Path $out 'manifest.json') -Encoding utf8
Write-Output ('Exported '+$manifest.Count+' embedded Windows icon PNGs.')
