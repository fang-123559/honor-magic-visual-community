param(
 [Parameter(Mandatory)][string]$PayloadDirectory,
 [Parameter(Mandatory)][string]$NativeBuildDirectory,
 [Parameter(Mandatory)][ValidatePattern('^\d+\.\d+\.\d+\.\d+$')][string]$PackageVersion,
 [string]$ProjectRoot,
 [string]$OutputDirectory,[string]$ProbeReport,[switch]$Sign,[switch]$Distribution,[switch]$Timestamp,[switch]$Unpackaged,
 [string]$PackagePublisher='CN=MagicCommunity.Native.Local',[ValidatePattern('^[0-9A-Fa-f]{40}$')][string]$PublicSigningThumbprint
)
$ErrorActionPreference='Stop'
if($Unpackaged -and (-not $Distribution -or $Sign -or $Timestamp -or $PublicSigningThumbprint)){throw 'Unpackaged builds require Distribution and never invoke a signer.'}
. (Join-Path $PSScriptRoot 'sign-local-artifact.ps1')
if($Timestamp -and -not $Sign){throw 'Timestamp requires signing.'}
. (Join-Path $PSScriptRoot '../system-runtime/native-package-family.ps1')
$packageFamily=Get-MagicNativePackageFamily $PackagePublisher
$customPublisher=$PackagePublisher -cne 'CN=MagicCommunity.Native.Local'
if($customPublisher -and -not $Distribution){throw 'A custom publisher is supported only for an independent distribution.'}
if($customPublisher -and $Sign -and -not $PublicSigningThumbprint){throw 'Select the public signing credential explicitly. The local test signer cannot sign this identity.'}
$certificate=$null
if($PublicSigningThumbprint){
 if(-not($Sign -and $Distribution -and $Timestamp)){throw 'Public signing requires -Distribution -Sign -Timestamp.'}
 $certificate=Get-MagicPublicSigningCertificate $PublicSigningThumbprint $PackagePublisher
}
$signingMode=if($PublicSigningThumbprint){'public-ca'}elseif($Sign){'local-test'}else{'unsigned'}
$source=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
if(-not $OutputDirectory){$OutputDirectory=Join-Path $source ('artifacts/native-package/'+(Get-Date -Format yyyyMMdd-HHmmss))}
$out=[IO.Path]::GetFullPath($OutputDirectory);$app=[IO.Path]::GetFullPath($PayloadDirectory);$native=[IO.Path]::GetFullPath($NativeBuildDirectory);if(-not $Distribution){$ProjectRoot=[IO.Path]::GetFullPath($ProjectRoot).TrimEnd('\')}
if(-not $out.StartsWith((Join-Path $source artifacts)+'\',[StringComparison]::OrdinalIgnoreCase)-or(Test-Path -LiteralPath $out)){throw 'Use a new artifacts directory in this source tree.'}
foreach($path in @($app,$native)){if(-not $path.StartsWith($source+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Payload must be built in this source tree.'}}
if(-not $Distribution -and (Get-Content -LiteralPath (Join-Path $ProjectRoot '.git/HEAD') -Raw).Trim() -ne 'ref: refs/heads/main'){throw 'The runtime data owner must be the main checkout.'}
$nativeReceiptPath=Join-Path $native 'build.json'
if($customPublisher -or (Test-Path -LiteralPath $nativeReceiptPath)){
 $nativeReceipt=Get-Content -LiteralPath $nativeReceiptPath -Raw -Encoding UTF8 | ConvertFrom-Json
 $support=if($nativeReceipt.support){$nativeReceipt.support}else{$nativeReceipt}
 $helperEntry=@($support.files | Where-Object name -ceq 'MagicWindowAction.exe')
 if(($customPublisher -or $support.packageFamily) -and ($support.packagePublisher -cne $PackagePublisher -or $support.packageFamily -cne $packageFamily -or $helperEntry.Count -ne 1 -or $helperEntry[0].sha256 -cne (Get-FileHash -LiteralPath (Join-Path $native 'MagicWindowAction.exe')).Hash)){
  throw 'Rebuild the native helpers with this exact -PackagePublisher before packaging.'
 }
}
$stage=Join-Path $out payload;[void][IO.Directory]::CreateDirectory($stage)
Get-ChildItem -LiteralPath $app -File -Recurse | Where-Object {$_.Extension -notin @('.pdb','.obj','.lib','.exp') -and (-not $Distribution -or $_.Name -notin @('native-system.json','native-backend-build.json'))} | ForEach-Object {$relative=$_.FullName.Substring($app.Length+1);$target=Join-Path $stage $relative;[void][IO.Directory]::CreateDirectory((Split-Path $target));Copy-Item -LiteralPath $_.FullName -Destination $target}
foreach($name in @('MagicSystemNative.exe','MagicTrayCapture.dll','MagicTaskbarGuard.exe','MagicNotificationPermission.exe','MagicNativeEntry.exe','MagicWindowAction.exe')){Copy-Item -LiteralPath (Join-Path $native $name) -Destination (Join-Path $stage $name)}
foreach($name in @('MagicCommunity.exe','MagicCommunity.dll','MagicDesktopComposition.exe','MagicDesktopGuard.exe','composition-renderer.json')){if(-not(Test-Path -LiteralPath (Join-Path $stage $name))){throw ('Incomplete desktop payload: '+$name)}}
# The Composition renderer uses /MD. A fresh PC must not depend on the developer
# machine already having the VC runtime installed. Distribution packages carry
# Microsoft's existing signed redistributable DLLs locally; no machine-wide install.
if($Distribution){
 $vswhere=Join-Path ([Environment]::GetFolderPath('ProgramFilesX86')) 'Microsoft Visual Studio/Installer/vswhere.exe'
 $vs=(& $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath).Trim()
 $redistVersion=(Get-ChildItem -LiteralPath (Join-Path $vs 'VC/Redist/MSVC') -Directory|Where-Object Name -Match '^\d+\.\d+\.\d+$'|Sort-Object {[version]$_.Name} -Descending|Select-Object -First 1).FullName
 if(-not $redistVersion){throw 'Visual C++ x64 redistributable directory is missing.'}
 $crt=(Get-ChildItem -LiteralPath (Join-Path $redistVersion 'x64') -Directory|Where-Object Name -Like 'Microsoft.VC*.CRT'|Select-Object -First 1).FullName
 foreach($name in @('msvcp140.dll','vcruntime140.dll','vcruntime140_1.dll')){
  $file=Join-Path $crt $name
  $signature=Get-AuthenticodeSignature -LiteralPath $file
  if($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'O=Microsoft Corporation'){throw ('Redistributable signature is not verified: '+$name)}
  Copy-Item -LiteralPath $file -Destination (Join-Path $stage $name)
 }
 $notices=Join-Path $stage 'third-party-licenses';[void][IO.Directory]::CreateDirectory($notices)
 $notice=@('Microsoft Visual C++ Runtime','Copyright Microsoft Corporation. Redistributable files from Visual Studio 2022 x64 CRT.',('Version: '+[IO.Path]::GetFileName($redistVersion)),'Source and terms: https://aka.ms/vs/17/redist.txt','Deployment reference: https://learn.microsoft.com/en-us/cpp/windows/deployment-in-visual-cpp','These files are not relicensed by Magic Community. App-local runtime updates require a new application package.')
 [IO.File]::WriteAllText((Join-Path $notices 'Microsoft-Visual-C-Runtime.txt'),($notice -join [Environment]::NewLine),[Text.UTF8Encoding]::new($false))
}
$runtime=Join-Path $source tools/dotnet
foreach($relative in @('dotnet.exe','host/fxr/10.0.13','shared/Microsoft.NETCore.App/10.0.13','shared/Microsoft.WindowsDesktop.App/10.0.13')){
 $target=Join-Path $stage ('dotnet/'+$relative);[void][IO.Directory]::CreateDirectory((Split-Path $target));Copy-Item -LiteralPath (Join-Path $runtime $relative) -Destination $target -Recurse
}
$launch=if($Distribution){@{schemaVersion=2;dataRootMode='per-user'}}else{@{schemaVersion=1;root=$ProjectRoot;probeReport=$ProbeReport}}
[IO.File]::WriteAllText((Join-Path $stage native-launch.json),($launch|ConvertTo-Json),[Text.UTF8Encoding]::new($false))
foreach($name in @('LICENSE.txt','ThirdPartyNotices.txt')){if(Test-Path -LiteralPath (Join-Path $runtime $name)){Copy-Item -LiteralPath (Join-Path $runtime $name) -Destination (Join-Path $stage ('dotnet/'+$name))}}
$assets=Join-Path $stage Assets
. (Join-Path $PSScriptRoot 'application-icon.ps1')
Write-MagicApplicationAssets -OutputDirectory $assets
. (Join-Path $PSScriptRoot '../system-runtime/native-package-manifest.ps1')
if(-not $Unpackaged){
$manifest=Get-MagicNativePackageManifest -PackageVersion $PackageVersion -Distribution:$Distribution -PackagePublisher $PackagePublisher
[IO.File]::WriteAllText((Join-Path $stage AppxManifest.xml),$manifest,[Text.UTF8Encoding]::new($false))
}
$sdk=Join-Path ${env:ProgramFiles(x86)} 'Windows Kits/10';$sdkVersion=(Get-ChildItem (Join-Path $sdk bin) -Directory|Where-Object {$_.Name -match '^10\.'}|Sort-Object Name -Descending|Select-Object -First 1).Name
$package=Join-Path $out $(if($Unpackaged){'MagicCommunity.Desktop_'+$PackageVersion+'_x64.zip'}else{'MagicCommunity.Native_'+$PackageVersion+'_x64.msix'})
# Sign the standalone requireAdministrator EXE as well as the containing MSIX.
# Only the staging copy changes; source build/version receipts remain reproducible.
if($Sign -and -not $PublicSigningThumbprint){
 $certificate=Get-ChildItem Cert:/CurrentUser/My | Where-Object {$_.Subject -eq 'CN=MagicCommunity.Native.Local' -and $_.FriendlyName -eq 'Magic Community native validation (local)' -and $_.HasPrivateKey -and $_.NotAfter -gt (Get-Date).AddHours(2)} | Sort-Object NotAfter -Descending | Select-Object -First 1
 if(-not $certificate){$certificate=New-SelfSignedCertificate -Type CodeSigningCert -Subject 'CN=MagicCommunity.Native.Local' -FriendlyName 'Magic Community native validation (local)' -CertStoreLocation Cert:/CurrentUser/My -KeyAlgorithm RSA -KeyLength 2048 -HashAlgorithm SHA256 -KeyExportPolicy NonExportable -NotAfter (Get-Date).AddDays(1)}
}
if($Sign){
 Invoke-MagicLocalSign (Join-Path $sdk "bin/$sdkVersion/x64/signtool.exe") $certificate.Thumbprint (Join-Path $stage 'MagicWindowAction.exe') (Join-Path $out 'helper-sign.log') -Timestamp:$Timestamp
}
if($Distribution){
 $installedFiles=@{};Get-ChildItem -LiteralPath $stage -File|Where-Object Extension -in @('.exe','.dll')|ForEach-Object {$installedFiles[$_.Name]=(Get-FileHash -LiteralPath $_.FullName).Hash}
 [IO.File]::WriteAllText((Join-Path $stage 'installed-desktop.json'),(@{protocol='Magic.Install/1';files=$installedFiles}|ConvertTo-Json -Depth 5),[Text.UTF8Encoding]::new($false))
}
if($Unpackaged){
 Add-Type -AssemblyName System.IO.Compression.FileSystem
 [IO.Compression.ZipFile]::CreateFromDirectory($stage,$package,[IO.Compression.CompressionLevel]::Optimal,$false)
}else{
 & (Join-Path $sdk "bin/$sdkVersion/x64/makeappx.exe") pack /d $stage /p $package /o *> (Join-Path $out pack.log)
 if($LASTEXITCODE -ne 0){Get-Content (Join-Path $out pack.log) -Tail 30;throw 'MSIX validation or packaging failed.'}
}
if($Sign){
 Invoke-MagicLocalSign (Join-Path $sdk "bin/$sdkVersion/x64/signtool.exe") $certificate.Thumbprint $package (Join-Path $out 'sign.log') -Timestamp:$Timestamp
 if($signingMode -eq 'local-test'){Export-Certificate -Cert $certificate -FilePath (Join-Path $out 'MagicCommunity.Native.Local.cer')|Out-Null}
}
$files=@{};Get-ChildItem $stage -File|Where-Object Extension -notin @('.xml','.json')|ForEach-Object {$files[$_.Name]=(Get-FileHash -LiteralPath $_.FullName).Hash}
if($Distribution -and -not $Unpackaged){
 foreach($name in @('Setup.ps1','Setup.Core.ps1','Setup.Shortcut.ps1','SetupShortcut.cs','安装与卸载.cmd','预览安装流程.cmd','使用说明.md')){Copy-Item -LiteralPath (Join-Path $source ('tools/distribution/'+$name)) -Destination $out}
 @{protocol='Magic.Install/1';packageName='MagicCommunity.Native';packageFamily=$packageFamily;signingMode=$signingMode;packageFile=[IO.Path]::GetFileName($package);version=$PackageVersion;sha256=(Get-FileHash -LiteralPath $package).Hash;dataRootMode='per-user';sourceCommit=(& git -C $source rev-parse HEAD).Trim();signed=[bool]$Sign}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $out 'setup-bundle.json') -Encoding UTF8
}
if($Unpackaged){
 @{protocol='Magic.Install/1';installKind='win32';packageName='MagicCommunity.Desktop';packageFamily='MagicCommunity.Unpackaged';signingMode='unsigned';packageFile=[IO.Path]::GetFileName($package);version=$PackageVersion;sha256=(Get-FileHash -LiteralPath $package).Hash;dataRootMode='per-user';sourceCommit=(& git -C $source rev-parse HEAD).Trim();signed=$false}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $out 'setup-bundle.json') -Encoding UTF8
}
$receipt=[ordered]@{distribution=[bool]$Distribution;protocol='Magic.System.Native/1';implementation='independent-win32';package=$package;sha256=(Get-FileHash -LiteralPath $package).Hash;version=$PackageVersion;publisher=$PackagePublisher;packageFamily=$packageFamily;signingMode=$signingMode;certificateThumbprint=$(if($certificate){$certificate.Thumbprint}else{$null});currentUserCertificateOnly=$true;windowHelperAuthenticodeSigned=[bool]$Sign;timestamped=[bool]$Timestamp;sourceCommit=(& git -C $source rev-parse HEAD).Trim();projectRoot=$ProjectRoot;files=$files;installed=$false;authorizationRequested=$false;desktopActivated=$false;output=$out}
if($Unpackaged){$receipt['installKind']='win32';$receipt['packageFamily']='MagicCommunity.Unpackaged';$receipt['publisher']=$null;$receipt['currentUserCertificateOnly']=$false}
$receipt|ConvertTo-Json -Depth 7|Set-Content (Join-Path $out package-build.json) -Encoding utf8
$receipt|ConvertTo-Json -Depth 7
