namespace NiceWP.Runtime.Host;

/// <summary>
/// Abstraction for dispatching work to the UI/STA thread owning wallpaper renderers and XAML windows.
/// </summary>
public interface IHostDispatcher
{
    bool HasThreadAccess { get; }
    Task<T> InvokeAsync<T>(Func<Task<T>> action);
    Task<T> InvokeAsync<T>(Func<T> action);
    Task InvokeAsync(Func<Task> action);
    Task InvokeAsync(Action action);
}
