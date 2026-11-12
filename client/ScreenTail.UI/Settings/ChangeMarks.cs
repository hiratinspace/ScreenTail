using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ScreenTail.UI.Settings;

/// <summary>
/// Which cards in a Settings section hold a change not yet saved, for the "Changed" mark beside each
/// card's heading (Spec, Settings layout). Bound by the card's name with the spaces and hyphens left out,
/// <c>{Binding Changed[CaptureScope]}</c>, because a WPF indexer path keeps no spaces; case is ignored.
/// </summary>
public sealed class ChangeMarks : ObservableObject
{
    private HashSet<string> _keys = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Visible beside a card with a change, collapsed beside one without.</summary>
    public Visibility this[string key] => _keys.Contains(Key(key)) ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>"Capture scope" becomes "Capturescope", matched against "CaptureScope" with case ignored.</summary>
    public static string Key(string name) => new([.. (name ?? string.Empty).Where(char.IsLetterOrDigit)]);

    internal void Set(IReadOnlyList<string> changes)
    {
        var keys = new HashSet<string>(changes.Select(Key), StringComparer.OrdinalIgnoreCase);
        if (keys.SetEquals(_keys))
        {
            return;
        }

        _keys = keys;
        OnPropertyChanged("Item[]");
    }
}
