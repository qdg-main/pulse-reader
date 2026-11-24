# Frontend compatibility check

This standalone console test compares two isolated application dependency
folders. It requires a retained stock MisakiSharp 2.1.1 baseline and a freshly
built Pulse Reader replacement. It never launches Pulse Reader or changes the
installed application, settings, models or running playback.

Run from the repository root, supplying full paths to the baseline and candidate
folders:

```powershell
dotnet run --project tests/PulseReader.FrontendCompatibilityTests --configuration Release -- '<baseline folder>' '<candidate folder>' '<repository>/tests/PulseReader.InputSequenceTests/TestData' '<report path>.json'
```

Each folder must contain `PulseReader.dll`, `MisakiSharp.dll`, `KokoroSharp.dll`
and their managed dependencies. The baseline frontend is pinned to SHA-256
`F90EF27EE2D001B15B31270003D2310297847D2B44F7DC2665150F014F9EFE0D`.
Do not rebuild or overwrite the baseline folder with the new project.

The check verifies twenty unchanged resource hashes and absence of the removed
helpers. It compares raw and application-prepared corpus text through both
US/GB phoneme and token paths, plus sample outputs for the seven other supported
languages. A difference or exception prints diagnostics and returns nonzero.
No speech model or audio playback is needed.

Exact corpus matches do not prove compatibility for all possible inputs.
Focused numeric/provider contract fixtures and listening acceptance supplement
this integration check. Their current results are recorded in
`docs/permissive-g2p-replacement-plan.md`.
