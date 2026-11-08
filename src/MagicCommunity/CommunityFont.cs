using System.Windows;
using System.Windows.Media;
using System.IO;

namespace MagicCommunity;

// Resolve once per process. Packaged files are private to this application.
internal static class CommunityFont
{
    internal static FontFamily Family { get; } = Resolve();

    private static FontFamily Resolve()
    {
        var bundled = ResolveBundled(Path.Combine(AppContext.BaseDirectory, "fonts"));
        if (bundled is not null) return bundled;

        var installed = Fonts.SystemFontFamilies.ToArray();
        foreach (string candidate in new[] { "HONOR Sans CN", "HONOR Sans Design" })
        {
            var family = installed.FirstOrDefault(value =>
                string.Equals(value.Source, candidate, StringComparison.OrdinalIgnoreCase) ||
                value.FamilyNames.Values.Any(name => string.Equals(name, candidate, StringComparison.OrdinalIgnoreCase)));
            if (family is not null) return family;
        }
        return SystemFonts.MessageFontFamily;
    }

    internal static FontFamily? ResolveBundled(string directory)
    {
        if (!File.Exists(Path.Combine(directory, "HONORSansCN-Regular.ttf"))) return null;
        try
        {
            var location = new Uri(Path.GetFullPath(directory) + Path.DirectorySeparatorChar);
            var family = new FontFamily(location, "./#HONOR Sans CN");
            var regular = new Typeface(family, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
            if (regular.TryGetGlyphTypeface(out var glyph) && glyph.FontUri.IsFile &&
                string.Equals(Path.GetDirectoryName(glyph.FontUri.LocalPath),
                    Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
                return family;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or
            ArgumentException or NotSupportedException or FormatException)
        {
            // Missing/corrupt optional assets must not prevent the desktop from starting.
        }
        return null;
    }
}
