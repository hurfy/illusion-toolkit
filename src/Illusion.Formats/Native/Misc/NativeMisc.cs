using System.Text;

namespace Illusion.Formats.Native.Misc;

/// <summary>The small-format facade over the native core: generic load/save plumbing —
/// bytes cross whole, the wire model is (de)serialized by the generated code.</summary>
internal static class NativeMisc
{
    internal delegate int LoadEntry(nint file, ulong len, out MfRawBuffer modelWire);

    internal static unsafe T Load<T>(ReadOnlySpan<byte> file, string entryPoint,
        Func<BinaryReader, T> readModel)
    {
        int status;
        MfRawBuffer raw;
        fixed (byte* p = file)
        {
            status = entryPoint switch
            {
                "mf_ids_load" => MiscNativeMethods.IdsLoad(p, (ulong)file.Length, out raw),
                "mf_act_load" => MiscNativeMethods.ActLoad(p, (ulong)file.Length, out raw),
                "mf_nav_aiworld_load" => MiscNativeMethods.NavAiWorldLoad(p, (ulong)file.Length, out raw),
                "mf_nav_objdata_load" => MiscNativeMethods.NavObjDataLoad(p, (ulong)file.Length, out raw),
                "mf_speech_load" => MiscNativeMethods.SpeechLoad(p, (ulong)file.Length, out raw),
                "mf_anim2_load" => MiscNativeMethods.Anim2Load(p, (ulong)file.Length, out raw),
                "mf_animtex_load" => MiscNativeMethods.AnimTexLoad(p, (ulong)file.Length, out raw),
                "mf_eds_load" => MiscNativeMethods.EdsLoad(p, (ulong)file.Length, out raw),
                "mf_cutscene_load" => MiscNativeMethods.CutsceneLoad(p, (ulong)file.Length, out raw),
                "mf_prefab_load" => MiscNativeMethods.PrefabLoad(p, (ulong)file.Length, out raw),
                "mf_entity_activator_load" => MiscNativeMethods.EntityActivatorLoad(p, (ulong)file.Length, out raw),
                "mf_tapindices_load" => MiscNativeMethods.TapIndicesLoad(p, (ulong)file.Length, out raw),
                "mf_soundsectors_load" => MiscNativeMethods.SoundSectorsLoad(p, (ulong)file.Length, out raw),
                "mf_fas_load" => MiscNativeMethods.FasLoad(p, (ulong)file.Length, out raw),
                "mf_fxa_load" => MiscNativeMethods.FxaLoad(p, (ulong)file.Length, out raw),
                "mf_atp_load" => MiscNativeMethods.AtpLoad(p, (ulong)file.Length, out raw),
                "mf_dat_load" => MiscNativeMethods.DatLoad(p, (ulong)file.Length, out raw),
                "mf_eff_load" => MiscNativeMethods.EffLoad(p, (ulong)file.Length, out raw),
                "mf_tyres_load" => MiscNativeMethods.TyresLoad(p, (ulong)file.Length, out raw),
                "mf_cityshops_load" => MiscNativeMethods.CityShopsLoad(p, (ulong)file.Length, out raw),
                "mf_shopmenu2_load" => MiscNativeMethods.ShopMenu2Load(p, (ulong)file.Length, out raw),
                "mf_gsd_load" => MiscNativeMethods.GsdLoad(p, (ulong)file.Length, out raw),
                "mf_nhv_load" => MiscNativeMethods.NhvLoad(p, (ulong)file.Length, out raw),
                "mf_stbl_load" => MiscNativeMethods.StblLoad(p, (ulong)file.Length, out raw),
                "mf_tra_load" => MiscNativeMethods.TraLoad(p, (ulong)file.Length, out raw),
                "mf_city_areas_load" => MiscNativeMethods.CityAreasLoad(p, (ulong)file.Length, out raw),
                "mf_city_streammap_load" => MiscNativeMethods.StreamMapLoad(p, (ulong)file.Length, out raw),
                _ => throw new ArgumentOutOfRangeException(nameof(entryPoint), entryPoint, null),
            };
        }
        using var buffer = new MfBuffer(raw);
        ThrowOnError(status, entryPoint);
        using var stream = new MemoryStream(buffer.ToArray(), writable: false);
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        return readModel(reader);
    }

    internal static unsafe byte[] Save(Action<BinaryWriter> writeModel, string entryPoint)
    {
        using var wireStream = new MemoryStream();
        using (var writer = new BinaryWriter(wireStream, Encoding.UTF8, leaveOpen: true))
        {
            writeModel(writer);
        }
        byte[] wire = wireStream.ToArray();

        int status;
        MfRawBuffer raw;
        fixed (byte* p = wire)
        {
            status = entryPoint switch
            {
                "mf_ids_save" => MiscNativeMethods.IdsSave(p, (ulong)wire.Length, out raw),
                "mf_act_save" => MiscNativeMethods.ActSave(p, (ulong)wire.Length, out raw),
                "mf_nav_aiworld_save" => MiscNativeMethods.NavAiWorldSave(p, (ulong)wire.Length, out raw),
                "mf_nav_objdata_save" => MiscNativeMethods.NavObjDataSave(p, (ulong)wire.Length, out raw),
                "mf_speech_save" => MiscNativeMethods.SpeechSave(p, (ulong)wire.Length, out raw),
                "mf_anim2_save" => MiscNativeMethods.Anim2Save(p, (ulong)wire.Length, out raw),
                "mf_animtex_save" => MiscNativeMethods.AnimTexSave(p, (ulong)wire.Length, out raw),
                "mf_eds_save" => MiscNativeMethods.EdsSave(p, (ulong)wire.Length, out raw),
                "mf_cutscene_save" => MiscNativeMethods.CutsceneSave(p, (ulong)wire.Length, out raw),
                "mf_prefab_save" => MiscNativeMethods.PrefabSave(p, (ulong)wire.Length, out raw),
                "mf_entity_activator_save" => MiscNativeMethods.EntityActivatorSave(p, (ulong)wire.Length, out raw),
                "mf_tapindices_save" => MiscNativeMethods.TapIndicesSave(p, (ulong)wire.Length, out raw),
                "mf_soundsectors_save" => MiscNativeMethods.SoundSectorsSave(p, (ulong)wire.Length, out raw),
                "mf_fas_save" => MiscNativeMethods.FasSave(p, (ulong)wire.Length, out raw),
                "mf_fxa_save" => MiscNativeMethods.FxaSave(p, (ulong)wire.Length, out raw),
                "mf_atp_save" => MiscNativeMethods.AtpSave(p, (ulong)wire.Length, out raw),
                "mf_dat_save" => MiscNativeMethods.DatSave(p, (ulong)wire.Length, out raw),
                "mf_eff_save" => MiscNativeMethods.EffSave(p, (ulong)wire.Length, out raw),
                "mf_tyres_save" => MiscNativeMethods.TyresSave(p, (ulong)wire.Length, out raw),
                "mf_cityshops_save" => MiscNativeMethods.CityShopsSave(p, (ulong)wire.Length, out raw),
                "mf_shopmenu2_save" => MiscNativeMethods.ShopMenu2Save(p, (ulong)wire.Length, out raw),
                "mf_gsd_save" => MiscNativeMethods.GsdSave(p, (ulong)wire.Length, out raw),
                "mf_nhv_save" => MiscNativeMethods.NhvSave(p, (ulong)wire.Length, out raw),
                "mf_stbl_save" => MiscNativeMethods.StblSave(p, (ulong)wire.Length, out raw),
                _ => throw new ArgumentOutOfRangeException(nameof(entryPoint), entryPoint, null),
            };
        }
        using var buffer = new MfBuffer(raw);
        ThrowOnError(status, entryPoint);
        return buffer.ToArray();
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
