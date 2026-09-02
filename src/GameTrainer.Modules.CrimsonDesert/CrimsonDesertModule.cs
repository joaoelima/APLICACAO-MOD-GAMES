using GameTrainer.Core.Memory;
using GameTrainer.Core.Models;
using GameTrainer.Core.Modules;

namespace GameTrainer.Modules.CrimsonDesert;

public sealed class CrimsonDesertModule : IGameModule
{
    // ---------------------------------------------------------------------
    // Crimson Desert - resolução externa de atributos
    // ---------------------------------------------------------------------
    // Referências públicas cruzadas (CrimsonDesertCoop / bbfox CT):
    // WorldSystem -> ActorManager (+0x30) -> UserActor (+0x28)
    // UserActor +0x58 -> Stats Component
    //
    // StatEntry:
    // +0x00 int32  type (0=HP, 17=Stamina, 18=Spirit)
    // +0x08 int64  current
    // +0x18 int64  max
    //
    // Layout atual documentado em maio/2026:
    // HP entry      = stats + 0x000
    // Stamina entry = stats + 0x510
    // Spirit entry  = stats + 0x5A0
    //
    // Layout legado:
    // Stamina entry = stats + 0x480
    // Spirit entry  = stats + 0x510
    // ---------------------------------------------------------------------

    private const int HealthId = 0;
    private const int StaminaId = 17;
    private const int SpiritId = 18;

    private const int StatTypeOffset = 0x00;
    private const int StatCurrentOffset = 0x08;
    private const int StatMaxOffset = 0x18;

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

    private const string ChildActorPattern =
        "48 8B 47 68 48 8B 88 38 01 00 00 80";

    private static readonly WorldSystemPattern[] WorldSystemPatterns =
    {
        new("P1", "48 83 EC 28 48 8B 0D ? ? ? ? 48 8B 49 ? E8 ? ? ? ? 84 C0 0F 94 C0 48 83 C4 28 C3", 7, 11),
        new("P2", "80 B8 ? ? ? ? 00 75 ? 48 8B 05 ? ? ? ? 48 8B 88 ? ? ? ?", 12, 16),
        new("P3", "48 8B 0D ? ? ? ? 48 8B 49 ? E8 ? ? ? ? 84 C0 0F 94 C0", 3, 7)
    };

    private static readonly int[] PlayerBaseCharacterSlots =
    {
        0x68,  // Kliff / personagem principal nas tabelas públicas
        0xE0,
        0x168,
        0x268
    };

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
            "Diagnóstico v0.2.5",
            $"Módulo: 0x{_memory.MainModuleBase.ToInt64():X} / 0x{_memory.MainModuleSize:X} bytes",
            "Estratégia: WorldSystem -> UserActor -> +0x58 Stats Component (StatEntry Int64)",
            "Layout prioritário: HP +08/+18, STA +518/+528, SPI +5A8/+5B8"
        };

        AddAobDiagnostic(diagnostics, "CurrentPlayer", CurrentPlayerPattern);
        AddAobDiagnostic(diagnostics, "PlayerBaseDiscovery", PlayerBaseDiscoveryPattern);
        AddAobDiagnostic(diagnostics, "ChildActor", ChildActorPattern);

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

                diagnostics.Add($"WorldSystem {pattern.Name}: match=0x{match.Value.ToInt64():X}, slot=0x{globalSlot.ToInt64():X}");

                if (!_memory.TryReadPointer(globalSlot, out var worldSystem))
                {
                    diagnostics.Add($"WorldSystem {pattern.Name}: ponteiro global inválido");
                    continue;
                }

                diagnostics.Add($"WorldSystem {pattern.Name}: WorldSystem=0x{worldSystem.ToInt64():X} ({DescribeAddress(worldSystem)})");

                if (!_memory.TryReadPointer(worldSystem + 0x30, out var actorManager))
                {
                    diagnostics.Add($"WorldSystem {pattern.Name}: ActorManager +0x30 inválido");
                    continue;
                }

                diagnostics.Add($"WorldSystem {pattern.Name}: ActorManager=0x{actorManager.ToInt64():X} ({DescribeAddress(actorManager)})");

                if (!_memory.TryReadPointer(actorManager + 0x28, out var actor))
                {
                    diagnostics.Add($"WorldSystem {pattern.Name}: UserActor +0x28 inválido");
                    continue;
                }

                diagnostics.Add($"WorldSystem {pattern.Name}: UserActor=0x{actor.ToInt64():X} ({DescribeAddress(actor)})");
                diagnostics.Add($"WorldSystem {pattern.Name}: {DescribeActor(actor)}");

                if (TryResolveDirectStats(actor, out var directRuntime, out var directDetail))
                {
                    directRuntime.WorldSystem = worldSystem;
                    directRuntime.ActorManager = actorManager;
                    directRuntime.Actor = actor;
                    CompleteSuccessfulResolution(
                        directRuntime,
                        $"WorldSystem {pattern.Name} -> Actor+58",
                        diagnostics,
                        directDetail);
                    return true;
                }

                diagnostics.Add($"WorldSystem {pattern.Name}: Actor+58 -> {directDetail}");

                if (TryResolveViaMarker(actor, out var markerRuntime, out var markerDetail))
                {
                    markerRuntime.WorldSystem = worldSystem;
                    markerRuntime.ActorManager = actorManager;
                    markerRuntime.Actor = actor;
                    CompleteSuccessfulResolution(
                        markerRuntime,
                        $"WorldSystem {pattern.Name} -> Marker",
                        diagnostics,
                        markerDetail);
                    return true;
                }

                diagnostics.Add($"WorldSystem {pattern.Name}: Marker -> {markerDetail}");
            }

            if (TryResolveViaPlayerBaseAob(out var aobRuntime, out var aobDetail))
            {
                CompleteSuccessfulResolution(aobRuntime, "PlayerBase AOB", diagnostics, aobDetail);
                return true;
            }
            diagnostics.Add($"PlayerBase AOB: {aobDetail}");

            if (TryResolveViaStaticPlayerBase(out var staticRuntime, out var staticDetail))
            {
                CompleteSuccessfulResolution(staticRuntime, "PlayerBase estático", diagnostics, staticDetail);
                return true;
            }
            diagnostics.Add($"PlayerBase estático: {staticDetail}");

            DiagnosticReport = string.Join(Environment.NewLine, diagnostics);
            InvalidateRuntime(
                "O UserActor foi localizado, mas o Stats Component desta build ainda não validou. Use “Copiar diagnóstico”.",
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

    private void AddAobDiagnostic(List<string> diagnostics, string name, string pattern)
    {
        if (_memory is null)
            return;

        var match = _memory.FindPatternInMainModule(pattern);
        diagnostics.Add(match.HasValue
            ? $"AOB {name}: 0x{match.Value.ToInt64():X} (RVA 0x{match.Value.ToInt64() - _memory.MainModuleBase.ToInt64():X})"
            : $"AOB {name}: não encontrado");
    }

    private bool TryResolveDirectStats(nint actor, out PlayerRuntime runtime, out string detail)
    {
        runtime = new PlayerRuntime();

        if (_memory is null)
        {
            detail = "memória indisponível";
            return false;
        }

        if (!_memory.TryReadPointer(actor + 0x58, out var statsBase))
        {
            detail = "actor+0x58 não contém ponteiro legível";
            return false;
        }

        return TryBuildRuntimeFromStatsBase(actor, statsBase, out runtime, out detail);
    }

    private bool TryResolveViaMarker(nint actor, out PlayerRuntime runtime, out string detail)
    {
        runtime = new PlayerRuntime();

        if (_memory is null)
        {
            detail = "memória indisponível";
            return false;
        }

        if (!_memory.TryReadPointer(actor + 0x20, out var marker))
        {
            detail = "actor+0x20 Marker inválido";
            return false;
        }

        if (!_memory.TryReadPointer(marker + 0x18, out var root))
        {
            detail = $"Marker=0x{marker.ToInt64():X}, marker+0x18 Root inválido";
            return false;
        }

        if (!_memory.TryReadPointer(root + 0x58, out var statsBase))
        {
            detail = $"Marker=0x{marker.ToInt64():X}, Root=0x{root.ToInt64():X}, root+0x58 inválido";
            return false;
        }

        if (!TryBuildRuntimeFromStatsBase(actor, statsBase, out runtime, out var statsDetail))
        {
            detail = $"Marker=0x{marker.ToInt64():X}, Root=0x{root.ToInt64():X}, Stats=0x{statsBase.ToInt64():X}; {statsDetail}";
            return false;
        }

        runtime.Marker = marker;
        runtime.Root = root;
        detail = $"Marker=0x{marker.ToInt64():X}, Root=0x{root.ToInt64():X}; {statsDetail}";
        return true;
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

            detail = $"Stats=0x{statsBase.ToInt64():X} ({DescribeAddress(statsBase)}); " +
                     $"HP={FormatStat(hp)}, STA={FormatStat(sta)}, SPI={FormatStat(spi)}; layout={layout.Name}";
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

        if (!IsPlausibleStat(current, max, expectedType == HealthId))
            return false;

        snapshot = new StatSnapshot(type, current, max);
        return true;
    }

    private static bool IsPlausibleStat(long current, long max, bool requirePositiveMax)
    {
        if (max < 0 || max > 10_000_000_000_000L)
            return false;

        if (requirePositiveMax && max == 0)
            return false;

        if (current < 0 || current > 100_000_000_000_000L)
            return false;

        if (max == 0)
            return current == 0;

        var upper = Math.Min(100_000_000_000_000L, max * 20L);
        return current <= upper;
    }

    private string DescribeStatsBase(nint statsBase)
    {
        if (_memory is null)
            return "memória indisponível";

        var parts = new List<string>
        {
            $"Stats candidato=0x{statsBase.ToInt64():X} ({DescribeAddress(statsBase)})"
        };

        DescribeRawEntry(parts, "HP@+000", statsBase + 0x000);
        DescribeRawEntry(parts, "STA@+510", statsBase + 0x510);
        DescribeRawEntry(parts, "SPI@+5A0", statsBase + 0x5A0);
        DescribeRawEntry(parts, "STA-leg@+480", statsBase + 0x480);
        DescribeRawEntry(parts, "SPI-leg@+510", statsBase + 0x510);

        return string.Join("; ", parts);
    }

    private void DescribeRawEntry(List<string> parts, string label, nint entry)
    {
        if (_memory is null || !_memory.IsReadable(entry, 0x20))
        {
            parts.Add($"{label}=ilegível");
            return;
        }

        var hasType = _memory.TryRead<int>(entry, out var type);
        var hasCurrent = _memory.TryRead<long>(entry + 0x08, out var current);
        var hasMax = _memory.TryRead<long>(entry + 0x18, out var max);

        parts.Add($"{label}[type={(hasType ? type.ToString() : "?")}, cur={(hasCurrent ? current.ToString() : "?")}, max={(hasMax ? max.ToString() : "?")}] ");
    }

    private string DescribeActor(nint actor)
    {
        if (_memory is null)
            return "Actor: memória indisponível";

        var parts = new List<string>();

        if (_memory.TryReadPointer(actor, out var vtable))
            parts.Add($"vtable=0x{vtable.ToInt64():X} ({DescribeAddress(vtable)})");
        else
            parts.Add("vtable=inválida");

        if (_memory.TryReadPointer(actor + 0x20, out var marker))
            parts.Add($"+20=0x{marker.ToInt64():X} ({DescribeAddress(marker)})");
        else
            parts.Add("+20=inválido");

        if (_memory.TryReadPointer(actor + 0x40, out var inner))
            parts.Add($"+40=0x{inner.ToInt64():X} ({DescribeAddress(inner)})");
        else
            parts.Add("+40=inválido");

        if (_memory.TryReadPointer(actor + 0x58, out var stats))
            parts.Add($"+58=0x{stats.ToInt64():X} ({DescribeAddress(stats)})");
        else
            parts.Add("+58=inválido");

        return "Actor probe: " + string.Join(", ", parts);
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

    private bool TryResolveViaPlayerBaseAob(out PlayerRuntime runtime, out string detail)
    {
        runtime = new PlayerRuntime();
        detail = string.Empty;

        if (_memory is null)
        {
            detail = "memória indisponível";
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
            detail = $"storage=0x{storage.ToInt64():X}, ponteiro base inválido";
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
            detail = "memória indisponível";
            return false;
        }

        var storage = _memory.MainModuleBase + StaticPlayerBaseRva;
        if (!_memory.TryReadPointer(storage, out var playerBase))
        {
            detail = $"RVA 0x{StaticPlayerBaseRva:X} não contém base válida";
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
            if (!TryResolvePointerChain(playerBase, new[] { 0x18, 0xA0, 0xD0, slot }, out var actor))
            {
                attempts.Add($"slot +0x{slot:X}: cadeia inválida");
                continue;
            }

            if (TryResolveDirectStats(actor, out runtime, out var directDetail))
            {
                runtime.Actor = actor;
                detail = $"PlayerBase=0x{playerBase.ToInt64():X}, slot=+0x{slot:X}, Actor=0x{actor.ToInt64():X}; {directDetail}";
                return true;
            }

            attempts.Add($"slot +0x{slot:X}: Actor=0x{actor.ToInt64():X}; {directDetail}");
        }

        detail = $"PlayerBase=0x{playerBase.ToInt64():X}; " + string.Join(" | ", attempts);
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

    private void RestoreStat(nint entry, int expectedType, string label)
    {
        if (_memory is null || !ValidateStatEntry(entry, expectedType, out var stat))
        {
            InvalidateRuntime($"{label}: bloco de atributo deixou de validar. Relocalizando...");
            return;
        }

        if (stat.Max <= 0)
            return;

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
        List<string> diagnostics,
        string detail)
    {
        _runtime = runtime;
        diagnostics.Add($"Método vencedor: {method}");
        diagnostics.Add(detail);
        diagnostics.Add($"Actor: 0x{runtime.Actor.ToInt64():X}");
        diagnostics.Add($"StatsBase: 0x{runtime.StatsBase.ToInt64():X}");
        diagnostics.Add($"HealthEntry: 0x{runtime.HealthEntry.ToInt64():X}");
        diagnostics.Add($"StaminaEntry: 0x{runtime.StaminaEntry.ToInt64():X}");
        diagnostics.Add($"SpiritEntry: 0x{runtime.SpiritEntry.ToInt64():X}");

        DiagnosticReport = string.Join(Environment.NewLine, diagnostics);
        RuntimeStatus = $"Jogador localizado • {method} • {runtime.LayoutName}";
        LastError = string.Empty;
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

    private static string FormatStat(StatSnapshot snapshot)
        => $"type={snapshot.Type}, {snapshot.Current}/{snapshot.Max}";

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

    private sealed class PlayerRuntime
    {
        public bool IsResolved { get; set; }
        public string LayoutName { get; set; } = string.Empty;

        public nint WorldSystem { get; set; }
        public nint ActorManager { get; set; }
        public nint Actor { get; set; }
        public nint Marker { get; set; }
        public nint Root { get; set; }
        public nint StatsBase { get; set; }

        public nint HealthEntry { get; set; }
        public nint StaminaEntry { get; set; }
        public nint SpiritEntry { get; set; }
    }
}
