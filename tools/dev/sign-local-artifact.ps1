function Invoke-MagicLocalSign([string]$SignTool,[string]$Thumbprint,[string]$File,[string]$Log,[switch]$Timestamp) {
    $arguments=@('sign','/s','My','/sha1',$Thumbprint,'/fd','SHA256')
    if($Timestamp){$arguments+=@('/tr','http://timestamp.digicert.com','/td','SHA256')}
    & $SignTool @arguments $File *> $Log
    if($LASTEXITCODE -ne 0){Get-Content -LiteralPath $Log -Tail 12 | Out-Host;throw ('签名失败：'+[IO.Path]::GetFileName($File))}
    if($Timestamp){
        & $SignTool verify /pa /tw $File *> ($Log+'.verify')
        if($LASTEXITCODE -ne 0){Get-Content -LiteralPath ($Log+'.verify') -Tail 12 | Out-Host;throw ('签名或时间戳校验失败：'+[IO.Path]::GetFileName($File))}
    }
}

# Using a certificate trusted on one test PC must never select the public signing channel.
function Get-MagicPublicSigningCertificate([string]$Thumbprint, [string]$Publisher) {
    if ($Thumbprint -notmatch '^[A-Fa-f0-9]{40}$' -or $Publisher -ieq 'CN=MagicCommunity.Native.Local') {
        throw 'Use a public code-signing certificate and its actual publisher, not the local test identity.'
    }
    $cert = Get-Item -LiteralPath ('Cert:\CurrentUser\My\' + $Thumbprint) -ErrorAction Stop
    $now = Get-Date
    if (-not $cert.HasPrivateKey -or $cert.Subject -cne $Publisher -or $cert.Subject -eq $cert.Issuer -or $now -lt $cert.NotBefore -or $now -ge $cert.NotAfter) {
        throw 'The specified signing credential is unavailable, expired, self-issued, or belongs to another publisher.'
    }
    $codeSigning = [Security.Cryptography.Oid]::new('1.3.6.1.5.5.7.3.3')
    $eku = $cert.Extensions | Where-Object { $_.Oid.Value -eq '2.5.29.37' }
    if (-not $eku -or $eku.EnhancedKeyUsages.Value -notcontains $codeSigning.Value) {
        throw 'The publisher certificate must explicitly allow code signing.'
    }
    $chain = [Security.Cryptography.X509Certificates.X509Chain]::new()
    try {
        [void]$chain.ChainPolicy.ApplicationPolicy.Add($codeSigning)
        $chain.ChainPolicy.UrlRetrievalTimeout = [TimeSpan]::FromSeconds(15)
        if (-not $chain.Build($cert) -or $chain.ChainElements.Count -lt 2) {
            throw 'The public code-signing chain could not be validated.'
        }
        $anchor = $chain.ChainElements[$chain.ChainElements.Count - 1].Certificate.Thumbprint
        if (-not (Test-Path -LiteralPath ('Cert:\LocalMachine\AuthRoot\' + $anchor))) {
            throw 'The issuer root is absent from the Windows third-party public root store on this build machine.'
        }
    } finally {
        $chain.Dispose()
    }
    return $cert
}
