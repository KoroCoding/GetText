# Test Results

| Date | Commit | Scope | Result | Evidence |
| --- | --- | --- | --- | --- |
| 2026-10-09 | f007bcb | Windows CI: unit tests, plugin packages, screens, UI self-test | PASS (492 tests, 5 plugins, 118 screenshots) | Actions run on `plugin-platform-rc` |
| 2026-10-09 | f007bcb | Mac CI: arm64/x64 build, helper, UI self-test (43), snapshots, smoke, x64 run | PASS | `ci/mac-results` summary.txt |
| 2026-10-09 | fbb333c (dev) | Real OCR on a roster-slide image (Windows OCR + AI OCR), grouping | PASS (20 boxes, 0 mixed) | local scratch test (not committed) |
| 2026-10-11 | 979b6da..f007bcb | Public history secret/PII scan | PASS (0 secrets, 0 PII) | docs/security/PUBLIC_AUDIT.md |
| 2026-10-11 | 5abc6af | Windows CI on .NET 10: unit tests, plugins, screens, UI self-test | PASS (515 tests, 5 plugins, 118 screenshots) | Actions run 38092170122 |
| 2026-10-11 | 5abc6af | Mac CI on .NET 10: arm64/x64, helper, UI, snapshots, smoke, x64 run | PASS | `ci/mac-results` |
| 2026-10-11 | 4d93c91 | Local unit tests (.NET 9 SDK) | PASS except 16 tests blocked by Smart App Control (rebuilt plugin DLLs, 0x800711C7) | CI is the record |
| — | — | Manual RC tests (docs/release/RC_MANUAL_TEST.md) | NOT TESTED | real-device tests pending |
