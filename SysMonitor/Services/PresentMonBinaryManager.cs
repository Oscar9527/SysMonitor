using System.Reflection;
using System.Security.Cryptography;
using System.IO;

namespace SysMonitor.Services;

internal static class PresentMonBinaryManager
{
    internal const string Version = "2.5.1";
    internal const string FileName = "PresentMon-2.5.1-x64.exe";
    internal const string Sha256 = "9BEC3083069F58F911E6A512F4806DB51A27BD096103087BC1D05EF54C80A191";
    internal const string ResourceName = "SysMonitor.ThirdParty.PresentMon-2.5.1.PresentMon-2.5.1-x64.exe";
    private static readonly SemaphoreSlim ExtractionGate = new(1, 1);

    internal static async Task<string> GetExecutablePathAsync(CancellationToken cancellationToken)
    {
        await ExtractionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(localAppData))
            {
                throw new InvalidOperationException("The local application data directory is unavailable.");
            }

            string directory = Path.Combine(
                localAppData,
                "SysMonitor",
                "runtime",
                "tools",
                $"PresentMon-{Version}");
            Directory.CreateDirectory(directory);
            string destination = Path.Combine(directory, FileName);
            if (File.Exists(destination) && await HasExpectedHashAsync(destination, cancellationToken).ConfigureAwait(false))
            {
                return destination;
            }

            await using Stream resource = typeof(PresentMonBinaryManager).Assembly
                .GetManifestResourceStream(ResourceName) ??
                throw new InvalidDataException("The embedded PresentMon binary is missing.");
            if (!string.Equals(await ComputeHashAsync(resource, cancellationToken).ConfigureAwait(false), Sha256, StringComparison.Ordinal))
            {
                throw new InvalidDataException("The embedded PresentMon binary failed its integrity check.");
            }

            resource.Position = 0;
            string temporary = Path.Combine(directory, $".{FileName}.{Guid.NewGuid():N}.tmp");
            try
            {
                await using (var output = new FileStream(
                                 temporary,
                                 FileMode.CreateNew,
                                 FileAccess.Write,
                                 FileShare.None,
                                 81920,
                                 FileOptions.Asynchronous | FileOptions.WriteThrough))
                {
                    await resource.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
                    await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                }

                if (!await HasExpectedHashAsync(temporary, cancellationToken).ConfigureAwait(false))
                {
                    throw new InvalidDataException("The extracted PresentMon binary failed its integrity check.");
                }

                File.Move(temporary, destination, overwrite: true);
            }
            finally
            {
                try
                {
                    File.Delete(temporary);
                }
                catch
                {
                }
            }

            if (!await HasExpectedHashAsync(destination, cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidDataException("The installed PresentMon binary failed its integrity check.");
            }

            return destination;
        }
        finally
        {
            ExtractionGate.Release();
        }
    }

    private static async Task<bool> HasExpectedHashAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            return string.Equals(
                await ComputeHashAsync(stream, cancellationToken).ConfigureAwait(false),
                Sha256,
                StringComparison.Ordinal);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static async Task<string> ComputeHashAsync(Stream stream, CancellationToken cancellationToken)
    {
        byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash);
    }
}
