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

    private static readonly StatLayout CurrentLayout = new(0x510, 0x5A0, "atual");
    private static readonly StatLayout LegacyLayout = new(0x480, 0x510, "legado");

    private static readonly WorldSystemPattern[] WorldSystemPatterns =
    {
        new("48 83 EC 28 48 8B 0D ? ? ? ? 48 8B 49 ? E8 ? ? ? ? 84 C0 0F 94 C0 48 83 C4 28 C3", 7, 11),
        new("80 B8 ? ? ? ? 00 75 ? 48 8B 05 ? ? ? ? 48 8B 88 ? ? ? ?", 12, 16),
        new("48 8B 0D ? ? ? ? 48 8B 49 ? E8 ? ? ? ? 84 C0 0F 94 C0", 3, 7)
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

        ResolveRuntime(force: true);
        return Task.CompletedTask;
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
        if (_memory is null || !_runtime.IsResolved || _runtime.AttackAddress == 0)
            return;

        if (!_memory.TryRead<int>(_runtime.AttackAddress, out var currentAttack) || currentAttack <= 0 || currentAttack > 50_000_000)
        {
            _runtime.AttackAddress = 0;
            return;
        }

        _originalAttack ??= currentAttack;

        // Valor alto, mas ainda dentro de uma faixa segura de int32.
        // Se o jogo limitar internamente o dano, o recurso simplesmente terá efeito reduzido.
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
            // O jogador pode ter trocado de mapa/personagem e invalidado o endereço.
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

        _nextResolveAttemptUtc = DateTime.UtcNow.AddSeconds(1);

        try
        {
            foreach (var pattern in WorldSystemPatterns)
            {
                var match = _memory.FindPatternInMainModule(pattern.Signature);
                if (!match.HasValue)
                    continue;

                var globalSlot = _memory.ResolveRipRelative(match.Value, pattern.DisplacementOffset, pattern.InstructionEndOffset);
                if (!_memory.TryReadPointer(globalSlot, out var worldSystem))
                    continue;

                if (!TryResolvePlayer(worldSystem, out var runtime))
                    continue;

                _runtime = runtime;
                RuntimeStatus = $"Jogador localizado, layout de atributos {_runtime.LayoutName}";
                LastError = string.Empty;
                return true;
            }

            InvalidateRuntime("Não foi possível localizar a estrutura do jogador. Entre no mundo do jogo e tente novamente.", scheduleRetry: false);
            return false;
        }
        catch (Exception ex)
        {
            InvalidateRuntime($"Falha ao localizar o jogador: {ex.Message}", scheduleRetry: false);
            return false;
        }
    }

    private bool TryResolvePlayer(nint worldSystem, out PlayerRuntime runtime)
    {
        runtime = new PlayerRuntime { WorldSystem = worldSystem };

        if (_memory is null || !_memory.TryReadPointer(worldSystem + 0x30, out var actorManager))
            return false;

        if (!_memory.TryReadPointer(actorManager + 0x28, out var actor))
            return false;

        if (!_memory.TryReadPointer(actor + 0x20, out var marker))
            return false;

        if (!_memory.TryReadPointer(marker + 0x18, out var root))
            return false;

        if (!_memory.TryReadPointer(root + 0x58, out var healthEntry) || !ValidateStatEntry(healthEntry, HealthId))
            return false;

        if (!TryResolveStatLayout(healthEntry, out var staminaEntry, out var spiritEntry, out var layoutName))
            return false;

        runtime.ActorManager = actorManager;
        runtime.Actor = actor;
        runtime.Marker = marker;
        runtime.Root = root;
        runtime.HealthEntry = healthEntry;
        runtime.StaminaEntry = staminaEntry;
        runtime.SpiritEntry = spiritEntry;
        runtime.LayoutName = layoutName;
        runtime.IsResolved = true;

        // O componente de combate é validado separadamente, pois pode mudar entre versões.
        if (TryResolveAttack(out var attackAddress, out _ , root))
            runtime.AttackAddress = attackAddress;

        return true;
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

        // A cadeia publicada para o componente de combate usa o mesmo root dos atributos,
        // porém troca o ramo +0x58 (stats) por +0x38 (combate).
        if (!_memory.TryReadPointer(root + 0x38, out var combatComponent))
            return false;

        if (!_memory.TryRead<int>(combatComponent, out var currentAttack))
            return false;

        // Evita escrever quando a estrutura não parece ser realmente o componente de combate.
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

    private void InvalidateRuntime(string reason, bool scheduleRetry = true)
    {
        RestoreOriginalAttack();
        _runtime = new PlayerRuntime();
        RuntimeStatus = reason;
        LastError = reason;
        if (scheduleRetry)
            _nextResolveAttemptUtc = DateTime.UtcNow.AddMilliseconds(500);
    }

    private readonly record struct WorldSystemPattern(string Signature, int DisplacementOffset, int InstructionEndOffset);
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
