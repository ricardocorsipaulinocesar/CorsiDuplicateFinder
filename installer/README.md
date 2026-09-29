# Building the installer

Requires [Inno Setup 6](https://jrsoftware.org/isdl.php) (`winget install JRSoftware.InnoSetup`).

## From Visual Studio

Create (or use) a Publish profile for `CorsiDuplicate.UI` with:
- Target runtime: `win-x64`
- Deployment mode: **Self-contained**
- Configuration: **Release**

Click **Publish**. The `BuildInstaller` MSBuild target (in `CorsiDuplicate.UI.csproj`) runs
automatically right after and produces `installer\Output\CorsiDuplicateFinderSetup.exe`. If
Inno Setup isn't installed, the publish still succeeds — you just get a warning in the Output
window instead of an installer.

## From the command line

```
dotnet publish src/CorsiDuplicate.UI/CorsiDuplicate.UI.csproj -c Release -r win-x64 --self-contained true
```

Same target, same result: `installer\Output\CorsiDuplicateFinderSetup.exe`.

## Manually, without MSBuild

```
dotnet publish src/CorsiDuplicate.UI/CorsiDuplicate.UI.csproj -c Release -r win-x64 --self-contained true -o publish-installer
```

then open `installer\CorsiDuplicateFinder.iss` in the Inno Setup Compiler and Build
(`Ctrl+F9`), or run `ISCC.exe installer\CorsiDuplicateFinder.iss` — this fallback path defaults
to `..\publish-installer` when no `/DMyPublishDir` override is given.

## Version number

`src/CorsiDuplicate.UI/version.txt` is the single source of the version (status bar, exe,
installer, Windows "Installed apps"). It holds the version the next installer publish will
carry; after each successful installer build the last number is bumped automatically
(1.0.0.0 → 1.0.0.1 …). Commit the updated `version.txt` after publishing. Edit the first three
numbers by hand for a bigger release.

ffmpeg isn't bundled: the app downloads it into `%LocalAppData%` on first run.
