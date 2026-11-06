# NuGet Publish Checklist

This checklist captures the remaining preparation work for publishing
`PooledMailKit` to NuGet.

Use it as a release gate, not just as a rough to-do memo.

## 1. Public API Review

- confirm the intended NuGet-facing surface is limited to:
  - `ISmtpSender`
  - `SmtpPoolOptions`
  - `SmtpHostOptions`
  - `SmtpSendResult`
  - `SmtpSendFailedException`
  - `SmtpFailureClassification`
  - `SmtpFailureKind`
  - `SmtpSendStage`
  - `ServiceCollectionExtensions`
- confirm implementation types that should not become extension points stay `internal`
- confirm `README.md`, `site/index.html`, and `docs/design/public-api-proposal.md` match the actual public surface
- decide whether any currently public exception or result types should be reduced further before `1.0`

## 2. Package Metadata

- set or confirm:
  - `PackageId`
  - `Version`
  - `Authors`
  - `Company`
  - `Description`
  - `PackageTags`
  - `RepositoryUrl`
  - `RepositoryType`
  - `PackageProjectUrl`
  - `PackageLicenseExpression` or packaged license file
  - `PackageReadmeFile`
  - `PackageIcon`
- confirm metadata is present in the shipping `.csproj`
- confirm the package name and namespace direction stay aligned

## 3. Pack Output

- enable and verify:
  - `.nupkg`
  - `.snupkg`
  - XML documentation output
  - SourceLink
  - deterministic build settings as appropriate
- include in package:
  - README
  - icon
  - license
- verify `dotnet pack -c Release` succeeds cleanly

## 4. Documentation Readiness

- make README NuGet-consumer-oriented
- keep GitHub Pages and README statements aligned
- confirm option guidance and failure semantics are accurate
- document package limitations honestly
- do not claim operational benefits that are not backed by recorded evidence

## 5. Release Artifacts And Governance

- add or confirm:
  - `LICENSE`
  - `CHANGELOG.md`
  - release notes template or draft
- decide whether `CONTRIBUTING.md` and issue templates are needed before first release
- ensure dated verification records remain linked from the release-facing docs

## 6. Verification Gate

- run and record:
  - `dotnet build C:\dev\MailKit.Pooling\MailKit.Pooling.sln -m:1`
  - `dotnet test C:\dev\MailKit.Pooling\MailKit.Pooling.sln --no-build`
- run build and test serially, not in parallel
- treat `testhost` DLL locks during concurrent build/test execution as orchestration errors, not product regressions
- rerun manual stress when release-risk changes affect:
  - pooling
  - retry
  - timeout
  - reconnect suppression
  - Docker-backed test orchestration
- keep Docker-backed integration and manual stress clearly separated from fast test expectations

## 7. Local Install Verification

- create a clean sample consumer project
- pack locally with `dotnet pack -c Release`
- install from a local folder source
- verify:
  - package restore
  - DI registration
  - `ISmtpSender` usage
  - README rendering in package metadata
  - symbol package generation

## 8. CI And Publish Automation

- add or confirm a build/test/pack workflow
- add or confirm a publish workflow:
  - manual trigger or tag trigger
  - no publish before build/test/pack pass
- use Trusted Publishing through `NuGet/login`, pinned to commit
  `8d196754b4036150537f80ac539e15c2f1028841` (`v1.2.0`)
- grant `contents: read` and `id-token: write` only to the publish job
- request the short-lived key after packing, immediately before pushing
- use `steps.login.outputs.NUGET_API_KEY`; do not fall back to the stored API key
- retain the main-commit guard for both login and push
- configure and verify the following policy in nuget.org Trusted Publishing:

  | Policy field | Required value |
  |---|---|
  | Selected NuGet owner | `commander` |
  | GitHub repository owner | `synthaicode` |
  | GitHub repository | `MailKit.Pooling` |
  | Workflow file | `nuget-publish.yml` (filename only) |
  | Environment | Leave blank; the job does not use a GitHub environment |
  | Package scope | `PooledMailKit`, new versions |

  Initial policy registration, OIDC token exchange and ordinary/symbol package
  publication were verified on 2026-11-06 in [Actions run 42179977503](https://github.com/synthaicode/MailKit.Pooling/actions/runs/42179977503).
  Workflow changes alone do not prove registration. Repeat token exchange and
  publication verification after changing the policy, owner or workflow.

  References: [NuGet Trusted Publishing](https://learn.microsoft.com/en-us/nuget/nuget-org/trusted-publishing)
  and [NuGet/login v1.2.0](https://github.com/NuGet/login/blob/v1.2.0/README.md).

- decide whether publish starts with:
  - dry-run only
  - prerelease package
  - direct public release

## 9. Versioning Policy

- decide whether first publish is:
  - `0.x`
  - `1.0.0`
- decide prerelease usage:
  - none
  - `-alpha`
  - `-beta`
  - `-rc`
- decide the compatibility policy for future public API changes
- align Git tag policy with package version policy

## 10. Release Decision

- review known gaps and confirm they are acceptable for first publish
- confirm the package description matches the actual MVP boundary
- confirm current validation evidence is sufficient for the claims in README and Pages
- make the final go/no-go decision for NuGet publication

## Suggested Execution Order

1. lock the public API
2. finish package metadata and pack settings
3. verify local pack/install flow
4. finalize docs and release notes
5. run release validation in serial order: build -> test -> manual stress as needed
6. enable CI publish flow
7. publish prerelease or first stable package
