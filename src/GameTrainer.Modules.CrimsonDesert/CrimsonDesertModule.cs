using GameTrainer.Core.Memory;
using GameTrainer.Core.Models;
using GameTrainer.Core.Modules;

namespace GameTrainer.Modules.CrimsonDesert;

public sealed class CrimsonDesertModule : IGameModule
{
    private ProcessMemory? _memory;

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
                    new() { Id = "infinite-health", Name = "Vida ilimitada", Type = TrainerFeatureType.Toggle },
                    new() { Id = "infinite-stamina", Name = "Vigor ilimitado", Type = TrainerFeatureType.Toggle },
                    new() { Id = "infinite-spirit", Name = "Espírito ilimitado", Type = TrainerFeatureType.Toggle },
                    new() { Id = "defense", Name = "Editar Defesa", Type = TrainerFeatureType.Number, Min = 0, Max = 999999, Step = 1 }
                }
            },
            new TrainerSection
            {
                Name = "Inventário",
                Features = new TrainerFeature[]
                {
                    new() { Id = "selected-item-quantity", Name = "Editar item selecionado / quantidade em dinheiro", Type = TrainerFeatureType.Number, Min = 0, Max = 999999999, Step = 1 }
                }
            },
            new TrainerSection
            {
                Name = "Estatísticas",
                Features = new TrainerFeature[]
                {
                    new() { Id = "max-health", Name = "Editar Saúde Máxima", Type = TrainerFeatureType.Number, Min = 1, Max = 999999, Step = 1 },
                    new() { Id = "max-stamina", Name = "Editar Stamina Máxima", Type = TrainerFeatureType.Number, Min = 1, Max = 999999, Step = 1 },
                    new() { Id = "max-spirit", Name = "Editar Espírito Máximo", Type = TrainerFeatureType.Number, Min = 1, Max = 999999, Step = 1 }
                }
            },
            new TrainerSection
            {
                Name = "Inimigos",
                Features = new TrainerFeature[]
                {
                    new() { Id = "one-hit-kill", Name = "Super Dano / Mortes com Um Golpe", Type = TrainerFeatureType.Toggle }
                }
            },
            new TrainerSection
            {
                Name = "Jogo",
                Features = new TrainerFeature[]
                {
                    new() { Id = "freeze-day", Name = "Congelar Dia", Type = TrainerFeatureType.Toggle },
                    new() { Id = "advance-hour", Name = "Avançar 1 Hora", Type = TrainerFeatureType.Action }
                }
            }
        }
    };

    public Task AttachAsync(ProcessMemory processMemory, CancellationToken cancellationToken = default)
    {
        _memory = processMemory;
        return Task.CompletedTask;
    }

    public Task<bool> SetToggleAsync(string featureId, bool enabled, CancellationToken cancellationToken = default)
    {
        // Os endereços/assinaturas do Crimson Desert serão adicionados depois da validação
        // da versão real do jogo. O módulo já está pronto para receber esses patches.
        return Task.FromResult(false);
    }

    public Task<bool> SetValueAsync(string featureId, double value, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(false);
    }
}
