using GameTrainer.Core.Memory;
using GameTrainer.Core.Models;
using GameTrainer.Core.Modules;

namespace GameTrainer.Modules.CrimsonDesert;

public sealed class CrimsonDesertModule : IGameModule
{
    private const int HealthId = 0;
    private const int StaminaId = 17;
    private const int SpiritId = 18;

    private const int StatTypeOffset = 0x00;
    private const int StatCurrentOffset = 0x08;
    private const int StatMaxOffset = 0x18;

    private const int ActorManagerBodyStart = 0xD0;
    private const int ActorManagerBodyCount = 8;

    private const int ActorTypeComponentOffset = 0x48;
    private const int TypeComponentActorOffset = 0x08;
    private const int TypeActorTypePtrOffset = 0x88;
    private const int TypeByteOffset = 0x01;
    private const byte LocalPlayerType = 0x01;

    private const int ActorToInnerOffset = 0x40;
    private const int InnerToCoreOffset = 0x08;
    private const int CoreToPositionStructOffset = 0x248;
    private const int PositionXOffset = 0x90;
    private const int PositionYOffset = 0x94;
    private const int PositionZOffset = 0x98;

    private static readonly StatLayout[] StatLayouts =
    {
        new(0x510, 0x5A0, "Int64-mai-2026"),
        new(0x480, 0x510, "Int64-legado-v1.01")
    };

    private const int StaticPlayerBaseRva = 0x05CC7618;

    private const string PlayerBaseDiscoveryPattern =
        "48 8B 0D ? ? ? ? E8 ? ? ? ? 41 B0 01 48 8B 53 08 48 8D 4C 24 40";

    private const string CurrentPlayerPattern =
        "48 8B 53 08 48 8D 4C 24 78 E8 ? ? ? ? 90 48 8B 43 68 48 8B 88 A0 01 00 00 48 8B 41 38 0F B7 48 20";

    private static readonly WorldSystemPattern[] WorldSystemPatterns =
    {
        new("P1", "48 83 EC 28 48 8B 0D ? ? ? ? 48 8B 49 ? E8 ? ? ? ? 84 C0 0F 94 C0 48 83 C4 28 C3", 7, 11),
        new("P2", "80 B8 ? ? ? ? 00 75 ? 48 8B 05 ? ? ? ? 48 8B 88 ? ? ? ?", 12, 16),
        new("P3", "48 8B 0D ? ? ? ? 48 8B 49 ? E8 ? ? ? ? 84 C0 0F 94 C0", 3, 7)
    };

    private static readonly int[] PlayerBaseCharacterSlots = { 0x68, 0xE0, 0x168, 0x268 };

    private ProcessMemory? _memory;
    private PlayerRuntime _runtime = new();
    private DateTime _nextResolveAttemptUtc = DateTime.MinValue;

    private bool _infiniteHealth;
    private bool _infiniteStamina;
    private bool _infiniteSpirit;

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
                        Description = "Restaura continuamente a vida para o valor máximo.",
                        Type = TrainerFeatureType.Toggle
                    },
                    new()
                    {
                        Id = "infinite-stamina",
                        Name = "Vigor ilimitado",
                        Description = "Restaura continuamente o vigor para o valor máximo.",
                        Type = TrainerFeatureType.Toggle
                    },
                    new()
                    {
                        Id = "infinite-spirit",
                        Name = "Espírito ilimitado",
                        Description = "Restaura continuamente o espírito para o valor máximo.",
                        Type = TrainerFeatureType.Toggle
                    }
                }
            },
            new TrainerSection
            {
                Name = "Inimigos",
                Features = new TrainerFeature[]
                {
                    new()
                    {
                        Id = "one-hit-kill",
                        Name = "Super Dano / Mortes com Um Golpe",
                        Description = "Aguardando validação segura do atributo de ataque nesta build.",
                        Type = TrainerFeatureType.Toggle
                    }
                }
            }
        }
    };

    public Task AttachAsync(ProcessMemory processMemory, CancellationToken cancellationToken = default)
    {
        _memory = processMemory;
        _runtime = new PlayerRuntime();
        LastError = string.Empty;
        DiagnosticReport = "Iniciando diagnóstico da memória do Crimson Desert...";
        ResolveRuntime(force: true);
        return Task.CompletedTask;
    }

    public Task<bool> ReprobeAsync(CancellationToken cancellationToken = default)
    {
        if (_memory is null || !_memory.IsAttached)
        {
            LastError = "O Crimson Desert não está conectado.";
            RuntimeStatus = LastError;
            DiagnosticReport = LastError;
            return Task.FromResult(false);
        }

        _runtime = new PlayerRuntime();
        _nextResolveAttemptUtc = DateTime.MinValue;
        return Task.FromResult(ResolveRuntime(force: true));
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
            LastError = enabled
                ? "Super Dano permanece desativado até validarmos o bloco de ataque desta build."
                : string.Empty;
            return Task.FromResult(!enabled);
        }

        if (enabled && !EnsureRuntime())
            return Task.FromResult(false);

        var success = featureId switch
        {
            "infinite-health" => SetFlag(ref _infiniteHealth, enabled),
            "infinite-stamina" => SetFlag(ref _infiniteStamina, enabled),
            "infinite-spirit" => SetFlag(ref _infiniteSpirit, enabled),
            _ => false
        };

        if (!success && string.IsNullOrWhiteSpace(LastError))
            LastError = $"Recurso desconhecido: {featureId}.";

        return Task.FromResult(success);
    }

    public Task<bool> SetValueAsync(string featureId, double value, CancellationToken cancellationToken = default)
        => Task.FromResult(false);

    public Task TickAsync(CancellationToken cancellationToken = default)
    {
        if (_memory is null || !_memory.IsAttached)
            return Task.CompletedTask;

        if (!_infiniteHealth && !_infiniteStamina && !_infiniteSpirit)
            return Task.CompletedTask;

        if (!EnsureRuntime())
            return Task.CompletedTask;

        try
        {
            if (_infiniteHealth)
                RestoreStat(_runtime.HealthEntry, HealthId, "Vida");
            if (_infiniteStamina)
                RestoreStat(_runtime.StaminaEntry, StaminaId, "Vigor");
            if (_infiniteSpirit)
                RestoreStat(_runtime.SpiritEntry, SpiritId, "Espírito");
        }
        catch
        {
            InvalidateRuntime("A estrutura do jogador mudou. Tentando localizar novamente...");
        }

        return Task.CompletedTask;
    }

    private bool SetFlag(ref bool field, bool enabled)
    {
        field = enabled;
        LastError = string.Empty;
        return true;
    }

    private bool EnsureRuntime()
    {
        if (_runtime.IsResolved && ValidateRuntime())
            return true;

        return ResolveRuntime(force: false);
    }

    private bool ResolveRuntime(bool force)
    {
        if (_memory is null || !_memory.IsAttached)
            return false;

        if (!force && DateTime.UtcNow < _nextResolveAttemptUtc)
            return false;

        _nextResolveAttemptUtc = DateTime.UtcNow.AddSeconds(2);

        var diagnostics = new List<string>
        {
            "Diagnóstico v0.2.6",
            $"Módulo: 0x{_memory.MainModuleBase.ToInt64():X} / 0x{_memory.MainModuleSize:X} bytes",
            "Estratégia: ActorManager body slots + type byte 0x01 + posição + Stats Component",
            "Body slots: +0xD0..+0x108 | Type chain: +48 -> +08 -> +88 -> +01",
            "Position chain: +40 -> +08 -> +248 -> XYZ em +90/+94/+98"
        };

        AddAobDiagnostic(diagnostics, "CurrentPlayer", CurrentPlayerPattern);
        AddAobDiagnostic(diagnostics, "PlayerBaseDiscovery", PlayerBaseDiscoveryPattern);

        try
        {
            foreach (var pattern in WorldSystemPatterns)
            {
                var match = _memory.FindPatternInMainModule(pattern.Signature);
                if (!match.HasValue)
                {
                    diagnostics.Add($"WorldSystem {pattern.Name}: assinatura não encontrada");
                    continue;
                }

                var globalSlot = _memory.ResolveRipRelative(
                    match.Value,
                    pattern.DisplacementOffset,
                    pattern.InstructionEndOffset);

                if (!_memory.TryReadPointer(globalSlot, out var worldSystem))
                {
                    diagnostics.Add($"WorldSystem {pattern.Name}: ponteiro global inválido");
                    continue;
                }

                if (!_memory.TryReadPointer(worldSystem + 0x30, out var actorManager))
                {
                    diagnostics.Add($"WorldSystem {pattern.Name}: ActorManager +0x30 inválido");
                    continue;
                }

                diagnostics.Add($"WorldSystem {pattern.Name}: WorldSystem=0x{worldSystem.ToInt64():X}, ActorManager=0x{actorManager.ToInt64():X}");

                // Mantemos o +0x28 apenas como telemetria. Na build 2692 ele apontou para dentro do módulo.
                if (_memory.TryReadPointer(actorManager + 0x28, out var legacyUserActor))
                {
                    diagnostics.Add($"WorldSystem {pattern.Name}: +0x28 => 0x{legacyUserActor.ToInt64():X} ({DescribeAddress(legacyUserActor)})");
                }
                else
                {
                    diagnostics.Add($"WorldSystem {pattern.Name}: +0x28 => inválido");
                }

                if (TryResolveLocalPlayerFromActorManager(actorManager, out var runtime, out var bodyDiagnostics))
                {
                    runtime.WorldSystem = worldSystem;
                    runtime.ActorManager = actorManager;
                    diagnostics.AddRange(bodyDiagnostics);
                    CompleteSuccessfulResolution(runtime, $"WorldSystem {pattern.Name} / ActorManager slots", diagnostics);
                    return true;
                }

                diagnostics.AddRange(bodyDiagnostics);
            }

            if (TryResolveViaPlayerBaseAob(out var aobRuntime, out var aobDetail))
            {
                diagnostics.Add($"PlayerBase AOB: {aobDetail}");
                CompleteSuccessfulResolution(aobRuntime, "PlayerBase AOB", diagnostics);
                return true;
            }
            diagnostics.Add($"PlayerBase AOB: {aobDetail}");

            if (TryResolveViaStaticPlayerBase(out var staticRuntime, out var staticDetail))
            {
                diagnostics.Add($"PlayerBase estático: {staticDetail}");
                CompleteSuccessfulResolution(staticRuntime, "PlayerBase estático", diagnostics);
                return true;
            }
            diagnostics.Add($"PlayerBase estático: {staticDetail}");

            DiagnosticReport = string.Join(Environment.NewLine, diagnostics);
            InvalidateRuntime(
                "ActorManager localizado, mas nenhum body slot validou como jogador local. Use “Copiar diagnóstico”.",
                scheduleRetry: false,
                preserveDiagnostic: true);
            return false;
        }
        catch (Exception ex)
        {
            diagnostics.Add($"Exceção: {ex.GetType().Name} - {ex.Message}");
            DiagnosticReport = string.Join(Environment.NewLine, diagnostics);
            InvalidateRuntime(
                "Falha durante o diagnóstico da memória. Use “Copiar diagnóstico”.",
                scheduleRetry: false,
                preserveDiagnostic: true);
            return false;
        }
    }

    private bool TryResolveLocalPlayerFromActorManager(
        nint actorManager,
        out PlayerRuntime runtime,
        out List<string> diagnostics)
    {
        runtime = new PlayerRuntime();
        diagnostics = new List<string> { "ActorManager body-slot scan:" };

        PlayerRuntime? uniqueFallback = null;
        var fallbackCount = 0;

        for (var i = 0; i < ActorManagerBodyCount; i++)
        {
            var offset = ActorManagerBodyStart + i * 8;

            if (_memory is null || !_memory.TryReadPointer(actorManager + offset, out var candidate))
            {
                diagnostics.Add($"  +0x{offset:X}: vazio/inválido");
                continue;
            }

            var addressKind = DescribeAddress(candidate);
            var typeOk = TryReadActorType(candidate, out var actorType, out var typeDetail);
            var posOk = TryReadActorPosition(candidate, out var position, out var posDetail);
            var statsOk = TryResolveActorStats(candidate, out var candidateRuntime, out var statsDetail);

            diagnostics.Add(
                $"  +0x{offset:X}: 0x{candidate.ToInt64():X} ({addressKind}) | " +
                $"type={(typeOk ? $"0x{actorType:X2}" : "?")} [{typeDetail}] | " +
                $"pos={(posOk ? $"{position.X:F2},{position.Y:F2},{position.Z:F2}" : "?")} [{posDetail}] | " +
                $"stats={(statsOk ? "OK" : "NOK")} [{statsDetail}]");

            if (typeOk && actorType == LocalPlayerType && posOk && statsOk)
            {
                candidateRuntime.Actor = candidate;
                candidateRuntime.ActorManagerSlot = offset;
                candidateRuntime.ActorType = actorType;
                candidateRuntime.Position = position;
                runtime = candidateRuntime;
                diagnostics.Add($"  => Jogador local confirmado no slot +0x{offset:X} pelo type byte 0x01.");
                return true;
            }

            // Fallback conservador: só usamos se exatamente UM slot tiver posição + stats válidos.
            if (posOk && statsOk && !IsInsideMainModule(candidate))
            {
                candidateRuntime.Actor = candidate;
                candidateRuntime.ActorManagerSlot = offset;
                candidateRuntime.ActorType = typeOk ? actorType : null;
                candidateRuntime.Position = position;
                uniqueFallback = candidateRuntime;
                fallbackCount++;
            }
        }

        if (fallbackCount == 1 && uniqueFallback is not null)
        {
            runtime = uniqueFallback;
            diagnostics.Add("  => Fallback aceito: exatamente um body slot apresentou posição e stats válidos.");
            return true;
        }

        diagnostics.Add($"  => Nenhum type 0x01 confirmado. Candidatos fallback válidos: {fallbackCount}.");
        return false;
    }

    private bool TryReadActorType(nint candidate, out byte type, out string detail)
    {
        type = 0;
        detail = string.Empty;

        if (_memory is null)
        {
            detail = "sem memória";
            return false;
        }

        if (!_memory.TryReadPointer(candidate + ActorTypeComponentOffset, out var component))
        {
            detail = "+48 inválido";
            return false;
        }

        if (!_memory.TryReadPointer(component + TypeComponentActorOffset, out var actorForType))
        {
            detail = $"comp=0x{component.ToInt64():X}, +08 inválido";
            return false;
        }

        if (!_memory.TryReadPointer(actorForType + TypeActorTypePtrOffset, out var typePtr))
        {
            detail = $"typeActor=0x{actorForType.ToInt64():X}, +88 inválido";
            return false;
        }

        if (!_memory.TryRead<byte>(typePtr + TypeByteOffset, out type))
        {
            detail = $"typePtr=0x{typePtr.ToInt64():X}, +01 ilegível";
            return false;
        }

        detail = $"comp=0x{component.ToInt64():X}, actor=0x{actorForType.ToInt64():X}, ptr=0x{typePtr.ToInt64():X}";
        return true;
    }

    private bool TryReadActorPosition(nint actor, out PositionSnapshot position, out string detail)
    {
        position = default;
        detail = string.Empty;

        if (_memory is null)
        {
            detail = "sem memória";
            return false;
        }

        if (!_memory.TryReadPointer(actor + ActorToInnerOffset, out var inner))
        {
            detail = "+40 inválido";
            return false;
        }

        if (!_memory.TryReadPointer(inner + InnerToCoreOffset, out var core))
        {
            detail = $"inner=0x{inner.ToInt64():X}, +08 inválido";
            return false;
        }

        if (!_memory.TryReadPointer(core + CoreToPositionStructOffset, out var posStruct))
        {
            detail = $"core=0x{core.ToInt64():X}, +248 inválido";
            return false;
        }

        if (!_memory.TryRead<float>(posStruct + PositionXOffset, out var x)
            || !_memory.TryRead<float>(posStruct + PositionYOffset, out var y)
            || !_memory.TryRead<float>(posStruct + PositionZOffset, out var z))
        {
            detail = $"posStruct=0x{posStruct.ToInt64():X}, XYZ ilegível";
            return false;
        }

        if (!float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(z)
            || Math.Abs(x) > 10_000_000f
            || Math.Abs(y) > 10_000_000f
            || Math.Abs(z) > 10_000_000f)
        {
            detail = $"XYZ implausível ({x},{y},{z})";
            return false;
        }

        position = new PositionSnapshot(x, y, z);
        detail = $"inner=0x{inner.ToInt64():X}, core=0x{core.ToInt64():X}, pos=0x{posStruct.ToInt64():X}";
        return true;
    }

    private bool TryResolveActorStats(nint actor, out PlayerRuntime runtime, out string detail)
    {
        runtime = new PlayerRuntime();
        var attempts = new List<string>();

        if (_memory is null)
        {
            detail = "sem memória";
            return false;
        }

        // Caminho 1: actor + 0x58 -> stats component.
        if (_memory.TryReadPointer(actor + 0x58, out var directStats))
        {
            if (TryBuildRuntimeFromStatsBase(actor, directStats, out runtime, out var directDetail))
            {
                detail = $"actor+58 => {directDetail}";
                return true;
            }
            attempts.Add($"actor+58=0x{directStats.ToInt64():X}: {directDetail}");
        }
        else
        {
            attempts.Add("actor+58 inválido");
        }

        // Caminho 2: actor +20 -> marker +18 -> root +58 -> stats.
        if (_memory.TryReadPointer(actor + 0x20, out var marker)
            && _memory.TryReadPointer(marker + 0x18, out var root))
        {
            if (_memory.TryReadPointer(root + 0x58, out var rootedStats))
            {
                if (TryBuildRuntimeFromStatsBase(actor, rootedStats, out runtime, out var rootedDetail))
                {
                    runtime.Marker = marker;
                    runtime.Root = root;
                    detail = $"marker/root => {rootedDetail}";
                    return true;
                }
                attempts.Add($"root+58=0x{rootedStats.ToInt64():X}: {rootedDetail}");
            }
            else
            {
                attempts.Add($"marker=0x{marker.ToInt64():X}, root=0x{root.ToInt64():X}, root+58 inválido");
            }
        }
        else
        {
            attempts.Add("marker/root inválido");
        }

        detail = string.Join(" | ", attempts);
        return false;
    }

    private bool TryBuildRuntimeFromStatsBase(
        nint actor,
        nint statsBase,
        out PlayerRuntime runtime,
        out string detail)
    {
        runtime = new PlayerRuntime();

        foreach (var layout in StatLayouts)
        {
            var health = statsBase;
            var stamina = statsBase + layout.StaminaFromHealth;
            var spirit = statsBase + layout.SpiritFromHealth;

            if (!ValidateStatEntry(health, HealthId, out var hp)
                || !ValidateStatEntry(stamina, StaminaId, out var sta)
                || !ValidateStatEntry(spirit, SpiritId, out var spi))
                continue;

            runtime.IsResolved = true;
            runtime.Actor = actor;
            runtime.StatsBase = statsBase;
            runtime.HealthEntry = health;
            runtime.StaminaEntry = stamina;
            runtime.SpiritEntry = spirit;
            runtime.LayoutName = layout.Name;

            detail = $"Stats=0x{statsBase.ToInt64():X}; HP={FormatStat(hp)}, STA={FormatStat(sta)}, SPI={FormatStat(spi)}; {layout.Name}";
            return true;
        }

        detail = DescribeStatsBase(statsBase);
        return false;
    }

    private bool ValidateStatEntry(nint entry, int expectedType, out StatSnapshot snapshot)
    {
        snapshot = default;

        if (_memory is null || entry == 0 || !_memory.IsReadable(entry, 0x20))
            return false;

        if (!_memory.TryRead<int>(entry + StatTypeOffset, out var type) || type != expectedType)
            return false;

        if (!_memory.TryRead<long>(entry + StatCurrentOffset, out var current)
            || !_memory.TryRead<long>(entry + StatMaxOffset, out var max))
            return false;

        if (max <= 0 || max > 10_000_000_000_000L || current < 0 || current > max * 20L)
            return false;

        snapshot = new StatSnapshot(type, current, max);
        return true;
    }

    private string DescribeStatsBase(nint statsBase)
    {
        var parts = new List<string> { $"stats=0x{statsBase.ToInt64():X} ({DescribeAddress(statsBase)})" };
        DescribeRawEntry(parts, "HP+000", statsBase);
        DescribeRawEntry(parts, "STA+510", statsBase + 0x510);
        DescribeRawEntry(parts, "SPI+5A0", statsBase + 0x5A0);
        DescribeRawEntry(parts, "STAleg+480", statsBase + 0x480);
        return string.Join("; ", parts);
    }

    private void DescribeRawEntry(List<string> parts, string label, nint entry)
    {
        if (_memory is null || !_memory.IsReadable(entry, 0x20))
        {
            parts.Add($"{label}=ilegível");
            return;
        }

        _memory.TryRead<int>(entry, out var type);
        _memory.TryRead<long>(entry + 0x08, out var current);
        _memory.TryRead<long>(entry + 0x18, out var max);
        parts.Add($"{label}[type={type},cur={current},max={max}]");
    }

    private bool TryResolveViaPlayerBaseAob(out PlayerRuntime runtime, out string detail)
    {
        runtime = new PlayerRuntime();
        detail = string.Empty;

        if (_memory is null)
        {
            detail = "sem memória";
            return false;
        }

        var match = _memory.FindPatternInMainModule(PlayerBaseDiscoveryPattern);
        if (!match.HasValue)
        {
            detail = "assinatura não encontrada";
            return false;
        }

        var storage = _memory.ResolveRipRelative(match.Value, 3, 7);
        if (!_memory.TryReadPointer(storage, out var playerBase))
        {
            detail = $"storage 0x{storage.ToInt64():X} inválido";
            return false;
        }

        return TryResolveFromKnownPlayerBase(playerBase, out runtime, out detail);
    }

    private bool TryResolveViaStaticPlayerBase(out PlayerRuntime runtime, out string detail)
    {
        runtime = new PlayerRuntime();
        detail = string.Empty;

        if (_memory is null)
        {
            detail = "sem memória";
            return false;
        }

        var storage = _memory.MainModuleBase + StaticPlayerBaseRva;
        if (!_memory.TryReadPointer(storage, out var playerBase))
        {
            detail = $"RVA 0x{StaticPlayerBaseRva:X} inválido";
            return false;
        }

        return TryResolveFromKnownPlayerBase(playerBase, out runtime, out detail);
    }

    private bool TryResolveFromKnownPlayerBase(nint playerBase, out PlayerRuntime runtime, out string detail)
    {
        runtime = new PlayerRuntime();
        var attempts = new List<string>();

        foreach (var slot in PlayerBaseCharacterSlots)
        {
            if (!TryResolvePointerChain(playerBase, new[] { 0x18, 0xA0, 0xD0, slot }, out var candidate))
            {
                attempts.Add($"+0x{slot:X}: cadeia inválida");
                continue;
            }

            if (TryReadActorPosition(candidate, out var pos, out _)
                && TryResolveActorStats(candidate, out runtime, out var statsDetail))
            {
                runtime.Actor = candidate;
                runtime.Position = pos;
                detail = $"base=0x{playerBase.ToInt64():X}, slot=+0x{slot:X}, actor=0x{candidate.ToInt64():X}; {statsDetail}";
                return true;
            }

            attempts.Add($"+0x{slot:X}: actor=0x{candidate.ToInt64():X} não validou");
        }

        detail = $"base=0x{playerBase.ToInt64():X}; " + string.Join(" | ", attempts);
        return false;
    }

    private bool TryResolvePointerChain(nint baseAddress, IReadOnlyList<int> offsets, out nint value)
    {
        value = baseAddress;

        if (_memory is null || value == 0)
            return false;

        foreach (var offset in offsets)
        {
            if (!_memory.TryReadPointer(value + offset, out value))
                return false;
        }

        return value != 0;
    }

    private void AddAobDiagnostic(List<string> diagnostics, string name, string pattern)
    {
        if (_memory is null)
            return;

        var match = _memory.FindPatternInMainModule(pattern);
        diagnostics.Add(match.HasValue
            ? $"AOB {name}: 0x{match.Value.ToInt64():X} (RVA 0x{match.Value.ToInt64() - _memory.MainModuleBase.ToInt64():X})"
            : $"AOB {name}: não encontrado");
    }

    private void RestoreStat(nint entry, int expectedType, string label)
    {
        if (_memory is null || !ValidateStatEntry(entry, expectedType, out var stat))
        {
            InvalidateRuntime($"{label}: bloco de atributo deixou de validar. Relocalizando...");
            return;
        }

        if (stat.Current < stat.Max)
            _memory.Write(entry + StatCurrentOffset, stat.Max);
    }

    private bool ValidateRuntime()
    {
        return _runtime.IsResolved
               && ValidateStatEntry(_runtime.HealthEntry, HealthId, out _)
               && ValidateStatEntry(_runtime.StaminaEntry, StaminaId, out _)
               && ValidateStatEntry(_runtime.SpiritEntry, SpiritId, out _);
    }

    private void CompleteSuccessfulResolution(
        PlayerRuntime runtime,
        string method,
        List<string> diagnostics)
    {
        _runtime = runtime;
        diagnostics.Add($"Método vencedor: {method}");
        diagnostics.Add($"Actor: 0x{runtime.Actor.ToInt64():X} ({DescribeAddress(runtime.Actor)})");
        diagnostics.Add($"ActorManager slot: +0x{runtime.ActorManagerSlot:X}");
        diagnostics.Add($"Actor type: {(runtime.ActorType.HasValue ? $"0x{runtime.ActorType.Value:X2}" : "fallback")}");
        diagnostics.Add($"Position: {runtime.Position.X:F2}, {runtime.Position.Y:F2}, {runtime.Position.Z:F2}");
        diagnostics.Add($"StatsBase: 0x{runtime.StatsBase.ToInt64():X}");
        diagnostics.Add($"HealthEntry: 0x{runtime.HealthEntry.ToInt64():X}");
        diagnostics.Add($"StaminaEntry: 0x{runtime.StaminaEntry.ToInt64():X}");
        diagnostics.Add($"SpiritEntry: 0x{runtime.SpiritEntry.ToInt64():X}");

        DiagnosticReport = string.Join(Environment.NewLine, diagnostics);
        RuntimeStatus = $"Jogador localizado • {method} • {runtime.LayoutName}";
        LastError = string.Empty;
    }

    private string DescribeAddress(nint address)
    {
        if (_memory is null || address == 0)
            return "nulo";

        var value = address.ToInt64();
        var start = _memory.MainModuleBase.ToInt64();
        var end = start + _memory.MainModuleSize;

        if (value >= start && value < end)
            return $"módulo+0x{value - start:X}";

        return "memória dinâmica";
    }

    private bool IsInsideMainModule(nint address)
    {
        if (_memory is null)
            return false;

        var value = address.ToInt64();
        var start = _memory.MainModuleBase.ToInt64();
        var end = start + _memory.MainModuleSize;
        return value >= start && value < end;
    }

    private void InvalidateRuntime(
        string reason,
        bool scheduleRetry = true,
        bool preserveDiagnostic = false)
    {
        _runtime = new PlayerRuntime();
        RuntimeStatus = reason;
        LastError = reason;

        if (!preserveDiagnostic)
            DiagnosticReport = reason;

        if (scheduleRetry)
            _nextResolveAttemptUtc = DateTime.UtcNow.AddMilliseconds(500);
    }

    private static string FormatStat(StatSnapshot stat)
        => $"{stat.Current}/{stat.Max}";

    private readonly record struct WorldSystemPattern(
        string Name,
        string Signature,
        int DisplacementOffset,
        int InstructionEndOffset);

    private readonly record struct StatLayout(
        int StaminaFromHealth,
        int SpiritFromHealth,
        string Name);

    private readonly record struct StatSnapshot(
        int Type,
        long Current,
        long Max);

    private readonly record struct PositionSnapshot(float X, float Y, float Z);

    private sealed class PlayerRuntime
    {
        public bool IsResolved { get; set; }
        public string LayoutName { get; set; } = string.Empty;

        public nint WorldSystem { get; set; }
        public nint ActorManager { get; set; }
        public nint Actor { get; set; }
        public int ActorManagerSlot { get; set; }
        public byte? ActorType { get; set; }
        public PositionSnapshot Position { get; set; }

        public nint Marker { get; set; }
        public nint Root { get; set; }
        public nint StatsBase { get; set; }
        public nint HealthEntry { get; set; }
        public nint StaminaEntry { get; set; }
        public nint SpiritEntry { get; set; }
    }
}
