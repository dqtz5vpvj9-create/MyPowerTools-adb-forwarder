# Archived integration snapshots

The 15 files listed below (6,606 physical source lines) were retired from the current tree because they are superseded integration snapshots, not current build inputs.

The active UI is current-integration/src/AdbForwarder.Surface; the adapter and service are AdbForwarder.MyPowerTools and AdbForwarder.Service.

The superproject solution, project includes, tool build scripts, package manifests, and runtime source staging do not consume these paths. Existing Surface XAML, reflection-loaded factory types, shared runtime ownership, original-source trees, and linked tests remain unchanged. No runtime behavior change or build-time speedup is claimed from deleting files that were not compiled.

## Restore from Git history

Recovery commit in this tool repository: `cd7286ddc58feba525ea51070dd1d034bc5befbb`. Keep the repository history containing this commit when publishing or mirroring it. A shallow clone or a source ZIP alone may not contain these historical blobs.

Run from this tool repository after saving any current changes:

```sh
git cat-file -e cd7286ddc58feba525ea51070dd1d034bc5befbb^{commit}
git restore --source=cd7286ddc58feba525ea51070dd1d034bc5befbb -- current-integration/src/MyPowerTools.Shell.Avalonia
```

If the commit is absent, obtain it from an existing full clone or the repository remote containing this history before restoring. Restoring adds historical source files to the working tree; it does not wire them back into the current build.

The original source-map capture fields remain historical provenance. Its archivedIntegrationSnapshots entries describe the later retirement and point to this document, which stays inside current-integration so source-bundle materialization preserves it.

## Exact file recovery inventory

| Path | Git blob SHA-1 | Physical lines |
| --- | --- | ---: |
| `current-integration/src/MyPowerTools.Shell.Avalonia/Services/AdbForwarderConfigurationService.cs` | `179c9c249a8050f1a5186fc202c63c33c4addb77` | 457 |
| `current-integration/src/MyPowerTools.Shell.Avalonia/Services/AdbForwarderElevationService.cs` | `26ac12c06ad7c6cea1c64b5f24964aaf9cb34870` | 539 |
| `current-integration/src/MyPowerTools.Shell.Avalonia/Services/AdbForwarderToolService.cs` | `6f9a75977573eef57c9b1ce02932c5f4ad171252` | 447 |
| `current-integration/src/MyPowerTools.Shell.Avalonia/Services/AdbForwardingWorkflowService.cs` | `6649a1e763e3f1a1f0430881f7297920e0cee82d` | 2085 |
| `current-integration/src/MyPowerTools.Shell.Avalonia/Services/AdbForwardingWorkflowStateStore.cs` | `bbc56aac0da7f5a7a16853464af45043a36d03cc` | 299 |
| `current-integration/src/MyPowerTools.Shell.Avalonia/ViewModels/AdbForwarderDeviceSettingEditorViewModels.cs` | `04f3280d76d35128b39d79d2738d12eeec8b55b4` | 199 |
| `current-integration/src/MyPowerTools.Shell.Avalonia/ViewModels/AdbForwarderMappingEditorViewModel.cs` | `04a05c7eb0a7a548cbba7ad10a45e62c83fbb882` | 128 |
| `current-integration/src/MyPowerTools.Shell.Avalonia/ViewModels/AdbForwarderViewModel.Environment.cs` | `c429f1f0ffeb09e002e54700860b7d986c179411` | 189 |
| `current-integration/src/MyPowerTools.Shell.Avalonia/ViewModels/AdbForwarderViewModel.Properties.cs` | `8a58a86a84392e9cd03ea0c2955f48fc4622358c` | 337 |
| `current-integration/src/MyPowerTools.Shell.Avalonia/ViewModels/AdbForwarderViewModel.Rules.cs` | `78fbea5572a53eb36c4718ba4fa0cf8f25b5ea95` | 216 |
| `current-integration/src/MyPowerTools.Shell.Avalonia/ViewModels/AdbForwarderViewModel.Workflow.cs` | `d18b3bd7aac62d113ec8f1816203dd182700c3ce` | 272 |
| `current-integration/src/MyPowerTools.Shell.Avalonia/ViewModels/AdbForwarderViewModels.cs` | `0b056a1ae734e9ac01db9592fd7df8e50bf0e8d2` | 184 |
| `current-integration/src/MyPowerTools.Shell.Avalonia/ViewModels/AdbForwarderWorkflowItemViewModels.cs` | `68d5c24d5cb2016ef2f5ac5b1a157837c1348338` | 100 |
| `current-integration/src/MyPowerTools.Shell.Avalonia/Views/AdbForwarderView.axaml` | `419b834412743606407caa9fd5471949b158db14` | 1070 |
| `current-integration/src/MyPowerTools.Shell.Avalonia/Views/AdbForwarderView.axaml.cs` | `f7db736b49beff02f28f7a981422efbfaca703c9` | 84 |
