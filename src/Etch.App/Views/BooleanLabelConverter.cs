using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Etch.App.Views;

/// <summary>
/// Picks one of two labels from a boolean.
/// </summary>
/// <remarks>
/// <para>
/// The converter parameter carries both labels separated by a vertical bar, true first:
/// <c>ConverterParameter="Unpin|Pin"</c>.
/// </para>
/// <para>
/// It exists so the tab's context menu can say "Pin" or "Unpin" from a single item
/// without reaching for a style trigger. The trigger version needs
/// <c>BasedOn="{StaticResource {x:Type MenuItem}}"</c> to avoid throwing away the theme's
/// own menu template, which makes a right-click depend on a resource key belonging to
/// another library — and a missing key there is a crash, not a cosmetic problem.
/// </para>
/// <para>
/// Public for the same reason the other converter is: XAML names it.
/// </para>
/// </remarks>
public sealed class BooleanLabelConverter : IValueConverter
{
    private const char Separator = '|';

    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not bool flag || parameter is not string labels)
        {
            return DependencyProperty.UnsetValue;
        }

        var split = labels.IndexOf(Separator, StringComparison.Ordinal);

        // Both halves are required. Falling back to the whole string would put the same
        // label on both states, which looks like a broken menu rather than a broken
        // binding and would take far longer to track down.
        if (split <= 0 || split == labels.Length - 1)
        {
            return DependencyProperty.UnsetValue;
        }

        return flag ? labels[..split] : labels[(split + 1)..];
    }

    /// <inheritdoc />
    /// <exception cref="NotSupportedException">Always. A label cannot say which state produced it.</exception>
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException("A label is a one-way conversion.");
}
