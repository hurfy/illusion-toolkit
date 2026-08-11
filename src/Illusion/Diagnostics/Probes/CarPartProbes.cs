using System.IO;
using System.Numerics;
using System.Text;
using Illusion.Assets;
using Illusion.Assets.Cars;
using Illusion.Formats.Native.Model;
using Illusion.Formats.Prefab;
using static Illusion.Diagnostics.Probes.ProbeAssert;

namespace Illusion.Diagnostics.Probes;

/// <summary>
/// Giving a bare component a deform part, and taking that part away again — measured over the corpus first,
/// because a part minted from nothing has to carry twenty-odd fields the toolkit does not interpret, and the
/// only honest source for what to put in them is what the 1698 shipped ones hold.
///
/// <para>
/// The removal half is the sharper question. A part is addressed by its POSITION in the list, and three other
/// structures index it — the parent link's second copy, the deformation block's own hash→index table, and the
/// joints' break energies — so taking one out of the middle renumbers references the toolkit does not own.
/// The census asks how far that reaches before the writer is allowed anywhere near it.
/// </para>
/// <para>Output: %TEMP%\illusion_car_parts.txt</para>
/// </summary>
internal static class CarPartProbes
{
    private static readonly string Scratch = Path.Combine(Path.GetTempPath(), "illusion_car_parts");

    internal static void RunCarPartsProbe(string focus)
    {
        string outFile = Path.Combine(Path.GetTempPath(), "illusion_car_parts.txt");
        var sb = new StringBuilder();
        int pass = 0, fail = 0;
        void Check(string name, bool ok, string detail = "")
        {
            if (ok) pass++; else fail++;
            sb.AppendLine($"[{(ok ? "PASS" : "FAIL")}] {name}{(detail == "" ? "" : " — " + detail)}");
        }

        try
        {
            if (!InitEnv(out string? err)) { sb.AppendLine("INIT FAIL: " + err); return; }
            string folder = Path.Combine(MafiaEnvironment.PcFolder, "sds", "cars");
            Corpus(sb, folder, Check);
            string? mirror = Mirror(focus, folder, sb, Check);
            if (mirror != null)
            {
                // The prefab as the car ships, kept before anything is written to the mirror: it is what
                // says a grant followed by a demotion left the car exactly as it was.
                byte[]? pristine = Car.ReadFrom(mirror)?.PrefabPath is { } path
                    ? File.ReadAllBytes(path)
                    : null;
                Grant(sb, mirror, Check);
                Demote(sb, mirror, pristine, Check);
                // Its own copy of the car, because it saves a collision, a mass and a marker into it — and
                // the round trip above is measured against a mirror nothing else has written to.
                if (Mirror(focus, folder, sb, Check, "-behaves") is { } second) Behaves(sb, second, Check);
                Undo(sb, mirror, Check);
                Refusals(sb, mirror, Check);
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine("EXCEPTION: " + ex);
        }
        finally
        {
            sb.Insert(0, $"BARE COMPONENTS: GRANT AND REMOVE A DEFORM PART ({focus}): "
                + $"{pass} passed, {fail} failed\n\n");
            File.WriteAllText(outFile, sb.ToString());
        }
    }

    // ── what a shipped part actually holds ──

    private static void Corpus(StringBuilder sb, string folder, Action<string, bool, string> check)
    {
        sb.AppendLine("════ what the shipped deform parts carry ════");

        int cars = 0, parts = 0;
        var unk2 = new Dictionary<byte, int>();
        var unk3Count = new Dictionary<int, int>();
        var unk18 = new Dictionary<byte, int>();
        var unk23 = new Dictionary<uint, int>();
        var unk24 = new Dictionary<uint, int>();
        int unk4Zero = 0, unk5Zero = 0, unk6Zero = 0;
        var unk4 = new List<float>();
        var unk5 = new List<float>();
        var unk6 = new List<float>();
        int impulses = 0, drops = 0, drains = 0, unk14 = 0, unk20 = 0, unk21 = 0, unk22 = 0;
        int volumeCollections = 0, commons = 0, comZeroTail = 0;
        int identityTransform = 0, parentIndexSet = 0, comEffects = 0;
        var comUnk2 = new Dictionary<int, int>();
        var comUnk3 = new Dictionary<int, int>();
        var comUnk4 = new Dictionary<int, int>();
        var comUnk5 = new Dictionary<int, int>();
        var comUnk6 = new Dictionary<int, int>();
        var comUnk7 = new Dictionary<int, int>();
        var comUnk8 = new Dictionary<int, int>();

        // What indexes a part, and therefore what a removal would have to renumber.
        int pairs = 0, pairsResolvingToPartBone = 0, pairsIndexingItsOwnPart = 0, pairsOutOfRange = 0;
        int carsWherePairsCoverParts = 0, carsWithPairs = 0;
        int owners = 0, ownerU16 = 0, ownerPartTransforms = 0, ownerTransformsMatchParts = 0;
        int joints = 0, breakEnergies = 0, breakEnergyInRange = 0;
        int carsPairsMatchJoints = 0, carsPairsMatchOwners = 0, pairIndexesAJoint = 0;
        int ownerHashIsPartBone = 0, ownerU16InParts = 0, ownerMatrixNamesPartBone = 0;
        int jointU16InParts = 0, jointU16Fields = 0;
        int unk14Values = 0, unk14InParts = 0, unk14InJoints = 0, unk14InOwners = 0;
        int unk20Values = 0, unk20InParts = 0, unk20InJoints = 0;
        int drainRows = 0, drainInParts = 0;
        int childInParentList = 0, childrenClaimed = 0, unk20NamesAChild = 0;
        int emptyParts = 0, volumeCollectionCounts = 0;
        var unk2Kinds = new Dictionary<string, int>();
        var comUnk2Values = new Dictionary<int, int>();
        var unk5Values = new Dictionary<float, int>();
        var unk6Values = new Dictionary<float, int>();
        var effectCounts = new Dictionary<int, int>();
        var volumeCollections2 = new Dictionary<int, int>();
        var volumesPerPart = new Dictionary<int, int>();
        var volumelessKinds = new Dictionary<string, int>();
        // Per KIND, because the numbers that vary vary with it: a window is written 60 where a bumper is 200.
        var byKind = new Dictionary<string, Dictionary<string, int>>();
        // And the whole template at once, so what a minted part carries is a COMBINATION shipped cars have
        // rather than a per-field mode that nothing in the corpus is actually written as.
        var templates = new Dictionary<string, Dictionary<string, int>>();
        var nested = new Dictionary<string, int>();
        var effectRows = new Dictionary<string, int>();
        int comZeroCentre = 0;
        int transformIdentity = 0, transformOnRest = 0, transformOnWorld = 0, transformElsewhere = 0;

        foreach (FileInfo sds in new DirectoryInfo(folder).GetFiles("*.sds").OrderBy(f => f.Name))
        {
            string extracted = MafiaEnvironment.ExtractedDir(sds);
            if (!File.Exists(Path.Combine(extracted, "SDSContent.xml"))) continue;

            Car? read;
            try { read = Car.ReadFrom(extracted); }
            catch (Exception) { continue; }
            if (read == null) continue;
            PrefabDeformationInitW? deformation = Deformation(read.Prefab);
            if (deformation == null) continue;
            Dictionary<ulong, (Vector3 Pose, Vector3 World)> bones = BonePlaces(read);
            cars++;

            List<PrefabDeformPartW> list = deformation.DeformParts;
            var boneOfPart = new Dictionary<ulong, int>();
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i].Unk3.Count > 0) boneOfPart[list[i].Unk3[0]] = i;
            }

            for (int at = 0; at < list.Count; at++)
            {
                PrefabDeformPartW part = list[at];
                parts++;
                unk2[part.Unk2] = unk2.GetValueOrDefault(part.Unk2) + 1;
                unk3Count[part.Unk3.Count] = unk3Count.GetValueOrDefault(part.Unk3.Count) + 1;
                unk18[part.Unk18] = unk18.GetValueOrDefault(part.Unk18) + 1;
                unk23[part.Unk23] = unk23.GetValueOrDefault(part.Unk23) + 1;
                unk24[part.Unk24] = unk24.GetValueOrDefault(part.Unk24) + 1;
                if (part.Unk4 == 0f) unk4Zero++; else unk4.Add(part.Unk4);
                if (part.Unk5 == 0f) unk5Zero++; else unk5.Add(part.Unk5);
                if (part.Unk6 == 0f) unk6Zero++; else unk6.Add(part.Unk6);
                if (part.InternalImpulses.Count > 0) impulses++;
                if (part.DropParts.Count > 0) drops++;
                if (part.DrainEnergy.Count > 0) drains++;
                if (part.Unk14.Count > 0) unk14++;
                if (part.Unk20.Count > 0) unk20++;
                if (part.Unk21Data.Count > 0) unk21++;
                if (part.Unk22RelData.Count > 0) unk22++;
                if (part.CollisionVolumes.Count > 0) volumeCollections++;
                if (part.Unk17 != 65535) parentIndexSet++;
                if (IsIdentity(part.PartTransform)) identityTransform++;
                if (part.Unk2 != 0) Bump(unk2Kinds, PartKind(part.PartType));
                Bump(unk5Values, part.Unk5);
                Bump(unk6Values, part.Unk6);
                if (part.CentreOfMass == Vector3.Zero) comZeroCentre++;
                // What the part's own transform is relative to has never been measured, and a minted part
                // has to put SOMETHING there. The two candidates are the bone's rest pose and its world
                // place; the answer decides whether identity is neutral or is "at the body's origin".
                Vector3 translation = part.PartTransform.Translation;
                if (IsIdentity(part.PartTransform)) transformIdentity++;
                else if (part.Unk3.Count > 0 && bones.TryGetValue(part.Unk3[0], out var place))
                {
                    if (Approx(translation, place.Pose)) transformOnRest++;
                    else if (Approx(translation, place.World)) transformOnWorld++;
                    else transformElsewhere++;
                }
                else transformElsewhere++;
                Dictionary<string, int> kind = byKind.TryGetValue(PartKind(part.PartType), out var had)
                    ? had
                    : byKind[PartKind(part.PartType)] = [];
                Bump(kind, $"unk5={part.Unk5:F0}");
                Bump(kind, $"flags=0x{part.Flags:X8}");
                if (part.Common.Count > 0)
                {
                    PrefabDeformPartCommonW block = part.Common[0];
                    Bump(kind, $"speed={block.SpeedMin:F0}…{block.SpeedMax:F0}");
                    Bump(kind, $"resist={block.Resistance:F0}");
                    Bump(kind, $"mass={block.Mass:F0}");
                    Bump(kind, $"energy={block.EnergyStart:F0}/{block.EnergyDrop:F2}");
                    Dictionary<string, int> template =
                        templates.TryGetValue(PartKind(part.PartType), out var seen)
                            ? seen
                            : templates[PartKind(part.PartType)] = [];
                    Bump(template, $"unk5/6 {R(part.Unk5)}/{R(part.Unk6)}  flags 0x{part.Flags:X8}  "
                        + $"speed {R(block.SpeedMin)}…{R(block.SpeedMax)}  resist {R(block.Resistance)}  "
                        + $"mass {R(block.Mass)}  energy {R(block.EnergyStart)}/{R(block.EnergyDrop)}  "
                        + $"unk7 {block.Unk7}");
                }
                Bump(volumeCollections2, part.CollisionVolumes.Count);
                volumeCollectionCounts += part.CollisionVolumes.Sum(c => c.Volumes.Count);
                // Whether a part with NO volume is a shape the corpus has: a minted one starts with none,
                // and a modder adds its collision afterwards.
                Bump(volumesPerPart, part.CollisionVolumes.Sum(c => c.Volumes.Count));
                if (part.CollisionVolumes.Sum(c => c.Volumes.Count) == 0) Bump(volumelessKinds, PartKind(part.PartType));
                if (part.SmDeformBones.Count == 0 && part.DrainEnergy.Count == 0 && part.Unk14.Count == 0
                    && part.Unk20.Count == 0 && part.CollisionVolumes.Sum(c => c.Volumes.Count) == 0)
                {
                    emptyParts++;
                }
                foreach (ushort value in part.Unk14)
                {
                    unk14Values++;
                    if (value < list.Count) unk14InParts++;
                    if (value < deformation.Joints.Count) unk14InJoints++;
                    if (value < deformation.OwnerDeforms.Count) unk14InOwners++;
                }
                // Whether unk20 is the THIRD copy of the parent link — the children, by index. 934 values
                // against 934 parts carrying a parent index is what made the question worth asking.
                foreach (ushort value in part.Unk20)
                {
                    unk20Values++;
                    if (value < list.Count) unk20InParts++;
                    if (value < deformation.Joints.Count) unk20InJoints++;
                    if (value < list.Count && list[value].Unk17 == at) unk20NamesAChild++;
                }
                if (part.Unk17 != 65535 && part.Unk17 < list.Count)
                {
                    childrenClaimed++;
                    if (list[part.Unk17].Unk20.Contains((ushort)at)) childInParentList++;
                }
                foreach (PrefabDrainEnergyW drain in part.DrainEnergy)
                {
                    drainRows++;
                    if (drain.DrainPart < list.Count) drainInParts++;
                }

                if (part.Common.Count == 0) continue;
                commons++;
                PrefabDeformPartCommonW common = part.Common[0];
                comUnk2[common.Unk2.Count] = comUnk2.GetValueOrDefault(common.Unk2.Count) + 1;
                comUnk3[common.Unk3Transform.Count] = comUnk3.GetValueOrDefault(common.Unk3Transform.Count) + 1;
                comUnk4[common.Unk4.Count] = comUnk4.GetValueOrDefault(common.Unk4.Count) + 1;
                comUnk5[common.Unk5Data.Count] = comUnk5.GetValueOrDefault(common.Unk5Data.Count) + 1;
                comUnk6[common.Unk6Value.Count] = comUnk6.GetValueOrDefault(common.Unk6Value.Count) + 1;
                comUnk7[common.Unk7] = comUnk7.GetValueOrDefault(common.Unk7) + 1;
                comUnk8[common.Unk8.Count] = comUnk8.GetValueOrDefault(common.Unk8.Count) + 1;
                foreach (int value in common.Unk2) Bump(comUnk2Values, value);
                if (common.PartEffects.Count > 0) comEffects++;
                Bump(effectCounts, common.PartEffects.Count);
                foreach (PrefabCollVolumeNestedW row in common.Unk5Data)
                {
                    Bump(nested, $"{row.Unk0:F2}/{row.Unk1:F2}/{row.Unk2:F2}/{row.Unk3}/{row.Unk4}");
                }
                foreach (PrefabDeformPartEffectsW row in common.PartEffects)
                {
                    Bump(effectRows, $"break {row.ParticleBreakId}, hinge {row.ParticleHingeVersionId}, "
                        + $"snow {row.SnowParticleId0}/{row.SnowParticleId1}/{row.SnowParticleId2}/"
                        + $"{row.SnowParticleId3}, scale {row.ParticleScale:F2}, packs {row.Packs.Count}");
                }
                if (common.Unk2.Count == 0 && common.Unk3Transform.Count == 0 && common.Unk4.Count == 0
                    && common.Unk5Data.Count == 0 && common.Unk6Value.Count == 0 && common.Unk8.Count == 0)
                {
                    comZeroTail++;
                }
            }

            // ── the hash→index table beside the parts ──
            if (deformation.Unk1Pairs.Count > 0) carsWithPairs++;
            if (deformation.Unk1Pairs.Count == deformation.Joints.Count) carsPairsMatchJoints++;
            if (deformation.Unk1Pairs.Count == deformation.OwnerDeforms.Count) carsPairsMatchOwners++;
            bool covers = deformation.Unk1Pairs.Count == list.Count;
            foreach (PrefabHashIndexW pair in deformation.Unk1Pairs)
            {
                pairs++;
                if (pair.Index < deformation.Joints.Count) pairIndexesAJoint++;
                if (boneOfPart.ContainsKey(pair.Hash)) pairsResolvingToPartBone++;
                if (pair.Index >= list.Count) { pairsOutOfRange++; covers = false; continue; }
                bool ownPart = list[pair.Index].Unk3.Count > 0 && list[pair.Index].Unk3[0] == pair.Hash;
                if (ownPart) pairsIndexingItsOwnPart++; else covers = false;
            }
            if (covers && deformation.Unk1Pairs.Count > 0) carsWherePairsCoverParts++;

            foreach (PrefabOwnerDeformW owner in deformation.OwnerDeforms)
            {
                owners++;
                ownerU16 += owner.Unk4.Count + owner.Unk6.Count;
                ownerPartTransforms += owner.PartTransforms.Count;
                if (owner.PartTransforms.Count == list.Count) ownerTransformsMatchParts++;
                if (boneOfPart.ContainsKey(owner.Unk0)) ownerHashIsPartBone++;
                if (boneOfPart.ContainsKey(owner.Unk1)) ownerHashIsPartBone++;
                foreach (ushort value in owner.Unk4.Concat(owner.Unk6))
                {
                    if (value < list.Count) ownerU16InParts++;
                }
                foreach (PrefabPartMatrixW matrix in owner.PartTransforms)
                {
                    if (boneOfPart.ContainsKey(matrix.PartHashName)) ownerMatrixNamesPartBone++;
                }
            }

            foreach (PrefabJointW joint in deformation.Joints)
            {
                joints++;
                foreach (ushort value in new[] { joint.Unk0, joint.Unk1, joint.Unk2, joint.Unk3 })
                {
                    jointU16Fields++;
                    if (value < list.Count) jointU16InParts++;
                }
                foreach (PrefabPartBreakEnergyW energy in joint.PartBreakEnergy)
                {
                    breakEnergies++;
                    if (energy.PartId < list.Count) breakEnergyInRange++;
                }
            }
        }

        sb.AppendLine($"    cars {cars}, deform parts {parts}");
        sb.AppendLine($"    unk2 {Spread(unk2)}");
        sb.AppendLine($"    unk3 (own bone) count {Spread(unk3Count)}");
        sb.AppendLine($"    unk4 zero on {unk4Zero}, otherwise {Range(unk4)}");
        sb.AppendLine($"    unk5 zero on {unk5Zero}, otherwise {Range(unk5)}");
        sb.AppendLine($"    unk6 zero on {unk6Zero}, otherwise {Range(unk6)}");
        sb.AppendLine($"    unk18 {Spread(unk18)}");
        sb.AppendLine($"    unk23 {Spread(unk23)}");
        sb.AppendLine($"    unk24 {Spread(unk24)}");
        sb.AppendLine($"    non-empty lists: impulses {impulses}, drops {drops}, drains {drains}, "
            + $"unk14 {unk14}, unk20 {unk20}, unk21 {unk21}, unk22 {unk22}, "
            + $"collision collections {volumeCollections}");
        sb.AppendLine($"    part transform is identity on {identityTransform} of {parts}");
        sb.AppendLine($"    parent index set on {parentIndexSet} of {parts}");
        sb.AppendLine($"    common block on {commons} of {parts}, of which {comZeroTail} carry an empty tail "
            + $"and {comEffects} an effects block");
        sb.AppendLine($"    common.unk2 {Spread(comUnk2)}  unk3_transform {Spread(comUnk3)}  "
            + $"unk4 {Spread(comUnk4)}");
        sb.AppendLine($"    common.unk5_data {Spread(comUnk5)}  unk6_value {Spread(comUnk6)}  "
            + $"unk7 {Spread(comUnk7)}  unk8 {Spread(comUnk8)}");
        sb.AppendLine($"    unk2 non-zero on kinds {Spread(unk2Kinds)}");
        sb.AppendLine($"    unk5 {Spread(unk5Values)}");
        sb.AppendLine($"    unk6 {Spread(unk6Values)}");
        sb.AppendLine($"    common.unk2 values {Spread(comUnk2Values)}");
        sb.AppendLine($"    common effects rows {Spread(effectCounts)}");
        sb.AppendLine($"    collision collections per part {Spread(volumeCollections2)}, "
            + $"{volumeCollectionCounts} volumes over all of them");
        sb.AppendLine($"    volumes per part {Spread(volumesPerPart)}; the ones carrying none are of kinds "
            + $"{Spread(volumelessKinds)}");
        sb.AppendLine($"    parts carrying no handle, no volume, no drain, no unk14 and no unk20: "
            + $"{emptyParts} of {parts}");
        sb.AppendLine($"    a part's index is in its parent's unk20 on {childInParentList} of "
            + $"{childrenClaimed}; an unk20 value names a part claiming this one as its parent "
            + $"{unk20NamesAChild} of {unk20Values}");
        sb.AppendLine($"    part.unk14 {unk14Values} values ({unk14InParts} inside the part list, "
            + $"{unk14InJoints} inside the joint list, {unk14InOwners} inside the owner-deform list)");
        sb.AppendLine($"    part.unk20 {unk20Values} values ({unk20InParts} inside the part list, "
            + $"{unk20InJoints} inside the joint list)");
        sb.AppendLine($"    drain-energy rows {drainRows} ({drainInParts} naming a part that exists)");
        sb.AppendLine($"    hash→index pairs {pairs} over {carsWithPairs} cars; naming a part's own bone "
            + $"{pairsResolvingToPartBone}, indexing that same part {pairsIndexingItsOwnPart}, "
            + $"out of range {pairsOutOfRange}; a complete part index on {carsWherePairsCoverParts} cars; "
            + $"the index is inside the joint list {pairIndexesAJoint} times");
        sb.AppendLine($"    per car the pair count equals the joint count on {carsPairsMatchJoints} and the "
            + $"owner-deform count on {carsPairsMatchOwners} of {cars}");
        sb.AppendLine($"    owner deforms {owners}, u16 entries {ownerU16} ({ownerU16InParts} inside the part "
            + $"list), part transforms {ownerPartTransforms} (matching the part count on "
            + $"{ownerTransformsMatchParts}, naming a part's own bone {ownerMatrixNamesPartBone}), "
            + $"owner hashes on a part's bone {ownerHashIsPartBone} of {owners * 2}");
        sb.AppendLine($"    joints {joints}, their u16 fields {jointU16InParts} of {jointU16Fields} inside "
            + $"the part list; part break energies {breakEnergies} "
            + $"({breakEnergyInRange} naming a part that exists)");

        sb.AppendLine($"    centre of mass is zero on {comZeroCentre} of {parts}");
        sb.AppendLine($"    part transform: identity {transformIdentity}, translation on the bone's REST "
            + $"position {transformOnRest}, on its world position {transformOnWorld}, "
            + $"neither {transformElsewhere}");
        sb.AppendLine($"    common.unk5_data rows {Spread(nested)}");
        sb.AppendLine($"    common effects rows {Spread(effectRows)}");
        sb.AppendLine("    ──── the commonest whole template, by kind ────");
        foreach ((string kind, Dictionary<string, int> counts) in templates.OrderBy(p => p.Key))
        {
            (string template, int held) = counts.OrderByDescending(p => p.Value).First();
            int total = counts.Sum(p => p.Value);
            sb.AppendLine($"    {kind,-8} {held,4} of {total,4}  {template}");
        }

        check("every part names exactly one bone", unk3Count.Count == 1 && unk3Count.ContainsKey(1),
            Spread(unk3Count));
        // The three findings the mint and the removal are built on. Asserted rather than printed, so that a
        // change in how the prefab is read fails here instead of in game.
        check("the parent link is written THREE times, and the third copy agrees with the other two",
            unk20Values > 0 && childInParentList == childrenClaimed
            && unk20NamesAChild == unk20Values,
            $"a part is in its parent's list {childInParentList}/{childrenClaimed}, and every one of the "
            + $"{unk20Values} entries names a part claiming it back ({unk20NamesAChild})");
        check("the hash→index table is not an index of the PARTS — it names owner deforms",
            pairs > 0 && pairsIndexingItsOwnPart == 0 && pairsResolvingToPartBone == pairs
            && carsWherePairsCoverParts == 0,
            $"{pairs} rows, all on a part's own bone, none of them indexing that part");
        check("the fields a minted part carries a constant in are constant over the corpus",
            unk4Zero == 0 && unk4.Count == parts && unk4.TrueForAll(v => v == 1f)
            && unk18.Count == 1 && unk18.ContainsKey(0)
            && unk23.Count == 1 && unk23.ContainsKey(0u) && unk24.Count == 1 && unk24.ContainsKey(0u)
            && impulses == 0 && drops == 0 && unk21 == 0 && unk22 == 0
            && commons == parts && comUnk3.Count == 1 && comUnk3.ContainsKey(0)
            && comUnk4.Count == 1 && comUnk4.ContainsKey(0)
            && comUnk8.Count == 1 && comUnk8.ContainsKey(0)
            && comUnk2Values.Count == 1 && comUnk2Values.ContainsKey(1065353216),
            $"unk4 1.0 ×{unk4.Count}, unk18/23/24 zero, no impulses, drops, unk21 or unk22, "
            + $"a common block on {commons}");
        // The part transform is the one authored field with NO rule behind it, so what the writer chose is
        // asserted against the corpus rather than left as a comment: if a later reading ever shows the
        // shipped parts carry the bone's rest pose instead, this fails here rather than in game.
        check("a part's own transform follows neither the bone's rest pose nor its world place, and identity "
            + "is the commonest single value it takes",
            transformIdentity > transformOnRest + transformOnWorld + transformElsewhere
                || (transformOnRest * 20 < parts && transformOnWorld == 0),
            $"identity {transformIdentity}, rest {transformOnRest}, world {transformOnWorld}, "
            + $"neither {transformElsewhere}, of {parts}");
        // What a granted part deliberately does NOT have, stated so that "it starts empty" is a measured
        // claim about the corpus rather than an assumption — the modder is told to add one, and the doc says
        // why.
        check("every shipped part carries at least one collision volume, which a minted one does not",
            !volumesPerPart.ContainsKey(0) && volumelessKinds.Count == 0,
            $"volumes per part {Spread(volumesPerPart)}");
        check("the body is the one part marked by unk2, so a copy of it must not carry that out",
            unk2.Count == 2 && unk2.GetValueOrDefault((byte)1) == cars && unk2Kinds.Count == 1
            && unk2Kinds.ContainsKey("body"),
            $"{Spread(unk2)}; on kinds {Spread(unk2Kinds)}");
        foreach (CarPartTemplate template in Car.PartKinds)
        {
            templates.TryGetValue(template.Name, out Dictionary<string, int>? counts);
            int held = counts == null ? 0 : counts.Max(p => p.Value);
            check($"the template for a new \"{template.Name}\" is what {template.Shipped} shipped parts of "
                + "that kind are written as",
                counts != null && held == template.Shipped && counts.Sum(p => p.Value) == template.OfKind,
                $"measured {held} of {counts?.Sum(p => p.Value) ?? 0}, claimed {template.Shipped} of "
                + $"{template.OfKind}");
        }
    }

    // ── the focus car, mirrored so the game's folders are never written to ──

    private static string? Mirror(
        string focus, string folder, StringBuilder sb, Action<string, bool, string> check,
        string into = "")
    {
        string source = MafiaEnvironment.ExtractedDir(new FileInfo(Path.Combine(folder, focus + ".sds")));
        if (!File.Exists(Path.Combine(source, "SDSContent.xml")))
        {
            check("the focus car is extracted", false, focus);
            return null;
        }

        string mirror = Path.Combine(Scratch, focus + into);
        try
        {
            if (Directory.Exists(mirror)) Directory.Delete(mirror, recursive: true);
            Directory.CreateDirectory(mirror);
            foreach (string file in Directory.GetFiles(source))
            {
                File.Copy(file, Path.Combine(mirror, Path.GetFileName(file)));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            check("the focus car can be mirrored into the temp directory", false, ex.Message);
            return null;
        }

        sb.AppendLine($"\n\n════ the focus car, mirrored into {mirror} ════");
        return mirror;
    }

    // ── giving a bare component a deform part ──

    /// <summary>
    /// One bare component granted a part, saved, and read back off the ARCHIVE rather than off the aggregate
    /// that wrote it — which is the only reading that says the edit reached a file.
    /// </summary>
    private static void Grant(StringBuilder sb, string mirror, Action<string, bool, string> check)
    {
        sb.AppendLine("\n════ a bare component is given a deform part ════");

        Car? car = Car.ReadFrom(mirror);
        if (car?.Body is not { } body) { check("the mirrored car reads", false, mirror); return; }

        byte[] was = File.ReadAllBytes(car.PrefabPath!);
        CarComponent? bare = Bare(car);
        if (bare == null) { check("the focus car has a bare component", false, ""); return; }

        int partsWere = car.Prefab.CarDeformParts.Count;
        int bareWere = car.Components.Count(c => c.IsBare);
        string name = bare.Name;
        sb.AppendLine($"    granting \"{name}\" a {Car.DefaultPartKind.Name} under \"{body.Name}\"");

        CarEdit? edit = car.GrantDeformPart(bare, Car.DefaultPartKind, body, out string? refusal);
        check("a bare component takes a deform part", edit != null, refusal ?? "");
        if (edit == null) return;
        CarSave saved = car.Save();
        check("the car saves after the grant", saved.Ok, string.Join("; ", saved.Lost));
        if (!saved.Ok) return;

        Car? again = Car.ReadFrom(mirror);
        CarComponent? grown = again?.Components.FirstOrDefault(
            c => string.Equals(c.Name, name, StringComparison.Ordinal));
        check("the component read back off the archive is no longer bare",
            grown is { IsBare: false }, grown == null ? "it is not in the car at all" : grown.Kind);
        if (grown == null || again == null) return;

        check("it is the kind that was asked for",
            grown.PartType == Car.DefaultPartKind.Type, $"{grown.Kind} ({grown.PartType})");
        check("it has damage parameters of its own", grown.DamageFields.Count > 0,
            $"{grown.DamageFields.Count} fields");
        check("its part names its own bone", grown.BoneHash == bare.BoneHash && grown.BoneResolves,
            grown.Name);
        check("it hangs off the component that was chosen",
            grown.Parent != null && grown.Parent.BoneHash == body.BoneHash,
            grown.Parent?.Name ?? "nothing");
        check("its geometry is still there", grown.Pieces == bare.Pieces,
            $"{grown.Pieces} pieces against {bare.Pieces}");
        check("it carries no collision, no handle and no marker yet",
            grown.Collisions.Count == 0 && grown.Handles.Count == 0,
            $"{grown.Collisions.Count} collisions, {grown.Handles.Count} handles");
        check("the car gained exactly one part and lost exactly one bare component",
            again.Prefab.CarDeformParts.Count == partsWere + 1
            && again.Components.Count(c => c.IsBare) == bareWere - 1,
            $"{again.Prefab.CarDeformParts.Count} parts (was {partsWere}), "
            + $"{again.Components.Count(c => c.IsBare)} bare (was {bareWere})");
        check("the grant raised no fault", again.Faults.Count == car.Faults.Count,
            $"{again.Faults.Count} faults, the same {car.Faults.Count} the car opened with");

        // ── all three copies of the parent link, read off the file ──
        CarDeformPart part = again.Prefab.CarDeformParts[grown.PartIndex];
        check("the parent link's hash copy names the chosen component's bone",
            part.ParentFrame == body.BoneHash, $"0x{part.ParentFrame:X16}");
        check("the parent link's index copy names the same part",
            part.ParentIndex == body.PartIndex, part.ParentIndex.ToString());
        check("the parent's own children list names the new part",
            Children(again.Prefab, body.PartIndex).Contains(grown.PartIndex),
            string.Join(",", Children(again.Prefab, body.PartIndex)));

        // ── and the whole of what a minted part carries ──
        Wire(sb, again, grown.PartIndex, check);

        // ── nothing else moved ──
        check("every part that was already there is byte-identical",
            SamePrefix(was, File.ReadAllBytes(again.PrefabPath!), partsWere, check),
            $"{partsWere} parts compared");
    }

    /// <summary>The bare component the probe works on: the first one with geometry, so the census's own
    /// definition and this are the same thing.</summary>
    private static CarComponent? Bare(Car car) =>
        car.Components.FirstOrDefault(c => c.IsBare && c.HasGeometry && c.BoneResolves);

    private static IReadOnlyList<int> Children(PrefabFile prefab, int parent)
    {
        PrefabDeformationInitW? deformation = Deformation(prefab);
        return deformation == null || parent < 0 || parent >= deformation.DeformParts.Count
            ? []
            : [.. deformation.DeformParts[parent].Unk20.Select(v => (int)v)];
    }

    /// <summary>
    /// What the minted part actually holds, field by field, against the template and the corpus constants —
    /// read off the archive, because the whole point is that these reached a file.
    /// </summary>
    private static void Wire(StringBuilder sb, Car car, int at, Action<string, bool, string> check)
    {
        PrefabDeformationInitW? deformation = Deformation(car.Prefab);
        if (deformation == null || at < 0 || at >= deformation.DeformParts.Count)
        {
            check("the minted part is in the deformation block", false, at.ToString());
            return;
        }
        PrefabDeformPartW part = deformation.DeformParts[at];
        CarPartTemplate template = Car.DefaultPartKind;
        sb.AppendLine($"    minted part {at}: flags 0x{part.Flags:X8}, unk5/6 {R(part.Unk5)}/{R(part.Unk6)}, "
            + $"common {part.Common.Count}, volumes {part.CollisionVolumes.Sum(c => c.Volumes.Count)}");

        check("the minted part carries the kind's own numbers",
            part.Flags == template.Flags && part.Unk5 == template.Threshold
            && part.Unk6 == template.Threshold,
            $"flags 0x{part.Flags:X8}, unk5/6 {R(part.Unk5)}/{R(part.Unk6)}");
        check("it carries the constants every shipped part carries",
            part.Unk2 == 0 && part.Unk4 == 1f && part.Unk18 == 0 && part.Unk23 == 0 && part.Unk24 == 0
            && part.InternalImpulses.Count == 0 && part.DropParts.Count == 0
            && part.Unk21Data.Count == 0 && part.Unk22RelData.Count == 0,
            $"unk2 {part.Unk2}, unk4 {R(part.Unk4)}, unk18 {part.Unk18}, unk23/24 {part.Unk23}/{part.Unk24}");
        check("it is in none of the machinery that makes a panel come off the car",
            part.Unk14.Count == 0 && part.DrainEnergy.Count == 0 && part.Unk20.Count == 0
            && deformation.Unk1Pairs.All(p => p.Hash != part.Unk3[0])
            && deformation.Joints.All(j => j.Unk0 != at && j.Unk1 != at && j.Unk2 != at && j.Unk3 != at),
            $"unk14 {part.Unk14.Count}, drains {part.DrainEnergy.Count}, unk20 {part.Unk20.Count}");
        check("it holds one empty collision collection, the shape every shipped part is in",
            part.CollisionVolumes.Count == 1 && part.CollisionVolumes[0].Volumes.Count == 0,
            $"{part.CollisionVolumes.Count} collections");

        check("its common block carries the kind's tuning and the corpus's own constants",
            part.Common.Count == 1
            && part.Common[0].SpeedMin == template.SpeedMin && part.Common[0].SpeedMax == template.SpeedMax
            && part.Common[0].Resistance == template.Resistance && part.Common[0].Mass == template.Mass
            && part.Common[0].EnergyStart == template.EnergyStart
            && part.Common[0].EnergyDrop == template.EnergyDrop
            && part.Common[0].Unk7 == template.Damping
            && part.Common[0].Unk2.Count == 1 && part.Common[0].Unk2[0] == 1065353216
            && part.Common[0].Unk3Transform.Count == 0 && part.Common[0].Unk4.Count == 0
            && part.Common[0].Unk6Value.Count == 0 && part.Common[0].Unk8.Count == 0,
            part.Common.Count == 0
                ? "there is no common block"
                : $"speed {R(part.Common[0].SpeedMin)}…{R(part.Common[0].SpeedMax)}, "
                    + $"mass {R(part.Common[0].Mass)}, unk7 {part.Common[0].Unk7}");
        // The half nobody has read rides along from a donor rather than being invented — so it has to be
        // there, and it has to be a shipped part's.
        check("the half of the common block nobody has read came from a part of this car",
            part.Common.Count == 1 && part.Common[0].Unk5Data.Count == 1
            && part.Common[0].PartEffects.Count == 1
            && deformation.DeformParts.Any(p => p != part && p.Common.Count == 1
                && Same(p.Common[0], part.Common[0])),
            part.Common.Count == 0
                ? "there is no common block"
                : $"unk5_data {part.Common[0].Unk5Data.Count}, effects {part.Common[0].PartEffects.Count}");
    }

    /// <summary>Whether two common blocks agree on the half the toolkit does not interpret.</summary>
    private static bool Same(PrefabDeformPartCommonW a, PrefabDeformPartCommonW b) =>
        a.Unk5Data.Count == b.Unk5Data.Count && a.PartEffects.Count == b.PartEffects.Count
        && a.Unk5Data.Zip(b.Unk5Data).All(p =>
            p.First.Unk0 == p.Second.Unk0 && p.First.Unk1 == p.Second.Unk1
            && p.First.Unk2 == p.Second.Unk2 && p.First.Unk3 == p.Second.Unk3
            && p.First.Unk4 == p.Second.Unk4)
        && a.PartEffects.Zip(b.PartEffects).All(p =>
            p.First.ParticleBreakId == p.Second.ParticleBreakId
            && p.First.ParticleScale == p.Second.ParticleScale
            && p.First.Packs.Count == p.Second.Packs.Count);

    /// <summary>
    /// Whether the parts a grant did not touch are byte for byte what they were — asked of the STRUCTURES
    /// rather than of the file, because appending a part moves every byte after the part list.
    /// </summary>
    private static bool SamePrefix(
        byte[] was, byte[] now, int parts, Action<string, bool, string> check)
    {
        PrefabFile before, after;
        using (var buffer = new MemoryStream(was, writable: false)) before = PrefabFile.Read(buffer);
        using (var buffer = new MemoryStream(now, writable: false)) after = PrefabFile.Read(buffer);

        PrefabDeformationInitW? had = Deformation(before);
        PrefabDeformationInitW? has = Deformation(after);
        if (had == null || has == null || had.DeformParts.Count != parts) return false;

        for (int i = 0; i < parts; i++)
        {
            byte[] one = Bytes(had.DeformParts[i]);
            byte[] two = Bytes(has.DeformParts[i]);
            // The parent whose children list gained the new part is the ONE exception, and it is the whole
            // of the third copy of the link.
            if (one.AsSpan().SequenceEqual(two)) continue;
            if (has.DeformParts[i].Unk20.Count == had.DeformParts[i].Unk20.Count + 1) continue;
            check($"part {i} was left alone", false, $"{one.Length} B against {two.Length} B");
            return false;
        }
        return true;
    }

    private static byte[] Bytes(PrefabDeformPartW part)
    {
        using var buffer = new MemoryStream();
        part.WriteTo(new BinaryWriter(buffer));
        return buffer.ToArray();
    }

    // ── once granted, it is a component like any other ──

    /// <summary>
    /// The granted component put through the three things a bare one could not do: taking a collision, having
    /// its damage parameters written, and being given a marker.
    ///
    /// <para>
    /// Each of the three is SAVED and read back off the archive, because "it behaves like any other" is a
    /// claim about the file and not about the object graph — a solid collision is only real once its ItemDesc
    /// record is on disk and the manifest names it.
    /// </para>
    /// </summary>
    private static void Behaves(StringBuilder sb, string mirror, Action<string, bool, string> check)
    {
        sb.AppendLine("\n════ and then it behaves like any other component ════");

        Car? car = Car.ReadFrom(mirror);
        if (car?.Body is not { } body) { check("the mirrored car reads", false, mirror); return; }
        CarComponent? bare = Bare(car);
        if (bare == null) { check("the focus car has a bare component", false, ""); return; }

        ulong bone = bare.BoneHash;
        if (car.GrantDeformPart(bare, Car.DefaultPartKind, body, out string? refusal) == null
            || !car.Save().Ok)
        {
            check("the component to work on can be granted a part", false, refusal ?? "the save was refused");
            return;
        }
        sb.AppendLine($"    on \"{bare.Name}\", granted a {Car.DefaultPartKind.Name} of \"{body.Name}\"");

        // ── a collision ──
        Car? at = Car.ReadFrom(mirror);
        CarComponent? grown = at?.ComponentOfBone(bone);
        if (at == null || grown == null) { check("the granted component reads back", false, ""); return; }

        CarEdit? collision = at.AddCollision(
            grown, CarCollisionRole.Body, CarCollisionShape.Box,
            new Vector3(0.2f, 0.2f, 0.05f), Vector3.Zero, out refusal);
        check("a granted component takes a collision", collision != null, refusal ?? "");
        CarSave saved = at.Save();
        check("the collision saves", saved.Ok, string.Join("; ", saved.Lost));

        at = Car.ReadFrom(mirror);
        grown = at?.ComponentOfBone(bone);
        check("that collision is on the component, by the role and shape that were asked for",
            grown?.Collisions.Count == 1 && grown.Collisions[0].Role == CarCollisionRole.Body
            && grown.Collisions[0].Shape == CarCollisionShape.Box,
            grown == null ? "the component is gone" : $"{grown.Collisions.Count} collisions");

        // ── its damage parameters ──
        if (at == null || grown == null) return;
        CarField mass = grown.DamageFields.First(f => f.Label.StartsWith("Mass", StringComparison.Ordinal));
        CarEdit? damage = at.SetDamage(grown, [mass with { Number = 7.5f }], out refusal);
        check("its damage parameters can be written", damage != null, refusal ?? "");
        saved = at.Save();
        check("the damage saves", saved.Ok, string.Join("; ", saved.Lost));

        at = Car.ReadFrom(mirror);
        grown = at?.ComponentOfBone(bone);
        check("the mass that was typed is what the archive now says",
            grown?.Damage != null && MathF.Abs(grown.Damage.Mass - 7.5f) < 1e-3f,
            grown?.Damage?.Mass.ToString("F2") ?? "there is no damage block");

        // ── and a marker ──
        if (at == null || grown == null) return;
        int markers = grown.Markers.Count;
        CarEdit? marker = at.AddMarker(grown, CarMarkerRole.FuelTank, out refusal);
        check("it can be given a marker", marker != null, refusal ?? "");
        saved = at.Save();
        check("the marker saves", saved.Ok, string.Join("; ", saved.Lost));

        at = Car.ReadFrom(mirror);
        grown = at?.ComponentOfBone(bone);
        check("the marker hangs off it rather than off the body",
            grown?.Markers.Count == markers + 1,
            $"{grown?.Markers.Count} markers against {markers}");
    }

    // ── and taking it away again ──

    /// <summary>
    /// The demotion, on a car that has just been granted a part: the component goes back to bare and the
    /// prefab goes back to the bytes it held before the grant, which is the strongest statement available
    /// that the round trip is closed.
    /// </summary>
    private static void Demote(
        StringBuilder sb, string mirror, byte[]? pristine, Action<string, bool, string> check)
    {
        sb.AppendLine("\n════ and the deform part taken away again ════");

        Car? car = Car.ReadFrom(mirror);
        if (car?.Body is not { } body) { check("the mirrored car reads", false, mirror); return; }

        // The part the grant above added is the last one, and it is the one this takes away.
        CarComponent? grown = car.Components
            .Where(c => !c.IsBare)
            .OrderByDescending(c => c.PartIndex)
            .FirstOrDefault();
        if (grown == null) { check("the granted part is there to take away", false, ""); return; }

        byte[] was = File.ReadAllBytes(car.PrefabPath!);
        int pieces = grown.Pieces;
        string name = grown.Name;
        int childrenWere = Children(car.Prefab, body.PartIndex).Count;

        CarEdit? edit = car.RemoveDeformPart(grown, out string? refusal);
        check("a granted part can be taken away", edit != null, refusal ?? "");
        if (edit == null) return;
        check("the demotion asks for no frame rewrite", !car.FramesChanged,
            car.FramesChanged ? "the frame graph was marked dirty" : "the frame graph was left alone");

        CarSave saved = car.Save();
        check("the car saves after the demotion", saved.Ok, string.Join("; ", saved.Lost));
        if (!saved.Ok) return;

        Car? again = Car.ReadFrom(mirror);
        CarComponent? bare = again?.Components.FirstOrDefault(
            c => string.Equals(c.Name, name, StringComparison.Ordinal));
        check("the component is bare again", bare is { IsBare: true },
            bare == null ? "it is not in the car at all" : bare.Kind);
        check("its geometry survived the demotion", bare?.Pieces == pieces,
            $"{bare?.Pieces} pieces against {pieces}");
        check("the parent's children list lost it",
            again != null && Children(again.Prefab, body.PartIndex).Count == childrenWere - 1,
            again == null ? "" : string.Join(",", Children(again.Prefab, body.PartIndex)));
        check("no fault was raised", again != null && again.Faults.Count == car.Faults.Count,
            $"{again?.Faults.Count} faults, the same {car.Faults.Count} the car opened with");

        // The whole point: a grant followed by a demotion is a car nobody touched.
        byte[] now = File.ReadAllBytes(again!.PrefabPath!);
        check("granting a part and taking it away leaves the prefab byte for byte as it was",
            pristine != null && now.AsSpan().SequenceEqual(pristine),
            $"{now.Length} B now, {was.Length} B before the demotion, {pristine?.Length} B as it shipped");
    }

    // ── one intent, one undo step ──

    /// <summary>
    /// Both operations taken back: the aggregate restores what the structures HELD rather than reversing what
    /// it did to them, so the measurable form of "one undo step" is that the bytes come back.
    /// </summary>
    private static void Undo(StringBuilder sb, string mirror, Action<string, bool, string> check)
    {
        sb.AppendLine("\n════ each of the two is one undo step ════");

        Car? car = Car.ReadFrom(mirror);
        if (car?.Body is not { } body) { check("the mirrored car reads", false, mirror); return; }
        CarComponent? bare = Bare(car);
        if (bare == null) { check("the focus car has a bare component", false, ""); return; }

        byte[] was = car.Prefab.ToBytes();
        CarEdit? grant = car.GrantDeformPart(bare, Car.DefaultPartKind, body, out string? refusal);
        check("the grant is one intent", grant != null, refusal ?? "");
        if (grant == null) return;
        check("the grant asks for no frame rewrite", !car.FramesChanged,
            car.FramesChanged ? "the frame graph was marked dirty" : "the frame graph was left alone");

        car.Restore(grant.Before);
        check("undoing the grant puts the prefab back byte for byte",
            car.Prefab.ToBytes().AsSpan().SequenceEqual(was), "");
        car.Restore(grant.After);
        check("redoing it puts the part back",
            car.Prefab.CarDeformParts.Any(p => p.Frame == bare.BoneHash), "");

        // …and the demotion, from the state the redo left.
        Car staged = Car.Stitch(car.Prefab, car.Frames, prefabPath: car.PrefabPath, extracted: mirror);
        CarComponent? grown = staged.ComponentOfBone(bare.BoneHash);
        if (grown is not { IsBare: false }) { check("the redone part is a component", false, ""); return; }

        byte[] withPart = staged.Prefab.ToBytes();
        CarEdit? demote = staged.RemoveDeformPart(grown, out refusal);
        check("the demotion is one intent", demote != null, refusal ?? "");
        if (demote == null) return;
        staged.Restore(demote.Before);
        check("undoing the demotion puts the part back byte for byte",
            staged.Prefab.ToBytes().AsSpan().SequenceEqual(withPart), "");
    }

    // ── what is refused, and what nothing is written for ──

    private static void Refusals(StringBuilder sb, string mirror, Action<string, bool, string> check)
    {
        sb.AppendLine("\n════ the refusals ════");

        Car? car = Car.ReadFrom(mirror);
        if (car?.Body is not { } body) { check("the mirrored car reads", false, mirror); return; }
        byte[] was = car.Prefab.ToBytes();

        CarComponent? bare = Bare(car);
        CarComponent? window = car.Components.FirstOrDefault(c => c.PartType == 5);
        CarComponent? door = car.Components.FirstOrDefault(c => c.PartType == 4);

        void Refused(string what, CarEdit? edit, string? why)
        {
            check(what, edit == null && !string.IsNullOrWhiteSpace(why), why ?? "it was allowed");
        }

        if (window != null)
        {
            Refused("a component that already has a part is not given a second one",
                car.GrantDeformPart(window, Car.DefaultPartKind, body, out string? why), why);
        }
        if (bare != null)
        {
            Refused("a part cannot hang off a bare component",
                car.GrantDeformPart(bare, Car.DefaultPartKind, bare, out string? why), why);
        }
        Refused("the body cannot be demoted", car.RemoveDeformPart(body, out string? bodyWhy), bodyWhy);
        if (bare != null)
        {
            Refused("a bare component has no part to take away",
                car.RemoveDeformPart(bare, out string? why), why);
        }
        if (door != null)
        {
            // A shipped door is held by its windows, by a joint and by the machinery that detaches it — the
            // whole reason the demotion asks first rather than renumbering blind.
            Refused("a shipped door is refused with what still holds it",
                car.RemoveDeformPart(door, out string? why), why);
        }

        // A demotion has to be a demotion and not a deletion. A bare component is minted only from a bone
        // that resolves AND draws, so demoting a component that has neither would take the row away with no
        // way to give the part back — the grant refuses an unresolved bone.
        CarComponent? broken = car.Components.FirstOrDefault(c => !c.IsBare && !c.BoneResolves);
        if (broken != null)
        {
            Refused("a component whose bone does not resolve cannot be demoted into nothing",
                car.RemoveDeformPart(broken, out string? why), why);
        }
        CarComponent? dark = car.Components.FirstOrDefault(
            c => !c.IsBare && c.BoneResolves && !c.HasGeometry && c.PartType != 1);
        if (dark != null)
        {
            Refused("nor can one that draws nothing at the level on screen",
                car.RemoveDeformPart(dark, out string? why), why);
        }
        sb.AppendLine($"    a component with an unresolved bone: {broken?.Name ?? "none on this car"}; "
            + $"one that draws nothing here: {dark?.Name ?? "none on this car"}");

        check("not one refusal changed the prefab", car.Prefab.ToBytes().AsSpan().SequenceEqual(was), "");
    }

    /// <summary>Where each bone of a car's rig stands: in its own rest pose, and in the world the model puts
    /// it in — the two candidates for what a part's own transform could be relative to.</summary>
    private static Dictionary<ulong, (Vector3 Pose, Vector3 World)> BonePlaces(Car car)
    {
        var places = new Dictionary<ulong, (Vector3, Vector3)>();
        Illusion.Formats.Frames.ObjectTypes.FrameObjectModel? model =
            car.Frames?.FrameObjects?.Values
                .OfType<Illusion.Formats.Frames.ObjectTypes.FrameObjectModel>().FirstOrDefault();
        if (model == null) return places;

        Illusion.Formats.Hashing.HashName[] names;
        Matrix4x4[] rest;
        try
        {
            names = model.GetSkeletonObject().BoneNames ?? [];
            rest = model.GetSkeletonObject().JointTransforms ?? [];
        }
        catch (Exception) { return places; }

        for (int i = 0; i < names.Length; i++)
        {
            string name = names[i].ToString();
            if (name.Length == 0) continue;
            Vector3 restAt = i < rest.Length ? rest[i].Translation : Vector3.Zero;
            places[Illusion.Formats.Hashing.Fnv64.Hash(name)] =
                (restAt, model.GetJointWorldTransform(i).Translation);
        }
        return places;
    }

    /// <summary>A float as it round-trips, so a template read off this report is the value and not a rounding
    /// of it.</summary>
    private static string R(float value) => value.ToString("R", System.Globalization.CultureInfo.InvariantCulture);

    private static void Bump<T>(Dictionary<T, int> into, T key) where T : notnull =>
        into[key] = into.GetValueOrDefault(key) + 1;

    /// <summary>The part kinds, as the reference toolkit reads them — repeated here rather than reached for,
    /// because a probe that asked the format layer would be asserting its own reading back at itself.</summary>
    private static string PartKind(uint type) => type switch
    {
        0 => "normal", 1 => "body", 2 => "wheel", 3 => "lid", 4 => "door", 5 => "window",
        6 => "cover", 7 => "bumper", 12 => "exhaust", 13 => "motor", 14 => "tyre", 15 => "snow",
        16 => "plow",
        _ => type.ToString(System.Globalization.CultureInfo.InvariantCulture),
    };

    private static string Spread<T>(Dictionary<T, int> counts) where T : notnull =>
        counts.Count == 0
            ? "—"
            : string.Join(", ", counts.OrderByDescending(p => p.Value).Take(8)
                .Select(p => $"{p.Key} ×{p.Value}"))
                + (counts.Count > 8 ? $" (+{counts.Count - 8} more)" : "");

    private static string Range(List<float> values) =>
        values.Count == 0 ? "never" : $"{values.Count} values, {values.Min():F3} … {values.Max():F3}";

    private static bool IsIdentity(PrefabTransformW t) =>
        t.Row0 == new Vector3(1f, 0f, 0f) && t.Row1 == new Vector3(0f, 1f, 0f)
        && t.Row2 == new Vector3(0f, 0f, 1f) && t.Translation == Vector3.Zero;

    /// <summary>The deformation block of a car's prefab, or null when it holds none.</summary>
    private static PrefabDeformationInitW? Deformation(PrefabFile prefab)
    {
        PrefabEntryW? entry = prefab.Wire.Prefabs.FirstOrDefault(p => p.CarInit.Count > 0);
        PrefabCarInitW? car = entry?.CarInit[0];
        return car is { Deformation.Count: > 0 } ? car.Deformation[0] : null;
    }
}
