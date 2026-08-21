# Etch.Core

The transform engine behind [Etch](https://github.com/HendrikVrey/Etch), packaged so
that sibling tools can run the same chain over their own buffers.

**This package is published to a private feed, not to nuget.org.** Etch is
source-available, not open source, and its licence forbids making any part of the
Software available to a third party — which is what a public package would do, since
every consumer's build output embeds `Etch.Core.dll`. If you want to use it, section
11 of the [licence](https://github.com/HendrikVrey/Etch/blob/master/LICENSE) says to
ask.

## What is in it

- **Format detection** — `Etch.Core.Detection.FormatDetection.Detect` sniffs JSON,
  NDJSON, base64, base64url, hex, URL-encoded text, JWTs, GUIDs, Unix epochs and
  ISO-8601 timestamps, and reports a confidence with the answer.
- **Transforms** — `Etch.Core.Transforms.TransformRegistry.All` is the catalogue.
  Each `ITransform` is a pure, synchronous function from text to text that reports
  bad input as a failed `TransformResult` rather than by throwing, because every
  buffer is untrusted input.
- **Palette ranking** — `Etch.Core.Palette.PaletteRanking` scores transforms against
  a detection result, a query string and a recency list, which is what makes "the
  obvious thing" resolvable to a single keystroke.
- **Text utilities** — `TextFinder` (literal and regex search over a CRLF-normalised
  view), `BraceFolding`, `LineEndings`, `Base64Text`, `Jwt`, `Utf8Position`,
  `LanguageSelector`.

## Rules it holds itself to

No UI reference, no disk, no network, no `PackageReference`, no reflection over the
assembly, and no shared mutable state — the registry hands the same transform
instance to every caller. `IsAotCompatible` is on and the assembly is
trim-and-AOT-clean.

Transforms are synchronous on purpose. A transform is a pure function; an
asynchronous signature would invite an implementation to await I/O and break that.
Callers run them on a background thread and apply the result as one undo group.

## Consuming it

The feed is `https://nuget.pkg.github.com/HendrikVrey/index.json` and it requires
authentication. See `docs/packaging.md` in the Etch repository for how to publish to
it and how to restore from it, including the offline fallback.
