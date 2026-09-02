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

        if (!_memory.TryRead<int>(_runtime.AttackAddress, out var currentAttack) || currentAttack <= 0 || currentAttack > 50_000_000)
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
            "Diagnóstico v0.2.1",
            $"Módulo: 0x{_memory.MainModuleBase.ToInt64():X} / 0x{_memory.MainModuleSize:X} bytes"
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
                "Não foi possível localizar o jogador. Clique em “Reanalisar memória” e depois em “Copiar diagnóstico”.",
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
        detail = "";

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
        detail = "";

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

        if (!TryResolvePointerChain(playerBase, new[] { 0x18, 0xA0, 0xD0, 0x68 }, out var actor))
        {
            detail = "base OK, cadeia +18/+A0/+D0/+68 falhou";
            return false;
        }

        if (!TryResolvePlayerFromActor(actor, out runtime, out var actorDetail))
        {
            detail = $"actor pela base OK, {actorDetail}";
            return false;
        }

        detail = $"assinatura/base/actor OK; {actorDetail}";
        return true;
    }

    private bool TryResolvePlayerViaStaticBase(out PlayerRuntime runtime, out string detail)
    {
        runtime = new PlayerRuntime();
        detail = "";

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

        if (!TryResolvePointerChain(playerBase, new[] { 0x18, 0xA0, 0xD0, 0x68 }, out var actor))
        {
            detail = "base válida, cadeia +18/+A0/+D0/+68 falhou";
            return false;
        }

        if (!TryResolvePlayerFromActor(actor, out runtime, out var actorDetail))
        {
            detail = $"actor pela base estática OK, {actorDetail}";
            return false;
        }

        detail = $"RVA legado/base/actor OK; {actorDetail}";
        return true;
    }

    private bool TryResolvePlayerFromActor(nint actor, out PlayerRuntime runtime, out string detail)
    {
        runtime = new PlayerRuntime { Actor = actor };
        detail = "";

        if (_memory is null || !_memory.IsReadable(actor, 0x60))
        {
            detail = "Actor não está legível";
            return false;
        }

        nint marker = 0;
        nint root = 0;
        if (_memory.TryReadPointer(actor + 0x20, out marker))
            _memory.TryReadPointer(marker + 0x18, out root);

        nint healthEntry = 0;
        var healthRoute = string.Empty;

        // Caminho publicado mais recente: actor + 0x58 -> entrada/componente de HP.
        if (_memory.TryReadPointer(actor + 0x58, out var directHealth) && ValidateStatEntry(directHealth, HealthId))
        {
            healthEntry = directHealth;
            healthRoute = "HP via Actor+0x58";
        }
        // Fallback legado: actor -> marker -> root -> +0x58 -> HP.
        else if (root != 0 && _memory.TryReadPointer(root + 0x58, out var rootedHealth) && ValidateStatEntry(rootedHealth, HealthId))
        {
            healthEntry = rootedHealth;
            healthRoute = "HP via Marker/Root+0x58";
        }
        else
        {
            detail = marker == 0
                ? "Actor encontrado, mas Marker (+0x20) e HP direto (+0x58) não validaram"
                : root == 0
                    ? "Marker OK, Root (+0x18) inválido e HP direto (+0x58) não validou"
                    : "Marker/Root OK, mas nenhuma entrada de HP válida foi encontrada em +0x58";
            return false;
        }

        if (!TryResolveStatLayout(healthEntry, out var staminaEntry, out var spiritEntry, out var layoutName))
        {
            detail = $"{healthRoute} OK, mas offsets de Vigor/Espírito não bateram";
            return false;
        }

        runtime.Marker = marker;
        runtime.Root = root;
        runtime.HealthEntry = healthEntry;
        runtime.StaminaEntry = staminaEntry;
        runtime.SpiritEntry = spiritEntry;
        runtime.LayoutName = layoutName;
        runtime.IsResolved = true;

        if (root != 0 && TryResolveAttack(out var attackAddress, out _, root))
            runtime.AttackAddress = attackAddress;

        detail = $"{healthRoute}; Vigor/Espírito layout {layoutName}";
        return true;
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

        return max > 0 && max < 10_000_000_000_000L && current >= 0 && current <= max * 2;
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

    private sealed class PlayerRuntime
    {
        public bool IsResolved { get; set; }
        public nint WorldSystem { get; set; }
        public nint ActorManager { get; set; }
        public nint Actor { get; set; }
        public nint Marker { get; set; }
        public nint Root { get; set; }
        public nint HealthEntry { get; set; }
        public nint StaminaEntry { get; set; }
        public nint SpiritEntry { get; set; }
        public nint AttackAddress { get; set; }
        public string LayoutName { get; set; } = string.Empty;
    }
}
