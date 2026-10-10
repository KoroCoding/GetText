# Known Issues

| ID | Severity | Issue | Status | Next |
| --- | --- | --- | --- | --- |
| K-01 | Medium | Plugins run in-process with GetText's privileges; manifest permissions are declarations, not a sandbox | By design (documented) | out-of-process option for untrusted plugins (P2) |
| K-02 | Medium | No cryptographic signature for plugin packages; "Official" is based on the bundled/official index SHA-256 | Open | P2-01 |
| K-03 | Medium | .NET 9 support ends 2026-11 | Open | P3-01 (.NET 10 LTS) |
| K-04 | Low | GitHub Actions pinned to tags, not commit SHAs; `contents: write` at workflow level | Open | P2-04 |
| K-05 | Low | Disabling/uninstalling a plugin takes effect after restart (keeps running until then) | By design | consider unload/close on disable |
| K-06 | Low | Translation overlay can hide the on-frame search highlights | Open | |
| K-07 | Low | macOS helper capture origin may shift when the frame crosses a screen edge (hypothesis, needs a Mac) | Needs device | |
| K-08 | Low | A commit message on `plugin-platform-rc` names the author's private dev repository | Accepted (history rewrite not allowed) | |
