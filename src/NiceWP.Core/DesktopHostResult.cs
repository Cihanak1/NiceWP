namespace NiceWP.Core;

public enum DesktopHostError
{
    None,
    ExplorerUnavailable,
    WorkerUnavailable,
    ShellUnresponsive,
    NativeFailure
}

public sealed record DesktopHostResult(nint Handle, DesktopHostError Error, int NativeError = 0)
{
    public bool Success => Handle != 0 && Error == DesktopHostError.None;
}
