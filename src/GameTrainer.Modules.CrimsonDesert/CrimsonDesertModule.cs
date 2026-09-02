using GameTrainer.Core.Memory;
using GameTrainer.Core.Models;
using GameTrainer.Core.Modules;

namespace GameTrainer.Modules.CrimsonDesert;

public sealed class CrimsonDesertModule : IGameModule
{
    private const int HealthId = 0;
    private const int StaminaId = 17;
    private const int SpiritId = 18;

    private const int CurrentValueOffset = 0x08;
    private const int MaxValueOffset = 0x18;

    private const int StaticPlayerBaseRva = 0x05CC7618;
    private const string PlayerBaseDiscoveryPattern =
        "48 8B 0D ? ? ? ? E8 ? ? ? ? 41 B0 01 48 8B 53 08 48 8D 4C 24 40";

    private const int StatsProbeWindow = 0x1000;
    private const int ObjectPointerProbeWindow = 0x300;

    private static readonly StatLayout CurrentLayout = new(0x510, 0x5A0, "atual");
    private static readonly StatLayout LegacyLayout = new(0x480, 0x510, "legado");

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
            if (_infiniteHealth)
                RestoreStat(_runtime.HealthEntry, HealthId);

            if (_infiniteStamina)
                RestoreStat(_runtime.StaminaEntry, StaminaId);

            if (_infiniteSpirit)
                RestoreStat(_runtime.SpiritEntry, SpiritId);

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
            LastError = "Não foi possível validar o atributo de ataque nesta versão do jogo. O Super Dano não foi ativado.";
            return false;
        }

        _runtime.AttackAddress = attackAddress;
        _originalAttack ??= attackValue;
        _oneHitKill = true;
        ApplySuperDamage();
        LastError = string.Empty;
        return true;
    }

    private void ApplySuperDamage()
    {
        if (_memory is null || !_runtime.IsResolved)
            return;

        if (_runtime.AttackAddress == 0)
        {
            if (!TryResolveAttack(out var resolvedAddress, out var resolvedAttack))
                return;

            _runtime.AttackAddress = resolvedAddress;
            _originalAttack ??= resolvedAttack;
        }

        if (!_memory.TryRead<int>(_runtime.AttackAddress, out var currentAttack)
            || currentAttack <= 0
            || currentAttack > 50_000_000)
        {
            _runtime.AttackAddress = 0;
            return;
        }

        _originalAttack ??= currentAttack;

        const int boostedAttack = 5_000_000;
        if (currentAttack != boostedAttack)
            _memory.Write(_runtime.AttackAddress, boostedAttack);
    }

    private void RestoreOriginalAttack()
    {
        if (_memory is null || !_memory.IsAttached || _runtime.AttackAddress == 0 || !_originalAttack.HasValue)
            return;

        try
        {
            _memory.Write(_runtime.AttackAddress, _originalAttack.Value);
        }
        catch
        {
            // O endereço pode ter mudado após loading, troca de personagem ou mapa.
        }
        finally
        {
            _originalAttack = null;
        }
    }

    private void RestoreStat(nint entry, int expectedType)
    {
        if (_memory is null || !ValidateStatEntry(entry, expectedType))
        {
            InvalidateRuntime("Os atributos do jogador mudaram. Relocalizando...");
            return;
        }

        var max = _memory.Read<long>(entry + MaxValueOffset);
        if (max <= 0 || max > 10_000_000_000_000L)
        {
            InvalidateRuntime("Valor máximo de atributo inválido. Relocalizando...");
            return;
        }

        _memory.Write(entry + CurrentValueOffset, max);
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
            "Diagnóstico v0.2.2",
            $"Módulo: 0x{_memory.MainModuleBase.ToInt64():X} / 0x{_memory.MainModuleSize:X} bytes",
            "Estratégia: WorldSystem + busca dinâmica e validada do bloco HP/Vigor/Espírito"
        };

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

                diagnostics.Add($"WorldSystem {pattern.Name}: assinatura OK");
                var globalSlot = _memory.ResolveRipRelative(match.Value, pattern.DisplacementOffset, pattern.InstructionEndOffset);
                if (!_memory.TryReadPointer(globalSlot, out var worldSystem))
                {
                    diagnostics.Add($"WorldSystem {pattern.Name}: ponteiro RIP inválido");
                    continue;
                }

                if (TryResolvePlayerFromWorldSystem(worldSystem, out var runtime, out var detail))
                {
                    CompleteSuccessfulResolution(runtime, $"WorldSystem {pattern.Name}", diagnostics, detail);
                    return true;
                }

                diagnostics.Add($"WorldSystem {pattern.Name}: {detail}");
            }

            if (TryResolvePlayerViaBaseSignature(out var sigRuntime, out var sigDetail))
            {
                CompleteSuccessfulResolution(sigRuntime, "PlayerBase AOB", diagnostics, sigDetail);
                return true;
            }
            diagnostics.Add($"PlayerBase AOB: {sigDetail}");

            if (TryResolvePlayerViaStaticBase(out var staticRuntime, out var staticDetail))
            {
                CompleteSuccessfulResolution(staticRuntime, "PlayerBase estático", diagnostics, staticDetail);
                return true;
            }
            diagnostics.Add($"PlayerBase estático: {staticDetail}");

            DiagnosticReport = string.Join(Environment.NewLine, diagnostics);
            InvalidateRuntime(
                "O jogador foi encontrado, mas o bloco de atributos desta build ainda não foi identificado. Use “Copiar diagnóstico”.",
                scheduleRetry: false,
                preserveDiagnostic: true);
            return false;
        }
        catch (Exception ex)
        {
            diagnostics.Add($"Exceção: {ex.GetType().Name} - {ex.Message}");
            DiagnosticReport = string.Join(Environment.NewLine, diagnostics);
            InvalidateRuntime(
                "Falha durante o diagnóstico da memória. Use “Copiar diagnóstico” para me enviar os detalhes.",
                scheduleRetry: false,
                preserveDiagnostic: true);
            return false;
        }
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
        diagnostics.Add($"Health: 0x{runtime.HealthEntry.ToInt64():X}");
        diagnostics.Add($"Stamina: 0x{runtime.StaminaEntry.ToInt64():X}");
        diagnostics.Add($"Spirit: 0x{runtime.SpiritEntry.ToInt64():X}");
        DiagnosticReport = string.Join(Environment.NewLine, diagnostics);
        RuntimeStatus = $"Jogador localizado • {method} • layout {runtime.LayoutName}";
        LastError = string.Empty;
    }

    private bool TryResolvePlayerFromWorldSystem(nint worldSystem, out PlayerRuntime runtime, out string detail)
    {
        runtime = new PlayerRuntime { WorldSystem = worldSystem };
        detail = string.Empty;

        if (_memory is null)
        {
            detail = "memória não disponível";
            return false;
        }

        if (!_memory.TryReadPointer(worldSystem + 0x30, out var actorManager))
        {
            detail = "WorldSystem OK, ActorManager (+0x30) inválido";
            return false;
        }

        if (!_memory.TryReadPointer(actorManager + 0x28, out var actor))
        {
            detail = "ActorManager OK, UserActor (+0x28) inválido";
            return false;
        }

        if (!TryResolvePlayerFromActor(actor, out runtime, out var actorDetail))
        {
            detail = $"Actor OK, {actorDetail}";
            return false;
        }

        runtime.WorldSystem = worldSystem;
        runtime.ActorManager = actorManager;
        detail = $"WorldSystem/ActorManager/UserActor OK; {actorDetail}";
        return true;
    }

    private bool TryResolvePlayerViaBaseSignature(out PlayerRuntime runtime, out string detail)
    {
        runtime = new PlayerRuntime();
        detail = string.Empty;

        if (_memory is null)
        {
            detail = "memória não disponível";
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
            detail = "assinatura OK, storage/base inválido";
            return false;
        }

        var slotCandidates = new[] { 0x68, 0xE0, 0x168, 0x268 };
        foreach (var slot in slotCandidates)
        {
            if (!TryResolvePointerChain(playerBase, new[] { 0x18, 0xA0, 0xD0, slot }, out var actor))
                continue;

            if (!TryResolvePlayerFromActor(actor, out runtime, out var actorDetail))
                continue;

            detail = $"assinatura/base/actor OK pelo slot +0x{slot:X}; {actorDetail}";
            return true;
        }

        detail = "base OK, mas nenhuma cadeia de personagem conhecida (+68/+E0/+168/+268) validou os atributos";
        return false;
    }

    private bool TryResolvePlayerViaStaticBase(out PlayerRuntime runtime, out string detail)
    {
        runtime = new PlayerRuntime();
        detail = string.Empty;

        if (_memory is null)
        {
            detail = "memória não disponível";
            return false;
        }

        var storage = _memory.MainModuleBase + StaticPlayerBaseRva;
        if (!_memory.TryReadPointer(storage, out var playerBase))
        {
            detail = $"RVA 0x{StaticPlayerBaseRva:X} não contém base válida";
            return false;
        }

        var slotCandidates = new[] { 0x68, 0xE0, 0x168, 0x268 };
        foreach (var slot in slotCandidates)
        {
            if (!TryResolvePointerChain(playerBase, new[] { 0x18, 0xA0, 0xD0, slot }, out var actor))
                continue;

            if (!TryResolvePlayerFromActor(actor, out runtime, out var actorDetail))
                continue;

            detail = $"RVA legado/base/actor OK pelo slot +0x{slot:X}; {actorDetail}";
            return true;
        }

        detail = "base válida, mas nenhuma cadeia de personagem conhecida validou os atributos";
        return false;
    }

    private bool TryResolvePlayerFromActor(nint actor, out PlayerRuntime runtime, out string detail)
    {
        runtime = new PlayerRuntime { Actor = actor };
        detail = string.Empty;

        if (_memory is null || !_memory.IsReadable(actor, 0x60))
        {
            detail = "Actor não está legível";
            return false;
        }

        nint marker = 0;
        nint root = 0;
        nint playerCore = 0;

        if (_memory.TryReadPointer(actor + 0x20, out marker))
            _memory.TryReadPointer(marker + 0x18, out root);

        if (_memory.TryReadPointer(actor + 0x40, out var inner))
            _memory.TryReadPointer(inner + 0x08, out playerCore);

        if (!TryLocateStatsBlock(
                actor,
                marker,
                root,
                playerCore,
                out var healthEntry,
                out var staminaEntry,
                out var spiritEntry,
                out var layoutName,
                out var statsRoute,
                out var probeCount))
        {
            detail = $"Marker={(marker != 0 ? "OK" : "N/A")}, Root={(root != 0 ? "OK" : "N/A")}, " +
                     $"PlayerCore={(playerCore != 0 ? "OK" : "N/A")}; busca dinâmica testou {probeCount} âncoras sem achar a tripla 0/17/18";
            return false;
        }

        runtime.Marker = marker;
        runtime.Root = root;
        runtime.PlayerCore = playerCore;
        runtime.HealthEntry = healthEntry;
        runtime.StaminaEntry = staminaEntry;
        runtime.SpiritEntry = spiritEntry;
        runtime.LayoutName = layoutName;
        runtime.IsResolved = true;

        if (root != 0 && TryResolveAttack(out var attackAddress, out _, root))
            runtime.AttackAddress = attackAddress;

        detail = $"Stats localizados por {statsRoute}; layout {layoutName}; {probeCount} âncoras avaliadas";
        return true;
    }

    private bool TryLocateStatsBlock(
        nint actor,
        nint marker,
        nint root,
        nint playerCore,
        out nint healthEntry,
        out nint staminaEntry,
        out nint spiritEntry,
        out string layoutName,
        out string route,
        out int probeCount)
    {
        healthEntry = 0;
        staminaEntry = 0;
        spiritEntry = 0;
        layoutName = string.Empty;
        route = string.Empty;
        probeCount = 0;

        if (_memory is null)
            return false;

        var probes = new List<StatsProbe>();
        var seen = new HashSet<long>();

        void AddProbe(string name, nint address)
        {
            if (address == 0 || !ProcessMemory.IsLikelyPointer(address) || !_memory.IsReadable(address))
                return;

            if (seen.Add(address.ToInt64()))
                probes.Add(new StatsProbe(name, address));
        }

        // Primeiro os caminhos documentados/publicados, tanto como ponteiro quanto inline.
        AddProbe("Actor", actor);
        AddProbe("Actor+0x58 inline", actor + 0x58);
        if (_memory.TryReadPointer(actor + 0x58, out var actor58))
            AddProbe("Actor+0x58 -> ptr", actor58);

        if (marker != 0)
        {
            AddProbe("Marker", marker);
            AddProbe("Marker+0x58 inline", marker + 0x58);
            if (_memory.TryReadPointer(marker + 0x58, out var marker58))
                AddProbe("Marker+0x58 -> ptr", marker58);
        }

        if (root != 0)
        {
            AddProbe("Root", root);
            AddProbe("Root+0x58 inline", root + 0x58);
            if (_memory.TryReadPointer(root + 0x58, out var root58))
                AddProbe("Root+0x58 -> ptr", root58);
        }

        if (playerCore != 0)
            AddProbe("PlayerCore", playerCore);

        // Builds novas podem mover o ponteiro do componente dentro do Actor/Root/Core.
        AddPointerFieldProbes(actor, "Actor", probes, seen);
        if (marker != 0)
            AddPointerFieldProbes(marker, "Marker", probes, seen);
        if (root != 0)
            AddPointerFieldProbes(root, "Root", probes, seen);
        if (playerCore != 0)
            AddPointerFieldProbes(playerCore, "PlayerCore", probes, seen);

        // Alguns caminhos públicos expõem o personagem controlado em slots/children.
        foreach (var offset in new[] { 0x68, 0xD0, 0xD8, 0xE0, 0xE8, 0xF0, 0xF8, 0x100, 0x108, 0x168, 0x268 })
        {
            if (!_memory.TryReadPointer(actor + offset, out var child))
                continue;

            AddProbe($"Actor+0x{offset:X} -> child", child);
            AddPointerFieldProbes(child, $"Child@+0x{offset:X}", probes, seen, 0x180);
        }

        foreach (var probe in probes.Take(192))
        {
            probeCount++;
            if (!TryFindStatTripletNear(
                    probe.Address,
                    out healthEntry,
                    out staminaEntry,
                    out spiritEntry,
                    out layoutName,
                    out var healthOffset))
                continue;

            route = healthOffset == 0
                ? probe.Name
                : $"{probe.Name} +0x{healthOffset:X}";
            return true;
        }

        return false;
    }

    private void AddPointerFieldProbes(
        nint objectBase,
        string label,
        List<StatsProbe> probes,
        HashSet<long> seen,
        int window = ObjectPointerProbeWindow)
    {
        if (_memory is null || objectBase == 0)
            return;

        for (var offset = 0; offset <= window; offset += 0x08)
        {
            if (!_memory.TryReadPointer(objectBase + offset, out var candidate))
                continue;

            if (!seen.Add(candidate.ToInt64()))
                continue;

            probes.Add(new StatsProbe($"{label}+0x{offset:X} -> ptr", candidate));
            if (probes.Count >= 256)
                return;
        }
    }

    private bool TryFindStatTripletNear(
        nint anchor,
        out nint healthEntry,
        out nint staminaEntry,
        out nint spiritEntry,
        out string layoutName,
        out int healthOffset)
    {
        healthEntry = 0;
        staminaEntry = 0;
        spiritEntry = 0;
        layoutName = string.Empty;
        healthOffset = 0;

        if (_memory is null || anchor == 0)
            return false;

        for (var offset = 0; offset <= StatsProbeWindow; offset += 0x08)
        {
            var candidate = anchor + offset;
            if (!ValidateStatEntry(candidate, HealthId))
                continue;

            if (!TryResolveStatLayout(candidate, out staminaEntry, out spiritEntry, out layoutName))
                continue;

            healthEntry = candidate;
            healthOffset = offset;
            return true;
        }

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

    private bool TryResolveStatLayout(nint healthEntry, out nint staminaEntry, out nint spiritEntry, out string layoutName)
    {
        foreach (var layout in new[] { CurrentLayout, LegacyLayout })
        {
            staminaEntry = healthEntry + layout.StaminaFromHealth;
            spiritEntry = healthEntry + layout.SpiritFromHealth;

            if (ValidateStatEntry(staminaEntry, StaminaId) && ValidateStatEntry(spiritEntry, SpiritId))
            {
                layoutName = layout.Name;
                return true;
            }
        }

        // Fallback da v0.2.2: descobre os offsets em tempo de execução dentro
        // de uma janela pequena do mesmo componente. Só aceita se os IDs 17 e 18
        // também tiverem current/max plausíveis.
        nint dynamicStamina = 0;
        nint dynamicSpirit = 0;

        for (var offset = 0x20; offset <= StatsProbeWindow; offset += 0x10)
        {
            var candidate = healthEntry + offset;

            if (dynamicStamina == 0 && ValidateStatEntry(candidate, StaminaId))
                dynamicStamina = candidate;

            if (dynamicSpirit == 0 && ValidateStatEntry(candidate, SpiritId))
                dynamicSpirit = candidate;

            if (dynamicStamina != 0 && dynamicSpirit != 0)
            {
                staminaEntry = dynamicStamina;
                spiritEntry = dynamicSpirit;
                layoutName = $"dinâmico (Sta +0x{(dynamicStamina - healthEntry):X}, Spi +0x{(dynamicSpirit - healthEntry):X})";
                return true;
            }
        }

        staminaEntry = 0;
        spiritEntry = 0;
        layoutName = string.Empty;
        return false;
    }

    private bool TryResolveAttack(out nint attackAddress, out int attackValue, nint rootOverride = 0)
    {
        attackAddress = 0;
        attackValue = 0;

        if (_memory is null || !_memory.IsAttached)
            return false;

        var root = rootOverride != 0 ? rootOverride : _runtime.Root;
        if (root == 0)
            return false;

        if (!_memory.TryReadPointer(root + 0x38, out var combatComponent))
            return false;

        if (!_memory.TryRead<int>(combatComponent, out var currentAttack))
            return false;

        if (currentAttack <= 0 || currentAttack > 5_000_000)
            return false;

        attackAddress = combatComponent;
        attackValue = currentAttack;
        return true;
    }

    private bool ValidateRuntime()
    {
        if (_memory is null || !_runtime.IsResolved)
            return false;

        return ValidateStatEntry(_runtime.HealthEntry, HealthId)
               && ValidateStatEntry(_runtime.StaminaEntry, StaminaId)
               && ValidateStatEntry(_runtime.SpiritEntry, SpiritId);
    }

    private bool ValidateStatEntry(nint entry, int expectedType)
    {
        if (_memory is null || entry == 0 || !_memory.IsReadable(entry, 0x20))
            return false;

        if (!_memory.TryRead<int>(entry, out var type) || type != expectedType)
            return false;

        if (!_memory.TryRead<long>(entry + CurrentValueOffset, out var current)
            || !_memory.TryRead<long>(entry + MaxValueOffset, out var max))
            return false;

        if (max <= 0 || max >= 10_000_000_000_000L)
            return false;

        // Buffs/debuffs podem deixar o valor atual temporariamente acima do máximo base.
        return current >= 0 && current <= max * 8;
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

    private readonly record struct StatLayout(int StaminaFromHealth, int SpiritFromHealth, string Name);
    private readonly record struct StatsProbe(string Name, nint Address);

    private sealed class PlayerRuntime
    {
        public bool IsResolved { get; set; }
        public nint WorldSystem { get; set; }
        public nint ActorManager { get; set; }
        public nint Actor { get; set; }
        public nint Marker { get; set; }
        public nint Root { get; set; }
        public nint PlayerCore { get; set; }
        public nint HealthEntry { get; set; }
        public nint StaminaEntry { get; set; }
        public nint SpiritEntry { get; set; }
        public nint AttackAddress { get; set; }
        public string LayoutName { get; set; } = string.Empty;
    }
}
