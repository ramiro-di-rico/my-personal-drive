---
name: upgrade-deps
description: Bump a NuGet package, the .NET SDK, or Avalonia in this repo safely — publish-date rule, the version pins that exist for a reason, native assets, and the AOT re-verification a bump requires. Use whenever a PackageReference version changes or a vulnerability advisory needs answering.
---

# Upgrade a dependency

Bumping a package here is not a one-line edit. The app is Native AOT, ships self-contained with
native assets (Skia, SQLite, PDFium), and several versions in
`src/MyPersonalDrive/MyPersonalDrive.csproj` are **pinned deliberately, with the reason written in
a comment next to them**. Read those comments before changing a number — they are the design
record, and deleting one loses the reason.

## Non-negotiables

- **Publish date ≥ 7 days.** Never install or pin a version published less than a week ago; take
  the newest version that is at least that old. If every release is too recent, stop and say so
  rather than picking something unsafe. When you change a version the user asked for, report:
  requested version, version used, its publish date, and the one-line reason.
- **Record the publish date in the comment**, the way the existing pins do (`Published
  2026-04-07.`). It is what makes the next bump auditable.
- **Never delete an existing pin comment to make a bump fit.** If the pin's reason no longer
  holds, say why it no longer holds in the new comment.

## The pins that exist today, and why

| Package | Pinned because |
|---|---|
| `Avalonia.Controls.ItemsRepeater` 12.0.0 | the only 12.x release; NuGet unifies its Avalonia reference up to 12.0.4 |
| `SQLitePCLRaw.bundle_e_sqlite3` 2.1.12 | a direct pin *only* to lift a transitive: `Microsoft.Data.Sqlite` still resolves 2.1.11, whose bundled SQLite is affected by CVE-2025-6965 (NU1903). Remove the line once Microsoft.Data.Sqlite resolves 2.1.12+ on its own |
| `PDFtoImage` 5.2.1 | newer 5.x bumps SkiaSharp to 4.x, a major Avalonia.Skia 12.0.4 does not use — it pins the 3.119.x line already referenced |

The shape to keep in mind: **SkiaSharp's version is dictated by Avalonia**, not by us. Any package
that drags in a different SkiaSharp major is the problem, not the solution.

## Steps

1. **Establish what you're fixing.** A CVE (`dotnet list package --vulnerable --include-transitive`),
   an outdated report (`dotnet list package --outdated`), or a feature you need. "It's newer" is
   not a reason to bump a pinned package.
2. **Check the publish date** and pick the newest compliant version, per the rule above.
3. **Check the graph, not just the number.** After the edit:

   ```bash
   dotnet restore src/MyPersonalDrive/MyPersonalDrive.csproj
   dotnet list src/MyPersonalDrive/MyPersonalDrive.csproj package --include-transitive
   ```

   Confirm no cross-major split appeared (two SkiaSharp majors, two Avalonia lines) and that the
   transitive you meant to lift actually moved.
4. **Verify a native claim by reading the binary, not the release notes** — that is how the
   SQLite pin was verified. For a native asset, check the shipped `.so` in the publish output.
5. **AOT re-verify. Mandatory for every package bump** — a new version can add reflection the
   trimmer cannot prove. Run the full `aot-check` skill; `dotnet test` does not cover this.
6. **Run the tests and publish end to end**:

   ```bash
   ./scripts/run-tests.sh
   ./scripts/publish-linux.sh
   ```

   Then the native-library and tarball verification from `release-linux` — a missing `.so` after
   a bump is the classic failure, and it only appears in the packaged output.
7. **Exercise the affected surface by hand.** A Skia/PDFium bump means the preview panes; an
   Avalonia bump means the whole UI (`run-app`, then `smoke-test`).
8. **SDK / TFM bumps** are their own change: `net10.0` appears in the csproj and in whatever CI
   pins the SDK (`ci-setup`). Bump them together or CI and local diverge silently.

## Checklist

- [ ] Version is ≥ 7 days old; date recorded in the comment
- [ ] Existing pin comments preserved or explicitly superseded
- [ ] Transitive graph checked; no cross-major SkiaSharp/Avalonia split
- [ ] Native claims verified against the shipped binary
- [ ] `aot-check` run, warnings clean
- [ ] `run-tests.sh` green; `publish-linux.sh` + tarball verification done
- [ ] Affected UI surface exercised by hand
- [ ] Change reported as: requested → used → publish date → reason
