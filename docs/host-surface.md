# ReClass.NET host surface

Every claim below is cited as `path:line` against the pinned submodule in
`external/ReClass.NET`. Re-check them after bumping the submodule.

## 1. Loader contract (hard requirements)

| Requirement | Source |
| --- | --- |
| Plugins dir = `Path.Combine(Application.StartupPath, "Plugins")`, scanned recursively for `*.dll`, `*.exe`, `*.so` | `Forms/MainForm.cs:106`, `Plugins/PluginManager.cs:44-48`, `Constants.cs:33` |
| `FileVersionInfo.ProductName` MUST equal `"ReClass.NET Plugin"` or the file is skipped | `Plugins/PluginInfo.cs:14`, `Plugins/PluginManager.cs:69-73` |
| `ProductName == null` ⇒ treated as a NATIVE plugin (LoadLibrary) | `Plugins/PluginInfo.cs:42` |
| Entry type name is `<FileNameWithoutExt>.<FileNameWithoutExt>Ext`, instantiated via `Activator.CreateInstanceFrom` (LoadFrom context) | `Plugins/PluginManager.cs:132-146` |
| Must derive from public `ReClassNET.Plugins.Plugin`, public parameterless ctor, `Initialize` returning `true` | `Plugins/Plugin.cs:11,36` |
| UI metadata comes from Win32 version info: FileDescription→Name, Comments→Description, CompanyName→Author | `Plugins/PluginInfo.cs:44-59` |
| Per-file exceptions are logged and skipped; no plugin can break the host load | `Plugins/PluginManager.cs:105-108` |
| No `InternalsVisibleTo` anywhere ⇒ every `internal` member is reflection-only | verified repo-wide |
| Build output is `bin\<Config>\<x86|x64>\`; the Windows build does NOT create `Plugins\` | `ReClass.NET.csproj:26,80,91,102`; root `Makefile:65-68` is the only creator |
| x86 and x64 are separate installs; assembly identity is platform-neutral (`ReClass.NET, Version=1.2.0.0`) so one AnyCPU plugin binds to both | `Properties/AssemblyInfo.cs:33-34`, verified against built binary |

Host framework: `net472`, legacy non-SDK csproj, `LangVersion=latest`, no NRT,
`AutoGenerateBindingRedirects=true` but `App.config` has no `assemblyBinding`
section. Verified build: MSBuild 17.14.51.32402, `Release|x64` → success, 0 warnings.

.NET Framework only reads the AppDomain's config, so a plugin `.dll.config`
is ignored. Any multi-assembly dependency graph needs an `AssemblyResolve`
shim installed from the `…Ext` static constructor.

## 2. Host API entry points

### Program (static, public) in `Program.cs`
`Settings :21`, `Logger :23`, `RemoteProcess :27`, `CoreFunctions :29` (=`RemoteProcess.CoreFunctions`), `MainForm :31`, `ShowException :104`.
`Program.MainForm` is assigned (`:87`) before `OnLoad` runs, so it is valid during `Plugin.Initialize`.

### IPluginHost in `Plugins/IPluginHost.cs:11`
`MainWindow`, `Resources`, `Process`, `Logger`, `Settings`.

### MainForm public surface
`CurrentProject :34` (null during Plugin.Initialize), `ProjectView :44`,
`MainMenu :46` (live `MenuStrip`, the only supported UI injection point),
`CurrentClassNode :48`.
`MainForm.Functions.cs`: `ShowPartialCodeGeneratorForm :24`, `ShowCodeGeneratorForm :31/:38`,
`AttachToProcess(string) :47`, `AttachToProcess(ProcessInfo) :62`, `SetProject :76`,
`AddBytesToClass :147`, `InsertBytesInClass :166`, `ClearSelection :184`,
`ShowOpenProjectFileDialog :191`, `LoadProjectFromPath :212`, `ReplaceSelectedNodesWithType :283`.

Negative results that shape the design:
- No `SaveProject`: replicate `new ReClassNetFile(project).Save(path, logger)` (`MainForm.cs:297`).
- No `Detach`: use `Program.RemoteProcess.Close()`.
- No selection accessor: `memoryViewControl` is private (`MainForm.Designer.cs:1410`).
- ToolStrip is private, so no toolbar buttons; menu items only.
- No project-changed event: `SetProject` swaps the instance and rewires handlers silently (`MainForm.Functions.cs:76-109`). Re-resolve `CurrentProject` on every call; the static `ClassNode.ClassCreated` survives swaps.
- `Plugin.Terminate()` runs in `OnFormClosed` (`MainForm.cs:142`), after the UI is gone but before `SettingsSerializer.Save` (`Program.cs:100`), so settings written there persist.

### Project model in `Project/ReClassNetProject.cs`
Events `ClassAdded :13`, `ClassRemoved :14`, `EnumAdded :17`, `EnumRemoved :18`.
`Enums :23`, `Classes :25`, `Path :27`, `CustomData :33`, `TypeMapping :38`.
`AddClass :51`, `ContainsClass :62`, `GetClassByUuid :69` (throws on miss),
`Clear :81`, `Remove :112` (throws `ClassReferencedException`), `RemoveUnusedClasses :128`,
`AddEnum :146`, `RemoveEnum :155` (throws `EnumReferencedException`).
No dirty flag, no undo stack and no save prompt anywhere in the repo.

### Nodes
`BaseNode` (`Nodes/BaseNode.cs:17`): `Offset :29` (recomputed by `UpdateOffsets`), `Name :32`,
`Comment :35`, `ParentNode` (internal setter) `:38`, `IsHidden :44`, `IsSelected :47`,
`MemorySize :50`, `CreateInstanceFromType(Type[, bool callInitialize]) :71/:81` (the factory,
`Activator.CreateInstance`), `Initialize :123`, `CopyFromNode :130`, `GetParentContainer :143`,
`GetParentClass :166`, `GetUserInterfaceInfo :101` (throws for `ClassNode` and `VirtualMethodNode`; needs GDI+ ⇒ UI thread).

`BaseContainerNode` (`Nodes/BaseContainerNode.cs:7`): `Nodes :14`, `CanHandleChildNode :26`,
`UpdateOffsets :46`, `FindNodeIndex :58`, `BeginUpdate :123`/`EndUpdate :131`,
`ReplaceChildNode :151/:164`, `AddBytes :219`, `InsertBytes :225`, `AddNodes :277`,
`AddNode :304`, `InsertNode :321`, `RemoveNode :341`.

`BaseWrapperNode` (`Nodes/BaseWrapperNode.cs:5`): `InnerNode :8`, `CanChangeInnerNodeTo :21`,
`ChangeInnerNode :25`, `ResolveMostInnerNode :50`. Array: `BaseWrapperArrayNode.Count :13`, `CurrentIndex :12`.
Cycle guard: `ClassUtil.IsCyclicIfClassIsAccessibleFromParent :17`, mandatory before wiring a class into a wrapper.

`ClassNode` (`Nodes/ClassNode.cs:12`): `Uuid :28` (the only stable identity in the model),
`AddressFormula :30`, `NodesChanged :32`, `static Create() :48` (implicitly adds to the current
project through the static `ClassCreated` event wired at `MainForm.Functions.cs:110`),
`internal ClassNode(bool) :34` (detached construction is NOT available to a plugin).

43 concrete node types; 36 are UI-exposed via the internal `NodeTypesBuilder`
(`UI/NodeTypesBuilder.cs:15-31`). The serialization registry
(`DataExchange/ReClass/ReClassNetFile.cs:38`, 39 types + 10 legacy aliases) is private.
⇒ enumerate `typeof(BaseNode).Assembly` for non-abstract `BaseNode` subclasses and
blacklist `ClassNode`, `VirtualMethodNode` and the 3 `Legacy/` shims
(`ClassInstanceArrayNode`, `ClassPointerArrayNode`, `ClassPointerNode`).

Mutation hazards:
- `ReplaceChildNode` compensates shrink only; growth silently eats following nodes (`BaseContainerNode.cs:192-195`).
- `BaseTextNode.CopyFromNode` drops Name/Comment/Offset (`BaseTextNode.cs:23`).
- `RemoveNode` does not backfill bytes.
- Every child mutation cascades `UpdateOffsets()` across all classes (`ReClassNetProject.cs:74`) ⇒ always wrap batches in `BeginUpdate`/`EndUpdate`.

### Serialization
`ReClassNetFile : IReClassImport, IReClassExport` `DataExchange/ReClass/ReClassNetFile.cs:11`;
`Load(string|Stream, ILogger)` `Read.cs:18/:25`; `Save(string|Stream, ILogger)` `Write.cs:17/:24`;
`static SerializeNodesToStream(Stream, IEnumerable<BaseNode>, ILogger)` `Write.cs:172`.
Format is a ZIP holding `Data.xml`. Import-only: `ReClassFile` (`.reclass`), `ReClassQtFile` (`.reclassqt`).
`Save(Stream, …)` is the snapshot primitive for an undo ring.

### Process / memory
`RemoteProcess` `Memory/RemoteProcess.cs:21` (`IRemoteMemoryReader`, `IRemoteMemoryWriter`, `IProcessReader`).
`Open(ProcessInfo) :106` / `Close() :128` / `ControlRemoteProcess :545`;
events `ProcessAttached :43`, `ProcessClosing :46`, `ProcessClosed :49`;
`Modules :62`, `Sections :75` (locked snapshot copies), `IsValid :88` (native round-trip per access),
`ReadRemoteMemoryIntoBuffer :151/:159`, `ReadRemoteMemory :180`, `WriteRemoteMemory :336`,
`GetSectionToPointer :350`, `GetModuleToPointer :359`, `GetModuleByName :368`, `GetNamedAddress :380`,
`EnumerateRemoteSectionsAndModules :408`, `UpdateProcessInformations() :427` (blocking),
`UpdateProcessInformationsAsync :434`, `ParseAddress :477`, `LoadAllSymbolsAsync :497`.

- `Open` compares `ProcessInfo` by reference, so re-attaching the same instance is a no-op; `Close()` first.
- A failed read on a dead process calls `Close()` internally, firing the close events on the caller's thread (`:170-177`).
- `ReadRemoteMemory(addr,size)` swallows the failure bool and returns zeros; use the `IntoBuffer` overload and surface the bool.
- Typed write extensions return `void`, so call `WriteRemoteMemory` directly to report success.

Process list: `Program.CoreFunctions.EnumerateProcesses()` → `IList<ProcessInfo>` (`Core/CoreFunctionsManager.cs:78`).
Custom backends: `RegisterFunctions(string, ICoreProcessFunctions) :48`, `SetActiveFunctionsProvider :56`.

Address formula: `AddressParser/Parser.cs:178 Parse(string)` + `Interpreter.Execute(IExpression, IProcessReader)`
(`Interpreter.cs:10`). All numbers are hex (`10` == `0x10`); module refs MUST be `<name.exe>`.
Use `Parser`+`Interpreter` (pure, thread-safe), NOT `RemoteProcess.ParseAddress` (unlocked cache).

Scanner (`MemoryScanner/Scanner.cs:15`): `Search(IScanComparer, IProgress<int>, CancellationToken) :176`,
`GetResults() :73`, `UndoLastScan() :95`, `CanUndoLastScan :41`, `TotalResultCount :36`.
Comparers in `MemoryScanner/Comparer/`: Byte/Short/Integer/Long/Float/Double/ArrayOfBytes/String/RegexString.
`ScanSettings` `MemoryScanner/ScanSettings.cs:12`. `EnableFastScan` is dead; the worker always
strides `FastScanAlignment`. No scan reset: dispose and construct a new `Scanner`.
Undo history is `CircularBuffer<ScanResultStore>(3)` ⇒ 2 undos.
`GetResults()` addresses are absolute only after that call; enumerate before `Dispose()`.

Pattern scan: `MemoryScanner/PatternScanner.cs:19/:35/:52/:74`, `BytePattern.Parse :209`.
Dumper: `Memory/Dumper.cs:14/:28/:39`. Disassembler: `Memory/Disassembler.cs:30/:43/:90/:139/:196`.
Dissector: `Memory/NodeDissector.cs:12 DissectNodes` (mutates the tree ⇒ UI thread),
`:26 GuessNode` (pure ⇒ background-safe). Requires a filled `MemoryBuffer` (`Memory/MemoryBuffer.cs:9`).

Debugger (`Debugger/RemoteDebugger.cs:11`): `AddBreakpoint :26`, `RemoveBreakpoint :41`,
`FindWhatAccessesAddress :54`, `FindWhatWritesToAddress :59`, `FindCodeByBreakpoint :64`,
`StartDebuggerIfNeeded :Thread.cs:18`, `Terminate :Thread.cs:90`.
The Find* methods `Show()` a `FoundCodeForm` ⇒ UI thread. Only 4 HW registers (Dr0-Dr3),
`HardwareBreakpoint.Equals` compares register only. `SoftwareBreakpoint` handlers are never
dispatched (`RemoteDebugger.Handler.cs:12` matches `HardwareBreakpoint` only), so do not expose it.
A live breakpoint handler blocks on `Control.Invoke` into the UI thread. Never block the UI
thread from the MCP server while breakpoints are armed.

### Code generation
`ICodeGenerator` (`CodeGenerator/ICodeGenerator.cs:11`): `GenerateCode(IReadOnlyList<ClassNode>, IReadOnlyList<EnumDescription>, ILogger)`.
`CppCodeGenerator(CppTypeMapping) :126`, `CSharpCodeGenerator :14` (parameterless).
Headless generation is background-safe once the class list is snapshotted.

### Settings
`Settings` `Settings.cs:7` (plain POCO, no change notification), persisted to
`%LOCALAPPDATA%\ReClass.NET\settings.xml` by the internal `SettingsSerializer`, saved once at exit.
Plugin storage: `Settings.CustomData` (`Util/CustomDataMap.cs:14`, key convention `Plugin.Group.Item`)
and per-project `ReClassNetProject.CustomData`.
`Settings.PluginColor` and `Settings.RawDataEncoding` are never persisted (host bug).

## 3. Threading contract

**Background-safe (no marshalling):** `CoreFunctions.*`; `RemoteProcess` `IsValid`/`Modules`/`Sections`/
reads/writes/`GetModuleByName`/`GetSectionToPointer`/`EnumerateRemoteSectionsAndModules`/
`UpdateProcessInformationsAsync`/`ControlRemoteProcess`; `Scanner`; `PatternScanner`; `BytePattern`;
`Dumper`; `Disassembler`; `MemoryBuffer`; `NodeDissector.GuessNode`; `Parser`+`Interpreter`;
`NativeMethods`; `RemoteDebugger.AddBreakpoint`/`RemoveBreakpoint`/`IsAttached`.

**UI thread required (`Program.MainForm.Invoke`):** all project/node mutation and enumeration
(events drive `ProjectView`); `RemoteProcess.Open`/`Close`/`Dispose`; `LoadAllSymbolsAsync`
(`FromCurrentSynchronizationContext`); `RemoteDebugger.Find*`; everything in `UI/LinkedWindowFeatures.cs`;
`NodeDissector.DissectNodes`; `GetUserInterfaceInfo`; `ReClassClipboard` (STA).

**Not thread-safe, do not touch concurrently:** `RemoteProcess.ParseAddress` (unlocked `formulaCache`),
`ReadRemoteRuntimeTypeInformation` (unlocked `rttiCache`), `NamedAddresses` (plain `Dictionary`),
`CoreFunctionsManager.RegisterFunctions`/`SetActiveFunctionsProvider`.

No locking exists on `ReClassNetProject.classes`, `BaseContainerNode.nodes` or
`BaseNode.nodeIndex`, and the render loop reads them concurrently with any mutation.
That is why reads of the node tree must be marshalled too, not just writes. It is
the single worst defect in the existing NateWeav plugin.

## 4. Toolchain on this machine

- MSBuild 17.14.51.32402 (`C:\Program Files\Microsoft Visual Studio\2022\Professional\MSBuild\Current\Bin\MSBuild.exe`) → C# 13
- .NET SDK 9.0.317 and 10.0.400 (`dotnet build` → C# 14), no `global.json`
- .NET Framework 4.7.2 targeting pack installed ⇒ `net472` builds work with both toolchains
- Verified: `MSBuild ReClass.NET.csproj /p:Configuration=Release /p:Platform=x64 /p:SolutionDir=F:\Repos\ReClass.NET\` → `bin\Release\x64\ReClass.NET.exe`, exit 0
- `HttpListener` on `http://127.0.0.1:<port>/`, `http://localhost:<port>/`, `http://[::1]:<port>/`
  binds as a non-admin user with no URL ACL (measured on Windows 11 26200 under
  `runas /trustlevel:0x20000`). LAN IP, machine name, `+` and `*` all fail with code 5.
