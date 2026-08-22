# Light and Standalone release builds

`SysMonitorLauncher.cs` is the launcher for the Light build. It embeds the
framework-dependent SysMonitor core into one EXE. If x64 .NET 8 Desktop
Runtime is missing, it offers a one-click link to Microsoft's official x64
installer and explains that the Standalone build needs no runtime install.

The launcher detects x64 Desktop Runtime through Registry64,
`DOTNET_ROOT_X64`, or the 64-bit Program Files directory. A custom portable
runtime should set `DOTNET_ROOT_X64`; a PATH-only x86 runtime is deliberately
not accepted.

From the repository root on Windows:

```powershell
.\Launcher\Build-Release.ps1
```

Requirements: Windows x64 and .NET 8 SDK 8.0.100 or newer. The script reads
`Version` and `AssemblyVersion` from `SysMonitor.csproj`. The release build
writes two versioned files and their SHA-256 values:

- `artifacts\SysMonitor-v1.0.6-Light.exe`: small framework-dependent build;
  guides the user to the official .NET installer when needed.
- `artifacts\SysMonitor-v1.0.6-Standalone.exe`: self-contained build with the
  .NET runtime included; no separate .NET installation is required.

Use `Build-Portable.ps1` only when rebuilding the Light artifact by itself.
