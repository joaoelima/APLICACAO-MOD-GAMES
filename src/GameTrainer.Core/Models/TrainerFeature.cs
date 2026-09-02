namespace GameTrainer.Core.Models;

public enum TrainerFeatureType
{
    Toggle,
    Number,
    Slider,
    Action
}

public sealed class TrainerFeature
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required TrainerFeatureType Type { get; init; }
    public string? Description { get; init; }
    public double? Min { get; init; }
    public double? Max { get; init; }
    public double? Step { get; init; }
    public bool IsAvailable { get; set; } = true;
}
