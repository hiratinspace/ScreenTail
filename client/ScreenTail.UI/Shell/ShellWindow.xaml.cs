using System.Windows;

namespace ScreenTail.UI.Shell;

public partial class ShellWindow : Window
{
    public ShellWindow()
    {
        InitializeComponent();
        DataContext = new ShellViewModel();
    }
}
