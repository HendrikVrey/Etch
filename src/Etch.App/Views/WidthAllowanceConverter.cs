using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Etch.App.Views;

/// <summary>
/// Subtracts a fixed allowance from a width, never going below zero.
/// </summary>
/// <remarks>
/// <para>
/// Exists for one job: bounding the tab strip inside the title bar. WPF-UI lays the
/// title bar out as a grid whose header column is <c>Auto</c> and whose caption buttons
/// sit in a later column, so a header wide enough to want the whole bar pushes minimise,
/// maximise and close off the right-hand edge. The strip therefore has to be told how
/// much room to leave.
/// </para>
/// <para>
/// A converter rather than a hard-coded number, which is what it replaced, because the
/// window is resizable, and a constant that is right at 1100 px wastes half the bar at
/// 1920 and overflows at 640.
/// </para>
/// <para>
/// Public for the same reason <c>BufferTab</c> is: XAML names it, and a markup-visible
/// type that pretends to be internal only works by accident of the generated type helper.
/// </para>
/// </remarks>
public sealed class WidthAllowanceConverter : IValueConverter
{
    /// <inheritdoc />
    /// <remarks>
    /// Anything that would produce a non-positive width falls back to
    /// <see cref="DependencyProperty.UnsetValue"/>, which leaves the target property at
    /// its own default rather than pinning it to zero. That covers the genuine error cases
    /// (a parameter that is not a number, a source that is not a width) and one ordinary
    /// one: on the very first layout pass the window has no measured width yet, and a
    /// converter that answered zero there would collapse the tab strip for a frame on
    /// every launch.
    /// </remarks>
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not double available || double.IsNaN(available) || !TryReadAllowance(parameter, out var allowance))
        {
            return DependencyProperty.UnsetValue;
        }

        var remaining = available - allowance;

        return remaining > 0d ? remaining : DependencyProperty.UnsetValue;
    }

    /// <inheritdoc />
    /// <exception cref="NotSupportedException">Always. The binding is one-way by nature.</exception>
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException("A width allowance is a one-way conversion.");

    private static bool TryReadAllowance(object? parameter, out double allowance)
    {
        // XAML hands ConverterParameter over as a string unless it is declared otherwise,
        // so both forms are accepted. Invariant culture, because the value comes from
        // markup rather than from the user.
        switch (parameter)
        {
            case double number:
                allowance = number;
                return true;

            case string text when double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed):
                allowance = parsed;
                return true;

            default:
                allowance = 0d;
                return false;
        }
    }
}
