# Changelog

## 0.5.0 - 2026-09-22 (tentative)

First public release, explicitly authorized for early evaluation while live
service testing is deferred. **Live Jev compatibility is unverified.** NuGet
classifies the exact `0.5.0` version as stable, but the package is tentative
and is not a production-readiness claim. APIs may change before 1.0.

- Native .NET 10 client for official TypeSafe AI Jev Choice, Score, and Noul decisions, structured values, typed question handles, model discovery, and native metadata.
- Microsoft.Extensions.AI function tools, explicit chat routing, and input or buffered-output assessments around separately configured chat clients.
- Bounded HTTP transport, safe error messages, cancellation, explicit ownership, and conservative configurable retries.
- Ten runnable samples with explicit synthetic offline mode, deterministic contract/integration tests, and separately gated real-service tests.
- NuGet package validation, symbols, release automation using existing trusted publishing, and original t2i-generated branding.
- README badges for NuGet version/downloads, cross-platform CI, release workflow, .NET, license, and tentative/live-validation status.
- The HTTP user agent follows the SDK assembly version rather than a stale hardcoded development version.

The earlier `0.1.0-preview.1` was an unpublished development version. This
release does not claim semantic correctness or prompt-injection protection.
