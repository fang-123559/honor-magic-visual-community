# Called only by the existing main-workspace start/stop entry points.
function Start-MagicTransition([string]$Root,[ValidateSet('entrance','exit')][string]$Mode,[switch]$Logon) {
    # Login starts behind the Windows sign-in flow. Do not create a second black
    # cover or decode entrance media; manual entry and exit keep their animation.
    if($Logon -and $Mode -eq 'entrance'){return $null}
    $process=$null
    try {
        $pointerPath=Join-Path $Root 'app.current.json'
        $pointer=Get-Content -LiteralPath $pointerPath -Raw -Encoding UTF8 | ConvertFrom-Json
        if($pointer.desktopRenderer -ne 'composition'){return $null}
        $preferences=$null
        $preferencesPath=Join-Path $Root 'data/ui-preferences.json'
        if(Test-Path -LiteralPath $preferencesPath){$preferences=Get-Content -LiteralPath $preferencesPath -Raw -Encoding UTF8|ConvertFrom-Json;if($preferences.WindowAnimations -eq $false){return $null}}
        $directory=[IO.Path]::GetDirectoryName([IO.Path]::GetFullPath((Join-Path $Root $pointer.entry)))
        if(-not $directory.StartsWith((Join-Path $Root 'versions')+'\',[StringComparison]::OrdinalIgnoreCase)){return $null}
        # Pre-alpha packages cannot acknowledge the compositor blackout event.
        # Skip their animation during upgrade/rollback; keep the desktop transition.
        if(-not(Test-Path -LiteralPath (Join-Path $directory 'transitions/entrance-alpha.frames')) -or -not(Test-Path -LiteralPath (Join-Path $directory 'transitions/entrance-alpha.manifest.json'))){return $null}
        $renderer=Join-Path $directory 'MagicDesktopComposition.exe'
        $receipt=Get-Content -LiteralPath (Join-Path $directory 'composition-renderer.json') -Raw -Encoding UTF8|ConvertFrom-Json
        if((Get-FileHash -LiteralPath $renderer -Algorithm SHA256).Hash -ne $receipt.sha256){return $null}
        $nonce=[Guid]::NewGuid().ToString('N');$marker=Join-Path $Root ('runtime/desktop/transition-'+$nonce+'.signal')
        [IO.File]::WriteAllText($marker,$nonce+':pending',[Text.UTF8Encoding]::new($false))
        $media=Join-Path $directory 'transitions/entrance.mp4'
        # All quoted arguments are controlled paths or a GUID. No shell invocation.
        $arguments=@('--desktop-transition',$Mode,('"'+$marker+'"'),$nonce,('"'+$media+'"'),[string]$PID,$(if($preferences.ReduceOnBatterySaver -eq $false){'0'}else{'1'}))
        $opaque=$null
        try {
            $opaque=[Threading.EventWaitHandle]::new($false,[Threading.EventResetMode]::ManualReset,('Local\MagicTransition-'+$nonce+'-opaque'))
            $process=Start-Process -FilePath $renderer -ArgumentList $arguments -WindowStyle Normal -PassThru
            # Start the community host only after the native helper confirms its
            # black cover is opaque. No window coordinates or persistent timer.
            if($null -ne $opaque -and -not $opaque.WaitOne(8000)){
                Complete-MagicTransition @{Process=$process;Marker=$marker;Nonce=$nonce} $false
                throw 'Transition did not confirm an opaque cover; desktop startup was not released.'
            }
            return @{Process=$process;Marker=$marker;Nonce=$nonce}
        } finally {if($null -ne $opaque){$opaque.Dispose()}}
    } catch {
        if($null -ne $process -and -not $process.HasExited){throw}
        Write-Verbose ('Transition unavailable: '+$_.Exception.Message);return $null
    }
}
function Complete-MagicTransition($Transition,[bool]$Success) {
    if($null -eq $Transition){return}
    # The helper self-closes after reveal, parent exit or a 30-second hard timeout.
    try{[IO.File]::WriteAllText($Transition.Marker,$Transition.Nonce+$(if($Success){':ready'}else{':failed'}),[Text.UTF8Encoding]::new($false))}catch{Write-Verbose 'Transition completion marker unavailable.'}
}
