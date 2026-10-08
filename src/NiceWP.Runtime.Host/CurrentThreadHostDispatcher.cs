namespace NiceWP.Runtime.Host;

/// <summary>
/// Synchronous dispatcher executing directly on the caller thread.
/// Suitable for unit tests or headless environments where WinUI message loop is not active.
/// </summary>
public sealed class CurrentThreadHostDispatcher : IHostDispatcher
{
    public bool HasThreadAccess => true;

    public Task<T> InvokeAsync<T>(Func<Task<T>> action) => action();
    public Task<T> InvokeAsync<T>(Func<T> action) => Task.FromResult(action());
    public Task InvokeAsync(Func<Task> action) => action();
    public Task InvokeAsync(Action action)
    {
        action();
        return Task.CompletedTask;
    }
}
