# Atomic readiness receipts can be replaced while the launcher reads them.
# Read one bounded file handle, allowing rename/delete but not in-place writers.
function Read-MagicNativeJson {
    param([Parameter(Mandatory=$true)][string]$Path,[int]$MaximumBytes=65536,[switch]$AllowTransient)
    try {
        $attributes=[IO.File]::GetAttributes($Path)
        if($attributes -band [IO.FileAttributes]::ReparsePoint){throw [IO.InvalidDataException]::new('Native document is a link.')}
        $stream=[IO.FileStream]::new($Path,[IO.FileMode]::Open,[IO.FileAccess]::Read,([IO.FileShare]::Read -bor [IO.FileShare]::Delete))
        try {
            if($stream.Length -le 0 -or $stream.Length -gt $MaximumBytes){throw [IO.InvalidDataException]::new('Native document exceeds bound or is empty.')}
            $reader=[IO.StreamReader]::new($stream,[Text.UTF8Encoding]::new($false,$true))
            try {$text=$reader.ReadToEnd()} finally {$reader.Dispose()}
        } finally {$stream.Dispose()}
        return ($text | ConvertFrom-Json -ErrorAction Stop)
    } catch {
        # PowerShell wraps FileStream's IOException in MethodInvocationException.
        # Only missing/replaced/briefly locked receipts may be retried; malformed
        # JSON, permissions, unsafe paths and identity errors remain real failures.
        $errorObject=$_.Exception
        while($errorObject.InnerException){$errorObject=$errorObject.InnerException}
        $code=$errorObject.HResult -band 65535
        if($AllowTransient -and (($errorObject -is [IO.IOException] -and $code -in @(2,3,32,33)))){return $null}
        throw
    }
}
