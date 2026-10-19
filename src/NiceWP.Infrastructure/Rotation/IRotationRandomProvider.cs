namespace NiceWP.Infrastructure.Rotation;

/// <summary>
/// Abstraction for random index generation in wallpaper shuffle rotation.
/// Enables deterministic unit testing without static random state.
/// </summary>
public interface IRotationRandomProvider
{
    /// <summary>
    /// Returns a non-negative random integer that is less than the specified maximum.
    /// </summary>
    int Next(int maxValue);
}

/// <summary>
/// Default implementation backed by <see cref="Random.Shared"/>.
/// </summary>
public sealed class DefaultRotationRandomProvider : IRotationRandomProvider
{
    public static readonly DefaultRotationRandomProvider Instance = new();

    public int Next(int maxValue) =>
        maxValue <= 1 ? 0 : Random.Shared.Next(maxValue);
}
