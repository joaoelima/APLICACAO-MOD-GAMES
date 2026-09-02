using System.Buffers.Binary;
using GameTrainer.Core.Memory;
using GameTrainer.Core.Models;
using GameTrainer.Core.Modules;

namespace GameTrainer.Modules.CrimsonDesert;

public sealed class CrimsonDesertModule : IGameModule, IDisposable
{
    private const int HookLength = 11;
    private const int CaveSize = 0x100;
    private const int CapturePlayerSlotOffset = 0x80;
    private const int CaptureComponentSlotOffset = 0x88;

    private const int StatHookLength = 9;
    private const int StatCaveSize = 0x100;
    private const int StatCaptureSlotOffset = 0x80;

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

    // AOB usada pela própria CT no script "Max Health + Stamina + Spirit (Godmode)".
    private const string StatWriteAob = "48 89 5F 08 48 8B 5C 24 48";

    private ProcessMemory? _memory;
    private RuntimeState _runtime = new();

    private nint _hookAddress;
    private nint _codeCave;
    private nint _capturePlayerSlot;
    private nint _captureComponentSlot;
    private byte[]? _originalHookBytes;
    private bool _hookInstalled;
    private string _hookSignature = "não resolvida";

    private nint _statHookAddress;
    private nint _statCodeCave;
    private nint _statCaptureSlot;
    private byte[]? _statOriginalHookBytes;
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

        if (InstallCurrentPlayerHook(out var playerHookLog))
            logs.Add(playerHookLog);
        else
            logs.Add(playerHookLog);

        if (!InstallStatWriteHook(out var statHookLog))
        {
            logs.Add(statHookLog);
            DiagnosticReport = string.Join(Environment.NewLine, logs.Where(x => !string.IsNullOrWhiteSpace(x)));
            RuntimeStatus = "Jogo conectado, mas o hook direto de stats da CT não pôde ser instalado.";
            LastError = RuntimeStatus;
            return;
        }

        logs.Add(statHookLog);
        await WaitForPlayerAndResolveAsync(string.Join(Environment.NewLine, logs), cancellationToken);
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

        if (!_hookInstalled)
            InstallCurrentPlayerHook(out var playerLog);
        else
            playerLog = BuildHookHeader();
        logs.Add(playerLog);

        if (!_statHookInstalled)
        {
            if (!InstallStatWriteHook(out var statLog))
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
            logs.Add(BuildStatHookHeader());
        }

        return await WaitForPlayerAndResolveAsync(string.Join(Environment.NewLine, logs), cancellationToken);
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
            && stats != 0
            && stats != _runtime.StatsBase
            && TryResolveDirectStats(stats, out var runtime, out var directDiagnostic))
        {
            if (TryReadCapturedPointers(out var player, out var component))
            {
                runtime.CapturedPlayer = player;
                runtime.PlayerComponent = component;
            }

            _runtime = runtime;
            DiagnosticReport = BuildCombinedHeader() + Environment.NewLine + directDiagnostic;
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

    private async Task<bool> WaitForPlayerAndResolveAsync(string hookLog, CancellationToken cancellationToken)
    {
        var log = new List<string> { hookLog };
        RuntimeStatus = "Hooks instalados. Aguardando a rotina de escrita de stats identificar o jogador...";

        for (var attempt = 0; attempt < 120; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (TryReadCapturedStats(out var stats) && stats != 0)
            {
                if (TryResolveDirectStats(stats, out var runtime, out var directLog))
                {
                    if (TryReadCapturedPointers(out var player, out var component))
                    {
                        runtime.CapturedPlayer = player;
                        runtime.PlayerComponent = component;
                    }

                    _runtime = runtime;
                    log.Add(directLog);
                    DiagnosticReport = string.Join(Environment.NewLine, log.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct());
                    RuntimeStatus = "Pronto • stats do jogador capturados diretamente pela rotina CT";
                    LastError = string.Empty;
                    return true;
                }

                log.Add(directLog);
            }

            await Task.Delay(50, cancellationToken);
        }

        if (TryReadCapturedStats(out var finalStats))
            log.Add($"Capture Stats/RDI final: 0x{finalStats.ToInt64():X}");
        else
            log.Add("Capture Stats/RDI final: leitura inválida");

        if (TryReadCapturedPointers(out var finalPlayer, out var finalComponent))
        {
            log.Add($"Capture cplayer final: 0x{finalPlayer.ToInt64():X}");
            log.Add($"Capture csplayer final: 0x{finalComponent.ToInt64():X}");
        }

        DiagnosticReport = string.Join(Environment.NewLine, log.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct());
        RuntimeStatus = "Hooks instalados, mas a rotina de stats ainda não capturou o bloco do jogador. Movimente-se/use vigor e copie o diagnóstico.";
        LastError = RuntimeStatus;
        return false;
    }

    private bool InstallStatWriteHook(out string diagnostic)
    {
        diagnostic = string.Empty;
        if (_memory is null || !_memory.IsAttached)
            return false;

        if (_statHookInstalled)
        {
            diagnostic = BuildStatHookHeader();
            return true;
        }

        var log = new List<string>
        {
            "Stat Capture v0.3.3 - CT StaminaInj",
            "AOB CT: 48 89 5F 08 48 8B 5C 24 48",
            "Filtro CT: [RDI+5A0] == 19",
            "Ação do trainer: somente captura RDI; não replica os writes 999999999 da CT"
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
            _statOriginalHookBytes = _memory.ReadBytes(_statHookAddress, StatHookLength);
            var expected = new byte[] { 0x48, 0x89, 0x5F, 0x08, 0x48, 0x8B, 0x5C, 0x24, 0x48 };
            if (!_statOriginalHookBytes.SequenceEqual(expected))
            {
                log.Add($"AOB StaminaInj: 0x{_statHookAddress.ToInt64():X}, bytes inesperados: {Convert.ToHexString(_statOriginalHookBytes)}");
                diagnostic = string.Join(Environment.NewLine, log);
                return false;
            }

            _statCodeCave = _memory.AllocateExecutableNear(_statHookAddress, StatCaveSize);
            _statCaptureSlot = _statCodeCave + StatCaptureSlotOffset;
            _memory.Write<long>(_statCaptureSlot, 0);

            var caveCode = BuildStatCaptureCave(
                _statCodeCave,
                _statCaptureSlot,
                _statHookAddress,
                _statOriginalHookBytes);
            _memory.WriteBytes(_statCodeCave, caveCode);

            var patch = new byte[StatHookLength];
            patch[0] = 0xE9;
            BinaryPrimitives.WriteInt32LittleEndian(patch.AsSpan(1, 4), CheckedRel32(_statHookAddress + 5, _statCodeCave));
            Array.Fill(patch, (byte)0x90, 5, StatHookLength - 5);
            _memory.WriteProtectedBytes(_statHookAddress, patch);
            _statHookInstalled = true;

            log.Add($"AOB StaminaInj: 0x{_statHookAddress.ToInt64():X} (RVA 0x{_statHookAddress.ToInt64() - _memory.MainModuleBase.ToInt64():X})");
            log.Add($"Stat cave: 0x{_statCodeCave.ToInt64():X}");
            log.Add($"Capture Stats/RDI: 0x{_statCaptureSlot.ToInt64():X}");
            log.Add("Hook direto de stats: instalado");
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

    private static byte[] BuildStatCaptureCave(
        nint cave,
        nint captureSlot,
        nint hookAddress,
        byte[] originalBytes)
    {
        var code = new List<byte>(96);

        code.Add(0x9C); // pushfq
        code.AddRange(new byte[] { 0x81, 0xBF, 0xA0, 0x05, 0x00, 0x00, 0x13, 0x00, 0x00, 0x00 }); // cmp dword ptr [rdi+5A0],19
        code.AddRange(new byte[] { 0x75, 0x0F }); // jne +15 (pula captura, vai para popfq)
        code.Add(0x50); // push rax
        code.AddRange(new byte[] { 0x48, 0xB8 }); // mov rax,imm64
        code.AddRange(BitConverter.GetBytes(captureSlot.ToInt64()));
        code.AddRange(new byte[] { 0x48, 0x89, 0x38 }); // mov [rax],rdi
        code.Add(0x58); // pop rax
        code.Add(0x9D); // popfq

        code.AddRange(originalBytes); // mov [rdi+08],rbx ; mov rbx,[rsp+48]

        var jumpInstruction = cave + code.Count;
        code.Add(0xE9);
        code.AddRange(BitConverter.GetBytes(CheckedRel32(jumpInstruction + 5, hookAddress + StatHookLength)));
        return code.ToArray();
    }

    private bool TryReadCapturedStats(out nint stats)
    {
        stats = 0;
        if (_memory is null || !_memory.IsAttached || !_statHookInstalled || _statCaptureSlot == 0)
            return false;

        if (!_memory.TryRead<long>(_statCaptureSlot, out var raw))
            return false;

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

        if (!_memory.TryRead<int>(stats + HealthIdOffset, out var healthId)
            || !_memory.TryRead<int>(stats + StaminaIdOffset, out var staminaId)
            || !_memory.TryRead<int>(stats + SpiritIdOffset, out var spiritId))
        {
            diagnostic = string.Join(Environment.NewLine, log.Append("Não foi possível ler os IDs do bloco direto."));
            return false;
        }

        log.Add($"Stat IDs diretos: HP={healthId}, Vigor={staminaId}, Espírito={spiritId}");
        if (healthId != ExpectedHealthId || spiritId != ExpectedSpiritId)
        {
            diagnostic = string.Join(Environment.NewLine, log.Append(
                $"Validação direta falhou: esperado HP={ExpectedHealthId} e Espírito={ExpectedSpiritId}. Vigor é apenas diagnóstico."));
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

    private bool InstallCurrentPlayerHook(out string diagnostic)
    {
        diagnostic = string.Empty;
        if (_memory is null || !_memory.IsAttached)
            return false;

        if (_hookInstalled)
        {
            diagnostic = BuildHookHeader();
            return true;
        }

        var log = new List<string>
        {
            "Player Capture v0.3.2 - preservado",
            $"Módulo: 0x{_memory.MainModuleBase.ToInt64():X} / 0x{_memory.MainModuleSize:X} bytes"
        };

        try
        {
            nint? match = _memory.FindPatternInMainModule(CheatTableCurrentPlayerAob);
            if (match.HasValue)
                _hookSignature = "CT-original-wildcard";
            else
            {
                match = _memory.FindPatternInMainModule(CurrentPlayerAob);
                if (match.HasValue)
                    _hookSignature = "fallback-direct-long";
                else
                {
                    match = _memory.FindPatternInMainModule(CurrentPlayerShortAob);
                    if (match.HasValue)
                        _hookSignature = "fallback-direct-short";
                    else
                    {
                        match = _memory.FindPatternInMainModule(CurrentPlayerLegacyShortAob);
                        if (match.HasValue)
                            _hookSignature = "fallback-legacy-short";
                    }
                }
            }

            if (!match.HasValue)
            {
                log.Add("AOB getcurrentplayer: não encontrado (não bloqueia o hook direto de stats)");
                diagnostic = string.Join(Environment.NewLine, log);
                return false;
            }

            _hookAddress = match.Value;
            _originalHookBytes = _memory.ReadBytes(_hookAddress, HookLength);
            var expectedPrefixA0 = new byte[] { 0x48, 0x8B, 0x43, 0x68, 0x48, 0x8B, 0x88, 0xA0, 0x01, 0x00, 0x00 };
            var expectedPrefixB0 = new byte[] { 0x48, 0x8B, 0x43, 0x68, 0x48, 0x8B, 0x88, 0xB0, 0x01, 0x00, 0x00 };
            if (!_originalHookBytes.SequenceEqual(expectedPrefixA0) && !_originalHookBytes.SequenceEqual(expectedPrefixB0))
            {
                log.Add($"AOB getcurrentplayer: bytes inesperados: {Convert.ToHexString(_originalHookBytes)}");
                diagnostic = string.Join(Environment.NewLine, log);
                return false;
            }

            _codeCave = _memory.AllocateExecutableNear(_hookAddress, CaveSize);
            _capturePlayerSlot = _codeCave + CapturePlayerSlotOffset;
            _captureComponentSlot = _codeCave + CaptureComponentSlotOffset;
            _memory.Write<long>(_capturePlayerSlot, 0);
            _memory.Write<long>(_captureComponentSlot, 0);

            var caveCode = BuildCaptureCave(_codeCave, _capturePlayerSlot, _captureComponentSlot, _hookAddress, _originalHookBytes);
            _memory.WriteBytes(_codeCave, caveCode);

            var patch = new byte[HookLength];
            patch[0] = 0xE9;
            BinaryPrimitives.WriteInt32LittleEndian(patch.AsSpan(1, 4), CheckedRel32(_hookAddress + 5, _codeCave));
            Array.Fill(patch, (byte)0x90, 5, HookLength - 5);
            _memory.WriteProtectedBytes(_hookAddress, patch);
            _hookInstalled = true;

            log.Add($"Assinatura: {_hookSignature}");
            log.Add($"AOB getcurrentplayer: 0x{_hookAddress.ToInt64():X}");
            log.Add($"Capture cplayer: 0x{_capturePlayerSlot.ToInt64():X}");
            log.Add($"Capture csplayer: 0x{_captureComponentSlot.ToInt64():X}");
            diagnostic = string.Join(Environment.NewLine, log);
            return true;
        }
        catch (Exception ex)
        {
            log.Add($"Falha no player hook preservado: {ex.GetType().Name} - {ex.Message}");
            diagnostic = string.Join(Environment.NewLine, log);
            SafeRemoveHook();
            return false;
        }
    }

    private static byte[] BuildCaptureCave(nint cave, nint playerSlot, nint componentSlot, nint hookAddress, byte[] originalBytes)
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
        var jumpInstruction = cave + code.Count;
        code.Add(0xE9);
        code.AddRange(BitConverter.GetBytes(CheckedRel32(jumpInstruction + 5, hookAddress + HookLength)));
        return code.ToArray();
    }

    private bool TryReadCapturedPointers(out nint capturedPlayer, out nint capturedComponent)
    {
        capturedPlayer = 0;
        capturedComponent = 0;
        if (_memory is null || !_memory.IsAttached || !_hookInstalled || _capturePlayerSlot == 0 || _captureComponentSlot == 0)
            return false;

        if (!_memory.TryRead<long>(_capturePlayerSlot, out var rawPlayer)
            || !_memory.TryRead<long>(_captureComponentSlot, out var rawComponent))
            return false;

        capturedPlayer = (nint)rawPlayer;
        capturedComponent = (nint)rawComponent;
        return (capturedPlayer == 0 || ProcessMemory.IsLikelyPointer(capturedPlayer))
               && (capturedComponent == 0 || ProcessMemory.IsLikelyPointer(capturedComponent));
    }

    private bool EnsureRuntime()
    {
        if (_runtime.IsResolved && ValidateRuntime())
            return true;

        if (TryReadCapturedStats(out var stats)
            && stats != 0
            && TryResolveDirectStats(stats, out var runtime, out var diagnostic))
        {
            if (TryReadCapturedPointers(out var player, out var component))
            {
                runtime.CapturedPlayer = player;
                runtime.PlayerComponent = component;
            }

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
        if (!_runtime.IsResolved || _memory is null)
            return false;

        if (!_memory.TryRead<int>(_runtime.StatsBase + HealthIdOffset, out var hpId)
            || !_memory.TryRead<int>(_runtime.StatsBase + SpiritIdOffset, out var spiritId))
            return false;

        if (hpId != ExpectedHealthId || spiritId != ExpectedSpiritId)
            return false;

        return TryReadStatPair(_runtime.HealthCurrent, _runtime.HealthMax, out _)
               && TryReadStatPair(_runtime.StaminaCurrent, _runtime.StaminaMax, out _)
               && TryReadStatPair(_runtime.SpiritCurrent, _runtime.SpiritMax, out _);
    }

    private bool TryReadStatPair(nint currentAddress, nint maxAddress, out StatPair stat)
    {
        stat = default;
        if (_memory is null
            || !_memory.TryRead<uint>(currentAddress, out var current)
            || !_memory.TryRead<uint>(maxAddress, out var maximum))
            return false;

        if (maximum == 0 || maximum > 1_000_000_000U || (ulong)current > (ulong)maximum * 20UL)
            return false;

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

        if (stat.Current != stat.Maximum)
            _memory!.Write(currentAddress, stat.Maximum);
    }

    private string BuildCombinedHeader()
        => BuildHookHeader() + Environment.NewLine + BuildStatHookHeader();

    private string BuildHookHeader()
    {
        if (_memory is null)
            return "Player hook indisponível.";

        return string.Join(Environment.NewLine,
            "Player Capture preservado",
            $"Assinatura: {_hookSignature}",
            $"AOB getcurrentplayer: {(_hookAddress == 0 ? "não resolvido" : $"0x{_hookAddress.ToInt64():X}")}",
            $"Capture cplayer: {(_capturePlayerSlot == 0 ? "não alocado" : $"0x{_capturePlayerSlot.ToInt64():X}")}",
            $"Capture csplayer: {(_captureComponentSlot == 0 ? "não alocado" : $"0x{_captureComponentSlot.ToInt64():X}")}");
    }

    private string BuildStatHookHeader()
    {
        if (_memory is null)
            return "Stat hook indisponível.";

        return string.Join(Environment.NewLine,
            "Diagnóstico v0.3.3 - CT Direct Stat Capture",
            $"Módulo: 0x{_memory.MainModuleBase.ToInt64():X} / 0x{_memory.MainModuleSize:X} bytes",
            $"AOB StaminaInj: {(_statHookAddress == 0 ? "não resolvido" : $"0x{_statHookAddress.ToInt64():X}")}",
            $"Stat cave: {(_statCodeCave == 0 ? "não alocado" : $"0x{_statCodeCave.ToInt64():X}")}",
            $"Capture Stats/RDI: {(_statCaptureSlot == 0 ? "não alocado" : $"0x{_statCaptureSlot.ToInt64():X}")}",
            "Filtro: [RDI+5A0] == 19",
            "Offsets: HP 08/18 | Vigor 518/528 | Espírito 5A8/5B8");
    }

    private static int CheckedRel32(nint instructionEnd, nint target)
    {
        var delta = target.ToInt64() - instructionEnd.ToInt64();
        if (delta < int.MinValue || delta > int.MaxValue)
            throw new InvalidOperationException("O salto relativo ficou fora do alcance de 32 bits.");
        return (int)delta;
    }

    public void Detach()
    {
        SafeRemoveStatHook();
        SafeRemoveHook();
        _memory = null;
        _runtime = new RuntimeState();
        _health = false;
        _stamina = false;
        _spirit = false;
        RuntimeStatus = "Aguardando o jogo";
    }

    private void SafeRemoveStatHook()
    {
        if (_memory is not null && _memory.IsAttached)
        {
            try
            {
                if (_statHookInstalled && _statHookAddress != 0 && _statOriginalHookBytes is { Length: StatHookLength })
                {
                    if (_memory.TryReadBytes(_statHookAddress, 5, out var current)
                        && current.Length == 5 && current[0] == 0xE9)
                    {
                        var rel = BinaryPrimitives.ReadInt32LittleEndian(current.AsSpan(1, 4));
                        var destination = _statHookAddress.ToInt64() + 5L + rel;
                        if (destination == _statCodeCave.ToInt64())
                            _memory.WriteProtectedBytes(_statHookAddress, _statOriginalHookBytes);
                    }
                }
            }
            catch { }

            try
            {
                if (_statCodeCave != 0)
                    _memory.FreeRemote(_statCodeCave);
            }
            catch { }
        }

        _statHookInstalled = false;
        _statHookAddress = 0;
        _statCodeCave = 0;
        _statCaptureSlot = 0;
        _statOriginalHookBytes = null;
    }

    private void SafeRemoveHook()
    {
        if (_memory is not null && _memory.IsAttached)
        {
            try
            {
                if (_hookInstalled && _hookAddress != 0 && _originalHookBytes is { Length: HookLength })
                {
                    if (_memory.TryReadBytes(_hookAddress, 5, out var current)
                        && current.Length == 5 && current[0] == 0xE9)
                    {
                        var rel = BinaryPrimitives.ReadInt32LittleEndian(current.AsSpan(1, 4));
                        var destination = _hookAddress.ToInt64() + 5L + rel;
                        if (destination == _codeCave.ToInt64())
                            _memory.WriteProtectedBytes(_hookAddress, _originalHookBytes);
                    }
                }
            }
            catch { }

            try
            {
                if (_codeCave != 0)
                    _memory.FreeRemote(_codeCave);
            }
            catch { }
        }

        _hookInstalled = false;
        _hookAddress = 0;
        _codeCave = 0;
        _capturePlayerSlot = 0;
        _captureComponentSlot = 0;
        _originalHookBytes = null;
        _hookSignature = "não resolvida";
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
