using System.Windows.Input;

namespace Etch.App.Views;

/// <summary>
/// An <see cref="ICommand"/> over a delegate.
/// </summary>
/// <remarks>
/// Forty lines rather than a dependency. WPF-UI ships one, but this is the shape every
/// MVVM library reimplements and the one that has to keep working; owning it costs
/// nothing and removes a package from the startup path.
/// </remarks>
/// <typeparam name="T">The command parameter type.</typeparam>
internal sealed class RelayCommand<T> : ICommand
{
    private readonly Action<T> _execute;
    private readonly Predicate<T>? _canExecute;

    /// <summary>Creates a command.</summary>
    public RelayCommand(Action<T> execute, Predicate<T>? canExecute = null)
    {
        ArgumentNullException.ThrowIfNull(execute);

        _execute = execute;
        _canExecute = canExecute;
    }

    /// <inheritdoc />
    /// <remarks>
    /// Routed through <see cref="CommandManager.RequerySuggested"/>, which WPF raises
    /// whenever focus or input state changes. That is sufficient here — every command
    /// in Etch is enabled by state the user has just altered — and it avoids each
    /// command owning a subscription that has to be torn down.
    /// </remarks>
    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    /// <inheritdoc />
    public bool CanExecute(object? parameter) =>
        TryConvert(parameter, out var typed) && (_canExecute?.Invoke(typed) ?? true);

    /// <inheritdoc />
    public void Execute(object? parameter)
    {
        if (TryConvert(parameter, out var typed))
        {
            _execute(typed);
        }
    }

    /// <summary>
    /// Converts the loosely typed command parameter, rejecting anything unexpected.
    /// </summary>
    /// <remarks>
    /// A binding that resolves to nothing delivers null, and a template applied to the
    /// wrong item type delivers something else entirely. Both are bugs, but neither is
    /// worth an unhandled cast exception on a button click — the command simply does
    /// not run, and the button appears disabled. Null is rejected along with the rest:
    /// every command here acts on a tab, and "act on no tab" has no meaning.
    /// </remarks>
    private static bool TryConvert(object? parameter, out T typed)
    {
        if (parameter is T value)
        {
            typed = value;
            return true;
        }

        typed = default!;
        return false;
    }
}

/// <summary>A parameterless <see cref="ICommand"/> over a delegate.</summary>
internal sealed class RelayCommand : ICommand
{
    private readonly Action _execute;
    private readonly Func<bool>? _canExecute;

    /// <summary>Creates a command.</summary>
    public RelayCommand(Action execute, Func<bool>? canExecute = null)
    {
        ArgumentNullException.ThrowIfNull(execute);

        _execute = execute;
        _canExecute = canExecute;
    }

    /// <inheritdoc />
    public event EventHandler? CanExecuteChanged
    {
        add => CommandManager.RequerySuggested += value;
        remove => CommandManager.RequerySuggested -= value;
    }

    /// <inheritdoc />
    public bool CanExecute(object? parameter) => _canExecute?.Invoke() ?? true;

    /// <inheritdoc />
    public void Execute(object? parameter) => _execute();
}
