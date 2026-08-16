using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SysMonitor.Models;
using SysMonitor.Services;
using Forms = System.Windows.Forms;
using DrawingColor = System.Drawing.Color;
using MediaColor = System.Windows.Media.Color;
using MediaColorConverter = System.Windows.Media.ColorConverter;
using MediaFontFamily = System.Windows.Media.FontFamily;
using WpfButton = System.Windows.Controls.Button;

namespace SysMonitor.UI;

public partial class GameOverlayAppearanceWindow : Window
{
    private bool _loading;
    private GameOverlayAppearance _applied = new();
    private GameOverlayAppearance _editing = new();
    private readonly IReadOnlyList<OverlaySkin> _skins =
    [
        new("经典霓虹", new GameOverlayAppearance(GpuColor: "#FF66D9FF", CpuColor: "#FF8BE9FD", FpsColor: "#FF50FA7B", MemoryColor: "#FFF1FA8C", NetworkColor: "#FFFFB86C")),
        new("冰川蓝", new GameOverlayAppearance(GpuColor: "#FF4CC9F0", CpuColor: "#FF90E0EF", FpsColor: "#FFB9FBC0", MemoryColor: "#FFA9DEF9", NetworkColor: "#FFCDB4DB")),
        new("落日暖色", new GameOverlayAppearance(GpuColor: "#FFFF9F1C", CpuColor: "#FFFFD166", FpsColor: "#FF95D5B2", MemoryColor: "#FFFFADAD", NetworkColor: "#FFBDB2FF")),
        new("简洁白", new GameOverlayAppearance(GpuColor: "#FFFFFFFF", CpuColor: "#FFFFFFFF", FpsColor: "#FFFFFFFF", MemoryColor: "#FFFFFFFF", NetworkColor: "#FFFFFFFF", OutlineThickness: 1.5d, ShadowOpacity: 0.9d))
    ];

    public event Action<GameOverlayAppearance>? PreviewChanged;
    public event Action<GameOverlayAppearance>? Applied;

    public GameOverlayAppearanceWindow()
    {
        _loading = true;
        InitializeComponent();
        FontBox.ItemsSource = Fonts.SystemFontFamilies.OrderBy(font => font.Source, StringComparer.CurrentCultureIgnoreCase).ToArray();
        SkinBox.ItemsSource = _skins;
        Closing += (_, e) => { e.Cancel = true; CancelAndHide(); };
        _loading = false;
        LoadAppearance(_applied);
    }

    public void LoadAppearance(GameOverlayAppearance appearance)
    {
        _applied = SettingsService.NormalizeOverlayAppearance(appearance);
        _editing = _applied;
        _loading = true;
        try
        {
            FontBox.SelectedItem = FontBox.Items.Cast<MediaFontFamily>().FirstOrDefault(font => string.Equals(font.Source, _editing.FontFamily, StringComparison.OrdinalIgnoreCase)) ?? FontBox.Items.Cast<MediaFontFamily>().FirstOrDefault();
            FontSizeSlider.Value = _editing.FontSize; OutlineSlider.Value = _editing.OutlineThickness;
            ShadowOpacitySlider.Value = _editing.ShadowOpacity; ShadowDepthSlider.Value = _editing.ShadowDepth;
            SkinBox.SelectedIndex = -1;
        }
        finally { _loading = false; }
        UpdateVisuals();
    }

    private GameOverlayAppearance Read() => SettingsService.NormalizeOverlayAppearance(_editing with
    {
        FontFamily = (FontBox.SelectedItem as MediaFontFamily)?.Source ?? "Consolas",
        FontSize = FontSizeSlider.Value, OutlineThickness = OutlineSlider.Value,
        ShadowOpacity = ShadowOpacitySlider.Value, ShadowDepth = ShadowDepthSlider.Value
    });

    private void Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _editing = Read();
        UpdateVisuals();
        PreviewChanged?.Invoke(_editing);
    }

    private void SkinChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || SkinBox.SelectedItem is not OverlaySkin skin) return;
        GameOverlayAppearance selected = skin.Appearance;
        _editing = SettingsService.NormalizeOverlayAppearance(selected with
        {
            FontFamily = _editing.FontFamily, FontSize = _editing.FontSize
        });
        _loading = true;
        try
        {
            OutlineSlider.Value = _editing.OutlineThickness; ShadowOpacitySlider.Value = _editing.ShadowOpacity; ShadowDepthSlider.Value = _editing.ShadowDepth;
        }
        finally { _loading = false; }
        UpdateVisuals();
        PreviewChanged?.Invoke(_editing);
    }

    private void ColorWheel_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not WpfButton { Tag: string target }) return;
        string current = target switch
        {
            "Gpu" => _editing.GpuColor, "Cpu" => _editing.CpuColor, "Fps" => _editing.FpsColor,
            "Memory" => _editing.MemoryColor, "Network" => _editing.NetworkColor,
            "Outline" => _editing.OutlineColor, "Shadow" => _editing.ShadowColor, _ => "#FFFFFFFF"
        };
        using var dialog = new Forms.ColorDialog { FullOpen = true, Color = ToDrawingColor(current) };
        if (dialog.ShowDialog() != Forms.DialogResult.OK) return;
        string hex = $"#{dialog.Color.A:X2}{dialog.Color.R:X2}{dialog.Color.G:X2}{dialog.Color.B:X2}";
        _editing = target switch
        {
            "Gpu" => _editing with { GpuColor = hex }, "Cpu" => _editing with { CpuColor = hex },
            "Fps" => _editing with { FpsColor = hex }, "Memory" => _editing with { MemoryColor = hex },
            "Network" => _editing with { NetworkColor = hex }, "Outline" => _editing with { OutlineColor = hex },
            "Shadow" => _editing with { ShadowColor = hex }, _ => _editing
        };
        _editing = SettingsService.NormalizeOverlayAppearance(_editing);
        UpdateVisuals();
        PreviewChanged?.Invoke(_editing);
    }

    private void UpdateVisuals()
    {
        FontSizeText.Text = $"{FontSizeSlider.Value:0}px"; OutlineText.Text = $"{OutlineSlider.Value:0.0}";
        ShadowOpacityText.Text = $"{ShadowOpacitySlider.Value:0%}"; ShadowDepthText.Text = $"{ShadowDepthSlider.Value:0.0}";
        ApplyColor(GpuColorButton, GpuColorText, _editing.GpuColor); ApplyColor(CpuColorButton, CpuColorText, _editing.CpuColor);
        ApplyColor(FpsColorButton, FpsColorText, _editing.FpsColor); ApplyColor(MemoryColorButton, MemoryColorText, _editing.MemoryColor); ApplyColor(NetworkColorButton, NetworkColorText, _editing.NetworkColor);
        MediaFontFamily family = FontBox.SelectedItem as MediaFontFamily ?? new MediaFontFamily("Consolas");
        foreach (TextBlock preview in new[] { PreviewGpu, PreviewCpu, PreviewFps, PreviewMemory, PreviewNetwork }) { preview.FontFamily = family; preview.FontSize = FontSizeSlider.Value; }
        PreviewGpu.Foreground = BrushFor(_editing.GpuColor); PreviewCpu.Foreground = BrushFor(_editing.CpuColor); PreviewFps.Foreground = BrushFor(_editing.FpsColor); PreviewMemory.Foreground = BrushFor(_editing.MemoryColor); PreviewNetwork.Foreground = BrushFor(_editing.NetworkColor);
    }

    private static void ApplyColor(WpfButton button, TextBlock text, string hex) { button.Background = BrushFor(hex); text.Text = hex[1..]; }
    private static SolidColorBrush BrushFor(string hex) { var brush = new SolidColorBrush((MediaColor)MediaColorConverter.ConvertFromString(hex)!); brush.Freeze(); return brush; }
    private static DrawingColor ToDrawingColor(string hex) { MediaColor color = (MediaColor)MediaColorConverter.ConvertFromString(hex)!; return DrawingColor.FromArgb(color.A, color.R, color.G, color.B); }
    private void Apply_Click(object sender, RoutedEventArgs e) { _applied = Read(); Applied?.Invoke(_applied); Hide(); }
    private void Cancel_Click(object sender, RoutedEventArgs e) => CancelAndHide();
    private void CancelAndHide() { PreviewChanged?.Invoke(_applied); Hide(); }

    private sealed record OverlaySkin(string Name, GameOverlayAppearance Appearance);
}
