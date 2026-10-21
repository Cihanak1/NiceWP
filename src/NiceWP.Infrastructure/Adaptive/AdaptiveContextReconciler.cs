using NiceWP.Core;

namespace NiceWP.Infrastructure.Adaptive;

/// <summary>
/// Reconciles dynamic environmental changes into a single authoritative desktop state.
/// Rather than imperatively rolling back actions when a condition ends, the reconciler recomputes
/// the current truth across all active signals (e.g., when Gaming ends while Battery Saver remains active,
/// the engine transitions smoothly into Battery Saver rather than naively resuming playback).
/// </summary>
public sealed class AdaptiveContextReconciler
{
    private readonly object gate = new();
    private AdaptiveStateResolverInput currentInput;
    private EffectiveDesktopState currentState;

    /// <summary>
    /// Raised whenever the reconciled effective desktop state changes.
    /// </summary>
    public event Action<EffectiveDesktopState>? StateReconciled;

    public EffectiveDesktopState CurrentState
    {
        get
        {
            lock (gate)
            {
                return currentState;
            }
        }
    }

    public AdaptiveStateResolverInput CurrentInput
    {
        get
        {
            lock (gate)
            {
                return currentInput;
            }
        }
    }

    public AdaptiveContextReconciler(AdaptiveStateResolverInput? initialInput = null)
    {
        currentInput = initialInput ?? new AdaptiveStateResolverInput();
        currentState = AdaptiveDesktopStateResolver.Resolve(currentInput);
    }

    /// <summary>
    /// Applies a mutation to the current input context and recomputes the effective state.
    /// Emits <see cref="StateReconciled"/> if the resulting state differs from the prior state.
    /// </summary>
    public EffectiveDesktopState Reconcile(Func<AdaptiveStateResolverInput, AdaptiveStateResolverInput> transform)
    {
        ArgumentNullException.ThrowIfNull(transform);

        EffectiveDesktopState nextState;
        EffectiveDesktopState previousState;
        Action<EffectiveDesktopState>? handler;

        lock (gate)
        {
            previousState = currentState;
            currentInput = transform(currentInput);
            currentState = AdaptiveDesktopStateResolver.Resolve(currentInput);
            nextState = currentState;
            handler = StateReconciled;
        }

        if (nextState != previousState)
        {
            handler?.Invoke(nextState);
        }

        return nextState;
    }

    /// <summary>
    /// Forces an immediate evaluation of the current input truth without mutating inputs.
    /// </summary>
    public EffectiveDesktopState Reevaluate()
    {
        return Reconcile(static current => current);
    }
}
