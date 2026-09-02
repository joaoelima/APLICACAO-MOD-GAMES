using System.Diagnostics;
using System.Windows;
using GameTrainer.Core.Memory;
using GameTrainer.Core.Processes;
using GameTrainer.Modules.CrimsonDesert;

namespace GameTrainer.App;

public partial class MainWindow : Window
{
    private readonly CrimsonDesertModule _module = new();
    private readonly GameProcessDetector _detector = new();
    private readonly ProcessMemory _memory = new();
    private readonly System.Windows.Threading.DispatcherTimer _timer;
    private int? _attachedProcessId;

    public MainWindow()
    {
        InitializeComponent();
        SectionsControl.ItemsSource = _module.Definition.Sections;

        _timer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(2)
        };
        _timer.Tick += (_, _) => RefreshGameState();
        _timer.Start();

        RefreshGameState();
    }

    private async void RefreshGameState()
    {
        try
        {
            var process = _detector.FindRunningProcess(_module.Definition.ProcessNames);
            if (process is null)
            {
                if (_memory.IsAttached) _memory.Detach();
                _attachedProcessId = null;
                GameStatusText.Text = "Aguardando o Crimson Desert ser iniciado...";
                StatusBadge.Text = "DESCONECTADO";
                return;
            }

            if (_attachedProcessId != process.Id)
            {
                _memory.Attach(process);
                await _module.AttachAsync(_memory);
                _attachedProcessId = process.Id;
            }

            var version = TryGetVersion(process);
            GameStatusText.Text = $"Jogo em execução • PID {process.Id}" + (version is null ? string.Empty : $" • {version}");
            StatusBadge.Text = "CONECTADO";
        }
        catch (Exception ex)
        {
            GameStatusText.Text = $"Falha ao conectar: {ex.Message}";
            StatusBadge.Text = "ERRO";
        }
    }

    private static string? TryGetVersion(Process process)
    {
        try
        {
            var path = process.MainModule?.FileName;
            if (string.IsNullOrWhiteSpace(path)) return null;
            var info = FileVersionInfo.GetVersionInfo(path);
            return info.FileVersion;
        }
        catch
        {
            return null;
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _timer.Stop();
        _memory.Dispose();
        base.OnClosed(e);
    }
}
