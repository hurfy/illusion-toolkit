namespace Illusion.Formats.Effects;

/// <summary>
/// The inside of an <c>.eff</c>, which the core carries byte-exact but does not decode: the property tree
/// <see cref="EffectsFile"/> rides as an opaque capsule, opened here so an effect can be read and edited.
///
/// <para>
/// <b>The container.</b> A chunk is <c>u32 tag, u32 size, byte[size - 8] payload</c>, the size INCLUDING
/// the header. A container's children are packed back to back and fill its payload exactly, with no
/// padding, so "container or leaf" is answered by trying to tile the payload and seeing whether it comes
/// out even. The root tag is 666. Every other tag is local to its parent — a 2 under an effect means its
/// generations, a 2 under a parameter block means something else — so nothing here assigns a tag a meaning
/// on its own; the schema layer above does, by walking down a known path.
/// </para>
/// <para>
/// <b>Why offsets rather than copies.</b> Editing an effect is overwhelmingly editing a NUMBER — a birth
/// rate, a colour, a size curve — and a number is four bytes in a payload whose length does not change. So
/// this keeps the file's bytes and hands out offsets into them: a write is one value put back where it was,
/// and everything the walk did not understand survives untouched by construction. The one structural change
/// offered — copying an effect — is a splice at a depth where exactly two ancestors need a corrected size.
/// </para>
/// </summary>
public sealed class EffectsTree
{
    /// <summary>The one tag that is not context-local: the file opens with it or it is not an effects file.</summary>
    public const uint RootTag = 666;

    /// <summary>The root's child that holds the effects.</summary>
    public const uint EffectsTag = 668;

    private const int HeaderSize = 8;

    private EffectsTree(byte[] bytes, EffectChunk root)
    {
        Bytes = bytes;
        Root = root;
    }

    /// <summary>The file's bytes, live: a value write goes straight into this buffer.</summary>
    public byte[] Bytes { get; }

    /// <summary>The 666 chunk that covers the whole file.</summary>
    public EffectChunk Root { get; }

    /// <summary>The effects, in file order. Empty when the root carries no 668 branch.</summary>
    public IReadOnlyList<EffectChunk> Effects =>
        Root.First(EffectsTag)?.Children ?? [];

    /// <summary>
    /// Walks the bytes, or returns null when they are not an effects file — one root chunk, tagged 666,
    /// whose size covers the whole file.
    /// </summary>
    public static EffectsTree? Read(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        if (Tile(bytes, 0, bytes.Length) is not [{ Tag: RootTag } root]) return null;
        return new EffectsTree(bytes, root);
    }

    /// <summary>The four bytes at this offset, as a float.</summary>
    public float ReadFloat(int offset) => BitConverter.ToSingle(Bytes, offset);

    /// <summary>Puts a float back where it came from. The payload's length cannot change, so nothing moves.</summary>
    public void WriteFloat(int offset, float value)
    {
        if (offset < 0 || offset + 4 > Bytes.Length) throw new ArgumentOutOfRangeException(nameof(offset));
        BitConverter.TryWriteBytes(Bytes.AsSpan(offset, 4), value);
    }

    /// <summary>The four bytes at this offset, as a signed 32-bit integer.</summary>
    public int ReadInt(int offset) => BitConverter.ToInt32(Bytes, offset);

    /// <summary>Puts an int back where it came from.</summary>
    public void WriteInt(int offset, int value)
    {
        if (offset < 0 || offset + 4 > Bytes.Length) throw new ArgumentOutOfRangeException(nameof(offset));
        BitConverter.TryWriteBytes(Bytes.AsSpan(offset, 4), value);
    }

    /// <summary>The single byte at this offset — an operator's enabled flag is one.</summary>
    public byte ReadByte(int offset) => Bytes[offset];

    /// <summary>Puts a byte back where it came from.</summary>
    public void WriteByte(int offset, byte value) => Bytes[offset] = value;

    /// <summary>The id of an effect — the second word of its payload, after the version.</summary>
    public uint IdOf(EffectChunk effect)
    {
        ArgumentNullException.ThrowIfNull(effect);
        return effect.Length >= 8 ? BitConverter.ToUInt32(Bytes, effect.Start + 4) : 0u;
    }

    /// <summary>
    /// A copy of the file with one effect duplicated under a new id, appended after the last one.
    ///
    /// <para>
    /// A splice rather than a rebuild: the copied bytes are the source effect's chunk verbatim, so every
    /// generation, operator and curve inside it comes across exactly as authored, and only the two chunks
    /// that now contain more — the 668 branch and the 666 root — need a corrected size. Nothing else in the
    /// file shifts meaning, because nothing in it is an offset: the format addresses by nesting.
    /// </para>
    /// </summary>
    /// <param name="source">The effect to copy, one of <see cref="Effects"/>.</param>
    /// <param name="newId">The id the copy gets. Picking a free one is the caller's job.</param>
    public byte[] WithEffectCopied(EffectChunk source, uint newId)
    {
        ArgumentNullException.ThrowIfNull(source);
        EffectChunk branch = Root.First(EffectsTag)
            ?? throw new InvalidOperationException("this file carries no effects branch to add to");
        if (source.Length < 8) throw new ArgumentException("the effect carries no id", nameof(source));

        int from = source.Start - HeaderSize;
        int size = source.Length + HeaderSize;
        int insertAt = branch.Start + branch.Length;   // after the last effect, still inside the branch

        var built = new byte[Bytes.Length + size];
        Bytes.AsSpan(0, insertAt).CopyTo(built);
        Bytes.AsSpan(from, size).CopyTo(built.AsSpan(insertAt));
        Bytes.AsSpan(insertAt).CopyTo(built.AsSpan(insertAt + size));

        // The id sits at the head of the effect's payload, right after its version word.
        BitConverter.TryWriteBytes(built.AsSpan(insertAt + HeaderSize + 4, 4), newId);

        Grow(built, branch.Start - HeaderSize, size);
        Grow(built, Root.Start - HeaderSize, size);
        return built;
    }

    private static void Grow(byte[] bytes, int header, int by)
    {
        uint was = BitConverter.ToUInt32(bytes, header + 4);
        BitConverter.TryWriteBytes(bytes.AsSpan(header + 4, 4), was + (uint)by);
    }

    /// <summary>
    /// The children of a payload, or null when it is a leaf. A container's children fill it exactly: a
    /// leftover byte, a size that runs past the end, or one too small to hold its own header all mean the
    /// bytes are data. A leaf whose data happens to tile is possible and harmless — callers descend by tag
    /// along a known path and never read what they did not go looking for.
    /// </summary>
    public static List<EffectChunk>? Tile(byte[] bytes, int start, int length)
    {
        var found = new List<EffectChunk>();
        int at = start, end = start + length;
        while (at + HeaderSize <= end)
        {
            uint tag = BitConverter.ToUInt32(bytes, at);
            uint size = BitConverter.ToUInt32(bytes, at + 4);
            if (size < HeaderSize || at + size > end) return null;
            found.Add(new EffectChunk(bytes, tag, at + HeaderSize, (int)size - HeaderSize));
            at += (int)size;
        }
        return at == end && found.Count > 0 ? found : null;
    }
}

/// <summary>One chunk: what it is called inside its parent, and where its payload lies in the file.</summary>
public sealed class EffectChunk
{
    private readonly byte[] _bytes;
    private List<EffectChunk>? _children;
    private bool _walked;

    internal EffectChunk(byte[] bytes, uint tag, int start, int length)
    {
        _bytes = bytes;
        Tag = tag;
        Start = start;
        Length = length;
    }

    /// <summary>The tag, which means whatever the PARENT says it means.</summary>
    public uint Tag { get; }

    /// <summary>Where the payload starts in the file.</summary>
    public int Start { get; }

    /// <summary>How long the payload is, without the header.</summary>
    public int Length { get; }

    /// <summary>The children, or null when the payload is data rather than chunks. Walked on first ask.</summary>
    public IReadOnlyList<EffectChunk>? Children
    {
        get
        {
            if (_walked) return _children;
            _walked = true;
            _children = EffectsTree.Tile(_bytes, Start, Length);
            return _children;
        }
    }

    /// <summary>The children carrying this tag; nothing when this is a leaf.</summary>
    public IEnumerable<EffectChunk> Where(uint tag) => (Children ?? []).Where(c => c.Tag == tag);

    /// <summary>The first child carrying this tag, or null.</summary>
    public EffectChunk? First(uint tag) => (Children ?? []).FirstOrDefault(c => c.Tag == tag);

    public override string ToString() => $"chunk {Tag} @{Start} ({Length} bytes)";
}
