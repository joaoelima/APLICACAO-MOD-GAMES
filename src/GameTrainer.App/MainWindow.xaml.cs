using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using GameTrainer.Core.Memory;
using GameTrainer.Core.Processes;
using GameTrainer.Modules.CrimsonDesert;

namespace GameTrainer.App;

public partial class MainWindow : Window
{
    private readonly CrimsonDesertModule _module = new();
    private readonly GameProcessDetector _detector = new();
    private readonly ProcessMemory _memory = new();
    private readonly System.Windows.Threading.DispatcherTimer _processTimer;
    private readonly System.Windows.Threading.DispatcherTimer _trainerTimer;

    private int? _attachedProcessId;
    private bool _updatingToggle;
    private bool _refreshingGameState;
    private bool _trainerTickRunning;
    private bool _initialScanStarted;
    private bool _isClosing;

    public MainWindow()
    {
        InitializeComponent();
        SectionsControl.ItemsSource = _module.Definition.Sections;

        _processTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(2)
        };
        _processTimer.Tick += async (_, _) => await RefreshGameStateAsync();

        _trainerTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(50)
        };
        _trainerTimer.Tick += async (_, _) => await RunTrainerTickAsync();

        ContentRendered += MainWindow_ContentRendered;
    }

    private async void MainWindow_ContentRendered(object? sender, EventArgs e)
    {
        if (_initialScanStarted)
            return;

        _initialScanStarted = true;
        _processTimer.Start();
        _trainerTimer.Start();

        await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
        await RefreshGameStateAsync();
    }

    private async Task RefreshGameStateAsync()
    {
        if (_refreshingGameState || _isClosing)
            return;

        _refreshingGameState = true;
        try
        {
            var process = _detector.FindRunningProcess(_module.Definition.ProcessNames);
            if (process is null)
            {
                if (_memory.IsAttached)
                    _memory.Detach();

                _attachedProcessId = null;
                GameStatusText.Text = "Aguardando o Crimson Desert ser iniciado...";
                TrainerStatusText.Text = "Abra o jogo e carregue o personagem para ativar as modificações.";
                StatusBadge.Text = "DESCONECTADO";
                return;
            }

            var version = TryGetVersion(process);
            GameStatusText.Text = $"Jogo em execução • PID {process.Id}" +
                                  (version is null ? string.Empty : $" • {version}");

            if (_attachedProcessId != process.Id || !_memory.IsAttached)
            {
                _memory.Attach(process);
                _attachedProcessId = process.Id;

                TrainerStatusText.Text = "Jogo detectado. Analisando a memória em segundo plano...";
                StatusBadge.Text = "ANALISANDO";

                await Task.Run(() => _module.AttachAsync(_memory));

                if (_isClosing)
                    return;
            }

            TrainerStatusText.Text = _module.RuntimeStatus;
            StatusBadge.Text = _module.IsRuntimeResolved ? "CONECTADO" : "DIAGNÓSTICO";
        }
        catch (Exception ex)
        {
            if (_isClosing)
                return;

            GameStatusText.Text = $"Falha ao conectar: {ex.Message}";
            TrainerStatusText.Text = "O trainer não fará escritas enquanto a conexão não estiver válida.";
            StatusBadge.Text = "ERRO";
        }
        finally
        {
            _refreshingGameState = false;
        }
    }

    private async Task RunTrainerTickAsync()
    {
        if (_trainerTickRunning || !_memory.IsAttached || _isClosing)
            return;

        _trainerTickRunning = true;
        try
        {
            await _module.TickAsync();
            TrainerStatusText.Text = _module.RuntimeStatus;
        }
        catch (Exception ex)
        {
            TrainerStatusText.Text = $"Falha no ciclo do trainer: {ex.Message}";
        }
        finally
        {
            _trainerTickRunning = false;
        }
    }

    private async void Reprobe_Click(object sender, RoutedEventArgs e)
    {
        if (!_memory.IsAttached)
        {
            TrainerStatusText.Text = "O Crimson Desert ainda não está conectado.";
            return;
        }

        TrainerStatusText.Text = "Reanalisando memória em segundo plano...";
        StatusBadge.Text = "ANALISANDO";

        try
        {
            var ok = await Task.Run(() => _module.ReprobeAsync());

            if (_isClosing)
                return;

            TrainerStatusText.Text = _module.RuntimeStatus;
            StatusBadge.Text = ok ? "CONECTADO" : "DIAGNÓSTICO";
        }
        catch (Exception ex)
        {
            if (_isClosing)
                return;

            TrainerStatusText.Text = $"Falha ao reanalisar: {ex.Message}";
            StatusBadge.Text = "ERRO";
        }
    }

    private void CopyDiagnostic_Click(object sender, RoutedEventArgs e)
    {
        var version = _memory.Process is null ? null : TryGetVersion(_memory.Process);
        var processInfo = _memory.Process is null
            ? "Processo: não conectado"
            : $"Processo: {_memory.Process.ProcessName}.exe | PID {_memory.Process.Id} | versão {version ?? "desconhecida"}";

        var report = $"Game Trainer v0.2.6{Environment.NewLine}" +
                     $"{processInfo}{Environment.NewLine}" +
                     $"Status: {_module.RuntimeStatus}{Environment.NewLine}{Environment.NewLine}" +
                     _module.DiagnosticReport;

        try
        {
            Clipboard.SetText(report);
            TrainerStatusText.Text = "Diagnóstico copiado. Cole essa informação na conversa comigo.";
        }
        catch (Exception ex)
        {
            TrainerStatusText.Text = $"Não foi possível copiar o diagnóstico: {ex.Message}";
        }
    }

    private async void FeatureToggle_Checked(object sender, RoutedEventArgs e)
    {
        if (_updatingToggle || sender is not CheckBox toggle || toggle.Tag is not string featureId)
            return;

        await ApplyToggleAsync(toggle, featureId, true);
    }

    private async void FeatureToggle_Unchecked(object sender, RoutedEventArgs e)
    {
        if (_updatingToggle || sender is not CheckBox toggle || toggle.Tag is not string featureId)
            return;

        await ApplyToggleAsync(toggle, featureId, false);
    }

    private async Task ApplyToggleAsync(CheckBox toggle, string featureId, bool enabled)
    {
        if (!_memory.IsAttached)
        {
            SetToggleVisual(toggle, !enabled);
            TrainerStatusText.Text = "Inicie o Crimson Desert antes de ativar uma modificação.";
            return;
        }

        if (!_module.IsRuntimeResolved && enabled)
        {
            SetToggleVisual(toggle, false);
            TrainerStatusText.Text = "Aguarde a análise da memória terminar antes de ativar uma modificação.";
            return;
        }

        try
        {
            var success = await _module.SetToggleAsync(featureId, enabled);
            if (!success)
            {
                SetToggleVisual(toggle, !enabled);
                TrainerStatusText.Text = string.IsNullOrWhiteSpace(_module.LastError)
                    ? "Não foi possível ativar este recurso nesta versão do jogo."
                    : _module.LastError;
                return;
            }

            TrainerStatusText.Text = enabled
                ? $"Recurso ativado • {_module.RuntimeStatus}"
                : $"Recurso desativado • {_module.RuntimeStatus}";
        }
        catch (Exception ex)
        {
            SetToggleVisual(toggle, !enabled);
            TrainerStatusText.Text = $"Falha ao alterar o recurso: {ex.Message}";
        }
    }

    private void SetToggleVisual(CheckBox toggle, bool value)
    {
        _updatingToggle = true;
        try
        {
            toggle.IsChecked = value;
        }
        finally
        {
            _updatingToggle = false;
        }
    }

    private static string? TryGetVersion(Process process)
    {
        try
        {
            var path = process.MainModule?.FileName;
            if (string.IsNullOrWhiteSpace(path))
                return null;

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
        _isClosing = true;
        _processTimer.Stop();
        _trainerTimer.Stop();
        _memory.Dispose();
        base.OnClosed(e);
    }
}
