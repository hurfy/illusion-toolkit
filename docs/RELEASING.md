# Releasing

A release is a pushed tag. `.github/workflows/release.yml` builds it on a clean Windows runner and
opens a **draft** release with the archive attached; publishing it is the last manual click.

The archive is framework-dependent (~20 MB zipped) and contains only what the toolkit runs on — no
symbols, no documents. The install instructions in the release notes name the two runtimes it needs.

## The one thing the workflow cannot do

`vendors/Mafia.Formats.dll` is committed, and the runner uses it as is (`MfCoreMode=Prebuilt`) — it
has no access to the private core. **Refreshing that DLL is a manual step before the tag.** Ship a
stale core and every format fix since the last release silently is not in the release.

## Checklist

1. **Rebuild the core and bring it over.** With `illusion-core` checked out beside this repository,
   a Release build here drives CMake automatically (Source mode wins when sources are found):

   ```powershell
   dotnet build Illusion.slnx -c Release
   Copy-Item ..\illusion-core\src\Mafia.Formats\native-build\release\bin\Mafia.Formats.dll vendors\ -Force
   ```

   A boundary-revision mismatch fails that build with an explicit message — `MF_ABI_REV` and
   `ExpectedAbiRev` move together.

2. **Run the guards** (they need the game installed; each prints its own verdict — read it, the
   probes exit 0 either way):

   ```powershell
   dotnet run --project src/Illusion -- --probe-native
   dotnet run --project src/Illusion -- --probe-native-misc
   dotnet run --project src/Illusion -- --probe-golden check .
   dotnet run --project src/Illusion -- --probe-loadperf
   ```

3. **Build the way the runner will**, so a Prebuilt-only regression is caught here and not in CI:

   ```powershell
   dotnet build Illusion.slnx -c Release -p:MfCoreMode=Prebuilt
   ```

4. **Commit and push** the refreshed DLL along with everything else. CI has to be green on `main`.

5. **Tag and push the tag.** The version comes from the tag alone; `<Version>` in
   `Directory.Build.props` is only the fallback for local builds, though keeping it in step avoids
   confusion:

   ```powershell
   git tag v0.3.0
   git push origin v0.3.0
   ```

6. **Check the draft** on the releases page — the generated commit log, the archive size, the
   SHA256 — then hit Publish.

A dry run is available without tagging: run the workflow manually (`Actions → Release → Run
workflow`) and give it a version. It produces the same draft, which can be deleted afterwards.
