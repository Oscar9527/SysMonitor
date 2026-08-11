using System.Collections.Immutable;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Windows.Media.Imaging;
using SysMonitor.Models;

namespace SysMonitor.Services;

public sealed class ThemePackageService
{
    private const long MaximumPackageBytes = 5L * 1024 * 1024;
    private const int MaximumEntries = 32;
    private const long MaximumEntryBytes = 2L * 1024 * 1024;
    private const long MaximumExpandedBytes = 10L * 1024 * 1024;
    private const long MaximumJsonBytes = 256L * 1024;
    private const double MaximumCompressionRatio = 100d;
    private static readonly Regex IdPattern = new(
        "^[a-z0-9](?:[a-z0-9-]{0,63})$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly HashSet<string> ReservedDeviceNames =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "CON", "PRN", "AUX", "NUL", "CLOCK$",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
        };
    private static readonly ImmutableDictionary<string, string> AllowedFiles =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["manifest.json"] = "manifest.json",
            ["theme.json"] = "theme.json",
            ["assets/preview.png"] = "assets/preview.png",
            ["assets/band-background.png"] = "assets/band-background.png",
            ["assets/tray-icon.ico"] = "assets/tray-icon.ico",
            ["LICENSE.txt"] = "LICENSE.txt",
            ["README.md"] = "README.md"
        }.ToImmutableDictionary(StringComparer.OrdinalIgnoreCase);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        MaxDepth = 16,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    private readonly Version _applicationVersion;
    private readonly SemaphoreSlim _importGate = new(1, 1);

    public ThemePackageService(string? themesRoot = null, Version? applicationVersion = null)
    {
        ThemesRoot = themesRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "SysMonitor",
            "Themes");
        _applicationVersion = applicationVersion ??
            typeof(ThemePackageService).Assembly.GetName().Version ?? new Version(1, 0);
    }

    public string ThemesRoot { get; }

    public async Task<ThemeImportResult> ImportAsync(
        string packagePath,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await _importGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return ThemeImportResult.Failed(ThemeImportErrorCode.Cancelled);
        }

        try
        {
            return await Task.Run(
                () => ImportCore(packagePath, cancellationToken),
                CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _importGate.Release();
        }
    }

    internal Task<ResolvedTheme?> ValidateInstalledAsync(
        string directory,
        CancellationToken cancellationToken = default) =>
        Task.Run(() =>
        {
            try
            {
                ValidatedPayload payload = ReadAndValidateDirectory(directory, cancellationToken);
                return ResolvePaths(payload, directory, isBuiltIn: false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                return null;
            }
        }, cancellationToken);

    internal void PrepareRootAndCleanStaging(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        EnsureSafeRoot();
        foreach (string directory in Directory.EnumerateDirectories(ThemesRoot, ".staging-*", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsDirectChild(ThemesRoot, directory) || HasReparsePoint(directory, includeChildren: true))
            {
                continue;
            }

            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch
            {
                // A stale staging directory is best-effort cleanup only.
            }
        }
    }

    private ThemeImportResult ImportCore(string packagePath, CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(packagePath) || !File.Exists(packagePath))
            {
                return ThemeImportResult.Failed(ThemeImportErrorCode.PackageNotFound);
            }

            if (!string.Equals(Path.GetExtension(packagePath), ".smonitor-theme", StringComparison.OrdinalIgnoreCase))
            {
                return ThemeImportResult.Failed(ThemeImportErrorCode.InvalidPackage);
            }

            RejectReparsePath(packagePath, includeLeaf: true);
            var info = new FileInfo(packagePath);
            if (info.Length > MaximumPackageBytes)
            {
                return ThemeImportResult.Failed(ThemeImportErrorCode.LimitExceeded);
            }

            ValidatedPayload payload = ReadAndValidateArchive(packagePath, cancellationToken);
            PrepareRootAndCleanStaging(cancellationToken);
            RejectBuiltInOrInvalidId(payload.Identity.Id);
            if (FindCollidingDirectory(payload.Identity.Id) is not null)
            {
                return ThemeImportResult.Failed(ThemeImportErrorCode.DuplicateId);
            }

            string staging = Path.Combine(ThemesRoot, $".staging-{Guid.NewGuid():N}");
            string destination = Path.Combine(ThemesRoot, payload.Identity.Id);
            Directory.CreateDirectory(staging);
            try
            {
                ValidateStagingDirectory(staging);
                WritePayload(staging, payload.Files, cancellationToken);
                ValidateStagingDirectory(staging);
                ValidatedPayload revalidated = ReadAndValidateDirectory(staging, cancellationToken);
                if (!string.Equals(payload.IdentityToken, revalidated.IdentityToken, StringComparison.Ordinal))
                {
                    throw new ThemeValidationException(ThemeImportErrorCode.InvalidPackage);
                }

                if (FindCollidingDirectory(payload.Identity.Id) is not null || Directory.Exists(destination))
                {
                    return ThemeImportResult.Failed(ThemeImportErrorCode.DuplicateId);
                }

                ValidateStagingDirectory(staging);
                try
                {
                    Directory.Move(staging, destination);
                }
                catch (IOException) when (Directory.Exists(destination))
                {
                    return ThemeImportResult.Failed(ThemeImportErrorCode.DuplicateId);
                }

                staging = string.Empty;
                return ThemeImportResult.Succeeded(
                    ResolvePaths(revalidated, destination, isBuiltIn: false));
            }
            finally
            {
                if (staging.Length > 0 && Directory.Exists(staging) &&
                    IsDirectChild(ThemesRoot, staging) && !HasReparsePoint(staging, includeChildren: true))
                {
                    try { Directory.Delete(staging, recursive: true); } catch { }
                }
            }
        }
        catch (OperationCanceledException)
        {
            return ThemeImportResult.Failed(ThemeImportErrorCode.Cancelled);
        }
        catch (ThemeValidationException exception)
        {
            return ThemeImportResult.Failed(exception.Code);
        }
        catch (JsonException)
        {
            return ThemeImportResult.Failed(ThemeImportErrorCode.InvalidPackage);
        }
        catch (IOException)
        {
            return ThemeImportResult.Failed(ThemeImportErrorCode.IoFailure);
        }
        catch (UnauthorizedAccessException)
        {
            return ThemeImportResult.Failed(ThemeImportErrorCode.IoFailure);
        }
        catch
        {
            return ThemeImportResult.Failed(ThemeImportErrorCode.InvalidPackage);
        }
    }

    private ValidatedPayload ReadAndValidateArchive(string packagePath, CancellationToken cancellationToken)
    {
        using FileStream stream = new(
            packagePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.SequentialScan);
        if (stream.Length > MaximumPackageBytes)
        {
            throw new ThemeValidationException(ThemeImportErrorCode.LimitExceeded);
        }

        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
        if (archive.Entries.Count > MaximumEntries)
        {
            throw new ThemeValidationException(ThemeImportErrorCode.LimitExceeded);
        }

        long total = 0;
        var files = ImmutableDictionary.CreateBuilder<string, ImmutableArray<byte>>(StringComparer.OrdinalIgnoreCase);
        var normalizedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string normalized = NormalizeArchivePath(entry.FullName, out bool directory);
            string collisionKey = WindowsCollisionKey(normalized);
            if (!normalizedKeys.Add(collisionKey))
            {
                throw new ThemeValidationException(ThemeImportErrorCode.InvalidPath);
            }

            if (directory)
            {
                if (!string.Equals(normalized, "assets", StringComparison.OrdinalIgnoreCase) ||
                    entry.Length != 0)
                {
                    throw new ThemeValidationException(ThemeImportErrorCode.InvalidPath);
                }

                continue;
            }

            if (!AllowedFiles.TryGetValue(normalized, out string? canonical))
            {
                throw new ThemeValidationException(ThemeImportErrorCode.InvalidPath);
            }

            ValidateEntryLimits(entry.Length, entry.CompressedLength, ref total);
            using Stream input = entry.Open();
            files[canonical] = ReadExactly(input, entry.Length, cancellationToken);
        }

        return ValidatePayload(files.ToImmutable(), cancellationToken);
    }

    private ValidatedPayload ReadAndValidateDirectory(string directory, CancellationToken cancellationToken)
    {
        if (!Directory.Exists(directory) || HasReparsePoint(directory, includeChildren: false))
        {
            throw new ThemeValidationException(ThemeImportErrorCode.InvalidPath);
        }

        var files = ImmutableDictionary.CreateBuilder<string, ImmutableArray<byte>>(StringComparer.OrdinalIgnoreCase);
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (string path in EnumerateInstalledFiles(directory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            FileAttributes attributes = File.GetAttributes(path);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new ThemeValidationException(ThemeImportErrorCode.InvalidPath);
            }

            string relative = Path.GetRelativePath(directory, path).Replace('\\', '/');
            string normalized = NormalizeArchivePath(relative, out bool directoryEntry);
            if (directoryEntry || !AllowedFiles.TryGetValue(normalized, out string? canonical) ||
                !keys.Add(WindowsCollisionKey(normalized)))
            {
                throw new ThemeValidationException(ThemeImportErrorCode.InvalidPath);
            }

            using var input = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                64 * 1024,
                FileOptions.SequentialScan);
            long length = input.Length;
            ValidateEntryLimits(length, compressedLength: null, ref total);
            files[canonical] = ReadExactly(input, length, cancellationToken);
        }

        if (files.Count > MaximumEntries)
        {
            throw new ThemeValidationException(ThemeImportErrorCode.LimitExceeded);
        }

        return ValidatePayload(files.ToImmutable(), cancellationToken);
    }

    private ValidatedPayload ValidatePayload(
        ImmutableDictionary<string, ImmutableArray<byte>> files,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!files.TryGetValue("manifest.json", out ImmutableArray<byte> manifestBytes) ||
            !files.TryGetValue("theme.json", out ImmutableArray<byte> themeBytes))
        {
            throw new ThemeValidationException(ThemeImportErrorCode.InvalidPackage);
        }

        if (manifestBytes.Length > MaximumJsonBytes || themeBytes.Length > MaximumJsonBytes)
        {
            throw new ThemeValidationException(ThemeImportErrorCode.LimitExceeded);
        }

        ManifestDto manifest;
        ThemeDto theme;
        try
        {
            manifest = JsonSerializer.Deserialize<ManifestDto>(manifestBytes.AsSpan(), JsonOptions) ??
                throw new JsonException();
        }
        catch (JsonException)
        {
            throw new ThemeValidationException(ThemeImportErrorCode.InvalidManifest);
        }

        try
        {
            theme = JsonSerializer.Deserialize<ThemeDto>(themeBytes.AsSpan(), JsonOptions) ??
                throw new JsonException();
        }
        catch (JsonException)
        {
            throw new ThemeValidationException(ThemeImportErrorCode.InvalidTheme);
        }

        ThemeIdentity identity = ValidateManifest(manifest, files);
        ThemeDefinition definition = ValidateTheme(theme, files);
        ValidateAssets(files, cancellationToken);
        return new ValidatedPayload(identity, definition, ComputeIdentity(files), files,
            manifest.Preview, theme.Band.BackgroundImage, theme.TrayIcon);
    }

    private ThemeIdentity ValidateManifest(
        ManifestDto manifest,
        ImmutableDictionary<string, ImmutableArray<byte>> files)
    {
        if (manifest.SchemaVersion != 1)
        {
            throw new ThemeValidationException(ThemeImportErrorCode.InvalidManifest);
        }

        RejectBuiltInOrInvalidId(manifest.Id);
        RequireText(manifest.Name, 128, ThemeImportErrorCode.InvalidManifest);
        RequireText(manifest.Author, 128, ThemeImportErrorCode.InvalidManifest);
        if (!Version.TryParse(manifest.Version, out _))
        {
            throw new ThemeValidationException(ThemeImportErrorCode.InvalidManifest);
        }

        if (!Version.TryParse(manifest.MinSysMonitorVersion, out Version? minimum))
        {
            throw new ThemeValidationException(ThemeImportErrorCode.InvalidManifest);
        }

        if (minimum > _applicationVersion)
        {
            throw new ThemeValidationException(ThemeImportErrorCode.IncompatibleVersion);
        }

        ValidateOptionalAssetReference(manifest.Preview, "assets/preview.png", files,
            ThemeImportErrorCode.InvalidManifest);
        return new ThemeIdentity(
            manifest.Id,
            manifest.Name.Trim(),
            manifest.Author.Trim(),
            manifest.Version,
            manifest.MinSysMonitorVersion);
    }

    private static ThemeDefinition ValidateTheme(
        ThemeDto theme,
        ImmutableDictionary<string, ImmutableArray<byte>> files)
    {
        if (theme.Colors is null || theme.Metrics is null || theme.Shape is null || theme.Band is null)
        {
            throw new ThemeValidationException(ThemeImportErrorCode.InvalidTheme);
        }

        string[] colors =
        {
            theme.Colors.AppBackground, theme.Colors.Surface, theme.Colors.Text,
            theme.Colors.Secondary, theme.Colors.Tertiary, theme.Colors.Separator,
            theme.Colors.Control, theme.Colors.Accent, theme.Metrics.Cpu,
            theme.Metrics.Memory, theme.Metrics.Gpu, theme.Metrics.Warning,
            theme.Metrics.Critical, theme.Band.BackgroundColor
        };
        if (colors.Any(color => !IsColor(color)) ||
            (theme.Band.TextColor is not null && !IsColor(theme.Band.TextColor)) ||
            (theme.Band.SeparatorColor is not null && !IsColor(theme.Band.SeparatorColor)))
        {
            throw new ThemeValidationException(ThemeImportErrorCode.InvalidTheme);
        }

        if (theme.Shape.GroupCornerRadius is not double groupCornerRadius ||
            theme.Shape.ShadowOpacity is not double shadowOpacity ||
            theme.Band.CornerRadius is not double bandCornerRadius ||
            !IsInRange(groupCornerRadius, 0, 32) ||
            !IsInRange(shadowOpacity, 0, 1) ||
            !IsInRange(bandCornerRadius, 0, 32))
        {
            throw new ThemeValidationException(ThemeImportErrorCode.InvalidTheme);
        }

        ValidateOptionalAssetReference(theme.Band.BackgroundImage, "assets/band-background.png", files,
            ThemeImportErrorCode.InvalidTheme);
        ValidateOptionalAssetReference(theme.TrayIcon, "assets/tray-icon.ico", files,
            ThemeImportErrorCode.InvalidTheme);
        return new ThemeDefinition(
            new ThemePalette(
                theme.Colors.AppBackground, theme.Colors.Surface, theme.Colors.Text,
                theme.Colors.Secondary, theme.Colors.Tertiary, theme.Colors.Separator,
                theme.Colors.Control, theme.Colors.Accent),
            new ThemeMetricPalette(
                theme.Metrics.Cpu, theme.Metrics.Memory, theme.Metrics.Gpu,
                theme.Metrics.Warning, theme.Metrics.Critical),
            new ThemeShape(groupCornerRadius, shadowOpacity),
            new ThemeBandStyle(
                theme.Band.BackgroundColor, bandCornerRadius,
                theme.Band.TextColor, theme.Band.SeparatorColor, null),
            null);
    }

    private static void ValidateAssets(
        ImmutableDictionary<string, ImmutableArray<byte>> files,
        CancellationToken cancellationToken)
    {
        if (files.TryGetValue("assets/preview.png", out ImmutableArray<byte> preview))
        {
            ValidatePng(preview, 2048, 2048, 4_194_304, cancellationToken);
        }

        if (files.TryGetValue("assets/band-background.png", out ImmutableArray<byte> band))
        {
            ValidatePng(band, 4096, 256, 1_048_576, cancellationToken);
        }

        if (files.TryGetValue("assets/tray-icon.ico", out ImmutableArray<byte> icon))
        {
            ValidateIco(icon, cancellationToken);
        }
    }

    private static void ValidatePng(
        ImmutableArray<byte> bytes,
        int maximumWidth,
        int maximumHeight,
        long maximumPixels,
        CancellationToken cancellationToken)
    {
        ReadOnlySpan<byte> signature = stackalloc byte[] { 137, 80, 78, 71, 13, 10, 26, 10 };
        if (bytes.Length < signature.Length || !bytes.AsSpan(0, signature.Length).SequenceEqual(signature))
        {
            throw new ThemeValidationException(ThemeImportErrorCode.InvalidAsset);
        }

        ValidatePngHeader(bytes.AsSpan(), maximumWidth, maximumHeight, maximumPixels);

        BitmapDecoder decoder = DecodeImage(bytes, cancellationToken);
        if (decoder.Frames.Count != 1)
        {
            throw new ThemeValidationException(ThemeImportErrorCode.InvalidAsset);
        }

        BitmapFrame frame = decoder.Frames[0];
        if (frame.PixelWidth <= 0 || frame.PixelHeight <= 0 ||
            frame.PixelWidth > maximumWidth || frame.PixelHeight > maximumHeight ||
            (long)frame.PixelWidth * frame.PixelHeight > maximumPixels)
        {
            throw new ThemeValidationException(ThemeImportErrorCode.InvalidAsset);
        }
    }

    private static void ValidateIco(ImmutableArray<byte> bytes, CancellationToken cancellationToken)
    {
        if (bytes.Length < 6 || bytes[0] != 0 || bytes[1] != 0 || bytes[2] != 1 || bytes[3] != 0)
        {
            throw new ThemeValidationException(ThemeImportErrorCode.InvalidAsset);
        }

        int declaredFrames = ReadUInt16LittleEndian(bytes.AsSpan(), 4);
        if (declaredFrames is < 1 or > 10 || bytes.Length < 6 + declaredFrames * 16)
        {
            throw new ThemeValidationException(ThemeImportErrorCode.InvalidAsset);
        }

        long declaredPixels = 0;
        for (int index = 0; index < declaredFrames; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int entryOffset = 6 + index * 16;
            int width = bytes[entryOffset] == 0 ? 256 : bytes[entryOffset];
            int height = bytes[entryOffset + 1] == 0 ? 256 : bytes[entryOffset + 1];
            declaredPixels += (long)width * height;
            uint dataLength = ReadUInt32LittleEndian(bytes.AsSpan(), entryOffset + 8);
            uint dataOffset = ReadUInt32LittleEndian(bytes.AsSpan(), entryOffset + 12);
            if (declaredPixels > 655_360 || dataLength == 0 ||
                dataOffset > bytes.Length || dataLength > bytes.Length - dataOffset)
            {
                throw new ThemeValidationException(ThemeImportErrorCode.InvalidAsset);
            }

            ReadOnlySpan<byte> payload = bytes.AsSpan((int)dataOffset, (int)dataLength);
            PreflightIconPayload(payload);
        }

        BitmapDecoder decoder = DecodeImage(bytes, cancellationToken);
        if (decoder.Frames.Count is < 1 or > 10)
        {
            throw new ThemeValidationException(ThemeImportErrorCode.InvalidAsset);
        }

        long pixels = 0;
        foreach (BitmapFrame frame in decoder.Frames)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (frame.PixelWidth <= 0 || frame.PixelHeight <= 0 ||
                frame.PixelWidth > 256 || frame.PixelHeight > 256)
            {
                throw new ThemeValidationException(ThemeImportErrorCode.InvalidAsset);
            }

            pixels += (long)frame.PixelWidth * frame.PixelHeight;
            if (pixels > 655_360)
            {
                throw new ThemeValidationException(ThemeImportErrorCode.InvalidAsset);
            }
        }
    }

    private static BitmapDecoder DecodeImage(
        ImmutableArray<byte> bytes,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            using var stream = new MemoryStream(bytes.ToArray(), writable: false);
            return BitmapDecoder.Create(
                stream,
                BitmapCreateOptions.PreservePixelFormat,
                BitmapCacheOption.OnLoad);
        }
        catch
        {
            throw new ThemeValidationException(ThemeImportErrorCode.InvalidAsset);
        }
    }

    private static void ValidatePngHeader(
        ReadOnlySpan<byte> bytes,
        int maximumWidth,
        int maximumHeight,
        long maximumPixels)
    {
        if (bytes.Length < 33 || ReadUInt32BigEndian(bytes, 8) != 13 ||
            !bytes.Slice(12, 4).SequenceEqual("IHDR"u8))
        {
            throw new ThemeValidationException(ThemeImportErrorCode.InvalidAsset);
        }

        uint width = ReadUInt32BigEndian(bytes, 16);
        uint height = ReadUInt32BigEndian(bytes, 20);
        if (width == 0 || height == 0 || width > maximumWidth || height > maximumHeight ||
            (long)width * height > maximumPixels)
        {
            throw new ThemeValidationException(ThemeImportErrorCode.InvalidAsset);
        }
    }

    private static void PreflightIconPayload(ReadOnlySpan<byte> payload)
    {
        ReadOnlySpan<byte> pngSignature = stackalloc byte[] { 137, 80, 78, 71, 13, 10, 26, 10 };
        if (payload.Length >= pngSignature.Length &&
            payload[..pngSignature.Length].SequenceEqual(pngSignature))
        {
            ValidatePngHeader(payload, 256, 256, 65_536);
            return;
        }

        if (payload.Length < 12)
        {
            throw new ThemeValidationException(ThemeImportErrorCode.InvalidAsset);
        }

        uint headerSize = ReadUInt32LittleEndian(payload, 0);
        int width;
        int doubledHeight;
        if (headerSize == 12)
        {
            width = ReadUInt16LittleEndian(payload, 4);
            doubledHeight = ReadUInt16LittleEndian(payload, 6);
        }
        else if (headerSize >= 40 && payload.Length >= headerSize)
        {
            long rawWidth = ReadInt32LittleEndian(payload, 4);
            long rawHeight = ReadInt32LittleEndian(payload, 8);
            if (rawWidth is int.MinValue || rawHeight is int.MinValue)
            {
                throw new ThemeValidationException(ThemeImportErrorCode.InvalidAsset);
            }

            width = (int)Math.Abs(rawWidth);
            doubledHeight = (int)Math.Abs(rawHeight);
        }
        else
        {
            throw new ThemeValidationException(ThemeImportErrorCode.InvalidAsset);
        }

        int height = doubledHeight / 2;
        if (width is < 1 or > 256 || height is < 1 or > 256)
        {
            throw new ThemeValidationException(ThemeImportErrorCode.InvalidAsset);
        }
    }

    private static ushort ReadUInt16LittleEndian(ReadOnlySpan<byte> bytes, int offset) =>
        (ushort)(bytes[offset] | bytes[offset + 1] << 8);

    private static uint ReadUInt32LittleEndian(ReadOnlySpan<byte> bytes, int offset) =>
        (uint)(bytes[offset] | bytes[offset + 1] << 8 | bytes[offset + 2] << 16 | bytes[offset + 3] << 24);

    private static int ReadInt32LittleEndian(ReadOnlySpan<byte> bytes, int offset) =>
        unchecked((int)ReadUInt32LittleEndian(bytes, offset));

    private static uint ReadUInt32BigEndian(ReadOnlySpan<byte> bytes, int offset) =>
        ((uint)bytes[offset] << 24) | ((uint)bytes[offset + 1] << 16) |
        ((uint)bytes[offset + 2] << 8) | bytes[offset + 3];

    private void WritePayload(
        string staging,
        ImmutableDictionary<string, ImmutableArray<byte>> files,
        CancellationToken cancellationToken)
    {
        foreach ((string relative, ImmutableArray<byte> bytes) in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidateStagingDirectory(staging);
            string destination = Path.Combine(staging, relative.Replace('/', Path.DirectorySeparatorChar));
            string fullDestination = Path.GetFullPath(destination);
            string stagingPrefix = Path.GetFullPath(staging).TrimEnd(Path.DirectorySeparatorChar) +
                Path.DirectorySeparatorChar;
            if (!fullDestination.StartsWith(stagingPrefix, StringComparison.OrdinalIgnoreCase))
            {
                throw new ThemeValidationException(ThemeImportErrorCode.InvalidPath);
            }

            string? parent = Path.GetDirectoryName(destination);
            if (parent is not null)
            {
                Directory.CreateDirectory(parent);
                RejectReparsePath(parent, includeLeaf: true);
            }

            using var output = new FileStream(
                destination, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                64 * 1024, FileOptions.WriteThrough);
            output.Write(bytes.AsSpan());
            output.Flush(flushToDisk: true);
        }
    }

    private void ValidateStagingDirectory(string staging)
    {
        EnsureSafeRoot();
        if (!Directory.Exists(staging) || !IsDirectChild(ThemesRoot, staging) ||
            HasReparsePoint(staging, includeChildren: true))
        {
            throw new ThemeValidationException(ThemeImportErrorCode.InvalidPath);
        }

        RejectReparsePath(staging, includeLeaf: true);
    }

    private ResolvedTheme ResolvePaths(ValidatedPayload payload, string root, bool isBuiltIn)
    {
        string? preview = ResolveOptionalPath(root, payload.PreviewReference);
        string? band = ResolveOptionalPath(root, payload.BandReference);
        string? tray = ResolveOptionalPath(root, payload.TrayReference);
        ThemeDefinition definition = payload.Definition with
        {
            Band = payload.Definition.Band with { BackgroundImagePath = band },
            TrayIconPath = tray
        };
        return new ResolvedTheme(
            payload.Identity, definition, payload.IdentityToken, isBuiltIn,
            preview, band, tray);
    }

    private static string? ResolveOptionalPath(string root, string? relative) =>
        relative is null
            ? null
            : Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));

    private string? FindCollidingDirectory(string id)
    {
        string desired = WindowsCollisionKey(id);
        foreach (string directory in Directory.EnumerateDirectories(ThemesRoot, "*", SearchOption.TopDirectoryOnly))
        {
            if (HasReparsePoint(directory, includeChildren: false))
            {
                continue;
            }

            if (string.Equals(
                    WindowsCollisionKey(Path.GetFileName(directory)),
                    desired,
                    StringComparison.OrdinalIgnoreCase))
            {
                return directory;
            }
        }

        return null;
    }

    private void EnsureSafeRoot()
    {
        string fullRoot = Path.GetFullPath(ThemesRoot);
        if (Directory.Exists(fullRoot))
        {
            RejectReparsePath(fullRoot, includeLeaf: true);
            return;
        }

        string? parent = Path.GetDirectoryName(fullRoot);
        if (parent is not null && Directory.Exists(parent))
        {
            RejectReparsePath(parent, includeLeaf: true);
        }

        Directory.CreateDirectory(fullRoot);
        RejectReparsePath(fullRoot, includeLeaf: true);
    }

    private static void RejectReparsePath(string path, bool includeLeaf)
    {
        string full = Path.GetFullPath(path);
        string? current = includeLeaf ? full : Path.GetDirectoryName(full);
        while (!string.IsNullOrEmpty(current) && (Directory.Exists(current) || File.Exists(current)))
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new ThemeValidationException(ThemeImportErrorCode.InvalidPath);
            }

            current = Path.GetDirectoryName(current);
        }
    }

    private static bool HasReparsePoint(string path, bool includeChildren)
    {
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            {
                return true;
            }

            if (!includeChildren || !Directory.Exists(path))
            {
                return false;
            }

            var pending = new Stack<string>();
            pending.Push(path);
            while (pending.Count > 0)
            {
                foreach (string item in Directory.EnumerateFileSystemEntries(
                             pending.Pop(), "*", SearchOption.TopDirectoryOnly))
                {
                    FileAttributes attributes = File.GetAttributes(item);
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        return true;
                    }

                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        pending.Push(item);
                    }
                }
            }

            return false;
        }
        catch
        {
            return true;
        }
    }

    private static bool IsDirectChild(string parent, string child) =>
        string.Equals(
            Path.GetDirectoryName(Path.GetFullPath(child).TrimEnd(Path.DirectorySeparatorChar)),
            Path.GetFullPath(parent).TrimEnd(Path.DirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);

    private static IEnumerable<string> EnumerateInstalledFiles(string root)
    {
        foreach (string item in Directory.EnumerateFileSystemEntries(
                     root, "*", SearchOption.TopDirectoryOnly))
        {
            FileAttributes attributes = File.GetAttributes(item);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new ThemeValidationException(ThemeImportErrorCode.InvalidPath);
            }

            if ((attributes & FileAttributes.Directory) == 0)
            {
                yield return item;
                continue;
            }

            if (!string.Equals(Path.GetFileName(item), "assets", StringComparison.OrdinalIgnoreCase))
            {
                throw new ThemeValidationException(ThemeImportErrorCode.InvalidPath);
            }

            foreach (string asset in Directory.EnumerateFileSystemEntries(
                         item, "*", SearchOption.TopDirectoryOnly))
            {
                FileAttributes assetAttributes = File.GetAttributes(asset);
                if ((assetAttributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
                {
                    throw new ThemeValidationException(ThemeImportErrorCode.InvalidPath);
                }

                yield return asset;
            }
        }
    }

    private static string NormalizeArchivePath(string path, out bool directory)
    {
        if (string.IsNullOrEmpty(path) || path.IndexOf('\0') >= 0)
        {
            throw new ThemeValidationException(ThemeImportErrorCode.InvalidPath);
        }

        string slash = path.Replace('\\', '/');
        directory = slash.EndsWith("/", StringComparison.Ordinal);
        if (directory)
        {
            slash = slash[..^1];
        }

        if (slash.Length == 0 || slash.StartsWith("/", StringComparison.Ordinal) || slash.Contains(':'))
        {
            throw new ThemeValidationException(ThemeImportErrorCode.InvalidPath);
        }

        string[] segments = slash.Split('/');
        foreach (string segment in segments)
        {
            if (segment.Length == 0 || segment is "." or ".." ||
                !string.Equals(segment, segment.TrimEnd(' ', '.'), StringComparison.Ordinal) ||
                IsReservedDeviceName(segment))
            {
                throw new ThemeValidationException(ThemeImportErrorCode.InvalidPath);
            }
        }

        return string.Join('/', segments);
    }

    private static string WindowsCollisionKey(string path) =>
        string.Join('/', path.Replace('\\', '/').Split('/')
            .Select(segment => segment.TrimEnd(' ', '.').ToUpperInvariant()));

    private static bool IsReservedDeviceName(string segment)
    {
        string stem = segment.Split('.')[0];
        return ReservedDeviceNames.Contains(stem);
    }

    private static void RejectBuiltInOrInvalidId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id) || !IdPattern.IsMatch(id) ||
            id.StartsWith("builtin.", StringComparison.OrdinalIgnoreCase) ||
            id.EndsWith(' ') || id.EndsWith('.') || id.Contains('.') ||
            IsReservedDeviceName(id))
        {
            throw new ThemeValidationException(ThemeImportErrorCode.InvalidManifest);
        }
    }

    private static void ValidateEntryLimits(
        long length,
        long? compressedLength,
        ref long total)
    {
        if (length < 0 || length > MaximumEntryBytes || total > MaximumExpandedBytes - length)
        {
            throw new ThemeValidationException(ThemeImportErrorCode.LimitExceeded);
        }

        if (compressedLength is long compressed)
        {
            if (compressed < 0 || (compressed == 0 && length > 0) ||
                (compressed > 0 && length / (double)compressed > MaximumCompressionRatio))
            {
                throw new ThemeValidationException(ThemeImportErrorCode.LimitExceeded);
            }
        }

        total += length;
    }

    private static ImmutableArray<byte> ReadExactly(
        Stream input,
        long expectedLength,
        CancellationToken cancellationToken)
    {
        if (expectedLength > int.MaxValue)
        {
            throw new ThemeValidationException(ThemeImportErrorCode.LimitExceeded);
        }

        byte[] bytes = new byte[(int)expectedLength];
        int offset = 0;
        while (offset < bytes.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int read = input.Read(bytes, offset, bytes.Length - offset);
            if (read == 0)
            {
                throw new ThemeValidationException(ThemeImportErrorCode.InvalidPackage);
            }

            offset += read;
        }

        if (input.ReadByte() != -1)
        {
            throw new ThemeValidationException(ThemeImportErrorCode.InvalidPackage);
        }

        return ImmutableArray.Create(bytes);
    }

    private static void ValidateOptionalAssetReference(
        string? value,
        string allowed,
        ImmutableDictionary<string, ImmutableArray<byte>> files,
        ThemeImportErrorCode error)
    {
        if (value is null)
        {
            return;
        }

        if (!string.Equals(value, allowed, StringComparison.Ordinal) || !files.ContainsKey(allowed))
        {
            throw new ThemeValidationException(error);
        }
    }

    private static void RequireText(string? value, int maximum, ThemeImportErrorCode error)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximum || value.Any(char.IsControl))
        {
            throw new ThemeValidationException(error);
        }
    }

    private static bool IsColor(string? value) =>
        value is { Length: 7 or 9 } && value[0] == '#' &&
        value.AsSpan(1).IndexOfAnyExcept("0123456789abcdefABCDEF") < 0;

    private static bool IsInRange(double value, double minimum, double maximum) =>
        double.IsFinite(value) && value >= minimum && value <= maximum;

    private static string ComputeIdentity(
        ImmutableDictionary<string, ImmutableArray<byte>> files)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> nameLength = stackalloc byte[4];
        Span<byte> contentLength = stackalloc byte[8];
        foreach ((string name, ImmutableArray<byte> bytes) in files.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            byte[] encodedName = Encoding.UTF8.GetBytes(name);
            BinaryPrimitives.WriteInt32LittleEndian(nameLength, encodedName.Length);
            BinaryPrimitives.WriteInt64LittleEndian(contentLength, bytes.Length);
            hash.AppendData(nameLength);
            hash.AppendData(encodedName);
            hash.AppendData(contentLength);
            hash.AppendData(bytes.AsSpan());
        }

        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private sealed record ValidatedPayload(
        ThemeIdentity Identity,
        ThemeDefinition Definition,
        string IdentityToken,
        ImmutableDictionary<string, ImmutableArray<byte>> Files,
        string? PreviewReference,
        string? BandReference,
        string? TrayReference);

    private sealed class ThemeValidationException : Exception
    {
        internal ThemeValidationException(ThemeImportErrorCode code) => Code = code;
        internal ThemeImportErrorCode Code { get; }
    }

    private sealed class ManifestDto
    {
        public int SchemaVersion { get; set; }
        public required string Id { get; set; }
        public required string Name { get; set; }
        public required string Author { get; set; }
        public required string Version { get; set; }
        public required string MinSysMonitorVersion { get; set; }
        public string? Preview { get; set; }
    }

    private sealed class ThemeDto
    {
        public required ColorsDto Colors { get; set; }
        public required MetricsDto Metrics { get; set; }
        public required ShapeDto Shape { get; set; }
        public required BandDto Band { get; set; }
        public string? TrayIcon { get; set; }
    }

    private sealed class ColorsDto
    {
        public required string AppBackground { get; set; }
        public required string Surface { get; set; }
        public required string Text { get; set; }
        public required string Secondary { get; set; }
        public required string Tertiary { get; set; }
        public required string Separator { get; set; }
        public required string Control { get; set; }
        public required string Accent { get; set; }
    }

    private sealed class MetricsDto
    {
        public required string Cpu { get; set; }
        public required string Memory { get; set; }
        public required string Gpu { get; set; }
        public required string Warning { get; set; }
        public required string Critical { get; set; }
    }

    private sealed class ShapeDto
    {
        public double? GroupCornerRadius { get; set; }
        public double? ShadowOpacity { get; set; }
    }

    private sealed class BandDto
    {
        public required string BackgroundColor { get; set; }
        public double? CornerRadius { get; set; }
        public string? TextColor { get; set; }
        public string? SeparatorColor { get; set; }
        public string? BackgroundImage { get; set; }
    }
}
