using System.IO;
using System.IO.Compression;
using System.Text;
using Illusion.Assets;
using Illusion.Assets.Sds;
using Illusion.Formats;
using Illusion.Formats.Archive;
using Illusion.Formats.Hashing;
using Illusion.Formats.IO;
using Illusion.Formats.Native;
using static Illusion.Diagnostics.Probes.ProbeAssert;

namespace Illusion.Diagnostics.Probes;

/// <summary>
/// Handshake with the native core (Mafia.Formats.dll): the version and ABI revision match the
/// managed facade, the buffer protocol round-trips a 10 MB payload byte-exactly, a bad call
/// reports a readable error, a double free is refused instead of corrupting the heap, and the
/// thread-local error text stays isolated under concurrent callers.
/// Output: %TEMP%\illusion_native.txt
/// </summary>
internal static class NativeProbes
{
    private delegate void CheckFn(string name, bool ok, string detail = "");

    internal static void RunNativeProbe()
    {
        string outFile = Path.Combine(Path.GetTempPath(), "illusion_native.txt");
        var sb = new StringBuilder();
        int pass = 0, fail = 0;

        void Check(string name, bool ok, string detail = "")
        {
            if (ok) pass++; else fail++;
            sb.AppendLine($"[{(ok ? "PASS" : "FAIL")}] {name}{(detail == "" ? "" : " — " + detail)}");
        }

        try
        {
            CheckHandshake(Check);
            CheckEchoRoundtrip(Check);
            CheckErrorReporting(Check);
            CheckDoubleFree(Check);
            CheckThreadIsolation(Check);
        }
        catch (Exception ex)
        {
            fail++;
            sb.AppendLine("[FAIL] unexpected exception — " + ex);
        }

        sb.Insert(0, $"NATIVE PROBE: {pass} passed, {fail} failed\n\n");
        File.WriteAllText(outFile, sb.ToString());
    }

    /// <summary>The load-and-identity gate: a version or ABI mismatch means the DLL on disk and
    /// the managed facade were built from different boundary revisions.</summary>
    private static void CheckHandshake(CheckFn check)
    {
        string version = NativeFormats.Version;
        check("native version matches the facade", version == NativeFormats.ExpectedVersion,
            $"native '{version}' vs managed '{NativeFormats.ExpectedVersion}'");
        uint rev = NativeFormats.AbiRev;
        check("ABI revision matches the facade", rev == NativeFormats.ExpectedAbiRev,
            $"native {rev} vs managed {NativeFormats.ExpectedAbiRev}");
    }

    private static void CheckEchoRoundtrip(CheckFn check)
    {
        var payload = new byte[10 * 1024 * 1024];
        new Random(1252).NextBytes(payload);

        (int status, MfBuffer buffer) = NativeFormats.Echo(payload);
        using (buffer)
        {
            check("a 10 MB echo succeeds", status == NativeMethods.Ok, NativeFormats.LastError);
            check("the echoed copy is byte-identical",
                buffer.ToArray().AsSpan().SequenceEqual(payload));
        }

        (int emptyStatus, MfBuffer empty) = NativeFormats.Echo([]);
        using (empty)
        {
            check("an empty echo yields an empty buffer",
                emptyStatus == NativeMethods.Ok && empty.Length == 0);
        }
    }

    private static unsafe void CheckErrorReporting(CheckFn check)
    {
        int status = NativeMethods.Echo(null, 16, out MfRawBuffer raw);
        check("a null payload with a length is refused", status != NativeMethods.Ok,
            status.ToString());
        check("the refusal carries a readable error",
            NativeFormats.LastError.Contains("mf_echo", StringComparison.Ordinal),
            NativeFormats.LastError);
        check("the failed call hands back no buffer", raw.Data == 0 && raw.Length == 0);

        (int okStatus, MfBuffer buffer) = NativeFormats.Echo([1, 2, 3]);
        using (buffer)
        {
            check("the next success clears the error",
                okStatus == NativeMethods.Ok && NativeFormats.LastError.Length == 0,
                NativeFormats.LastError);
        }
    }

    private static unsafe void CheckDoubleFree(CheckFn check)
    {
        byte[] payload = [1, 2, 3, 4];
        MfRawBuffer raw;
        fixed (byte* p = payload)
        {
            check("the seed allocation succeeds",
                NativeMethods.Echo(p, (ulong)payload.Length, out raw) == NativeMethods.Ok,
                NativeFormats.LastError);
        }

        MfRawBuffer stale = raw;
        check("the first free succeeds", NativeMethods.Free(ref raw) == NativeMethods.Ok,
            NativeFormats.LastError);
        check("the free zeroes the struct", raw.Data == 0 && raw.Length == 0);
        check("freeing the zeroed struct again is a safe no-op",
            NativeMethods.Free(ref raw) == NativeMethods.Ok, NativeFormats.LastError);
        check("a stale pointer is refused", NativeMethods.Free(ref stale) != NativeMethods.Ok);
        check("the refusal explains itself", NativeFormats.LastError.Length > 0,
            NativeFormats.LastError);
    }

    /// <summary>Background streaming loads documents concurrently, so the boundary must be
    /// reentrant: a failure on one thread must stay visible there while other threads succeed.</summary>
    private static void CheckThreadIsolation(CheckFn check)
    {
        const int workers = 4, rounds = 200;
        int violations = 0;

        Parallel.For(0, workers, worker =>
        {
            var payload = new byte[256];
            payload.AsSpan().Fill((byte)worker);
            for (int i = 0; i < rounds; i++)
            {
                FailingEcho();
                if (NativeFormats.LastError.Length == 0)
                {
                    Interlocked.Increment(ref violations);
                }

                (int status, MfBuffer buffer) = NativeFormats.Echo(payload);
                using (buffer)
                {
                    if (status != NativeMethods.Ok || NativeFormats.LastError.Length != 0)
                    {
                        Interlocked.Increment(ref violations);
                    }
                }
            }
        });

        check("thread-local errors stay isolated under concurrency", violations == 0,
            $"{violations} violation(s) across {workers}×{rounds} rounds");
    }

    private static unsafe void FailingEcho() => _ = NativeMethods.Echo(null, 1, out _);

    // ────────────────────────── dual-path core parity ──────────────────────────

    /// <summary>
    /// Native core primitives verified on real game data. FNV-1 32/64 is checked against the
    /// retained C# hash helpers (hashing is a lookup primitive, not a protected decoder, so both
    /// implementations live). The rest is twin-free after the cutover: the XTEA unwrap of genuinely
    /// wrapped archives (including the unaligned-tail population) must yield a loadable archive,
    /// zlib inflate/deflate round-trips, and — when this install carries oodle blocks — the native
    /// oodle shim binds the game's own oo2core.
    /// Output: %TEMP%\illusion_native_core.txt
    /// </summary>
    internal static void RunCoreParityProbe()
    {
        string outFile = Path.Combine(Path.GetTempPath(), "illusion_native_core.txt");
        var sb = new StringBuilder();
        int pass = 0, fail = 0;

        void Check(string name, bool ok, string detail = "")
        {
            if (ok) pass++; else fail++;
            sb.AppendLine($"[{(ok ? "PASS" : "FAIL")}] {name}{(detail == "" ? "" : " — " + detail)}");
        }

        try
        {
            if (!InitEnv(out string? err))
            {
                sb.AppendLine("INIT FAIL: " + err);
                return;
            }

            List<FileInfo> archives = ResourceUnpacker.EnumerateGameSds()
                .Where(f => !f.FullName.Contains(@"\.mafiahub\", StringComparison.OrdinalIgnoreCase))
                .ToList();
            Check("the install offers archives to test against", archives.Count > 0,
                $"{archives.Count} archives");

            CheckFnvParity(Check, archives);
            CheckXteaParity(Check, sb, archives);
            CheckZlibParity(Check, archives);
            CheckOodleParity(Check, sb, archives);
        }
        catch (Exception ex)
        {
            fail++;
            sb.AppendLine("[FAIL] unexpected exception — " + ex);
        }

        sb.Insert(0, $"NATIVE CORE PROBE: {pass} passed, {fail} failed\n\n");
        File.WriteAllText(outFile, sb.ToString());
    }

    private static void CheckFnvParity(CheckFn check, List<FileInfo> archives)
    {
        // The v19 resource-type names plus strings that exercise cp1252's high half —
        // the hash operates on encoded bytes, so the encoding is part of the contract.
        string[] names =
        [
            "Texture", "Mipmap", "IndexBufferPool", "VertexBufferPool", "AnimalTrafficPaths",
            "FrameResource", "Effects", "FrameNameTable", "EntityDataStorage", "PREFAB",
            "ItemDesc", "Actors", "Collisions", "AudioSectors", "SoundTable", "Speech",
            "FxAnimSet", "FxActor", "Cutscene", "Translokator", "Animation2",
            "NAV_AIWORLD_DATA", "NAV_OBJ_DATA", "NAV_HPD_DATA", "Script", "XML", "Sound",
            "MemFile", "Table", "Animated Texture", "tables/fsfh.bin",
            "Straße mit Häusern", "œuvre — «šž»", "",
        ];

        int stringMatches = 0;
        foreach (string name in names)
        {
            byte[] bytes = EndianStreamExtensions.DefaultEncoding.GetBytes(name);
            bool ok32 = Fnv32.Hash(bytes, 0, bytes.Length) == NativeFormats.Fnv32(bytes);
            bool ok64 = Fnv64.Hash(bytes, 0, bytes.Length) == NativeFormats.Fnv64(bytes);
            if (ok32 && ok64) stringMatches++;
        }
        check("fnv32/fnv64 match on every reference string", stringMatches == names.Length,
            $"{stringMatches}/{names.Length}");

        var random = new Random(1946);
        int bufferMatches = 0;
        int[] sizes = [1, 7, 8, 255, 4096, 1 << 20];
        foreach (int size in sizes)
        {
            byte[] buffer = new byte[size];
            random.NextBytes(buffer);
            if (Fnv32.Hash(buffer, 0, buffer.Length) == NativeFormats.Fnv32(buffer)
                && Fnv64.Hash(buffer, 0, buffer.Length) == NativeFormats.Fnv64(buffer))
            {
                bufferMatches++;
            }
        }
        check("fnv32/fnv64 match on random buffers", bufferMatches == sizes.Length,
            $"{bufferMatches}/{sizes.Length}");

        if (archives.Count > 0)
        {
            byte[] real = File.ReadAllBytes(archives[0].FullName);
            check("fnv32/fnv64 match on a real archive's bytes",
                Fnv32.Hash(real, 0, real.Length) == NativeFormats.Fnv32(real)
                && Fnv64.Hash(real, 0, real.Length) == NativeFormats.Fnv64(real),
                $"{archives[0].Name} ({real.Length} bytes)");
        }
    }

    /// <summary>Twin-free: representative wrapped archives (aligned and partial-tail) must
    /// unwrap natively into loadable archives.</summary>
    private static void CheckXteaParity(CheckFn check, StringBuilder sb, List<FileInfo> archives)
    {
        const ulong FsfhMarker = 0x39DD22E69C74EC6F;
        var wrapped = new List<FileInfo>();
        var unaligned = new List<FileInfo>();

        foreach (FileInfo file in archives)
        {
            using FileStream fs = File.OpenRead(file.FullName);
            if (fs.Length < 0x10000 + 16)
            {
                continue;
            }
            fs.Position = 0x90;
            byte[] marker = fs.ReadBytes(15);
            if (Fnv64.Hash(marker, 0, marker.Length) != FsfhMarker)
            {
                continue;
            }
            wrapped.Add(file);
            if ((fs.Length - 0x10000) % 8 != 0)
            {
                unaligned.Add(file);
            }
        }
        sb.AppendLine($"(xtea census: {wrapped.Count} wrapped archives, {unaligned.Count} with a partial tail)");
        check("the install carries XTEA-wrapped archives", wrapped.Count > 0,
            $"{wrapped.Count} wrapped");

        // A representative pair: one aligned, and — critically — one with the partial tail
        // the game leaves undecrypted.
        var candidates = new List<FileInfo>();
        FileInfo? aligned = wrapped.Find(w => (w.Length - 0x10000) % 8 == 0);
        if (aligned is not null) candidates.Add(aligned);
        if (unaligned.Count > 0) candidates.Add(unaligned[0]);
        if (candidates.Count == 0 && wrapped.Count > 0) candidates.Add(wrapped[0]);

        foreach (FileInfo file in candidates)
        {
            byte[] wrappedBytes = File.ReadAllBytes(file.FullName);
            long payloadLength = wrappedBytes.Length - 0x10000;

            // Twin-free: the unwrapped bytes must parse as a full archive — a wrong schedule,
            // a missed partial tail or a corrupted block stream all fail the load.
            bool parses = false;
            try
            {
                byte[] unwrapped = Formats.Native.Archive.NativeSds.Unwrap(wrappedBytes);
                using var stream = new MemoryStream(unwrapped, writable: false);
                parses = unwrapped.Length != wrappedBytes.Length
                    && SdsArchive.Load(stream).Entries.Count > 0;
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or SdsFormatException)
            {
                parses = false;
            }
            check($"native unwrap of {file.Name} yields a loadable archive ({payloadLength} bytes"
                + (payloadLength % 8 != 0 ? ", partial tail)" : ")"), parses);
        }
    }

    private static void CheckZlibParity(CheckFn check, List<FileInfo> archives)
    {
        if (archives.Count == 0)
        {
            return;
        }

        // Real content, capped at 1 MB — representative without being slow.
        byte[] original;
        using (FileStream fs = File.OpenRead(archives[0].FullName))
        {
            original = new byte[Math.Min(fs.Length, 1 << 20)];
            fs.ReadExactly(original);
        }

        using var packedByCSharp = new MemoryStream();
        using (var zlib = new ZLibStream(packedByCSharp, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            zlib.Write(original);
        }

        (int inflateStatus, MfBuffer inflated) = NativeFormats.Inflate(
            packedByCSharp.ToArray(), (ulong)original.Length);
        using (inflated)
        {
            check("native inflate restores a C#-deflated payload",
                inflateStatus == NativeMethods.Ok
                && inflated.ToArray().AsSpan().SequenceEqual(original),
                NativeFormats.LastError);
        }

        (int deflateStatus, MfBuffer deflated) = NativeFormats.Deflate(original);
        using (deflated)
        {
            byte[] packedNative = deflated.ToArray();
            check("native deflate produces a payload", deflateStatus == NativeMethods.Ok
                && packedNative.Length > 0, NativeFormats.LastError);

            using var unpackedByCSharp = new MemoryStream();
            using (var source = new MemoryStream(packedNative))
            using (var zlib = new ZLibStream(source, CompressionMode.Decompress))
            {
                zlib.CopyTo(unpackedByCSharp);
            }
            check("C# inflate restores the native-deflated payload",
                unpackedByCSharp.ToArray().AsSpan().SequenceEqual(original));
        }
    }

    /// <summary>The oodle shim needs the game's own oo2core DLL, which only a Mafia II DE
    /// install carries. Missing pieces SKIP — a classic install is a supported configuration,
    /// not a failure.</summary>
    private static void CheckOodleParity(CheckFn check, StringBuilder sb, List<FileInfo> archives)
    {
        _ = archives;
        string oodleDll = Path.Combine(MafiaEnvironment.PcFolder, "oo2core_8_win64.dll");
        if (!File.Exists(oodleDll))
        {
            sb.AppendLine("[SKIP] oodle bind — no oo2core_8_win64.dll in this install (classic)");
            return;
        }

        check("the native shim binds the game's oo2core",
            NativeFormats.OodleBind(oodleDll) == NativeMethods.Ok, NativeFormats.LastError);
    }
}
