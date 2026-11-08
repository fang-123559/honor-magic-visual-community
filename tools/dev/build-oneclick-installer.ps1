param([Parameter(Mandatory)][string]$BaseDirectory,[Parameter(Mandatory)][string]$UpdateDirectory,[Parameter(Mandatory)][string]$OutputDirectory,[switch]$Timestamp)
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
. (Join-Path $PSScriptRoot 'sign-local-artifact.ps1')
$base=[IO.Path]::GetFullPath($BaseDirectory);$update=[IO.Path]::GetFullPath($UpdateDirectory);$out=[IO.Path]::GetFullPath($OutputDirectory)
foreach($directory in @($base,$update,$out)){
    if(-not $directory.StartsWith((Join-Path $root 'artifacts')+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Use directories under artifacts in this checkout.'}
}
if(Test-Path -LiteralPath $out){throw 'Use a new output directory.'}
[void][IO.Directory]::CreateDirectory($out)
$guide=Join-Path $root 'tools/distribution'
$bundles=@();$receipts=@()
foreach($directory in @($base,$update)){
    $bundle=Get-Content -LiteralPath (Join-Path $directory 'setup-bundle.json') -Raw -Encoding UTF8|ConvertFrom-Json
    $receipt=Get-Content -LiteralPath (Join-Path $directory 'package-build.json') -Raw -Encoding UTF8|ConvertFrom-Json
    if(-not $receipt.distribution -or -not $bundle.signed -or $bundle.packageName -cne 'MagicCommunity.Native' -or $bundle.protocol -cne 'Magic.Install/1' -or $bundle.dataRootMode -cne 'per-user' -or $bundle.packageFile -cnotmatch '^MagicCommunity\.Native_[0-9.]+_x64\.msix$'){throw 'Expected a signed independent distribution.'}
    if($receipt.sha256 -cne $bundle.sha256 -or $bundle.sha256 -cne (Get-FileHash -LiteralPath (Join-Path $directory $bundle.packageFile)).Hash){throw 'Package digest differs.'}
    foreach($name in @('Setup.ps1','Setup.Core.ps1','Setup.Shortcut.ps1','SetupShortcut.cs')){
        if((Get-FileHash -LiteralPath (Join-Path $directory $name)).Hash -cne (Get-FileHash -LiteralPath (Join-Path $guide $name)).Hash){throw ('Installer source differs: '+$name)}
    }
    $bundles+=$bundle;$receipts+=$receipt
}
$first=[version]$bundles[0].version;$next=[version]$bundles[1].version
if($first.ToString(3) -cne $next.ToString(3) -or $next.Revision -ne ($first.Revision+1) -or $receipts[0].certificateThumbprint -cne $receipts[1].certificateThumbprint -or $receipts[0].sourceCommit -cne $receipts[1].sourceCommit){throw 'Packages must use the same release and signer.'}
if($bundles[0].packageFamily -cne $bundles[1].packageFamily -or $receipts[0].publisher -cne $receipts[1].publisher -or $receipts[0].signingMode -cne $receipts[1].signingMode){throw 'Base and upgrade must use the same publisher identity and signing channel.'}
if($receipts[0].signingMode -eq 'public-ca'){
    if(-not($Timestamp -and $receipts[0].timestamped -and $receipts[1].timestamped)){throw 'The public installer and both MSIX packages require timestamps.'}
    . (Join-Path $PSScriptRoot '../system-runtime/native-package-family.ps1')
    $family=Get-MagicNativePackageFamily $receipts[0].publisher
    if($family -cne $bundles[0].packageFamily -or $family -cne $receipts[0].packageFamily -or $family -cne $receipts[1].packageFamily){throw 'Public installer package family differs from the signer.'}
    [void](Get-MagicPublicSigningCertificate $receipts[0].certificateThumbprint $receipts[0].publisher)
}
$version=$first.ToString(3)
$compiler=Join-Path $env:SystemRoot 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
$sdk=Join-Path ([Environment]::GetFolderPath('ProgramFilesX86')) 'Windows Kits/10/bin'
$sdkBin=Get-ChildItem -LiteralPath $sdk -Directory | Where-Object Name -Match '^10\.' | Sort-Object {[version]$_.Name} -Descending | Select-Object -First 1
$signTool=Join-Path $sdkBin.FullName 'x64/signtool.exe'
$directories=@($base,$update)
for($index=0;$index -lt $receipts.Count;$index++){
    $receipt=$receipts[$index]
    $actualPackage=Join-Path $directories[$index] $bundles[$index].packageFile
    $packageSignature=Get-AuthenticodeSignature -LiteralPath $actualPackage
    if($packageSignature.Status -ne 'Valid' -or $packageSignature.SignerCertificate.Thumbprint -ine $receipt.certificateThumbprint -or $packageSignature.SignerCertificate.Subject -cne $receipt.publisher -or ($Timestamp -and -not $packageSignature.TimeStamperCertificate)){throw ('MSIX signer or timestamp differs: '+$receipt.version)}
    $verifyArgs=@('verify','/pa');if($Timestamp){$verifyArgs+='/tw'}
    & $signTool @verifyArgs $actualPackage *> (Join-Path $out ($receipt.version+'.verify.log'))
    if($LASTEXITCODE -ne 0){throw ('MSIX signature check failed: '+$receipt.version)}
}
$metadata=@(
 'using System.Reflection;',
 ('[assembly: AssemblyVersion("'+$first+'")]'),
 ('[assembly: AssemblyFileVersion("'+$first+'")]'),
 '[assembly: AssemblyProduct("Magic 视界社区版")]',
 '[assembly: AssemblyTitle("Magic 视界社区版安装程序")]',
 'internal static class SetupPayload {',
 ('    internal const string BaseFile = "'+$bundles[0].packageFile+'";'),
 ('    internal const string UpdateFile = "'+$bundles[1].packageFile+'";'),
 '}'
) -join [Environment]::NewLine
$metadataPath=Join-Path $out 'SetupPayload.cs'
[IO.File]::WriteAllText($metadataPath,$metadata,[Text.UTF8Encoding]::new($true))
# Preserve the original icon's small and large frames in the installer too.
$packageIcon=Join-Path $base 'payload/Assets/MagicCommunity.ico'
if((Get-FileHash -LiteralPath $packageIcon).Hash -ne (Get-FileHash -LiteralPath (Join-Path $root 'assets/community/application.ico')).Hash){throw 'Package application icon does not match the release source.'}
$icon=Join-Path $out 'setup.ico'
Copy-Item -LiteralPath $packageIcon -Destination $icon
$exe=Join-Path $out ('MagicCommunity-Setup-'+$version+'-x64.exe')
$arguments=@('/nologo','/target:winexe','/platform:x64','/optimize+','/reference:System.Windows.Forms.dll',('/out:'+$exe),('/win32manifest:'+(Join-Path $guide 'SetupLauncher.manifest')),('/win32icon:'+$icon))
foreach($pair in @(@('Setup.ps1','Wizard'),@('Setup.Core.ps1','Core'),@('Setup.Shortcut.ps1','Shortcuts'),@('SetupShortcut.cs','ShellLink'),@('setup-bundle.json','BaseManifest'),@($bundles[0].packageFile,'BasePackage'))){
    $arguments+='/resource:'+(Join-Path $base $pair[0])+','+$pair[1]
}
$arguments+='/resource:'+(Join-Path $update 'setup-bundle.json')+',UpdateManifest'
$arguments+='/resource:'+(Join-Path $update $bundles[1].packageFile)+',UpdatePackage'
& $compiler @arguments (Join-Path $guide 'SetupLauncher.cs') $metadataPath *> (Join-Path $out 'compile.log')
if($LASTEXITCODE -ne 0){Get-Content (Join-Path $out 'compile.log') -Tail 20 | Out-Host;throw 'One-click installer build failed.'}
Invoke-MagicLocalSign $signTool $receipts[0].certificateThumbprint $exe (Join-Path $out 'sign.log') -Timestamp:$Timestamp
$signature=Get-AuthenticodeSignature -LiteralPath $exe
if($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Thumbprint -cne $receipts[0].certificateThumbprint){throw 'Installer signature verification failed.'}
$verify=Start-Process -FilePath $exe -ArgumentList '--verify-only' -WindowStyle Hidden -PassThru -RedirectStandardError (Join-Path $out 'launcher-verification.log')
if(-not $verify.WaitForExit(120000) -or $verify.ExitCode -ne 0){Get-Content (Join-Path $out 'launcher-verification.log') -Tail 15 | Out-Host;throw 'Packaged launcher could not verify its two distributions.'}
$report=[ordered]@{version=$version;sourceCommit=$receipts[0].sourceCommit;launcherSourceCommit=(& git -C $root rev-parse HEAD).Trim();entry=$exe;sha256=(Get-FileHash -LiteralPath $exe).Hash;bytes=(Get-Item -LiteralPath $exe).Length;certificate=$signature.SignerCertificate.Thumbprint;signingMode=$receipts[0].signingMode;packageFamily=$bundles[0].packageFamily;timestamped=[bool]$Timestamp;timeStamper=$(if($signature.TimeStamperCertificate){$signature.TimeStamperCertificate.Subject});packages=@($bundles|Select-Object version,sha256);launcherVerificationExit=$verify.ExitCode;installed=$false}
$report|ConvertTo-Json -Depth 6|Set-Content -LiteralPath (Join-Path $out 'installer-build.json') -Encoding UTF8
$report|ConvertTo-Json -Depth 6
