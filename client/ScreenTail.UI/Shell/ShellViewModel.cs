using CommunityToolkit.Mvvm.ComponentModel;

namespace ScreenTail.UI.Shell;

public sealed partial class ShellViewModel : ObservableObject
{
    public ShellViewModel()
    {
        Status = "Not connected to the capture service";
    }

    [ObservableProperty]
    public partial string Status { get; set; }
}
