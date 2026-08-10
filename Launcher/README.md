# Portable launcher

`SysMonitorLauncher.cs` is a small .NET Framework launcher that embeds the
framework-dependent SysMonitor core into one distributable EXE. It verifies
the extracted core, stops an older cached SysMonitor process tree, checks for
the .NET 8 Desktop Runtime x64, and starts the core without installing files
into the program directory.

From the repository root on Windows:

```powershell
.\Launcher\Build-Portable.ps1
```

The final file and its SHA-256 are written to `artifacts\SysMonitor.exe`.
