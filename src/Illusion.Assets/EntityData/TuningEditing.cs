using Illusion.Formats;
using Illusion.Formats.Actors;
using Illusion.Formats.EntityData;

namespace Illusion.Assets.EntityData;

/// <summary>
/// Writing one number of an entity-data table — a car's mass, a gear ratio, the name of the wheel it puts
/// on its axles.
///
/// <para>
/// The write lands in the extracted working copy immediately, like a prefab pick or a collision box does;
/// the archive still has to be built. Everything the core does not name keeps its bytes, so an archive
/// whose tuning was changed in one field differs from the shipped one in that field alone.
/// </para>
/// <para>
/// A field is addressed by its OFFSET in the table, never by its position in the field list: the list is
/// rebuilt every read, and an undo recorded against an index would come back to a different field the
/// moment the core learns one more name.
/// </para>
/// </summary>
public static class TuningEditing
{
    /// <summary>A field's value, in whichever of its shapes applies. One type for all five kinds keeps the
    /// undo record one shape rather than five.</summary>
    public readonly record struct TuningValue(long Number, float X, float Y, float Z, string Text)
    {
        public static TuningValue Of(long number) => new(number, 0, 0, 0, "");

        public static TuningValue Of(float x, float y = 0, float z = 0) => new(0, x, y, z, "");

        public static TuningValue Of(string text) => new(0, 0, 0, 0, text ?? "");
    }

    /// <summary>What one edit did, and everything needed to take it back.</summary>
    public sealed record Change(
        string StoragePath, int TableIndex, uint Offset, TuningValue Before, TuningValue After, string Label);

    /// <summary>
    /// Writes one field of one table. Returns null — writing nothing — when the storage cannot be opened,
    /// the table is not there, or nothing in it sits at that offset.
    /// </summary>
    public static Change? Set(string storagePath, int tableIndex, uint offset, TuningValue value, string label)
    {
        ArgumentException.ThrowIfNullOrEmpty(storagePath);

        if (Open(storagePath) is not { } storage) return null;
        if (Field(storage, tableIndex, offset) is not { } field) return null;

        TuningValue before = Read(field);
        Apply(field, value);
        AtomicFile.WriteAllBytes(storagePath, storage.ToBytes());
        return new Change(storagePath, tableIndex, offset, before, value, label);
    }

    /// <summary>Puts a field back to a value it held — the undo of <see cref="Set"/>; the redo is the same
    /// call with the change's After.</summary>
    public static bool Restore(Change change, TuningValue value)
    {
        ArgumentNullException.ThrowIfNull(change);

        if (Open(change.StoragePath) is not { } storage) return false;
        if (Field(storage, change.TableIndex, change.Offset) is not { } field) return false;

        Apply(field, value);
        AtomicFile.WriteAllBytes(change.StoragePath, storage.ToBytes());
        return true;
    }

    private static EntityDataStorageFile? Open(string path)
    {
        try { return EntityDataStorageFile.Load(path); }
        catch (Exception ex) when (ex is IOException or SdsFormatException) { return null; }
    }

    private static ActorPropertyField? Field(EntityDataStorageFile storage, int tableIndex, uint offset)
    {
        if (tableIndex < 0 || tableIndex >= storage.Tables.Count) return null;
        foreach (ActorPropertyField field in storage.Tables[tableIndex].Fields)
        {
            if (field.Offset == offset) return field;
        }
        return null;
    }

    private static TuningValue Read(ActorPropertyField field) => field.Kind switch
    {
        ActorPropertyKind.Float => TuningValue.Of(field.Single),
        ActorPropertyKind.Vector3 => TuningValue.Of(field.Vector.X, field.Vector.Y, field.Vector.Z),
        ActorPropertyKind.Text => TuningValue.Of(field.Text),
        _ => TuningValue.Of(field.Number),
    };

    private static void Apply(ActorPropertyField field, TuningValue value)
    {
        switch (field.Kind)
        {
            case ActorPropertyKind.Float:
                field.Single = value.X;
                break;
            case ActorPropertyKind.Vector3:
                field.Vector = new System.Numerics.Vector3(value.X, value.Y, value.Z);
                break;
            case ActorPropertyKind.Text:
                field.Text = value.Text;
                break;
            default:
                field.Number = value.Number;
                break;
        }
    }
}
