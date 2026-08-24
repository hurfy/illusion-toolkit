namespace Illusion.Mcp.Tools;

/// <summary>
/// The paging every list-shaped tool shares. A district's FrameResource holds tens of thousands of
/// objects and a stock archive thousands of resources; handing all of that back in one response
/// would blow the model's context on a single call, so the browsing tools window their arrays and
/// report <c>total</c> alongside so the caller knows what it has not seen.
/// </summary>
internal static class Page
{
    /// <summary>What a tool serves when the caller names no limit.</summary>
    public const int DefaultLimit = 100;

    /// <summary>The ceiling a caller cannot argue past. Not a correctness bound — a courtesy to the
    /// context window on the other end of the socket.</summary>
    private const int MaxLimit = 5000;

    /// <summary>Reduces a caller's window to one the server will actually serve. A limit of zero or
    /// less means "unspecified", not "nothing" — an omitted argument arrives as the default 0.</summary>
    public static (int Offset, int Limit) Clamp(int offset, int limit) =>
        (Math.Max(0, offset), limit <= 0 ? DefaultLimit : Math.Min(limit, MaxLimit));

    /// <summary>The requested window of <paramref name="source"/>, clamped to what is there.</summary>
    public static List<T> Slice<T>(IReadOnlyList<T> source, int offset, int limit)
    {
        if (offset >= source.Count)
        {
            return [];
        }

        int count = Math.Min(limit, source.Count - offset);
        var window = new List<T>(count);
        for (int i = 0; i < count; i++)
        {
            window.Add(source[offset + i]);
        }
        return window;
    }
}
