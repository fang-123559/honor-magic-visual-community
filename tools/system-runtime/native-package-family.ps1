# Derive the package family using Windows' app model; the same value is pinned
# in the elevated helper and recorded in the installer metadata.
function Get-MagicNativePackageFamily([string]$Publisher) {
    if ([string]::IsNullOrWhiteSpace($Publisher) -or $Publisher.Length -gt 8192 -or $Publisher -match '[\x00-\x1f]') {
        throw 'Invalid package publisher.'
    }
    [void][Security.Cryptography.X509Certificates.X500DistinguishedName]::new($Publisher)
    if (-not ('MagicPackaging.PackageIdentity' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace MagicPackaging {
    public static class PackageIdentity {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct Id {
            public uint Reserved, Architecture;
            public ulong Version;
            public string Name, Publisher, ResourceId, PublisherId;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        private static extern int PackageFamilyNameFromId(ref Id id, ref uint length, StringBuilder family);

        public static string ForPublisher(string publisher) {
            var id = new Id { Name = "MagicCommunity.Native", Publisher = publisher, Architecture = 9, Version = 1UL << 48 };
            var family = new StringBuilder(128);
            uint length = (uint)family.Capacity;
            int result = PackageFamilyNameFromId(ref id, ref length, family);
            if (result != 0) throw new Win32Exception(result);
            return family.ToString();
        }
    }
}
'@
    }
    $family = [MagicPackaging.PackageIdentity]::ForPublisher($Publisher)
    if ($family -cnotmatch '^MagicCommunity\.Native_[a-z0-9]{13}$') { throw 'Unexpected Windows package family.' }
    return $family
}
