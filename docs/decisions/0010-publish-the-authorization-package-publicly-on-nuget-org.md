# 0010. Also publish the authorization package publicly on nuget.org, under MIT

- Status: Accepted
- Date: 2026-11-09
- Tickets: AUTH-204 (PLT-07), follow-up to [0009](0009-publish-the-authorization-package-to-github-packages.md)

## Context

Decision 0009 published `AuthService.Authorization` to the repository's private GitHub Packages feed, which needs a GitHub token with
`read:packages` and access to the repository. Teams outside the organisation, and the products of other teams, could not restore it
without being handed a token. The owner wants the package to be downloadable by anyone.

## Decision

- **Same workflow, one more job.** `publish-nuget` in `.github/workflows/publish-authorization-package.yml` runs after the GitHub Packages
  push has been installed into the sample product and its tests have passed. It downloads the `.nupkg` that was kept by the `publish`
  job and pushes that file to `https://api.nuget.org/v3/index.json`, so nuget.org gets the very package that was verified. Both feeds
  carry the same versions.
- **The public licence is MIT** (`PackageLicenseExpression`). The earlier text, which limited use to Auth Service products and forbade
  redistribution, cannot be combined with an open feed. `LICENSE.txt` was removed from the package.
- **No project URL, repository URL or source link in the package, and no embedded sources.** The repository is private, so every such
  link would be dead. Debug symbols stay embedded in the assembly. Documentation is the package README.
- **The nuget.org key lives in a GitHub environment named `nuget`** as the secret `NUGET_API_KEY`: a key limited to the package id
  `AuthService.Authorization`, with the push permission only, and an expiry. The job fails with a clear message if it is missing. Adding
  required reviewers to the `nuget` environment turns every public release into a manual approval.
- **The first public version is 1.0.1.** Version 1.0.0 went to GitHub Packages under the old licence text, and a version cannot be
  replaced, so the same number must not mean two different packages.

## Consequences

- A version on nuget.org can never be deleted or replaced, only **unlisted**, and the licence it carries cannot be withdrawn. A release
  is therefore the one step in the pipeline that cannot be undone.
- The package and its public API become a public contract. The claim names (`sub`, `tid`, `aud`, `permissions` as `key:Level`) are part
  of it, which makes the Auth Service's token format public too (it is the product's own token format, not a secret).
- The package id `AuthService.Authorization` is not reserved by a prefix. Anyone could publish a lookalike id; reserving the
  `AuthService.` prefix on nuget.org is possible and would prevent that.
- Source stepping into the package is not available until the repository, or a separate public repository for the package, is public.
- The repository's other code is not licensed by this decision; only the package carries MIT.
