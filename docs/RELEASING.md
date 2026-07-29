# Releasing

A release is a pushed tag. `.github/workflows/release.yml` builds it on a clean Windows runner and
opens a **draft** release with the archive attached; publishing it is the last manual click.

The archive is framework-dependent (~20 MB zipped) and contains only what the toolkit runs on — no
symbols, no documents. The install instructions in the release notes name the two runtimes it needs.

## The release is an interface now

Every installed toolkit reads this release through the GitHub API and can install it over itself
(`src/Illusion/Updates`, gated by `--probe-update`), so three things about a release are contract:

- the tag parses as `major.minor.patch` — the workflow already refuses anything else;
- exactly one attached `.zip` has **`win-x64`** in its name, and it wraps its files in one folder;
- a **`<archive>.sha256`** goes out beside it. A download that does not match it is thrown away
  rather than installed. A release without one still installs — older releases have none — it just
  installs unverified, so do not drop the file.

Editing the draft's notes is fine; nothing reads them. Renaming the archive is not.

## The one thing the workflow cannot do

`vendors/Mafia.Formats.dll` is committed, and the runner uses it as is (`MfCoreMode=Prebuilt`) — it
has no access to the private core. A **Release** build here refreshes that DLL by itself when the
core sources are beside this repository, but **committing it is still on you**: a stale core takes
every format fix since the last release out of the release with it, and once took the release past
a boundary revision, which would have thrown on the first native call.

Nothing else from the core's build folder travels — the vendoring step copies the one DLL, and the
private test binary sitting next to it stays where it is.

## Checklist

1. **Rebuild the core.** With `illusion-core` checked out beside this repository, a Release build
   here drives CMake automatically (Source mode wins when sources are found) and copies the result
   over `vendors/Mafia.Formats.dll` itself:

   ```powershell
   dotnet build Illusion.slnx -c Release
   git status vendors     # did the core actually move?
   ```

   Only a Release build vendors, because that is the binary that ships — a Debug build produces a
   RelWithDebInfo core and deliberately leaves the committed one alone, so day-to-day work cannot
   downgrade a release by accident. `-p:MfVendorCore=false` opts out when building from an
   experimental core.

   Two guards sit under this. A boundary-revision mismatch fails the build outright — `MF_ABI_REV`
   and `ExpectedAbiRev` move together — and `--probe-native` checks that the core actually loaded
   exports every entry point the facade imports, which is the case a revision number cannot see (a
   core that only gains an export may keep its number).

2. **Run the guards** (they need the game installed; each prints its own verdict — read it, the
   probes exit 0 either way):

   ```powershell
   dotnet run --project src/Illusion -- --probe-native
   dotnet run --project src/Illusion -- --probe-native-misc
   dotnet run --project src/Illusion -- --probe-golden check .
   dotnet run --project src/Illusion -- --probe-loadperf
   ```

   And the one guard that is about the release itself rather than the game — `live` also asks the
   real releases page, so it catches an archive named or shaped in a way no installed toolkit can
   use (it needs no game and SKIPs when GitHub is unreachable):

   ```powershell
   dotnet run --project src/Illusion -- --probe-update live
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
