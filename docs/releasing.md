# Building, checking, and releasing the package

Preparing this infrastructure does **not** authorize a publication. Ordinary
builds, CI, and package-consumer checks need no Jev credentials and make no Jev
service calls. NuGet dependency restore requires network access. Live service
validation remains a separate, explicitly approved prerequisite for stable
publication. The only current exception is an explicitly approved manual
preview publication acknowledging that live compatibility is unverified.

## Local package check

Use PowerShell 7 on Windows, Linux, or macOS and the .NET SDK selected by
`global.json`: 10.0.400 with `latestPatch` roll-forward. Hosted workflows install
the matching `10.0.4xx` feature band.

From the repository root:

```powershell
dotnet restore ElBruno.AI.Jev.slnx
dotnet build ElBruno.AI.Jev.slnx --configuration Release --no-restore
dotnet format ElBruno.AI.Jev.slnx --verify-no-changes --no-restore

$library = (Resolve-Path .\src\ElBruno.AI.Jev\ElBruno.AI.Jev.csproj).Path
$packages = Join-Path (Join-Path $PWD artifacts) packages
dotnet pack $library --configuration Release --output $packages
.\scripts\Test-Package.ps1 -PackageDirectory $packages
```

`Test-Package.ps1` reads the version from `Directory.Build.props` by default.
For an explicitly selected prerelease, pass the **same** override to pack and
the package check; do not use `--no-build` against assemblies of another version:

```powershell
$version = '0.1.0-preview.1'
dotnet pack $library --configuration Release --output $packages "-p:Version=$version"
.\scripts\Test-Package.ps1 -PackageDirectory $packages -Version $version
```

The check:

- Requires the metadata owner's root README to exist; never creates it.
- Opens the exact `.nupkg` and `.snupkg` as ZIP archives, checking identity,
  version, authors, description, tags, dependency entries, README/icon metadata,
  MIT expression and license file, `lib/net10.0` DLL/XML, portable PDB, and a
  128 x 128 PNG icon below 1 MB.
- Confirms the archive's root `README.md` matches `docs\nuget-readme.md`,
  ignoring UTF-8 BOM and CRLF/LF differences. This preserves the dedicated
  NuGet page instead of accidentally packing the repository README's relative
  hero image. Check downloaded artifacts from their matching release checkout.
- Copies the standalone consumer into a uniquely named directory under
  `tests\ElBruno.AI.Jev.PackageTests\.work`. Its only SDK dependency is an
  **exact-version PackageReference**, never a ProjectReference. Do not add this
  unreleased-package-dependent project to the solution.
- Creates an explicit NuGet configuration with only the local package directory
  and nuget.org; source mapping restricts `ElBruno.AI.Jev` to the local directory.
  Separate package, HTTP, and plugin caches prevent machine-cache false positives.
- Restores, builds, and runs the consumer, checking the packaged assembly's hash,
  assembly/file/informational versions, and matching portable-PDB identity.
  Public API checks cover Choice, fractional Score, Noul, structured state,
  nullable usage, model discovery, and cancellation through a fake
  `HttpMessageHandler` at a reserved `.invalid` host. No actual HTTP handler
  is constructed for service requests.
- Restores the original process environment and deletes only that specific
  generated test-run directory. It never clears machine-wide NuGet caches.

The version policy is SemVer `major.minor.patch[-prerelease]`; numeric
prerelease identifiers cannot have leading zeroes. Build metadata is rejected
because it is not part of NuGet's package-version identity. Assembly and file
versions must be `major.minor.patch.0`; the informational version retains the
full prerelease and may append a source commit after `+`.

### Package-check parameters

| Parameter | Behavior |
| --- | --- |
| `-PackageDirectory` | Existing directory holding both exact-version package artifacts; defaults to `artifacts\packages`. |
| `-Version` | Exact SemVer; defaults to the repository's version property. |
| `-RequireSourceLink` | Requires actual repository metadata, project URL/release notes, immutable commit, and matching Source Link mappings in the symbol PDB. |
| `-ExpectedRepositoryUrl` | Use the confirmed `https://github.com/elbruno/ElBruno.AI.Jev` identity; required with `-RequireSourceLink`. |
| `-UsePublicFeed` | Restores the exact package **only from public nuget.org**, with a new isolated cache for each attempt; still compares its DLL with the validated local artifact. |
| `-IndexAttempts` | Bounded public restore attempts, 1–20; default 10. Local feed checks make one attempt. |
| `-IndexDelaySeconds` | Delay between public restore failures, 1–60 seconds; default 30. |

## Offline tests and coverage

```powershell
$env:JEV_RUN_LIVE = '0'
$tests = (Resolve-Path .\tests\ElBruno.AI.Jev.Tests\ElBruno.AI.Jev.Tests.csproj).Path
$results = Join-Path (Join-Path $PWD artifacts) ("coverage-" + [Guid]::NewGuid().ToString('N'))
dotnet test $tests --configuration Release --no-build --no-restore `
    --settings coverage.runsettings --collect 'XPlat Code Coverage' `
    --logger trx --results-directory $results
.\scripts\Test-Coverage.ps1 -ReportDirectory $results
```

The enforced gates are **90% lines** and **85% branches**.
`coverage.runsettings` measures the library, excludes only its generated
`JevJsonContext` serialization context, and includes handwritten transport,
response parsing, wire DTOs, validation, and Microsoft.Extensions.AI integration.
Do not exclude those implementations to make a gate pass. Automatic-property
coverage is retained.

The coverage script requires exactly one distinct Cobertura report from a dedicated
unit test run. Byte-identical attachment copies made by the TRX logger are
deduplicated by SHA-256; different reports still fail rather than being merged
or silently selected. The script verifies that the library is present and fails
on missing/empty data or either threshold. It uses the raw counts rather than
rounded percentages. `scripts\Test-Coverage.Tests.ps1` exercises these gates.
Optional `-MinimumLineCoverage` and `-MinimumBranchCoverage` arguments are for
diagnostic experimentation; both workflows deliberately use the defaults.
Do not mix integration-test reports or stale runs into that directory.

`ci.yml` runs restore, Release build, formatting/analyzers, offline tests,
coverage, sample compilation, package validation, and the isolated consumer on
Linux and Windows. Every sample also runs with explicit synthetic `--offline`
fixtures. macOS runs a build/unit-test smoke job. Live integration tests remain visibly skipped with
`JEV_RUN_LIVE=0`. Reports and package artifacts are retained with
`actions/upload-artifact@v4`.

## Authorized release workflow

`publish.yml` supports:

1. A **published GitHub release** with a tag such as `v0.1.0-preview.1` or
   `0.1.0-preview.1`; the tag resolves the exact source commit. This runs
   validation only and never automatically publishes to NuGet.
2. **Manual dispatch** with required `version` and `ref` inputs. `version` has
   no `v` prefix. `ref` must be the corresponding version tag, optionally
   prefixed `v`, or a full 40-character commit SHA. Arbitrary moving branch
   names are not accepted. The boolean `approve-preview-publication` defaults
   to **false**, which also means validation only.

**Publication is blocked by default while live compatibility is unverified.**
Only a manual dispatch with `approve-preview-publication=true` and an exact
`major.minor.patch-preview[.identifier...]` version can reach the publishing
job. That input explicitly acknowledges the absence of live validation for
this particular preview; it is not an attestation that live tests passed.
Stable versions and other prerelease channels cannot use this exception.
They remain blocked until actual live-contract evidence supports a separately
reviewed policy change. Creating a GitHub release, approving repository
creation, or leaving the checkbox unchecked does not authorize a NuGet push.

Inputs are validated and passed to shell scripts through environment variables,
not interpolated into executable shell text. The workflow resolves the checkout
to an immutable commit, then runs the Linux/Windows quality, sample, package,
Source Link, and isolated-consumer checks. The release override is applied
consistently during restore, build, pack, and consumer verification.

The public repository is confirmed as
[elbruno/ElBruno.AI.Jev](https://github.com/elbruno/ElBruno.AI.Jev), with
`main` as the default workflow branch. Manual dispatch requires the workflow
to exist on that branch.

Repository/project metadata can use that confirmed URL. The release workflow
still derives its overrides from the real `GITHUB_REPOSITORY` and resolves
an actual checkout commit. Repository creation alone does not establish a
published source revision or valid Source Link evidence.

The protected **`release` environment** additionally gates an approved preview
publication. It is configured to require approval from `elbruno`. All jobs have
`contents: read`; only the publishing job additionally has `id-token: write`.
The job rechecks the downloaded, validated Linux artifacts and exchanges OIDC
through **`NuGet/login@v1`**, with the account name supplied by the
**`NUGET_USER` secret**. No long-lived NuGet API key is needed.

### Existing trusted-publishing policy

The owner has already confirmed an existing NuGet trusted-publishing policy for
**`ElBruno.AI.Jev`**. **Reuse it; do not create a duplicate.** Its exact fields
have not been inspected. Repository creation and the `NUGET_USER=elbruno` repository secret are configured;
the NuGet username was verified from the reference package's public ownership.
Before an authorized publication verify:

- The policy's actual owner/repository match `GITHUB_REPOSITORY`.
- The allowed workflow filename matches **`publish.yml`**.
- Its optional environment restriction agrees with **`release`**.
- The configured `NUGET_USER` matches the existing policy's intended **NuGet account**.
- Its first-publication scope allows the exact package ID `ElBruno.AI.Jev`.

If those existing fields differ, adapt the workflow or obtain approval for the
smallest required policy change before running an authorized release. Also verify
the environment's approval/protection settings in GitHub.

### Publishing and indexing evidence

Global publication concurrency prevents overlapping publication jobs, including
duplicate-version attempts. The workflow pushes the exact validated `.nupkg`;
`dotnet nuget push` also submits its adjacent `.snupkg` by default. Deliberately
do **not** add `--skip-duplicate`: an unexpected duplicate must fail, not appear
to be a successful new publication.

After push, `Test-Package.ps1 -UsePublicFeed` performs up to ten exact-version
public restores, 30 seconds apart, with a fresh isolated package/HTTP cache on
every attempt. A successful restore must also build and run the offline consumer
and match the validated assembly hash. Exhausted retries or consumer failures
fail the workflow; a push alone is not indexing success. The publishing job has
a 25-minute overall deadline, including network restore time.

If indexing verification fails after a successful push, investigate the NuGet
status and rerun **only** the public package check against downloaded validated
artifacts; do not blindly republish the immutable version:

```powershell
.\scripts\Test-Package.ps1 -PackageDirectory $packages -Version $version `
    -UsePublicFeed -IndexAttempts 10 -IndexDelaySeconds 30
```

Package artifacts and test evidence are attached to the workflow run. With
read-only contents permission the workflow intentionally does not upload assets
to, edit, or create a GitHub release. The maintainer can attach the validated
packages and authored release notes to the release separately.

## First version and later compatibility baselines

For the first publication, there is no published public API baseline to compare.
`EnablePackageValidation` still runs SDK package validation during pack, but it
does **not** replace a review of the initial public API. Before authorization:

- Review the public surface/XML documentation and native/MEAI contracts.
- Verify the packaged README, MIT license, icon, dependencies, and version policy.
- On the real repository's release commit, require Source Link verification:
  the consumer checks the PDB identity, document mappings, expected repository,
  and immutable source revision matching the package manifest without downloading source.
- Separately verify that the mapped source is publicly accessible at that commit;
  mapping validation alone does not prove public repository visibility.
- Finish actual coverage gates. For stable publication, finish the explicitly
  authorized live-contract validation; offline consumer success is not
  live-service compatibility evidence. An approved preview must disclose the
  unverified live compatibility rather than claim those tests passed.

After an approved version is published, configure
`PackageValidationBaselineVersion` to the last appropriate published version
for subsequent builds (or pass `-p:PackageValidationBaselineVersion=VERSION`
to a deliberate validation pack). Review API compatibility failures; do not
silence them with blanket suppressions. The repository metadata owner maintains
that baseline, not the standalone consumer.

This consumer is not a trimming or Native AOT certification. Those claims require
separate publish-and-run evidence.
