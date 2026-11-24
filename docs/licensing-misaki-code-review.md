# MisakiSharp 2.1.1 code lineage review

This report supports Pulse Reader's release decision for MisakiSharp 2.1.1 at
`4ec982c8cd8f17d7b0cc6ef69605459ff2ce8a06`. It records source evidence and practical
distribution options. It does not select a new license for the packaged product
or declare the full release cleared. Reviewed on 2026-10-12.

Update 2026-10-13: this report describes the original upstream DLL. Pulse Reader
now builds a modified source frontend that removes the reviewed number class and
punctuation/normalization routines. See the [replacement result](permissive-g2p-replacement-plan.md).
The original source and notices remain reference evidence; they are not a claim
that the removed code is still compiled into the modified assembly.

The number converter is an explicit num2words source port. The punctuation
preservation and restoration routines have concrete structural correspondence
to GPL phonemizer source, beyond matching its output. A release assessment should
treat those routines as GPL-derived unless contrary provenance establishes
otherwise. Adding notices and source while describing the linked product as
MIT-only would not resolve that finding. Existing GPL grants provide a possible
compliant distribution route without asking a maintainer for general permission.
Choosing that route changes the combined product's distribution terms and remains
a product decision.

## Exact sources and scope

The restored NuGet package's `misakisharp.nuspec` identifies the exact source
commit above, declares Apache-2.0 and has an empty dependency group for net8.0.
Its DLL SHA-256 is `F90EF27EE2D001B15B31270003D2310297847D2B44F7DC2665150F014F9EFE0D`.
Package metadata alone does not override incorporated code's licenses.

The comparison sources are:

| Source | Pin and evidence |
| --- | --- |
| [MisakiSharp](https://github.com/Lyrcaxis/MisakiSharp/tree/4ec982c8cd8f17d7b0cc6ef69605459ff2ce8a06) | Exact shipped package source commit. Its [NOTICE, lines 11-12](https://github.com/Lyrcaxis/MisakiSharp/blob/4ec982c8cd8f17d7b0cc6ef69605459ff2ce8a06/NOTICE#L11-L12) names the num2words port and phonemizer behavior lineage. |
| [misaki 0.9.4](https://github.com/hexgrad/misaki/tree/fba1236595f2d2bf21d414ba6e57d25256afada3) | Commit `fba1236595f2d2bf21d414ba6e57d25256afada3`, dated 2025-08-11, is the last upstream commit before MisakiSharp's initial 2026-07-23 port. `misaki/__init__.py:1` and `pyproject.toml:6` both declare 0.9.4. This is a pinned comparator, not proof of the maintainer's exact local checkout. |
| [num2words 0.5.14](https://github.com/savoirfairelinux/num2words/tree/6abe2c64714a38e41de23fe20fa319d0ad2fd845) | Tag v0.5.14 resolves to `6abe2c64714a38e41de23fe20fa319d0ad2fd845`. The [misaki lockfile, lines 1218-1227](https://github.com/hexgrad/misaki/blob/fba1236595f2d2bf21d414ba6e57d25256afada3/uv.lock#L1218-L1227) pins its PyPI source distribution. All three compared Python files are byte-identical between that distribution and this commit. |
| [phonemizer-fork 3.3.2](https://pypi.org/project/phonemizer-fork/3.3.2/) | The [misaki lockfile, lines 1506-1520](https://github.com/hexgrad/misaki/blob/fba1236595f2d2bf21d414ba6e57d25256afada3/uv.lock#L1506-L1520) pins this package, rather than bootphon's package. Its source distribution hash was verified. |
| [phonemizer 3.3.0](https://github.com/bootphon/phonemizer/tree/3ed323f0c77797b829092b07a8a4b08ee6f71187) | Tag v3.3.0 resolves to `3ed323f0c77797b829092b07a8a4b08ee6f71187`. Its `phonemizer/punctuation.py` is byte-identical to phonemizer-fork 3.3.2. This supplies pinned GitHub line references for the original routine. |

MisakiSharp history reinforces the declared port. The [initial release commit](https://github.com/Lyrcaxis/MisakiSharp/commit/b20a5b001001933cb95e1ad642458772ddb169fc)
describes a native C# port of misaki 0.9.4. `EspeakG2P.cs` first appears at
[d16193d1df45cbbf6e7a8918756a092eaad9fe0f](https://github.com/Lyrcaxis/MisakiSharp/commit/d16193d1df45cbbf6e7a8918756a092eaad9fe0f),
the 2026-08-08 v2.0.0 release. The file has no later changes through the package
commit. This history does not contain an independent-development account.

## Number converter evidence

[EnglishG2P.Lexicon.cs:323-436](https://github.com/Lyrcaxis/MisakiSharp/blob/4ec982c8cd8f17d7b0cc6ef69605459ff2ce8a06/EnglishG2P.Lexicon.cs#L323-L436)
contains the compiled nested `EnglishG2P.Num2Words` class. Its comments explicitly
identify num2words' split, clean and merge routines. The correspondence is
specific:

| MisakiSharp source | Original source and corresponding structure |
| --- | --- |
| Lines 333-343, card construction | [lang_EU.py:84-101](https://github.com/savoirfairelinux/num2words/blob/6abe2c64714a38e41de23fe20fa319d0ad2fd845/num2words/lang_EU.py#L84-L101) generates the reversed unit/tens combinations with the same uncommon prefixes and centillion. [lang_EN.py:24-56](https://github.com/savoirfairelinux/num2words/blob/6abe2c64714a38e41de23fe20fa319d0ad2fd845/num2words/lang_EN.py#L24-L56) supplies the same descending middle and low card lists and ordinal exceptions. |
| Lines 346-350, Cardinal | [base.py:103-119](https://github.com/savoirfairelinux/num2words/blob/6abe2c64714a38e41de23fe20fa319d0ad2fd845/num2words/base.py#L103-L119) checks sign/range then calls clean(splitnum(value)). |
| Lines 353-366, Ordinal and Year | [lang_EN.py:71-84 and 90-112](https://github.com/savoirfairelinux/num2words/blob/6abe2c64714a38e41de23fe20fa319d0ad2fd845/num2words/lang_EN.py#L71-L112) edits the final hyphen-separated word, changes y to ie before th, and uses the same three-part year exception condition and oh- prefix. |
| Lines 370-400, floating-point reading | [base.py:121-158](https://github.com/savoirfairelinux/num2words/blob/6abe2c64714a38e41de23fe20fa319d0ad2fd845/num2words/base.py#L121-L158) computes the decimal part, uses the same 0.01 rounding tolerance, pads digits and reads them individually. The C# code adds a Python float-representation precision implementation. |
| Lines 402-410, SplitNum | [base.py:68-93](https://github.com/savoirfairelinux/num2words/blob/6abe2c64714a38e41de23fe20fa319d0ad2fd845/num2words/base.py#L68-L93) walks descending cards, special-cases zero, represents quotient one as a tuple, otherwise recurses, then appends a recursively split remainder. |
| Lines 414-425, Clean | [base.py:163-182](https://github.com/savoirfairelinux/num2words/blob/6abe2c64714a38e41de23fe20fa319d0ad2fd845/num2words/base.py#L163-L182) repeatedly folds the first two pairs, retains the remaining list as a nested list, and recursively collapses nested lists. |
| Lines 428-434, Merge | [lang_EN.py:58-69](https://github.com/savoirfairelinux/num2words/blob/6abe2c64714a38e41de23fe20fa319d0ad2fd845/num2words/lang_EN.py#L58-L69) uses the same ordered five branches, numeric comparisons and hyphen/and/space/comma joins. |

This is explicit source-port evidence supported by concrete implementation
structure. It is not an inference from English number behavior. The original
headers in all three Python files state Copyright 2003 Taro Ogawa and Copyright
2013 Savoir-faire Linux inc., and license the code under LGPL **2.1 or later**.
The standalone COPYING text's version heading does not erase that later-version
grant.

## Phonemizer and misaki evidence

The same file includes distinct origins. [MisakiSharp EspeakG2P.cs:15-31 and
167-195](https://github.com/Lyrcaxis/MisakiSharp/blob/4ec982c8cd8f17d7b0cc6ef69605459ff2ce8a06/EspeakG2P.cs#L15-L195)
ports the misaki wrapper's e2m maps, parentheses/guillemet transformations,
ordered English substitutions and British/US branches. Compare [misaki
espeak.py:11-107](https://github.com/hexgrad/misaki/blob/fba1236595f2d2bf21d414ba6e57d25256afada3/misaki/espeak.py#L11-L107).
That wrapper is in misaki's Apache-2.0 repository. Calling a GPL backend from
Python does not, by itself, prove that those maps or wrapper routines copied GPL
source.

The C# punctuation routines implement code that misaki delegates to phonemizer.
Compare [MisakiSharp EspeakG2P.cs:54-113](https://github.com/Lyrcaxis/MisakiSharp/blob/4ec982c8cd8f17d7b0cc6ef69605459ff2ce8a06/EspeakG2P.cs#L54-L113)
with [phonemizer punctuation.py:25-28, 69-75 and
113-220](https://github.com/bootphon/phonemizer/blob/3ed323f0c77797b829092b07a8a4b08ee6f71187/phonemizer/punctuation.py#L25-L220).
Both use the same default punctuation set and surrounding-whitespace regex,
the A/B/E/I position tags, the all-punctuation special case, first/last match
tests and sequential splitting around retained mark strings. The C# `Restore`
specializes the Python algorithm to one input line, `strip=False`, and word
separator space. It preserves the same ordered branches for exhausted marks,
exhausted text, leading/end/alone marks, a final inner mark with one chunk left,
and merging the first two chunks. It also removes then restores the trailing
word separator in the same places. This combination of unusual branch choices
and representation supports code derivation much more strongly than shared
functionality alone. The C# comment expressly names `Punctuation.restore`.

`PostprocessChunk`, lines 105-110, is a reduced form of [phonemizer
espeak.py:114-148](https://github.com/bootphon/phonemizer/blob/3ed323f0c77797b829092b07a8a4b08ee6f71187/phonemizer/backend/espeak/espeak.py#L114-L148)
for misaki's chosen stress/tie/strip settings. It retains flattening newlines,
one double-space replacement, the empty result guard, per-word strip and a
trailing separator. Its flag regex matches
`phonemizer-fork-3.3.2/phonemizer/backend/espeak/language_switch.py:84,150-158`.
These shorter routines alone would support weaker conclusions, but the detailed
punctuation correspondence supplies stronger context.

`EspeakLibrary.TextToPhonemes`, C# lines 139-151, corresponds to
`phonemizer-fork-3.3.2/phonemizer/backend/espeak/wrapper.py:313-366`: UTF-8 input,
the tie-mode bits, repeated pointer-to-pointer calls and joining clause results.
Those bits and the cursor protocol follow eSpeak's API; their match alone would
not prove GPL code derivation. The surrounding C# native-library binding,
locking and voice fallback have implementation differences. This review does
not independently classify every interop statement as copied GPL expression.

The fork's relevant files each retain Copyright 2015-2021 Mathieu Bernard and
the **GPL-3.0-or-later** grant. Its punctuation file SHA-256 is
`E8F87C8F4DCAC4E24CF2243EBD51698EA53D4C2AC2CA2A060324E6F6F84EC374`, identical
to the bootphon v3.3.0 file.

`EspeakReplay` separately describes a measured-output replay design. The code
and its generators do not establish that it copied eSpeak implementation source.
Phoneme output or a measured behavior match is insufficient evidence of that
claim. It still calls the shared punctuation pipeline. The default English
fallback also calls that pipeline, so simply using the replay provider or never
installing native eSpeak does not remove the reviewed routines from the DLL.
No eSpeak executable was downloaded or invoked for this review.

## Distribution choices supported by this evidence

The GNU [compatibility guidance](https://www.gnu.org/licenses/license-compatibility.en.html)
and Apache's [GPL compatibility statement](https://apache.org/licenses/GPL-compatibility.html)
explain a GPLv3 route for Apache-2.0 code. Num2words' later-version grant and
LGPL-2.1 section 3 also provide routes to GPLv3 for its port. GPL-3.0-or-later
phonemizer grants permit compliant modification and redistribution. General
maintainer permission is not an additional prerequisite to using those grants.
The [GNU translated-code explanation](https://www.gnu.org/licenses/gpl-faq.en.html#TranslateCode)
is relevant to a source port. Its separate [program-output explanation](https://www.gnu.org/licenses/gpl-faq.en.html#WhatCaseIsOutputGPL)
does not clear a translated implementation.

There are two practical choices, neither implemented by this report:

1. Distribute the linked program under GPLv3-compatible combined-work terms,
   retaining the MIT grants on original Pulse Reader source and all Apache/MIT
   notices for incorporated components. Supply complete corresponding source and
   build/install instructions for the combined work. Review the whole shipped
   dependency inventory, installer terms and any non-system runtime components
   against that route. A DLL linked into the application is not merely an
   unrelated work placed alongside it. An accompanying archive by itself does
   not satisfy the requirement to convey the combined work under GPL terms.
2. Retain a permissive packaged-product strategy by replacing or removing the
   derived routines and building a reviewed replacement library. Both the
   num2words port and the phonemizer punctuation/restoration pipeline need
   attention. Disabling fallback at runtime leaves their compiled code in the
   shipped DLL. A replacement must come from a suitably licensed implementation
   or independently developed behavior specification, with provenance and
   regression evidence. This is code/dependency work beyond the source/notices
   assignment, and output parity alone would not establish independent origin.

An LGPL-only library strategy becomes relevant if the GPL-derived routines are
actually removed or resolved by contrary evidence. The number port still needs
LGPL-compatible terms for the modified library, corresponding source, required
notices and a working modification/replacement route. Apache-only labeling of
the modified number-port library is not sufficient. Selecting a version-3 route
addresses the Apache compatibility question without pretending the original
headers said LGPL-2.1-only.

## Source and reconstruction materials

The complete pinned MisakiSharp archive is locally sufficient to compile the
library. Include its root C# files, `MisakiSharp.csproj`, all twenty `data/`
resources, `LICENSE`, `NOTICE`, README, generators, tests/fixtures and workflow
instructions in source delivery. Do not substitute only the three Python
reference files for the actual modified C# source. Keep a local copy and hash of
the exact source archive, not only a default-branch link or an upstream URL
whose availability the distributor does not control.

The cache-only reconstruction succeeded with SDK 10.0.400, the installed net8.0
reference pack 8.0.30 and the unmodified project targeting net8.0. Command from
`artifacts/license-source-audit/code`, whose `global.json` pins that SDK:

```powershell
dotnet build MisakiSharp-4ec982c8cd8f17d7b0cc6ef69605459ff2ce8a06/MisakiSharp.csproj -c Release -p:RestoreSources=P:/Code/Pulse-Reader/artifacts/license-source-audit/code -p:NuGetAudit=false --nologo
```

The build reported zero warnings/errors. Reflection confirmed the number class,
EspeakG2P and EspeakFallback in both the package and rebuilt assemblies. All
twenty embedded resource streams matched SHA-256 between the assemblies.
The rebuilt DLL hash was
`187A4B36C64B1667A012F9181230B054990E10F498AE82E1065574B9FC0175FB`; it differs
from the NuGet DLL. This proves source compilation and resource recovery, not
byte-for-byte reproduction of the maintainer's build. The upstream workflow
uses .NET SDK 8.0.x without an exact compiler pin. The initial SDK 10.0.401
offline attempt requested uninstalled reference pack 8.0.31; pinning the already
installed 10.0.400 SDK resolved this without a dependency install.

For the GPL route, corresponding-source delivery must also cover Pulse Reader,
KokoroSharp and other non-system linked components as applicable, their exact
patches, dependency resolution, build scripts and installer configuration.
Demonstrate rebuilding and substituting the library with a compatible interface
in the delivered layout. DLL/source availability does not alone prove this.
Document any installation information required by GPLv3 section 6 for the
actual distribution; do not assume signing or installer restrictions are absent.

The pinned archive builds from the existing resource files, but it does not
prove every resource can be regenerated from preferred editing inputs.
`generators/generate_espeak_emit.py:13,40-62` expects per-language word, bigram and
LTS dump files in a maintainer-local `models_notpushed/espeak_dump` path.
`generate_espeak_en.py:130` reads `en_rule_wordlist.txt` there.
The generator sources and final resources are included; those intermediate
inputs and a fully pinned generator environment are not all in this repository.
If full data regeneration or their preferred-source status is required, review
those missing inputs together with the separate wordfreq/data provenance audit.
Do not equate successful DLL compilation with resolution of that issue.

## Remaining questions and contact threshold

The broad questions about the number port's origin, upstream later-version
grants, and punctuation code correspondence are answerable from public source.
No maintainer contact is needed to establish those facts or obtain general
permission for a GPL-compliant route. Choosing the product distribution strategy
and completing its source/replacement qualification remain release decisions.

The source does not establish the maintainer's exact local Python checkout or
generator dependency environment, nor does it prove a complete independently
developed origin for the punctuation routines. Those uncertainties do not erase
the concrete correspondence. Only if a permissive strategy depends on contrary
provenance would a narrowly framed provenance inquiry become relevant. Exact
missing generator inputs may also warrant an inquiry after the data audit
exhausts public sources. This report does not recommend opening a general
permission request or issue, and none was sent.

Audit archives, per-resource comparison hashes and reconstruction results are recorded in
`artifacts/license-source-audit/code/provenance.json`. The cache and report are
research materials; release source packaging is handled separately.
