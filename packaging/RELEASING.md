# WEDM release management

## Versioning

Edit `Directory.Build.props` at the solution root:

- `WedmProductVersion` — semantic version for assemblies and installers.
- `WedmReleaseChannel` — label embedded in `InformationalVersion` (Stable, Beta, etc.).
- `WedmProductName` — Windows `Product` / `AssemblyTitle` metadata.

## Build a release bundle

From `WEDM/packaging`:

```powershell
Set-ExecutionPolicy -Scope Process Bypass
.\publish-release.ps1 -Configuration Release
```

This publishes `WEDM.UI` (self-contained win-x64) into `artifacts/wedm-<version>-<channel>-win-x64` and writes `release-manifest.json` with per-file SHA-256 for integrity validation (`ReleaseBundleManifestValidator` in code).

## Enterprise installer (Inno Setup)

1. Install [Inno Setup 6](https://jrsoftware.org/isinfo.php).
2. Run `publish-release.ps1` to produce a publish directory.
3. Compile the script (adjust `/D` defines as needed):

```text
ISCC.exe /DMyAppVersion=1.1.0 /DPublishDir=C:\full\path\to\artifact\folder WEDM.Enterprise.iss
```

Signed builds: configure your `SignTool` in Inno or sign `WEDM-Setup-*.exe` post-build with your enterprise timestamping service.

## Bootstrapper

`WEDM.Bootstrapper` (`WEDM.Bootstrap.exe`) performs VC++ registry detection and can launch the Inno-generated `WEDM.Setup.exe` with `--install --silent`. Place the bootstrapper next to the setup EXE in your release media layout.

## Silent install / upgrade / repair / uninstall

Inno Setup supports:

- Silent install: `WEDM.Setup.exe /VERYSILENT`
- Uninstall: use `unins000.exe` from the install directory or Add/Remove Programs.
- Repair: re-run the same version installer (Inno upgrade-aware when `AppId` is stable).

## Product sidecar

The UI ships `wedm-product.json` and optional `wedm-update-feed.json` beside `WEDM.exe` for release channel, release notes path, and future local update manifest parsing (`IUpdateManifestReader`).

## Operational telemetry

`IOperationalTelemetrySink` is invoked from `DeploymentOrchestrator` for lifecycle events. The default implementation is a no-op; replace the registration in `App.xaml.cs` with your exporter when approved.
