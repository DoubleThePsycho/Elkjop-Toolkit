# Elkjøp Toolkit

Solution containing multiple support tools (`Elkjop Toolkit` (unified root dispatcher), `Support Tool`, `Account Setup` (formerly `Bypass OOBE`), `Toolkit.Shared`, and future additions).

## Multi-chat project — read this first

The user works on this solution from **two separate Claude chats**, alternating between them. Neither chat sees the other's edits or conversation history, so:

- **Files may have changed since your memory of them was formed.** A method rename, a deleted class, a moved file — any of it can happen in the other chat with no notice here.
- **Do not trust your own memory's description of a file's shape.** Re-read the actual current file contents before editing anything, even (especially) files you're confident you already know.
- **Treat on-disk state as the source of truth, memory as a hint.** If what you find on disk contradicts memory, disk wins — update or correct the memory rather than acting on the stale version.
- If the IDE surfaces a "file modified outside this session" notice, that's a strong signal the other chat has been active. Read the file in full before proceeding, don't just patch around the diff.
- When you learn something durable about this solution, save it to memory so the *other* chat can pick it up next time it looks — that's the main way the two chats stay in sync at all.

## Color preview build (development-only)

A third build configuration, `Color`, exists purely to compare theme/window-shell changes across
all three real windows at once without going through OOBE/Windows Update/a VM. It is never
deployed and never published to X:\.

Build the whole solution (COMReference means full-framework `MSBuild.exe`, not `dotnet build` -
same pre-existing constraint Debug/Release already have):
```
MSBuild.exe "Elkjøp Toolkit.slnx" -p:Configuration=Color
```
or per-project with `dotnet build "<project>.csproj" -c Color` for Account Setup/Elkjop Toolkit
(Support Tool still needs `MSBuild.exe` because of its own COMReference).

Then run the Color build of the root dispatcher:
```
"Elkjop Toolkit\bin\Color\net10.0-windows\Elkjop Toolkit.exe"
```

Expected result: the real Account Setup, Support Tool, and Restart-required windows all appear
together, automatically positioned side by side, with inert/synthetic data - no elevation prompt,
no OOBE/Windows Update/Sysprep/scheduled task/config work of any kind. Every `#if COLOR` block
lives directly in the file it modifies (`Elkjop Toolkit/Program.cs`, `Account Setup/Program.cs`,
`Support Tool/App.xaml.cs`, `Support Tool/Views/RebootCountdownWindow.xaml.cs`) plus one small
preview-only ViewModel (`Support Tool/Preview/ColorPreviewMainViewModel.cs`) - never scattered
into event handlers or production ViewModels.

## Source tree organization

Folders are for navigation; namespaces stay at the project/layer level. Every file under
`Support Tool/Services/**` is in `namespace Support_Tool.Services;`, and every file under
`Toolkit.Shared/Runtime/` (and the root-level helpers) is in `namespace Toolkit.Shared;`. The
config DTOs under `Toolkit.Shared/Configuration/` keep their own `Toolkit.Shared.Configuration`
namespace, while `JsonConfigStore`/`PortableConfigSource` in that same folder stay in
`Toolkit.Shared`. When you move a file, don't change its namespace unless you have a separate
reason to. A namespace change ripples `using` edits across all three apps.

`Support Tool/Services/` is grouped by responsibility:

- `Install/` - WinGet install pipeline: catalog, install service/executors (Real/DryRun),
  WinGet prerequisite and installed-state checks, install logging, app icon cache.
- `Uninstall/` - removal pipeline: uninstall service/executors (Appx, Program, Real/DryRun),
  Revo locating/handling, uninstall-command parsing, uninstall logging.
- `Inventory/` - what is installed on the machine (Appx apps, Win32 programs) and the
  Debloater rule set used to classify it.
- `Process/` - external process execution (`IProcessRunner`/`ProcessRunner`, results, lineage).
- `Runtime/` - the staged Runtime and session lifecycle: `config.json`/`session.json` loading,
  first-desktop actions, reboot, Runtime teardown.
- `WindowsUpdate/`, `Connectivity/` - the Windows Update engine and the Wi-Fi/connectivity code it uses.
- `RestorePoint/` - restore-point abstraction. Nothing calls it yet, and the real implementation intentionally throws.
- `Logger.cs` stays in the `Services/` root because every area uses it. Nothing else belongs there.
  Put a new service in the folder that matches its job. Add a new folder only for a new
  subsystem, and don't add a folder for a single file.

`Toolkit.Shared/` keeps `Configuration/` (config DTOs plus loading/locating the portable config),
`Runtime/` (Runtime staging, session state/guard, the resume scheduled-task XML), `Themes/` and
`Images/`. The cross-cutting helpers (`ElevationHelper`, `OobeGuard`, `ToolkitLogger`,
`ToolkitPaths`) live in the root. Don't move `Themes/ElkjopTheme.xaml` or `Images/*`: the WPF
projects link them by relative path in their `.csproj` and load them through
`pack://application:,,,/Themes/...` and `/Resources/favicon.ico` URIs.

## WPF/XAML maintainability rules

The Support Tool UI has already had a repository-wide XAML maintainability pass. Preserve that structure rather than reintroducing duplicated templates or unnecessary layout wrappers.

### Keep pages separate

The main Support Tool pages remain separate XAML files under `Support Tool/Views/`:

- `HomePage.xaml`
- `SetupHomePage.xaml`
- `ServicePage.xaml`
- `InstallPage.xaml`
- `DebloaterPage.xaml`
- `ProgramsPage.xaml`
- `SettingsPage.xaml`
- `WindowsUpdatePage.xaml`

`MainWindow.xaml` is the application shell: titlebar, sidebar/navigation, current-page host, persistent activity footers, and modal overlay.

Do not merge page contents into `MainWindow.xaml`. Individual pages must remain practical to open and edit by hand.

### Avoid redundant layout nesting

Do not add wrapper `Grid`, `StackPanel`, `Border`, `DockPanel`, etc. elements unless they provide an actual layout or visual purpose.

Examples of legitimate purposes:

- `Grid` provides rows/columns or overlays multiple children.
- `Border` provides background, border, corner radius or padding.
- `StackPanel` arranges multiple children.
- `ScrollViewer` provides scrolling.
- `Viewbox` / `Canvas` / `Path` nesting belongs to vector artwork.
- `ControlTemplate -> Border -> ContentPresenter` is normal WPF composition.

A wrapper containing one child should not exist merely to carry properties that can safely live on that child.

Do not flatten structures blindly just because the XML is deeply nested. WPF templates, styles, triggers and vector graphics naturally produce deep XML.

### Reuse existing shared styles

Before creating a new local style/template, inspect `Support Tool/App.xaml` and `Toolkit.Shared/Themes/ElkjopTheme.xaml`.

Existing shared patterns include:

- page headers / headings / subtitles;
- footer bars;
- thin progress bars;
- Windows Update progress bars;
- quick-access cards;
- Apps/Programs search and view-toggle controls;
- grid/list item containers;
- shared semantic theme brushes.

If several controls need the same visual treatment, prefer one shared style over copied XAML.

Do not create elaborate custom controls merely to reduce line count. The goal is readability and fewer duplicated definitions, not maximum abstraction.

### Sidebar

`MainWindow.xaml` intentionally keeps each navigation entry visible and editable individually.

The common appearance/hover/active behavior belongs in `SidebarNavButtonStyle` and related sidebar styles. Do not copy a full `Button.ControlTemplate` into every navigation entry again.

Do not extract the sidebar into another view unless there is a concrete maintainability benefit beyond reducing line count.

### Preserve visual behavior when refactoring XAML

A structural cleanup must not intentionally change:

- colors;
- spacing;
- margins/padding;
- dimensions;
- typography;
- borders/corner radii;
- bindings;
- commands;
- visibility logic;
- page routing;
- application behavior.

Use the `Color` configuration for visual comparison after non-trivial XAML changes.

A successful build alone does not prove visual equivalence.
