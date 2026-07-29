using System.Numerics;
using Illusion.Formats.Actors;
using Illusion.Formats.CityAreas;
using Illusion.Formats.Navigation;
using Illusion.Formats.StreamMap;
using Illusion.Formats.Translokator;

namespace Illusion.Formats.Native.Misc;

/// <summary>Wire↔managed mapping for the actor pack, the NAV pair, the translokator and
/// the city streaming tables (the .ids mapper lives in <see cref="NativeItemDesc"/>).</summary>
internal static class NativeMiscFiles
{
    internal static ActorsFile ReadActors(ReadOnlySpan<byte> bytes)
    {
        Model.ActorsFileW wire = NativeMisc.Load(bytes, "mf_act_load", Model.ActorsFileW.ReadFrom);
        var file = new ActorsFile
        {
            StringBuffer = wire.StringBuffer,
            Binary = wire.Binary,
        };
        foreach (Model.ActSceneReferenceW reference in wire.SceneReferences)
        {
            file.SceneReferences.Add(new ActorSceneReference
            {
                FrameHash = reference.FrameHash,
                Unk0 = (ushort)reference.Unk0,
                NamePos = (ushort)reference.NamePos,
                FrameIndex = reference.FrameIndex,
                Name = reference.Name,
            });
        }
        for (int i = 0; i < wire.Binary.Items.Count; i++)
        {
            Model.ActorItemW item = wire.Binary.Items[i];
            file.ActorList.Add(new ActorEntry
            {
                Index = i,
                IsTyped = item.Typed != 0,
                TypeId = item.TypeId,
                TypeName = item.TypeName,
                EntityName = item.EntityName,
                Name1 = item.Name1,
                SceneSector = item.SceneSector,
                LinkedDefinition = item.LinkedDefinition,
                LinkedFrame = item.LinkedFrame,
                EntityHash = item.EntityHash,
                FrameHash = item.FrameHash,
                Position = item.Position,
                Rotation = new Quaternion(item.RotationX, item.RotationY, item.RotationZ, item.RotationW),
                Scale = item.Scale,
                Flags = item.Flags,
                InitPropId = item.InitPropId,
            });
        }
        return file;
    }

    internal static byte[] ActorsToBytes(ActorsFile file)
    {
        // The transform is the only editable part of an actor, and it writes back into the same wire
        // items the read produced — the untouched fields (and every offset) then re-emit as they were.
        foreach (ActorEntry actor in file.ActorList)
        {
            if (!actor.IsTyped || actor.Index < 0 || actor.Index >= file.Binary.Items.Count)
            {
                continue;
            }
            Model.ActorItemW item = file.Binary.Items[actor.Index];
            item.Position = actor.Position;
            item.RotationX = actor.Rotation.X;
            item.RotationY = actor.Rotation.Y;
            item.RotationZ = actor.Rotation.Z;
            item.RotationW = actor.Rotation.W;
            item.Scale = actor.Scale;
        }

        var wire = new Model.ActorsFileW
        {
            StringBuffer = file.StringBuffer,
            Binary = file.Binary,
        };
        foreach (ActorSceneReference reference in file.SceneReferences)
        {
            wire.SceneReferences.Add(new Model.ActSceneReferenceW
            {
                FrameHash = reference.FrameHash,
                Unk0 = reference.Unk0,
                NamePos = reference.NamePos,
                FrameIndex = reference.FrameIndex,
                Name = reference.Name,
            });
        }
        return NativeMisc.Save(wire.WriteTo, "mf_act_save");
    }

    internal static AiWorldFile ReadAiWorld(ReadOnlySpan<byte> bytes)
    {
        Model.NavAiWorldW wire = NativeMisc.Load(bytes, "mf_nav_aiworld_load", Model.NavAiWorldW.ReadFrom);
        return new AiWorldFile
        {
            WorldId = wire.WorldId,
            PathObjectCount = wire.PathObjectCount,
            PathObjects = wire.PathObjects,
            GenerationName = wire.GenerationName,
        };
    }

    internal static byte[] AiWorldToBytes(AiWorldFile file)
    {
        var wire = new Model.NavAiWorldW
        {
            WorldId = file.WorldId,
            PathObjectCount = file.PathObjectCount,
            PathObjects = file.PathObjects,
            GenerationName = file.GenerationName,
        };
        return NativeMisc.Save(wire.WriteTo, "mf_nav_aiworld_save");
    }

    internal static Speech.SpeechFile ReadSpeech(ReadOnlySpan<byte> bytes)
    {
        Model.SpeechFileW wire = NativeMisc.Load(bytes, "mf_speech_load", Model.SpeechFileW.ReadFrom);
        return new Speech.SpeechFile { Wire = wire };
    }

    internal static byte[] SpeechToBytes(Speech.SpeechFile file)
    {
        return NativeMisc.Save(file.Wire.WriteTo, "mf_speech_save");
    }

    internal static Animations.Animation2File ReadAnim2(ReadOnlySpan<byte> bytes)
    {
        Model.AnimFileW wire = NativeMisc.Load(bytes, "mf_anim2_load", Model.AnimFileW.ReadFrom);
        return new Animations.Animation2File { Wire = wire };
    }

    internal static byte[] Anim2ToBytes(Animations.Animation2File file)
    {
        return NativeMisc.Save(file.Wire.WriteTo, "mf_anim2_save");
    }

    internal static Textures.AnimatedTextureFile ReadAnimTex(ReadOnlySpan<byte> bytes)
    {
        Model.AnimTexFileW wire = NativeMisc.Load(bytes, "mf_animtex_load", Model.AnimTexFileW.ReadFrom);
        return new Textures.AnimatedTextureFile { Wire = wire };
    }

    internal static byte[] AnimTexToBytes(Textures.AnimatedTextureFile file)
    {
        return NativeMisc.Save(file.Wire.WriteTo, "mf_animtex_save");
    }

    internal static EntityData.EntityDataStorageFile ReadEds(ReadOnlySpan<byte> bytes)
    {
        Model.EdsFileW wire = NativeMisc.Load(bytes, "mf_eds_load", Model.EdsFileW.ReadFrom);
        return new EntityData.EntityDataStorageFile { Wire = wire };
    }

    internal static byte[] EdsToBytes(EntityData.EntityDataStorageFile file)
    {
        return NativeMisc.Save(file.Wire.WriteTo, "mf_eds_save");
    }

    internal static Cutscene.CutsceneFile ReadCutscene(ReadOnlySpan<byte> bytes)
    {
        Model.CutsceneFileW wire = NativeMisc.Load(bytes, "mf_cutscene_load", Model.CutsceneFileW.ReadFrom);
        return new Cutscene.CutsceneFile { Wire = wire };
    }

    internal static byte[] CutsceneToBytes(Cutscene.CutsceneFile file)
    {
        return NativeMisc.Save(file.Wire.WriteTo, "mf_cutscene_save");
    }

    internal static Prefab.PrefabFile ReadPrefab(ReadOnlySpan<byte> bytes)
    {
        Model.PrefabFileW wire = NativeMisc.Load(bytes, "mf_prefab_load", Model.PrefabFileW.ReadFrom);
        return new Prefab.PrefabFile { Wire = wire };
    }

    internal static byte[] PrefabToBytes(Prefab.PrefabFile file)
    {
        return NativeMisc.Save(file.Wire.WriteTo, "mf_prefab_save");
    }

    internal static EntityActivator.EntityActivatorFile ReadEntityActivator(ReadOnlySpan<byte> bytes)
    {
        Model.EntityActivatorW wire = NativeMisc.Load(bytes, "mf_entity_activator_load", Model.EntityActivatorW.ReadFrom);
        return new EntityActivator.EntityActivatorFile { Wire = wire };
    }

    internal static byte[] EntityActivatorToBytes(EntityActivator.EntityActivatorFile file)
    {
        return NativeMisc.Save(file.Wire.WriteTo, "mf_entity_activator_save");
    }

    internal static Navigation.TapIndicesFile ReadTapIndices(ReadOnlySpan<byte> bytes)
    {
        Model.TapIndicesW wire = NativeMisc.Load(bytes, "mf_tapindices_load", Model.TapIndicesW.ReadFrom);
        return new Navigation.TapIndicesFile { Wire = wire };
    }

    internal static byte[] TapIndicesToBytes(Navigation.TapIndicesFile file)
    {
        return NativeMisc.Save(file.Wire.WriteTo, "mf_tapindices_save");
    }

    internal static Sound.SoundSectorFile ReadSoundSectors(ReadOnlySpan<byte> bytes)
    {
        Model.SoundSectorFileW wire = NativeMisc.Load(bytes, "mf_soundsectors_load", Model.SoundSectorFileW.ReadFrom);
        return new Sound.SoundSectorFile { Wire = wire };
    }

    internal static byte[] SoundSectorsToBytes(Sound.SoundSectorFile file)
    {
        return NativeMisc.Save(file.Wire.WriteTo, "mf_soundsectors_save");
    }

    internal static FaceFx.FaceFxAnimSetFile ReadFaceFxAnimSet(ReadOnlySpan<byte> bytes)
    {
        Model.FxContainerW wire = NativeMisc.Load(bytes, "mf_fas_load", Model.FxContainerW.ReadFrom);
        return new FaceFx.FaceFxAnimSetFile { Wire = wire };
    }

    internal static byte[] FaceFxAnimSetToBytes(FaceFx.FaceFxAnimSetFile file)
    {
        return NativeMisc.Save(file.Wire.WriteTo, "mf_fas_save");
    }

    internal static FaceFx.FaceFxActorFile ReadFaceFxActor(ReadOnlySpan<byte> bytes)
    {
        Model.FxContainerW wire = NativeMisc.Load(bytes, "mf_fxa_load", Model.FxContainerW.ReadFrom);
        return new FaceFx.FaceFxActorFile { Wire = wire };
    }

    internal static byte[] FaceFxActorToBytes(FaceFx.FaceFxActorFile file)
    {
        return NativeMisc.Save(file.Wire.WriteTo, "mf_fxa_save");
    }

    internal static Navigation.AnimalTrafficPathsFile ReadAnimalTrafficPaths(ReadOnlySpan<byte> bytes)
    {
        Model.AtpFileW wire = NativeMisc.Load(bytes, "mf_atp_load", Model.AtpFileW.ReadFrom);
        return new Navigation.AnimalTrafficPathsFile { Wire = wire };
    }

    internal static byte[] AnimalTrafficPathsToBytes(Navigation.AnimalTrafficPathsFile file)
    {
        return NativeMisc.Save(file.Wire.WriteTo, "mf_atp_save");
    }

    internal static Text.TextDatabaseFile ReadTextDatabase(ReadOnlySpan<byte> bytes)
    {
        Model.DatFileW wire = NativeMisc.Load(bytes, "mf_dat_load", Model.DatFileW.ReadFrom);
        return new Text.TextDatabaseFile { Wire = wire };
    }

    internal static byte[] TextDatabaseToBytes(Text.TextDatabaseFile file)
    {
        return NativeMisc.Save(file.Wire.WriteTo, "mf_dat_save");
    }

    internal static Effects.EffectsFile ReadEffects(ReadOnlySpan<byte> bytes)
    {
        Model.EffFileW wire = NativeMisc.Load(bytes, "mf_eff_load", Model.EffFileW.ReadFrom);
        return new Effects.EffectsFile { Wire = wire };
    }

    internal static byte[] EffectsToBytes(Effects.EffectsFile file)
    {
        return NativeMisc.Save(file.Wire.WriteTo, "mf_eff_save");
    }

    internal static Tyres.TyresFile ReadTyres(ReadOnlySpan<byte> bytes)
    {
        Model.TyresFileW wire = NativeMisc.Load(bytes, "mf_tyres_load", Model.TyresFileW.ReadFrom);
        return new Tyres.TyresFile { Wire = wire };
    }

    internal static byte[] TyresToBytes(Tyres.TyresFile file)
    {
        return NativeMisc.Save(file.Wire.WriteTo, "mf_tyres_save");
    }

    internal static City.CityShopsFile ReadCityShops(ReadOnlySpan<byte> bytes)
    {
        Model.CityShopsFileW wire = NativeMisc.Load(bytes, "mf_cityshops_load", Model.CityShopsFileW.ReadFrom);
        return new City.CityShopsFile { Wire = wire };
    }

    internal static byte[] CityShopsToBytes(City.CityShopsFile file)
    {
        return NativeMisc.Save(file.Wire.WriteTo, "mf_cityshops_save");
    }

    internal static City.ShopMenu2File ReadShopMenu2(ReadOnlySpan<byte> bytes)
    {
        Model.ShopMenu2FileW wire = NativeMisc.Load(bytes, "mf_shopmenu2_load", Model.ShopMenu2FileW.ReadFrom);
        return new City.ShopMenu2File { Wire = wire };
    }

    internal static byte[] ShopMenu2ToBytes(City.ShopMenu2File file)
    {
        return NativeMisc.Save(file.Wire.WriteTo, "mf_shopmenu2_save");
    }

    internal static Navigation.RoadmapFile ReadRoadmap(ReadOnlySpan<byte> bytes)
    {
        Model.GsdFileW wire = NativeMisc.Load(bytes, "mf_gsd_load", Model.GsdFileW.ReadFrom);
        return new Navigation.RoadmapFile { Wire = wire };
    }

    internal static byte[] RoadmapToBytes(Navigation.RoadmapFile file)
    {
        return NativeMisc.Save(file.Wire.WriteTo, "mf_gsd_save");
    }

    internal static Navigation.NavHpdFile ReadNavHpd(ReadOnlySpan<byte> bytes)
    {
        Model.NhvFileW wire = NativeMisc.Load(bytes, "mf_nhv_load", Model.NhvFileW.ReadFrom);
        return new Navigation.NavHpdFile { Wire = wire };
    }

    internal static byte[] NavHpdToBytes(Navigation.NavHpdFile file)
    {
        return NativeMisc.Save(file.Wire.WriteTo, "mf_nhv_save");
    }

    internal static Sound.SoundTableFile ReadSoundTable(ReadOnlySpan<byte> bytes)
    {
        Model.StblFileW wire = NativeMisc.Load(bytes, "mf_stbl_load", Model.StblFileW.ReadFrom);
        return new Sound.SoundTableFile { Wire = wire };
    }

    internal static byte[] SoundTableToBytes(Sound.SoundTableFile file)
    {
        return NativeMisc.Save(file.Wire.WriteTo, "mf_stbl_save");
    }

    internal static ObjDataFile ReadObjData(ReadOnlySpan<byte> bytes)
    {
        Model.NavObjDataW wire = NativeMisc.Load(bytes, "mf_nav_objdata_load", Model.NavObjDataW.ReadFrom);
        return new ObjDataFile
        {
            Name = wire.Name,
            GenerationName = wire.GenerationName,
            GraphVersion = wire.GraphVersion,
            GraphId = wire.GraphId,
            GraphTag0 = wire.GraphTag0,
            GraphTag1 = wire.GraphTag1,
            GraphVertices = wire.GraphVertices,
            GraphEdges = wire.GraphEdges,
            Aimesh = wire.Aimesh,
        };
    }

    internal static byte[] ObjDataToBytes(ObjDataFile file)
    {
        var wire = new Model.NavObjDataW
        {
            Name = file.Name,
            GenerationName = file.GenerationName,
            GraphVersion = file.GraphVersion,
            GraphId = file.GraphId,
            GraphTag0 = file.GraphTag0,
            GraphTag1 = file.GraphTag1,
            GraphVertices = file.GraphVertices,
            GraphEdges = file.GraphEdges,
            Aimesh = file.Aimesh,
        };
        return NativeMisc.Save(wire.WriteTo, "mf_nav_objdata_save");
    }

    internal static void ReadTranslokator(TranslokatorLoader loader, ReadOnlySpan<byte> bytes)
    {
        Model.TranslokatorW wire = NativeMisc.Load(bytes, "mf_tra_load", Model.TranslokatorW.ReadFrom);
        loader.Version = wire.Version;
        loader.Unk1 = wire.Unk1;
        loader.Unk2 = (short)wire.Unk2;
        loader.Bounds = new Mathematics.BoundingBox { Min = wire.BoundsMin, Max = wire.BoundsMax };

        loader.Grids = new Grid[wire.Grids.Count];
        for (int i = 0; i < wire.Grids.Count; i++)
        {
            Model.TraGridW gridWire = wire.Grids[i];
            loader.Grids[i] = new Grid
            {
                Key = (short)gridWire.Key,
                Origin = gridWire.Origin,
                CellSize = new System.Numerics.Vector2(gridWire.CellSizeX, gridWire.CellSizeY),
                Width = gridWire.Width,
                Height = gridWire.Height,
                Data = [.. gridWire.Data],
            };
        }

        loader.ObjectGroups = new ObjectGroup[wire.Groups.Count];
        for (int i = 0; i < wire.Groups.Count; i++)
        {
            Model.TraGroupW groupWire = wire.Groups[i];
            var group = new ObjectGroup
            {
                ActorType = (ActorTypes)groupWire.ActorType,
                Unk01 = (short)groupWire.Unk01,
                Objects = new Translokator.Object[groupWire.Objects.Count],
            };
            for (int x = 0; x < groupWire.Objects.Count; x++)
            {
                Model.TraObjectW objectWire = groupWire.Objects[x];
                var obj = new Translokator.Object
                {
                    Unk02 = (short)objectWire.Unk02,
                    UnkBytes1 = objectWire.UnkBytes1,
                    GridMax = objectWire.GridMax,
                    GridMin = objectWire.GridMin,
                    Instances = new List<Instance>(objectWire.Instances.Count),
                };
                obj.Name.String = objectWire.Name;
                obj.Name.Hash = objectWire.Hash;
                foreach (Model.TraInstanceW wireInstance in objectWire.Instances)
                {
                    obj.Instances.Add(new Instance
                    {
                        W0 = (ushort)wireInstance.W0,
                        W1 = (ushort)wireInstance.W1,
                        W2 = (ushort)wireInstance.W2,
                        D5 = wireInstance.D5,
                        ID = (ushort)wireInstance.Id,
                        D4 = (ushort)wireInstance.D4,
                        Scale = wireInstance.Scale,
                        Rotation = wireInstance.Rotation,
                        Position = wireInstance.Position,
                    });
                }
                group.Objects[x] = obj;
            }
            loader.ObjectGroups[i] = group;
        }
    }

    internal static byte[] TranslokatorToBytes(TranslokatorLoader loader)
    {
        var wire = new Model.TranslokatorW
        {
            Version = loader.Version,
            Unk1 = loader.Unk1,
            Unk2 = loader.Unk2,
            BoundsMin = loader.Bounds.Min,
            BoundsMax = loader.Bounds.Max,
        };

        foreach (Grid grid in loader.Grids)
        {
            var gridWire = new Model.TraGridW
            {
                Key = grid.Key,
                Origin = grid.Origin,
                CellSizeX = grid.CellSize.X,
                CellSizeY = grid.CellSize.Y,
                Width = grid.Width,
                Height = grid.Height,
            };
            gridWire.Data.AddRange(grid.Data);
            wire.Grids.Add(gridWire);
        }

        foreach (ObjectGroup group in loader.ObjectGroups)
        {
            var groupWire = new Model.TraGroupW
            {
                ActorType = (int)group.ActorType,
                Unk01 = group.Unk01,
            };
            foreach (Translokator.Object obj in group.Objects)
            {
                var objectWire = new Model.TraObjectW
                {
                    Unk02 = obj.Unk02,
                    Hash = obj.Name.Hash,
                    Name = obj.Name.String,
                    UnkBytes1 = obj.UnkBytes1,
                    GridMax = obj.GridMax,
                    GridMin = obj.GridMin,
                };
                foreach (Instance instance in obj.Instances)
                {
                    // Position/Rotation/Scale are what the core re-quantizes; the packed
                    // words ride along unread on this path (they are the read half's
                    // convenience), so an edited transform reaches the file as edited.
                    objectWire.Instances.Add(new Model.TraInstanceW
                    {
                        Position = instance.Position,
                        Rotation = instance.Rotation,
                        Scale = instance.Scale,
                        Id = instance.ID,
                        W0 = instance.W0,
                        W1 = instance.W1,
                        W2 = instance.W2,
                        D4 = instance.D4,
                        D5 = instance.D5,
                    });
                }
                groupWire.Objects.Add(objectWire);
            }
            wire.Groups.Add(groupWire);
        }

        return NativeMisc.Save(wire.WriteTo, "mf_tra_save");
    }

    internal static IReadOnlyList<CityAreaEntry> ReadCityAreas(ReadOnlySpan<byte> bytes)
    {
        Model.CityAreasW wire = NativeMisc.Load(bytes, "mf_city_areas_load", Model.CityAreasW.ReadFrom);
        var areas = new List<CityAreaEntry>(wire.Areas.Count);
        foreach (Model.CityAreaW area in wire.Areas)
        {
            areas.Add(new CityAreaEntry
            {
                Name = area.Name,
                Target1 = area.HasTarget1 != 0 ? area.Target1 : null,
                Target2 = area.HasTarget2 != 0 ? area.Target2 : null,
            });
        }
        return areas;
    }

    internal static (string[] Headers, StreamMapLine[] Lines, StreamMapLoader[] Loaders)
        ReadStreamMap(ReadOnlySpan<byte> bytes)
    {
        Model.StreamMapW wire = NativeMisc.Load(bytes, "mf_city_streammap_load", Model.StreamMapW.ReadFrom);
        var lines = new StreamMapLine[wire.Lines.Count];
        for (int i = 0; i < lines.Length; i++)
        {
            lines[i] = new StreamMapLine
            {
                Name = wire.Lines[i].Name,
                LineID = wire.Lines[i].LineId,
                GroupID = wire.Lines[i].GroupId,
            };
        }
        var loaders = new StreamMapLoader[wire.Loaders.Count];
        for (int i = 0; i < loaders.Length; i++)
        {
            loaders[i] = new StreamMapLoader
            {
                Start = wire.Loaders[i].Start,
                End = wire.Loaders[i].End,
                Type = (StreamGroupType)wire.Loaders[i].GroupType,
                Path = wire.Loaders[i].Path,
                Entity = wire.Loaders[i].Entity,
            };
        }
        return ([.. wire.GroupHeaders], lines, loaders);
    }
}
