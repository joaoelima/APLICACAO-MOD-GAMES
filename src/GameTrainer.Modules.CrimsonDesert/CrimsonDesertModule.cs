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
    private const int StatTypeOffset = 0x00;
    private const int StatCurrentOffset = 0x08;
    private const int StatMaxOffset = 0x18;

    private const int ActorManagerBodyStart = 0xD0;
    private const int ActorManagerBodyCount = 8;
    private const int ActorManagerProbeWindow = 0x400;

    private const int ActorTypeComponentOffset = 0x48;
    private const int TypeComponentActorOffset = 0x08;
    private const int TypeActorTypePtrOffset = 0x88;
    private const int TypeByteOffset = 0x01;
    private const byte LocalPlayerType = 0x01;

    private const int ActorToInnerOffset = 0x40;
    private const int InnerToCoreOffset = 0x08;
    private const int CoreToPositionStructOffset = 0x248;
    private const int PositionXOffset = 0x90;
    private const int PositionYOffset = 0x94;
    private const int PositionZOffset = 0x98;

    private const long HeapScanBudgetBytes = 768L * 1024 * 1024;
    private const long HeapScanRadiusBytes = 32L * 1024 * 1024 * 1024;
    private const int HeapChunkSize = 4 * 1024 * 1024;
    private const int MaxHeapCandidates = 12;

    private static readonly int[] NestedActorOffsets =
    {
        0x18, 0x20, 0x28, 0x30, 0x38, 0x40, 0x48, 0x50, 0x58, 0x60, 0x68,
        0xD0, 0xD8, 0xE0, 0xE8, 0xF0, 0xF8, 0x100, 0x108, 0x168, 0x268
    };

    private static readonly StatLayout[] StatLayouts =
    {
        new(0x510, 0x5A0, "Int64-mai-2026"),
        new(0x480, 0x510, "Int64-legado-v1.01")
    };

    private const int StaticPlayerBaseRva = 0x05CC7618;

    private const string PlayerBaseDiscoveryPattern =
        "48 8B 0D ? ? ? ? E8 ? ? ? ? 41 B0 01 48 8B 53 08 48 8D 4C 24 40";

    private const string CurrentPlayerPattern =
        "48 8B 53 08 48 8D 4C 24 78 E8 ? ? ? ? 90 48 8B 43 68 48 8B 88 A0 01 00 00 48 8B 41 38 0F B7 48 20";

    private static readonly WorldSystemPattern[] WorldSystemPatterns =
    {
        new("P1", "48 83 EC 28 48 8B 0D ? ? ? ? 48 8B 49 ? E8 ? ? ? ? 84 C0 0F 94 C0 48 83 C4 28 C3", 7, 11),
        new("P2", "80 B8 ? ? ? ? 00 75 ? 48 8B 05 ? ? ? ? 48 8B 88 ? ? ? ?", 12, 16),
        new("P3", "48 8B 0D ? ? ? ? 48 8B 49 ? E8 ? ? ? ? 84 C0 0F 94 C0", 3, 7)
    };

    private static readonly int[] PlayerBaseCharacterSlots = { 0x68, 0xE0, 0x168, 0x268 };

    private ProcessMemory? _memory;
    private PlayerRuntime _runtime = new();
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
                    new()
                    {
                        Id = "infinite-health",
                        Name = "Vida ilimitada",
                        Description = "Mantém a vida no valor máximo enquanto estiver ativado.",
                        Type = TrainerFeatureType.Toggle
                    },
                    new()
                    {
                        Id = "infinite-stamina",
                        Name = "Vigor ilimitado",
                        Description = "Mantém o vigor no valor máximo enquanto estiver ativado.",
                        Type = TrainerFeatureType.Toggle
                    },
                    new()
                    {
                        Id = "infinite-spirit",
                        Name = "Espírito ilimitado",
                        Description = "Mantém o espírito no valor máximo enquanto estiver ativado.",
                        Type = TrainerFeatureType.Toggle
                    }
                }
            },
            new TrainerSection
            {
                Name = "Combate",
                Features = new TrainerFeature[]
                {
                    new()
                    {
                        Id = "one-hit-kill",
                        Name = "Super Dano / Mortes com Um Golpe",
                        Description = "Em desenvolvimento: fica indisponível até o atributo de dano ser validado com segurança.",
                        Type = TrainerFeatureType.Toggle,
                        IsAvailable = false
                    }
                }
            }
        }
    };

    public Task AttachAsync(ProcessMemory processMemory, CancellationToken cancellationToken = default)
    {
        _memory = processMemory;
        _runtime = new PlayerRuntime();
        LastError = string.Empty;
        DiagnosticReport = "Iniciando resolução automática da memória do Crimson Desert...";
        ResolveRuntime(force: true, cancellationToken);
        return Task.CompletedTask;
    }

    public Task<bool> ReprobeAsync(CancellationToken cancellationToken = default)
    {
        if (_memory is null || !_memory.IsAttached)
        {
            LastError = "O Crimson Desert não está conectado.";
            RuntimeStatus = LastError;
            DiagnosticReport = LastError;
            return Task.FromResult(false);
        }

        _runtime = new PlayerRuntime();
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

        var success = featureId switch
        {
            "infinite-health" => SetFlag(ref _infiniteHealth, enabled),
            "infinite-stamina" => SetFlag(ref _infiniteStamina, enabled),
            "infinite-spirit" => SetFlag(ref _infiniteSpirit, enabled),
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

        if (!_infiniteHealth && !_infiniteStamina && !_infiniteSpirit)
            return Task.CompletedTask;

        if (!EnsureRuntime(cancellationToken))
            return Task.CompletedTask;

        try
        {
            if (_infiniteHealth)
                RestoreStat(_runtime.HealthEntry, HealthId, "Vida");
            if (_infiniteStamina)
                RestoreStat(_runtime.StaminaEntry, StaminaId, "Vigor");
            if (_infiniteSpirit)
                RestoreStat(_runtime.SpiritEntry, SpiritId, "Espírito");
        }
        catch
        {
            InvalidateRuntime("A estrutura do jogador mudou. Relocalizando automaticamente...");
        }

        return Task.CompletedTask;
    }

    private bool SetFlag(ref bool field, bool enabled)
    {
        field = enabled;
        LastError = string.Empty;
        return true;
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

        var diagnostics = new List<string>
        {
            "Diagnóstico v0.2.8",
            $"Módulo: 0x{_memory.MainModuleBase.ToInt64():X} / 0x{_memory.MainModuleSize:X} bytes",
            "Modo: resolução automática; sem mapeamento manual",
            "Estratégia A: WorldSystem -> ActorManager -> objetos de heap validados por vtable",
            "Estratégia B: busca automática do bloco HP/Vigor/Espírito em MEM_PRIVATE"
        };

        AddAobDiagnostic(diagnostics, "CurrentPlayer", CurrentPlayerPattern);
        AddAobDiagnostic(diagnostics, "PlayerBaseDiscovery", PlayerBaseDiscoveryPattern);

        var heapAnchors = new HashSet<nint>();

        try
        {
            foreach (var pattern in WorldSystemPatterns)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var match = _memory.FindPatternInMainModule(pattern.Signature);
                if (!match.HasValue)
                {
                    diagnostics.Add($"WorldSystem {pattern.Name}: assinatura não encontrada");
                    continue;
                }

                var globalSlot = _memory.ResolveRipRelative(match.Value, pattern.DisplacementOffset, pattern.InstructionEndOffset);
                if (!_memory.TryReadPointer(globalSlot, out var worldSystem))
                {
                    diagnostics.Add($"WorldSystem {pattern.Name}: ponteiro global inválido");
                    continue;
                }

                heapAnchors.Add(worldSystem);

                if (!_memory.TryReadPointer(worldSystem + 0x30, out var actorManager))
                {
                    diagnostics.Add($"WorldSystem {pattern.Name}: ActorManager +0x30 inválido");
                    continue;
                }

                heapAnchors.Add(actorManager);
                diagnostics.Add($"WorldSystem {pattern.Name}: WorldSystem=0x{worldSystem.ToInt64():X}, ActorManager=0x{actorManager.ToInt64():X}");

                if (_memory.TryReadPointer(actorManager + 0x28, out var oldField))
                    diagnostics.Add($"  +0x28=0x{oldField.ToInt64():X} ({DescribeAddress(oldField)}) — somente telemetria");

                if (TryResolvePlayerFromActorManager(actorManager, out var runtime, out var actorDiagnostics))
                {
                    runtime.WorldSystem = worldSystem;
                    runtime.ActorManager = actorManager;
                    diagnostics.AddRange(actorDiagnostics);
                    CompleteSuccessfulResolution(runtime, $"WorldSystem {pattern.Name} / heap actor", diagnostics);
                    return true;
                }

                diagnostics.AddRange(actorDiagnostics);
            }

            if (TryResolveViaPlayerBaseAob(out var aobRuntime, out var aobDetail))
            {
                diagnostics.Add($"PlayerBase AOB: {aobDetail}");
                CompleteSuccessfulResolution(aobRuntime, "PlayerBase AOB", diagnostics);
                return true;
            }
            diagnostics.Add($"PlayerBase AOB: {aobDetail}");

            if (TryResolveViaStaticPlayerBase(out var staticRuntime, out var staticDetail))
            {
                diagnostics.Add($"PlayerBase estático: {staticDetail}");
                CompleteSuccessfulResolution(staticRuntime, "PlayerBase estático", diagnostics);
                return true;
            }
            diagnostics.Add($"PlayerBase estático: {staticDetail}");

            if (heapAnchors.Count > 0
                && TryResolveStatsByAutomaticHeapScan(heapAnchors, out var heapRuntime, out var heapDiagnostics, cancellationToken))
            {
                diagnostics.AddRange(heapDiagnostics);
                CompleteSuccessfulResolution(heapRuntime, "Heap Stat Scan", diagnostics);
                return true;
            }

            if (heapAnchors.Count > 0)
            {
                _ = TryResolveStatsByAutomaticHeapScan(heapAnchors, out _, out var heapFailure, cancellationToken);
                diagnostics.AddRange(heapFailure);
            }

            DiagnosticReport = string.Join(Environment.NewLine, diagnostics);
            InvalidateRuntime(
                "Jogo conectado, mas os atributos desta build ainda não foram validados. Use “Copiar diagnóstico”.",
                scheduleRetry: false,
                preserveDiagnostic: true);
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
            diagnostics.Add($"Exceção: {ex.GetType().Name} - {ex.Message}");
            DiagnosticReport = string.Join(Environment.NewLine, diagnostics);
            InvalidateRuntime(
                "Falha durante a resolução automática. Use “Copiar diagnóstico”.",
                scheduleRetry: false,
                preserveDiagnostic: true);
            return false;
        }
    }

    private bool TryResolvePlayerFromActorManager(
        nint actorManager,
        out PlayerRuntime runtime,
        out List<string> diagnostics)
    {
        runtime = new PlayerRuntime();
        diagnostics = new List<string> { "ActorManager object scan:" };

        var roots = new HashSet<nint>();

        for (var i = 0; i < ActorManagerBodyCount; i++)
        {
            var offset = ActorManagerBodyStart + i * 8;
            if (_memory is not null && _memory.TryReadPointer(actorManager + offset, out var candidate))
                roots.Add(candidate);
        }

        for (var offset = 0; offset <= ActorManagerProbeWindow; offset += 8)
        {
            if (_memory is not null && _memory.TryReadPointer(actorManager + offset, out var candidate))
                roots.Add(candidate);
        }

        var candidates = new HashSet<nint>();
        foreach (var root in roots)
        {
            AddHeapObjectCandidate(candidates, root);
            if (_memory is null || IsInsideMainModule(root) || !_memory.IsReadable(root, 0x280))
                continue;

            foreach (var nestedOffset in NestedActorOffsets)
            {
                if (_memory.TryReadPointer(root + nestedOffset, out var nested))
                    AddHeapObjectCandidate(candidates, nested);
            }
        }

        diagnostics.Add($"  raízes={roots.Count}, objetos heap/vtable={candidates.Count}");

        var resolved = new List<ScoredRuntime>();
        foreach (var candidate in candidates.Take(256))
        {
            if (!TryResolveActorStats(candidate, out var candidateRuntime, out var statsDetail))
                continue;

            var score = 100;
            var typeText = "?";
            var positionText = "?";

            if (TryReadActorType(candidate, out var actorType, out _))
            {
                candidateRuntime.ActorType = actorType;
                typeText = $"0x{actorType:X2}";
                score += actorType == LocalPlayerType ? 50 : 5;
            }

            if (TryReadActorPosition(candidate, out var position, out _))
            {
                candidateRuntime.Position = position;
                candidateRuntime.HasPosition = true;
                positionText = $"{position.X:F1},{position.Y:F1},{position.Z:F1}";
                score += 20;
            }

            candidateRuntime.Actor = candidate;
            resolved.Add(new ScoredRuntime(candidateRuntime, score, statsDetail));
            diagnostics.Add($"  candidato 0x{candidate.ToInt64():X}: score={score}, type={typeText}, pos={positionText}, {statsDetail}");
        }

        if (resolved.Count == 0)
        {
            diagnostics.Add("  nenhum objeto candidato apresentou tripla de stats válida");
            return false;
        }

        var ordered = resolved.OrderByDescending(r => r.Score).ToArray();
        var best = ordered[0];

        if (ordered.Length > 1 && ordered[1].Score == best.Score && best.Score < 150)
        {
            diagnostics.Add($"  resolução ambígua: {ordered.Length} candidatos; dois melhores empataram em {best.Score}");
            return false;
        }

        runtime = best.Runtime;
        diagnostics.Add($"  => ator selecionado: 0x{runtime.Actor.ToInt64():X}, score={best.Score}");
        return true;
    }

    private void AddHeapObjectCandidate(HashSet<nint> candidates, nint candidate)
    {
        if (_memory is null || candidate == 0 || IsInsideMainModule(candidate) || !_memory.IsReadable(candidate, 0x60))
            return;

        // Método inspirado em object discovery por RTTI/vtable: um objeto C++ de heap
        // normalmente começa com um ponteiro de vtable que cai dentro do módulo do jogo.
        if (_memory.TryReadPointer(candidate, out var vtable) && IsInsideMainModule(vtable))
            candidates.Add(candidate);
    }

    private bool TryReadActorType(nint candidate, out byte type, out string detail)
    {
        type = 0;
        detail = string.Empty;

        if (_memory is null)
        {
            detail = "sem memória";
            return false;
        }

        if (!_memory.TryReadPointer(candidate + ActorTypeComponentOffset, out var component)
            || !_memory.TryReadPointer(component + TypeComponentActorOffset, out var actorForType)
            || !_memory.TryReadPointer(actorForType + TypeActorTypePtrOffset, out var typePtr)
            || !_memory.TryRead<byte>(typePtr + TypeByteOffset, out type))
        {
            detail = "cadeia de tipo não validou";
            return false;
        }

        detail = $"comp=0x{component.ToInt64():X}, actor=0x{actorForType.ToInt64():X}, ptr=0x{typePtr.ToInt64():X}";
        return true;
    }

    private bool TryReadActorPosition(nint actor, out PositionSnapshot position, out string detail)
    {
        position = default;
        detail = string.Empty;

        if (_memory is null)
        {
            detail = "sem memória";
            return false;
        }

        if (!_memory.TryReadPointer(actor + ActorToInnerOffset, out var inner)
            || !_memory.TryReadPointer(inner + InnerToCoreOffset, out var core)
            || !_memory.TryReadPointer(core + CoreToPositionStructOffset, out var posStruct)
            || !_memory.TryRead<float>(posStruct + PositionXOffset, out var x)
            || !_memory.TryRead<float>(posStruct + PositionYOffset, out var y)
            || !_memory.TryRead<float>(posStruct + PositionZOffset, out var z))
        {
            detail = "cadeia de posição não validou";
            return false;
        }

        if (!float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(z)
            || Math.Abs(x) > 10_000_000f
            || Math.Abs(y) > 10_000_000f
            || Math.Abs(z) > 10_000_000f)
        {
            detail = $"XYZ implausível ({x},{y},{z})";
            return false;
        }

        position = new PositionSnapshot(x, y, z);
        detail = $"inner=0x{inner.ToInt64():X}, core=0x{core.ToInt64():X}, pos=0x{posStruct.ToInt64():X}";
        return true;
    }

    private bool TryResolveActorStats(nint actor, out PlayerRuntime runtime, out string detail)
    {
        runtime = new PlayerRuntime();
        var attempts = new List<string>();

        if (_memory is null)
        {
            detail = "sem memória";
            return false;
        }

        if (_memory.TryReadPointer(actor + 0x58, out var directStats))
        {
            if (TryBuildRuntimeFromStatsBase(actor, directStats, out runtime, out var directDetail))
            {
                detail = $"actor+58 => {directDetail}";
                return true;
            }
            attempts.Add($"+58=0x{directStats.ToInt64():X}");
        }

        if (_memory.TryReadPointer(actor + 0x20, out var marker)
            && _memory.TryReadPointer(marker + 0x18, out var root)
            && _memory.TryReadPointer(root + 0x58, out var rootedStats))
        {
            if (TryBuildRuntimeFromStatsBase(actor, rootedStats, out runtime, out var rootedDetail))
            {
                runtime.Marker = marker;
                runtime.Root = root;
                detail = $"marker/root => {rootedDetail}";
                return true;
            }
            attempts.Add($"root+58=0x{rootedStats.ToInt64():X}");
        }

        // Alguns patches movem o componente dentro do objeto. Fazemos uma busca pequena,
        // somente por ponteiros do próprio ator, e validamos a tripla completa antes de aceitar.
        for (var offset = 0; offset <= 0x180; offset += 8)
        {
            if (!_memory.TryReadPointer(actor + offset, out var child))
                continue;

            if (TryBuildRuntimeFromStatsBase(actor, child, out runtime, out var childDetail))
            {
                detail = $"actor+0x{offset:X} -> stats => {childDetail}";
                return true;
            }

            if (_memory.TryReadPointer(child + 0x58, out var nestedStats)
                && TryBuildRuntimeFromStatsBase(actor, nestedStats, out runtime, out var nestedDetail))
            {
                detail = $"actor+0x{offset:X} -> child+58 => {nestedDetail}";
                return true;
            }
        }

        detail = attempts.Count == 0 ? "nenhum componente de stats validou" : string.Join(", ", attempts) + "; tripla inválida";
        return false;
    }

    private bool TryBuildRuntimeFromStatsBase(
        nint actor,
        nint statsBase,
        out PlayerRuntime runtime,
        out string detail)
    {
        runtime = new PlayerRuntime();

        foreach (var layout in StatLayouts)
        {
            var health = statsBase;
            var stamina = statsBase + layout.StaminaFromHealth;
            var spirit = statsBase + layout.SpiritFromHealth;

            if (!ValidateStatEntry(health, HealthId, out var hp)
                || !ValidateStatEntry(stamina, StaminaId, out var sta)
                || !ValidateStatEntry(spirit, SpiritId, out var spi))
                continue;

            runtime.IsResolved = true;
            runtime.Actor = actor;
            runtime.StatsBase = statsBase;
            runtime.HealthEntry = health;
            runtime.StaminaEntry = stamina;
            runtime.SpiritEntry = spirit;
            runtime.LayoutName = layout.Name;

            detail = $"Stats=0x{statsBase.ToInt64():X}; HP={FormatStat(hp)}, STA={FormatStat(sta)}, SPI={FormatStat(spi)}; {layout.Name}";
            return true;
        }

        detail = "bloco não corresponde a nenhum layout conhecido";
        return false;
    }

    private bool TryResolveStatsByAutomaticHeapScan(
        IEnumerable<nint> anchors,
        out PlayerRuntime runtime,
        out List<string> diagnostics,
        CancellationToken cancellationToken)
    {
        runtime = new PlayerRuntime();
        diagnostics = new List<string> { "Automatic heap stat scan:" };

        if (_memory is null)
        {
            diagnostics.Add("  memória indisponível");
            return false;
        }

        var anchorValues = anchors.Select(a => a.ToInt64()).Where(a => a > 0).Distinct().ToArray();
        if (anchorValues.Length == 0)
        {
            diagnostics.Add("  nenhuma âncora dinâmica disponível");
            return false;
        }

        var regions = _memory.GetReadableRegions(writableOnly: true)
            .Where(r => r.Type == MemPrivate && r.Size >= 0x1000 && r.BaseAddress.ToInt64() >= 0x1_0000_0000L)
            .Select(r => new RankedRegion(r, DistanceToAnchors(r, anchorValues)))
            .Where(r => r.Distance <= HeapScanRadiusBytes)
            .OrderBy(r => r.Distance)
            .ThenBy(r => r.Region.BaseAddress.ToInt64())
            .ToArray();

        diagnostics.Add($"  regiões privadas próximas: {regions.Length}; orçamento={HeapScanBudgetBytes / (1024 * 1024)} MB");

        var found = new Dictionary<long, HeapStatsCandidate>();
        long scanned = 0;

        foreach (var ranked in regions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (scanned >= HeapScanBudgetBytes || found.Count >= MaxHeapCandidates)
                break;

            var region = ranked.Region;
            var regionOffset = 0L;

            while (regionOffset < region.Size && scanned < HeapScanBudgetBytes && found.Count < MaxHeapCandidates)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var remainingRegion = region.Size - regionOffset;
                var remainingBudget = HeapScanBudgetBytes - scanned;
                var baseLength = (int)Math.Min(Math.Min(HeapChunkSize, remainingRegion), remainingBudget);
                if (baseLength < 0x1000)
                    break;

                var overlap = 0x600;
                var requested = (int)Math.Min(remainingRegion, (long)baseLength + overlap);
                var chunkAddress = region.BaseAddress + regionOffset;

                if (_memory.TryReadBytes(chunkAddress, requested, out var bytes))
                    ScanStatsChunk(chunkAddress, bytes, baseLength, found);

                scanned += baseLength;
                regionOffset += baseLength;
            }
        }

        diagnostics.Add($"  varrido={scanned / (1024d * 1024):F1} MB; triplas válidas={found.Count}");
        foreach (var candidate in found.Values.Take(8))
            diagnostics.Add($"  candidato 0x{candidate.StatsBase:X}: {candidate.Layout.Name}, HP={candidate.Health.Current}/{candidate.Health.Max}, STA={candidate.Stamina.Current}/{candidate.Stamina.Max}, SPI={candidate.Spirit.Current}/{candidate.Spirit.Max}");

        if (found.Count != 1)
        {
            if (found.Count > 1)
                diagnostics.Add("  resultado ambíguo; nenhuma escrita será habilitada até existir uma única tripla válida");
            return false;
        }

        var winner = found.Values.Single();
        runtime.IsResolved = true;
        runtime.StatsBase = (nint)winner.StatsBase;
        runtime.HealthEntry = (nint)winner.StatsBase;
        runtime.StaminaEntry = (nint)(winner.StatsBase + winner.Layout.StaminaFromHealth);
        runtime.SpiritEntry = (nint)(winner.StatsBase + winner.Layout.SpiritFromHealth);
        runtime.LayoutName = winner.Layout.Name + "/heap";
        return true;
    }

    private void ScanStatsChunk(
        nint chunkAddress,
        byte[] bytes,
        int primaryLength,
        Dictionary<long, HeapStatsCandidate> output)
    {
        var maximumOffset = StatLayouts.Max(l => l.SpiritFromHealth) + 0x20;
        var scanLimit = Math.Min(primaryLength, bytes.Length - maximumOffset);
        if (scanLimit <= 0)
            return;

        for (var offset = 0; offset <= scanLimit; offset += 8)
        {
            if (BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset, 4)) != HealthId)
                continue;

            if (!TryReadStatFromBuffer(bytes, offset, HealthId, out var health))
                continue;

            foreach (var layout in StatLayouts)
            {
                if (!TryReadStatFromBuffer(bytes, offset + layout.StaminaFromHealth, StaminaId, out var stamina)
                    || !TryReadStatFromBuffer(bytes, offset + layout.SpiritFromHealth, SpiritId, out var spirit))
                    continue;

                var address = chunkAddress.ToInt64() + offset;
                output.TryAdd(address, new HeapStatsCandidate(address, layout, health, stamina, spirit));
            }
        }
    }

    private static bool TryReadStatFromBuffer(byte[] bytes, int offset, int expectedType, out StatSnapshot stat)
    {
        stat = default;
        if (offset < 0 || offset + 0x20 > bytes.Length)
            return false;

        var type = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset + StatTypeOffset, 4));
        if (type != expectedType)
            return false;

        var current = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(offset + StatCurrentOffset, 8));
        var max = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(offset + StatMaxOffset, 8));
        if (!PlausibleStatValues(current, max))
            return false;

        stat = new StatSnapshot(type, current, max);
        return true;
    }

    private static long DistanceToAnchors(MemoryRegionInfo region, IReadOnlyList<long> anchors)
    {
        var start = region.BaseAddress.ToInt64();
        var end = start + region.Size;
        var best = long.MaxValue;

        foreach (var anchor in anchors)
        {
            var distance = anchor >= start && anchor < end
                ? 0
                : anchor < start
                    ? start - anchor
                    : anchor - end;
            if (distance < best)
                best = distance;
        }

        return best;
    }

    private bool ValidateStatEntry(nint entry, int expectedType, out StatSnapshot snapshot)
    {
        snapshot = default;

        if (_memory is null || entry == 0 || !_memory.IsReadable(entry, 0x20))
            return false;

        if (!_memory.TryRead<int>(entry + StatTypeOffset, out var type) || type != expectedType
            || !_memory.TryRead<long>(entry + StatCurrentOffset, out var current)
            || !_memory.TryRead<long>(entry + StatMaxOffset, out var max)
            return false;

        if (!PlausibleStatValues(current, max))
            return false;

        snapshot = new StatSnapshot(type, current, max);
        return true;
    }

    private static bool PlausibleStatValues(long current, long max)
        => max > 0
           && max < 10_000_000_000_000L
           && current >= 0
           && current <= Math.Min(100_000_000_000_000L, max * 20L);

    private bool TryResolveViaPlayerBaseAob(out PlayerRuntime runtime, out string detail)
    {
        runtime = new PlayerRuntime();
        detail = string.Empty;

        if (_memory is null)
        {
            detail = "sem memória";
            return false;
        }

        var match = _memory.FindPatternInMainModule(PlayerBaseDiscoveryPattern);
        if (!match.HasValue)
        {
            detail = "assinatura não encontrada";
            return false;
        }

        var storage = _memory.ResolveRipRelative(match.Value, 3, 7);
        if (!_memory.TryReadPointer(storage, out var playerBase))
        {
            detail = $"storage 0x{storage.ToInt64():X} inválido";
            return false;
        }

        return TryResolveFromKnownPlayerBase(playerBase, out runtime, out detail);
    }

    private bool TryResolveViaStaticPlayerBase(out PlayerRuntime runtime, out string detail)
    {
        runtime = new PlayerRuntime();
        detail = string.Empty;

        if (_memory is null)
        {
            detail = "sem memória";
            return false;
        }

        var storage = _memory.MainModuleBase + StaticPlayerBaseRva;
        if (!_memory.TryReadPointer(storage, out var playerBase))
        {
            detail = $"RVA 0x{StaticPlayerBaseRva:X} inválido";
            return false;
        }

        return TryResolveFromKnownPlayerBase(playerBase, out runtime, out detail);
    }

    private bool TryResolveFromKnownPlayerBase(nint playerBase, out PlayerRuntime runtime, out string detail)
    {
        runtime = new PlayerRuntime();
        var attempts = new List<string>();

        foreach (var slot in PlayerBaseCharacterSlots)
        {
            if (!TryResolvePointerChain(playerBase, new[] { 0x18, 0xA0, 0xD0, slot }, out var candidate))
            {
                attempts.Add($"+0x{slot:X}: cadeia inválida");
                continue;
            }

            if (TryResolveActorStats(candidate, out runtime, out var statsDetail))
            {
                runtime.Actor = candidate;
                if (TryReadActorPosition(candidate, out var pos, out _))
                {
                    runtime.Position = pos;
                    runtime.HasPosition = true;
                }
                detail = $"base=0x{playerBase.ToInt64():X}, slot=+0x{slot:X}, actor=0x{candidate.ToInt64():X}; {statsDetail}";
                return true;
            }

            attempts.Add($"+0x{slot:X}: actor=0x{candidate.ToInt64():X} não validou");
        }

        detail = $"base=0x{playerBase.ToInt64():X}; " + string.Join(" | ", attempts);
        return false;
    }

    private bool TryResolvePointerChain(nint baseAddress, IReadOnlyList<int> offsets, out nint value)
    {
        value = baseAddress;
        if (_memory is null || value == 0)
            return false;

        foreach (var offset in offsets)
        {
            if (!_memory.TryReadPointer(value + offset, out value))
                return false;
        }

        return value != 0;
    }

    private void AddAobDiagnostic(List<string> diagnostics, string name, string pattern)
    {
        if (_memory is null)
            return;

        var match = _memory.FindPatternInMainModule(pattern);
        diagnostics.Add(match.HasValue
            ? $"AOB {name}: 0x{match.Value.ToInt64():X} (RVA 0x{match.Value.ToInt64() - _memory.MainModuleBase.ToInt64():X})"
            : $"AOB {name}: não encontrado");
    }

    private void RestoreStat(nint entry, int expectedType, string label)
    {
        if (_memory is null || !ValidateStatEntry(entry, expectedType, out var stat))
        {
            InvalidateRuntime($"{label}: bloco de atributo deixou de validar. Relocalizando automaticamente...");
            return;
        }

        if (stat.Current < stat.Max)
            _memory.Write(entry + StatCurrentOffset, stat.Max);
    }

    private bool ValidateRuntime()
        => _runtime.IsResolved
           && ValidateStatEntry(_runtime.HealthEntry, HealthId, out _)
           && ValidateStatEntry(_runtime.StaminaEntry, StaminaId, out _)
           && ValidateStatEntry(_runtime.SpiritEntry, SpiritId, out _);

    private void CompleteSuccessfulResolution(PlayerRuntime runtime, string method, List<string> diagnostics)
    {
        _runtime = runtime;
        diagnostics.Add($"Método vencedor: {method}");
        if (runtime.Actor != 0)
            diagnostics.Add($"Actor: 0x{runtime.Actor.ToInt64():X} ({DescribeAddress(runtime.Actor)})");
        if (runtime.ActorType.HasValue)
            diagnostics.Add($"Actor type: 0x{runtime.ActorType.Value:X2}");
        if (runtime.HasPosition)
            diagnostics.Add($"Position: {runtime.Position.X:F2}, {runtime.Position.Y:F2}, {runtime.Position.Z:F2}");
        diagnostics.Add($"StatsBase: 0x{runtime.StatsBase.ToInt64():X}");
        diagnostics.Add($"HealthEntry: 0x{runtime.HealthEntry.ToInt64():X}");
        diagnostics.Add($"StaminaEntry: 0x{runtime.StaminaEntry.ToInt64():X}");
        diagnostics.Add($"SpiritEntry: 0x{runtime.SpiritEntry.ToInt64():X}");
        diagnostics.Add($"Layout: {runtime.LayoutName}");

        DiagnosticReport = string.Join(Environment.NewLine, diagnostics);
        RuntimeStatus = $"Pronto • {runtime.LayoutName} • Vida/Vigor/Espírito validados";
        LastError = string.Empty;
    }

    private string DescribeAddress(nint address)
    {
        if (_memory is null || address == 0)
            return "nulo";

        var value = address.ToInt64();
        var start = _memory.MainModuleBase.ToInt64();
        var end = start + _memory.MainModuleSize;
        return value >= start && value < end
            ? $"módulo+0x{value - start:X}"
            : "memória dinâmica";
    }

    private bool IsInsideMainModule(nint address)
    {
        if (_memory is null)
            return false;

        var value = address.ToInt64();
        var start = _memory.MainModuleBase.ToInt64();
        var end = start + _memory.MainModuleSize;
        return value >= start && value < end;
    }

    private void InvalidateRuntime(string reason, bool scheduleRetry = true, bool preserveDiagnostic = false)
    {
        _runtime = new PlayerRuntime();
        RuntimeStatus = reason;
        LastError = reason;

        if (!preserveDiagnostic)
            DiagnosticReport = reason;

        if (scheduleRetry)
            _nextResolveAttemptUtc = DateTime.UtcNow.AddMilliseconds(750);
    }

    private static string FormatStat(StatSnapshot stat) => $"{stat.Current}/{stat.Max}";

    private readonly record struct WorldSystemPattern(string Name, string Signature, int DisplacementOffset, int InstructionEndOffset);
    private readonly record struct StatLayout(int StaminaFromHealth, int SpiritFromHealth, string Name);
    private readonly record struct StatSnapshot(int Type, long Current, long Max);
    private readonly record struct PositionSnapshot(float X, float Y, float Z);
    private readonly record struct ScoredRuntime(PlayerRuntime Runtime, int Score, string Detail);
    private readonly record struct RankedRegion(MemoryRegionInfo Region, long Distance);
    private readonly record struct HeapStatsCandidate(long StatsBase, StatLayout Layout, StatSnapshot Health, StatSnapshot Stamina, StatSnapshot Spirit);

    private sealed class PlayerRuntime
    {
        public bool IsResolved { get; set; }
        public string LayoutName { get; set; } = string.Empty;

        public nint WorldSystem { get; set; }
        public nint ActorManager { get; set; }
        public nint Actor { get; set; }
        public byte? ActorType { get; set; }
        public PositionSnapshot Position { get; set; }
        public bool HasPosition { get; set; }

        public nint Marker { get; set; }
        public nint Root { get; set; }
        public nint StatsBase { get; set; }
        public nint HealthEntry { get; set; }
        public nint StaminaEntry { get; set; }
        public nint SpiritEntry { get; set; }
    }
}
