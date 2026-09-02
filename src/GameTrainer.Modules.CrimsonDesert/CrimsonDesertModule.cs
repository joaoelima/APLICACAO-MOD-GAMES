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
    private const int ExpectedStaminaId = 17;
    private const int ExpectedSpiritId = 19;

    // Contexto observado na build 1.0.0.2692. O hook real começa 15 bytes após o início.
    private const string CurrentPlayerContextAob =
        "48 8B 53 08 48 8D 4C 24 78 E8 ? ? ? ? 90 48 8B 43 68 48 8B 88 A0 01 00 00";
    private const int CurrentPlayerContextHookOffset = 15;

    // Assinatura direta da região descrita pela Cheat Table atual.
    private const string CurrentPlayerAob =
        "48 8B 43 68 48 8B 88 A0 01 00 00 48 8B 41 38 0F B7 48 20";

    private const string CurrentPlayerLegacyContextAob =
        "48 8B 53 08 48 8D 4C 24 78 E8 ? ? ? ? 90 48 8B 43 68 48 8B 88 B0 01 00 00";

    private const string CurrentPlayerShortAob =
        "48 8B 43 68 48 8B 88 A0 01 00 00";

    private const string CurrentPlayerLegacyShortAob =
        "48 8B 43 68 48 8B 88 B0 01 00 00";

    private ProcessMemory? _memory;
    private RuntimeState _runtime = new();

    private nint _hookAddress;
    private nint _codeCave;
    private nint _capturePlayerSlot;
    private nint _captureComponentSlot;
    private byte[]? _originalHookBytes;
    private bool _hookInstalled;
    private string _hookSignature = "não resolvida";

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

        if (!InstallCurrentPlayerHook(out var hookLog))
        {
            DiagnosticReport = hookLog;
            RuntimeStatus = "Jogo conectado, mas o hook do jogador atual não pôde ser instalado. Use “Copiar diagnóstico”.";
            LastError = RuntimeStatus;
            return;
        }

        await WaitForPlayerAndResolveAsync(hookLog, cancellationToken);
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
        var log = new List<string>();

        if (!_hookInstalled)
        {
            if (!InstallCurrentPlayerHook(out var hookLog))
            {
                DiagnosticReport = hookLog;
                RuntimeStatus = "Não foi possível instalar o hook do jogador atual.";
                LastError = RuntimeStatus;
                return false;
            }
            log.Add(hookLog);
        }
        else
        {
            log.Add(BuildHookHeader());
        }

        return await WaitForPlayerAndResolveAsync(string.Join(Environment.NewLine, log), cancellationToken);
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

        if (_hookInstalled && TryReadCapturedPointers(out var capturedPlayer, out var capturedComponent)
            && capturedPlayer != 0
            && capturedPlayer != _runtime.CapturedPlayer)
        {
            if (TryResolveFromCapture(capturedPlayer, capturedComponent, out var runtime, out var diagnostic))
            {
                _runtime = runtime;
                DiagnosticReport = BuildHookHeader() + Environment.NewLine + diagnostic;
                RuntimeStatus = "Pronto • jogador capturado pela rotina CT • Vida/Vigor/Espírito validados";
                LastError = string.Empty;
            }
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
        RuntimeStatus = "Hook instalado. Aguardando a rotina do jogador preencher cplayer/csplayer...";

        for (var attempt = 0; attempt < 100; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (TryReadCapturedPointers(out var capturedPlayer, out var capturedComponent)
                && (capturedPlayer != 0 || capturedComponent != 0))
            {
                if (TryResolveFromCapture(capturedPlayer, capturedComponent, out var runtime, out var resolveLog))
                {
                    _runtime = runtime;
                    log.Add(resolveLog);
                    DiagnosticReport = string.Join(Environment.NewLine, log.Where(x => !string.IsNullOrWhiteSpace(x)));
                    RuntimeStatus = "Pronto • jogador capturado pela rotina CT • Vida/Vigor/Espírito validados";
                    LastError = string.Empty;
                    return true;
                }

                log.Add(resolveLog);
            }

            await Task.Delay(50, cancellationToken);
        }

        if (TryReadCapturedPointers(out var finalPlayer, out var finalComponent))
        {
            log.Add($"Capture cplayer final: 0x{finalPlayer.ToInt64():X}");
            log.Add($"Capture csplayer final: 0x{finalComponent.ToInt64():X}");
        }
        else
        {
            log.Add("Capture slots finais: leitura inválida");
        }

        DiagnosticReport = string.Join(Environment.NewLine, log.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct());
        RuntimeStatus = "Hook instalado, mas a cadeia do jogador ainda não validou. Use “Copiar diagnóstico”.";
        LastError = RuntimeStatus;
        return false;
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
            "Diagnóstico v0.3.1 - CT Context Player Capture",
            $"Módulo: 0x{_memory.MainModuleBase.ToInt64():X} / 0x{_memory.MainModuleSize:X} bytes",
            "Fonte estrutural: CrimsonDesert.CT",
            "Cadeia: cplayer -> [cplayer+68]/csplayer -> +20 -> +18 -> +58 -> Stats",
            "Offsets: HP 08/18 | Vigor 518/528 | Espírito 5A8/5B8 | Spirit ID 19"
        };

        try
        {
            nint? match;
            var context = _memory.FindPatternInMainModule(CurrentPlayerContextAob);
            if (context.HasValue)
            {
                match = context.Value + CurrentPlayerContextHookOffset;
                _hookSignature = "context-2692 +15";
            }
            else
            {
                var legacyContext = _memory.FindPatternInMainModule(CurrentPlayerLegacyContextAob);
                if (legacyContext.HasValue)
                {
                    match = legacyContext.Value + CurrentPlayerContextHookOffset;
                    _hookSignature = "context-legacy +15";
                }
                else
                {
                    match = _memory.FindPatternInMainModule(CurrentPlayerAob);
                    if (match.HasValue)
                        _hookSignature = "direct-long";
                    else
                    {
                        match = _memory.FindPatternInMainModule(CurrentPlayerShortAob);
                        if (match.HasValue)
                            _hookSignature = "direct-short";
                        else
                        {
                            match = _memory.FindPatternInMainModule(CurrentPlayerLegacyShortAob);
                            if (match.HasValue)
                                _hookSignature = "legacy-short";
                        }
                    }
                }
            }

            if (!match.HasValue)
            {
                log.Add("AOB getcurrentplayer: não encontrado");
                diagnostic = string.Join(Environment.NewLine, log);
                return false;
            }

            _hookAddress = match.Value;
            _originalHookBytes = _memory.ReadBytes(_hookAddress, HookLength);

            var expectedPrefixA0 = new byte[] { 0x48, 0x8B, 0x43, 0x68, 0x48, 0x8B, 0x88, 0xA0, 0x01, 0x00, 0x00 };
            var expectedPrefixB0 = new byte[] { 0x48, 0x8B, 0x43, 0x68, 0x48, 0x8B, 0x88, 0xB0, 0x01, 0x00, 0x00 };
            if (!_originalHookBytes.SequenceEqual(expectedPrefixA0) && !_originalHookBytes.SequenceEqual(expectedPrefixB0))
            {
                log.Add($"AOB getcurrentplayer: 0x{_hookAddress.ToInt64():X}, mas os 11 bytes não correspondem ao bloco esperado");
                log.Add($"Assinatura selecionada: {_hookSignature}");
                log.Add($"Bytes: {Convert.ToHexString(_originalHookBytes)}");
                diagnostic = string.Join(Environment.NewLine, log);
                return false;
            }

            _codeCave = _memory.AllocateExecutableNear(_hookAddress, CaveSize);
            _capturePlayerSlot = _codeCave + CapturePlayerSlotOffset;
            _captureComponentSlot = _codeCave + CaptureComponentSlotOffset;
            _memory.Write<long>(_capturePlayerSlot, 0);
            _memory.Write<long>(_captureComponentSlot, 0);

            var caveCode = BuildCaptureCave(
                _codeCave,
                _capturePlayerSlot,
                _captureComponentSlot,
                _hookAddress,
                _originalHookBytes);
            _memory.WriteBytes(_codeCave, caveCode);

            var patch = new byte[HookLength];
            patch[0] = 0xE9;
            BinaryPrimitives.WriteInt32LittleEndian(patch.AsSpan(1, 4), CheckedRel32(_hookAddress + 5, _codeCave));
            Array.Fill(patch, (byte)0x90, 5, HookLength - 5);

            _memory.WriteProtectedBytes(_hookAddress, patch);
            _hookInstalled = true;

            log.Add($"Assinatura selecionada: {_hookSignature}");
            log.Add($"AOB getcurrentplayer: 0x{_hookAddress.ToInt64():X} (RVA 0x{_hookAddress.ToInt64() - _memory.MainModuleBase.ToInt64():X})");
            log.Add($"Code cave: 0x{_codeCave.ToInt64():X}");
            log.Add($"Capture cplayer: 0x{_capturePlayerSlot.ToInt64():X}");
            log.Add($"Capture csplayer: 0x{_captureComponentSlot.ToInt64():X}");
            log.Add("Hook: instalado; executa os 11 bytes originais e captura RBX + RAX sem alterar o resultado da rotina");
            diagnostic = string.Join(Environment.NewLine, log);
            return true;
        }
        catch (Exception ex)
        {
            log.Add($"Falha ao instalar hook: {ex.GetType().Name} - {ex.Message}");
            diagnostic = string.Join(Environment.NewLine, log);
            SafeRemoveHook();
            return false;
        }
    }

    private static byte[] BuildCaptureCave(
        nint cave,
        nint playerSlot,
        nint componentSlot,
        nint hookAddress,
        byte[] originalBytes)
    {
        var code = new List<byte>(80);

        // Primeiro reproduz exatamente o código do jogo:
        // mov rax,[rbx+68]
        // mov rcx,[rax+1A0/1B0]
        code.AddRange(originalBytes);

        // Preserva RDX e flags, e guarda RBX (cplayer) + RAX (csplayer/component).
        code.Add(0x52);                         // push rdx
        code.AddRange(new byte[] { 0x48, 0xBA }); // mov rdx, imm64
        code.AddRange(BitConverter.GetBytes(playerSlot.ToInt64()));
        code.AddRange(new byte[] { 0x48, 0x89, 0x1A }); // mov [rdx],rbx
        code.AddRange(new byte[] { 0x48, 0xBA }); // mov rdx, imm64
        code.AddRange(BitConverter.GetBytes(componentSlot.ToInt64()));
        code.AddRange(new byte[] { 0x48, 0x89, 0x02 }); // mov [rdx],rax
        code.Add(0x5A);                         // pop rdx

        var jumpInstruction = cave + code.Count;
        code.Add(0xE9);
        code.AddRange(BitConverter.GetBytes(CheckedRel32(jumpInstruction + 5, hookAddress + HookLength)));
        return code.ToArray();
    }

    private bool TryReadCapturedPointers(out nint capturedPlayer, out nint capturedComponent)
    {
        capturedPlayer = 0;
        capturedComponent = 0;
        if (_memory is null || !_memory.IsAttached || !_hookInstalled
            || _capturePlayerSlot == 0 || _captureComponentSlot == 0)
            return false;

        if (!_memory.TryRead<long>(_capturePlayerSlot, out var rawPlayer)
            || !_memory.TryRead<long>(_captureComponentSlot, out var rawComponent))
            return false;

        capturedPlayer = (nint)rawPlayer;
        capturedComponent = (nint)rawComponent;
        var playerOk = capturedPlayer == 0 || ProcessMemory.IsLikelyPointer(capturedPlayer);
        var componentOk = capturedComponent == 0 || ProcessMemory.IsLikelyPointer(capturedComponent);
        return playerOk && componentOk;
    }

    private bool TryResolveFromCapture(
        nint capturedPlayer,
        nint capturedComponent,
        out RuntimeState runtime,
        out string diagnostic)
    {
        runtime = new RuntimeState();
        if (_memory is null)
        {
            diagnostic = "ProcessMemory indisponível.";
            return false;
        }

        var log = new List<string>
        {
            $"Captured cplayer/RBX: 0x{capturedPlayer.ToInt64():X}",
            $"Captured csplayer/RAX: 0x{capturedComponent.ToInt64():X}"
        };

        nint componentFromPlayer = 0;
        var hasComponentFromPlayer = capturedPlayer != 0
                                     && _memory.TryReadPointer(capturedPlayer + 0x68, out componentFromPlayer);
        log.Add(hasComponentFromPlayer
            ? $"[cplayer+68]: 0x{componentFromPlayer.ToInt64():X}"
            : "[cplayer+68]: inválido/não disponível");

        var playerComponent = capturedComponent != 0 && _memory.IsReadable(capturedComponent)
            ? capturedComponent
            : componentFromPlayer;

        if (playerComponent == 0 || !_memory.IsReadable(playerComponent))
        {
            diagnostic = string.Join(Environment.NewLine, log.Append("Nenhum csplayer/component válido foi capturado."));
            return false;
        }

        if (capturedComponent != 0 && hasComponentFromPlayer)
            log.Add(capturedComponent == componentFromPlayer
                ? "Comparação csplayer: RAX == [cplayer+68]"
                : $"Comparação csplayer: divergente (RAX 0x{capturedComponent.ToInt64():X} != [cplayer+68] 0x{componentFromPlayer.ToInt64():X})");

        if (!_memory.TryReadPointer(playerComponent + 0x20, out var marker))
        {
            diagnostic = string.Join(Environment.NewLine, log.Append("[csplayer+20]: inválido"));
            return false;
        }
        log.Add($"[csplayer+20]: 0x{marker.ToInt64():X}");

        if (!_memory.TryReadPointer(marker + 0x18, out var root))
        {
            diagnostic = string.Join(Environment.NewLine, log.Append("[marker+18]: inválido"));
            return false;
        }
        log.Add($"[marker+18]: 0x{root.ToInt64():X}");

        if (!_memory.TryReadPointer(root + 0x58, out var stats))
        {
            diagnostic = string.Join(Environment.NewLine, log.Append("[root+58]: inválido"));
            return false;
        }
        log.Add($"[root+58] Stats: 0x{stats.ToInt64():X}");

        if (!_memory.TryRead<int>(stats + HealthIdOffset, out var healthId)
            || !_memory.TryRead<int>(stats + StaminaIdOffset, out var staminaId)
            || !_memory.TryRead<int>(stats + SpiritIdOffset, out var spiritId))
        {
            diagnostic = string.Join(Environment.NewLine, log.Append("Não foi possível ler os IDs dos atributos."));
            return false;
        }

        log.Add($"Stat IDs: HP={healthId}, Vigor={staminaId}, Espírito={spiritId}");
        if (healthId != ExpectedHealthId || staminaId != ExpectedStaminaId || spiritId != ExpectedSpiritId)
        {
            diagnostic = string.Join(Environment.NewLine, log.Append(
                $"IDs esperados: HP={ExpectedHealthId}, Vigor={ExpectedStaminaId}, Espírito={ExpectedSpiritId}."));
            return false;
        }

        if (!TryReadStatPair(stats + HealthCurrentOffset, stats + HealthMaxOffset, out var hp)
            || !TryReadStatPair(stats + StaminaCurrentOffset, stats + StaminaMaxOffset, out var stamina)
            || !TryReadStatPair(stats + SpiritCurrentOffset, stats + SpiritMaxOffset, out var spirit))
        {
            diagnostic = string.Join(Environment.NewLine, log.Append("Current/Maximum não passaram pela validação."));
            return false;
        }

        log.Add($"HP={hp.Current}/{hp.Maximum}");
        log.Add($"Vigor={stamina.Current}/{stamina.Maximum}");
        log.Add($"Espírito={spirit.Current}/{spirit.Maximum}");

        runtime = new RuntimeState
        {
            IsResolved = true,
            CapturedPlayer = capturedPlayer,
            PlayerComponent = playerComponent,
            Marker = marker,
            Root = root,
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

    private bool EnsureRuntime()
    {
        if (_runtime.IsResolved && ValidateRuntime())
            return true;

        if (TryReadCapturedPointers(out var player, out var component)
            && (player != 0 || component != 0)
            && TryResolveFromCapture(player, component, out var runtime, out var diagnostic))
        {
            _runtime = runtime;
            DiagnosticReport = BuildHookHeader() + Environment.NewLine + diagnostic;
            RuntimeStatus = "Pronto • jogador capturado pela rotina CT • Vida/Vigor/Espírito validados";
            LastError = string.Empty;
            return true;
        }

        RuntimeStatus = "Aguardando o ponteiro do jogador validar novamente...";
        LastError = RuntimeStatus;
        return false;
    }

    private bool ValidateRuntime()
    {
        if (!_runtime.IsResolved || _memory is null)
            return false;

        if (!_memory.TryRead<int>(_runtime.StatsBase + HealthIdOffset, out var hpId)
            || !_memory.TryRead<int>(_runtime.StatsBase + StaminaIdOffset, out var staminaId)
            || !_memory.TryRead<int>(_runtime.StatsBase + SpiritIdOffset, out var spiritId))
            return false;

        if (hpId != ExpectedHealthId || staminaId != ExpectedStaminaId || spiritId != ExpectedSpiritId)
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

    private string BuildHookHeader()
    {
        if (_memory is null)
            return "Hook indisponível.";

        return string.Join(Environment.NewLine,
            "Diagnóstico v0.3.1 - CT Context Player Capture",
            $"Módulo: 0x{_memory.MainModuleBase.ToInt64():X} / 0x{_memory.MainModuleSize:X} bytes",
            $"Assinatura: {_hookSignature}",
            $"AOB getcurrentplayer: {(_hookAddress == 0 ? "não resolvido" : $"0x{_hookAddress.ToInt64():X}")}",
            $"Code cave: {(_codeCave == 0 ? "não alocado" : $"0x{_codeCave.ToInt64():X}")}",
            $"Capture cplayer: {(_capturePlayerSlot == 0 ? "não alocado" : $"0x{_capturePlayerSlot.ToInt64():X}")}",
            $"Capture csplayer: {(_captureComponentSlot == 0 ? "não alocado" : $"0x{_captureComponentSlot.ToInt64():X}")}",
            "Cadeia CT: cplayer -> [cplayer+68]/csplayer -> +20 -> +18 -> +58 -> Stats",
            "Offsets CT: HP 08/18 | Vigor 518/528 | Espírito 5A8/5B8",
            "IDs esperados: HP=0 | Vigor=17 | Espírito=19");
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
        SafeRemoveHook();
        _memory = null;
        _runtime = new RuntimeState();
        _health = false;
        _stamina = false;
        _spirit = false;
        RuntimeStatus = "Aguardando o jogo";
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
                        && current.Length == 5
                        && current[0] == 0xE9)
                    {
                        var rel = BinaryPrimitives.ReadInt32LittleEndian(current.AsSpan(1, 4));
                        var destination = _hookAddress.ToInt64() + 5L + rel;
                        if (destination == _codeCave.ToInt64())
                            _memory.WriteProtectedBytes(_hookAddress, _originalHookBytes);
                    }
                }
            }
            catch
            {
            }

            try
            {
                if (_codeCave != 0)
                    _memory.FreeRemote(_codeCave);
            }
            catch
            {
            }
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
        public nint Marker { get; set; }
        public nint Root { get; set; }
        public nint StatsBase { get; set; }
        public nint HealthCurrent { get; set; }
        public nint HealthMax { get; set; }
        public nint StaminaCurrent { get; set; }
        public nint StaminaMax { get; set; }
        public nint SpiritCurrent { get; set; }
        public nint SpiritMax { get; set; }
    }
}
