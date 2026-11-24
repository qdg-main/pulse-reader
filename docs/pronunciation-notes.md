# Pronunciation notes

Status: living engineering record for English Kokoro playback. Update this document when listening confirms a pronunciation problem or rules one out.

Pulse Reader uses a static pronunciation dictionary only for terms with one intended spoken form. An optional local classifier handles the approved context-dependent words below. It returns a meaning label, while deterministic code owns the pronunciation change.

## Production dictionary

| Written form | Spoken hint | Reason |
| --- | --- | --- |
| `dotnet` | `dot net` | Kokoro joined the word into an unnatural pronunciation. |
| `JSON` | `Jason` | The phonetic hint produces the normal spoken pronunciation instead of spelling the letters separately. |

Dictionary matching is case-insensitive and limited to whole terms. It applies after URL, path, and filename preparation, so `settings.json` receives the `JSON` pronunciation while `JsonSerializer` remains unchanged.

## Context-dependent production rules

These findings came from listening to the `af_heart` voice in complete sentences. None is safe as a static dictionary entry because each written word has two valid pronunciations.

| Word | Context tested | Observed problem | Applied phoneme case |
| --- | --- | --- | --- |
| `axes` | Woodcutting tools versus graph dimensions | The tools were pronounced like the plural of `axis`. | Cutting tools. |
| `bass` | Musical instrument versus fish | Both uses were pronounced like the fish. | Low-frequency sound, instrument, or audio control. |
| `lead` | Guiding versus the metal | The metal used the guiding pronunciation. | The metal. |
| `wind` | Moving air versus turning something | Both uses were pronounced as moving air. | Turning, wrapping, coiling, or a curving path. |
| `live` | Residing versus real-time activity or broadcast | Real-time uses sometimes received the residing pronunciation. | Real-time, active, connected, or not prerecorded. |

`bow` and `tear` were pronounced correctly in both tested contexts. Keep them as controls if a future context-aware implementation changes the speech preparation pipeline.

## Words ruled out during the first review

Kokoro pronounced `acknowledgement`, `ambiance`, `artefact`, and `verified` correctly in the review sentences. Do not add them to the dictionary unless a different repeatable failure appears.

## Second listening round

Kokoro pronounced the following Misaki missing-word candidates correctly:

- `affordances`
- `analyser`
- `behaviour`
- `catalogue`
- `theatre`

Source: [Misaki issue 58, English missing words](https://github.com/hexgrad/misaki/issues/58).

Kokoro also pronounced these words from an independent TTS pronunciation study correctly:

- `bureaucracy`
- `cashier`
- `courier`
- `discrepancy`
- `exaggerate`
- `expertise`
- `itinerary`
- `subtle`

Source: [Do Text-to-Speech Synthesisers Pronounce Correctly?](https://eprints.soton.ac.uk/264149/1/Do_Text-to-Speech_Synthesisers_Pronounce_Correctly.pdf), Table 2.

The listener did not know the expected pronunciation of `acai`, so it is excluded from both the passing and failing results. It is too uncommon for Pulse Reader to pursue without a real-world report.

Do not build a broad speculative dictionary from external word lists. Prioritize common technical terms, acronyms, product names, and other vocabulary encountered during normal PC use. Add an entry when real Pulse Reader usage exposes a repeatable problem.

## Acceptance policy

Add a static entry only when all of the following are true:

1. The current Kokoro build repeats the wrong pronunciation in a normal sentence.
2. The written term has one intended pronunciation in Pulse Reader's supported usage.
3. The spoken hint fixes the term without changing larger identifiers or unrelated words.
4. An automated whole-term boundary test covers the entry.

Record new context-dependent failures here before expanding the classifier. Add a production rule only after listening confirms the replacement and a balanced classifier corpus covers both meanings.
