using System.Windows;

namespace Illusion.Rendering.Gizmos;

/// <summary>
/// The pointer a transform is solved against, with a "slow down for fine work" mode (held Ctrl). Every gizmo
/// tool — moving, turning, resizing, locked to an axis or free — is a pure function of the pointer position, so
/// slowing the POINTER covers all of them at once, and none of them has to know precision exists.
///
/// The whole difficulty is continuity: pressing the modifier must not make the object jump, and neither must
/// letting go. So the slowed run starts from wherever the pointer had got to, and what it left behind is carried
/// on as a fixed offset — which also means turning it on and off repeatedly costs no accumulated drift beyond
/// the one offset per toggle.
/// </summary>
public sealed class PrecisionPointer
{
    /// <summary>Share of the pointer's movement the transform follows while precision is held.</summary>
    public const double Rate = 0.1;

    private Point? _origin;   // raw pointer where precision was engaged
    private Point _base;      // solved pointer at that moment — where the slowed movement starts from
    private double _offsetX, _offsetY;   // what earlier precision runs left behind

    /// <summary>Forgets everything. Called when a drag starts, so no offset leaks in from the previous one.</summary>
    public void Reset()
    {
        _origin = null;
        _offsetX = _offsetY = 0;
    }

    /// <summary>The position to solve against for a raw pointer, given whether precision is held right now.</summary>
    public Point Solve(Point raw, bool precise)
    {
        if (precise)
        {
            if (_origin is null)
            {
                _origin = raw;
                _base = new Point(raw.X + _offsetX, raw.Y + _offsetY);
            }
            return Slowed(raw, _origin.Value);
        }

        if (_origin is { } was)
        {
            // Letting go: keep the pointer where the slowed movement had reached, as a plain offset from here on.
            Point at = Slowed(raw, was);
            _offsetX = at.X - raw.X;
            _offsetY = at.Y - raw.Y;
            _origin = null;
        }
        return new Point(raw.X + _offsetX, raw.Y + _offsetY);
    }

    private Point Slowed(Point raw, Point origin) =>
        new(_base.X + (raw.X - origin.X) * Rate, _base.Y + (raw.Y - origin.Y) * Rate);
}
