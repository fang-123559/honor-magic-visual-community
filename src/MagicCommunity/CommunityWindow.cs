using System.Windows;

namespace MagicCommunity;

// Default base for community-owned user windows. No thread/global window hook.
public class CommunityWindow : Window
{
    public CommunityWindow()
    {
        SourceInitialized += (_, _) => {
            if (WindowStyle != WindowStyle.None) NativeWindowCaption.HideIcon(this);
        };
    }
}
