---
name: deploy
description: Release a new version of Sofia — ask whether to bump major, mid (minor) or minor (patch), update the version, commit, tag vX.Y.Z and push main and the tag to origin.
---

# Deploy

Versioning is `MAJOR.MID.MINOR` (e.g. `0.2.2`): "major" bumps the first number, "mid" the second, "minor" the third. The source of truth is `<Version>` in `src/Sofia.Server/Sofia.Server.csproj`; tags are `vX.Y.Z` (a `v*` tag triggers `.github/workflows/release.yml`).

## Steps

1. Preconditions (stop and report if any fails):
   - `git branch --show-current` is `main`.
   - `git status --porcelain` is empty (otherwise ask the user whether to continue; never include unrelated files in the release commit).
   - `git fetch origin` and local `main` is not behind `origin/main`.
2. Read the current version from the csproj `<Version>` and the latest tag (`git tag --sort=-v:refname | select -first 1`). If they differ, tell the user and ask which to use as base.
3. Ask with the `question` tool: "Which part of the version should be bumped?" with options `major (X.0.0)`, `mid (x.Y.0)`, `minor (x.y.Z)`, showing the resulting version for each. Bumping a part resets the parts to its right to 0.
4. Show the new version and tag and ask for confirmation before changing anything.
5. Update `<Version>` in `src/Sofia.Server/Sofia.Server.csproj` (only that file; check with grep for other places that hold the same old version, e.g. docs, and update them if they are release versions).
6. Commit only the changed files: `git commit -m "chore(release): vX.Y.Z"`.
7. Tag: `git tag -a vX.Y.Z -m "vX.Y.Z"`. Abort if the tag already exists.
8. Push: `git push origin main` then `git push origin vX.Y.Z`. Never force-push.
9. Report the new version, the tag and the push result (and that the release workflow was triggered).
