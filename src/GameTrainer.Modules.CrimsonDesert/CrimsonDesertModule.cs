using System.Buffers.Binary;
using GameTrainer.Core.Memory;
using GameTrainer.Core.Models;
using GameTrainer.Core.Modules;

namespace GameTrainer.Modules.CrimsonDesert;

public sealed class CrimsonDesertModule : IGameModule, IDisposable
{
    private const int HookLength = 11;
    private const int CaveSize = 0x100;
    private const int CaptureSlotOffset = 0x80;

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

    // Cheat Table atual: mov rax,[rbx+68] / mov rcx,[rax+1A0].
    private const string CurrentPlayerAob =
        "48 8B 43 68 48 8B 88 A0 01 00 00 48 8B 41 38 0F B7 48 20";

    // Fallback para localizar somente o par de instruções usado pelo hook.
    private const string CurrentPlayerShortAob =
        "48 8B 43 68 48 8B 88 A0 01 00 00";

    private const string CurrentPlayerLegacyShortAob =
        "48 8B 43 68 48 8B 88 B0 01 00 00";

    private ProcessMemory? _memory;
    private RuntimeState _runtime = new();

    private nint _hookAddress;
    private nint _codeCave;
    private nint _captureSlot;
    private byte[]? _originalHookBytes;
    private bool _hookInstalled;

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
                    new()
                    {
                        Id = "infinite-health",
                        Name = "Vida ilimitada",
                        Description = "Mantém a vida no máximo atual do personagem.",
                        Type = TrainerFeatureType.Toggle
                    },
                    new()
                    {
                        Id = "infinite-stamina",
                        Name = "Vigor ilimitado",
                        Description = "Mantém o vigor no máximo atual do personagem.",
                        Type = TrainerFeatureType.Toggle
                    },
                    new()
                    {
                        Id = "infinite-spirit",
                        Name = "Espírito ilimitado",
                        Description = "Mantém o espírito no máximo atual do personagem.",
                        Type = TrainerFeatureType.Toggle
                    }
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
            case "infinite-health":
                _health = enabled;
                break;
            case "infinite-stamina":
                _stamina = enabled;
                break;
            case "infinite-spirit":
                _spirit = enabled;
                break;
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

        // O hook atualiza cplayer continuamente. Se o personagem controlado mudar,
        // trocamos os endereços automaticamente antes de qualquer escrita.
        if (_hookInstalled && TryReadCapturedPlayer(out var capturedPlayer)
            && capturedPlayer != 0
            && capturedPlayer != _runtime.CapturedPlayer)
        {
            _runtime = new RuntimeState();
            TryResolveFromCapturedPlayer(capturedPlayer, out _runtime, out _);
        }

        if (!_health && !_stamina && !_spirit)
            return Task.CompletedTask;

        if (!EnsureRuntime())
            return Task.CompletedTask;

        if (_health)
            RestoreCurrentToMaximum(_runtime.HealthCurrent, _runtime.HealthMax, "Vida");
        if (_stamina)
            RestoreCurrentToMaximum(_runtime.StaminaCurrent, _runtime.StaminaMax, "Vigor");
        if (_spirit)
            RestoreCurrentToMaximum(_runtime.SpiritCurrent, _runtime.SpiritMax, "Espírito");

        return Task.CompletedTask;
    }

    private async Task<bool> WaitForPlayerAndResolveAsync(string hookLog, CancellationToken cancellationToken)
    {
        var log = new List<string> { hookLog };
        RuntimeStatus = "Hook instalado. Aguardando a rotina do jogador preencher o ponteiro...";

        for (var attempt = 0; attempt < 60; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (TryReadCapturedPlayer(out var capturedPlayer) && capturedPlayer != 0)
            {
                if (TryResolveFromCapturedPlayer(capturedPlayer, out var runtime, out var resolveLog))
                {
                    _runtime = runtime;
                    log.Add(resolveLog);
                    DiagnosticReport = string.Join(Environment.NewLine, log.Where(x => !string.IsNullOrWhiteSpace(x)));
                    RuntimeStatus = "Pronto • jogador capturado pelo AOB • Vida/Vigor/Espírito validados";
                    LastError = string.Empty;
                    return true;
                }

                log.Add(resolveLog);
            }

            await Task.Delay(50, cancellationToken);
        }

        if (TryReadCapturedPlayer(out var finalPlayer))
            log.Add($"Capture slot final: 0x{finalPlayer.ToInt64():X}");
        else
            log.Add("Capture slot final: leitura inválida");

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
            "Diagnóstico v0.2.8 - CT Direct Player Capture",
            $"Módulo: 0x{_memory.MainModuleBase.ToInt64():X} / 0x{_memory.MainModuleSize:X} bytes",
            "Fonte estrutural: CrimsonDesert.CT",
            "Cadeia: cplayer -> [cplayer+68] -> +20 -> +18 -> +58 -> Stats",
            "Offsets: HP 08/18 | Vigor 518/528 | Espírito 5A8/5B8 | Spirit ID 19"
        };

        try
        {
            var match = _memory.FindPatternInMainModule(CurrentPlayerAob)
                        ?? _memory.FindPatternInMainModule(CurrentPlayerShortAob)
                        ?? _memory.FindPatternInMainModule(CurrentPlayerLegacyShortAob);

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
                log.Add($"Bytes: {Convert.ToHexString(_originalHookBytes)}");
                diagnostic = string.Join(Environment.NewLine, log);
                return false;
            }

            _codeCave = _memory.AllocateExecutableNear(_hookAddress, CaveSize);
            _captureSlot = _codeCave + CaptureSlotOffset;
            _memory.Write<long>(_captureSlot, 0);

            var caveCode = BuildCaptureCave(_codeCave, _captureSlot, _hookAddress, _originalHookBytes);
            _memory.WriteBytes(_codeCave, caveCode);

            var patch = new byte[HookLength];
            patch[0] = 0xE9;
            BinaryPrimitives.WriteInt32LittleEndian(
                patch.AsSpan(1, 4),
                CheckedRel32(_hookAddress + 5, _codeCave));
            Array.Fill(patch, (byte)0x90, 5, HookLength - 5);

            _memory.WriteProtectedBytes(_hookAddress, patch);
            _hookInstalled = true;

            log.Add($"AOB getcurrentplayer: 0x{_hookAddress.ToInt64():X} (RVA 0x{_hookAddress.ToInt64() - _memory.MainModuleBase.ToInt64():X})");
            log.Add($"Code cave: 0x{_codeCave.ToInt64():X}");
            log.Add($"Capture slot: 0x{_captureSlot.ToInt64():X}");
            log.Add("Hook: instalado; captura RBX e restaura exatamente as 11 instruções sobrescritas");
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
        nint captureSlot,
        nint hookAddress,
        byte[] originalBytes)
    {
        var code = new List<byte>(64)
        {
            0x50,                         // push rax
            0x48, 0xB8                   // mov rax, imm64
        };
        code.AddRange(BitConverter.GetBytes(captureSlot.ToInt64()));
        code.AddRange(new byte[]
        {
            0x48, 0x89, 0x18,             // mov [rax],rbx
            0x58                           // pop rax
        });

        code.AddRange(originalBytes);

        var jumpInstruction = cave + code.Count;
        code.Add(0xE9);
        code.AddRange(BitConverter.GetBytes(
            CheckedRel32(jumpInstruction + 5, hookAddress + HookLength)));

        return code.ToArray();
    }

    private bool TryReadCapturedPlayer(out nint capturedPlayer)
    {
        capturedPlayer = 0;
        if (_memory is null || !_memory.IsAttached || !_hookInstalled || _captureSlot == 0)
            return false;

        if (!_memory.TryRead<long>(_captureSlot, out var raw))
            return false;

        capturedPlayer = (nint)raw;
        return capturedPlayer == 0 || ProcessMemory.IsLikelyPointer(capturedPlayer);
    }

    private bool TryResolveFromCapturedPlayer(
        nint capturedPlayer,
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
            $"Captured cplayer: 0x{capturedPlayer.ToInt64():X}"
        };

        if (!_memory.TryReadPointer(capturedPlayer + 0x68, out var playerComponent))
        {
            diagnostic = string.Join(Environment.NewLine, log.Append("[cplayer+68]: inválido"));
            return false;
        }
        log.Add($"[cplayer+68]: 0x{playerComponent.ToInt64():X}");

        if (!_memory.TryReadPointer(playerComponent + 0x20, out var marker))
        {
            diagnostic = string.Join(Environment.NewLine, log.Append("[component+20]: inválido"));
            return false;
        }
        log.Add($"[component+20]: 0x{marker.ToInt64():X}");

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

        if (TryReadCapturedPlayer(out var player)
            && player != 0
            && TryResolveFromCapturedPlayer(player, out var runtime, out var diagnostic))
        {
            _runtime = runtime;
            DiagnosticReport = BuildHookHeader() + Environment.NewLine + diagnostic;
            RuntimeStatus = "Pronto • jogador capturado pelo AOB • Vida/Vigor/Espírito validados";
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

        // Valores da tabela são inteiros de 32 bits. Aceitamos current acima do max
        // somente até 20x para tolerar buffs temporários sem aceitar lixo de memória.
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
            "Diagnóstico v0.2.8 - CT Direct Player Capture",
            $"Módulo: 0x{_memory.MainModuleBase.ToInt64():X} / 0x{_memory.MainModuleSize:X} bytes",
            $"AOB getcurrentplayer: {(_hookAddress == 0 ? "não resolvido" : $"0x{_hookAddress.ToInt64():X}")}",
            $"Code cave: {(_codeCave == 0 ? "não alocado" : $"0x{_codeCave.ToInt64():X}")}",
            $"Capture slot: {(_captureSlot == 0 ? "não alocado" : $"0x{_captureSlot.ToInt64():X}")}",
            "Cadeia CT: cplayer -> [cplayer+68] -> +20 -> +18 -> +58 -> Stats",
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
                    // Só restauramos se o ponto ainda começa com o JMP que instalamos.
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
                // Cleanup best effort: não mascarar o fechamento do aplicativo.
            }

            try
            {
                if (_codeCave != 0)
                    _memory.FreeRemote(_codeCave);
            }
            catch
            {
                // Cleanup best effort.
            }
        }

        _hookInstalled = false;
        _hookAddress = 0;
        _codeCave = 0;
        _captureSlot = 0;
        _originalHookBytes = null;
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
