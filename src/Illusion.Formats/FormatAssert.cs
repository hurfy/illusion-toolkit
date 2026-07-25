namespace Illusion.Formats;

/// <summary>
/// Format-invariant guard: throws when a parse/serialize invariant is violated. (The vendored version
/// popped a MessageBox and continued on release builds; a library must not show UI and must not carry
/// on past corrupt data — every invariant here holds for valid Mafia II files, verified by the
/// round-trip probe across the whole install.)
/// </summary>
internal static class FormatAssert
{
    public static void Ensure(bool bCondition, string MessageFormat, params object[] MessageArgs)
    {
        if (!bCondition)
        {
            throw new FileFormatException(string.Format(MessageFormat, MessageArgs));
        }
    }
}
