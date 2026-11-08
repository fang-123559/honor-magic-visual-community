# Shared by MSIX packaging. The managed and native entry hosts embed this same
# original multi-resolution ICO, rather than a frame from the entrance movie.
function Write-MagicApplicationAssets {
    param([Parameter(Mandatory)][string]$OutputDirectory)
    $root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
    $output=[IO.Path]::GetFullPath($OutputDirectory)
    if(-not $output.StartsWith((Join-Path $root 'artifacts')+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Icon output must be in this worktree artifacts directory.'}
    [void][IO.Directory]::CreateDirectory($output)
    $icon=Join-Path $root 'assets/community/application.ico'
    Add-Type -AssemblyName PresentationCore
    $decoder=[Windows.Media.Imaging.BitmapDecoder]::Create([Uri]$icon,[Windows.Media.Imaging.BitmapCreateOptions]::PreservePixelFormat,[Windows.Media.Imaging.BitmapCacheOption]::OnLoad)
    $frame=$decoder.Frames | Sort-Object PixelWidth -Descending | Select-Object -First 1
    foreach($size in @(16,24,32,44,48,64,96,150,256)){
        $scaled=[Windows.Media.Imaging.TransformedBitmap]::new($frame,[Windows.Media.ScaleTransform]::new($size/$frame.PixelWidth,$size/$frame.PixelHeight))
        $names=@('Logo44.targetsize-'+$size+'_altform-unplated.png')
        if($size -in @(44,150)){$names+=('Logo'+$size+'.png')}
        foreach($name in $names){
            $encoder=[Windows.Media.Imaging.PngBitmapEncoder]::new()
            $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($scaled))
            $file=[IO.File]::Create((Join-Path $output $name));try{$encoder.Save($file)}finally{$file.Dispose()}
        }
    }
    Copy-Item -LiteralPath $icon -Destination (Join-Path $output 'MagicCommunity.ico')
}
