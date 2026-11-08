using System.IO;
using System.Text.Json;
namespace MagicCommunity;
// resources/native-popup-identities.json is the migration ledger. Only exact
// built-in aliases are translated; external plugin namespaces are untouched.
internal static class NativePopupIds
{
    internal static readonly IReadOnlyDictionary<string,string> Aliases = new Dictionary<string,string>(StringComparer.Ordinal)
    {
        ["@seelen/calendar-popup"]="@magic/calendar-popup",
        ["@seelen/keyboard-selector"]="@magic/keyboard-selector",
        ["@seelen/user-menu"]="@magic/user-menu",
        ["@seelen/quick-settings"]="@magic/quick-settings",
        ["@seelen/media-popup"]="@magic/media-popup",
        ["@seelen/network-popup"]="@magic/network-popup",
        ["@seelen/bluetooth-popup"]="@magic/bluetooth-popup",
        ["@seelen/notifications"]="@magic/notifications",
        ["@seelen/system-tray"]="@magic/system-tray",
        ["@seelen/apps-menu"]="@magic/apps-menu",
        ["@seelen/task-switcher"]="@magic/task-switcher",
        ["@seelen/power-menu"]="@magic/power-menu",
        ["@seelen/flyouts"]="@magic/flyouts",
    };
    internal static readonly string[] Supported = Aliases.Values.Where(id=>id!="@magic/flyouts").ToArray();
    internal static string Canonical(string id) => Aliases.TryGetValue(id,out var current) ? current : id;
    internal static string[] ReadSelection(JsonElement value)
    {
        if(!value.TryGetProperty("nativePopupProtocol",out var protocol)||protocol.GetInt32()!=1||
           !value.TryGetProperty("desktopRenderer",out var renderer)||renderer.GetString()!="composition"||
           !value.TryGetProperty("nativePopups",out var list)||list.ValueKind!=JsonValueKind.Array)return [];
        return list.EnumerateArray().Select(item=>Canonical(item.GetString()??"")).Where(Supported.Contains).Distinct(StringComparer.Ordinal).ToArray();
    }
}
