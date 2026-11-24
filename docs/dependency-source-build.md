# MisakiSharp source and rebuilding

The release recipe creates a separate `PulseReader-<version>-DependencySources.zip`
asset. Retain it locally and deliver it alongside any approved release using
this library. An upstream link alone is not the distributor's retained source.
Run the source-bundle and release tools with PowerShell 7.4 or later. Their host
runtime must provide the hash and stream APIs; installing a newer SDK alone does
not update an older PowerShell host.
The bundle includes the complete pinned MisakiSharp source archive, its embedded
resources and generators, pinned original code-lineage archives, licence texts,
review reports and this guide. No speech models or user data are included.

The current bundle also includes `local-frontend/src/MisakiSharp.PulseReader`,
the complete modified project actually used by Pulse Reader. Its project targets
.NET 10 Windows and retains the original twenty data resources. Build that local
project for a replacement DLL; the upstream archive below is a historical
reference, not the current product implementation. The source inventory records
the patch marker, source hashes and per-build DLL hash.

This is **library source support**, not a claim that the combined application
has fulfilled GPL corresponding-source obligations. Public distribution remains
blocked until final package/listening acceptance and distribution conditions are met. See the
[code review](licensing-misaki-code-review.md) and
[data review](licensing-pronunciation-data-review.md).

## Verify and build the current library

Check `source-files.json` in the extracted source bundle. It records the complete
modified source snapshot and the retained notice files. Install the .NET 10 SDK,
then run from the bundle root:

```powershell
dotnet build ./local-frontend/src/MisakiSharp.PulseReader/MisakiSharp.PulseReader.csproj --configuration Release
```

The output is `local-frontend/src/MisakiSharp.PulseReader/bin/Release/net10.0-windows/MisakiSharp.dll`.
No speech model, eSpeak installation or additional runtime package is needed
to build this resource-backed frontend. `misaki-frontend-build.json` in each
application payload records its actual DLL and resource hashes; the checked-in
source contract records the patch marker and required/removed helper metadata.

## Rebuild the historical upstream reference

Check the archived files against `licenses/dependency-source-manifest.json`
before extraction. Extract `archives/MisakiSharp-4ec982c.zip` into a new folder.
It contains `MisakiSharp-4ec982c8cd8f17d7b0cc6ef69605459ff2ce8a06`.

The unmodified project targets .NET 8.0 and has no NuGet package dependencies.
Install a suitable .NET SDK and reference pack if they are not already present,
then run:

```powershell
dotnet build ./MisakiSharp-4ec982c8cd8f17d7b0cc6ef69605459ff2ce8a06/MisakiSharp.csproj --configuration Release
```

The output is `bin/Release/net8.0/MisakiSharp.dll`. No eSpeak installation or
model download is needed to compile the shipped resource-backed library.
Building its data anew is a separate operation; some upstream generator inputs
and exact environments are not recorded in the repository. Do not treat
successful compilation as proof that every resource's generating source is
complete.

The 12 October 2026 review built with SDK 10.0.400 and the installed .NET 8.0.30
reference pack. All twenty embedded resources matched the NuGet DLL by SHA-256.
The rebuilt assembly itself was not byte-identical; upstream pins only SDK
8.0.x, not the exact original compiler. The code review records the hashes.

## Test a replacement safely

Close Pulse Reader first. Use a **disposable Windows profile or VM** with a new
extracted portable copy, not an existing installation or the user's normal
profile. A portable copy alone still uses the same user-data directory and does
not isolate settings. Keep the
original `MisakiSharp.dll` as a recoverable backup outside that copy, and replace
only that DLL with your build. Preserve the public API and assembly identity
required by KokoroSharp. Run pronunciation, fallback and ordinary playback
regressions before adopting it. The current replacement has automated phoneme,
token and resource comparisons plus focused audio samples; these are not a
guarantee for every unseen input.

There is no installer-enforced signature check on this managed library. Source
and a replaceable file layout do not by themselves settle the library's licence
terms or the linked program's GPL obligations. A GPL combined-work route also
needs the matching Pulse Reader and other applicable non-system component
sources, patches, dependency resolution, installation information and build
instructions. Those are not supplied by this dependency-only asset.

## Build the source asset

```powershell
./tools/build-dependency-sources.ps1 -OutputRoot ./artifacts/source-bundle-check
```

The default source cache is `artifacts/license-source-audit/code`. Missing source
archives stop the build. Add `-DownloadMissing` to retrieve only the pinned
public source archives. Every copied or downloaded archive must match its
checked-in SHA-256. Existing output directories are never overwritten. The
release recipe also checks that its packaged DLL matches the source manifest.
