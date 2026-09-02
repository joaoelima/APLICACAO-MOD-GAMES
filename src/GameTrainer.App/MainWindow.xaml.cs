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
    private readonly MemoryDiscoveryScanner _discovery;
    private readonly System.Windows.Threading.DispatcherTimer _processTimer;
    private readonly System.Windows.Threading.DispatcherTimer _trainerTimer;

    private int? _attachedProcessId;
    private bool _updatingToggle;
    private bool _refreshingGameState;
    private bool _trainerTickRunning;
    private bool _initialScanStarted;
    private bool _isClosing;
    private bool _discoveryBusy;

    public MainWindow()
    {
        InitializeComponent();
        SectionsControl.ItemsSource = _module.Definition.Sections;
        _discovery = new MemoryDiscoveryScanner(_memory);

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
        if (_trainerTickRunning || !_memory.IsAttached || _isClosing || _discoveryBusy)
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

        var report = $"Game Trainer v0.2.7{Environment.NewLine}" +
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

    private async void StartDiscovery_Click(object sender, RoutedEventArgs e)
    {
        if (!_memory.IsAttached)
        {
            DiscoveryStatusText.Text = "O Crimson Desert precisa estar conectado antes de iniciar o mapeamento.";
            return;
        }

        await RunDiscoveryActionAsync(async () =>
        {
            DiscoveryStatusText.Text = "Capturando memória base para Vida. Aguarde...";
            await _discovery.CaptureBaselineAsync("Vida");
            DiscoveryStatusText.Text = $"Base de Vida capturada ({_discovery.CapturedRegions} regiões / {_discovery.CapturedBytes / (1024d * 1024):F1} MB). Agora tome dano no jogo e clique em “Registrar perda de Vida”.";
            StartDiscoveryButton.IsEnabled = false;
            RecordHealthButton.IsEnabled = true;
        });
    }

    private async void RecordHealth_Click(object sender, RoutedEventArgs e)
    {
        await RunDiscoveryActionAsync(async () =>
        {
            DiscoveryStatusText.Text = "Comparando a memória após a perda de Vida...";
            var result = await _discovery.CaptureDecreaseAsync("VIDA");
            DiscoveryStatusText.Text = $"Vida registrada: {result.TotalCandidates} candidatos retidos. Capturando nova base para Vigor...";
            await _discovery.CaptureBaselineAsync("Vigor");
            DiscoveryStatusText.Text = "Base de Vigor pronta. Agora corra/gaste stamina no jogo e clique em “Registrar gasto de Vigor”.";
            RecordHealthButton.IsEnabled = false;
            RecordStaminaButton.IsEnabled = true;
        });
    }

    private async void RecordStamina_Click(object sender, RoutedEventArgs e)
    {
        await RunDiscoveryActionAsync(async () =>
        {
            DiscoveryStatusText.Text = "Comparando a memória após o gasto de Vigor...";
            var result = await _discovery.CaptureDecreaseAsync("VIGOR");
            DiscoveryStatusText.Text = $"Vigor registrado: {result.TotalCandidates} candidatos retidos. Capturando nova base para Espírito...";
            await _discovery.CaptureBaselineAsync("Espírito");
            DiscoveryStatusText.Text = "Base de Espírito pronta. Agora gaste Espírito no jogo e clique em “Registrar gasto de Espírito”.";
            RecordStaminaButton.IsEnabled = false;
            RecordSpiritButton.IsEnabled = true;
        });
    }

    private async void RecordSpirit_Click(object sender, RoutedEventArgs e)
    {
        await RunDiscoveryActionAsync(async () =>
        {
            DiscoveryStatusText.Text = "Comparando a memória após o gasto de Espírito...";
            var result = await _discovery.CaptureDecreaseAsync("ESPÍRITO");
            DiscoveryStatusText.Text = $"Espírito registrado: {result.TotalCandidates} candidatos retidos. Clique em “Finalizar análise e copiar log”.";
            RecordSpiritButton.IsEnabled = false;
            FinishDiscoveryButton.IsEnabled = true;
        });
    }

    private void FinishDiscovery_Click(object sender, RoutedEventArgs e)
    {
        if (_memory.Process is null)
        {
            DiscoveryStatusText.Text = "O processo do jogo não está mais conectado.";
            return;
        }

        var version = TryGetVersion(_memory.Process) ?? "desconhecida";
        var report = _discovery.BuildReport(version);

        try
        {
            Clipboard.SetText(report);
            DiscoveryStatusText.Text = "Análise finalizada e log copiado. Cole o conteúdo aqui na conversa.";
            FinishDiscoveryButton.IsEnabled = false;
            StartDiscoveryButton.IsEnabled = true;
        }
        catch (Exception ex)
        {
            DiscoveryStatusText.Text = $"A análise terminou, mas não foi possível copiar o log: {ex.Message}";
        }
    }

    private async Task RunDiscoveryActionAsync(Func<Task> action)
    {
        if (_discoveryBusy)
            return;

        _discoveryBusy = true;
        SetDiscoveryButtonsEnabled(false);
        StatusBadge.Text = "MAPEANDO";

        try
        {
            await action();
        }
        catch (Exception ex)
        {
            DiscoveryStatusText.Text = $"Falha no mapeamento: {ex.Message}";
            StartDiscoveryButton.IsEnabled = true;
        }
        finally
        {
            _discoveryBusy = false;
            StatusBadge.Text = _module.IsRuntimeResolved ? "CONECTADO" : "DIAGNÓSTICO";
            RestoreDiscoveryStepButtonState();
        }
    }

    private void SetDiscoveryButtonsEnabled(bool enabled)
    {
        StartDiscoveryButton.IsEnabled = enabled;
        RecordHealthButton.IsEnabled = enabled;
        RecordStaminaButton.IsEnabled = enabled;
        RecordSpiritButton.IsEnabled = enabled;
        FinishDiscoveryButton.IsEnabled = enabled;
    }

    private void RestoreDiscoveryStepButtonState()
    {
        if (FinishDiscoveryButton.IsEnabled)
            return;

        if (RecordSpiritButton.IsEnabled || RecordStaminaButton.IsEnabled || RecordHealthButton.IsEnabled)
            return;

        // O próprio passo atual é reabilitado dentro da ação; caso uma ação falhe,
        // permitimos reiniciar o mapeamento sem fechar o aplicativo.
        if (_discovery.Results.Count == 0)
            StartDiscoveryButton.IsEnabled = true;
        else if (!_discovery.Results.ContainsKey("VIDA"))
            RecordHealthButton.IsEnabled = true;
        else if (!_discovery.Results.ContainsKey("VIGOR"))
            RecordStaminaButton.IsEnabled = true;
        else if (!_discovery.Results.ContainsKey("ESPÍRITO"))
            RecordSpiritButton.IsEnabled = true;
        else
            FinishDiscoveryButton.IsEnabled = true;
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
