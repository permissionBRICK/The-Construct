namespace Construct.Companion.Core.Desktop;

// Windows can deliver popup deactivation before or after the tray MouseDown.
// Capture the press intent so the delayed single-click action never reopens a
// popup that this same press just dismissed.
public sealed class PopupGesture
{
    private bool pressActive;
    private bool dismissedBeforePress;
    private bool show;
    public void FocusLost(bool leftButtonOnTray)
    {
        if (!leftButtonOnTray) { dismissedBeforePress=false; return; }
        if (pressActive) show=false;
        else dismissedBeforePress=true;
    }
    public void Press(bool popupVisible)
    {
        show=!popupVisible && !dismissedBeforePress;
        dismissedBeforePress=false; pressActive=true;
    }
    public bool Click()
    {
        var result=show; Reset(); return result;
    }
    public void Reset() { pressActive=false; dismissedBeforePress=false; show=false; }
}
