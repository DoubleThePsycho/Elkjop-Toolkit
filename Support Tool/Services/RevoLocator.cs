using System.IO;
using Toolkit.Shared;

namespace Support_Tool.Services;

/// <summary>
/// Resolves the path to the technician USB's activated Revo Uninstaller Pro Portable CLI
/// (RevoUnPro.exe), using the same source.json/PortableConfigSourceLocator identity infrastructure
/// ConfigService.SaveAsync already uses to relocate the originating USB after a reboot changes its
/// drive letter - never a hardcoded drive letter. The activated Revo folder itself is technician-
/// USB-only data (licensed/activated - never committed to the repo, never copied into Runtime);
/// this class only ever reads its path, never its contents beyond checking the exe exists.
/// </summary>
/// <remarks>
/// Current real USB layout (inspected directly, not guessed) - a temporary staging location per
/// the technician's own note; update <see cref="RevoFolderRelativePath"/> if/when it moves:
/// <c>&lt;USB root&gt;\Elkjop Toolkit\RevoUninstallerPro_Portable\{x64|x86}\RevoUnPro.exe</c>.
/// </remarks>
public sealed class RevoLocator
{
    private const string RevoFolderRelativePath = @"Elkjop Toolkit\RevoUninstallerPro_Portable";
    private const string RevoExeName = "RevoUnPro.exe";

    private readonly object _lock = new();
    private string? _cachedPath;

    /// <summary>
    /// Caches a SUCCESSFUL resolution for the rest of this process's lifetime - a technician
    /// replugging the USB after Revo was already found once won't trigger a re-scan for no
    /// reason. A failed lookup is deliberately NOT cached: the USB being absent/disconnected at
    /// the moment of one uninstall must not permanently disable Revo for the rest of the session
    /// once it's reconnected - the next call simply re-resolves (still just source.json + one
    /// File.Exists check, never a directory scan). If a previously-cached path later stops
    /// existing (USB removed, Revo folder moved), the cache is dropped and re-resolved too, so
    /// RevoUninstallService.IsAvailable never keeps claiming a path that's no longer there.
    /// </summary>
    public string? TryGetRevoExePath(Logger logger)
    {
        lock (_lock)
        {
            if (_cachedPath is not null)
            {
                if (File.Exists(_cachedPath))
                {
                    return _cachedPath;
                }

                logger.Info($"[Revo] Previously located path '{_cachedPath}' no longer exists - re-resolving.");
                _cachedPath = null;
            }

            var resolved = Resolve(logger);
            if (resolved is not null)
            {
                _cachedPath = resolved;
            }

            return resolved;
        }
    }

    private static string? Resolve(Logger logger)
    {
        var sourceInfo = PortableConfigSourceLocator.Load(RuntimeStaging.SourcePath);
        if (sourceInfo is null)
        {
            logger.Info("[Revo] No portable source recorded for this session - Revo unavailable, native uninstall only.");
            return null;
        }

        var resolvedConfigPath = PortableConfigSourceLocator.Resolve(sourceInfo);
        if (resolvedConfigPath is null)
        {
            logger.Info("[Revo] Source USB is not currently connected - Revo unavailable, native uninstall only.");
            return null;
        }

        var usbRoot = Path.GetPathRoot(resolvedConfigPath);
        if (string.IsNullOrEmpty(usbRoot))
        {
            return null;
        }

        var archFolder = Environment.Is64BitOperatingSystem ? "x64" : "x86";
        var candidate = Path.Combine(usbRoot, RevoFolderRelativePath, archFolder, RevoExeName);

        if (File.Exists(candidate))
        {
            logger.Info($"[Revo] Located: '{candidate}'.");
            return candidate;
        }

        logger.Info($"[Revo] Not found at expected path '{candidate}' - Revo unavailable, native uninstall only.");
        return null;
    }
}
