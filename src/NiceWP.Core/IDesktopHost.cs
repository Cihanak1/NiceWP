namespace NiceWP.Core;

public interface IDesktopHost
{
    Task<DesktopHostResult> ResolveAsync(CancellationToken cancellationToken = default);

    bool IsValid(nint handle);

    /// <summary>
    /// Attaches an unowned top-level window on its owning thread and returns its previous parent.
    /// The implementation preserves and restores its native styles and bounds.
    /// The caller retains ownership of the child and must save the returned parent for Detach.
    /// Invalid handles/state and native failures are reported as exceptions.
    /// </summary>
    nint Attach(nint host, nint window);

    /// <summary>Restores the saved parent, style and bounds on the owning thread; failures throw.
    /// Repeated detachment is safe. A destroyed window only releases the saved state.</summary>
    void Detach(nint host, nint window, nint previousParent);
}
