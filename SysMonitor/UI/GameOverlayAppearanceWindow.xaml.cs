using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
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
        new("微星小飞机 (经典橙绿)", new GameOverlayAppearance(FontFamily: "Consolas", FontSize: 16, LabelColor: "#FFFF8C00", ValueColor: "#FFFF8C00", GpuColor: "#FFFF8C00", CpuColor: "#FF00E5FF", FpsColor: "#FF00E676", MemoryColor: "#FFFFD600", NetworkColor: "#FFE040FB", OutlineColor: "#FF000000", OutlineThickness: 1.5, ShadowOpacity: 0.95, ShadowDepth: 1.0)),
        new("赛博电竞 (青绿炫彩)", new GameOverlayAppearance(FontFamily: "Consolas", FontSize: 14, LabelColor: "#FF4CC9F0", ValueColor: "#FF4CC9F0", GpuColor: "#FF4CC9F0", CpuColor: "#FF70B5FF", FpsColor: "#FF7BDCB5", MemoryColor: "#FFA9DEF9", NetworkColor: "#FF6FD3E1", OutlineColor: "#FF000000", OutlineThickness: 1.5, ShadowOpacity: 0.95, ShadowDepth: 1.0)),
        new("极简纯黑 (高对比度)", new GameOverlayAppearance(FontFamily: "Segoe UI", FontSize: 13, LabelColor: "#FFB0BEC5", ValueColor: "#FFB0BEC5", GpuColor: "#FFB0BEC5", CpuColor: "#FFB0BEC5", FpsColor: "#FF81C784", MemoryColor: "#FFB0BEC5", NetworkColor: "#FFB0BEC5", OutlineColor: "#FF000000", OutlineThickness: 1.2, ShadowOpacity: 0.85, ShadowDepth: 1.0)),
        new("暖阳琥珀 (复古温润)", new GameOverlayAppearance(FontFamily: "Segoe UI", FontSize: 13, LabelColor: "#FFFFA94D", ValueColor: "#FFFFA94D", GpuColor: "#FFFFA94D", CpuColor: "#FFFFD166", FpsColor: "#FF95D5B2", MemoryColor: "#FFFF8E72", NetworkColor: "#FFE4B1FF", OutlineColor: "#FF000000", OutlineThickness: 1.5, ShadowOpacity: 0.90, ShadowDepth: 1.0)),
        new("经典多彩 (炫彩蓝紫)", new GameOverlayAppearance(FontFamily: "Segoe UI", FontSize: 13, LabelColor: "#FF7E57C2", ValueColor: "#FF7E57C2", GpuColor: "#FF7E57C2", CpuColor: "#FF1976D2", FpsColor: "#FF1B9A5A", MemoryColor: "#FFD97706", NetworkColor: "#FF0097A7", OutlineColor: "#FF000000", OutlineThickness: 1.2, ShadowOpacity: 0.85, ShadowDepth: 1.0))
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
            "Outline" or "Shadow" => _editing.OutlineColor, _ => "#FFFFFFFF"
        };
        using var dialog = new Forms.ColorDialog { FullOpen = true, Color = ToDrawingColor(current) };
        if (dialog.ShowDialog() != Forms.DialogResult.OK) return;
        string hex = $"#{dialog.Color.A:X2}{dialog.Color.R:X2}{dialog.Color.G:X2}{dialog.Color.B:X2}";
        _editing = target switch
        {
            "Gpu" => _editing with { GpuColor = hex }, "Cpu" => _editing with { CpuColor = hex },
            "Fps" => _editing with { FpsColor = hex }, "Memory" => _editing with { MemoryColor = hex },
            "Network" => _editing with { NetworkColor = hex },
            "Outline" or "Shadow" => _editing with { OutlineColor = hex, ShadowColor = hex }, _ => _editing
        };
        _editing = SettingsService.NormalizeOverlayAppearance(_editing);
        UpdateVisuals();
        PreviewChanged?.Invoke(_editing);
    }

    private void UpdateVisuals()
    {
        FontSizeText.Text = $"{FontSizeSlider.Value:0}px"; OutlineText.Text = $"{OutlineSlider.Value:0.0}";
        ShadowOpacityText.Text = $"{ShadowOpacitySlider.Value:0%}"; ShadowDepthText.Text = $"{ShadowDepthSlider.Value:0.0}";
        PreviewSurface.Background = System.Windows.Media.Brushes.Transparent;
        ApplyColor(GpuColorButton, GpuColorText, _editing.GpuColor); ApplyColor(CpuColorButton, CpuColorText, _editing.CpuColor);
        ApplyColor(FpsColorButton, FpsColorText, _editing.FpsColor); ApplyColor(MemoryColorButton, MemoryColorText, _editing.MemoryColor); ApplyColor(NetworkColorButton, NetworkColorText, _editing.NetworkColor);
        MediaFontFamily family = FontBox.SelectedItem as MediaFontFamily ?? new MediaFontFamily("Consolas");
        DropShadowEffect effect = CreateTextEffect(_editing);
        PreviewPanel.Effect = effect;
        foreach (TextBlock preview in new[] { PreviewGpu, PreviewCpu, PreviewFps, PreviewMemory, PreviewNetwork }) { preview.FontFamily = family; preview.FontSize = FontSizeSlider.Value; preview.Effect = null; }
        PreviewGpu.Foreground = BrushFor(_editing.GpuColor); PreviewCpu.Foreground = BrushFor(_editing.CpuColor); PreviewFps.Foreground = BrushFor(_editing.FpsColor); PreviewMemory.Foreground = BrushFor(_editing.MemoryColor); PreviewNetwork.Foreground = BrushFor(_editing.NetworkColor);
    }

    private static void ApplyColor(WpfButton button, TextBlock text, string hex) { button.Background = BrushFor(hex); text.Text = hex[1..]; }
    private static SolidColorBrush BrushFor(string hex) { var brush = new SolidColorBrush((MediaColor)MediaColorConverter.ConvertFromString(hex)!); brush.Freeze(); return brush; }
    private static DropShadowEffect CreateTextEffect(GameOverlayAppearance appearance)
    {
        var effect = new DropShadowEffect
        {
            Color = (MediaColor)MediaColorConverter.ConvertFromString(appearance.OutlineColor)!,
            BlurRadius = 1d + (appearance.OutlineThickness * 2d),
            ShadowDepth = appearance.ShadowDepth,
            Direction = 315d,
            Opacity = appearance.ShadowOpacity,
            RenderingBias = RenderingBias.Performance
        };
        effect.Freeze();
        return effect;
    }
    private static DrawingColor ToDrawingColor(string hex) { MediaColor color = (MediaColor)MediaColorConverter.ConvertFromString(hex)!; return DrawingColor.FromArgb(color.A, color.R, color.G, color.B); }
    private void TitleBar_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.LeftButton == System.Windows.Input.MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void Apply_Click(object sender, RoutedEventArgs e) { _applied = Read(); Applied?.Invoke(_applied); Hide(); MemoryOptimizer.TrimWorkingSet(); }
    private void Cancel_Click(object sender, RoutedEventArgs e) => CancelAndHide();
    private void CancelAndHide() { PreviewChanged?.Invoke(_applied); Hide(); MemoryOptimizer.TrimWorkingSet(); }

    private sealed record OverlaySkin(string Name, GameOverlayAppearance Appearance);
}
