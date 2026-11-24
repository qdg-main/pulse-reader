# Microsoft CRT distribution review, 13 October 2026

Implementation status: the maintainer confirmed individual development and
approved installer agreement plus a first-launch portable agreement. The
maintainer also acknowledged the publisher indemnity/defence condition on
13 October 2026. Package generation now preserves the full runtime text,
MIT licence and scoped distribution introduction; the original DOCX files
remain included. This report's initial gap findings describe the pre-change
package, not the final acceptance result. Validation is recorded in releasing.md.

This read-only review supports the maintainer's 1.0.0 package decision. The documented grant covers the four unchanged release CRT DLLs. Two remaining matters are the publisher's qualifying Visual Studio use and the release's end-user/distributor agreement flow. The review does not establish contract enforceability or grant permission to publish.

## Exact primary sources and provenance

Microsoft's current [licence directory](https://visualstudio.microsoft.com/license-terms/) links to the [Community 2026 page](https://visualstudio.microsoft.com/license-terms/vs2026-ga-community/) and [Visual C++ V14 runtime page](https://visualstudio.microsoft.com/license-terms/vs2026-ga-visualcpp-v14-redist-runtime/). Their embedded documents resolve to these exact originals:

| Preserved original | Current official download | SHA-256, same online and local |
| --- | --- | --- |
| `licenses/Microsoft-Visual-Studio-2026-Community-LICENSE.docx` | [Community DOCX](https://visualstudio.microsoft.com/wp-content/uploads/2025/10/Visual_Studio_2026-License-Community_ENU.docx) | `02AC07637F4A513A807CD3721C7620433FC483BC049D25784B0C10C54B05E1FB` |
| `licenses/Microsoft-VC-Runtime-LICENSE.docx` | [Runtime DOCX](https://visualstudio.microsoft.com/wp-content/uploads/2025/10/Visual-C-V14-License-Redistributable_and_Runtime_ENU.docx) | `08651651A7602FC7C0E2763DE0FDE1FF9F868DF2780597CD1775EE9D6441C783` |

Both originals say last updated October 1, 2025. Current official downloads were read into memory and hashed; no Office program or licence content was executed. Word XML was read through .NET ZipArchive and XML APIs.

Paragraph locators below count every `w:p` under `w:body` in `word/document.xml`, including empty paragraphs. They are stable against the exact hashes above and avoid pagination differences between Word versions. Section names and numbers refer to the rendered document.

`Microsoft-VC-Runtime-derived-plaintext.txt` retains the runtime's original text nodes in document order, separated by blank lines. It has 51 source paragraphs and SHA-256 `441867162C8327509A08A9C626A5D1C2DF2544A7859DF0AEB94CD22229208869`. Word automatic numbering and layout are omitted. No substantive words or regional clauses were rewritten. Keep the original DOCX alongside any derived display text.

## Supplying installation and files

Read-only `vswhere` inspection reports Community `18.9.12112.369`, `IsPrerelease: False`, channel `VisualStudio.18.Release`, at `C:/Program Files/Microsoft Visual Studio/18/Community`.

The exact source directory is `VC/Redist/MSVC/14.51.36231/x64/Microsoft.VC145.CRT`. All four source hashes match Candidate 11's standard payload manifest. The payload files have valid Microsoft signatures and version `14.51.36247.0`. The directory version and DLL version differ; the bytes agree.

The [2026 Distributable Code list](https://learn.microsoft.com/en-us/visualstudio/releases/2026/redistribution), under "Distributable Code Files" and "Visual C++ Runtime Files", expressly includes Community. It covers unchanged files under `VC/redist`, with debug and prerelease exclusions. These four files fit that listed category. The grant remains conditional on the supplying licence. Microsoft also [documents application-local DLL deployment](https://learn.microsoft.com/en-us/cpp/windows/redistributing-visual-cpp-files), while recommending central deployment for servicing. Keeping application-local files is a supported packaging route; future CRT updates require new app packages.

## Supplying Community clauses

The [exact Community document](https://visualstudio.microsoft.com/wp-content/uploads/2025/10/Visual_Studio_2026-License-Community_ENU.docx) contains these applicable conditions:

| Locator | Condition and present evidence |
| --- | --- |
| Section 2(b), paragraph 12 | Individuals may develop/test their own applications, including commercial ones. Publisher capacity remains a factual question. |
| Section 2(c)(i), paragraphs 13-15 | Organizations may use Community for applications released under OSI-approved licences. MIT is the selected app-source licence. |
| Section 2(c), paragraphs 20-23 | Other organizational uses have five-user/nonenterprise limits. These limits do not displace the open-source exception. |
| Section 8(a)(i), paragraph 53 | Listed object code may be copied/distributed. The CRT category is listed. |
| Section 8(a)(iii), paragraph 55 | Authorized downstream distributors may redistribute code as part of the application. |
| Section 8(b)(i), paragraph 57 | Add significant primary functionality. Pulse Reader's reading/speech application supplies it. |
| Section 8(b)(ii), paragraph 58 | Obtain protective distributor/end-user terms. MIT-only installer assent does not evidence this. |
| Section 8(b)(iii), paragraph 59 | Publisher indemnification/defence covers app-related claims, except claims based solely on Distributable Code. |
| Section 8(c), paragraphs 61-64 | No prerelease/Insiders distribution, misleading Microsoft branding, or prohibited source relicensing. Current files are release binaries; scoped MIT avoids relicensing them. |
| Scope and protections, paragraphs 72-78, 81-85, 88-98 | Preserve software restrictions, notices, trade-law duties, mandatory regional rights, Microsoft warranty/damages protections. |

The organizational route should be evaluated against the actual publisher and project. An enterprise-size question is unnecessary when the documented individual-own-application or qualifying open-source route applies.

## Runtime terms and required package change

The [original runtime document](https://visualstudio.microsoft.com/wp-content/uploads/2025/10/Visual-C-V14-License-Redistributable_and_Runtime_ENU.docx) governs end-user installation/use. Paragraph 6 accepts through use; paragraphs 13-19 reserve rights and restrict circumvention, reverse engineering, notice removal and sharing/distribution. Paragraphs 20-39 retain trade, regional, warranty and damages conditions. It grants no publisher redistribution entitlement. Preserve all of it, including French and regional material.

`installer/PulseReader.iss:37` currently uses `LicenseFile={#PayloadDir}\LICENSE`, which displays only the root MIT licence. The runtime DOCX and redistribution notice are included in payloads but are not currently presented as a scoped agreement before installation/use.

Recommended implementation for the parent to review:

1. Add a separate package agreement/display file. Identify exactly `msvcp140.dll`, `msvcp140_1.dll`, `vcruntime140.dll`, and `vcruntime140_1.dll` as Microsoft code governed by the preserved runtime terms. Include the unchanged runtime text and root MIT text. State that Microsoft's terms apply to those files and do not replace rights in Pulse Reader's original source or other components.
2. Point the installer licence page at that combined display file. Keep both original DOCX documents in the payload and retain their source hashes.
3. Add a first-launch agreement for portable packages before using CRT-backed speech/model setup. Present the same scoped text with accept/decline; record the accepted text/version under user settings. This provides stronger evidence than relying solely on a public download-page statement. Avoid duplicating the prompt where the installer already established the same user's agreement. A download/use notice alone can disclose terms, but this review cannot establish that every download channel obtains agreement.
4. Include distributor terms limited to bundled Microsoft DLLs. Preserve an authorized downstream distribution route under Section 8(a)(iii), unchanged files/notices, significant app functionality, agreement by subsequent users/distributors, applicable distribution restrictions and Microsoft protections. The standalone runtime prohibition does not by itself grant downstream redistribution. Do not impose a new restrictive licence on MIT source or make every end user accept the publisher's indemnity.
5. Require the derived display agreement in payload verification, rebuild both flavours, and test acceptance/decline and portable launch. Keep release readiness false until the publisher confirmation and these checks are complete.

This is an implementation recommendation based on the documented conditions. Microsoft does not provide a prescribed Pulse Reader agreement template or say that a particular checkbox guarantees the required degree of protection. Retaining its exact runtime protections and an explicit scoped distributor agreement is a reasonable way to address the concrete gap. No source identified a prerequisite to contact Microsoft, a library maintainer, or a lawyer.

## Necessary publisher confirmation

The repository cannot establish whether Quantum Dot Games is the individual's trading name or a distinct publishing organization, or the publisher's acceptance of the distribution obligation. A concise question is sufficient:

"Are you publishing Pulse Reader as your own application under your individual Community licence, or as an organization using Community for this MIT open-source project, and do you accept the publisher indemnity/defence obligation in Community Section 8(b)(iii)?"

This confirmation concerns the publisher's factual entitlement and chosen obligation. It is separate from user consent to the four runtime DLLs. No independent nonenterprise requirement follows when the open-source exception applies.

## Validation and limits

Verified the live official list and licence-directory links; matched both official DOCX hashes; extracted and checked original runtime text; inspected installer licence routing; compared the four CRT manifest, payload and installed-source hashes; checked stable Community metadata and valid payload signatures. No shipping documents, source inventory, app files or installer definitions were edited. No installation, app restart, protection change, publication or external contact occurred. Parent owns final contract wording, clean-profile checks and release approval.
