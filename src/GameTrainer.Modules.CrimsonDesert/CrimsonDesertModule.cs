using System.Buffers.Binary;
using GameTrainer.Core.Memory;
using GameTrainer.Core.Models;
using GameTrainer.Core.Modules;

namespace GameTrainer.Modules.CrimsonDesert;

public sealed class CrimsonDesertModule : IGameModule, IDisposable
{
    private const int PlayerHookLength = 11;
    private const int PlayerCaveSize = 0x100;
    private const int PlayerSlotOffset = 0x80;
    private const int ComponentSlotOffset = 0x88;

    private const int StatHookLength = 9;
    private const int StatCaveSize = 0x100;
    private const int StatSlotOffset = 0x80;

    private const int HealthIdOffset = 0x000;
    private const int HealthCurrentOffset = 0x008;
    private const int HealthMaxOffset = 0x018;
    private const int StaminaIdOffset = 0x510;
    private const int StaminaCurrentOffset = 0x518;
    private const int StaminaMaxOffset = 0x528;
    private const int SpiritIdOffset = 0x5A0;
    private const int SpiritCurrentOffset = 0x5A8;
    private const int SpiritMaxOffset = 0x5B8;

    private const int ExpectedHealthId = 0;
    private const int ExpectedSpiritId = 19;

    private const string CheatTableCurrentPlayerAob =
        "48 ? ? ? 48 ? ? ? ? ? ? 48 ? ? ? 0F B7 ? ? 66 ? ? ? ? B8 ? ? ? ? 66 ? ? 74 ? 48 ? ? ? ? E8 ? ? ? ? 0F B7 ? 48 ? ? ? ? 48 ? ? B2 ? FF ? ? 0F B7 ? 48 ? ? ? ? E8 ? ? ? ? 3A";
    private const string CurrentPlayerAob =
        "48 8B 43 68 48 8B 88 A0 01 00 00 48 8B 41 38 0F B7 48 20";
    private const string CurrentPlayerShortAob =
        "48 8B 43 68 48 8B 88 A0 01 00 00";
    private const string CurrentPlayerLegacyShortAob =
        "48 8B 43 68 48 8B 88 B0 01 00 00";

    // AOB exata usada pela CT no script StaminaInj/Godmode.
    private const string StatWriteAob = "48 89 5F 08 48 8B 5C 24 48";

    private ProcessMemory? _memory;
    private RuntimeState _runtime = new();

    private nint _playerHookAddress;
    private nint _playerCave;
    private nint _playerSlot;
    private nint _componentSlot;
    private byte[]? _playerOriginalBytes;
    private bool _playerHookInstalled;
    private string _playerSignature = "não resolvida";

    private nint _statHookAddress;
    private nint _statCave;
    private nint _statSlot;
    private byte[]? _statOriginalBytes;
    private bool _statHookInstalled;

    private bool _health;
    private bool _stamina;
    private bool _spirit;

    public string LastError { get; private set; } = string.Empty;
    public string RuntimeStatus { get; private set; } = "Aguardando o jogo";
    public string DiagnosticReport { get; private set; } = "Nenhum diagnóstico executado ainda.";
    public bool IsRuntimeResolved => _runtime.IsResolved;

    public GameDefinition Definition { get; } = new()
    {
        Id = "crimson-desert",
        Name = "Crimson Desert",
        ProcessNames = new[] { "CrimsonDesert.exe" },
        Sections = new[]
        {
            new TrainerSection
            {
                Name = "Jogador",
                Features = new TrainerFeature[]
                {
                    new() { Id = "infinite-health", Name = "Vida ilimitada", Description = "Mantém a vida no máximo atual do personagem.", Type = TrainerFeatureType.Toggle },
                    new() { Id = "infinite-stamina", Name = "Vigor ilimitado", Description = "Mantém o vigor no máximo atual do personagem.", Type = TrainerFeatureType.Toggle },
                    new() { Id = "infinite-spirit", Name = "Espírito ilimitado", Description = "Mantém o espírito no máximo atual do personagem.", Type = TrainerFeatureType.Toggle }
                }
            },
            new TrainerSection
            {
                Name = "Combate",
                Features = new TrainerFeature[]
                {
                    new()
                    {
                        Id = "one-hit-kill",
                        Name = "Super Dano / Mortes com Um Golpe",
                        Description = "Em desenvolvimento.",
                        Type = TrainerFeatureType.Toggle,
                        IsAvailable = false
                    }
                }
            }
        }
    };

    public async Task AttachAsync(ProcessMemory processMemory, CancellationToken cancellationToken = default)
    {
        Detach();
        _memory = processMemory;
        _runtime = new RuntimeState();
        LastError = string.Empty;

        var logs = new List<string>();
        InstallPlayerHook(out var playerLog);
        logs.Add(playerLog);

        if (!InstallStatHook(out var statLog))
        {
            logs.Add(statLog);
            DiagnosticReport = string.Join(Environment.NewLine, logs.Where(x => !string.IsNullOrWhiteSpace(x)));
            RuntimeStatus = "Jogo conectado, mas o hook direto de stats da CT não pôde ser instalado.";
            LastError = RuntimeStatus;
            return;
        }

        logs.Add(statLog);
        await WaitForStatsAsync(string.Join(Environment.NewLine, logs), cancellationToken);
    }

    public async Task<bool> ReprobeAsync(CancellationToken cancellationToken = default)
    {
        if (_memory is null || !_memory.IsAttached)
        {
            LastError = "O Crimson Desert não está conectado.";
            RuntimeStatus = LastError;
            return false;
        }

        _runtime = new RuntimeState();
        var logs = new List<string>();

        string playerLog;
        if (!_playerHookInstalled)
            InstallPlayerHook(out playerLog);
        else
            playerLog = BuildPlayerHeader();
        logs.Add(playerLog);

        if (!_statHookInstalled)
        {
            if (!InstallStatHook(out var statLog))
            {
                logs.Add(statLog);
                DiagnosticReport = string.Join(Environment.NewLine, logs.Where(x => !string.IsNullOrWhiteSpace(x)));
                RuntimeStatus = "Não foi possível instalar o hook direto de stats da CT.";
                LastError = RuntimeStatus;
                return false;
            }
            logs.Add(statLog);
        }
        else
        {
            logs.Add(BuildStatHeader());
        }

        return await WaitForStatsAsync(string.Join(Environment.NewLine, logs), cancellationToken);
    }

    public Task<bool> SetToggleAsync(string featureId, bool enabled, CancellationToken cancellationToken = default)
    {
        if (_memory is null || !_memory.IsAttached)
        {
            LastError = "O Crimson Desert não está conectado.";
            return Task.FromResult(false);
        }

        if (featureId == "one-hit-kill")
        {
            LastError = "Super Dano ainda não está disponível nesta build.";
            return Task.FromResult(false);
        }

        if (enabled && !EnsureRuntime())
            return Task.FromResult(false);

        switch (featureId)
        {
            case "infinite-health": _health = enabled; break;
            case "infinite-stamina": _stamina = enabled; break;
            case "infinite-spirit": _spirit = enabled; break;
            default:
                LastError = $"Recurso desconhecido: {featureId}.";
                return Task.FromResult(false);
        }

        LastError = string.Empty;
        return Task.FromResult(true);
    }

    public Task<bool> SetValueAsync(string featureId, double value, CancellationToken cancellationToken = default)
        => Task.FromResult(false);

    public Task TickAsync(CancellationToken cancellationToken = default)
    {
        if (_memory is null || !_memory.IsAttached)
            return Task.CompletedTask;

        if (_statHookInstalled && TryReadCapturedStats(out var stats)
            && stats != 0 && stats != _runtime.StatsBase
            && TryResolveDirectStats(stats, out var runtime, out var diagnostic))
        {
            FillCapturedPlayer(runtime);
            _runtime = runtime;
            DiagnosticReport = BuildCombinedHeader() + Environment.NewLine + diagnostic;
            RuntimeStatus = "Pronto • stats do jogador capturados diretamente pela rotina CT";
            LastError = string.Empty;
        }

        if (!_health && !_stamina && !_spirit)
            return Task.CompletedTask;

        if (!EnsureRuntime())
            return Task.CompletedTask;

        if (_health) RestoreCurrentToMaximum(_runtime.HealthCurrent, _runtime.HealthMax, "Vida");
        if (_stamina) RestoreCurrentToMaximum(_runtime.StaminaCurrent, _runtime.StaminaMax, "Vigor");
        if (_spirit) RestoreCurrentToMaximum(_runtime.SpiritCurrent, _runtime.SpiritMax, "Espírito");
        return Task.CompletedTask;
    }

    private async Task<bool> WaitForStatsAsync(string header, CancellationToken cancellationToken)
    {
        var log = new List<string> { header };
        RuntimeStatus = "Hooks instalados. Aguardando a rotina de escrita de stats identificar o jogador...";

        for (var attempt = 0; attempt < 120; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (TryReadCapturedStats(out var stats) && stats != 0)
            {
                if (TryResolveDirectStats(stats, out var runtime, out var diagnostic))
                {
                    FillCapturedPlayer(runtime);
                    _runtime = runtime;
                    log.Add(diagnostic);
                    DiagnosticReport = string.Join(Environment.NewLine, log.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct());
                    RuntimeStatus = "Pronto • stats do jogador capturados diretamente pela rotina CT";
                    LastError = string.Empty;
                    return true;
                }
                log.Add(diagnostic);
            }
            await Task.Delay(50, cancellationToken);
        }

        log.Add(TryReadCapturedStats(out var finalStats)
            ? $"Capture Stats/RDI final: 0x{finalStats.ToInt64():X}"
            : "Capture Stats/RDI final: leitura inválida");

        if (TryReadCapturedPlayer(out var player, out var component))
        {
            log.Add($"Capture cplayer final: 0x{player.ToInt64():X}");
            log.Add($"Capture csplayer final: 0x{component.ToInt64():X}");
        }

        DiagnosticReport = string.Join(Environment.NewLine, log.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct());
        RuntimeStatus = "Hooks instalados, mas a rotina de stats ainda não capturou o bloco do jogador. Movimente-se/use vigor e copie o diagnóstico.";
        LastError = RuntimeStatus;
        return false;
    }

    private bool InstallStatHook(out string diagnostic)
    {
        diagnostic = string.Empty;
        if (_memory is null || !_memory.IsAttached) return false;
        if (_statHookInstalled)
        {
            diagnostic = BuildStatHeader();
            return true;
        }

        var log = new List<string>
        {
            "Stat Capture v0.3.3 - CT StaminaInj",
            "AOB CT: 48 89 5F 08 48 8B 5C 24 48",
            "Filtro CT: [RDI+5A0] == 19",
            "Ação: somente captura RDI; não escreve 999999999"
        };

        try
        {
            var match = _memory.FindPatternInMainModule(StatWriteAob);
            if (!match.HasValue)
            {
                log.Add("AOB StaminaInj: não encontrado");
                diagnostic = string.Join(Environment.NewLine, log);
                return false;
            }

            _statHookAddress = match.Value;
            _statOriginalBytes = _memory.ReadBytes(_statHookAddress, StatHookLength);
            var expected = new byte[] { 0x48, 0x89, 0x5F, 0x08, 0x48, 0x8B, 0x5C, 0x24, 0x48 };
            if (!_statOriginalBytes.SequenceEqual(expected))
            {
                log.Add($"AOB StaminaInj: bytes inesperados {Convert.ToHexString(_statOriginalBytes)}");
                diagnostic = string.Join(Environment.NewLine, log);
                return false;
            }

            _statCave = _memory.AllocateExecutableNear(_statHookAddress, StatCaveSize);
            _statSlot = _statCave + StatSlotOffset;
            _memory.Write<long>(_statSlot, 0);
            _memory.WriteBytes(_statCave, BuildStatCaptureCave(_statCave, _statSlot, _statHookAddress, _statOriginalBytes));

            var patch = new byte[StatHookLength];
            patch[0] = 0xE9;
            BinaryPrimitives.WriteInt32LittleEndian(patch.AsSpan(1, 4), CheckedRel32(_statHookAddress + 5, _statCave));
            Array.Fill(patch, (byte)0x90, 5, StatHookLength - 5);
            _memory.WriteProtectedBytes(_statHookAddress, patch);
            _statHookInstalled = true;

            log.Add($"AOB StaminaInj: 0x{_statHookAddress.ToInt64():X} (RVA 0x{_statHookAddress.ToInt64() - _memory.MainModuleBase.ToInt64():X})");
            log.Add($"Stat cave: 0x{_statCave.ToInt64():X}");
            log.Add($"Capture Stats/RDI: 0x{_statSlot.ToInt64():X}");
            diagnostic = string.Join(Environment.NewLine, log);
            return true;
        }
        catch (Exception ex)
        {
            log.Add($"Falha no hook direto de stats: {ex.GetType().Name} - {ex.Message}");
            diagnostic = string.Join(Environment.NewLine, log);
            SafeRemoveStatHook();
            return false;
        }
    }

    private static byte[] BuildStatCaptureCave(nint cave, nint slot, nint hook, byte[] originalBytes)
    {
        var code = new List<byte>(96);
        code.Add(0x9C); // pushfq
        code.AddRange(new byte[] { 0x81, 0xBF, 0xA0, 0x05, 0x00, 0x00, 0x13, 0x00, 0x00, 0x00 }); // cmp [rdi+5A0],19
        code.AddRange(new byte[] { 0x75, 0x0F }); // jne popfq
        code.Add(0x50); // push rax
        code.AddRange(new byte[] { 0x48, 0xB8 });
        code.AddRange(BitConverter.GetBytes(slot.ToInt64()));
        code.AddRange(new byte[] { 0x48, 0x89, 0x38 }); // mov [rax],rdi
        code.Add(0x58); // pop rax
        code.Add(0x9D); // popfq
        code.AddRange(originalBytes);
        var jump = cave + code.Count;
        code.Add(0xE9);
        code.AddRange(BitConverter.GetBytes(CheckedRel32(jump + 5, hook + StatHookLength)));
        return code.ToArray();
    }

    private bool TryReadCapturedStats(out nint stats)
    {
        stats = 0;
        if (_memory is null || !_memory.IsAttached || !_statHookInstalled || _statSlot == 0) return false;
        if (!_memory.TryRead<long>(_statSlot, out var raw)) return false;
        stats = (nint)raw;
        return stats == 0 || (ProcessMemory.IsLikelyPointer(stats) && _memory.IsReadable(stats));
    }

    private bool TryResolveDirectStats(nint stats, out RuntimeState runtime, out string diagnostic)
    {
        runtime = new RuntimeState();
        if (_memory is null || stats == 0 || !_memory.IsReadable(stats))
        {
            diagnostic = "Stats/RDI capturado é inválido.";
            return false;
        }

        var log = new List<string> { $"Captured Stats/RDI: 0x{stats.ToInt64():X}" };
        if (!_memory.TryRead<int>(stats + HealthIdOffset, out var hpId)
            || !_memory.TryRead<int>(stats + StaminaIdOffset, out var staminaId)
            || !_memory.TryRead<int>(stats + SpiritIdOffset, out var spiritId))
        {
            diagnostic = string.Join(Environment.NewLine, log.Append("Não foi possível ler os IDs do bloco direto."));
            return false;
        }

        log.Add($"Stat IDs diretos: HP={hpId}, Vigor={staminaId}, Espírito={spiritId}");
        if (hpId != ExpectedHealthId || spiritId != ExpectedSpiritId)
        {
            diagnostic = string.Join(Environment.NewLine, log.Append($"Validação direta falhou: esperado HP={ExpectedHealthId} e Espírito={ExpectedSpiritId}."));
            return false;
        }

        if (!TryReadStatPair(stats + HealthCurrentOffset, stats + HealthMaxOffset, out var hp)
            || !TryReadStatPair(stats + StaminaCurrentOffset, stats + StaminaMaxOffset, out var stamina)
            || !TryReadStatPair(stats + SpiritCurrentOffset, stats + SpiritMaxOffset, out var spirit))
        {
            diagnostic = string.Join(Environment.NewLine, log.Append("Current/Maximum do bloco direto não passaram pela validação."));
            return false;
        }

        log.Add($"HP={hp.Current}/{hp.Maximum}");
        log.Add($"Vigor={stamina.Current}/{stamina.Maximum}");
        log.Add($"Espírito={spirit.Current}/{spirit.Maximum}");

        runtime = new RuntimeState
        {
            IsResolved = true,
            StatsBase = stats,
            HealthCurrent = stats + HealthCurrentOffset,
            HealthMax = stats + HealthMaxOffset,
            StaminaCurrent = stats + StaminaCurrentOffset,
            StaminaMax = stats + StaminaMaxOffset,
            SpiritCurrent = stats + SpiritCurrentOffset,
            SpiritMax = stats + SpiritMaxOffset
        };
        diagnostic = string.Join(Environment.NewLine, log);
        return true;
    }

    private bool InstallPlayerHook(out string diagnostic)
    {
        diagnostic = string.Empty;
        if (_memory is null || !_memory.IsAttached) return false;
        if (_playerHookInstalled)
        {
            diagnostic = BuildPlayerHeader();
            return true;
        }

        var log = new List<string> { "Player Capture preservado" };
        try
        {
            nint? match = _memory.FindPatternInMainModule(CheatTableCurrentPlayerAob);
            if (match.HasValue) _playerSignature = "CT-original-wildcard";
            else
            {
                match = _memory.FindPatternInMainModule(CurrentPlayerAob);
                if (match.HasValue) _playerSignature = "fallback-direct-long";
                else
                {
                    match = _memory.FindPatternInMainModule(CurrentPlayerShortAob);
                    if (match.HasValue) _playerSignature = "fallback-direct-short";
                    else
                    {
                        match = _memory.FindPatternInMainModule(CurrentPlayerLegacyShortAob);
                        if (match.HasValue) _playerSignature = "fallback-legacy-short";
                    }
                }
            }

            if (!match.HasValue)
            {
                log.Add("AOB getcurrentplayer: não encontrado (não bloqueia stats direto)");
                diagnostic = string.Join(Environment.NewLine, log);
                return false;
            }

            _playerHookAddress = match.Value;
            _playerOriginalBytes = _memory.ReadBytes(_playerHookAddress, PlayerHookLength);
            var a0 = new byte[] { 0x48, 0x8B, 0x43, 0x68, 0x48, 0x8B, 0x88, 0xA0, 0x01, 0x00, 0x00 };
            var b0 = new byte[] { 0x48, 0x8B, 0x43, 0x68, 0x48, 0x8B, 0x88, 0xB0, 0x01, 0x00, 0x00 };
            if (!_playerOriginalBytes.SequenceEqual(a0) && !_playerOriginalBytes.SequenceEqual(b0))
            {
                log.Add("Player hook: bytes inesperados");
                diagnostic = string.Join(Environment.NewLine, log);
                return false;
            }

            _playerCave = _memory.AllocateExecutableNear(_playerHookAddress, PlayerCaveSize);
            _playerSlot = _playerCave + PlayerSlotOffset;
            _componentSlot = _playerCave + ComponentSlotOffset;
            _memory.Write<long>(_playerSlot, 0);
            _memory.Write<long>(_componentSlot, 0);
            _memory.WriteBytes(_playerCave, BuildPlayerCaptureCave(_playerCave, _playerSlot, _componentSlot, _playerHookAddress, _playerOriginalBytes));

            var patch = new byte[PlayerHookLength];
            patch[0] = 0xE9;
            BinaryPrimitives.WriteInt32LittleEndian(patch.AsSpan(1, 4), CheckedRel32(_playerHookAddress + 5, _playerCave));
            Array.Fill(patch, (byte)0x90, 5, PlayerHookLength - 5);
            _memory.WriteProtectedBytes(_playerHookAddress, patch);
            _playerHookInstalled = true;

            log.Add($"Assinatura: {_playerSignature}");
            log.Add($"AOB getcurrentplayer: 0x{_playerHookAddress.ToInt64():X}");
            diagnostic = string.Join(Environment.NewLine, log);
            return true;
        }
        catch (Exception ex)
        {
            log.Add($"Falha no player hook preservado: {ex.GetType().Name} - {ex.Message}");
            diagnostic = string.Join(Environment.NewLine, log);
            SafeRemovePlayerHook();
            return false;
        }
    }

    private static byte[] BuildPlayerCaptureCave(nint cave, nint playerSlot, nint componentSlot, nint hook, byte[] originalBytes)
    {
        var code = new List<byte>(80);
        code.AddRange(originalBytes);
        code.Add(0x52);
        code.AddRange(new byte[] { 0x48, 0xBA });
        code.AddRange(BitConverter.GetBytes(playerSlot.ToInt64()));
        code.AddRange(new byte[] { 0x48, 0x89, 0x1A });
        code.AddRange(new byte[] { 0x48, 0xBA });
        code.AddRange(BitConverter.GetBytes(componentSlot.ToInt64()));
        code.AddRange(new byte[] { 0x48, 0x89, 0x02 });
        code.Add(0x5A);
        var jump = cave + code.Count;
        code.Add(0xE9);
        code.AddRange(BitConverter.GetBytes(CheckedRel32(jump + 5, hook + PlayerHookLength)));
        return code.ToArray();
    }

    private bool TryReadCapturedPlayer(out nint player, out nint component)
    {
        player = 0;
        component = 0;
        if (_memory is null || !_memory.IsAttached || !_playerHookInstalled || _playerSlot == 0 || _componentSlot == 0) return false;
        if (!_memory.TryRead<long>(_playerSlot, out var rawPlayer) || !_memory.TryRead<long>(_componentSlot, out var rawComponent)) return false;
        player = (nint)rawPlayer;
        component = (nint)rawComponent;
        return (player == 0 || ProcessMemory.IsLikelyPointer(player)) && (component == 0 || ProcessMemory.IsLikelyPointer(component));
    }

    private void FillCapturedPlayer(RuntimeState runtime)
    {
        if (TryReadCapturedPlayer(out var player, out var component))
        {
            runtime.CapturedPlayer = player;
            runtime.PlayerComponent = component;
        }
    }

    private bool EnsureRuntime()
    {
        if (_runtime.IsResolved && ValidateRuntime()) return true;
        if (TryReadCapturedStats(out var stats) && stats != 0 && TryResolveDirectStats(stats, out var runtime, out var diagnostic))
        {
            FillCapturedPlayer(runtime);
            _runtime = runtime;
            DiagnosticReport = BuildCombinedHeader() + Environment.NewLine + diagnostic;
            RuntimeStatus = "Pronto • stats do jogador capturados diretamente pela rotina CT";
            LastError = string.Empty;
            return true;
        }
        RuntimeStatus = "Aguardando a rotina de stats capturar o jogador...";
        LastError = RuntimeStatus;
        return false;
    }

    private bool ValidateRuntime()
    {
        if (!_runtime.IsResolved || _memory is null) return false;
        if (!_memory.TryRead<int>(_runtime.StatsBase + HealthIdOffset, out var hpId)
            || !_memory.TryRead<int>(_runtime.StatsBase + SpiritIdOffset, out var spiritId)) return false;
        if (hpId != ExpectedHealthId || spiritId != ExpectedSpiritId) return false;
        return TryReadStatPair(_runtime.HealthCurrent, _runtime.HealthMax, out _)
            && TryReadStatPair(_runtime.StaminaCurrent, _runtime.StaminaMax, out _)
            && TryReadStatPair(_runtime.SpiritCurrent, _runtime.SpiritMax, out _);
    }

    private bool TryReadStatPair(nint currentAddress, nint maxAddress, out StatPair stat)
    {
        stat = default;
        if (_memory is null || !_memory.TryRead<uint>(currentAddress, out var current) || !_memory.TryRead<uint>(maxAddress, out var maximum)) return false;
        if (maximum == 0 || maximum > 1_000_000_000U || (ulong)current > (ulong)maximum * 20UL) return false;
        stat = new StatPair(current, maximum);
        return true;
    }

    private void RestoreCurrentToMaximum(nint currentAddress, nint maxAddress, string label)
    {
        if (!TryReadStatPair(currentAddress, maxAddress, out var stat))
        {
            _runtime = new RuntimeState();
            RuntimeStatus = $"{label}: estrutura mudou; aguardando recaptura automática...";
            return;
        }
        if (stat.Current != stat.Maximum) _memory!.Write(currentAddress, stat.Maximum);
    }

    private string BuildCombinedHeader() => BuildPlayerHeader() + Environment.NewLine + BuildStatHeader();

    private string BuildPlayerHeader() => string.Join(Environment.NewLine,
        "Player Capture preservado",
        $"Assinatura: {_playerSignature}",
        $"AOB getcurrentplayer: {(_playerHookAddress == 0 ? "não resolvido" : $"0x{_playerHookAddress.ToInt64():X}")}",
        $"Capture cplayer: {(_playerSlot == 0 ? "não alocado" : $"0x{_playerSlot.ToInt64():X}")}",
        $"Capture csplayer: {(_componentSlot == 0 ? "não alocado" : $"0x{_componentSlot.ToInt64():X}")}");

    private string BuildStatHeader()
    {
        if (_memory is null) return "Stat hook indisponível.";
        return string.Join(Environment.NewLine,
            "Diagnóstico v0.3.3 - CT Direct Stat Capture",
            $"Módulo: 0x{_memory.MainModuleBase.ToInt64():X} / 0x{_memory.MainModuleSize:X} bytes",
            $"AOB StaminaInj: {(_statHookAddress == 0 ? "não resolvido" : $"0x{_statHookAddress.ToInt64():X}")}",
            $"Stat cave: {(_statCave == 0 ? "não alocado" : $"0x{_statCave.ToInt64():X}")}",
            $"Capture Stats/RDI: {(_statSlot == 0 ? "não alocado" : $"0x{_statSlot.ToInt64():X}")}",
            "Filtro: [RDI+5A0] == 19",
            "Offsets: HP 08/18 | Vigor 518/528 | Espírito 5A8/5B8");
    }

    private static int CheckedRel32(nint instructionEnd, nint target)
    {
        var delta = target.ToInt64() - instructionEnd.ToInt64();
        if (delta < int.MinValue || delta > int.MaxValue) throw new InvalidOperationException("O salto relativo ficou fora do alcance de 32 bits.");
        return (int)delta;
    }

    public void Detach()
    {
        SafeRemoveStatHook();
        SafeRemovePlayerHook();
        _memory = null;
        _runtime = new RuntimeState();
        _health = _stamina = _spirit = false;
        RuntimeStatus = "Aguardando o jogo";
    }

    private void SafeRemoveStatHook()
    {
        if (_memory is not null && _memory.IsAttached)
        {
            try
            {
                if (_statHookInstalled && _statHookAddress != 0 && _statOriginalBytes is { Length: StatHookLength }
                    && _memory.TryReadBytes(_statHookAddress, 5, out var current) && current[0] == 0xE9)
                {
                    var rel = BinaryPrimitives.ReadInt32LittleEndian(current.AsSpan(1, 4));
                    if (_statHookAddress.ToInt64() + 5L + rel == _statCave.ToInt64()) _memory.WriteProtectedBytes(_statHookAddress, _statOriginalBytes);
                }
            }
            catch { }
            try { if (_statCave != 0) _memory.FreeRemote(_statCave); } catch { }
        }
        _statHookInstalled = false;
        _statHookAddress = _statCave = _statSlot = 0;
        _statOriginalBytes = null;
    }

    private void SafeRemovePlayerHook()
    {
        if (_memory is not null && _memory.IsAttached)
        {
            try
            {
                if (_playerHookInstalled && _playerHookAddress != 0 && _playerOriginalBytes is { Length: PlayerHookLength }
                    && _memory.TryReadBytes(_playerHookAddress, 5, out var current) && current[0] == 0xE9)
                {
                    var rel = BinaryPrimitives.ReadInt32LittleEndian(current.AsSpan(1, 4));
                    if (_playerHookAddress.ToInt64() + 5L + rel == _playerCave.ToInt64()) _memory.WriteProtectedBytes(_playerHookAddress, _playerOriginalBytes);
                }
            }
            catch { }
            try { if (_playerCave != 0) _memory.FreeRemote(_playerCave); } catch { }
        }
        _playerHookInstalled = false;
        _playerHookAddress = _playerCave = _playerSlot = _componentSlot = 0;
        _playerOriginalBytes = null;
        _playerSignature = "não resolvida";
    }

    public void Dispose() => Detach();

    private readonly record struct StatPair(uint Current, uint Maximum);

    private sealed class RuntimeState
    {
        public bool IsResolved { get; set; }
        public nint CapturedPlayer { get; set; }
        public nint PlayerComponent { get; set; }
        public nint StatsBase { get; set; }
        public nint HealthCurrent { get; set; }
        public nint HealthMax { get; set; }
        public nint StaminaCurrent { get; set; }
        public nint StaminaMax { get; set; }
        public nint SpiritCurrent { get; set; }
        public nint SpiritMax { get; set; }
    }
}
