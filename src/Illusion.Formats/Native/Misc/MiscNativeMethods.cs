using System.Runtime.InteropServices;

namespace Illusion.Formats.Native.Misc;

/// <summary>The small-format import surface (P5): item descriptions, actor packs, the NAV
/// pair, the translokator and the city streaming tables. Kept 1:1 with <c>mf_abi.h</c>.</summary>
internal static partial class MiscNativeMethods
{
    [LibraryImport(NativeMethods.LibraryName, EntryPoint = "mf_ids_load")]
    internal static unsafe partial int IdsLoad(byte* file, ulong len, out MfRawBuffer modelWire);

    [LibraryImport(NativeMethods.LibraryName, EntryPoint = "mf_ids_save")]
    internal static unsafe partial int IdsSave(byte* modelWire, ulong len, out MfRawBuffer fileBytes);

    [LibraryImport(NativeMethods.LibraryName, EntryPoint = "mf_act_load")]
    internal static unsafe partial int ActLoad(byte* file, ulong len, out MfRawBuffer modelWire);

    [LibraryImport(NativeMethods.LibraryName, EntryPoint = "mf_act_save")]
    internal static unsafe partial int ActSave(byte* modelWire, ulong len, out MfRawBuffer fileBytes);

    [LibraryImport(NativeMethods.LibraryName, EntryPoint = "mf_nav_aiworld_load")]
    internal static unsafe partial int NavAiWorldLoad(byte* file, ulong len, out MfRawBuffer modelWire);

    [LibraryImport(NativeMethods.LibraryName, EntryPoint = "mf_nav_aiworld_save")]
    internal static unsafe partial int NavAiWorldSave(byte* modelWire, ulong len, out MfRawBuffer fileBytes);

    [LibraryImport(NativeMethods.LibraryName, EntryPoint = "mf_nav_objdata_load")]
    internal static unsafe partial int NavObjDataLoad(byte* file, ulong len, out MfRawBuffer modelWire);

    [LibraryImport(NativeMethods.LibraryName, EntryPoint = "mf_nav_objdata_save")]
    internal static unsafe partial int NavObjDataSave(byte* modelWire, ulong len, out MfRawBuffer fileBytes);

    [LibraryImport(NativeMethods.LibraryName, EntryPoint = "mf_speech_load")]
    internal static unsafe partial int SpeechLoad(byte* file, ulong len, out MfRawBuffer modelWire);

    [LibraryImport(NativeMethods.LibraryName, EntryPoint = "mf_speech_save")]
    internal static unsafe partial int SpeechSave(byte* modelWire, ulong len, out MfRawBuffer fileBytes);

    [LibraryImport(NativeMethods.LibraryName, EntryPoint = "mf_anim2_load")]
    internal static unsafe partial int Anim2Load(byte* file, ulong len, out MfRawBuffer modelWire);

    [LibraryImport(NativeMethods.LibraryName, EntryPoint = "mf_anim2_save")]
    internal static unsafe partial int Anim2Save(byte* modelWire, ulong len, out MfRawBuffer fileBytes);

    [LibraryImport(NativeMethods.LibraryName, EntryPoint = "mf_animtex_load")]
    internal static unsafe partial int AnimTexLoad(byte* file, ulong len, out MfRawBuffer modelWire);

    [LibraryImport(NativeMethods.LibraryName, EntryPoint = "mf_animtex_save")]
    internal static unsafe partial int AnimTexSave(byte* modelWire, ulong len, out MfRawBuffer fileBytes);

    [LibraryImport(NativeMethods.LibraryName, EntryPoint = "mf_eds_load")]
    internal static unsafe partial int EdsLoad(byte* file, ulong len, out MfRawBuffer modelWire);

    [LibraryImport(NativeMethods.LibraryName, EntryPoint = "mf_eds_save")]
    internal static unsafe partial int EdsSave(byte* modelWire, ulong len, out MfRawBuffer fileBytes);

    [LibraryImport(NativeMethods.LibraryName, EntryPoint = "mf_cutscene_load")]
    internal static unsafe partial int CutsceneLoad(byte* file, ulong len, out MfRawBuffer modelWire);

    [LibraryImport(NativeMethods.LibraryName, EntryPoint = "mf_cutscene_save")]
    internal static unsafe partial int CutsceneSave(byte* modelWire, ulong len, out MfRawBuffer fileBytes);

    [LibraryImport(NativeMethods.LibraryName, EntryPoint = "mf_prefab_load")]
    internal static unsafe partial int PrefabLoad(byte* file, ulong len, out MfRawBuffer modelWire);

    [LibraryImport(NativeMethods.LibraryName, EntryPoint = "mf_prefab_save")]
    internal static unsafe partial int PrefabSave(byte* modelWire, ulong len, out MfRawBuffer fileBytes);

    [LibraryImport(NativeMethods.LibraryName, EntryPoint = "mf_entity_activator_load")]
    internal static unsafe partial int EntityActivatorLoad(byte* file, ulong len, out MfRawBuffer modelWire);

    [LibraryImport(NativeMethods.LibraryName, EntryPoint = "mf_entity_activator_save")]
    internal static unsafe partial int EntityActivatorSave(byte* modelWire, ulong len, out MfRawBuffer fileBytes);

    [LibraryImport(NativeMethods.LibraryName, EntryPoint = "mf_tapindices_load")]
    internal static unsafe partial int TapIndicesLoad(byte* file, ulong len, out MfRawBuffer modelWire);

    [LibraryImport(NativeMethods.LibraryName, EntryPoint = "mf_tapindices_save")]
    internal static unsafe partial int TapIndicesSave(byte* modelWire, ulong len, out MfRawBuffer fileBytes);

    [LibraryImport(NativeMethods.LibraryName, EntryPoint = "mf_soundsectors_load")]
    internal static unsafe partial int SoundSectorsLoad(byte* file, ulong len, out MfRawBuffer modelWire);

    [LibraryImport(NativeMethods.LibraryName, EntryPoint = "mf_soundsectors_save")]
    internal static unsafe partial int SoundSectorsSave(byte* modelWire, ulong len, out MfRawBuffer fileBytes);

    [LibraryImport(NativeMethods.LibraryName, EntryPoint = "mf_fas_load")]
    internal static unsafe partial int FasLoad(byte* file, ulong len, out MfRawBuffer modelWire);

    [LibraryImport(NativeMethods.LibraryName, EntryPoint = "mf_fas_save")]
    internal static unsafe partial int FasSave(byte* modelWire, ulong len, out MfRawBuffer fileBytes);

    [LibraryImport(NativeMethods.LibraryName, EntryPoint = "mf_fxa_load")]
    internal static unsafe partial int FxaLoad(byte* file, ulong len, out MfRawBuffer modelWire);

    [LibraryImport(NativeMethods.LibraryName, EntryPoint = "mf_fxa_save")]
    internal static unsafe partial int FxaSave(byte* modelWire, ulong len, out MfRawBuffer fileBytes);

    [LibraryImport(NativeMethods.LibraryName, EntryPoint = "mf_atp_load")]
    internal static unsafe partial int AtpLoad(byte* file, ulong len, out MfRawBuffer modelWire);

    [LibraryImport(NativeMethods.LibraryName, EntryPoint = "mf_atp_save")]
    internal static unsafe partial int AtpSave(byte* modelWire, ulong len, out MfRawBuffer fileBytes);

    [LibraryImport(NativeMethods.LibraryName, EntryPoint = "mf_dat_load")]
    internal static unsafe partial int DatLoad(byte* file, ulong len, out MfRawBuffer modelWire);

    [LibraryImport(NativeMethods.LibraryName, EntryPoint = "mf_dat_save")]
    internal static unsafe partial int DatSave(byte* modelWire, ulong len, out MfRawBuffer fileBytes);

    [LibraryImport(NativeMethods.LibraryName, EntryPoint = "mf_eff_load")]
    internal static unsafe partial int EffLoad(byte* file, ulong len, out MfRawBuffer modelWire);

    [LibraryImport(NativeMethods.LibraryName, EntryPoint = "mf_eff_save")]
    internal static unsafe partial int EffSave(byte* modelWire, ulong len, out MfRawBuffer fileBytes);

    [LibraryImport(NativeMethods.LibraryName, EntryPoint = "mf_tyres_load")]
    internal static unsafe partial int TyresLoad(byte* file, ulong len, out MfRawBuffer modelWire);

    [LibraryImport(NativeMethods.LibraryName, EntryPoint = "mf_tyres_save")]
    internal static unsafe partial int TyresSave(byte* modelWire, ulong len, out MfRawBuffer fileBytes);

    [LibraryImport(NativeMethods.LibraryName, EntryPoint = "mf_cityshops_load")]
    internal static unsafe partial int CityShopsLoad(byte* file, ulong len, out MfRawBuffer modelWire);

    [LibraryImport(NativeMethods.LibraryName, EntryPoint = "mf_cityshops_save")]
    internal static unsafe partial int CityShopsSave(byte* modelWire, ulong len, out MfRawBuffer fileBytes);

    [LibraryImport(NativeMethods.LibraryName, EntryPoint = "mf_shopmenu2_load")]
    internal static unsafe partial int ShopMenu2Load(byte* file, ulong len, out MfRawBuffer modelWire);

    [LibraryImport(NativeMethods.LibraryName, EntryPoint = "mf_shopmenu2_save")]
    internal static unsafe partial int ShopMenu2Save(byte* modelWire, ulong len, out MfRawBuffer fileBytes);

    [LibraryImport(NativeMethods.LibraryName, EntryPoint = "mf_gsd_load")]
    internal static unsafe partial int GsdLoad(byte* file, ulong len, out MfRawBuffer modelWire);

    [LibraryImport(NativeMethods.LibraryName, EntryPoint = "mf_gsd_save")]
    internal static unsafe partial int GsdSave(byte* modelWire, ulong len, out MfRawBuffer fileBytes);

    [LibraryImport(NativeMethods.LibraryName, EntryPoint = "mf_nhv_load")]
    internal static unsafe partial int NhvLoad(byte* file, ulong len, out MfRawBuffer modelWire);

    [LibraryImport(NativeMethods.LibraryName, EntryPoint = "mf_nhv_save")]
    internal static unsafe partial int NhvSave(byte* modelWire, ulong len, out MfRawBuffer fileBytes);

    [LibraryImport(NativeMethods.LibraryName, EntryPoint = "mf_stbl_load")]
    internal static unsafe partial int StblLoad(byte* file, ulong len, out MfRawBuffer modelWire);

    [LibraryImport(NativeMethods.LibraryName, EntryPoint = "mf_stbl_save")]
    internal static unsafe partial int StblSave(byte* modelWire, ulong len, out MfRawBuffer fileBytes);

    [LibraryImport(NativeMethods.LibraryName, EntryPoint = "mf_tra_load")]
    internal static unsafe partial int TraLoad(byte* file, ulong len, out MfRawBuffer modelWire);

    [LibraryImport(NativeMethods.LibraryName, EntryPoint = "mf_tra_save")]
    internal static unsafe partial int TraSave(byte* modelWire, ulong len, out MfRawBuffer fileBytes);

    [LibraryImport(NativeMethods.LibraryName, EntryPoint = "mf_city_areas_load")]
    internal static unsafe partial int CityAreasLoad(byte* file, ulong len, out MfRawBuffer modelWire);

    [LibraryImport(NativeMethods.LibraryName, EntryPoint = "mf_city_streammap_load")]
    internal static unsafe partial int StreamMapLoad(byte* file, ulong len, out MfRawBuffer modelWire);
}
