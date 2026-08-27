using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Setup_Launcher;

// setup.exe - the technician-facing double-click entry point, replacing the old
// "Bypass OOBE.bat" launcher (deleted 2026-08-17). Published as a single self-contained win-x86
// binary rather than the usual three win-x64/x86/arm64 RID builds this solution's real tools
// publish as - a true "AnyCPU" self-contained .NET publish isn't possible (self-contained
// always bundles a runtime for one specific architecture), but x86 code runs natively on x86,
// under WOW64 on x64, and under Windows' own built-in x86 emulation on ARM64, so one small
// self-contained x86 exe is the closest real equivalent: it runs unmodified on every
// architecture this toolkit targets without needing three separate published launchers.
//
// Carries its own requireAdministrator manifest (app.manifest) so the UAC prompt fires exactly
// once, here, before Main ever runs - see app.manifest for why the child inherits this
// process's already-elevated token instead of prompting a second time. NOT yet verified on a
// real VM that this is genuinely a single prompt end to end - flagged for that, same discipline
// as every other elevation-behavior claim in this project.
internal static class Program
{
    [STAThread]
    private static int Main()
    {
        // Mirrors the old .bat's own architecture detection exactly:
        // PROCESSOR_ARCHITEW6432 is only set when the current process is running under WOW64
        // (i.e. a 32-bit process on a 64-bit OS) and holds the *real* underlying architecture in
        // that case; PROCESSOR_ARCHITECTURE alone would report "x86" for a WOW64 process even on
        // a 64-bit machine, so it's only the fallback for genuinely native processes.
        var architecture = Environment.GetEnvironmentVariable("PROCESSOR_ARCHITEW6432");
        if (string.IsNullOrEmpty(architecture))
        {
            architecture = Environment.GetEnvironmentVariable("PROCESSOR_ARCHITECTURE");
        }

        var targetName = architecture?.ToUpperInvariant() switch
        {
            "AMD64" => "Account Setup-x64.exe",
            "ARM64" => "Account Setup-arm64.exe",
            "X86" => "Account Setup-x86.exe",
            _ => null,
        };

        if (targetName is null)
        {
            ShowError($"Unsupported or unrecognized processor architecture: {architecture}");
            return 1;
        }

        // AppContext.BaseDirectory resolves correctly to this exe's own folder even under
        // PublishSingleFile (guaranteed by .NET, not a temp extraction path) - same assumption
        // Account Setup's own StageSetupAgent already relies on for the identical sibling-folder
        // pattern.
        var targetPath = Path.Combine(AppContext.BaseDirectory, "Elkjop Toolkit", "Account Setup", targetName);

        if (!File.Exists(targetPath))
        {
            ShowError($"Could not find \"{targetPath}\"");
            return 1;
        }

        // UseShellExecute=false calls CreateProcess directly rather than going through the
        // shell's own elevation check - this process is already elevated (see app.manifest), so
        // the child simply inherits that token instead of triggering its own UAC prompt. Not
        // waited on: this launcher's whole job is done once the real tool is started.
        Process.Start(new ProcessStartInfo
        {
            FileName = targetPath,
            UseShellExecute = false,
        });

        return 0;
    }

    private static void ShowError(string message) =>
        MessageBoxW(IntPtr.Zero, message, "Elkjøp - Setup", MB_ICONERROR);

    private const uint MB_ICONERROR = 0x10;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);
}
