using System.Text.Json;
using SysMonitor.Models;
using SysMonitor.Services;
using SysMonitor.UI;

namespace SysMonitor.Tests;

public sealed class OverlayBackgroundOpacityTests
{
    [Theory]
    [InlineData(0d, 0)]
    [InlineData(0.37d, 94)]
    [InlineData(0.8d, 204)]
    [InlineData(1d, 255)]
    [InlineData(-1d, 0)]
    [InlineData(2d, 255)]
    public void ToAlpha_ClampsAndRoundsOpacity(double opacity, byte expected)
    {
        Assert.Equal(expected, OverlayBackgroundOpacity.ToAlpha(opacity));
    }

    [Fact]
    public void ToAlpha_NonFiniteValueUsesLegacyDefault()
    {
        Assert.Equal((byte)204, OverlayBackgroundOpacity.ToAlpha(double.NaN));
        Assert.Equal((byte)204, OverlayBackgroundOpacity.ToAlpha(double.PositiveInfinity));
    }

    [Fact]
    public void LegacyAppearanceJsonWithoutOpacityUsesEightyPercent()
    {
        GameOverlayAppearanceSettings settings =
            JsonSerializer.Deserialize<GameOverlayAppearanceSettings>("{}")!;

        Assert.Equal(0.8d, settings.BackgroundOpacity);
        Assert.Equal(0.8d, settings.ToEffective().BackgroundOpacity);
    }

    [Theory]
    [InlineData(-0.1d, 0d)]
    [InlineData(0.42d, 0.42d)]
    [InlineData(1.1d, 1d)]
    public void NormalizeOverlayAppearance_ClampsBackgroundOpacity(double value, double expected)
    {
        GameOverlayAppearance normalized = SettingsService.NormalizeOverlayAppearance(
            new GameOverlayAppearance(BackgroundOpacity: value));

        Assert.Equal(expected, normalized.BackgroundOpacity);
        GameOverlayAppearance roundTrip = GameOverlayAppearanceSettings
            .FromEffective(normalized)
            .ToEffective();
        Assert.Equal(expected, roundTrip.BackgroundOpacity);
    }
}
