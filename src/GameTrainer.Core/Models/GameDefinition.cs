namespace GameTrainer.Core.Models;

public sealed class GameDefinition
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required IReadOnlyList<string> ProcessNames { get; init; }
    public required IReadOnlyList<TrainerSection> Sections { get; init; }
}
