# Pulse Reader frontend modifications

Based on MisakiSharp 2.1.1, source commit
`4ec982c8cd8f17d7b0cc6ef69605459ff2ce8a06`, Copyright 2026 Lyrcaxis.
The original Apache licence and NOTICE are retained verbatim. UPSTREAM-README
records the original distribution, not claims about this modified build.

Pulse Reader removes the num2words-derived nested number class and the
phonemizer-derived punctuation preservation/restoration/normalization routines.
Their generated regex helpers are also removed. Replacement number conversion
ports the pinned MIT number-to-words implementation by Martin Eneqvist and
adds observed British wording, year and fractional policies. Replacement
punctuation uses source-offset spans and a forward assembler. No formal
clean-room process or blanket legal clearance is claimed.

Existing frontend/tagger code, IPA mappings, Replay implementations and all
twenty resource files are preserved. The untouched native/process provider APIs
follow the eSpeak API and were not identified as concrete protected-source ports
in the code review. Normal Pulse Reader playback uses the managed Replay path.
The project targets the same .NET runtime as Pulse Reader and preserves the
MisakiSharp assembly identity expected by KokoroSharp.

The original NOTICE mentions removed components as historical origins. See the
repository's THIRD-PARTY-NOTICES and source inventory for the modified build's
actual code and data terms. Generated data keeps the applicable original and
scoped ShareAlike notices; it is not all newly licensed MIT.
