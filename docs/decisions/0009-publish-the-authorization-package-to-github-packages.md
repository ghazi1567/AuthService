# 0009. Publish the authorization package to GitHub Packages from a version tag

- Status: Accepted
- Date: 2026-11-03
- Tickets: AUTH-204 (PLT-07), AUTH-31 (TOK-03)

## Context

Products enforce Auth Service tokens with the `AuthService.Authorization` package. It built and packed, but a product outside this
repository could only use it by referencing the project or copying files, so there was no way to give a product a version, to fix one
without touching the others, or to say what changed.

A package that products trust to check tokens must come from one place, be built by a machine nobody can quietly change, and be
tested before it reaches anyone.

## Decision

- **The feed is this repository's GitHub Packages feed** (`https://nuget.pkg.github.com/<owner>/index.json`).
- **A release is a tag**: `authorization-v<major>.<minor>.<patch>[-prerelease]`, pushed on a commit that is on `main`. The workflow
  `publish-authorization-package.yml` is the only thing that publishes. Its steps, each needing the one before: check the tag is a
  semantic version and the commit is on `main`; build and run the whole test suite; pack at the tag's version; push; install the
  published package into the sample product and run the sample's tests.
- **The version comes from the tag only.** The project has no fixed version; a build from anywhere else is `0.0.0-local` (or
  `0.0.0-ci.<run>` in CI), which cannot be mistaken for a release.
- **Versioning follows semantic versioning.** The claim contract (`sub`, `tid`, `aud`, `permissions` as `key:Level`) and the public API
  are the surface. A breaking change to either is a new major version; the Auth Service keeps issuing the old contract until products
  have moved. This is applied in review and when choosing the tag; nothing checks it mechanically yet.
- **The package carries its README, a licence file and its symbols.** The symbols are embedded in the assembly with Source Link,
  because GitHub Packages does not host separate `.snupkg` symbol packages.
- **The credential is the run's own `GITHUB_TOKEN`**, with `packages: write` only in the job that pushes. It expires with the run and is
  stored nowhere, so there is no long-lived publishing secret to leak, and nothing on a developer machine can publish.
- **The sample product installs the package, never the project.** Every pull request packs the package and runs the sample's tests
  against it; a release repeats that against the package it has just published.

## Alternatives considered

- **nuget.org.** The natural home for a package anyone can use, and a restore needs no credentials. It needs a long-lived API key held
  as a repository secret, makes every version permanent and public (a version can only be unlisted, not removed), and the package is
  for this platform's own products, not for the public. Not chosen now; moving later is a change to one workflow step and the
  consumers' feed.
- **A self-hosted feed (Azure Artifacts, a private NuGet server).** More to run and to secure for one package. Not chosen.
- **Publishing from a developer machine, or from every merge.** Not reviewable, and a merge is not a decision to release. Not chosen.
- **Package validation against the previous version** (`EnablePackageValidation`) to catch a breaking API change automatically. Worth
  turning on once a first version is published, as it needs a baseline to compare with. Not done yet.

## Consequences

- A product team needs a GitHub token with `read:packages` to restore the package, even though the package is not secret. That is the
  cost of GitHub Packages and the main reason to reconsider nuget.org if packages become public.
- The licence file grants use to products of the Auth Service and nothing more (no redistribution, no resale). The owner confirmed this
  text for the first release (1.0.0). It must be replaced with a different licence before the package is given to anyone outside.
- A bad version cannot be replaced, only superseded, so a release is a considered act: the next version is the fix.
- The feed's address changes with the repository's owner; the workflow reads the owner from the run, and the README says how to add it.
