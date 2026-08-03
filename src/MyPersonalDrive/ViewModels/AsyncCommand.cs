using System.Windows.Input;

namespace MyPersonalDrive.ViewModels;

public sealed class AsyncCommand : ICommand
{
    private readonly Func<Task> _execute;
    private readonly Func<bool>? _canExecute;
    private readonly Action<Exception>? _onError;
    private bool _isExecuting;

    public AsyncCommand(Func<Task> execute, Func<bool>? canExecute = null, Action<Exception>? onError = null)
    {
        _execute = execute;
        _canExecute = canExecute;
        _onError = onError;
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => !_isExecuting && (_canExecute?.Invoke() ?? true);

    /// <summary>
    /// `ICommand.Execute` is `async void`, so a caller cannot await the work it starts. That's
    /// fine for a button click but useless to anything that needs to know when the command
    /// finished — tests, above all. This is the same body, awaitable; <see cref="Execute"/> is
    /// now just the fire-and-forget wrapper the binding layer needs.
    /// </summary>
    public async Task ExecuteAsync(object? parameter = null)
    {
        if (!CanExecute(parameter))
        {
            return;
        }

        _isExecuting = true;
        RaiseCanExecuteChanged();

        try
        {
            await _execute();
        }
        catch (OperationCanceledException)
        {
            // Expected when navigation cancels an in-flight operation.
        }
        catch (Exception ex)
        {
            // This method is `async void`: any exception that escapes here terminates the
            // process. Route it to the caller instead of letting it propagate.
            _onError?.Invoke(ex);
        }
        finally
        {
            _isExecuting = false;
            RaiseCanExecuteChanged();
        }
    }

    public async void Execute(object? parameter) => await ExecuteAsync(parameter);

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
