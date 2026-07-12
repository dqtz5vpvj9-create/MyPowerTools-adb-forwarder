<!-- mypowertools-materialized-source -->
# adb-forwarder

This repository contains the `adb-forwarder` tool source and its current
MyPowerTools adapter. Its local submodule origin is:

```text
file:///C:/Users/lixinrui/repo/MyPowerTools.ToolRepos/adb-forwarder
```

## Repository layout

- `original-source/` contains the captured AdbForwarder product repository.
- `current-integration/` contains the MyPowerTools adapter, package template,
  product UI integration source, services, and related test snapshots.
- `source-map.json` records the captured source commit, dirty state, and file
  mapping.
- `tool-release.json` declares the adapter project, suite project references,
  package template, and staged package output.
- `build.ps1` builds the adapter in Release configuration and stages the
  package at `artifacts/package`.

The package and module IDs are both `adb-forwarder`.

## Build

Pass the MyPowerTools superproject explicitly when the repositories are in
arbitrary locations:

```powershell
pwsh ./build.ps1 -MyPowerToolsRepoRoot 'C:\path\to\MyPowerTools'
```

The parameter can be omitted for a checkout under
`MyPowerTools/tools/adb-forwarder` or a standalone checkout beside a directory
named `MyPowerTools`:

```powershell
pwsh ./build.ps1
```

Automatic discovery also considers the `MYPOWERTOOLS_REPO_ROOT` environment
variable. A direct adapter build must pass the equivalent MSBuild property:

```powershell
dotnet build ./current-integration/src/AdbForwarder.MyPowerTools/AdbForwarder.MyPowerTools.csproj `
  --configuration Release `
  -p:MyPowerToolsRepoRoot='C:\path\to\MyPowerTools'
```

The package template remains under
`current-integration/modules/adb-forwarder`. The build copies the completed
template, including `AdbForwarder.MyPowerTools.dll`, to `artifacts/package`.
Package integrity metadata requires refresh before a signed release.

## Publishing the repository

After publishing this repository, update its URL from the MyPowerTools
superproject and commit `.gitmodules`:

```powershell
git config -f .gitmodules submodule.tools/adb-forwarder.url <remote-url>
git submodule sync -- tools/adb-forwarder
git add .gitmodules
```
