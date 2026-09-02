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
    private readonly GameLauncher _launcher = new();
    private readonly ProcessMemory _memory = new();
    private readonly System.Windows.Threading.DispatcherTimer _processTimer;
    private readonly System.Windows.Threading.DispatcherTimer _trainerTimer;
    private int? _attachedProcessId;
    private GameInstallation? _installation;
    private bool _updatingToggle, _refreshingGameState, _trainerTickRunning, _initialScanStarted, _isClosing, _launchRequested;

    public MainWindow()
    {
        InitializeComponent();
        SectionsControl.ItemsSource = _module.Definition.Sections;
        _processTimer = new() { Interval = TimeSpan.FromSeconds(2) };
        _processTimer.Tick += async (_, _) => await RefreshGameStateAsync();
        _trainerTimer = new() { Interval = TimeSpan.FromMilliseconds(50) };
        _trainerTimer.Tick += async (_, _) => await RunTrainerTickAsync();
        ContentRendered += MainWindow_ContentRendered;
    }

    private async void MainWindow_ContentRendered(object? sender, EventArgs e)
    {
        if (_initialScanStarted) return;
        _initialScanStarted = true;
        await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
        await DiscoverInstallationAsync();
        _processTimer.Start();
        _trainerTimer.Start();
        await RefreshGameStateAsync();
    }

    private async Task DiscoverInstallationAsync()
    {
        InstallationStatusText.Text = "Procurando Crimson Desert no Steam e nos programas instalados...";
        try
        {
            _installation = await Task.Run(() => _launcher.FindInstallation("Crimson Desert", "CrimsonDesert.exe"));
            if (_installation is null)
            {
                InstallationStatusText.Text = "Instalação não localizada automaticamente. Se o jogo já estiver aberto, o trainer ainda poderá detectá-lo.";
                LaunchGameButton.IsEnabled = false;
                return;
            }
            InstallationStatusText.Text = $"Detectado • {_installation.Platform} • {_installation.InstallDirectory}";
            LaunchGameButton.IsEnabled = true;
        }
        catch (Exception ex)
        {
            InstallationStatusText.Text = $"Falha ao detectar instalação: {ex.Message}";
            LaunchGameButton.IsEnabled = false;
        }
    }

    private async void LaunchGame_Click(object sender, RoutedEventArgs e)
    {
        if (_installation is null)
        {
            await DiscoverInstallationAsync();
            if (_installation is null) return;
        }
        try
        {
            var existing = _detector.FindRunningProcess(_module.Definition.ProcessNames);
            if (existing is not null)
            {
                GameStatusText.Text = $"Crimson Desert já está em execução • PID {existing.Id}";
                await RefreshGameStateAsync();
                return;
            }
            _launcher.Launch(_installation);
            _launchRequested = true;
            LaunchGameButton.IsEnabled = false;
            LaunchGameButton.Content = "INICIANDO...";
            GameStatusText.Text = "Jogo iniciado pelo Game Trainer • aguardando CrimsonDesert.exe...";
            TrainerStatusText.Text = "Acompanhando a inicialização do jogo. O trainer conectará automaticamente quando o processo estiver disponível.";
            StatusBadge.Text = "INICIANDO";
        }
        catch (Exception ex)
        {
            LaunchGameButton.IsEnabled = true;
            LaunchGameButton.Content = "INICIAR JOGO";
            GameStatusText.Text = $"Não foi possível iniciar o jogo: {ex.Message}";
            StatusBadge.Text = "ERRO";
        }
    }

    private async Task RefreshGameStateAsync()
    {
        if (_refreshingGameState || _isClosing) return;
        _refreshingGameState = true;
        try
        {
            var process = _detector.FindRunningProcess(_module.Definition.ProcessNames);
            if (process is null)
            {
                if (_memory.IsAttached) { _module.Detach(); _memory.Detach(); }
                _attachedProcessId = null;
                if (!_launchRequested)
                {
                    GameStatusText.Text = _installation is null ? "Crimson Desert não está em execução." : "Crimson Desert detectado e pronto para iniciar.";
                    TrainerStatusText.Text = _installation is null ? "O trainer também detectará o jogo caso ele seja aberto externamente." : "Clique em INICIAR JOGO. O trainer acompanhará a sessão automaticamente.";
                    StatusBadge.Text = _installation is null ? "NÃO LOCALIZADO" : "PRONTO PARA JOGAR";
                    LaunchGameButton.IsEnabled = _installation is not null;
                    LaunchGameButton.Content = "INICIAR JOGO";
                }
                return;
            }

            _launchRequested = false;
            LaunchGameButton.IsEnabled = false;
            LaunchGameButton.Content = "EM EXECUÇÃO";
            var version = TryGetVersion(process);
            GameStatusText.Text = $"Jogo em execução • PID {process.Id}" + (version is null ? "" : $" • {version}");

            if (_attachedProcessId != process.Id || !_memory.IsAttached)
            {
                if (_memory.IsAttached) { _module.Detach(); _memory.Detach(); }
                _memory.Attach(process);
                _attachedProcessId = process.Id;
                TrainerStatusText.Text = "Processo acompanhado. Preparando captura do jogador...";
                StatusBadge.Text = "ANALISANDO";
                await Task.Run(() => _module.AttachAsync(_memory));
                if (_isClosing) return;
            }
            TrainerStatusText.Text = _module.RuntimeStatus;
            StatusBadge.Text = _module.IsRuntimeResolved ? "PRONTO" : "DIAGNÓSTICO";
        }
        catch (Exception ex)
        {
            if (!_isClosing) { GameStatusText.Text = $"Falha ao conectar: {ex.Message}"; TrainerStatusText.Text = "O trainer não fará escritas enquanto a conexão não estiver válida."; StatusBadge.Text = "ERRO"; }
        }
        finally { _refreshingGameState = false; }
    }

    private async Task RunTrainerTickAsync()
    {
        if (_trainerTickRunning || !_memory.IsAttached || _isClosing) return;
        _trainerTickRunning = true;
        try { await _module.TickAsync(); TrainerStatusText.Text = _module.RuntimeStatus; StatusBadge.Text = _module.IsRuntimeResolved ? "PRONTO" : "DIAGNÓSTICO"; }
        catch (Exception ex) { TrainerStatusText.Text = $"Falha no ciclo do trainer: {ex.Message}"; StatusBadge.Text = "ERRO"; }
        finally { _trainerTickRunning = false; }
    }

    private async void Reprobe_Click(object sender, RoutedEventArgs e)
    {
        if (!_memory.IsAttached) { TrainerStatusText.Text = "O Crimson Desert ainda não está conectado."; return; }
        TrainerStatusText.Text = "Reanalisando memória em segundo plano..."; StatusBadge.Text = "ANALISANDO";
        try { var ok = await Task.Run(() => _module.ReprobeAsync()); if (!_isClosing) { TrainerStatusText.Text = _module.RuntimeStatus; StatusBadge.Text = ok ? "PRONTO" : "DIAGNÓSTICO"; } }
        catch (Exception ex) { if (!_isClosing) { TrainerStatusText.Text = $"Falha ao reanalisar: {ex.Message}"; StatusBadge.Text = "ERRO"; } }
    }

    private void CopyDiagnostic_Click(object sender, RoutedEventArgs e)
    {
        var version = _memory.Process is null ? null : TryGetVersion(_memory.Process);
        var processInfo = _memory.Process is null ? "Processo: não conectado" : $"Processo: {_memory.Process.ProcessName}.exe | PID {_memory.Process.Id} | versão {version ?? "desconhecida"}";
        var installInfo = _installation is null ? "Instalação: não localizada" : $"Instalação: {_installation.Platform} | {_installation.InstallDirectory}";
        var report = $"Game Trainer v0.3.0{Environment.NewLine}{installInfo}{Environment.NewLine}{processInfo}{Environment.NewLine}Status: {_module.RuntimeStatus}{Environment.NewLine}{Environment.NewLine}{_module.DiagnosticReport}";
        try { Clipboard.SetText(report); TrainerStatusText.Text = "Diagnóstico copiado. Cole essa informação na conversa comigo."; }
        catch (Exception ex) { TrainerStatusText.Text = $"Não foi possível copiar o diagnóstico: {ex.Message}"; }
    }

    private async void FeatureToggle_Checked(object sender, RoutedEventArgs e) { if (!_updatingToggle && sender is CheckBox t && t.Tag is string id) await ApplyToggleAsync(t, id, true); }
    private async void FeatureToggle_Unchecked(object sender, RoutedEventArgs e) { if (!_updatingToggle && sender is CheckBox t && t.Tag is string id) await ApplyToggleAsync(t, id, false); }

    private async Task ApplyToggleAsync(CheckBox toggle, string featureId, bool enabled)
    {
        if (!_memory.IsAttached) { SetToggleVisual(toggle, !enabled); TrainerStatusText.Text = "Inicie o Crimson Desert antes de ativar uma modificação."; return; }
        if (!_module.IsRuntimeResolved && enabled) { SetToggleVisual(toggle, false); TrainerStatusText.Text = "Os atributos do jogador ainda não foram validados. Use Reanalisar memória ou Copiar diagnóstico."; return; }
        try
        {
            var success = await _module.SetToggleAsync(featureId, enabled);
            if (!success) { SetToggleVisual(toggle, !enabled); TrainerStatusText.Text = string.IsNullOrWhiteSpace(_module.LastError) ? "Não foi possível ativar este recurso nesta versão do jogo." : _module.LastError; return; }
            TrainerStatusText.Text = enabled ? $"Recurso ativado • {_module.RuntimeStatus}" : $"Recurso desativado • {_module.RuntimeStatus}";
        }
        catch (Exception ex) { SetToggleVisual(toggle, !enabled); TrainerStatusText.Text = $"Falha ao alterar o recurso: {ex.Message}"; }
    }

    private void SetToggleVisual(CheckBox toggle, bool value) { _updatingToggle = true; try { toggle.IsChecked = value; } finally { _updatingToggle = false; } }
    private static string? TryGetVersion(Process process) { try { var path = process.MainModule?.FileName; if (string.IsNullOrWhiteSpace(path)) return null; return FileVersionInfo.GetVersionInfo(path).FileVersion; } catch { return null; } }

    protected override void OnClosed(EventArgs e)
    {
        _isClosing = true; _processTimer.Stop(); _trainerTimer.Stop(); _module.Detach(); _memory.Dispose(); base.OnClosed(e);
    }
}
