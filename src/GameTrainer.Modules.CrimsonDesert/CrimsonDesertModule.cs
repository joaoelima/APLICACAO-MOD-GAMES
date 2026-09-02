using System.Buffers.Binary;
using GameTrainer.Core.Memory;
using GameTrainer.Core.Models;
using GameTrainer.Core.Modules;

namespace GameTrainer.Modules.CrimsonDesert;

public sealed class CrimsonDesertModule : IGameModule
{
    private const uint MemPrivate = 0x20000;
    private const int CurrentOffset = 0x08;
    private const int MaxOffset = 0x18;
    private const long ScanBudget = 768L * 1024 * 1024;
    private const int ChunkSize = 4 * 1024 * 1024;

    private static readonly StatLayout[] Layouts =
    {
        new(0x510, 0x5A0, "Int64-mai-2026"),
        new(0x480, 0x510, "Int64-legado")
    };

    private static readonly WorldPattern[] WorldPatterns =
    {
        new("P1", "48 83 EC 28 48 8B 0D ? ? ? ? 48 8B 49 ? E8 ? ? ? ? 84 C0 0F 94 C0 48 83 C4 28 C3", 7, 11),
        new("P2", "80 B8 ? ? ? ? 00 75 ? 48 8B 05 ? ? ? ? 48 8B 88 ? ? ? ?", 12, 16),
        new("P3", "48 8B 0D ? ? ? ? 48 8B 49 ? E8 ? ? ? ? 84 C0 0F 94 C0", 3, 7)
    };

    private ProcessMemory? _memory;
    private RuntimeState _runtime = new();
    private DateTime _nextResolve = DateTime.MinValue;
    private bool _health;
    private bool _stamina;
    private bool _spirit;

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
                    new() { Id = "infinite-health", Name = "Vida ilimitada", Description = "Mantém a vida no valor máximo.", Type = TrainerFeatureType.Toggle },
                    new() { Id = "infinite-stamina", Name = "Vigor ilimitado", Description = "Mantém o vigor no valor máximo.", Type = TrainerFeatureType.Toggle },
                    new() { Id = "infinite-spirit", Name = "Espírito ilimitado", Description = "Mantém o espírito no valor máximo.", Type = TrainerFeatureType.Toggle }
                }
            },
            new TrainerSection
            {
                Name = "Combate",
                Features = new TrainerFeature[]
                {
                    new() { Id = "one-hit-kill", Name = "Super Dano / Mortes com Um Golpe", Description = "Em desenvolvimento.", Type = TrainerFeatureType.Toggle, IsAvailable = false }
                }
            }
        }
    };

    public Task AttachAsync(ProcessMemory processMemory, CancellationToken cancellationToken = default)
    {
        _memory = processMemory;
        _runtime = new RuntimeState();
        Resolve(true, cancellationToken);
        return Task.CompletedTask;
    }

    public Task<bool> ReprobeAsync(CancellationToken cancellationToken = default)
    {
        _runtime = new RuntimeState();
        _nextResolve = DateTime.MinValue;
        return Task.FromResult(Resolve(true, cancellationToken));
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
            LastError = "Super Dano ainda não está disponível nesta build.";
            return Task.FromResult(false);
        }

        if (enabled && !EnsureRuntime(cancellationToken))
            return Task.FromResult(false);

        switch (featureId)
        {
            case "infinite-health": _health = enabled; break;
            case "infinite-stamina": _stamina = enabled; break;
            case "infinite-spirit": _spirit = enabled; break;
            default: return Task.FromResult(false);
        }

        LastError = string.Empty;
        return Task.FromResult(true);
    }

    public Task<bool> SetValueAsync(string featureId, double value, CancellationToken cancellationToken = default)
        => Task.FromResult(false);

    public Task TickAsync(CancellationToken cancellationToken = default)
    {
        if (_memory is null || !_memory.IsAttached || (!_health && !_stamina && !_spirit))
            return Task.CompletedTask;

        if (!EnsureRuntime(cancellationToken))
            return Task.CompletedTask;

        if (_health) Restore(_runtime.Health, 0, "Vida");
        if (_stamina) Restore(_runtime.Stamina, 17, "Vigor");
        if (_spirit) Restore(_runtime.Spirit, 18, "Espírito");
        return Task.CompletedTask;
    }

    private bool EnsureRuntime(CancellationToken ct)
    {
        if (_runtime.IsResolved && ValidateRuntime()) return true;
        return Resolve(false, ct);
    }

    private bool Resolve(bool force, CancellationToken ct)
    {
        if (_memory is null || !_memory.IsAttached) return false;
        if (!force && DateTime.UtcNow < _nextResolve) return false;
        _nextResolve = DateTime.UtcNow.AddSeconds(2);
        RuntimeStatus = "Analisando automaticamente a memória do jogo...";

        var log = new List<string>
        {
            "Diagnóstico v0.2.8",
            $"Módulo: 0x{_memory.MainModuleBase.ToInt64():X} / 0x{_memory.MainModuleSize:X} bytes",
            "Modo: resolução automática; sem mapeamento manual"
        };

        var anchors = new HashSet<nint>();
        foreach (var pattern in WorldPatterns)
        {
            ct.ThrowIfCancellationRequested();
            var match = _memory.FindPatternInMainModule(pattern.Signature);
            if (!match.HasValue)
            {
                log.Add($"WorldSystem {pattern.Name}: assinatura não encontrada");
                continue;
            }

            var slot = _memory.ResolveRipRelative(match.Value, pattern.Disp, pattern.End);
            if (!_memory.TryReadPointer(slot, out var world)) continue;
            anchors.Add(world);
            log.Add($"WorldSystem {pattern.Name}: 0x{world.ToInt64():X}");
            if (_memory.TryReadPointer(world + 0x30, out var manager))
            {
                anchors.Add(manager);
                log.Add($"ActorManager {pattern.Name}: 0x{manager.ToInt64():X}");
            }
        }

        if (anchors.Count > 0 && TryHeapScan(anchors, out var state, out var scanLog, ct))
        {
            _runtime = state;
            log.AddRange(scanLog);
            log.Add("Método vencedor: Heap Stat Scan");
            log.Add($"StatsBase: 0x{_runtime.Stats.ToInt64():X}");
            log.Add($"HealthEntry: 0x{_runtime.Health.ToInt64():X}");
            log.Add($"StaminaEntry: 0x{_runtime.Stamina.ToInt64():X}");
            log.Add($"SpiritEntry: 0x{_runtime.Spirit.ToInt64():X}");
            log.Add($"Layout: {_runtime.Layout}");
            DiagnosticReport = string.Join(Environment.NewLine, log);
            RuntimeStatus = $"Pronto • {_runtime.Layout} • Vida/Vigor/Espírito validados";
            LastError = string.Empty;
            return true;
        }

        if (anchors.Count > 0)
        {
            _ = TryHeapScan(anchors, out _, out var failedScanLog, ct);
            log.AddRange(failedScanLog);
        }

        DiagnosticReport = string.Join(Environment.NewLine, log);
        RuntimeStatus = "Jogo conectado, mas os atributos desta build ainda não foram validados. Use “Copiar diagnóstico”.";
        LastError = RuntimeStatus;
        return false;
    }

    private bool TryHeapScan(IEnumerable<nint> anchors, out RuntimeState state, out List<string> log, CancellationToken ct)
    {
        state = new RuntimeState();
        log = new List<string> { "Heap stat scan:" };
        var anchorValues = anchors.Select(a => a.ToInt64()).Distinct().ToArray();
        var regions = _memory!.GetReadableRegions(true)
            .Where(r => r.Type == MemPrivate && r.Size >= 0x1000 && r.BaseAddress.ToInt64() >= 0x1_0000_0000L)
            .OrderBy(r => Distance(r, anchorValues))
            .ToArray();

        var found = new Dictionary<long, StatLayout>();
        long scanned = 0;

        foreach (var region in regions)
        {
            if (scanned >= ScanBudget || found.Count > 8) break;
            long offset = 0;
            while (offset < region.Size && scanned < ScanBudget && found.Count <= 8)
            {
                ct.ThrowIfCancellationRequested();
                var remaining = region.Size - offset;
                var length = (int)Math.Min(Math.Min(ChunkSize, remaining), ScanBudget - scanned);
                if (length < 0x1000) break;

                var readLength = (int)Math.Min(remaining, (long)length + 0x600);
                var address = region.BaseAddress + (nint)offset;
                if (_memory.TryReadBytes(address, readLength, out var bytes))
                    ScanBuffer(address, bytes, length, found);

                scanned += length;
                offset += length;
            }
        }

        log.Add($"  varrido={scanned / (1024d * 1024):F1} MB; candidatos={found.Count}");
        foreach (var item in found.Take(6)) log.Add($"  0x{item.Key:X}: {item.Value.Name}");
        if (found.Count != 1) return false;

        var winner = found.Single();
        state.IsResolved = true;
        state.Stats = (nint)winner.Key;
        state.Health = state.Stats;
        state.Stamina = state.Stats + winner.Value.StaminaOffset;
        state.Spirit = state.Stats + winner.Value.SpiritOffset;
        state.Layout = winner.Value.Name + "/heap";
        return true;
    }

    private static void ScanBuffer(nint baseAddress, byte[] bytes, int primaryLength, Dictionary<long, StatLayout> found)
    {
        var maxOffset = Layouts.Max(l => l.SpiritOffset) + 0x20;
        var limit = Math.Min(primaryLength, bytes.Length - maxOffset);
        if (limit <= 0) return;

        for (var offset = 0; offset <= limit; offset += 8)
        {
            foreach (var layout in Layouts)
            {
                if (!BufferedStat(bytes, offset, 0)) continue;
                if (!BufferedStat(bytes, offset + layout.StaminaOffset, 17)) continue;
                if (!BufferedStat(bytes, offset + layout.SpiritOffset, 18)) continue;
                found.TryAdd(baseAddress.ToInt64() + offset, layout);
            }
        }
    }

    private static bool BufferedStat(byte[] bytes, int offset, int type)
    {
        if (offset < 0 || offset + 0x20 > bytes.Length) return false;
        if (BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset, 4)) != type) return false;
        var current = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(offset + CurrentOffset, 8));
        var max = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(offset + MaxOffset, 8));
        return Plausible(current, max);
    }

    private bool ReadStat(nint address, int type, out Stat stat)
    {
        stat = default;
        if (!_memory!.IsReadable(address, 0x20)) return false;
        if (!_memory.TryRead<int>(address, out var actual) || actual != type) return false;
        if (!_memory.TryRead<long>(address + CurrentOffset, out var current)) return false;
        if (!_memory.TryRead<long>(address + MaxOffset, out var max)) return false;
        if (!Plausible(current, max)) return false;
        stat = new Stat(current, max);
        return true;
    }

    private void Restore(nint address, int type, string label)
    {
        if (!ReadStat(address, type, out var stat))
        {
            _runtime = new RuntimeState();
            RuntimeStatus = $"{label}: endereço deixou de validar. Relocalizando...";
            return;
        }
        if (stat.Current < stat.Max) _memory!.Write(address + CurrentOffset, stat.Max);
    }

    private bool ValidateRuntime()
        => _runtime.IsResolved
           && ReadStat(_runtime.Health, 0, out _)
           && ReadStat(_runtime.Stamina, 17, out _)
           && ReadStat(_runtime.Spirit, 18, out _);

    private static bool Plausible(long current, long max)
        => max > 0 && max < 10_000_000_000_000L && current >= 0 && current <= Math.Min(100_000_000_000_000L, max * 20L);

    private static long Distance(MemoryRegionInfo region, IReadOnlyList<long> anchors)
    {
        var start = region.BaseAddress.ToInt64();
        var end = start + region.Size;
        var best = long.MaxValue;
        foreach (var anchor in anchors)
        {
            var distance = anchor >= start && anchor < end ? 0 : anchor < start ? start - anchor : anchor - end;
            if (distance < best) best = distance;
        }
        return best;
    }

    private readonly record struct WorldPattern(string Name, string Signature, int Disp, int End);
    private readonly record struct StatLayout(int StaminaOffset, int SpiritOffset, string Name);
    private readonly record struct Stat(long Current, long Max);

    private sealed class RuntimeState
    {
        public bool IsResolved { get; set; }
        public nint Stats { get; set; }
        public nint Health { get; set; }
        public nint Stamina { get; set; }
        public nint Spirit { get; set; }
        public string Layout { get; set; } = string.Empty;
    }
}
