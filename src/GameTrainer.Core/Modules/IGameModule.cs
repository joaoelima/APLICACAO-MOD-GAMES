using GameTrainer.Core.Models;
using GameTrainer.Core.Memory;

namespace GameTrainer.Core.Modules;

public interface IGameModule
{
    GameDefinition Definition { get; }
    Task AttachAsync(ProcessMemory processMemory, CancellationToken cancellationToken = default);
    Task<bool> SetToggleAsync(string featureId, bool enabled, CancellationToken cancellationToken = default);
    Task<bool> SetValueAsync(string featureId, double value, CancellationToken cancellationToken = default);
}
