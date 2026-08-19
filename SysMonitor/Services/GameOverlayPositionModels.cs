using System.Collections.Generic;
using System.Linq;
using SysMonitor.Models;

namespace SysMonitor.Services;

/// <summary>
/// A monitor rectangle in physical screen pixels.  The coordinates are not
/// normalized to the virtual-screen origin, so negative coordinates are
/// expected for monitors positioned to the left or above the primary display.
/// </summary>
internal readonly record struct ScreenPixelBounds(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;
    public bool IsValid => Right > Left && Bottom > Top;
}

/// <summary>
/// Immutable runtime snapshot used to roll a settings-window live preview back
/// without reading or writing persisted settings.
/// </summary>
internal sealed record GameOverlayPreviewState(
    string LayoutMode,
    IReadOnlyList<GameOverlayMonitorPositionSettings> MonitorPositions);

/// <summary>
/// Read-only identity and physical bounds for a monitor used by the overlay.
/// Stable identities are normalized DISPLAYCONFIG monitor device paths.  A
/// fallback identity is deliberately tied to both the GDI name and exact full
/// monitor bounds, since no geometric approximation is safe for persistence.
/// </summary>
internal readonly record struct OverlayMonitorIdentity(
    string StableMonitorId,
    string GdiDeviceName,
    string DisplayName,
    ScreenPixelBounds Bounds,
    bool IsFallback)
{
    public bool IsValid =>
        !string.IsNullOrWhiteSpace(StableMonitorId) &&
        !string.IsNullOrWhiteSpace(GdiDeviceName) &&
        Bounds.IsValid;

    public static OverlayMonitorIdentity CreateStable(
        string monitorDevicePath,
        string gdiDeviceName,
        string? displayName,
        ScreenPixelBounds bounds)
    {
        string normalizedGdi = OverlayMonitorIdentityText.NormalizeGdiName(gdiDeviceName);
        string normalizedPath = OverlayMonitorIdentityText.NormalizeMonitorDevicePath(monitorDevicePath);
        if (string.IsNullOrEmpty(normalizedGdi) || string.IsNullOrEmpty(normalizedPath))
        {
            return CreateFallback(normalizedGdi, displayName, bounds);
        }

        return new OverlayMonitorIdentity(
            normalizedPath,
            normalizedGdi,
            string.IsNullOrWhiteSpace(displayName) ? normalizedGdi : displayName.Trim(),
            bounds,
            IsFallback: false);
    }

    public static OverlayMonitorIdentity CreateFallback(
        string gdiDeviceName,
        string? displayName,
        ScreenPixelBounds bounds)
    {
        string normalizedGdi = OverlayMonitorIdentityText.NormalizeGdiName(gdiDeviceName);
        if (string.IsNullOrEmpty(normalizedGdi))
        {
            normalizedGdi = "<UNKNOWN-GDI>";
        }

        return new OverlayMonitorIdentity(
            OverlayMonitorIdentityText.BuildFallbackId(normalizedGdi, bounds),
            normalizedGdi,
            string.IsNullOrWhiteSpace(displayName) ? normalizedGdi : displayName.Trim(),
            bounds,
            IsFallback: true);
    }
}

internal static class OverlayMonitorIdentityText
{
    public static string NormalizeGdiName(string? value) => Normalize(value);

    public static string NormalizeMonitorDevicePath(string? value) => Normalize(value);

    public static string BuildFallbackId(string normalizedGdiName, ScreenPixelBounds bounds) =>
        $"fallback:gdi={NormalizeGdiName(normalizedGdiName)};bounds={bounds.Left},{bounds.Top},{bounds.Right},{bounds.Bottom}";

    private static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        // Device paths and GDI names are case-insensitive.  Removing outer
        // whitespace makes identities stable across native API formatting.
        return value.Trim().ToUpperInvariant();
    }
}

/// <summary>
/// Conservative identity reconciliation.  Stable IDs are matched only to
/// stable IDs; fallback IDs require an exact normalized GDI name and bounds.
/// Any duplicate candidate is considered ambiguous and returns no match.
/// </summary>
internal static class OverlayMonitorIdentityMatcher
{
    public static bool TryReconcile(
        OverlayMonitorIdentity requested,
        IReadOnlyList<OverlayMonitorIdentity> candidates,
        out OverlayMonitorIdentity match) =>
        TryMatch(requested, candidates, out match);

    public static bool TryMatch(
        OverlayMonitorIdentity requested,
        IReadOnlyList<OverlayMonitorIdentity> candidates,
        out OverlayMonitorIdentity match)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        match = default;

        if (string.IsNullOrWhiteSpace(requested.StableMonitorId))
        {
            return false;
        }

        // A duplicate stable path means the topology snapshot cannot tell
        // which physical target is intended.  Reject the complete snapshot,
        // even when the duplicate is not the requested path.
        var stableIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (OverlayMonitorIdentity candidate in candidates)
        {
            if (!candidate.IsFallback)
            {
                string stableId = OverlayMonitorIdentityText.NormalizeMonitorDevicePath(candidate.StableMonitorId);
                if (!string.IsNullOrEmpty(stableId) && !stableIds.Add(stableId))
                {
                    return false;
                }
            }
        }

        IEnumerable<OverlayMonitorIdentity> matches;
        if (requested.IsFallback)
        {
            string gdi = OverlayMonitorIdentityText.NormalizeGdiName(requested.GdiDeviceName);
            matches = candidates.Where(candidate =>
                candidate.IsFallback &&
                string.Equals(
                    OverlayMonitorIdentityText.NormalizeGdiName(candidate.GdiDeviceName),
                    gdi,
                    StringComparison.Ordinal) &&
                candidate.Bounds == requested.Bounds);
        }
        else
        {
            string stableId = OverlayMonitorIdentityText.NormalizeMonitorDevicePath(requested.StableMonitorId);
            matches = candidates.Where(candidate =>
                !candidate.IsFallback &&
                string.Equals(
                    OverlayMonitorIdentityText.NormalizeMonitorDevicePath(candidate.StableMonitorId),
                    stableId,
                    StringComparison.Ordinal));
        }

        // Exactly one match is required.  In particular, duplicate stable IDs
        // fail closed rather than selecting whichever path happened to appear
        // first in QueryDisplayConfig.
        using IEnumerator<OverlayMonitorIdentity> enumerator = matches.GetEnumerator();
        if (!enumerator.MoveNext())
        {
            return false;
        }

        OverlayMonitorIdentity first = enumerator.Current;
        if (enumerator.MoveNext())
        {
            return false;
        }

        match = first;
        return true;
    }
}
