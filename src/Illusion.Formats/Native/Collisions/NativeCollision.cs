using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using Illusion.Formats.Collisions;

namespace Illusion.Formats.Native.Collisions;

/// <summary>
/// The collision facade over the native core: every byte-level .col and cooked-mesh operation
/// runs in Mafia.Formats.dll, and this class translates between the wire images and the
/// editable managed types (<see cref="CollisionFile"/>, <see cref="CookedTriangleMesh"/>).
/// The managed byte code survives as the <c>*Managed</c> twins until the P6 cutover — the
/// dual-path probe diffs the two on the whole install.
/// </summary>
internal static class NativeCollision
{
    // ── .col ──

    internal static unsafe CollisionFile Load(ReadOnlySpan<byte> file)
    {
        int status;
        MfRawBuffer raw;
        fixed (byte* p = file)
        {
            status = ColNativeMethods.Load(p, (ulong)file.Length, out raw);
        }
        using var buffer = new MfBuffer(raw);
        ThrowOnError(status, "mf_col_load");

        using var stream = new MemoryStream(buffer.ToArray(), writable: false);
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        Model.CollisionModel model = Model.CollisionModel.ReadFrom(reader);

        var result = new CollisionFile { Version = model.Version, Platform = model.Platform };
        foreach (Model.CollisionInstance instance in model.Instances)
        {
            result.Instances.Add(new Formats.Collisions.CollisionInstance
            {
                Position = instance.Position,
                Rotation = instance.Rotation,
                Hash = instance.Hash,
                Unk4 = instance.Unk4,
                Group = instance.Group,
            });
        }
        foreach (Model.CollisionMesh mesh in model.Meshes)
        {
            var target = new Formats.Collisions.CollisionMesh { Hash = mesh.Hash, CookedMesh = mesh.CookedMesh };
            foreach (Model.CollisionSection section in mesh.Sections)
            {
                target.Sections.Add(new Formats.Collisions.CollisionSection
                {
                    Start = section.Start,
                    NumEdges = section.NumEdges,
                    Material = section.Material,
                    Unk2 = section.Unk2,
                });
            }
            result.Meshes.Add(target);
        }
        return result;
    }

    internal static unsafe byte[] Save(CollisionFile file)
    {
        var model = new Model.CollisionModel { Version = file.Version, Platform = file.Platform };
        foreach (Formats.Collisions.CollisionInstance instance in file.Instances)
        {
            model.Instances.Add(new Model.CollisionInstance
            {
                Position = instance.Position,
                Rotation = instance.Rotation,
                Hash = instance.Hash,
                Unk4 = instance.Unk4,
                Group = instance.Group,
            });
        }
        foreach (Formats.Collisions.CollisionMesh mesh in file.Meshes)
        {
            var source = new Model.CollisionMesh { Hash = mesh.Hash, CookedMesh = mesh.CookedMesh ?? [] };
            foreach (Formats.Collisions.CollisionSection section in mesh.Sections)
            {
                source.Sections.Add(new Model.CollisionSection
                {
                    Start = section.Start,
                    NumEdges = section.NumEdges,
                    Material = section.Material,
                    Unk2 = section.Unk2,
                });
            }
            model.Meshes.Add(source);
        }

        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            model.WriteTo(writer);
        }
        byte[] wire = stream.ToArray();

        int status;
        MfRawBuffer raw;
        fixed (byte* p = wire)
        {
            status = ColNativeMethods.Save(p, (ulong)wire.Length, out raw);
        }
        using var buffer = new MfBuffer(raw);
        ThrowOnError(status, "mf_col_save");
        return buffer.ToArray();
    }

    // ── cooked-mesh capsule operations ──

    internal static unsafe CookedTriangleMesh Decode(byte[] cooked)
    {
        int status;
        MfRawBuffer raw;
        fixed (byte* p = cooked)
        {
            status = ColNativeMethods.DecodeMesh(p, (ulong)cooked.Length, out raw);
        }
        using var buffer = new MfBuffer(raw);
        ThrowOnError(status, "mf_col_decode_mesh");

        using var stream = new MemoryStream(buffer.ToArray(), writable: false);
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        Model.DecodedMesh decoded = Model.DecodedMesh.ReadFrom(reader);

        var triangles = new int[decoded.Triangles.Count];
        for (int i = 0; i < triangles.Length; i++)
        {
            triangles[i] = (int)decoded.Triangles[i];
        }
        return CookedTriangleMesh.FromDecoded(
            [.. decoded.Vertices], triangles, [.. decoded.TriangleMaterials]);
    }

    internal static unsafe int ValidateOpcodeTail(byte[] cooked)
    {
        int status;
        int trailing;
        fixed (byte* p = cooked)
        {
            status = ColNativeMethods.ValidateTail(p, (ulong)cooked.Length, out trailing);
        }
        ThrowOnError(status, "mf_col_validate_tail");
        return trailing;
    }

    internal static unsafe Model.ColMeshLayoutW MeshLayout(byte[] cooked)
    {
        int status;
        MfRawBuffer raw;
        fixed (byte* p = cooked)
        {
            status = ColNativeMethods.MeshLayout(p, (ulong)cooked.Length, out raw);
        }
        using var buffer = new MfBuffer(raw);
        ThrowOnError(status, "mf_col_mesh_layout");
        using var stream = new MemoryStream(buffer.ToArray(), writable: false);
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        return Model.ColMeshLayoutW.ReadFrom(reader);
    }

    internal static unsafe bool TailSupported(byte[] cooked, out string? refusal)
    {
        int status;
        int supported;
        fixed (byte* p = cooked)
        {
            status = ColNativeMethods.TailSupported(p, (ulong)cooked.Length, out supported);
        }
        ThrowOnError(status, "mf_col_tail_supported");
        refusal = supported != 0 ? null : NativeFormats.LastError;
        return supported != 0;
    }

    internal static unsafe byte[] Scale(byte[] cooked, Vector3 scale)
    {
        int status;
        MfRawBuffer raw;
        fixed (byte* p = cooked)
        {
            status = ColNativeMethods.ScaleMesh(
                p, (ulong)cooked.Length, scale.X, scale.Y, scale.Z, out raw);
        }
        using var buffer = new MfBuffer(raw);
        if (status == NativeMethods.Ok)
        {
            return buffer.ToArray();
        }

        // The managed twin distinguished "malformed blob" from "tail layout not recognised";
        // keep that contract for callers that catch NotSupportedException specifically.
        string error = NativeFormats.LastError;
        if (error.Contains("metadata", StringComparison.Ordinal)
            || error.Contains("layout not recognised", StringComparison.Ordinal))
        {
            throw new NotSupportedException(error);
        }
        throw new CollisionDecodeException(error);
    }

    internal static unsafe byte[] Widen(byte[] cooked)
    {
        int status;
        MfRawBuffer raw;
        fixed (byte* p = cooked)
        {
            status = ColNativeMethods.WidenMesh(p, (ulong)cooked.Length, out raw);
        }
        using var buffer = new MfBuffer(raw);
        ThrowOnError(status, "mf_col_widen_mesh");
        return buffer.ToArray();
    }

    internal static unsafe CollisionSectionPlan? TryBuildSections(
        int[] triangleIndices, ushort[] surfaceIds, out string? refusal)
    {
        int status;
        MfRawBuffer raw;
        fixed (int* indices = triangleIndices)
        fixed (ushort* surfaces = surfaceIds)
        {
            status = ColNativeMethods.BuildSections(
                (uint*)indices, (ulong)triangleIndices.Length,
                surfaces, (ulong)surfaceIds.Length, out raw);
        }
        using var buffer = new MfBuffer(raw);
        if (status != NativeMethods.Ok)
        {
            refusal = NativeFormats.LastError;
            return null;
        }
        refusal = null;

        using var stream = new MemoryStream(buffer.ToArray(), writable: false);
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        Model.SectionPlan plan = Model.SectionPlan.ReadFrom(reader);

        var indicesOut = new int[plan.TriangleIndices.Count];
        for (int i = 0; i < indicesOut.Length; i++)
        {
            indicesOut[i] = (int)plan.TriangleIndices[i];
        }
        var sections = new List<Formats.Collisions.CollisionSection>(plan.Sections.Count);
        foreach (Model.CollisionSection section in plan.Sections)
        {
            sections.Add(new Formats.Collisions.CollisionSection
            {
                Start = section.Start,
                NumEdges = section.NumEdges,
                Material = section.Material,
                Unk2 = section.Unk2,
            });
        }
        return new CollisionSectionPlan(indicesOut, [.. plan.SurfaceIds], sections);
    }

    internal static unsafe byte[]? TryWriteCookerBin(
        Vector3[] positions, int[] triangleIndices, ushort[] surfaceIds, out string? refusal)
    {
        ReadOnlySpan<float> floats = MemoryMarshal.Cast<Vector3, float>(positions);
        int status;
        MfRawBuffer raw;
        fixed (float* p = floats)
        fixed (int* indices = triangleIndices)
        fixed (ushort* surfaces = surfaceIds)
        {
            status = ColNativeMethods.CookerBin(
                p, (ulong)positions.Length, (uint*)indices, (ulong)triangleIndices.Length,
                surfaces, (ulong)surfaceIds.Length, out raw);
        }
        using var buffer = new MfBuffer(raw);
        if (status != NativeMethods.Ok)
        {
            refusal = NativeFormats.LastError;
            return null;
        }
        refusal = null;
        return buffer.ToArray();
    }

    private static void ThrowOnError(int status, string entryPoint)
    {
        if (status == NativeMethods.Ok)
        {
            return;
        }
        string error = NativeFormats.LastError;
        throw new CollisionDecodeException(error.Length != 0 ? error : $"{entryPoint} failed ({status})");
    }
}
