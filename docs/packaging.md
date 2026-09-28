# Packaging `Etch.Core`

`Etch.Core` (format detection, the transform catalogue, palette ranking, and the
text utilities behind them) is published as a NuGet package so that
[Sling](https://github.com/HendrikVrey/Sling), the HTTP client that runs the same
transform chain over response bodies, can depend on a version of it rather than on a
submodule or a copied folder.

It is the only packable project in the repository. `Directory.Build.props` sets
`IsPackable=false` for everything, and `Etch.Core.csproj` turns it back on for
itself, so `dotnet pack Etch.slnx` produces exactly one package instead of also
trying to pack a WPF application and three test projects.

## The feed is private, and the licence is the reason

**`Etch.Core` is not on nuget.org and must not be put there.**

`LICENSE` §3(b) forbids distributing, publishing or otherwise making the Software or
any part of it available to a third party. A package on nuget.org does exactly that,
and worse: it *advertises* it as something to take a dependency on. Every consumer's
build output embeds `Etch.Core.dll`, so shipping their application would redistribute
it, which the licence does not permit. Publishing a package nobody is licensed to use
would be a broken thing to publish, and unlisting it later does not un-publish it:
nuget.org versions cannot be deleted.

Three smaller reasons point the same way. The transform engine is the product's
differentiator and §11 keeps the option of separate commercial terms open. A public
package id is a permanent public API contract, which is a real obligation to take on
for a library with one consumer. And both consumer and publisher are the same person,
so a private feed costs nothing that a public one would buy.

The feed is **GitHub Packages** under this account:

```
https://nuget.pkg.github.com/HendrikVrey/index.json
```

### The visibility trap

GitHub's NuGet registry has *granular* permissions, which means a package's
visibility is its own and does **not** follow the visibility of the repository it is
linked to. That is what allows a private package to be published from this public
repository.

**Making a package public in GitHub Packages is a one-way door: it cannot be made
private again.** After the first publish, check
`github.com/users/HendrikVrey/packages/nuget/package/Etch.Core` and confirm it reads
Private. If it does not, the fix is to delete the package and republish, not to
change a setting.

## Versioning

The package version is the repository's `<VersionPrefix>` from
`Directory.Build.props`, with the same `-dev.<run>` suffix the rolling installer
uses. One number to bump, in one place.

| Trigger | Package version | Meaning |
|---|---|---|
| push to `master` | `1.0.1-dev.42` | rolling; a NuGet prerelease, so it is only resolved by a consumer who asks for it |
| push of a `v*` tag | `1.0.1` | stable |

A version in GitHub Packages is immutable, so the push uses `--skip-duplicate`: a
re-run of a tagged release must not fail the job merely because the identical package
is already there.

## Publishing

Automatic. `.github/workflows/release.yml` packs and pushes on every run, after the
test gate and before the installer, so a package only ever exists for a build whose
suite was green. It authenticates with the workflow's own `GITHUB_TOKEN` and the
`packages: write` permission; no personal access token is involved.

To publish by hand, which should be rare:

```bash
dotnet pack src/Etch.Core/Etch.Core.csproj -c Release -p:Version=1.0.1 -o packages
dotnet nuget push "packages/Etch.Core.1.0.1.nupkg" --source https://nuget.pkg.github.com/HendrikVrey/index.json --api-key <CLASSIC_PAT>
```

**The token must be a classic personal access token with `write:packages`.** GitHub
Packages does not support fine-grained tokens, so Linda's fine-grained PAT (see the
Linda mind, `memories/github.md`) cannot do this, and it lacks the scope anyway.

## Consuming it

Sling's `NuGet.config`, `local-feed/README.md` and `docs/etch-core-package.md` cover
the consuming side, including the offline fallback that lets a machine build with no
credentials at all.

## What the package contains

One assembly, no dependencies, plus the licence and a README. Debug information is
embedded in the DLL rather than shipped as a symbol package, because GitHub Packages
does not host `.snupkg` and a consumer stepping into a transform should not need a
symbol server.

```
lib/net10.0/Etch.Core.dll
LICENSE
README.md
```

`Etch.Core` takes no `PackageReference` and never will; the empty dependency group in
the nuspec is the machine-checkable form of that rule.
