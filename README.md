# KSP PR Picker

A Windows tool for testing in-progress pull requests from [KSP-RO](https://github.com/KSP-RO) (and other GitHub) repositories in a local Kerbal Space Program install.

<img width="1484" height="1189" alt="Screenshot 2026-06-05 183719" src="https://github.com/user-attachments/assets/79b0b9f3-fc8d-4b29-987e-6620e914dc41" />
<img width="546" height="593" alt="Screenshot 2026-06-05 183738" src="https://github.com/user-attachments/assets/ea9782d7-db97-45fe-bf3b-9e5c0344e27e" />

It lists open PRs across selected repositories, lets you pick a set, then for each repo merges the chosen PRs onto `master`, builds the mod from source, and deploys it into your KSP `GameData` — with a one-time pristine backup so you can restore the stock state afterwards.

## What it does

- **Multi-repo PR listing** — pulls open PRs (via `gh`) from the repositories you select, showing author, files touched, and GitHub's mergeability. PR numbers are scoped per repo.
- **Conflict awareness** — flags PRs that conflict with `master` (dark red) and PRs that overlap a checked PR on a shared file (light red); the Files link shows the per-file breakdown.
- **Merge + build + deploy** — per repo: resets a `picker/build` branch to `master`, merges the selected PRs (with conflict resolution via `git rerere` and a graphical merge tool), builds the C# projects, and overlays each `GameData/<Mod>` folder onto the install.
- **Per-mod backups** — the first deploy backs up each touched mod folder to `GameDataPrPickerBak/<Mod>`; **Restore backup** puts them back.
- **Trust the clanker** — when off, the tool *prints* the exact git/MSBuild/copy commands instead of running them, so you can review and run them yourself.

## Requirements

- Windows, .NET Framework 4.8
- [GitHub CLI](https://cli.github.com/) (`gh`), authenticated
- Visual Studio / MSBuild
- A Kerbal Space Program install

Paths (MSBuild, KSP, repos folder, merge tool) are configured in **Settings** and persisted to `%APPDATA%\rp1-pr-picker\config.txt`.

## Notes

The picker owns the repositories it clones under the configured Repos folder; it won't clone into a pre-existing non-clone directory. Building arbitrary mods from source is best-effort — some repos have bespoke build systems.
