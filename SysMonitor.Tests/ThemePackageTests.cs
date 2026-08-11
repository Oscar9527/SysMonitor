using System.IO;
using System.IO.Compression;
using System.Text;
using SysMonitor.Models;
using SysMonitor.Services;

namespace SysMonitor.Tests;

public sealed class ThemePackageTests
{
    [Fact]
    public async Task ValidPackageInstallsAndReturnsOnlyValidatedPaths()
    {
        using var temp = new ThemeTestDirectory();
        string package = ThemeTestPackage.Create(
            temp.Path,
            "ocean",
            preview: "assets/preview.png",
            trayIcon: "assets/tray-icon.ico",
            bandImage: "assets/band-background.png",
            additionalEntries:
            [
                ("assets/preview.png", ThemeTestPackage.CreatePng(1, 1)),
                ("assets/band-background.png", ThemeTestPackage.CreatePng(8, 1)),
                ("assets/tray-icon.ico", ThemeTestPackage.CreateIcoWithPng(16, 16))
            ]);
        var service = new ThemePackageService(temp.Themes, new Version(1, 3, 0));

        ThemeImportResult result = await service.ImportAsync(package);

        Assert.True(result.Success);
        Assert.Equal(ThemeImportErrorCode.None, result.ErrorCode);
        Assert.Equal("ocean", result.Theme!.Identity.Id);
        Assert.False(result.Theme.IsBuiltIn);
        Assert.Equal(64, result.Theme.IdentityToken.Length);
        Assert.True(Directory.Exists(Path.Combine(temp.Themes, "ocean")));
        Assert.Equal(
            Path.Combine(temp.Themes, "ocean", "assets", "preview.png"),
            result.Theme.PreviewPath);
        Assert.True(File.Exists(result.Theme.PreviewPath));
        Assert.True(File.Exists(result.Theme.BandBackgroundPath));
        Assert.True(File.Exists(result.Theme.TrayIconPath));
    }

    [Theory]
    [InlineData("../escape.txt")]
    [InlineData("/absolute.txt")]
    [InlineData("C:/absolute.txt")]
    [InlineData("manifest.json:payload")]
    [InlineData("assets//preview.png")]
    [InlineData("assets/CON.png")]
    [InlineData("assets/COM1.png")]
    [InlineData("README.md.")]
    [InlineData("payload.exe")]
    public async Task UnsafeOrNonWhitelistedEntryIsRejected(string entryName)
    {
        using var temp = new ThemeTestDirectory();
        string package = ThemeTestPackage.Create(
            temp.Path,
            "unsafe",
            additionalEntries: [(entryName, Encoding.UTF8.GetBytes("x"))]);

        ThemeImportResult result = await new ThemePackageService(
            temp.Themes, new Version(1, 3, 0)).ImportAsync(package);

        Assert.False(result.Success);
        Assert.Equal(ThemeImportErrorCode.InvalidPath, result.ErrorCode);
        Assert.False(File.Exists(Path.Combine(temp.Path, "escape.txt")));
    }

    [Theory]
    [InlineData("MANIFEST.JSON")]
    [InlineData(".\\manifest.json")]
    public async Task WindowsNormalizedDuplicateEntryIsRejected(string duplicateName)
    {
        using var temp = new ThemeTestDirectory();
        string package = ThemeTestPackage.Create(
            temp.Path,
            "duplicate-path",
            additionalEntries: [(duplicateName, ThemeTestPackage.Manifest("duplicate-path"))]);

        ThemeImportResult result = await new ThemePackageService(
            temp.Themes, new Version(1, 3, 0)).ImportAsync(package);

        Assert.False(result.Success);
        Assert.Equal(ThemeImportErrorCode.InvalidPath, result.ErrorCode);
    }

    [Fact]
    public async Task ForwardAndBackslashDuplicateAssetIsRejected()
    {
        using var temp = new ThemeTestDirectory();
        byte[] png = ThemeTestPackage.CreatePng(1, 1);
        string package = ThemeTestPackage.Create(
            temp.Path,
            "slash-collision",
            preview: "assets/preview.png",
            additionalEntries:
            [("assets/preview.png", png), ("assets\\preview.png", png)]);

        ThemeImportResult result = await new ThemePackageService(
            temp.Themes, new Version(1, 3, 0)).ImportAsync(package);

        Assert.False(result.Success);
        Assert.Equal(ThemeImportErrorCode.InvalidPath, result.ErrorCode);
    }

    [Theory]
    [InlineData("CON")]
    [InlineData("theme.json")]
    [InlineData("builtin.default")]
    [InlineData("Uppercase")]
    public async Task InvalidOrReservedIdIsRejected(string id)
    {
        using var temp = new ThemeTestDirectory();
        string package = ThemeTestPackage.Create(temp.Path, id);

        ThemeImportResult result = await new ThemePackageService(
            temp.Themes, new Version(1, 3, 0)).ImportAsync(package);

        Assert.False(result.Success);
        Assert.Equal(ThemeImportErrorCode.InvalidManifest, result.ErrorCode);
    }

    [Fact]
    public async Task ExcessiveCompressionRatioIsRejected()
    {
        using var temp = new ThemeTestDirectory();
        string package = ThemeTestPackage.Create(
            temp.Path,
            "ratio",
            additionalEntries: [("README.md", new byte[200_000])]);

        ThemeImportResult result = await new ThemePackageService(
            temp.Themes, new Version(1, 3, 0)).ImportAsync(package);

        Assert.False(result.Success);
        Assert.Equal(ThemeImportErrorCode.LimitExceeded, result.ErrorCode);
    }

    [Fact]
    public async Task OversizedEntryIsRejected()
    {
        using var temp = new ThemeTestDirectory();
        byte[] oversized = new byte[2 * 1024 * 1024 + 1];
        Random.Shared.NextBytes(oversized);
        string package = ThemeTestPackage.Create(
            temp.Path, "oversized", additionalEntries: [("README.md", oversized)]);

        ThemeImportResult result = await new ThemePackageService(
            temp.Themes, new Version(1, 3, 0)).ImportAsync(package);

        Assert.False(result.Success);
        Assert.Equal(ThemeImportErrorCode.LimitExceeded, result.ErrorCode);
    }

    [Fact]
    public async Task InvalidPngSignatureIsRejected()
    {
        using var temp = new ThemeTestDirectory();
        string package = ThemeTestPackage.Create(
            temp.Path,
            "bad-image",
            preview: "assets/preview.png",
            additionalEntries: [("assets/preview.png", Encoding.UTF8.GetBytes("not a png"))]);

        ThemeImportResult result = await new ThemePackageService(
            temp.Themes, new Version(1, 3, 0)).ImportAsync(package);

        Assert.False(result.Success);
        Assert.Equal(ThemeImportErrorCode.InvalidAsset, result.ErrorCode);
    }

    [Fact]
    public async Task DecodedPngDimensionsAreEnforced()
    {
        using var temp = new ThemeTestDirectory();
        byte[] png = ThemeTestPackage.CreatePng(width: 2049, height: 1);
        string package = ThemeTestPackage.Create(
            temp.Path,
            "large-image",
            preview: "assets/preview.png",
            additionalEntries: [("assets/preview.png", png)]);

        ThemeImportResult result = await new ThemePackageService(
            temp.Themes, new Version(1, 3, 0)).ImportAsync(package);

        Assert.False(result.Success);
        Assert.Equal(ThemeImportErrorCode.InvalidAsset, result.ErrorCode);
    }

    [Fact]
    public async Task InvalidIcoDecoderPayloadIsRejected()
    {
        using var temp = new ThemeTestDirectory();
        byte[] fakeIcon = [0, 0, 1, 0, 1, 0, 16, 16, 0, 0, 1, 0, 32, 0, 4, 0, 0, 0, 22, 0, 0, 0, 1, 2, 3, 4];
        string package = ThemeTestPackage.Create(
            temp.Path,
            "bad-icon",
            trayIcon: "assets/tray-icon.ico",
            additionalEntries: [("assets/tray-icon.ico", fakeIcon)]);

        ThemeImportResult result = await new ThemePackageService(
            temp.Themes, new Version(1, 3, 0)).ImportAsync(package);

        Assert.False(result.Success);
        Assert.Equal(ThemeImportErrorCode.InvalidAsset, result.ErrorCode);
    }

    [Fact]
    public async Task EmbeddedIcoDimensionsAreEnforcedBeforeDecode()
    {
        using var temp = new ThemeTestDirectory();
        string package = ThemeTestPackage.Create(
            temp.Path,
            "large-icon",
            trayIcon: "assets/tray-icon.ico",
            additionalEntries:
            [("assets/tray-icon.ico", ThemeTestPackage.CreateIcoWithPng(257, 1))]);

        ThemeImportResult result = await new ThemePackageService(
            temp.Themes, new Version(1, 3, 0)).ImportAsync(package);

        Assert.False(result.Success);
        Assert.Equal(ThemeImportErrorCode.InvalidAsset, result.ErrorCode);
    }

    [Fact]
    public async Task MoreThanMaximumArchiveEntriesIsRejected()
    {
        using var temp = new ThemeTestDirectory();
        var entries = Enumerable.Range(0, 31)
            .Select(_ => ("README.md", Array.Empty<byte>()))
            .ToArray();
        string package = ThemeTestPackage.Create(
            temp.Path, "entries", additionalEntries: entries);

        ThemeImportResult result = await new ThemePackageService(
            temp.Themes, new Version(1, 3, 0)).ImportAsync(package);

        Assert.False(result.Success);
        Assert.Equal(ThemeImportErrorCode.LimitExceeded, result.ErrorCode);
    }

    [Fact]
    public async Task InvalidJsonUnknownFieldAndSchemaAreRejected()
    {
        using var temp = new ThemeTestDirectory();
        string package = ThemeTestPackage.Create(
            temp.Path,
            "json",
            manifestOverride: Encoding.UTF8.GetBytes("{\"schemaVersion\":2,\"unknown\":true}"));

        ThemeImportResult result = await new ThemePackageService(
            temp.Themes, new Version(1, 3, 0)).ImportAsync(package);

        Assert.False(result.Success);
        Assert.Equal(ThemeImportErrorCode.InvalidManifest, result.ErrorCode);
    }

    [Theory]
    [InlineData("#12345")]
    [InlineData("red")]
    [InlineData("#GG0000")]
    public async Task InvalidColorIsRejected(string color)
    {
        using var temp = new ThemeTestDirectory();
        string package = ThemeTestPackage.Create(
            temp.Path, "bad-color", themeOverride: ThemeTestPackage.Theme(color: color));

        ThemeImportResult result = await new ThemePackageService(
            temp.Themes, new Version(1, 3, 0)).ImportAsync(package);

        Assert.False(result.Success);
        Assert.Equal(ThemeImportErrorCode.InvalidTheme, result.ErrorCode);
    }

    [Theory]
    [InlineData(-1, 0.5)]
    [InlineData(33, 0.5)]
    [InlineData(10, 1.1)]
    public async Task InvalidNumericRangeIsRejected(double radius, double opacity)
    {
        using var temp = new ThemeTestDirectory();
        string package = ThemeTestPackage.Create(
            temp.Path,
            "bad-range",
            themeOverride: ThemeTestPackage.Theme(radius: radius, opacity: opacity));

        ThemeImportResult result = await new ThemePackageService(
            temp.Themes, new Version(1, 3, 0)).ImportAsync(package);

        Assert.False(result.Success);
        Assert.Equal(ThemeImportErrorCode.InvalidTheme, result.ErrorCode);
    }

    [Fact]
    public async Task MinimumApplicationVersionIsEnforced()
    {
        using var temp = new ThemeTestDirectory();
        string package = ThemeTestPackage.Create(temp.Path, "future", minimumVersion: "9.0.0");

        ThemeImportResult result = await new ThemePackageService(
            temp.Themes, new Version(1, 3, 0)).ImportAsync(package);

        Assert.False(result.Success);
        Assert.Equal(ThemeImportErrorCode.IncompatibleVersion, result.ErrorCode);
    }
}

internal sealed class ThemeTestDirectory : IDisposable
{
    internal ThemeTestDirectory()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"SysMonitor.ThemeTests.{Guid.NewGuid():N}");
        Themes = System.IO.Path.Combine(Path, "Themes");
        Directory.CreateDirectory(Path);
    }

    internal string Path { get; }
    internal string Themes { get; }

    public void Dispose()
    {
        if (Directory.Exists(Path))
        {
            Directory.Delete(Path, recursive: true);
        }
    }
}

internal static class ThemeTestPackage
{
    internal static string Create(
        string directory,
        string id,
        string minimumVersion = "1.0.0",
        string? preview = null,
        string? trayIcon = null,
        string? bandImage = null,
        byte[]? manifestOverride = null,
        byte[]? themeOverride = null,
        IReadOnlyList<(string Name, byte[] Bytes)>? additionalEntries = null)
    {
        string package = System.IO.Path.Combine(directory, $"{Guid.NewGuid():N}.smonitor-theme");
        using (FileStream stream = File.Create(package))
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
        {
            Add(archive, "manifest.json", manifestOverride ?? Manifest(id, minimumVersion, preview));
            Add(archive, "theme.json", themeOverride ?? Theme(trayIcon: trayIcon, bandImage: bandImage));
            foreach ((string name, byte[] bytes) in additionalEntries ?? [])
            {
                Add(archive, name, bytes);
            }
        }

        return package;
    }

    internal static byte[] Manifest(
        string id,
        string minimumVersion = "1.0.0",
        string? preview = null) => Encoding.UTF8.GetBytes(
        $$"""
        {
          "schemaVersion": 1,
          "id": "{{id}}",
          "name": "Test Theme",
          "author": "Tests",
          "version": "1.2.3",
          "minSysMonitorVersion": "{{minimumVersion}}"{{(preview is null ? string.Empty : $",\n  \"preview\": \"{preview}\"")}}
        }
        """);

    internal static byte[] Theme(
        string color = "#112233",
        double radius = 12,
        double opacity = 0.1,
        string? trayIcon = null,
        string? bandImage = null) => Encoding.UTF8.GetBytes(
        $$"""
        {
          "colors": {
            "appBackground": "{{color}}", "surface": "#FFFFFFFF", "text": "#111111",
            "secondary": "#222222", "tertiary": "#333333", "separator": "#444444",
            "control": "#555555", "accent": "#0066CC"
          },
          "metrics": {
            "cpu": "#007AFF", "memory": "#AF52DE", "gpu": "#34C759",
            "warning": "#FF9500", "critical": "#FF3B30"
          },
          "shape": { "groupCornerRadius": {{radius.ToString(System.Globalization.CultureInfo.InvariantCulture)}}, "shadowOpacity": {{opacity.ToString(System.Globalization.CultureInfo.InvariantCulture)}} },
          "band": { "backgroundColor": "#00000000", "cornerRadius": 0{{(bandImage is null ? string.Empty : $", \"backgroundImage\": \"{bandImage}\"")}} }{{(trayIcon is null ? string.Empty : $",\n  \"trayIcon\": \"{trayIcon}\"")}}
        }
        """);

    internal static byte[] CreatePng(int width, int height)
    {
        using var result = new MemoryStream();
        result.Write([137, 80, 78, 71, 13, 10, 26, 10]);
        byte[] header = new byte[13];
        WriteBigEndian(header, 0, (uint)width);
        WriteBigEndian(header, 4, (uint)height);
        header[8] = 8;
        header[9] = 6;
        WriteChunk(result, "IHDR", header);

        byte[] pixels = new byte[checked(height * (1 + width * 4))];
        using var compressed = new MemoryStream();
        using (var zlib = new ZLibStream(compressed, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            zlib.Write(pixels);
        }

        WriteChunk(result, "IDAT", compressed.ToArray());
        WriteChunk(result, "IEND", []);
        return result.ToArray();
    }

    internal static byte[] CreateIcoWithPng(int width, int height)
    {
        byte[] png = CreatePng(width, height);
        byte[] icon = new byte[22 + png.Length];
        icon[2] = 1;
        icon[4] = 1;
        icon[6] = width is >= 256 ? (byte)0 : (byte)width;
        icon[7] = height is >= 256 ? (byte)0 : (byte)height;
        icon[10] = 1;
        icon[12] = 32;
        WriteLittleEndian(icon, 14, (uint)png.Length);
        WriteLittleEndian(icon, 18, 22);
        png.CopyTo(icon, 22);
        return icon;
    }

    private static void Add(ZipArchive archive, string name, byte[] bytes)
    {
        ZipArchiveEntry entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        using Stream output = entry.Open();
        output.Write(bytes);
    }

    private static void WriteChunk(Stream stream, string type, byte[] data)
    {
        Span<byte> length = stackalloc byte[4];
        WriteBigEndian(length, 0, (uint)data.Length);
        stream.Write(length);
        byte[] typeBytes = Encoding.ASCII.GetBytes(type);
        stream.Write(typeBytes);
        stream.Write(data);
        byte[] crcInput = new byte[typeBytes.Length + data.Length];
        typeBytes.CopyTo(crcInput, 0);
        data.CopyTo(crcInput, typeBytes.Length);
        Span<byte> crc = stackalloc byte[4];
        WriteBigEndian(crc, 0, Crc32(crcInput));
        stream.Write(crc);
    }

    private static uint Crc32(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (byte value in data)
        {
            crc ^= value;
            for (int bit = 0; bit < 8; bit++)
            {
                crc = (crc >> 1) ^ (0xEDB88320u & (uint)-(int)(crc & 1));
            }
        }

        return ~crc;
    }

    private static void WriteBigEndian(Span<byte> buffer, int offset, uint value)
    {
        buffer[offset] = (byte)(value >> 24);
        buffer[offset + 1] = (byte)(value >> 16);
        buffer[offset + 2] = (byte)(value >> 8);
        buffer[offset + 3] = (byte)value;
    }

    private static void WriteLittleEndian(Span<byte> buffer, int offset, uint value)
    {
        buffer[offset] = (byte)value;
        buffer[offset + 1] = (byte)(value >> 8);
        buffer[offset + 2] = (byte)(value >> 16);
        buffer[offset + 3] = (byte)(value >> 24);
    }
}
