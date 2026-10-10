# Implementation Matrix

Backlog for the "GetText — Next Generation" plan. Read this first in every session and continue from the next unfinished task.

- Status: `NOT STARTED` / `IN PROGRESS` / `IMPLEMENTED` / `TESTED` / `VERIFIED` / `BLOCKED`
- Baseline (2026-10-11): `main` = `979b6da` (v1.0.0 line), `plugin-platform-rc` = `f007bcb` (Windows / Mac CI green). Work branch: `next-gen` (from `plugin-platform-rc`, local until the security gate passes).
- Existing state classification (PHASE 0): **IMPLEMENTED** = in `plugin-platform-rc` and covered by tests/CI, **PARTIAL** = some of the asked scope exists, **MISSING** = nothing yet.
- `VERIFIED` requires real-device manual testing as well (docs/release/RC_MANUAL_TEST.md). Nothing below is `VERIFIED` yet: the manual RC tests are still `NOT TESTED`.

## PHASE 0 — Inventory of the current code

| ID | Feature | Baseline | Notes |
| --- | --- | --- | --- |
| B-01 | Windows WPF app / macOS Avalonia + Swift helper | IMPLEMENTED | .NET 9 (end of support 2026-11) |
| B-02 | DesignTokens / icon registry / Home / Command Palette | IMPLEMENTED | |
| B-03 | AI OCR (RapidOCR + PP-OCRv6) / OS OCR (Windows OCR, Vision) | IMPLEMENTED | |
| B-04 | Ctrl+F search + on-screen highlights (word/char geometry, no fabricated positions) | IMPLEMENTED | |
| B-05 | Layout OCR (frames / columns) | IMPLEMENTED | merged-row fix in `fbb333c` |
| B-06 | Screen translation (local FuguMT / NLLB, optional Google / DeepL) | IMPLEMENTED | |
| B-07 | Translation Overlay (core, View ▾) | IMPLEMENTED | moved from plugin in `fbb333c` |
| B-08 | Meeting transcription (minutes) / screen recording | IMPLEMENTED | |
| B-09 | Quick OCR | IMPLEMENTED | |
| B-10 | Plugin API v1 / Plugin Manager / Safe Mode / rollback | IMPLEMENTED | in-process, same privileges (not a sandbox) |
| B-11 | Model Manager (per-model install/remove) | IMPLEMENTED | |
| B-12 | Developer API (127.0.0.1, token, off by default) | IMPLEMENTED | |
| B-13 | Per-window topmost | IMPLEMENTED | |
| B-14 | Official plugins: Keyword Monitor, Presentation Capture, OCR History, Document OCR, QR/Barcode | IMPLEMENTED | 5 plugins (Translation Overlay is core) |
| B-15 | Plugin signature verification | MISSING | trust = SHA-256 from bundled/official index only |
| B-16 | Installer / auto update | PARTIAL | ZIP + setup.bat / update.ps1; no installer, no signed auto-update |
| B-17 | Localization (resources) | MISSING | UI strings are Japanese literals |

## Backlog

| ID | Priority | Feature | Status | Files | Tests | Commit | Blocker | Next Action |
| --- | --- | --- | --- | --- | --- | --- | --- | --- |
| P1-01 | P0 | Public repository privacy & secret audit (all refs, history, media, releases, CI) | IN PROGRESS | docs/security/PUBLIC_AUDIT.md | scan script (local) | | Release assets / Actions logs / artifacts need download or login | finish report |
| P2-01 | P0 | Plugin package signing (official publisher key, signature in package, verify before "Official/Verified") | NOT STARTED | PluginHost/* | PluginPlatformTests | | key custody decision (user) | design + verification code, test key only |
| P2-02 | P1 | Developer API hardening review (rate limit, token storage DPAPI/Keychain) | NOT STARTED | DeveloperApi.cs | DeveloperApiTests | | | audit + fixes |
| P2-03 | P1 | Network egress inventory + UI disclosure | PARTIAL | Settings → Privacy | | | | document every path |
| P2-04 | P1 | Supply chain: pin Actions to SHA, least-privilege permissions, SBOM, artifact attestation | NOT STARTED | .github/workflows | CI | | needs push to test CI | |
| P3-01 | P0 | .NET 10 LTS migration (Windows, Mac, SDK, plugins, tests, CI) | NOT STARTED | *.csproj, workflows | all | | .NET 10 SDK on this PC / CI | dedicated branch |
| P3-02 | P1 | Core-only / Recommended / Custom install | PARTIAL | setup.ps1, SetupDialog | | | | |
| P3-03 | P1 | Migration tests from old user environments | PARTIAL | SettingsMigrationTests | | | | |
| P4-01 | P1 | Interactive OCR: select/copy text on the source image, edit, confidence, candidates | NOT STARTED | | | | | |
| P4-02 | P1 | Search: regex, fuzzy, Unicode normalization, keep position across updates | PARTIAL | TextSearch.cs | OcrLayoutTests | | | regex + NFKC |
| P4-03 | P1 | Structured OCR modes (Plain / Paragraph / Multi-column / Layout / Table / Code / Vertical / Raw) | PARTIAL | OcrLayout.cs | | | | |
| P4-04 | P2 | Code OCR mode (indentation, symbols, copy as code) | NOT STARTED | | | | | |
| P4-05 | P3 | Math OCR plugin (LaTeX) | NOT STARTED | | | | model license | |
| P4-06 | P2 | Table OCR (cells, CSV/TSV/Markdown/JSON/XLSX) | NOT STARTED | | | | | |
| P5-01 | P2 | Document OCR: more formats, preprocessing, preview, selective re-OCR | PARTIAL | plugins/GetText.Plugin.DocumentOcr | DocumentOcrTests | | | |
| P5-02 | P2 | Searchable PDF / DOCX / CSV export | NOT STARTED | | | | | |
| P6-01 | P2 | Screenshot plugin (capture, pin, annotate, safe redaction) | NOT STARTED | | | | | |
| P7-01 | P2 | Translation: selected/clipboard text, glossary, history, per-app profiles | PARTIAL | Translator.cs | | | | |
| P8-01 | P2 | Presentation: slide list, reorder, OCR text, PDF/Markdown/JSON export, meeting link | PARTIAL | plugins/GetText.Plugin.Presentation | PresentationCaptureTests | | | |
| P9-01 | P2 | Meeting: action items/decisions with source timestamps, chapters | PARTIAL | Minutes* | | | | |
| P9-02 | P3 | Live subtitles plugin / dictation | NOT STARTED | | | | | |
| P10-01 | P2 | OCR History on SQLite (FTS, paging, filters, timeline) | NOT STARTED | plugins/GetText.Plugin.History | HistoryTests | | dependency choice (no big deps) | |
| P10-02 | P3 | Semantic search (local embeddings) | NOT STARTED | | | | model download | |
| P11-01 | P3 | Workflow automation | NOT STARTED | | | | | |
| P11-02 | P2 | Plugin template + CLI, contract tests | PARTIAL | plugins/GetText.Plugin.Sample, docs/plugins | | | | |
| P11-03 | P2 | OCR / batch CLI (name must not clash with GNU gettext) | NOT STARTED | | | | | |
| P11-04 | P3 | Optional MCP server plugin | NOT STARTED | | | | | |
| P12-01 | P2 | Windows installer / portable; macOS DMG, signing/notarization prep | NOT STARTED | tools/, mac/packaging | | | signing certs | |
| P12-02 | P2 | Auto update with hash/signature check and rollback | NOT STARTED | update.ps1 | | | depends on P2-01 | |
| P12-03 | P1 | Localization: resources, Japanese + English UI | NOT STARTED | | | | large | |
| P13-01 | P1 | Performance baseline (startup, idle CPU/RAM, OCR latency) | NOT STARTED | | | | | |
| P14-01 | P1 | README (English first, Japanese page), CONTRIBUTING, SECURITY, CHANGELOG | PARTIAL | README.md | | | | |
| P14-02 | P2 | Reproducible benchmarks (OCR / layout accuracy) | NOT STARTED | | | | | |
| P15-01 | P0 | Release candidate validation (manual tests) | BLOCKED | docs/release/RC_MANUAL_TEST.md | | | real-device tests by the user | |
