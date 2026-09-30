# Imported: ResoniteWorkbench.Protocol

The 10 `.cs` files in this directory are imported verbatim (unchanged) from the
`ResoniteWorkbench.Protocol` project in the user's own `resonite-workbench` repository:

- Source repository: `C:\Users\jojoh\Documents\resonite-workbench`
- Source commit: `7d40c92` (`7d40c9292296362c71d106552172488dbf37c14a`)
- Source path: `src/ResoniteWorkbench.Protocol/`

Imported files:

- `WorkbenchBuildInfo.cs`
- `RpcExceptions.cs`
- `RpcFrameCodec.cs`
- `RpcMessages.cs`
- `WorkbenchJson.cs`
- `WorkbenchRpcClient.cs`
- `RpcNames.cs`
- `RpcUsageExport.cs`
- `RpcParams.cs`
- `RpcUsage.cs`

The namespace is unchanged (`ResoniteWorkbench.Protocol`), to keep the import a faithful,
low-risk copy. New ResoLoop-specific code lives outside this directory, in the `RLoop.Workbench`
namespace, and consumes these types.

This project is dependency-free (BCL only) in the source repository. `resonite-workbench` is the
repository owner's own project; the copy was explicitly authorized (2026-09-26, see
`plan/workbench-design.md` section 6.1 and section 12, decision 2). The `resonite-workbench`
repository itself is never modified by ResoLoop's build or tests.

Do not edit these files to fix ResoLoop-specific issues; if the wire protocol needs to change,
that is a `resonite-workbench` change proposed separately (see `plan/workbench-design.md` section
5.3 (0) for the process ResoLoop uses to propose RPC changes upstream).
