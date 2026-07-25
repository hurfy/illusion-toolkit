namespace Illusion.Formats.Collisions;

/// <summary>Thrown when a PhysX-cooked collision mesh blob cannot be decoded into renderable geometry.</summary>
public sealed class CollisionDecodeException : Exception
{
    public CollisionDecodeException(string message) : base(message)
    {
    }
}
