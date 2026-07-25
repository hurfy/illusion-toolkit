namespace Illusion.Formats.Frames;

// TODO(smell): process-wide mutable counter — every FrameEntry in the process shares this sequence,
// which would break concurrent parsing of independent documents. Scope it per FrameResource when the
// frame graph gets a construction context.
public static class RefManager
{
    //set to 10 because the first 10 are placeholders for render assets.
    private static int _currentRefID = 10;

    public static int GetNewRefID()
    {
        _currentRefID++;
        return _currentRefID;
    }
}
