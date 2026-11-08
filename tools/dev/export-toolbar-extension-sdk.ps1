param([string]$OutputDirectory)
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
if(-not $OutputDirectory){$OutputDirectory=Join-Path $root ('artifacts/toolbar-extension-sdk/'+(Get-Date -Format 'yyyyMMdd-HHmmss'))}
$out=[IO.Path]::GetFullPath($OutputDirectory)
$artifactRoot=[IO.Path]::GetFullPath((Join-Path $root 'artifacts')).TrimEnd('\')+'\'
if(-not $out.StartsWith($artifactRoot,[StringComparison]::OrdinalIgnoreCase)){throw 'SDK output must remain inside this worktree artifacts directory.'}
if(Test-Path -LiteralPath $out){throw 'Use a new output directory; existing SDK artifacts are not overwritten.'}
$payload=Join-Path $out 'payload'
New-Item -ItemType Directory -Path (Join-Path $payload 'docs/extensions'),(Join-Path $payload 'integrations') -Force|Out-Null
$docs=Join-Path $root 'docs/extensions/toolbar-v1'
foreach($file in Get-ChildItem -LiteralPath $docs -Recurse -File){
    $relative=[IO.Path]::GetRelativePath($docs,$file.FullName).Replace('\','/')
    if($relative -match '(^|/)(bin|obj|artifacts|node_modules|\.git)/' -or $file.Extension -notin @('.md','.json','.cs','.csproj','.ps1')){continue}
    $target=Join-Path $payload ('docs/extensions/toolbar-v1/'+$relative)
    [void][IO.Directory]::CreateDirectory((Split-Path $target))
    Copy-Item -LiteralPath $file.FullName -Destination $target
}
$entry=@'
# 顶部栏第三方扩展开发资料

从 [注册工作指导](docs/extensions/toolbar-v1/注册工作指导.md) 开始，再阅读 [完整协议](docs/extensions/toolbar-v1/README.md)。

- [开发者提交清单](docs/extensions/toolbar-v1/提交清单模板.md)
- [机器可读接口](docs/extensions/toolbar-v1/api.json)
- [可构建示例](docs/extensions/toolbar-v1/examples/minimal-provider/Program.cs)
- [验证与尚未验收的范围](docs/extensions/toolbar-v1/VERIFICATION.json)

FILES.json 提供源提交与逐文件 SHA256。请保留目录结构；示例工程通过相对路径链接参考 SDK。
这份资料没有真实 manifest、令牌、私人配置、二进制或依赖缓存。当前生产版本与修复加载状态以注册工作指导为准。
'@
[IO.File]::WriteAllText((Join-Path $payload '开始阅读.md'),$entry,[Text.UTF8Encoding]::new($false))
$sdk=Join-Path $payload 'integrations/toolbar-extension-v1'
New-Item -ItemType Directory -Path $sdk|Out-Null
foreach($file in 'Contracts.cs','HostClient.cs','ProviderServer.cs','SOURCE.json'){Copy-Item -LiteralPath (Join-Path $root ('integrations/toolbar-extension-v1/'+$file)) -Destination $sdk}
$files=@(Get-ChildItem -LiteralPath $payload -Recurse -File|Sort-Object FullName|ForEach-Object {@{path=[IO.Path]::GetRelativePath($payload,$_.FullName).Replace('\','/');bytes=$_.Length;sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()}})
@{schemaVersion=1;documentVersion='1.0.1';sourceCommit=(& git -C $root rev-parse HEAD).Trim();protocol='Magic.ToolbarExtension/1';createdUtc=[DateTime]::UtcNow.ToString('O');files=$files;scope='Documentation, JSON Schema, generic fixtures and reference source only; no runtime tokens, app binaries or user data.'}|ConvertTo-Json -Depth 8|Set-Content -LiteralPath (Join-Path $payload 'FILES.json') -Encoding utf8
$zip=Join-Path $out 'Magic-ToolbarExtension-v1-developer-sdk.zip'
Compress-Archive -Path (Join-Path $payload '*') -DestinationPath $zip
@{path=$zip;files=$files.Count;bytes=(Get-Item -LiteralPath $zip).Length;sha256=(Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()}|ConvertTo-Json
