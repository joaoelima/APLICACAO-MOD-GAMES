using GameTrainer.Core.Memory;
using GameTrainer.Core.Models;
using GameTrainer.Core.Modules;

namespace GameTrainer.Modules.CrimsonDesert;

public sealed class CrimsonDesertModule : IGameModule
{
    // ---------------------------------------------------------------------
    // Crimson Desert - layouts conhecidos
    // ---------------------------------------------------------------------
    // Layout atual (bbfox CT, atualização 2026-08-01):
    // ServerUserActor -> char slot -> +68 -> +20 -> +18 -> +58 -> +0
    // HP:     current +08 / max +18
    // Stamina current +6C8 / max +6D8
    // Spirit  current +758 / max +768
    // Todos como DWORD. A tabela pública repõe current = max * 10.
    // ---------------------------------------------------------------------
    private const int CurrentHpOffset2026 = 0x08;
    private const int MaxHpOffset2026 = 0x18;
    private const int CurrentStaminaOffset2026 = 0x6C8;
    private const int MaxStaminaOffset2026 = 0x6D8;
    private const int CurrentSpiritOffset2026 = 0x758;
    private const int MaxSpiritOffset2026 = 0x768;
    private const int CurrentValueScale2026 = 10;

    // Character slots vistos na tabela atual.
    private static readonly int[] CurrentCharacterSlots = { 0xD0, 0xD8, 0xE0 };

    // Layout legado: StatEntry com id + current/max em Int64.
    private const int HealthId = 0;
    private const int StaminaId = 17;
    private const int SpiritId = 18;
    private const int LegacyCurrentValueOffset = 0x08;
    private const int LegacyMaxValueOffset = 0x18;
    private static readonly LegacyStatLayout[] LegacyLayouts =
    {
        new(0x510, 0x5A0, "legado-mai-2026"),
        new(0x480, 0x510, "legado-v1.01")
    };

    // AOB do fluxo de "Get current char pointer" publicado em 2026-08-01.
    // Aqui ele serve apenas como confirmação da família da build; não fazemos
    // injeção/hook para capturar RBX.
    private const string CurrentPlayerDataPattern =
        "48 8B 53 08 48 8D 4C 24 78 E8 ? ? ? ? 90 48 8B 43 68 48 8B 88 A0 01 00 00 48 8B 41 38 0F B7 48 20";

    private const int StaticPlayerBaseRva = 0x05CC7618;
    private const string PlayerBaseDiscoveryPattern =
        "48 8B 0D ? ? ? ? E8 ? ? ? ? 41 B0 01 48 8B 53 08 48 8D 4C 24 40";

    private static readonly WorldSystemPattern[] WorldSystemPatterns =
    {
        new("P1", "48 83 EC 28 48 8B 0D ? ? ? ? 48 8B 49 ? E8 ? ? ? ? 84 C0 0F 94 C0 48 83 C4 28 C3", 7, 11),
        new("P2", "80 B8 ? ? ? ? 00 75 ? 48 8B 05 ? ? ? ? 48 8B 88 ? ? ? ?", 12, 16),
        new("P3", "48 8B 0D ? ? ? ? 48 8B 49 ? E8 ? ? ? ? 84 C0 0F 94 C0", 3, 7)
    };

    private ProcessMemory? _memory;
    private PlayerRuntime _runtime = new();
    private DateTime _nextResolveAttemptUtc = DateTime.MinValue;

    private bool _infiniteHealth;
    private bool _infiniteStamina;
    private bool _infiniteSpirit;
    private bool _oneHitKill;
    private int? _originalAttack;

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
                        Description = "Eleva temporariamente o atributo de ataque do personagem. Recurso experimental.",
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
        _originalAttack = null;
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

        RestoreOriginalAttack();
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

        if (enabled && !EnsureRuntime())
            return Task.FromResult(false);

        var success = featureId switch
        {
            "infinite-health" => SetFlag(ref _infiniteHealth, enabled),
            "infinite-stamina" => SetFlag(ref _infiniteStamina, enabled),
            "infinite-spirit" => SetFlag(ref _infiniteSpirit, enabled),
            "one-hit-kill" => SetOneHitKill(enabled),
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

        if (!_infiniteHealth && !_infiniteStamina && !_infiniteSpirit && !_oneHitKill)
            return Task.CompletedTask;

        if (!EnsureRuntime())
            return Task.CompletedTask;

        try
        {
            if (_runtime.Format == StatFormat.CurrentDword2026)
            {
                if (_infiniteHealth)
                    RestoreCurrentDwordStat(_runtime.HealthCurrent, _runtime.HealthMax, "Vida");
                if (_infiniteStamina)
                    RestoreCurrentDwordStat(_runtime.StaminaCurrent, _runtime.StaminaMax, "Vigor");
                if (_infiniteSpirit)
                    RestoreCurrentDwordStat(_runtime.SpiritCurrent, _runtime.SpiritMax, "Espírito");
            }
            else
            {
                if (_infiniteHealth)
                    RestoreLegacyStat(_runtime.HealthEntry, HealthId);
                if (_infiniteStamina)
                    RestoreLegacyStat(_runtime.StaminaEntry, StaminaId);
                if (_infiniteSpirit)
                    RestoreLegacyStat(_runtime.SpiritEntry, SpiritId);
            }

            if (_oneHitKill)
                ApplySuperDamage();
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

    // ---------------------------------------------------------------------
    // Runtime resolution
    // ---------------------------------------------------------------------
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
            "Diagnóstico v0.2.4",
            $"Módulo: 0x{_memory.MainModuleBase.ToInt64():X} / 0x{_memory.MainModuleSize:X} bytes",
            "Layout prioritário: tabela pública 2026-08-01 (DWORD / HP+08, STA+6C8, SPI+758)"
        };

        var currentAob = _memory.FindPatternInMainModule(CurrentPlayerDataPattern);
        diagnostics.Add(currentAob.HasValue
            ? $"AOB CurrentPlayer 2026-08-01: encontrado em 0x{currentAob.Value.ToInt64():X}"
            : "AOB CurrentPlayer 2026-08-01: não encontrado");

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
                    diagnostics.Add($"WorldSystem {pattern.Name}: ponteiro RIP inválido");
                    continue;
                }

                if (!_memory.TryReadPointer(worldSystem + 0x30, out var actorManager))
                {
                    diagnostics.Add($"WorldSystem {pattern.Name}: ActorManager +0x30 inválido");
                    continue;
                }

                if (!_memory.TryReadPointer(actorManager + 0x28, out var actor))
                {
                    diagnostics.Add($"WorldSystem {pattern.Name}: UserActor +0x28 inválido");
                    continue;
                }

                diagnostics.Add($"WorldSystem {pattern.Name}: Actor 0x{actor.ToInt64():X}");

                // Primeiro tenta exatamente a cadeia da tabela atual.
                if (TryResolveCurrent2026FromServerUserActor(actor, out var currentRuntime, out var currentDetail))
                {
                    currentRuntime.WorldSystem = worldSystem;
                    currentRuntime.ActorManager = actorManager;
                    currentRuntime.Actor = actor;
                    CompleteSuccessfulResolution(currentRuntime, $"WorldSystem {pattern.Name}", diagnostics, currentDetail);
                    return true;
                }

                diagnostics.Add($"WorldSystem {pattern.Name}: layout 2026 -> {currentDetail}");

                // Fallback: estrutura antiga actor->marker->root.
                if (TryResolveLegacyFromActor(actor, out var legacyRuntime, out var legacyDetail))
                {
                    legacyRuntime.WorldSystem = worldSystem;
                    legacyRuntime.ActorManager = actorManager;
                    legacyRuntime.Actor = actor;
                    CompleteSuccessfulResolution(legacyRuntime, $"WorldSystem {pattern.Name} legado", diagnostics, legacyDetail);
                    return true;
                }

                diagnostics.Add($"WorldSystem {pattern.Name}: legado -> {legacyDetail}");
            }

            // Mantém os caminhos antigos como última tentativa.
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
                "Actor localizado, mas a cadeia de atributos da build ainda não validou. Use “Copiar diagnóstico”.",
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

    private bool TryResolveCurrent2026FromServerUserActor(
        nint serverUserActor,
        out PlayerRuntime runtime,
        out string detail)
    {
        runtime = new PlayerRuntime();
        detail = string.Empty;

        if (_memory is null)
        {
            detail = "memória indisponível";
            return false;
        }

        var attempts = new List<string>();

        foreach (var slot in CurrentCharacterSlots)
        {
            // Tabela atual:
            // ServerUserActor + slot -> +68 -> +20 -> +18 -> +58 -> +0
            if (TryResolvePointerChain(
                    serverUserActor,
                    new[] { slot, 0x68, 0x20, 0x18, 0x58, 0x00 },
                    out var statsBase)
                && TryBuildCurrentRuntime(statsBase, slot, out runtime, out var values))
            {
                detail = $"slot +0x{slot:X}, cadeia completa com +0 final; {values}";
                return true;
            }

            // Algumas representações do CE deixam o +0 apenas como o endereço final,
            // sem uma dereferência adicional. Testamos as duas formas e validamos valores.
            if (TryResolvePointerChain(
                    serverUserActor,
                    new[] { slot, 0x68, 0x20, 0x18, 0x58 },
                    out statsBase)
                && TryBuildCurrentRuntime(statsBase, slot, out runtime, out values))
            {
                detail = $"slot +0x{slot:X}, cadeia sem +0 final; {values}";
                return true;
            }

            attempts.Add($"+0x{slot:X}: não validou");
        }

        detail = string.Join("; ", attempts);
        return false;
    }

    private bool TryBuildCurrentRuntime(
        nint statsBase,
        int characterSlot,
        out PlayerRuntime runtime,
        out string values)
    {
        runtime = new PlayerRuntime();
        values = string.Empty;

        if (!ValidateCurrentStatsBase(statsBase, out var snapshot))
            return false;

        runtime.IsResolved = true;
        runtime.Format = StatFormat.CurrentDword2026;
        runtime.StatsBase = statsBase;
        runtime.CharacterSlot = characterSlot;

        runtime.HealthCurrent = statsBase + CurrentHpOffset2026;
        runtime.HealthMax = statsBase + MaxHpOffset2026;
        runtime.StaminaCurrent = statsBase + CurrentStaminaOffset2026;
        runtime.StaminaMax = statsBase + MaxStaminaOffset2026;
        runtime.SpiritCurrent = statsBase + CurrentSpiritOffset2026;
        runtime.SpiritMax = statsBase + MaxSpiritOffset2026;

        values = $"StatsBase 0x{statsBase.ToInt64():X}; " +
                 $"HP {snapshot.HpCurrent}/{snapshot.HpMax}; " +
                 $"STA {snapshot.StaminaCurrent}/{snapshot.StaminaMax}; " +
                 $"SPI {snapshot.SpiritCurrent}/{snapshot.SpiritMax}";
        return true;
    }

    private bool ValidateCurrentStatsBase(nint statsBase, out CurrentStatsSnapshot snapshot)
    {
        snapshot = default;
        if (_memory is null || statsBase == 0 || !_memory.IsReadable(statsBase, MaxSpiritOffset2026 + sizeof(int)))
            return false;

        if (!_memory.TryRead<int>(statsBase + CurrentHpOffset2026, out var hpCur)
            || !_memory.TryRead<int>(statsBase + MaxHpOffset2026, out var hpMax)
            || !_memory.TryRead<int>(statsBase + CurrentStaminaOffset2026, out var staCur)
            || !_memory.TryRead<int>(statsBase + MaxStaminaOffset2026, out var staMax)
            || !_memory.TryRead<int>(statsBase + CurrentSpiritOffset2026, out var spiCur)
            || !_memory.TryRead<int>(statsBase + MaxSpiritOffset2026, out var spiMax))
            return false;

        if (!IsPlausibleCurrent2026(hpCur, hpMax)
            || !IsPlausibleCurrent2026(staCur, staMax)
            || !IsPlausibleCurrent2026(spiCur, spiMax))
            return false;

        snapshot = new CurrentStatsSnapshot(hpCur, hpMax, staCur, staMax, spiCur, spiMax);
        return true;
    }

    private static bool IsPlausibleCurrent2026(int current, int max)
    {
        if (max <= 0 || max > 100_000_000)
            return false;
        if (current < 0)
            return false;

        // A tabela atual usa current = max * 10. Aceitamos folga para buffs.
        var upperBound = Math.Max((long)max * 30L, (long)max + 10_000L);
        return current <= upperBound;
    }

    // ---------------------------------------------------------------------
    // Reposição de atributos
    // ---------------------------------------------------------------------
    private void RestoreCurrentDwordStat(nint currentAddress, nint maxAddress, string label)
    {
        if (_memory is null
            || !_memory.TryRead<int>(maxAddress, out var max)
            || max <= 0
            || max > 100_000_000)
        {
            InvalidateRuntime($"{label}: valor máximo inválido. Relocalizando...");
            return;
        }

        var desired64 = (long)max * CurrentValueScale2026;
        if (desired64 <= 0 || desired64 > int.MaxValue)
        {
            InvalidateRuntime($"{label}: valor calculado fora da faixa segura. Relocalizando...");
            return;
        }

        var desired = (int)desired64;
        if (!_memory.TryRead<int>(currentAddress, out var current))
        {
            InvalidateRuntime($"{label}: não foi possível ler o valor atual. Relocalizando...");
            return;
        }

        if (current < desired)
            _memory.Write(currentAddress, desired);
    }

    // ---------------------------------------------------------------------
    // Fallback legado
    // ---------------------------------------------------------------------
    private bool TryResolveLegacyFromActor(nint actor, out PlayerRuntime runtime, out string detail)
    {
        runtime = new PlayerRuntime();
        detail = string.Empty;

        if (_memory is null)
        {
            detail = "memória indisponível";
            return false;
        }

        if (!_memory.TryReadPointer(actor + 0x20, out var marker))
        {
            detail = "Marker +0x20 inválido";
            return false;
        }

        if (!_memory.TryReadPointer(marker + 0x18, out var root))
        {
            detail = "Root +0x18 inválido";
            return false;
        }

        nint healthEntry = 0;
        if (_memory.TryReadPointer(root + 0x58, out var rooted) && ValidateLegacyStatEntry(rooted, HealthId))
            healthEntry = rooted;
        else if (_memory.TryReadPointer(actor + 0x58, out var direct) && ValidateLegacyStatEntry(direct, HealthId))
            healthEntry = direct;
        else
        {
            detail = "nenhuma entrada Health/Int64 válida em +0x58";
            return false;
        }

        foreach (var layout in LegacyLayouts)
        {
            var stamina = healthEntry + layout.StaminaFromHealth;
            var spirit = healthEntry + layout.SpiritFromHealth;

            if (!ValidateLegacyStatEntry(stamina, StaminaId)
                || !ValidateLegacyStatEntry(spirit, SpiritId))
                continue;

            runtime.IsResolved = true;
            runtime.Format = StatFormat.LegacyQword;
            runtime.Marker = marker;
            runtime.Root = root;
            runtime.HealthEntry = healthEntry;
            runtime.StaminaEntry = stamina;
            runtime.SpiritEntry = spirit;
            runtime.LayoutName = layout.Name;
            detail = $"Health 0x{healthEntry.ToInt64():X}; layout {layout.Name}";
            return true;
        }

        detail = "Health legado encontrado, mas Stamina/Spirit não validaram";
        return false;
    }

    private void RestoreLegacyStat(nint entry, int expectedType)
    {
        if (_memory is null || !ValidateLegacyStatEntry(entry, expectedType))
        {
            InvalidateRuntime("Os atributos legados mudaram. Relocalizando...");
            return;
        }

        var max = _memory.Read<long>(entry + LegacyMaxValueOffset);
        _memory.Write(entry + LegacyCurrentValueOffset, max);
    }

    private bool ValidateLegacyStatEntry(nint entry, int expectedType)
    {
        if (_memory is null || entry == 0 || !_memory.IsReadable(entry, 0x20))
            return false;

        if (!_memory.TryRead<int>(entry, out var type) || type != expectedType)
            return false;

        if (!_memory.TryRead<long>(entry + LegacyCurrentValueOffset, out var current)
            || !_memory.TryRead<long>(entry + LegacyMaxValueOffset, out var max))
            return false;

        return max > 0
               && max < 10_000_000_000_000L
               && current >= 0
               && current <= max * 8;
    }

    // ---------------------------------------------------------------------
    // PlayerBase fallbacks
    // ---------------------------------------------------------------------
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
            detail = "storage/base inválido";
            return false;
        }

        return TryResolveKnownPlayerBase(playerBase, out runtime, out detail);
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

        return TryResolveKnownPlayerBase(playerBase, out runtime, out detail);
    }

    private bool TryResolveKnownPlayerBase(nint playerBase, out PlayerRuntime runtime, out string detail)
    {
        runtime = new PlayerRuntime();
        detail = string.Empty;

        // Caminho antigo: base +18 -> +A0 -> +D0 -> slot.
        foreach (var slot in new[] { 0x68, 0xE0, 0x168, 0x268 })
        {
            if (!TryResolvePointerChain(playerBase, new[] { 0x18, 0xA0, 0xD0, slot }, out var actor))
                continue;

            if (TryResolveCurrent2026FromServerUserActor(actor, out runtime, out var currentDetail))
            {
                runtime.Actor = actor;
                detail = $"base antiga -> actor + slot 0x{slot:X}; {currentDetail}";
                return true;
            }

            if (TryResolveLegacyFromActor(actor, out runtime, out var legacyDetail))
            {
                runtime.Actor = actor;
                detail = $"base antiga -> actor + slot 0x{slot:X}; {legacyDetail}";
                return true;
            }
        }

        detail = "base encontrada, mas nenhuma cadeia conhecida validou";
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

    // ---------------------------------------------------------------------
    // Super dano - permanece experimental
    // ---------------------------------------------------------------------
    private bool SetOneHitKill(bool enabled)
    {
        if (!enabled)
        {
            _oneHitKill = false;
            RestoreOriginalAttack();
            LastError = string.Empty;
            return true;
        }

        if (!TryResolveAttack(out var attackAddress, out var attackValue))
        {
            LastError = "Super Dano ainda não validou o atributo de ataque nesta build.";
            return false;
        }

        _runtime.AttackAddress = attackAddress;
        _originalAttack ??= attackValue;
        _oneHitKill = true;
        ApplySuperDamage();
        LastError = string.Empty;
        return true;
    }

    private bool TryResolveAttack(out nint address, out int value)
    {
        address = 0;
        value = 0;

        if (_memory is null || _runtime.Root == 0)
            return false;

        if (!_memory.TryReadPointer(_runtime.Root + 0x38, out var component))
            return false;

        if (!_memory.TryRead<int>(component, out var current)
            || current <= 0
            || current > 5_000_000)
            return false;

        address = component;
        value = current;
        return true;
    }

    private void ApplySuperDamage()
    {
        if (_memory is null || !_runtime.IsResolved)
            return;

        if (_runtime.AttackAddress == 0)
        {
            if (!TryResolveAttack(out var address, out var original))
                return;

            _runtime.AttackAddress = address;
            _originalAttack ??= original;
        }

        if (!_memory.TryRead<int>(_runtime.AttackAddress, out var current)
            || current <= 0
            || current > 50_000_000)
        {
            _runtime.AttackAddress = 0;
            return;
        }

        _originalAttack ??= current;
        const int boostedAttack = 5_000_000;
        if (current != boostedAttack)
            _memory.Write(_runtime.AttackAddress, boostedAttack);
    }

    private void RestoreOriginalAttack()
    {
        if (_memory is null
            || !_memory.IsAttached
            || _runtime.AttackAddress == 0
            || !_originalAttack.HasValue)
            return;

        try
        {
            _memory.Write(_runtime.AttackAddress, _originalAttack.Value);
        }
        catch
        {
        }
        finally
        {
            _originalAttack = null;
        }
    }

    // ---------------------------------------------------------------------
    // Runtime validation / diagnostics
    // ---------------------------------------------------------------------
    private bool ValidateRuntime()
    {
        if (_memory is null || !_runtime.IsResolved)
            return false;

        if (_runtime.Format == StatFormat.CurrentDword2026)
            return ValidateCurrentStatsBase(_runtime.StatsBase, out _);

        return ValidateLegacyStatEntry(_runtime.HealthEntry, HealthId)
               && ValidateLegacyStatEntry(_runtime.StaminaEntry, StaminaId)
               && ValidateLegacyStatEntry(_runtime.SpiritEntry, SpiritId);
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

        if (runtime.Format == StatFormat.CurrentDword2026)
        {
            diagnostics.Add($"StatsBase: 0x{runtime.StatsBase.ToInt64():X}");
            diagnostics.Add($"HP current/max: 0x{runtime.HealthCurrent.ToInt64():X} / 0x{runtime.HealthMax.ToInt64():X}");
            diagnostics.Add($"STA current/max: 0x{runtime.StaminaCurrent.ToInt64():X} / 0x{runtime.StaminaMax.ToInt64():X}");
            diagnostics.Add($"SPI current/max: 0x{runtime.SpiritCurrent.ToInt64():X} / 0x{runtime.SpiritMax.ToInt64():X}");
            runtime.LayoutName = "DWORD-2026-08";
        }
        else
        {
            diagnostics.Add($"HealthEntry: 0x{runtime.HealthEntry.ToInt64():X}");
            diagnostics.Add($"StaminaEntry: 0x{runtime.StaminaEntry.ToInt64():X}");
            diagnostics.Add($"SpiritEntry: 0x{runtime.SpiritEntry.ToInt64():X}");
        }

        DiagnosticReport = string.Join(Environment.NewLine, diagnostics);
        RuntimeStatus = $"Jogador localizado • {method} • {runtime.LayoutName}";
        LastError = string.Empty;
    }

    private void InvalidateRuntime(
        string reason,
        bool scheduleRetry = true,
        bool preserveDiagnostic = false)
    {
        RestoreOriginalAttack();
        _runtime = new PlayerRuntime();
        RuntimeStatus = reason;
        LastError = reason;

        if (!preserveDiagnostic)
            DiagnosticReport = reason;

        if (scheduleRetry)
            _nextResolveAttemptUtc = DateTime.UtcNow.AddMilliseconds(500);
    }

    private readonly record struct WorldSystemPattern(
        string Name,
        string Signature,
        int DisplacementOffset,
        int InstructionEndOffset);

    private readonly record struct LegacyStatLayout(
        int StaminaFromHealth,
        int SpiritFromHealth,
        string Name);

    private readonly record struct CurrentStatsSnapshot(
        int HpCurrent,
        int HpMax,
        int StaminaCurrent,
        int StaminaMax,
        int SpiritCurrent,
        int SpiritMax);

    private enum StatFormat
    {
        None,
        CurrentDword2026,
        LegacyQword
    }

    private sealed class PlayerRuntime
    {
        public bool IsResolved { get; set; }
        public StatFormat Format { get; set; }
        public string LayoutName { get; set; } = string.Empty;

        public nint WorldSystem { get; set; }
        public nint ActorManager { get; set; }
        public nint Actor { get; set; }
        public nint Marker { get; set; }
        public nint Root { get; set; }
        public int CharacterSlot { get; set; }

        public nint StatsBase { get; set; }
        public nint HealthCurrent { get; set; }
        public nint HealthMax { get; set; }
        public nint StaminaCurrent { get; set; }
        public nint StaminaMax { get; set; }
        public nint SpiritCurrent { get; set; }
        public nint SpiritMax { get; set; }

        public nint HealthEntry { get; set; }
        public nint StaminaEntry { get; set; }
        public nint SpiritEntry { get; set; }

        public nint AttackAddress { get; set; }
    }
}
