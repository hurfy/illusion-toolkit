using System.Numerics;
using Illusion.Assets;

namespace Illusion.Diagnostics.Probes;

/// <summary>Shared probe helpers: environment initialization and small assertion/diff utilities.</summary>
internal static class ProbeAssert
{
    /// <summary>Initializes the game environment from the launcher-saved settings path — probes are headless
    /// and cannot ask for the path interactively.</summary>
    internal static bool InitEnv(out string? error) =>
        MafiaEnvironment.TryInitialize(UserSettings.Load().GamePath, out error);

    internal static bool Approx(Vector3 a, Vector3 b, float eps = 1e-3f) => (a - b).Length() <= eps;
    internal static bool QApprox(Quaternion a, Quaternion b, float eps = 1e-3f) => MathF.Abs(Quaternion.Dot(a, b)) > 1f - eps;
    internal static bool Near(double a, double b) => Math.Abs(a - b) < 1e-6;

    internal static long FirstDiff(byte[] a, byte[] b)
    {
        long n = Math.Min(a.Length, b.Length);
        for (long i = 0; i < n; i++)
        {
            if (a[i] != b[i]) return i;
        }
        return n;
    }
}
