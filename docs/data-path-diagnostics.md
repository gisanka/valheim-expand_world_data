# Data path diagnostic build

Based on upstream EWD 1.80, commit `1905e7e5fbe68e6c23096b333bcfae35d1dd9c9b`.
Diagnostic marker: `data-paths-2026-11-11`. The plugin version and sync version remain upstream's 1.80.

Install the built ExpandWorldData.dll in place of the existing EWD DLL (keep only one copy).
Use the same configuration and EWP version for comparison runs. Restart fully with
`Automatic data reload = false`, save both the BepInEx log and server console output,
then repeat with only that setting changed to true.

Search the logs for `[EWD path diagnostic`. The diagnostic is always active in this branch.
It records the working directory and BepInEx config path in Awake without accessing
DataLoading, so it does not deliberately force early initialization of its static fields.
The working directory used when GamePath is captured is recorded separately.
Before loading, and before/after watcher directory creation, it reports the stored paths,
their literal equality, existence and attributes. Each recursive file query reports its
source label, pattern and count, or full exception with HResult and parent attributes.
Literal equality does not establish filesystem identity; symlink targets and mount
namespaces require inspection on the server if the log still leaves ambiguity.

The diagnostic does not create directories when automatic reload is off, skip failed
sources, list YAML contents, or suppress the original enumeration exception. It adds
filesystem reads and logging, which can affect timing. The upstream loader still clears
its data dictionaries before enumeration and still stops on the first failed source.

Build prerequisites are the upstream .NET Framework 4.8 project and the referenced
BepInEx, YamlDotNet, Unity and Valheim assemblies under the existing `../../Libs` paths.
Game references must expose the private members used by upstream (publicized build
references). No extra runtime dependencies are introduced by the diagnostic.

Validation on Windows, 2026-11-11:
- Release build targeting .NET Framework 4.8: passed, zero warnings/errors.
- Existing migration tests plus three diagnostic filesystem tests: 36 passed, zero failed/skipped.
- Used local Valheim/Unity, BepInEx and YamlDotNet references; cached BepInEx.AssemblyPublicizer.MSBuild 0.4.3 imported through an external validation props file. No project build settings changed.
- Initial build with unpublicized references failed on upstream private game members; resolved for the successful validation build.
- An unprivileged test attempt stalled and was cancelled; the subsequent local test run passed.
- git diff --check: passed.
- Linux/DatHost and in-game runtime validation: not performed.
