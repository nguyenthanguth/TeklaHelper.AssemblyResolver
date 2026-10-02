# TeklaHelper.AssemblyResolver

[![CI](https://github.com/nguyenthanguth/TeklaStructures.AssemblyResolver/actions/workflows/ci.yml/badge.svg)](https://github.com/nguyenthanguth/TeklaStructures.AssemblyResolver/actions/workflows/ci.yml)
[![NuGet](https://img.shields.io/nuget/v/TeklaHelper.AssemblyResolver.svg)](https://www.nuget.org/packages/TeklaHelper.AssemblyResolver)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)

Finds Tekla Structures installations in the Windows Registry and, at runtime, loads every DLL your application is missing from the folders of the Tekla Structures version it works with.

This lets a standalone application (console, WPF, WinForms, …) reference the Tekla Open API **without copying any Tekla DLLs** next to the executable. When the runtime cannot find a DLL — a Tekla Open API assembly such as `Tekla.Structures.Model`, or one of its dependencies such as `Trimble.Remoting` or `System.Memory` — the resolver loads it from the matching Tekla Structures installation.

## Features

- Discovers every installed Tekla Structures version from `HKLM` (both 64-bit and 32-bit registry views, `Trimble\Tekla Structures` and legacy `Tekla\Structures` keys). Keys that cannot be read are skipped instead of failing.
- Loads Tekla assemblies **only** from the installation with the same major version as requested (e.g. `2026.0.0.0` → Tekla 2026). It never silently loads a DLL from a different Tekla version unless you opt in.
- Loads any other missing DLL (e.g. `Trimble.Remoting`, `System.Memory`, `Google.Protobuf`) **only** from the Tekla installation in use, so DLLs of different Tekla versions are never mixed.
- Verifies the assembly name, version and `PublicKeyToken` of the file before loading it.
- Supports both the new layout (`bin`, `bin\Net48Runtime` on Tekla 2026+) and the old layout (`nt\bin`, `nt\bin\plugins` on Tekla 2020 and earlier), plus extra sub-folders you declare in `AdditionalSearchDirectories`.
- Targets `netstandard2.0`. The Tekla Open API itself requires .NET Framework 4.8 — see [Requirements](#requirements).

## Installation

```shell
dotnet add package TeklaHelper.AssemblyResolver
```

or with the Package Manager Console:

```powershell
Install-Package TeklaHelper.AssemblyResolver
```

## Quick start

1. In a project targeting `net48`, reference the Tekla Open API DLLs (or the `Tekla.Structures.*` NuGet packages) as usual, but **do not copy them to the output folder** — otherwise the runtime finds the local copy and the resolver is never asked:

   ```xml
   <Reference Include="Tekla.Structures.Model">
     <HintPath>C:\TeklaStructures\2026.0\bin\Tekla.Structures.Model.dll</HintPath>
     <Private>false</Private>
   </Reference>
   ```

   For a `PackageReference`, use `<ExcludeAssets>runtime</ExcludeAssets>`.

2. Register the resolver at the very start of `Main`, **before** calling any method that uses Tekla types. The JIT loads referenced assemblies as soon as such a method is compiled, so keep the Tekla code in a separate method:

   ```csharp
   using System.Runtime.CompilerServices;
   using TeklaHelper.AssemblyResolver;

   internal static class Program
   {
       private static void Main()
       {
           TeklaAssemblyResolver.Register();
           RunTeklaCode();
       }

       [MethodImpl(MethodImplOptions.NoInlining)]
       private static void RunTeklaCode()
       {
           var model = new Tekla.Structures.Model.Model();
           System.Console.WriteLine(model.GetConnectionStatus());
       }
   }
   ```

   `TeklaAssemblyResolver.Register()` creates one shared resolver from the registry; calling it again returns the same resolver. To control the lifetime or the installations yourself, create an instance instead:

   ```csharp
   using (var resolver = TeklaAssemblyResolver.CreateFromRegistry())
   {
       resolver.Register();
       RunTeklaCode();
   }
   ```

## Listing installed Tekla Structures versions

```csharp
foreach (TeklaInstallation installation in TeklaInstallationFinder.FindAll())
{
    Console.WriteLine(installation.Version);          // 2026.0
    Console.WriteLine(installation.InstallDirectory); // C:\TeklaStructures\2026.0
    Console.WriteLine(string.Join(", ", installation.GetBinDirectories()));
}
```

`TeklaInstallation` also exposes `ProductVersion`, `EnvironmentDirectory`, `ModelDirectory`, `MajorVersion`, `GetExtensionsDirectory()` / `EnsureExtensionsDirectory()` and `FindAssemblyPath(...)`.

## How assemblies are resolved

| Requested assembly | Installations searched | Version check |
| --- | --- | --- |
| Tekla assembly with a Tekla version (e.g. `Tekla.Structures.Model, Version=2026.0.0.0`) | Only installations with the same major version | File major version must match |
| Any other missing DLL (e.g. `Trimble.Remoting`, `Google.Protobuf`) | Only `ActiveInstallation`; all installations, newest first, while it is not known yet | File version must be ≥ requested version |

Directories are searched in this order inside each installation:

1. `bin\Net48Runtime` (Tekla 2026+, searched first when running on .NET Framework)
2. `bin` (Tekla 2021+)
3. `nt\bin\plugins` (Tekla 2020 and earlier)
4. `nt\bin` (Tekla 2020 and earlier)
5. `AdditionalSearchDirectories`, in the order they were added

A request counts as a *Tekla assembly with a Tekla version* when its name starts with `Tekla.` or it is signed with the Tekla public key (`PublicKeyToken=2f04dbe497b71114`), **and** its major version is a year (2000–2099) or the major version of an installed Tekla (old numbering such as `21.1`). Third-party DLLs with similar version numbers (e.g. `DevExpress.Data.v21.1`) and Tekla libraries with their own versioning (e.g. `Tekla.Common.Geometry 4.7`) are treated as dependencies.

`ActiveInstallation` is the installation the first Tekla assembly was loaded from. If a Tekla assembly was already loaded by other means (for example inside a Tekla Structures plugin), the resolver detects its installation from the file location.

If no matching installation is found the resolver returns `null`, so the runtime throws the usual `FileNotFoundException`.

### Additional search directories

Tekla keeps many DLLs in sub-folders (`bin\plugins\…`, `bin\applications\…`, `bin\Features\…`) that are not searched by default. Add the ones your application needs, relative to the installation directory:

```csharp
TeklaAssemblyResolver resolver = TeklaAssemblyResolver.Register();
resolver.AdditionalSearchDirectories.Add(@"bin\plugins\Tekla\Drawings\AdvancedGridLabels"); // Tekla 2021+
resolver.AdditionalSearchDirectories.Add(@"nt\bin\plugins\Tekla");                           // Tekla 2020 and earlier
```

Each relative path is resolved inside every installation, so the DLL still comes from the Tekla version in use; folders that do not exist in an installation are skipped. Absolute paths are used as-is. Sub-folders are not searched recursively on purpose: they can contain copies from other Tekla versions (for example `bin\applications\Tekla\Model\StatusSharing\Tekla.Structures.dll` is version 2024 in Tekla 2026).

### Version fallback

```csharp
TeklaAssemblyResolver.Register(allowVersionFallback: true);
// or, on your own instance
resolver.AllowVersionFallback = true;
```

Allows loading DLLs from another Tekla version when the requested version is not installed, and loading dependencies from other installations when the installation in use does not have them. This is disabled by default because API differences between versions can cause `MissingMethodException` or `TypeLoadException` at runtime.

### Logging

Set `Logger` to see what the resolver does: registration, each resolve request, the file that was loaded, files that were skipped and why, and requests that could not be resolved. Logging is off by default.

```csharp
TeklaAssemblyResolver.Register(logger: Console.WriteLine);

// or write to a file
TeklaAssemblyResolver resolver = TeklaAssemblyResolver.Register();
resolver.Logger = message => File.AppendAllText(@"C:\Temp\resolver.log", $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}");
```

Example output with Tekla Structures 2026:

```text
[TeklaAssemblyResolver] Registered. Tekla Structures installations: 2026.0 (C:\TeklaStructures\2026.0), 2020.0 (C:\TeklaStructures\2020.0)
[TeklaAssemblyResolver] Resolving Tekla.Structures.Model, Version=2026.0.0.0, Culture=neutral, PublicKeyToken=2f04dbe497b71114
[TeklaAssemblyResolver] Tekla.Structures.Model: loaded C:\TeklaStructures\2026.0\bin\Tekla.Structures.Model.dll
[TeklaAssemblyResolver] Active installation: Tekla Structures 2026.0 (C:\TeklaStructures\2026.0)
[TeklaAssemblyResolver] Resolving Trimble.Remoting, Version=4.0.0.0, Culture=neutral, PublicKeyToken=a70cba4ef557ee03
[TeklaAssemblyResolver] Trimble.Remoting: loaded C:\TeklaStructures\2026.0\bin\Trimble.Remoting.dll
```

The logger can be called from several threads at once. Exceptions thrown by it are ignored, and it is never called recursively, so a logger that itself needs a missing DLL cannot break or loop the resolver.

## API overview

| Type | Member | Description |
| --- | --- | --- |
| `TeklaInstallationFinder` | `FindAll()` | Returns all installations, newest first. Empty on non-Windows platforms. |
| `TeklaAssemblyResolver` | `static Register(allowVersionFallback = false, logger = null)` | Creates (once) and registers a shared resolver from the registry, and returns it. |
| | `CreateFromRegistry()` | Creates a resolver for all installations found in the registry. |
| | `Register()` / `Unregister()` / `Dispose()` | Subscribes to / unsubscribes from `AppDomain.CurrentDomain.AssemblyResolve`. |
| | `FindAssemblyPath(AssemblyName)` | Returns the path of the matching DLL without loading it. |
| | `LoadAssembly(AssemblyName)` | Loads the matching DLL (or returns the already loaded one). |
| | `AdditionalSearchDirectories` | Extra folders, relative to each installation, searched after the standard bin folders. |
| | `Logger` | `Action<string>` that receives diagnostic messages; null disables logging. |
| | `Installations`, `ActiveInstallation`, `AllowVersionFallback` | Resolver state and options. |

## Requirements

- Windows with at least one Tekla Structures version installed.
- An application that uses the Tekla Open API must target **.NET Framework 4.8** (`net48`).

> [!NOTE]
> The resolver itself targets `netstandard2.0` and also loads DLLs correctly on .NET 6/8+. However, the Tekla Open API communicates with Tekla Structures through .NET Remoting, which does not exist on .NET Core / .NET 5+. On .NET 8, `new Model()` fails inside Tekla's own code with `TypeLoadException: Could not load type 'System.Runtime.Remoting.Lifetime.ClientSponsor'`, and `GetConnectionStatus()` returns `false`. This is a Tekla Open API limitation and cannot be fixed by the resolver. On .NET 6/8+ you can still use `TeklaInstallationFinder` to list installations.

## Building from source

```shell
git clone https://github.com/nguyenthanguth/TeklaStructures.AssemblyResolver.git
cd TeklaStructures.AssemblyResolver
dotnet build TeklaHelper.AssemblyResolver.slnx -c Release
```

### Packing

```shell
dotnet pack TeklaHelper.AssemblyResolver/TeklaHelper.AssemblyResolver.csproj -c Release -o artifacts
```

## License

[MIT](LICENSE)

Tekla and Tekla Structures are trademarks of Trimble Inc. This project is not affiliated with or endorsed by Trimble.
