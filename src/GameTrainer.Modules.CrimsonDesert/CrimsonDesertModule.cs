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
    private const long BackRefBudget = 512L * 1024 * 1024;
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
            "Modo: resolução automática; sem mapeamento manual",
            "Seleção: tripla de stats + backlink de objeto/vtable + type/posição quando disponíveis"
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
            log.Add($"Método vencedor: {_runtime.Method}");
            if (_runtime.Actor != 0)
                log.Add($"Actor/Object: 0x{_runtime.Actor.ToInt64():X}");
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

        var found = new Dictionary<long, StatCandidate>();
        long scanned = 0;

        foreach (var region in regions)
        {
            if (scanned >= ScanBudget || found.Count > 12) break;
            long offset = 0;
            while (offset < region.Size && scanned < ScanBudget && found.Count <= 12)
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
        foreach (var candidate in found.Values.OrderBy(c => c.Address).Take(12))
        {
            log.Add(
                $"  0x{candidate.Address:X}: {candidate.Layout.Name} | " +
                $"HP={candidate.Health.Current}/{candidate.Health.Max} | " +
                $"STA={candidate.Stamina.Current}/{candidate.Stamina.Max} | " +
                $"SPI={candidate.Spirit.Current}/{candidate.Spirit.Max}");
        }

        AddOverlapDiagnostics(found.Values, log);

        if (found.Count == 1)
        {
            state = BuildRuntime(found.Values.Single(), 0, "Heap Stat Scan / único candidato");
            return true;
        }

        if (found.Count > 1
            && TrySelectByBackReferences(found.Values.ToArray(), anchorValues, out var selected, out var actor, out var backRefLog, ct))
        {
            log.AddRange(backRefLog);
            state = BuildRuntime(selected, actor, "Heap Stat Scan + backlink");
            return true;
        }

        if (found.Count > 1)
        {
            _ = TrySelectByBackReferences(found.Values.ToArray(), anchorValues, out _, out _, out var backRefFailure, ct);
            log.AddRange(backRefFailure);
        }

        return false;
    }

    private bool TrySelectByBackReferences(
        IReadOnlyList<StatCandidate> candidates,
        IReadOnlyList<long> anchors,
        out StatCandidate selected,
        out nint actor,
        out List<string> log,
        CancellationToken ct)
    {
        selected = default;
        actor = 0;
        log = new List<string> { "Backlink scan:" };

        var targets = candidates.ToDictionary(c => c.Address, c => c);
        var points = anchors.Concat(candidates.Select(c => c.Address)).Distinct().ToArray();
        var regions = _memory!.GetReadableRegions(true)
            .Where(r => r.Type == MemPrivate && r.Size >= 0x1000 && r.BaseAddress.ToInt64() >= 0x1_0000_0000L)
            .OrderBy(r => Distance(r, points))
            .ToArray();

        var scores = candidates.ToDictionary(c => c.Address, _ => new CandidateScore());
        long scanned = 0;

        foreach (var region in regions)
        {
            if (scanned >= BackRefBudget) break;
            long offset = 0;
            while (offset < region.Size && scanned < BackRefBudget)
            {
                ct.ThrowIfCancellationRequested();
                var remaining = region.Size - offset;
                var length = (int)Math.Min(Math.Min(ChunkSize, remaining), BackRefBudget - scanned);
                if (length < 0x1000) break;

                var address = region.BaseAddress + (nint)offset;
                if (_memory.TryReadBytes(address, length, out var bytes))
                    ScanBackReferences(address, bytes, targets, scores);

                scanned += length;
                offset += length;
            }
        }

        log.Add($"  varrido={scanned / (1024d * 1024):F1} MB");
        foreach (var candidate in candidates.OrderBy(c => c.Address))
        {
            var score = scores[candidate.Address];
            log.Add(
                $"  0x{candidate.Address:X}: refs={score.ReferenceCount}, direct+58={score.DirectOwnerCount}, " +
                $"score={score.BestScore}, owner={(score.BestOwner == 0 ? "-" : $"0x{score.BestOwner.ToInt64():X}")}" +
                (string.IsNullOrWhiteSpace(score.BestDetail) ? string.Empty : $" | {score.BestDetail}"));
        }

        var ranked = candidates
            .Select(c => new { Candidate = c, Score = scores[c.Address] })
            .OrderByDescending(x => x.Score.BestScore)
            .ThenByDescending(x => x.Score.DirectOwnerCount)
            .ThenByDescending(x => x.Score.ReferenceCount)
            .ToArray();

        if (ranked.Length == 0 || ranked[0].Score.BestScore < 100)
        {
            log.Add("  nenhum candidato recebeu backlink direto +0x58 com objeto/vtable válida");
            return false;
        }

        if (ranked.Length > 1
            && ranked[1].Score.BestScore == ranked[0].Score.BestScore
            && ranked[1].Score.DirectOwnerCount == ranked[0].Score.DirectOwnerCount)
        {
            log.Add("  resultado ainda ambíguo: os dois melhores candidatos empataram");
            return false;
        }

        selected = ranked[0].Candidate;
        actor = ranked[0].Score.BestOwner;
        log.Add($"  => vencedor 0x{selected.Address:X} ({selected.Layout.Name}), score={ranked[0].Score.BestScore}");
        return true;
    }

    private void ScanBackReferences(
        nint baseAddress,
        byte[] bytes,
        IReadOnlyDictionary<long, StatCandidate> targets,
        IDictionary<long, CandidateScore> scores)
    {
        for (var offset = 0; offset <= bytes.Length - 8; offset += 8)
        {
            var raw = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(offset, 8));
            if (!targets.ContainsKey(raw)) continue;

            var score = scores[raw];
            score.ReferenceCount++;

            var pointerField = baseAddress + offset;
            var possibleOwner = pointerField - 0x58;
            if (TryScoreOwner(possibleOwner, out var ownerScore, out var detail))
            {
                score.DirectOwnerCount++;
                if (ownerScore > score.BestScore)
                {
                    score.BestScore = ownerScore;
                    score.BestOwner = possibleOwner;
                    score.BestDetail = detail;
                }
            }
        }
    }

    private bool TryScoreOwner(nint owner, out int score, out string detail)
    {
        score = 0;
        detail = string.Empty;

        if (_memory is null || owner == 0 || IsInsideModule(owner) || !_memory.IsReadable(owner, 0x60))
            return false;

        if (!_memory.TryReadPointer(owner, out var vtable) || !IsInsideModule(vtable))
            return false;

        score = 100;
        var details = new List<string> { $"vtable=0x{vtable.ToInt64():X}" };

        if (TryReadActorType(owner, out var type))
        {
            score += type == 0x01 ? 50 : 5;
            details.Add($"type=0x{type:X2}");
        }

        if (TryReadPosition(owner, out var x, out var y, out var z))
        {
            score += 20;
            details.Add($"pos={x:F1},{y:F1},{z:F1}");
        }

        detail = string.Join(", ", details);
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

    private bool IsInsideModule(nint address)
    {
        if (_memory is null) return false;
        var value = address.ToInt64();
        var start = _memory.MainModuleBase.ToInt64();
        var end = start + _memory.MainModuleSize;
        return value >= start && value < end;
    }

    private static void AddOverlapDiagnostics(IEnumerable<StatCandidate> candidates, List<string> log)
    {
        var list = candidates.OrderBy(c => c.Address).ToArray();
        var overlaps = new List<string>();

        for (var i = 0; i < list.Length; i++)
        {
            for (var j = i + 1; j < list.Length; j++)
            {
                var a = list[i];
                var b = list[j];
                var aStamina = a.Address + a.Layout.StaminaOffset;
                var aSpirit = a.Address + a.Layout.SpiritOffset;
                var bStamina = b.Address + b.Layout.StaminaOffset;
                var bSpirit = b.Address + b.Layout.SpiritOffset;

                if (aStamina == bStamina && aSpirit == bSpirit)
                    overlaps.Add($"  sobreposição: 0x{a.Address:X}/{a.Layout.Name} e 0x{b.Address:X}/{b.Layout.Name} compartilham Vigor/Espírito");
            }
        }

        if (overlaps.Count > 0)
        {
            log.Add("Estruturas sobrepostas:");
            log.AddRange(overlaps);
        }
    }

    private static RuntimeState BuildRuntime(StatCandidate winner, nint actor, string method)
    {
        var stats = (nint)winner.Address;
        return new RuntimeState
        {
            IsResolved = true,
            Actor = actor,
            Stats = stats,
            Health = stats,
            Stamina = stats + winner.Layout.StaminaOffset,
            Spirit = stats + winner.Layout.SpiritOffset,
            Layout = winner.Layout.Name + "/heap",
            Method = method
        };
    }

    private static void ScanBuffer(nint baseAddress, byte[] bytes, int primaryLength, Dictionary<long, StatCandidate> found)
    {
        var maxOffset = Layouts.Max(l => l.SpiritOffset) + 0x20;
        var limit = Math.Min(primaryLength, bytes.Length - maxOffset);
        if (limit <= 0) return;

        for (var offset = 0; offset <= limit; offset += 8)
        {
            if (!BufferedStat(bytes, offset, 0, out var health)) continue;

            foreach (var layout in Layouts)
            {
                if (!BufferedStat(bytes, offset + layout.StaminaOffset, 17, out var stamina)) continue;
                if (!BufferedStat(bytes, offset + layout.SpiritOffset, 18, out var spirit)) continue;

                var address = baseAddress.ToInt64() + offset;
                found.TryAdd(address, new StatCandidate(address, layout, health, stamina, spirit));
            }
        }
    }

    private static bool BufferedStat(byte[] bytes, int offset, int type, out Stat stat)
    {
        stat = default;
        if (offset < 0 || offset + 0x20 > bytes.Length) return false;
        if (BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset, 4)) != type) return false;
        var current = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(offset + CurrentOffset, 8));
        var max = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(offset + MaxOffset, 8));
        if (!Plausible(current, max)) return false;
        stat = new Stat(current, max);
        return true;
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
    private readonly record struct StatCandidate(long Address, StatLayout Layout, Stat Health, Stat Stamina, Stat Spirit);

    private sealed class CandidateScore
    {
        public int ReferenceCount { get; set; }
        public int DirectOwnerCount { get; set; }
        public int BestScore { get; set; }
        public nint BestOwner { get; set; }
        public string BestDetail { get; set; } = string.Empty;
    }

    private sealed class RuntimeState
    {
        public bool IsResolved { get; set; }
        public nint Actor { get; set; }
        public nint Stats { get; set; }
        public nint Health { get; set; }
        public nint Stamina { get; set; }
        public nint Spirit { get; set; }
        public string Layout { get; set; } = string.Empty;
        public string Method { get; set; } = string.Empty;
    }
}
