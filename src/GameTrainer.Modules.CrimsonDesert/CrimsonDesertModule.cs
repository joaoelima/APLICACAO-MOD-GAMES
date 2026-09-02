using System.Buffers.Binary;
using GameTrainer.Core.Memory;
using GameTrainer.Core.Models;
using GameTrainer.Core.Modules;

namespace GameTrainer.Modules.CrimsonDesert;

public sealed class CrimsonDesertModule : IGameModule
{
    private const uint MemPrivate = 0x20000;
    private const int HealthId = 0;
    private const int StaminaId = 17;
    private const int SpiritId = 18;
    private const int StatCurrentOffset = 0x08;
    private const int StatMaxOffset = 0x18;
    private const long ScanBudgetBytes = 768L * 1024 * 1024;
    private const int ScanChunkSize = 4 * 1024 * 1024;

    private static readonly StatLayout[] Layouts =
    {
        new(0x510, 0x5A0, "Int64-mai-2026"),
        new(0x480, 0x510, "Int64-legado")
    };

    private static readonly WorldSystemPattern[] WorldSystemPatterns =
    {
        new("P1", "48 83 EC 28 48 8B 0D ? ? ? ? 48 8B 49 ? E8 ? ? ? ? 84 C0 0F 94 C0 48 83 C4 28 C3", 7, 11),
        new("P2", "80 B8 ? ? ? ? 00 75 ? 48 8B 05 ? ? ? ? 48 8B 88 ? ? ? ?", 12, 16),
        new("P3", "48 8B 0D ? ? ? ? 48 8B 49 ? E8 ? ? ? ? 84 C0 0F 94 C0", 3, 7)
    };

    private const string CurrentPlayerPattern =
        "48 8B 53 08 48 8D 4C 24 78 E8 ? ? ? ? 90 48 8B 43 68 48 8B 88 A0 01 00 00 48 8B 41 38 0F B7 48 20";

    private ProcessMemory? _memory;
    private RuntimeState _runtime = new();
    private DateTime _nextResolveAttemptUtc = DateTime.MinValue;
    private bool _infiniteHealth;
    private bool _infiniteStamina;
    private bool _infiniteSpirit;

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
                    new() { Id = "infinite-health", Name = "Vida ilimitada", Description = "Mantém a vida no valor máximo enquanto estiver ativado.", Type = TrainerFeatureType.Toggle },
                    new() { Id = "infinite-stamina", Name = "Vigor ilimitado", Description = "Mantém o vigor no valor máximo enquanto estiver ativado.", Type = TrainerFeatureType.Toggle },
                    new() { Id = "infinite-spirit", Name = "Espírito ilimitado", Description = "Mantém o espírito no valor máximo enquanto estiver ativado.", Type = TrainerFeatureType.Toggle }
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
        ResolveRuntime(force: true, cancellationToken);
        return Task.CompletedTask;
    }

    public Task<bool> ReprobeAsync(CancellationToken cancellationToken = default)
    {
        _runtime = new RuntimeState();
        _nextResolveAttemptUtc = DateTime.MinValue;
        return Task.FromResult(ResolveRuntime(force: true, cancellationToken));
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
            case "infinite-health": _infiniteHealth = enabled; break;
            case "infinite-stamina": _infiniteStamina = enabled; break;
            case "infinite-spirit": _infiniteSpirit = enabled; break;
            default:
                LastError = $"Recurso desconhecido: {featureId}.";
                return Task.FromResult(false);
        }

        LastError = string.Empty;
        return Task.FromResult(true);
    }

    public Task<bool> SetValueAsync(string featureId, double value, CancellationToken cancellationToken = default)
        => Task.FromResult(false);

    public Task TickAsync(CancellationToken cancellationToken = default)
    {
        if (_memory is null || !_memory.IsAttached)
            return Task.CompletedTask;

        if (!_infiniteHealth && !_infiniteStamina && !_infiniteSpirit)
            return Task.CompletedTask;

        if (!EnsureRuntime(cancellationToken))
            return Task.CompletedTask;

        if (_infiniteHealth) RestoreStat(_runtime.HealthEntry, HealthId, "Vida");
        if (_infiniteStamina) RestoreStat(_runtime.StaminaEntry, StaminaId, "Vigor");
        if (_infiniteSpirit) RestoreStat(_runtime.SpiritEntry, SpiritId, "Espírito");

        return Task.CompletedTask;
    }

    private bool EnsureRuntime(CancellationToken cancellationToken)
    {
        if (_runtime.IsResolved && ValidateRuntime())
            return true;
        return ResolveRuntime(force: false, cancellationToken);
    }

    private bool ResolveRuntime(bool force, CancellationToken cancellationToken)
    {
        if (_memory is null || !_memory.IsAttached)
            return false;

        if (!force && DateTime.UtcNow < _nextResolveAttemptUtc)
            return false;

        _nextResolveAttemptUtc = DateTime.UtcNow.AddSeconds(2);
        RuntimeStatus = "Analisando automaticamente a memória do jogo...";

        var log = new List<string>
        {
            "Diagnóstico v0.2.8",
            $"Módulo: 0x{_memory.MainModuleBase.ToInt64():X} / 0x{_memory.MainModuleSize:X} bytes",
            "Modo: resolução automática",
            "Estratégia: WorldSystem/ActorManager + validação de objetos + fallback por heap stat scan"
        };

        try
        {
            var currentPlayerMatch = _memory.FindPatternInMainModule(CurrentPlayerPattern);
            log.Add(currentPlayerMatch.HasValue
                ? $"AOB CurrentPlayer: 0x{currentPlayerMatch.Value.ToInt64():X}"
                : "AOB CurrentPlayer: não encontrado");

            var anchors = new HashSet<nint>();

            foreach (var pattern in WorldSystemPatterns)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var match = _memory.FindPatternInMainModule(pattern.Signature);
                if (!match.HasValue)
                {
                    log.Add($"WorldSystem {pattern.Name}: assinatura não encontrada");
                    continue;
                }

                var slot = _memory.ResolveRipRelative(match.Value, pattern.DisplacementOffset, pattern.InstructionEndOffset);
                if (!_memory.TryReadPointer(slot, out var worldSystem))
                {
                    log.Add($"WorldSystem {pattern.Name}: ponteiro global inválido");
                    continue;
                }

                anchors.Add(worldSystem);

                if (!_memory.TryReadPointer(worldSystem + 0x30, out var actorManager))
                {
                    log.Add($"WorldSystem {pattern.Name}: ActorManager inválido");
                    continue;
                }

                anchors.Add(actorManager);
                log.Add($"WorldSystem {pattern.Name}: WorldSystem=0x{worldSystem.ToInt64():X}, ActorManager=0x{actorManager.ToInt64():X}");

                if (TryResolveFromActorManager(actorManager, out var runtime, out var detail))
                {
                    _runtime = runtime;
                    log.Add(detail);
                    CompleteResolution("ActorManager", log);
                    return true;
                }

                log.Add(detail);
            }

            if (anchors.Count > 0 && TryResolveByHeapScan(anchors, out var heapRuntime, out var heapLog, cancellationToken))
            {
                _runtime = heapRuntime;
                log.AddRange(heapLog);
                CompleteResolution("Heap Stat Scan", log);
                return true;
            }

            if (anchors.Count > 0)
            {
                _ = TryResolveByHeapScan(anchors, out _, out var heapFailure, cancellationToken);
                log.AddRange(heapFailure);
            }

            DiagnosticReport = string.Join(Environment.NewLine, log);
            RuntimeStatus = "Jogo conectado, mas os atributos desta build ainda não foram validados. Use “Copiar diagnóstico”.";
            LastError = RuntimeStatus;
            return false;
        }
        catch (OperationCanceledException)
        {
            RuntimeStatus = "Análise cancelada.";
            LastError = RuntimeStatus;
            return false;
        }
        catch (Exception ex)
        {
            log.Add($"Exceção: {ex.GetType().Name} - {ex.Message}");
            DiagnosticReport = string.Join(Environment.NewLine, log);
            RuntimeStatus = "Falha durante a resolução automática.";
            LastError = RuntimeStatus;
            return false;
        }
    }

    private bool TryResolveFromActorManager(nint actorManager, out RuntimeState runtime, out string detail)
    {
        runtime = new RuntimeState();
        var roots = new HashSet<nint>();

        for (var offset = 0; offset <= 0x400; offset += 8)
        {
            if (_memory!.TryReadPointer(actorManager + offset, out var pointer))
                roots.Add(pointer);
        }

        var candidates = new HashSet<nint>();
        foreach (var root in roots)
        {
            AddObjectCandidate(candidates, root);
            if (!_memory!.IsReadable(root, 0x280)) continue;

            for (var offset = 0; offset <= 0x280; offset += 8)
            {
                if (_memory.TryReadPointer(root + offset, out var nested))
                    AddObjectCandidate(candidates, nested);
            }
        }

        var matches = new List<(RuntimeState Runtime, int Score, string Detail)>();
        foreach (var candidate in candidates.Take(512))
        {
            if (!TryResolveStatsNearActor(candidate, out var state, out var statsDetail))
                continue;

            var score = 100;
            if (TryValidatePosition(candidate)) score += 20;
            if (TryReadActorType(candidate, out var actorType) && actorType == 0x01) score += 50;
            state.Actor = candidate;
            matches.Add((state, score, statsDetail));
        }

        if (matches.Count == 0)
        {
            detail = $"ActorManager: {roots.Count} raízes, {candidates.Count} objetos candidatos, nenhum stats válido";
            return false;
        }

        var ordered = matches.OrderByDescending(x => x.Score).ToArray();
        if (ordered.Length > 1 && ordered[0].Score == ordered[1].Score && ordered[0].Score < 150)
        {
            detail = $"ActorManager: resolução ambígua ({ordered.Length} candidatos)";
            return false;
        }

        runtime = ordered[0].Runtime;
        detail = $"ActorManager: ator=0x{runtime.Actor.ToInt64():X}, score={ordered[0].Score}, {ordered[0].Detail}";
        return true;
    }

    private void AddObjectCandidate(HashSet<nint> candidates, nint candidate)
    {
        if (_memory is null || candidate == 0 || IsInsideMainModule(candidate) || !_memory.IsReadable(candidate, 0x60))
            return;

        if (_memory.TryReadPointer(candidate, out var vtable) && IsInsideMainModule(vtable))
            candidates.Add(candidate);
    }

    private bool TryResolveStatsNearActor(nint actor, out RuntimeState runtime, out string detail)
    {
        runtime = new RuntimeState();

        for (var offset = 0; offset <= 0x180; offset += 8)
        {
            if (!_memory!.TryReadPointer(actor + offset, out var pointer))
                continue;

            if (TryBuildRuntime(pointer, out runtime, out detail))
                return true;

            if (_memory.TryReadPointer(pointer + 0x58, out var nested) && TryBuildRuntime(nested, out runtime, out detail))
                return true;
        }

        detail = "nenhuma tripla HP/Vigor/Espírito validou";
        return false;
    }

    private bool TryBuildRuntime(nint statsBase, out RuntimeState runtime, out string detail)
    {
        runtime = new RuntimeState();
        foreach (var layout in Layouts)
        {
            var hp = statsBase;
            var stamina = statsBase + layout.StaminaOffset;
            var spirit = statsBase + layout.SpiritOffset;

            if (!ValidateStatEntry(hp, HealthId, out var hpStat)
                || !ValidateStatEntry(stamina, StaminaId, out var staStat)
                || !ValidateStatEntry(spirit, SpiritId, out var spiStat))
                continue;

            runtime.IsResolved = true;
            runtime.StatsBase = statsBase;
            runtime.HealthEntry = hp;
            runtime.StaminaEntry = stamina;
            runtime.SpiritEntry = spirit;
            runtime.LayoutName = layout.Name;
            detail = $"Stats=0x{statsBase.ToInt64():X}; HP={hpStat.Current}/{hpStat.Max}; STA={staStat.Current}/{staStat.Max}; SPI={spiStat.Current}/{spiStat.Max}; {layout.Name}";
            return true;
        }

        detail = "layout não reconhecido";
        return false;
    }

    private bool TryResolveByHeapScan(IEnumerable<nint> anchors, out RuntimeState runtime, out List<string> diagnostics, CancellationToken cancellationToken)
    {
        runtime = new RuntimeState();
        diagnostics = new List<string> { "Heap stat scan:" };

        var anchorValues = anchors.Select(a => a.ToInt64()).Distinct().ToArray();
        var regions = _memory!.GetReadableRegions(writableOnly: true)
            .Where(r => r.Type == MemPrivate && r.Size >= 0x1000 && r.BaseAddress.ToInt64() >= 0x1_0000_0000L)
            .OrderBy(r => DistanceToAnchors(r, anchorValues))
            .ToArray();

        var found = new Dictionary<long, (StatLayout Layout, StatSnapshot Hp, StatSnapshot Sta, StatSnapshot Spi)>();
        long scanned = 0;

        foreach (var region in regions)
        {
            if (scanned >= ScanBudgetBytes || found.Count > 8) break;
            var regionOffset = 0L;

            while (regionOffset < region.Size && scanned < ScanBudgetBytes && found.Count <= 8)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var remaining = region.Size - regionOffset;
                var baseLength = (int)Math.Min(Math.Min(ScanChunkSize, remaining), ScanBudgetBytes - scanned);
                if (baseLength < 0x1000) break;

                var requested = (int)Math.Min(remaining, (long)baseLength + 0x600);
                var chunkAddress = region.BaseAddress + regionOffset;
                if (_memory.TryReadBytes(chunkAddress, requested, out var bytes))
                    ScanChunk(chunkAddress, bytes, baseLength, found);

                scanned += baseLength;
                regionOffset += baseLength;
            }
        }

        diagnostics.Add($"  varrido={scanned / (1024d * 1024):F1} MB; candidatos={found.Count}");
        foreach (var candidate in found.Take(6))
            diagnostics.Add($"  0x{candidate.Key:X}: {candidate.Value.Layout.Name}, HP={candidate.Value.Hp.Current}/{candidate.Value.Hp.Max}, STA={candidate.Value.Sta.Current}/{candidate.Value.Sta.Max}, SPI={candidate.Value.Spi.Current}/{candidate.Value.Spi.Max}");

        if (found.Count != 1)
            return false;

        var winner = found.Single();
        runtime.IsResolved = true;
        runtime.StatsBase = (nint)winner.Key;
        runtime.HealthEntry = (nint)winner.Key;
        runtime.StaminaEntry = (nint)(winner.Key + winner.Value.Layout.StaminaOffset);
        runtime.SpiritEntry = (nint)(winner.Key + winner.Value.Layout.SpiritOffset);
        runtime.LayoutName = winner.Value.Layout.Name + "/heap";
        return true;
    }

    private static void ScanChunk(nint chunkAddress, byte[] bytes, int primaryLength, Dictionary<long, (StatLayout Layout, StatSnapshot Hp, StatSnapshot Sta, StatSnapshot Spi)> output)
    {
        var maxOffset = Layouts.Max(x => x.SpiritOffset) + 0x20;
        var scanLimit = Math.Min(primaryLength, bytes.Length - maxOffset);
        if (scanLimit <= 0) return;

        for (var offset = 0; offset <= scanLimit; offset += 8)
        {
            if (BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset, 4)) != HealthId) continue;
            if (!TryReadBufferedStat(bytes, offset, HealthId, out var hp)) continue;

            foreach (var layout in Layouts)
            {
                if (!TryReadBufferedStat(bytes, offset + layout.StaminaOffset, StaminaId, out var sta)
                    || !TryReadBufferedStat(bytes, offset + layout.SpiritOffset, SpiritId, out var spi))
                    continue;

                var address = chunkAddress.ToInt64() + offset;
                output.TryAdd(address, (layout, hp, sta, spi));
            }
        }
    }

    private static bool TryReadBufferedStat(byte[] bytes, int offset, int expectedType, out StatSnapshot stat)
    {
        stat = default;
        if (offset < 0 || offset + 0x20 > bytes.Length) return false;
        if (BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset, 4)) != expectedType) return false;

        var current = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(offset + StatCurrentOffset, 8));
        var max = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(offset + StatMaxOffset, 8));
        if (!Plausible(current, max)) return false;

        stat = new StatSnapshot(current, max);
        return true;
    }

    private bool ValidateStatEntry(nint entry, int expectedType, out StatSnapshot stat)
    {
        stat = default;
        if (_memory is null || !_memory.IsReadable(entry, 0x20)) return false;
        if (!_memory.TryRead<int>(entry, out var type) || type != expectedType) return false;
        if (!_memory.TryRead<long>(entry + StatCurrentOffset, out var current)) return false;
        if (!_memory.TryRead<long>(entry + StatMaxOffset, out var max)) return false;
        if (!Plausible(current, max)) return false;

        stat = new StatSnapshot(current, max);
        return true;
    }

    private static bool Plausible(long current, long max)
        => max > 0 && max < 10_000_000_000_000L && current >= 0 && current <= Math.Min(100_000_000_000_000L, max * 20L);

    private bool TryValidatePosition(nint actor)
    {
        if (!_memory!.TryReadPointer(actor + 0x40, out var inner)) return false;
        if (!_memory.TryReadPointer(inner + 0x08, out var core)) return false;
        if (!_memory.TryReadPointer(core + 0x248, out var pos)) return false;
        if (!_memory.TryRead<float>(pos + 0x90, out var x) || !_memory.TryRead<float>(pos + 0x94, out var y) || !_memory.TryRead<float>(pos + 0x98, out var z)) return false;
        return float.IsFinite(x) && float.IsFinite(y) && float.IsFinite(z) && Math.Abs(x) < 10_000_000 && Math.Abs(y) < 10_000_000 && Math.Abs(z) < 10_000_000;
    }

    private bool TryReadActorType(nint actor, out byte type)
    {
        type = 0;
        return _memory!.TryReadPointer(actor + 0x48, out var a)
               && _memory.TryReadPointer(a + 0x08, out var b)
               && _memory.TryReadPointer(b + 0x88, out var c)
               && _memory.TryRead<byte>(c + 0x01, out type);
    }

    private void RestoreStat(nint entry, int expectedType, string label)
    {
        if (!ValidateStatEntry(entry, expectedType, out var stat))
        {
            _runtime = new RuntimeState();
            RuntimeStatus = $"{label}: endereço deixou de validar. Relocalizando...";
            LastError = RuntimeStatus;
            return;
        }

        if (stat.Current < stat.Max)
            _memory!.Write(entry + StatCurrentOffset, stat.Max);
    }

    private bool ValidateRuntime()
        => _runtime.IsResolved
           && ValidateStatEntry(_runtime.HealthEntry, HealthId, out _)
           && ValidateStatEntry(_runtime.StaminaEntry, StaminaId, out _)
           && ValidateStatEntry(_runtime.SpiritEntry, SpiritId, out _);

    private void CompleteResolution(string method, List<string> log)
    {
        log.Add($"Método vencedor: {method}");
        log.Add($"StatsBase: 0x{_runtime.StatsBase.ToInt64():X}");
        log.Add($"HealthEntry: 0x{_runtime.HealthEntry.ToInt64():X}");
        log.Add($"StaminaEntry: 0x{_runtime.StaminaEntry.ToInt64():X}");
        log.Add($"SpiritEntry: 0x{_runtime.SpiritEntry.ToInt64():X}");
        log.Add($"Layout: {_runtime.LayoutName}");
        DiagnosticReport = string.Join(Environment.NewLine, log);
        RuntimeStatus = $"Pronto • {_runtime.LayoutName} • Vida/Vigor/Espírito validados";
        LastError = string.Empty;
    }

    private bool IsInsideMainModule(nint address)
    {
        var value = address.ToInt64();
        var start = _memory!.MainModuleBase.ToInt64();
        return value >= start && value < start + _memory.MainModuleSize;
    }

    private static long DistanceToAnchors(MemoryRegionInfo region, IReadOnlyList<long> anchors)
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

    private readonly record struct WorldSystemPattern(string Name, string Signature, int DisplacementOffset, int InstructionEndOffset);
    private readonly record struct StatLayout(int StaminaOffset, int SpiritOffset, string Name);
    private readonly record struct StatSnapshot(long Current, long Max);

    private sealed class RuntimeState
    {
        public bool IsResolved { get; set; }
        public nint Actor { get; set; }
        public nint StatsBase { get; set; }
        public nint HealthEntry { get; set; }
        public nint StaminaEntry { get; set; }
        public nint SpiritEntry { get; set; }
        public string LayoutName { get; set; } = string.Empty;
    }
}
