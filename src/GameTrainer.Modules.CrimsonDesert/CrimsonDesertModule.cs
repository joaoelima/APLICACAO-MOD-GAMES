using System.Buffers.Binary;
using GameTrainer.Core.Memory;
using GameTrainer.Core.Models;
using GameTrainer.Core.Modules;

namespace GameTrainer.Modules.CrimsonDesert;

public sealed class CrimsonDesertModule : IGameModule
{
    private const uint MemPrivate = 0x20000;
    private const long StatScanBudget = 768L * 1024 * 1024;
    private const long BackRefBudget = 512L * 1024 * 1024;
    private const int ChunkSize = 4 * 1024 * 1024;

    // Layout usado pela tabela pública atual do bbfox (01/08/2026):
    // current/max = 4 bytes; Max fica +0x10 a partir do Current.
    private const int HpCurrent = 0x08;
    private const int HpMax = 0x18;
    private const int StaminaCurrent = 0x6C8;
    private const int StaminaMax = 0x6D8;
    private const int SpiritCurrent = 0x758;
    private const int SpiritMax = 0x768;
    private const int MaxLayoutOffset = SpiritMax + 4;

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
                    new() { Id = "infinite-health", Name = "Vida ilimitada", Description = "Mantém a vida preenchida enquanto estiver ativado.", Type = TrainerFeatureType.Toggle },
                    new() { Id = "infinite-stamina", Name = "Vigor ilimitado", Description = "Mantém o vigor preenchido enquanto estiver ativado.", Type = TrainerFeatureType.Toggle },
                    new() { Id = "infinite-spirit", Name = "Espírito ilimitado", Description = "Mantém o espírito preenchido enquanto estiver ativado.", Type = TrainerFeatureType.Toggle }
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

        if (_health) Restore(_runtime.HealthCurrent, _runtime.HealthMax, "Vida");
        if (_stamina) Restore(_runtime.StaminaCurrent, _runtime.StaminaMax, "Vigor");
        if (_spirit) Restore(_runtime.SpiritCurrent, _runtime.SpiritMax, "Espírito");
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
            "Layout: bbfox 2026-08-01 / 4 Bytes",
            "Offsets: HP +8/+18 | Vigor +6C8/+6D8 | Espírito +758/+768",
            "Seleção: stats plausíveis -> root+58 -> marker+18 -> actor+20 -> vtable/type/posição"
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

        if (anchors.Count > 0 && TryResolveCurrentLayout(anchors, out var state, out var scanLog, ct))
        {
            _runtime = state;
            log.AddRange(scanLog);
            log.Add($"Método vencedor: {_runtime.Method}");
            log.Add($"StatsBase: 0x{_runtime.StatsBase.ToInt64():X}");
            if (_runtime.Actor != 0) log.Add($"Actor: 0x{_runtime.Actor.ToInt64():X}");
            log.Add($"HP current/max: 0x{_runtime.HealthCurrent.ToInt64():X} / 0x{_runtime.HealthMax.ToInt64():X}");
            log.Add($"STA current/max: 0x{_runtime.StaminaCurrent.ToInt64():X} / 0x{_runtime.StaminaMax.ToInt64():X}");
            log.Add($"SPI current/max: 0x{_runtime.SpiritCurrent.ToInt64():X} / 0x{_runtime.SpiritMax.ToInt64():X}");
            DiagnosticReport = string.Join(Environment.NewLine, log);
            RuntimeStatus = "Pronto • layout 4 Bytes validado • Vida/Vigor/Espírito disponíveis";
            LastError = string.Empty;
            return true;
        }

        if (anchors.Count > 0)
        {
            _ = TryResolveCurrentLayout(anchors, out _, out var failureLog, ct);
            log.AddRange(failureLog);
        }

        DiagnosticReport = string.Join(Environment.NewLine, log);
        RuntimeStatus = "Jogo conectado, mas o jogador ainda não foi identificado com segurança. Use “Copiar diagnóstico”.";
        LastError = RuntimeStatus;
        return false;
    }

    private bool TryResolveCurrentLayout(
        IEnumerable<nint> anchors,
        out RuntimeState state,
        out List<string> log,
        CancellationToken ct)
    {
        state = new RuntimeState();
        log = new List<string> { "4-byte stat scan:" };

        var anchorValues = anchors.Select(a => a.ToInt64()).Distinct().ToArray();
        var regions = GetPrivateWritableRegions(anchorValues);
        var candidates = new Dictionary<long, StatCandidate>();
        long scanned = 0;

        foreach (var region in regions)
        {
            if (scanned >= StatScanBudget || candidates.Count > 24) break;
            long regionOffset = 0;

            while (regionOffset < region.Size && scanned < StatScanBudget && candidates.Count <= 24)
            {
                ct.ThrowIfCancellationRequested();
                var remaining = region.Size - regionOffset;
                var primary = (int)Math.Min(Math.Min(ChunkSize, remaining), StatScanBudget - scanned);
                if (primary < 0x1000) break;

                var readLength = (int)Math.Min(remaining, (long)primary + MaxLayoutOffset);
                var address = region.BaseAddress + (nint)regionOffset;
                if (_memory!.TryReadBytes(address, readLength, out var bytes))
                    Scan4ByteCandidates(address, bytes, primary, candidates);

                scanned += primary;
                regionOffset += primary;
            }
        }

        log.Add($"  varrido={scanned / (1024d * 1024):F1} MB; candidatos={candidates.Count}");
        foreach (var c in candidates.Values.OrderBy(c => c.Address).Take(20))
        {
            log.Add($"  0x{c.Address:X} | HP={c.Health.Current}/{c.Health.Max} | STA={c.Stamina.Current}/{c.Stamina.Max} | SPI={c.Spirit.Current}/{c.Spirit.Max} | IDs={c.HealthId}/{c.StaminaId}/{c.SpiritId}");
        }

        if (candidates.Count == 0)
            return false;

        if (!TryResolveBacklinkChain(candidates.Values.ToArray(), anchorValues, out var selected, out var actor, out var chainLog, ct))
        {
            log.AddRange(chainLog);
            return false;
        }

        log.AddRange(chainLog);
        state = BuildRuntime(selected, actor, "4-byte heap + reverse actor chain");
        return true;
    }

    private bool TryResolveBacklinkChain(
        IReadOnlyList<StatCandidate> candidates,
        IReadOnlyList<long> anchors,
        out StatCandidate selected,
        out nint selectedActor,
        out List<string> log,
        CancellationToken ct)
    {
        selected = default;
        selectedActor = 0;
        log = new List<string> { "Reverse pointer chain:" };

        var statsTargets = candidates.Select(c => c.Address).ToHashSet();
        var statsToRoots = FindExpectedOwners(statsTargets, 0x58, anchors.Concat(statsTargets).ToArray(), ct, out var pass1Bytes);
        log.Add($"  pass1 stats <- root+58: {statsToRoots.Sum(kv => kv.Value.Count)} refs / {pass1Bytes / (1024d * 1024):F1} MB");

        var rootTargets = statsToRoots.Values.SelectMany(x => x).Select(x => x.ToInt64()).ToHashSet();
        if (rootTargets.Count == 0) return false;

        var rootToMarkers = FindExpectedOwners(rootTargets, 0x18, anchors.Concat(rootTargets).ToArray(), ct, out var pass2Bytes);
        log.Add($"  pass2 root <- marker+18: {rootToMarkers.Sum(kv => kv.Value.Count)} refs / {pass2Bytes / (1024d * 1024):F1} MB");

        var markerTargets = rootToMarkers.Values.SelectMany(x => x).Select(x => x.ToInt64()).ToHashSet();
        if (markerTargets.Count == 0) return false;

        var markerToActors = FindExpectedOwners(markerTargets, 0x20, anchors.Concat(markerTargets).ToArray(), ct, out var pass3Bytes);
        log.Add($"  pass3 marker <- actor+20: {markerToActors.Sum(kv => kv.Value.Count)} refs / {pass3Bytes / (1024d * 1024):F1} MB");

        var scored = new List<ResolvedCandidate>();

        foreach (var candidate in candidates)
        {
            if (!statsToRoots.TryGetValue(candidate.Address, out var roots)) continue;

            foreach (var root in roots)
            {
                if (!rootToMarkers.TryGetValue(root.ToInt64(), out var markers)) continue;

                foreach (var marker in markers)
                {
                    if (!markerToActors.TryGetValue(marker.ToInt64(), out var actors)) continue;

                    foreach (var actor in actors)
                    {
                        if (!TryScoreActor(actor, out var score, out var detail)) continue;
                        if (candidate.HealthId == 0 && candidate.StaminaId == 17 && candidate.SpiritId == 18) score += 30;

                        scored.Add(new ResolvedCandidate(candidate, actor, score, detail));
                        log.Add($"  0x{candidate.Address:X} -> root 0x{root.ToInt64():X} -> marker 0x{marker.ToInt64():X} -> actor 0x{actor.ToInt64():X} | score={score} | {detail}");
                    }
                }
            }
        }

        if (scored.Count == 0)
        {
            log.Add("  nenhuma cadeia completa chegou a um actor com vtable válida");
            return false;
        }

        var ranked = scored.OrderByDescending(x => x.Score).ToArray();
        if (ranked.Length > 1 && ranked[1].Score == ranked[0].Score)
        {
            log.Add($"  ambíguo: dois candidatos empataram com score {ranked[0].Score}");
            return false;
        }

        selected = ranked[0].Candidate;
        selectedActor = ranked[0].Actor;
        log.Add($"  => vencedor: stats 0x{selected.Address:X}, actor 0x{selectedActor.ToInt64():X}, score {ranked[0].Score}");
        return true;
    }

    private Dictionary<long, List<nint>> FindExpectedOwners(
        IReadOnlySet<long> targets,
        int fieldOffset,
        IReadOnlyList<long> priority,
        CancellationToken ct,
        out long scannedBytes)
    {
        var result = targets.ToDictionary(x => x, _ => new List<nint>());
        scannedBytes = 0;
        if (targets.Count == 0) return result;

        foreach (var region in GetPrivateWritableRegions(priority))
        {
            if (scannedBytes >= BackRefBudget) break;
            long regionOffset = 0;

            while (regionOffset < region.Size && scannedBytes < BackRefBudget)
            {
                ct.ThrowIfCancellationRequested();
                var remaining = region.Size - regionOffset;
                var length = (int)Math.Min(Math.Min(ChunkSize, remaining), BackRefBudget - scannedBytes);
                if (length < 0x1000) break;

                var baseAddress = region.BaseAddress + (nint)regionOffset;
                if (_memory!.TryReadBytes(baseAddress, length, out var bytes))
                {
                    for (var offset = 0; offset <= bytes.Length - 8; offset += 8)
                    {
                        var raw = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(offset, 8));
                        if (!targets.Contains(raw)) continue;

                        var pointerField = baseAddress + offset;
                        var owner = pointerField - fieldOffset;
                        if (_memory.IsReadable(owner, fieldOffset + 8))
                            result[raw].Add(owner);
                    }
                }

                scannedBytes += length;
                regionOffset += length;
            }
        }

        return result;
    }

    private IReadOnlyList<MemoryRegionInfo> GetPrivateWritableRegions(IReadOnlyList<long> priority)
        => _memory!.GetReadableRegions(true)
            .Where(r => r.Type == MemPrivate && r.Size >= 0x1000 && r.BaseAddress.ToInt64() >= 0x1_0000_0000L)
            .OrderBy(r => Distance(r, priority))
            .ToArray();

    private static void Scan4ByteCandidates(
        nint baseAddress,
        byte[] bytes,
        int primaryLength,
        IDictionary<long, StatCandidate> candidates)
    {
        var limit = Math.Min(primaryLength, bytes.Length - MaxLayoutOffset);
        if (limit <= 0) return;

        for (var offset = 0; offset <= limit; offset += 8)
        {
            if (!ReadPair(bytes, offset + HpCurrent, offset + HpMax, out var hp)) continue;
            if (!ReadPair(bytes, offset + StaminaCurrent, offset + StaminaMax, out var stamina)) continue;
            if (!ReadPair(bytes, offset + SpiritCurrent, offset + SpiritMax, out var spirit)) continue;

            var healthId = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset, 4));
            var staminaId = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset + 0x6C0, 4));
            var spiritId = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset + 0x750, 4));

            var address = baseAddress.ToInt64() + offset;
            candidates.TryAdd(address, new StatCandidate(address, hp, stamina, spirit, healthId, staminaId, spiritId));
        }
    }

    private static bool ReadPair(byte[] bytes, int currentOffset, int maxOffset, out StatPair pair)
    {
        pair = default;
        if (currentOffset < 0 || maxOffset < 0 || maxOffset + 4 > bytes.Length) return false;

        var current = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(currentOffset, 4));
        var max = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(maxOffset, 4));
        if (!Plausible(current, max)) return false;

        pair = new StatPair(current, max);
        return true;
    }

    private bool TryScoreActor(nint actor, out int score, out string detail)
    {
        score = 0;
        detail = string.Empty;

        if (_memory is null || actor == 0 || IsInsideModule(actor) || !_memory.IsReadable(actor, 0x60))
            return false;
        if (!_memory.TryReadPointer(actor, out var vtable) || !IsInsideModule(vtable))
            return false;

        score = 100;
        var parts = new List<string> { $"vtable=0x{vtable.ToInt64():X}" };

        if (TryReadActorType(actor, out var type))
        {
            score += type == 0x01 ? 60 : 5;
            parts.Add($"type=0x{type:X2}");
        }

        if (TryReadPosition(actor, out var x, out var y, out var z))
        {
            score += 20;
            parts.Add($"pos={x:F1},{y:F1},{z:F1}");
        }

        detail = string.Join(", ", parts);
        return true;
    }

    private bool TryReadActorType(nint actor, out byte type)
    {
        type = 0;
        if (_memory is null) return false;
        if (!_memory.TryReadPointer(actor + 0x48, out var component)) return false;
        if (!_memory.TryReadPointer(component + 0x08, out var typedActor)) return false;
        if (!_memory.TryReadPointer(typedActor + 0x88, out var typePtr)) return false;
        return _memory.TryRead<byte>(typePtr + 0x01, out type);
    }

    private bool TryReadPosition(nint actor, out float x, out float y, out float z)
    {
        x = y = z = 0;
        if (_memory is null) return false;
        if (!_memory.TryReadPointer(actor + 0x40, out var inner)) return false;
        if (!_memory.TryReadPointer(inner + 0x08, out var core)) return false;
        if (!_memory.TryReadPointer(core + 0x248, out var pos)) return false;
        if (!_memory.TryRead<float>(pos + 0x90, out x)) return false;
        if (!_memory.TryRead<float>(pos + 0x94, out y)) return false;
        if (!_memory.TryRead<float>(pos + 0x98, out z)) return false;

        return float.IsFinite(x) && float.IsFinite(y) && float.IsFinite(z)
               && Math.Abs(x) < 10_000_000f
               && Math.Abs(y) < 10_000_000f
               && Math.Abs(z) < 10_000_000f;
    }

    private bool ValidateRuntime()
        => _runtime.IsResolved
           && ReadLivePair(_runtime.HealthCurrent, _runtime.HealthMax, out _)
           && ReadLivePair(_runtime.StaminaCurrent, _runtime.StaminaMax, out _)
           && ReadLivePair(_runtime.SpiritCurrent, _runtime.SpiritMax, out _);

    private bool ReadLivePair(nint currentAddress, nint maxAddress, out StatPair pair)
    {
        pair = default;
        if (_memory is null) return false;
        if (!_memory.TryRead<uint>(currentAddress, out var current)) return false;
        if (!_memory.TryRead<uint>(maxAddress, out var max)) return false;
        if (!Plausible(current, max)) return false;
        pair = new StatPair(current, max);
        return true;
    }

    private void Restore(nint currentAddress, nint maxAddress, string label)
    {
        if (!ReadLivePair(currentAddress, maxAddress, out var stat))
        {
            _runtime = new RuntimeState();
            RuntimeStatus = $"{label}: endereço deixou de validar. Relocalizando...";
            return;
        }

        var target = (uint)Math.Min((ulong)uint.MaxValue, (ulong)stat.Max * 10UL);
        if (stat.Current < target)
            _memory!.Write(currentAddress, target);
    }

    private static bool Plausible(uint current, uint max)
        => max > 0
           && max <= 1_000_000_000U
           && (ulong)current <= (ulong)max * 20UL;

    private bool IsInsideModule(nint address)
    {
        if (_memory is null) return false;
        var value = address.ToInt64();
        var start = _memory.MainModuleBase.ToInt64();
        return value >= start && value < start + _memory.MainModuleSize;
    }

    private static long Distance(MemoryRegionInfo region, IReadOnlyList<long> anchors)
    {
        if (anchors.Count == 0) return long.MaxValue / 2;
        var start = region.BaseAddress.ToInt64();
        var end = start + region.Size;
        var best = long.MaxValue;

        foreach (var anchor in anchors)
        {
            var distance = anchor >= start && anchor < end
                ? 0
                : anchor < start ? start - anchor : anchor - end;
            if (distance < best) best = distance;
        }

        return best;
    }

    private static RuntimeState BuildRuntime(StatCandidate candidate, nint actor, string method)
    {
        var stats = (nint)candidate.Address;
        return new RuntimeState
        {
            IsResolved = true,
            Actor = actor,
            StatsBase = stats,
            HealthCurrent = stats + HpCurrent,
            HealthMax = stats + HpMax,
            StaminaCurrent = stats + StaminaCurrent,
            StaminaMax = stats + StaminaMax,
            SpiritCurrent = stats + SpiritCurrent,
            SpiritMax = stats + SpiritMax,
            Method = method
        };
    }

    private readonly record struct WorldPattern(string Name, string Signature, int Disp, int End);
    private readonly record struct StatPair(uint Current, uint Max);
    private readonly record struct StatCandidate(long Address, StatPair Health, StatPair Stamina, StatPair Spirit, int HealthId, int StaminaId, int SpiritId);
    private readonly record struct ResolvedCandidate(StatCandidate Candidate, nint Actor, int Score, string Detail);

    private sealed class RuntimeState
    {
        public bool IsResolved { get; set; }
        public nint Actor { get; set; }
        public nint StatsBase { get; set; }
        public nint HealthCurrent { get; set; }
        public nint HealthMax { get; set; }
        public nint StaminaCurrent { get; set; }
        public nint StaminaMax { get; set; }
        public nint SpiritCurrent { get; set; }
        public nint SpiritMax { get; set; }
        public string Method { get; set; } = string.Empty;
    }
}
