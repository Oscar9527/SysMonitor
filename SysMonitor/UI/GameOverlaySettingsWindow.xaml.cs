using System.Collections.ObjectModel;
using System.Windows;
using SysMonitor.Models;

namespace SysMonitor.UI;

public partial class GameOverlaySettingsWindow : Window
{
    private readonly ObservableCollection<MetricItem> _items = [];
    public event Action<GameOverlayMetricVisibility, string>? Applied;

    public GameOverlaySettingsWindow()
    {
        InitializeComponent();
        MetricList.ItemsSource = _items;
        Closing += (_, e) => { e.Cancel = true; Hide(); };
    }

    public void LoadConfiguration(GameOverlayMetricVisibility visibility, string? sampling)
    {
        var enabled = new Dictionary<string, bool>(StringComparer.Ordinal)
        {
            ["fps"] = visibility.FrameRate, ["cpu"] = visibility.Cpu, ["gpu"] = visibility.Gpu,
            ["memory"] = visibility.Memory, ["network"] = visibility.Network
        };
        _items.Clear();
        foreach (string id in GameOverlayMetricOrder.Normalize(visibility.Order))
            _items.Add(new MetricItem(id, NameFor(id), enabled[id]));
        SamplingBox.SelectedValue = sampling?.Trim().ToLowerInvariant() switch { "low" => "low", "high" => "high", _ => "standard" };
        MetricList.SelectedIndex = _items.Count == 0 ? -1 : 0;
    }

    private void MoveUp_Click(object sender, RoutedEventArgs e) => Move(-1);
    private void MoveDown_Click(object sender, RoutedEventArgs e) => Move(1);
    private void Move(int delta)
    {
        int current = MetricList.SelectedIndex;
        int target = current + delta;
        if (current < 0 || target < 0 || target >= _items.Count) return;
        _items.Move(current, target);
        MetricList.SelectedIndex = target;
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        bool Enabled(string id) => _items.First(item => item.Id == id).Enabled;
        var metrics = new GameOverlayMetricVisibility(Enabled("fps"), Enabled("cpu"), Enabled("gpu"), Enabled("memory"), Enabled("network"))
        {
            Order = _items.Select(item => item.Id).ToArray()
        };
        Applied?.Invoke(metrics, SamplingBox.SelectedValue as string ?? "standard");
        Hide();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Hide();

    private static string NameFor(string id) => id switch
    {
        "gpu" => "GPU（使用率、温度、频率）",
        "cpu" => "CPU（使用率、温度、频率）",
        "fps" => "FPS（当前帧率）",
        "memory" => "内存（占用、配置频率）",
        "network" => "网络（下载、上传）",
        _ => id
    };

    private sealed class MetricItem(string id, string name, bool enabled)
    {
        public string Id { get; } = id;
        public string Name { get; } = name;
        public bool Enabled { get; set; } = enabled;
    }
}
