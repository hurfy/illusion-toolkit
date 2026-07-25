using System.Text;
using Illusion.Formats.Hashing;
using Illusion.Formats.Materials;
using Illusion.Formats.Materials.Versions;

namespace Illusion.Formats.Native.Materials;

/// <summary>The materials facade over the native core: the byte image of a .mtl library
/// crosses the boundary whole; this class maps the wire model onto the editable managed
/// material objects and back.</summary>
internal static class NativeMtl
{
    internal static unsafe Model.MtlLibraryW LoadLibrary(ReadOnlySpan<byte> file)
    {
        int status;
        MfRawBuffer raw;
        fixed (byte* p = file)
        {
            status = MtlNativeMethods.MtlLoad(p, (ulong)file.Length, out raw);
        }
        using var buffer = new MfBuffer(raw);
        ThrowOnError(status, "mf_mtl_load");
        using var stream = new MemoryStream(buffer.ToArray(), writable: false);
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        return Model.MtlLibraryW.ReadFrom(reader);
    }

    internal static unsafe byte[] SaveLibrary(Model.MtlLibraryW model)
    {
        using var wireStream = new MemoryStream();
        using (var writer = new BinaryWriter(wireStream, Encoding.UTF8, leaveOpen: true))
        {
            model.WriteTo(writer);
        }
        byte[] wire = wireStream.ToArray();

        int status;
        MfRawBuffer raw;
        fixed (byte* p = wire)
        {
            status = MtlNativeMethods.MtlSave(p, (ulong)wire.Length, out raw);
        }
        using var buffer = new MfBuffer(raw);
        ThrowOnError(status, "mf_mtl_save");
        return buffer.ToArray();
    }

    /// <summary>Wire → the managed material objects, in wire order (the write order).</summary>
    internal static Dictionary<ulong, IMaterial> ToMaterials(Model.MtlLibraryW wire)
    {
        var materials = new Dictionary<ulong, IMaterial>();
        foreach (Model.MtlMaterialW entry in wire.Materials)
        {
            IMaterial material = MaterialFactory.ConstructMaterial((MaterialVersion)wire.Version);
            material.MaterialName.String = entry.Name;
            material.MaterialName.Hash = entry.Hash;
            material.Flags = (MaterialFlags)entry.Flags;
            material.ShaderID = entry.ShaderId;
            material.ShaderHash = entry.ShaderHash;
            material.Parameters = new List<MaterialParameter>();
            foreach (Model.MtlParameterW parameter in entry.Parameters)
            {
                material.Parameters.Add(new MaterialParameter
                {
                    ID = parameter.Id,
                    Paramaters = [.. parameter.Values],
                });
            }

            if (material is Material_v57 v57)
            {
                v57.Unk0 = entry.Unk0;
                v57.Unk1 = entry.Unk1;
                v57.Unk3 = entry.Unk3;
                v57.Unk4 = entry.Unk4;
                v57.Unk5 = entry.Unk5;
                v57.Samplers = [.. entry.Samplers.Select(s => FillSampler(new MaterialSampler_v57(), s))];
            }
            else if (material is Material_v58 v58)
            {
                v58.Unk0 = entry.Unk0;
                v58.Unk1 = entry.Unk1;
                v58.Unk2 = entry.Unk2;
                v58.Unk3 = entry.Unk3;
                v58.Unk4 = entry.Unk4;
                v58.Unk5 = entry.Unk5;
                v58.Unk6 = entry.Unk6;
                v58.Unk7 = entry.Unk7;
                v58.Samplers = [.. entry.Samplers.Select(s => FillSampler(new MaterialSampler_v58(), s))];
            }
            materials.Add(entry.Hash, material);
        }
        return materials;
    }

    /// <summary>The managed material objects → wire, preserving dictionary order (the managed
    /// writer serializes in that order, and it is significant).</summary>
    internal static Model.MtlLibraryW ToWire(MaterialVersion version, int unk2,
        Dictionary<ulong, IMaterial> materials)
    {
        var wire = new Model.MtlLibraryW { Version = (uint)version, Unk2 = unk2 };
        foreach (IMaterial source in materials.Values)
        {
            IMaterial material = source.GetMTLVersion() != version
                ? MaterialFactory.ConvertMaterial(version, source)
                : source;

            var entry = new Model.MtlMaterialW
            {
                Hash = material.MaterialName.Hash,
                Name = material.MaterialName.String,
                Flags = (uint)material.Flags,
                ShaderId = material.ShaderID,
                ShaderHash = material.ShaderHash,
            };
            foreach (MaterialParameter parameter in material.Parameters)
            {
                var parameterWire = new Model.MtlParameterW { Id = parameter.ID };
                parameterWire.Values.AddRange(parameter.Paramaters);
                entry.Parameters.Add(parameterWire);
            }

            if (material is Material_v57 v57)
            {
                entry.Unk0 = v57.Unk0;
                entry.Unk1 = v57.Unk1;
                entry.Unk3 = v57.Unk3;
                entry.Unk4 = v57.Unk4;
                entry.Unk5 = v57.Unk5;
                foreach (MaterialSampler_v57 sampler in v57.Samplers)
                {
                    entry.Samplers.Add(SamplerToWire(sampler.ID, sampler.UnkSet0, sampler.TextureName,
                        sampler.TexType, sampler.UnkZero, sampler.SamplerStates, sampler.UnkSet1));
                }
            }
            else if (material is Material_v58 v58)
            {
                entry.Unk0 = v58.Unk0;
                entry.Unk1 = v58.Unk1;
                entry.Unk2 = v58.Unk2;
                entry.Unk3 = v58.Unk3;
                entry.Unk4 = v58.Unk4;
                entry.Unk5 = v58.Unk5;
                entry.Unk6 = v58.Unk6;
                entry.Unk7 = v58.Unk7;
                foreach (MaterialSampler_v58 sampler in v58.Samplers)
                {
                    entry.Samplers.Add(SamplerToWire(sampler.ID, sampler.UnkSet0, sampler.TextureName,
                        sampler.TexType, sampler.UnkZero, sampler.SamplerStates, sampler.UnkSet1));
                }
            }
            wire.Materials.Add(entry);
        }
        return wire;
    }

    private static T FillSampler<T>(T sampler, Model.MtlSamplerW wire) where T : IMaterialSampler
    {
        sampler.ID = wire.Id;
        sampler.SamplerStates = [.. wire.SamplerStates];
        if (sampler is MaterialSampler_v57 v57)
        {
            v57.UnkSet0 = [.. wire.UnkSet0];
            v57.UnkSet1 = [.. wire.UnkSet1];
            v57.TextureName.String = wire.TextureName;
            v57.TextureName.Hash = wire.TextureHash;
            v57.TexType = wire.TexType;
            v57.UnkZero = wire.UnkZero;
        }
        else if (sampler is MaterialSampler_v58 v58)
        {
            v58.UnkSet0 = [.. wire.UnkSet0];
            v58.UnkSet1 = [.. wire.UnkSet1];
            v58.TextureName.String = wire.TextureName;
            v58.TextureName.Hash = wire.TextureHash;
            v58.TexType = wire.TexType;
            v58.UnkZero = wire.UnkZero;
        }
        return sampler;
    }

    private static Model.MtlSamplerW SamplerToWire(string id, int[] unkSet0,
        HashName textureName, byte texType, byte unkZero, byte[] samplerStates, int[] unkSet1)
    {
        var wire = new Model.MtlSamplerW
        {
            Id = id,
            TextureHash = textureName.Hash,
            TexType = texType,
            UnkZero = unkZero,
            SamplerStates = [.. samplerStates],
            TextureName = textureName.String,
        };
        wire.UnkSet0.AddRange(unkSet0);
        wire.UnkSet1.AddRange(unkSet1);
        return wire;
    }

    private static void ThrowOnError(int status, string entryPoint)
    {
        if (status == NativeMethods.Ok)
        {
            return;
        }
        string error = NativeFormats.LastError;
        throw new InvalidDataException(error.Length != 0 ? error : $"{entryPoint} failed ({status})");
    }
}
