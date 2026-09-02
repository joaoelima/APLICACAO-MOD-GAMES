namespace GameTrainer.Core.Models;

public sealed class TrainerSection
{
    public required string Name { get; init; }
    public required IReadOnlyList<TrainerFeature> Features { get; init; }
}
