# Third-party notices

The root LICENSE covers Pulse Reader's original application code. Third-party
software and data keep their own licenses. Release packages include the license
texts below and notices from the exact NuGet and runtime packs used by the build.
`dependency-inventory.json` records the resolved inventory.
`licenses/source-inventory.json` records the checked-in license sources.

| Component | Version | License and notice files |
| --- | --- | --- |
| Interop.UIAutomationClient | 10.19041.0 | Interop.UIAutomationClient-LICENSE.txt, MIT |
| KokoroSharp and KokoroSharp.CPU | 0.8.4 | KokoroSharp-LICENSE.txt, MIT |
| Kokoro v1.0 voice tensors | 54 voices from KokoroSharp 0.8.4 | Kokoro-Voices-LICENSE.txt, Apache-2.0; original model card and voice attributions remain in voices/ |
| Microsoft.ML.OnnxRuntime and Managed | 1.22.0 | ONNXRuntime-LICENSE.txt, MIT; package ThirdPartyNotices files |
| MisakiSharp, Pulse Reader source frontend | Based on 2.1.1; patch number-and-punctuation-v1 | Apache upstream code; MIT replacement code and number-to-words-LICENSE.txt; MisakiSharp-COMPONENT-NOTICES.txt and MisakiSharp-DATA-NOTICE.txt retain component/data terms |
| NAudio, Asio, Core, Midi, Wasapi, WinForms and WinMM | 2.2.1 | NAudio-LICENSE.txt, MIT |
| NumSharp | 0.30.0 | NumSharp-LICENSE.txt, Apache-2.0 |
| OpenTK.Audio.OpenAL, Core and Mathematics | 5.0-pre.13 | OpenTK-LICENSE.txt and OpenTK-COPYRIGHT-NOTICES.txt, MIT; OpenTK-attribution-review.json |
| UniDic converted data embedded in MisakiSharp | Contemporary Written Japanese 3.1.0 | UniDic-3.1.0-BSD.txt, UniDic-3.1.0-AUTHORS.txt and UniDic-3.1.0-COPYING.txt; BSD option from its triple license |
| wordfreq and direct Wikipedia-derived pronunciation data | Exact generator versions/dump dates unrecorded | MisakiSharp-DATA-NOTICE.txt, wordfreq-NOTICE.md, wordfreq-LICENSE.txt, wordfreq-README.md and CC terms; scoped attribution and adaptation notices |
| System.Numerics.Tensors | 9.0.5 | System.Numerics.Tensors-LICENSE.txt, MIT; package third-party notices |
| System.Speech | 10.0.11 | System.Speech-LICENSE.txt, MIT; package third-party notices |
| .NET and Windows Desktop runtime | Version recorded by self-contained build | Runtime-pack LICENSE and .NET THIRD-PARTY-NOTICES; self-contained payload only |
| Microsoft Visual C++ x64 CRT | Version recorded in native-runtime-manifest.json | Microsoft-VC-Runtime-redistribution.txt and original runtime/Visual Studio Community 2026 licence documents |

## MisakiSharp component lineage and release review

The exact source commit for MisakiSharp 2.1.1 is
`4ec982c8cd8f17d7b0cc6ef69605459ff2ce8a06`. Its full upstream NOTICE is included.
It identifies Apache and MIT components, BSD-licensed UniDic data, English
number logic ported from LGPL-2.1 num2words sources, and GPL-3.0 phonemizer
behavior lineage. The referenced LGPL and GPL license texts are also included.

The upstream English Num2Words class is a concrete LGPL-2.1-or-later source port.
Upstream punctuation Preserve/Restore also has detailed structural
correspondence to GPL-3.0-or-later phonemizer source. The
[code review](docs/licensing-misaki-code-review.md) records exact comparisons and
copyright headers. This is code-lineage evidence, not merely shared phoneme
output. The evidence supports treating those routines as GPL-derived unless
contrary provenance establishes otherwise. A linked DLL is not cleared as an
unrelated aggregate by putting notices beside it.

The modified source frontend removes that number class and the reviewed
punctuation/normalization routines and generated regex helpers from compilation.
It uses the MIT number-to-words source by Martin Eneqvist with C# compatibility
policies, and newly authored source-offset punctuation assembly. The root MIT
licence remains scoped to Pulse Reader's original code; original Apache/MIT
component rights and scoped data terms remain intact. The upstream NOTICE and
LGPL/GPL texts are retained as historical attribution/reference-source material,
not a claim that those removed implementations still ship in the modified DLL.
See [the replacement plan](docs/permissive-g2p-replacement-plan.md) and the source
project's PORTING.md. No formal clean-room or blanket legal clearance is claimed.

The release recipe preserves pinned library and original-lineage sources in a
separate DependencySources ZIP, including the complete modified local project.
All twenty embedded-resource hashes match the stock DLL; the source patch changes
the assembly itself. See
[the source build guide](docs/dependency-source-build.md). This is library source
support, not complete corresponding source for a GPL-distributed combined app.
Missing generator intermediates and the exact environment remain recorded
limits, rather than invented inputs or a claim of full regeneration.

The distributed MisakiSharp.dll embeds twenty data resources, including UniDic's
converted Japanese lexicon, character tables, and compact connection matrix.
The upstream NOTICE's statement that the matrix is fetched separately does not
describe version 2.1.1. The included UniDic BSD and AUTHORS texts come from the
official 3.1.0 release, whose copyright notice is 2011-2021.

Eight pronunciation resources use wordfreq-derived vocabulary; multilingual
resources also retain directly harvested Wikipedia pairs, and Hindi uses
Wikipedia titles/articles. MisakiSharp-DATA-NOTICE.txt identifies affected
resources, modifications, contributors and scoped CC-BY-SA conditions.
Wordfreq's complete notice and README bibliography preserve its underlying
source credits, including freely available SUBTLEX data. The exact generator
version is unknown, but reviewed historical/current wordfreq data blobs agree.
Missing version metadata alone does not require a general permission request.

MisakiSharp-COMPONENT-NOTICES.txt retains 27 original attribution texts/headers,
including previously omitted cn2an and pinyin-to-ipa credits. Chinese converted
data retains CC-CEDICT, Unicode/Unihan and Zdic's declared CC0 terms alongside
the MIT wrappers. Permissive model/tagger notices do not claim the original
weights or full training sentences ship. The
[data review](docs/licensing-pronunciation-data-review.md) distinguishes these
notice requirements from residual output-rights uncertainty. The bounded
source check identified no concrete conflicting output term or protected eSpeak
source expression in the generated resources. The published project declarations
and scoped input notices support retaining their exact bytes. That uncertainty
does not create a new permission prerequisite or establish blanket legal
clearance. Data notices do not relicense unrelated content.

The OpenTK pinned source's LICENSE references THIRD_PARTIES.md, absent at commit
f6652e6c493587fe174d379e9b553176b114b163. A complete-content review of the 107 C#
source files for the three shipped managed libraries found MIT notices and no
additional license text. OpenTK-COPYRIGHT-NOTICES.txt retains the distinct source
copyright notices; OpenTK-attribution-review.json records the paths and hashes.
The restored packages and inspected payload contain managed bindings without an
OpenAL native binary. The narrow missing-reference attribution review is closed
for this inventory; this finding does not certify other payloads or legal status.

The maintainer accepted the replacement listening examples. Public release
remains gated on final package/clean-profile acceptance and release approval.
Microsoft runtime terms are included in the separate distribution agreement,
presented by the installer and on portable first launch. They apply to the four
Microsoft CRT files, not Pulse Reader's MIT code. The maintainer confirmed
individual Community use and acknowledged the publisher distribution condition.
The original Microsoft documents
are retained; the runtime end-user document is not itself the publisher's
redistribution grant. Changes to these notice inputs require rebuilding release
artifacts before distribution. No external permission request has been sent.

## Models and optional contextual pronunciation

The installer and portable packages contain no Kokoro ONNX model, classifier
GGUF model, llama runtime, or user configuration. The Kokoro model is acquired
separately; its upstream license is Apache-2.0 and its model card contains
training-data attributions. The bundled 54 voice tensors and original attribution
files remain part of the application payload.

Optional contextual pronunciation uses Qwen3-4B-Instruct-2507 (Apache-2.0), the
LM Studio community Q4_K_M conversion pinned by the setup workflow, and llama.cpp
b10662 (MIT) with LLVM OpenMP (Apache-2.0 with LLVM Exceptions). The llama and
OpenMP license texts are included for reference. Downloaded optional assets must
also retain their source licenses and model attributions. Evaluation-only models
in developer tooling are not release payloads.

Primary sources: [Kokoro](https://huggingface.co/hexgrad/Kokoro-82M),
[Qwen](https://huggingface.co/Qwen/Qwen3-4B-Instruct-2507),
[MisakiSharp pinned NOTICE](https://github.com/Lyrcaxis/MisakiSharp/blob/4ec982c8cd8f17d7b0cc6ef69605459ff2ce8a06/NOTICE),
[UniDic 3.1.0 license files](https://clrd.ninjal.ac.jp/unidic_archive/cwj/3.1.0/unidic-cwj-3.1.0-full/liceses/),
[wordfreq attribution notice](https://github.com/rspeer/wordfreq/blob/912caf64b657478d1dff1138efdc078947d54bb1/NOTICE.md),
[GNU program-output explanation](https://www.gnu.org/licenses/gpl-faq.en.html#WhatCaseIsOutputGPL),
[llama.cpp b10662](https://github.com/ggml-org/llama.cpp/tree/b10662),
[Microsoft redistribution terms](https://learn.microsoft.com/en-us/cpp/windows/redistributing-visual-cpp-files).
