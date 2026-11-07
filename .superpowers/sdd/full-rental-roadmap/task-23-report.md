# Task 23: Agent package dependency metadata

## Change

The Agent package build regression now parses `Agent/Agent.deps.json` and requires its `Agent/<version>` library entry to be a project whose identity matches the `AgentVersion` returned by the package builder.

`Publish-Services.ps1` now cleans the selected project's Release/RID outputs before publishing. The .NET 10 SDK's `GenerateBuildDependencyFile` target is incremental on the assets file, its cache, and imported project files; a change to `Version` alone does not invalidate the generated dependency manifest. A clean removes the stale dependency file so publish regenerates it using the requested version. Runtime Agent behavior and dependencies are unchanged.

## TDD and package checks

All PowerShell checks used the requested PowerShell 7 executable from the repository root.

### RED

Command:

```powershell
& 'C:\Users\User\.cache\codex-runtimes\codex-primary-runtime\dependencies\native\powershell\pwsh.exe' -NoProfile -File '.\scripts\agent\Test-AgentPackageBuild.ps1'
```

Before the publish fix, the check exited 1 at the new assertion with:

```text
Published Agent dependency identity does not match the package version.
```

This reproduced the stale project identity in the generated package.

### GREEN: package build

After the publish fix, the same command exited 0:

```text
Agent package build test passed for win-x64; SHA256=8226C9EFA0C7E44DE4364327FAAF73190DFA26C87C2D43D92A8C4EA1C08F56F6.
```

### Package plan

Command:

```powershell
& 'C:\Users\User\.cache\codex-runtimes\codex-primary-runtime\dependencies\native\powershell\pwsh.exe' -NoProfile -File '.\scripts\agent\Test-AgentPackagePlan.ps1'
```

Output:

```text
Agent package plan tests passed; no files or services changed.
```

### Fix investigation

An initial `RestoreForce=true` publish property did not refresh the manifest; the regression remained red, so that property was removed. The clean-before-publish change is the minimal verified correction for the SDK's incremental dependency-file output.

## Commit and local artifact

The tracked fix and this Task 23 completion note are committed before the clean-HEAD package refresh. The commit identifier and final artifact evidence will be recorded here after that refresh.

The default win-x64 artifact build and `.env.local` hash-only update are pending. The final record will include the clean revision/version, ZIP and checksum/environment hash agreement, required and excluded package entries, and confirmation that other `.env.local` values were preserved.
