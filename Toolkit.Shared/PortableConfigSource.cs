using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace Toolkit.Shared;

/// <summary>
/// Records which portable USB volume a staged Runtime\config.json originally came from, so a
/// later Settings save can find that same volume again after a reboot changes its drive letter.
/// Identity is the volume's serial number (assigned when the drive was formatted, stable across
/// reboots and re-plugging into a different port), never the drive letter or volume label -
/// both of those can legitimately change. <see cref="OriginalDriveRoot"/> is carried along
/// purely for diagnostics/logging; resolving a source must never trust it as authoritative.
/// </summary>
public sealed class PortableConfigSourceInfo
{
    public required string VolumeSerialNumber { get; init; }

    // e.g. "Elkjop Toolkit\config.json" - combined with whichever drive root currently matches
    // VolumeSerialNumber to relocate the file.
    public required string RelativeConfigPath { get; init; }

    public string? OriginalDriveRoot { get; init; }
}

/// <summary>
/// Creates, persists, and resolves <see cref="PortableConfigSourceInfo"/> - the shared
/// implementation both staging entry points (Account Setup, Service Launcher) and Support Tool
/// itself use, so there is exactly one place that knows how to identify a volume and stage/read
/// the source.json file (<see cref="RuntimeStaging.SourcePath"/>), not three independent copies.
/// </summary>
public static class PortableConfigSourceLocator
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// Builds source metadata from an actual, currently-reachable config file path (e.g.
    /// "F:\Elkjop Toolkit\config.json" at staging time, while the USB is still known-connected).
    /// Returns <see langword="null"/> if the volume serial number can't be determined - callers
    /// staging fresh must treat that as a staging failure rather than silently proceeding with no
    /// portable identity at all.
    /// </summary>
    public static PortableConfigSourceInfo? CreateFromConfigPath(string configPath)
    {
        var root = Path.GetPathRoot(configPath);
        if (string.IsNullOrEmpty(root))
        {
            return null;
        }

        if (!TryGetVolumeSerialNumber(root, out var serial))
        {
            return null;
        }

        return new PortableConfigSourceInfo
        {
            VolumeSerialNumber = serial,
            RelativeConfigPath = Path.GetRelativePath(root, configPath),
            OriginalDriveRoot = root,
        };
    }

    // Same temp-file + atomic-rename shape RuntimeStaging/SessionService already use for
    // session.json - a crash mid-write can never leave a half-written source.json behind.
    public static void Save(PortableConfigSourceInfo info, string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempPath = path + ".tmp";
        File.WriteAllText(tempPath, JsonSerializer.Serialize(info, JsonOptions));
        File.Move(tempPath, path, overwrite: true);
    }

    /// <summary>
    /// Missing or corrupt source.json is never fatal to the caller - it just means portable
    /// Settings saving isn't available for this session (e.g. an older staged Runtime that
    /// predates this feature, or one staged without a source.json for some other reason). Callers
    /// must treat a <see langword="null"/> return as "local-only saves", not as an error.
    /// </summary>
    public static PortableConfigSourceInfo? Load(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            return JsonSerializer.Deserialize<PortableConfigSourceInfo>(File.ReadAllText(path), JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Finds the currently-mounted drive whose volume serial number matches, and returns the full
    /// path to its config file - or <see langword="null"/> if no currently-ready drive matches
    /// (USB not connected, or connected but the file itself is missing). Deliberately does not
    /// assume <see cref="DriveType.Removable"/> - some USB enclosures/card readers report as
    /// Fixed, and a technician could in principle stage from a fixed drive too - every ready
    /// mounted drive root is checked.
    /// </summary>
    public static string? Resolve(PortableConfigSourceInfo info)
    {
        foreach (var root in EnumerateReadyDriveRoots())
        {
            if (!TryGetVolumeSerialNumber(root, out var serial) ||
                !string.Equals(serial, info.VolumeSerialNumber, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var candidate = Path.Combine(root, info.RelativeConfigPath);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static IEnumerable<string> EnumerateReadyDriveRoots()
    {
        foreach (var drive in DriveInfo.GetDrives())
        {
            bool ready;
            try
            {
                // IsReady itself can throw for a drive that disappears mid-enumeration (e.g. a
                // card reader with no card) - treat that exactly like "not ready".
                ready = drive.IsReady;
            }
            catch
            {
                ready = false;
            }

            if (ready)
            {
                yield return drive.RootDirectory.FullName;
            }
        }
    }

    private static bool TryGetVolumeSerialNumber(string rootPathName, out string serial)
    {
        var normalizedRoot = rootPathName.EndsWith('\\') ? rootPathName : rootPathName + "\\";
        var volumeNameBuffer = new StringBuilder(261);
        var fileSystemNameBuffer = new StringBuilder(261);

        try
        {
            if (NativeMethods.GetVolumeInformation(
                    normalizedRoot, volumeNameBuffer, volumeNameBuffer.Capacity,
                    out var serialNumber, out _, out _,
                    fileSystemNameBuffer, fileSystemNameBuffer.Capacity))
            {
                serial = serialNumber.ToString("X8");
                return true;
            }
        }
        catch
        {
            // A drive can fail/disconnect between IsReady returning true and this call -
            // treat exactly like "couldn't determine the serial", never crash the caller.
        }

        serial = string.Empty;
        return false;
    }

    private static class NativeMethods
    {
        [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
        public static extern bool GetVolumeInformation(
            string rootPathName,
            StringBuilder volumeNameBuffer,
            int volumeNameSize,
            out uint volumeSerialNumber,
            out uint maximumComponentLength,
            out uint fileSystemFlags,
            StringBuilder fileSystemNameBuffer,
            int fileSystemNameSize);
    }
}
