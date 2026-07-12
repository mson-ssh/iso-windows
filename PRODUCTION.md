# WinISOBuilder Production Checklist

## Runtime Requirements

- Windows 10/11 x64.
- .NET 10 SDK for building, pinned by `global.json`.
- Windows ADK Deployment Tools with `oscdimg.exe`.
- Administrator elevation. The app manifest requests elevation.
- Enough free disk space for at least two extracted Windows image copies.

## Build

```powershell
dotnet restore
dotnet build WinISOBuilder.sln -c Release
```

## Publish

```powershell
dotnet publish WinISOBuilder.csproj -p:PublishProfile=win-x64
```

The publish output is written to:

```text
bin\Release\net10.0-windows\publish\win-x64\
```

## Required Manual Test Matrix

Run these tests on an elevated Windows machine with Windows ADK installed.

| Case | Source | Expected result |
| --- | --- | --- |
| WIM, all editions | Windows ISO with `sources\install.wim` | ISO builds and boots. All editions remain. |
| WIM, excluded editions | Multi-edition WIM | Output WIM contains only selected editions. Drivers are injected into each selected edition. |
| ESD source | Windows ISO with `sources\install.esd` | Selected editions are exported to `install.wim`; `install.esd` is removed from workspace. |
| Empty driver folder | Folder with no `.inf` files | Build is blocked before DISM starts. |
| Cancel during servicing | Cancel while DISM is running | Active process stops and mounted image is discarded where possible. |
| Output not writable | Protected output directory | Build is blocked before DISM starts. |
| BIOS boot | Hyper-V Generation 1 or equivalent | ISO reaches Windows Setup. |
| UEFI boot | Hyper-V Generation 2 or equivalent | ISO reaches Windows Setup. |
| Lab mode | Unattended enabled | Local account and first-run policies are applied in a trusted lab VM. |

## Diagnostic Logs

Tool logs are written under:

```text
%TEMP%\WinISOBuilder\logs
```

Each external tool invocation records command line, exit code, stdout, and stderr.
