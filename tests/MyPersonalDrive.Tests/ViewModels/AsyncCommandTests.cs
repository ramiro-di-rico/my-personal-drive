using MyPersonalDrive.ViewModels;
using Xunit;

namespace MyPersonalDrive.Tests.ViewModels;

/// <summary>
/// Covers docs/PLAN-TECH-DEBT.md B0.1: AsyncCommand.Execute is `async void`, so an exception
/// that escapes it would terminate the process. These tests verify the exception is routed to
/// onError instead of escaping.
/// </summary>
public class AsyncCommandTests
{
    [Fact]
    public async Task ThrowingTask_RoutesExceptionToOnError_InsteadOfEscaping()
    {
        Exception? captured = null;
        var command = new AsyncCommand(() => throw new InvalidOperationException("boom"), onError: ex => captured = ex);

        command.Execute(null);
        await WaitForIdleAsync(command);

        Assert.NotNull(captured);
        Assert.Equal("boom", captured!.Message);
    }

    [Fact]
    public async Task FaultedTask_RoutesExceptionToOnError()
    {
        Exception? captured = null;
        var command = new AsyncCommand(() => Task.FromException(new IOException("disk full")), onError: ex => captured = ex);

        command.Execute(null);
        await WaitForIdleAsync(command);

        Assert.IsType<IOException>(captured);
    }

    [Fact]
    public async Task OperationCanceledException_IsSwallowed_WithoutCallingOnError()
    {
        var onErrorCalled = false;
        var command = new AsyncCommand(() => throw new OperationCanceledException(), onError: _ => onErrorCalled = true);

        command.Execute(null);
        await WaitForIdleAsync(command);

        Assert.False(onErrorCalled);
    }

    [Fact]
    public async Task SuccessfulExecution_ReEnablesTheCommand()
    {
        var command = new AsyncCommand(() => Task.CompletedTask);

        command.Execute(null);
        await WaitForIdleAsync(command);

        Assert.True(command.CanExecute(null));
    }

    [Fact]
    public async Task NoOnErrorHandler_DoesNotThrowFromExecute()
    {
        var command = new AsyncCommand(() => throw new InvalidOperationException("boom"));

        var exception = Record.Exception(() => command.Execute(null));
        await WaitForIdleAsync(command);

        Assert.Null(exception);
        Assert.True(command.CanExecute(null));
    }

    /// <summary>
    /// Execute is `async void`, so there is nothing to await directly. In every case exercised
    /// here the underlying Func&lt;Task&gt; completes (or throws) synchronously, so this delay
    /// is a safety margin rather than a real wait.
    /// </summary>
    private static Task WaitForIdleAsync(AsyncCommand command) => Task.Delay(20);
}
