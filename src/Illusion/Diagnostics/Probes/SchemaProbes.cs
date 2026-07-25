using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Text;
using Illusion.Formats.Native;
using Illusion.Formats.Native.Model;

namespace Illusion.Diagnostics.Probes;

/// <summary>
/// The generated-model cycle proof (D3): a collision model full of hostile values survives
/// C# write → native read → native write → C# read bit-exactly (NaN payloads included), the
/// wire refuses trailing garbage, truncation and hostile counts on both sides, and the
/// committed generated files match a fresh regeneration (drift check via the tool itself).
/// Output: %TEMP%\illusion_schema.txt
/// </summary>
internal static class SchemaProbes
{
    private delegate void CheckFn(string name, bool ok, string detail = "");

    internal static void RunSchemaProbe(string? repoRootArg)
    {
        string outFile = Path.Combine(Path.GetTempPath(), "illusion_schema.txt");
        var sb = new StringBuilder();
        int pass = 0, fail = 0;

        void Check(string name, bool ok, string detail = "")
        {
            if (ok) pass++; else fail++;
            sb.AppendLine($"[{(ok ? "PASS" : "FAIL")}] {name}{(detail == "" ? "" : " — " + detail)}");
        }

        try
        {
            CheckNativeCycle(Check);
            CheckWireStrictness(Check);
            CheckRegen(Check, sb, repoRootArg);
        }
        catch (Exception ex)
        {
            fail++;
            sb.AppendLine("[FAIL] unexpected exception — " + ex);
        }

        sb.Insert(0, $"SCHEMA PROBE: {pass} passed, {fail} failed\n\n");
        File.WriteAllText(outFile, sb.ToString());
    }

    /// <summary>A model that goes out of its way to be hostile: NaNs with custom payloads,
    /// negative zero, denormals, infinities, empty and non-trivial lists and blobs.</summary>
    private static CollisionModel BuildHostileModel()
    {
        float nanPayload = BitConverter.UInt32BitsToSingle(0x7FC00123);
        float negNan = BitConverter.UInt32BitsToSingle(0xFFC00001);
        float denormal = BitConverter.UInt32BitsToSingle(0x00000001);

        var blob = new byte[4097];
        new Random(17).NextBytes(blob);

        return new CollisionModel
        {
            Version = 17,
            Platform = 0,
            Instances =
            {
                new CollisionInstance
                {
                    Position = new Vector3(nanPayload, -0.0f, float.PositiveInfinity),
                    Rotation = new Vector3(negNan, denormal, float.NegativeInfinity),
                    Hash = 0xDEADBEEFCAFEBABE,
                    Unk4 = -1,
                    Group = 255,
                },
                new CollisionInstance
                {
                    Position = new Vector3(1.5f, -2.25f, 1e-42f),
                    Rotation = new Vector3(0, 0, 0),
                    Hash = 0,
                    Unk4 = int.MinValue,
                    Group = 0,
                },
            },
            Meshes =
            {
                new CollisionMesh { Hash = 1, CookedMesh = [], Sections = { } },
                new CollisionMesh
                {
                    Hash = ulong.MaxValue,
                    CookedMesh = blob,
                    Sections =
                    {
                        new CollisionSection { Start = 0, NumEdges = 3, Material = 2, Unk2 = 0 },
                        new CollisionSection
                        {
                            Start = uint.MaxValue, NumEdges = 0, Material = 0, Unk2 = uint.MaxValue,
                        },
                    },
                },
            },
        };
    }

    private static byte[] ToWire(CollisionModel model)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            model.WriteTo(writer);
        }
        return stream.ToArray();
    }

    private static CollisionModel FromWire(byte[] wire)
    {
        using var stream = new MemoryStream(wire, writable: false);
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        return CollisionModel.ReadFrom(reader);
    }

    private static void CheckNativeCycle(CheckFn check)
    {
        CollisionModel model = BuildHostileModel();
        byte[] wire = ToWire(model);

        // C#-only sanity first: read-back + comparator silence.
        var selfDiffs = new List<string>();
        CollisionModel.Diff("model", model, FromWire(wire), selfDiffs);
        check("C# wire round-trips the hostile model", selfDiffs.Count == 0,
            string.Join("; ", selfDiffs.Take(3)));

        (int status, MfBuffer buffer) = NativeFormats.CollisionModelEcho(wire);
        using (buffer)
        {
            check("the native echo accepts the wire image", status == NativeMethods.Ok,
                NativeFormats.LastError);
            if (status != NativeMethods.Ok)
            {
                return;
            }

            byte[] echoed = buffer.ToArray();
            check("the echoed wire image is byte-identical", echoed.AsSpan().SequenceEqual(wire),
                $"{wire.Length} bytes in, {echoed.Length} out");

            var diffs = new List<string>();
            CollisionModel.Diff("model", model, FromWire(echoed), diffs);
            check("the comparator sees no drift after the native cycle", diffs.Count == 0,
                string.Join("; ", diffs.Take(3)));
        }
    }

    private static void CheckWireStrictness(CheckFn check)
    {
        byte[] wire = ToWire(BuildHostileModel());

        byte[] trailing = [.. wire, 0xAA];
        (int status, MfBuffer buffer) = NativeFormats.CollisionModelEcho(trailing);
        using (buffer)
        {
            check("native refuses trailing bytes", status != NativeMethods.Ok,
                NativeFormats.LastError);
            check("the refusal names the trailing bytes",
                NativeFormats.LastError.Contains("trailing", StringComparison.Ordinal),
                NativeFormats.LastError);
        }

        (int truncStatus, MfBuffer truncBuffer) = NativeFormats.CollisionModelEcho(
            wire.AsSpan(0, wire.Length / 2));
        using (truncBuffer)
        {
            check("native refuses a truncated wire image", truncStatus != NativeMethods.Ok,
                NativeFormats.LastError);
        }

        // A hostile list count: version+platform, then instances count 0xFFFFFFFF.
        byte[] hostile = new byte[12];
        BitConverter.TryWriteBytes(hostile.AsSpan(0, 4), 17u);
        BitConverter.TryWriteBytes(hostile.AsSpan(4, 4), 0u);
        BitConverter.TryWriteBytes(hostile.AsSpan(8, 4), 0xFFFFFFFFu);
        (int hostileStatus, MfBuffer hostileBuffer) = NativeFormats.CollisionModelEcho(hostile);
        using (hostileBuffer)
        {
            check("native refuses a hostile list count", hostileStatus != NativeMethods.Ok,
                NativeFormats.LastError);
        }

        bool managedRefused = false;
        try
        {
            FromWire(hostile);
        }
        catch (Exception ex) when (ex is InvalidDataException or EndOfStreamException)
        {
            managedRefused = true;
        }
        check("C# refuses the same hostile count", managedRefused);
    }

    /// <summary>Runs the generator's own drift check over the committed files. The schema and the
    /// generator live with the native core, which this repository does not carry: the check runs
    /// when the core is at hand (beside this tree, or passed as the argument) and is skipped
    /// otherwise — a C#-only contributor has nothing to regenerate from.</summary>
    private static void CheckRegen(CheckFn check, StringBuilder sb, string? repoRootArg)
    {
        string? root = FindCoreRoot(repoRootArg);
        if (root is null)
        {
            sb.AppendLine("[SKIP] regen drift check — the native core is not beside this tree "
                + "(pass its root: --probe-schema <coreRoot>)");
            return;
        }

        var info = new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        info.ArgumentList.Add("run");
        info.ArgumentList.Add("--project");
        info.ArgumentList.Add(Path.Combine(root, "tools", "mf-schema-gen"));
        info.ArgumentList.Add("--");
        info.ArgumentList.Add("check");
        info.ArgumentList.Add(root);

        using Process? process = Process.Start(info);
        if (process is null)
        {
            check("the regen check launches", false, "dotnet did not start");
            return;
        }
        string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        check("the committed generated files match a fresh regeneration", process.ExitCode == 0,
            output.ReplaceLineEndings(" | "));
    }

    /// <summary>The native core's repository root: the explicit argument, a tree walked up from
    /// this build that carries the core, or the core repository checked out beside it — the same
    /// lookup order the build uses.</summary>
    private static string? FindCoreRoot(string? explicitRoot)
    {
        if (explicitRoot is not null)
        {
            return HoldsCore(explicitRoot) ? explicitRoot : null;
        }

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (HoldsCore(dir.FullName))
            {
                return dir.FullName;
            }
            string beside = Path.Combine(dir.FullName, "..", "illusion-core");
            if (HoldsCore(beside))
            {
                return Path.GetFullPath(beside);
            }
            dir = dir.Parent;
        }
        return null;
    }

    private static bool HoldsCore(string root) =>
        File.Exists(Path.Combine(root, "src", "Mafia.Formats", "schema", "model.mfs"))
        && Directory.Exists(Path.Combine(root, "tools", "mf-schema-gen"));
}
