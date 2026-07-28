# Packaging SharpMUTerm

SharpMUTerm publishes as a **self-contained, single-file** executable — no .NET runtime required on
the target machine.

## Supported runtimes

`linux-x64`, `linux-arm64`, `win-x64`, `osx-x64`, `osx-arm64` (declared in
`src/SharpMUTerm.Tui/SharpMUTerm.Tui.csproj`).

## Local publish

Publish profiles live in `src/SharpMUTerm.Tui/Properties/PublishProfiles/`:

```bash
# Linux x64 → publish output contains a single `sharpmuterm` binary
dotnet publish src/SharpMUTerm.Tui -p:PublishProfile=linux-x64 -o out/linux-x64

# Windows x64 → `sharpmuterm.exe`
dotnet publish src/SharpMUTerm.Tui -p:PublishProfile=win-x64 -o out/win-x64
```

For a RID without a profile, pass the flags directly:

```bash
dotnet publish src/SharpMUTerm.Tui -c Release -r osx-arm64 \
  --self-contained -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true -o out/osx-arm64
```

The profiles enable single-file extraction, compression, and ReadyToRun for faster startup.
These knobs live **only** in the publish profiles, so ordinary `dotnet build` / `dotnet run`
(and CI) are unaffected — they never require a runtime identifier.

## CI / releases

`.github/workflows/release.yml` runs on `v*` tags (and manual dispatch): it publishes
`linux-x64` and `win-x64`, packages them (`.tar.gz` / `.zip`), uploads them as workflow
artifacts, and — on a tag — attaches them to the GitHub release.

```bash
git tag v0.1.0
git push origin v0.1.0
```
