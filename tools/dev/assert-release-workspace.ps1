$ErrorActionPreference='Stop'
function Assert-MagicReleaseWorkspace {
    param([Parameter(Mandatory=$true)][string]$Root)
    $resolvedRoot=[IO.Path]::GetFullPath($Root).TrimEnd('\','/')
    # A linked worktree has a .git file; the primary checkout has a directory.
    # Read HEAD directly so logon does not add a Git executable/PATH dependency.
    $metadata=Join-Path $resolvedRoot '.git'
    $head=Join-Path $metadata 'HEAD'
    if(-not(Test-Path -LiteralPath $metadata -PathType Container) -or -not(Test-Path -LiteralPath $head -PathType Leaf) -or [IO.File]::ReadAllText($head).Trim() -ne 'ref: refs/heads/main'){
        throw 'Only the primary main checkout can load, stop or activate the real Magic desktop. Use tools/dev/check-worktree.ps1 for isolated checks; submit changes for integration.'
    }
}
