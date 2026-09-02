using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace GameTrainer.Core.Memory;

public sealed class MemoryDiscoveryScanner
{
    private const uint MemPrivate = 0x20000;
    private const long MaxSnapshotBytes = 512L * 1024 * 1024;
    private const long MaxSliceBytes = 64L * 1024 * 1024;
    private const long PriorityRadiusBytes = 4L * 1024 * 1024 * 1024;
    private const int ChunkSize = 1024 * 1024;
    private const int MaxCandidatesPerType = 5000;

    private readonly ProcessMemory _memory;
    private readonly List<SnapshotRegion> _baseline = new();
    private readonly Dictionary<string, DiscoveryResult> _results = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<long> _priorityAddresses = new();

    public MemoryDiscoveryScanner(ProcessMemory memory)
    {
        _memory = memory;
    }

    public string Status { get; private set; } = "Mapeamento ainda não iniciado.";
    public long CapturedBytes { get; private set; }
    public int CapturedRegions => _baseline.Count;

    public IReadOnlyDictionary<string, DiscoveryResult> Results => _results;

    public void SetPriorityAddresses(IEnumerable<nint> addresses)
    {
        _priorityAddresses.Clear();
        foreach (var address in addresses.Select(a => a.ToInt64()).Where(a => a >= 0x10000).Distinct().Take(32))
            _priorityAddresses.Add(address);
    }

    public Task CaptureBaselineAsync(string nextStep, CancellationToken cancellationToken = default)
        => Task.Run(() => CaptureBaseline(nextStep, cancellationToken), cancellationToken);

    public Task<DiscoveryResult> CaptureDecreaseAsync(string label, CancellationToken cancellationToken = default)
        => Task.Run(() => CompareAgainstBaseline(label, ChangeDirection.Decreased, cancellationToken), cancellationToken);

    public Task<DiscoveryResult> CaptureIncreaseAsync(string label, CancellationToken cancellationToken = default)
        => Task.Run(() => CompareAgainstBaseline(label, ChangeDirection.Increased, cancellationToken), cancellationToken);

    private void CaptureBaseline(string nextStep, CancellationToken cancellationToken)
    {
        if (!_memory.IsAttached)
            throw new InvalidOperationException("O jogo não está conectado.");

        _baseline.Clear();
        CapturedBytes = 0;
        Status = $"Capturando memória base para {nextStep}...";

        var privateRegions = _memory.GetReadableRegions(writableOnly: true)
            .Where(r => r.Size >= 4096 && r.Type == MemPrivate)
            .ToArray();

        if (privateRegions.Length == 0)
            privateRegions = _memory.GetReadableRegions(writableOnly: true)
                .Where(r => r.Size >= 4096)
                .ToArray();

        var slices = new List<RegionSlice>();
        foreach (var region in privateRegions)
        {
            foreach (var slice in SplitRegion(region))
            {
                var distance = DistanceToPriority(slice.BaseAddress.ToInt64(), slice.Length);

                if (_priorityAddresses.Count > 0 && distance > PriorityRadiusBytes)
                    continue;

                // Sem âncoras, evita repetir o erro da v0.2.7 de consumir o orçamento
                // inteiro em mapeamentos baixos antes de chegar ao heap 64-bit do jogo.
                if (_priorityAddresses.Count == 0 && slice.BaseAddress.ToInt64() < 0x1_0000_0000L)
                    continue;

                slices.Add(slice with { PriorityDistance = distance });
            }
        }

        if (slices.Count == 0)
        {
            foreach (var region in privateRegions)
            {
                foreach (var slice in SplitRegion(region))
                    slices.Add(slice with { PriorityDistance = DistanceToPriority(slice.BaseAddress.ToInt64(), slice.Length) });
            }
        }

        var ordered = slices
            .OrderBy(s => s.PriorityDistance)
            .ThenByDescending(s => s.Length)
            .ThenBy(s => s.BaseAddress.ToInt64())
            .ToArray();

        foreach (var slice in ordered)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (CapturedBytes >= MaxSnapshotBytes)
                break;

            var remainingBudget = MaxSnapshotBytes - CapturedBytes;
            var length = (int)Math.Min(slice.Length, remainingBudget);
            if (length < 4096)
                continue;

            var bytes = ReadRegionBestEffort(slice.BaseAddress, length, cancellationToken);
            if (bytes is null || bytes.Length < 4096)
                continue;

            _baseline.Add(new SnapshotRegion(slice.BaseAddress, bytes));
            CapturedBytes += bytes.Length;
        }

        if (_baseline.Count == 0)
            throw new InvalidOperationException("Nenhuma região de memória privada/gravável pôde ser capturada.");

        Status = $"Base capturada para {nextStep}: {_baseline.Count} blocos, {FormatBytes(CapturedBytes)}.";
    }

    private IEnumerable<RegionSlice> SplitRegion(MemoryRegionInfo region)
    {
        var offset = 0L;
        while (offset < region.Size)
        {
            var length = Math.Min(MaxSliceBytes, region.Size - offset);
            yield return new RegionSlice(region.BaseAddress + offset, length, long.MaxValue);
            offset += length;
        }
    }

    private long DistanceToPriority(long baseAddress, long length)
    {
        if (_priorityAddresses.Count == 0)
            return long.MaxValue / 4;

        var end = baseAddress + length;
        var best = long.MaxValue;
        foreach (var anchor in _priorityAddresses)
        {
            long distance;
            if (anchor >= baseAddress && anchor < end)
                distance = 0;
            else if (anchor < baseAddress)
                distance = baseAddress - anchor;
            else
                distance = anchor - end;

            if (distance < best)
                best = distance;
        }

        return best;
    }

    private DiscoveryResult CompareAgainstBaseline(
        string label,
        ChangeDirection direction,
        CancellationToken cancellationToken)
    {
        if (_baseline.Count == 0)
            throw new InvalidOperationException("Capture uma memória base antes de registrar uma alteração.");

        Status = $"Comparando memória para {label}...";

        var int32 = new List<MemoryCandidate>();
        var int64 = new List<MemoryCandidate>();
        var floats = new List<MemoryCandidate>();
        long comparedBytes = 0;

        foreach (var region in _baseline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var current = ReadRegionBestEffort(region.BaseAddress, region.Bytes.Length, cancellationToken);
            if (current is null || current.Length != region.Bytes.Length)
                continue;

            comparedBytes += current.Length;
            CompareInt32(region.BaseAddress, region.Bytes, current, direction, int32);
            CompareInt64(region.BaseAddress, region.Bytes, current, direction, int64);
            CompareFloat(region.BaseAddress, region.Bytes, current, direction, floats);
        }

        var result = new DiscoveryResult(
            label,
            direction,
            comparedBytes,
            Rank(int32),
            Rank(int64),
            Rank(floats),
            DateTime.Now);

        _results[label] = result;
        Status = $"{label}: {result.TotalCandidates} candidatos encontrados.";
        return result;
    }

    private byte[]? ReadRegionBestEffort(nint baseAddress, int length, CancellationToken cancellationToken)
    {
        var output = new byte[length];
        var written = 0;

        while (written < length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var take = Math.Min(ChunkSize, length - written);
            if (!_memory.TryReadBytes(baseAddress + written, take, out var chunk))
                return null;

            Buffer.BlockCopy(chunk, 0, output, written, chunk.Length);
            written += chunk.Length;
        }

        return output;
    }

    private static void CompareInt32(
        nint baseAddress,
        byte[] before,
        byte[] after,
        ChangeDirection direction,
        List<MemoryCandidate> output)
    {
        for (var offset = 0; offset <= before.Length - 4; offset += 4)
        {
            var a = BinaryPrimitives.ReadInt32LittleEndian(before.AsSpan(offset, 4));
            var b = BinaryPrimitives.ReadInt32LittleEndian(after.AsSpan(offset, 4));
            if (!Changed(a, b, direction) || !PlausibleInt32(a, b))
                continue;

            AddBounded(output, new MemoryCandidate(baseAddress + offset, "Int32", a, b, Score(a, b)));
        }
    }

    private static void CompareInt64(
        nint baseAddress,
        byte[] before,
        byte[] after,
        ChangeDirection direction,
        List<MemoryCandidate> output)
    {
        for (var offset = 0; offset <= before.Length - 8; offset += 8)
        {
            var a = BinaryPrimitives.ReadInt64LittleEndian(before.AsSpan(offset, 8));
            var b = BinaryPrimitives.ReadInt64LittleEndian(after.AsSpan(offset, 8));
            if (!Changed(a, b, direction) || !PlausibleInt64(a, b))
                continue;

            AddBounded(output, new MemoryCandidate(baseAddress + offset, "Int64", a, b, Score(a, b)));
        }
    }

    private static void CompareFloat(
        nint baseAddress,
        byte[] before,
        byte[] after,
        ChangeDirection direction,
        List<MemoryCandidate> output)
    {
        for (var offset = 0; offset <= before.Length - 4; offset += 4)
        {
            var aBits = BinaryPrimitives.ReadInt32LittleEndian(before.AsSpan(offset, 4));
            var bBits = BinaryPrimitives.ReadInt32LittleEndian(after.AsSpan(offset, 4));
            var a = BitConverter.Int32BitsToSingle(aBits);
            var b = BitConverter.Int32BitsToSingle(bBits);

            if (!float.IsFinite(a) || !float.IsFinite(b))
                continue;
            if (!Changed(a, b, direction) || !PlausibleFloat(a, b))
                continue;

            AddBounded(output, new MemoryCandidate(baseAddress + offset, "Float", a, b, Score(a, b)));
        }
    }

    private static bool Changed(long a, long b, ChangeDirection direction)
        => direction == ChangeDirection.Decreased ? b < a : b > a;

    private static bool Changed(float a, float b, ChangeDirection direction)
        => direction == ChangeDirection.Decreased ? b < a : b > a;

    private static bool PlausibleInt32(int a, int b)
        => a >= 0 && b >= 0 && a <= 100_000_000 && b <= 100_000_000 && a != b;

    private static bool PlausibleInt64(long a, long b)
        => a >= 0 && b >= 0 && a <= 10_000_000_000_000L && b <= 10_000_000_000_000L && a != b;

    private static bool PlausibleFloat(float a, float b)
        => a >= 0 && b >= 0 && a <= 100_000_000f && b <= 100_000_000f && Math.Abs(a - b) >= 0.0001f;

    private static double Score(double before, double after)
    {
        var delta = Math.Abs(before - after);
        var scale = Math.Max(Math.Abs(before), 1.0);
        var relative = Math.Min(delta / scale, 10.0);

        var magnitudeBonus = before switch
        {
            >= 1 and <= 100_000 => 2.0,
            > 100_000 and <= 100_000_000 => 1.0,
            _ => 0.0
        };

        // A v0.2.7 ficou dominada por regiões transitórias que simplesmente zeraram.
        // Não removemos zero (um recurso pode realmente acabar), mas reduzimos sua prioridade.
        var zeroPenalty = after == 0 ? 4.0 : 0.0;

        return relative * 10.0 + magnitudeBonus - zeroPenalty;
    }

    private static void AddBounded(List<MemoryCandidate> list, MemoryCandidate candidate)
    {
        if (list.Count < MaxCandidatesPerType)
        {
            list.Add(candidate);
            return;
        }

        var worstIndex = 0;
        var worstScore = list[0].Score;
        for (var i = 1; i < list.Count; i++)
        {
            if (list[i].Score < worstScore)
            {
                worstScore = list[i].Score;
                worstIndex = i;
            }
        }

        if (candidate.Score > worstScore)
            list[worstIndex] = candidate;
    }

    private static IReadOnlyList<MemoryCandidate> Rank(List<MemoryCandidate> candidates)
        => candidates.OrderByDescending(c => c.Score).ThenBy(c => c.Address.ToInt64()).Take(250).ToArray();

    public string BuildReport(string gameVersion)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Game Trainer v0.2.8 - Heap Focused Memory Discovery");
        sb.AppendLine($"Jogo: CrimsonDesert.exe | versão {gameVersion}");
        sb.AppendLine($"Snapshot: {CapturedRegions} blocos / {FormatBytes(CapturedBytes)}");
        sb.AppendLine($"Âncoras dinâmicas priorizadas: {_priorityAddresses.Count}");
        if (_priorityAddresses.Count > 0)
            sb.AppendLine("Âncoras: " + string.Join(", ", _priorityAddresses.Take(12).Select(a => $"0x{a:X}")));
        sb.AppendLine("Modo: somente leitura durante descoberta");
        sb.AppendLine();

        foreach (var result in _results.Values)
        {
            sb.AppendLine($"=== {result.Label} ({result.Direction}) ===");
            sb.AppendLine($"Comparado: {FormatBytes(result.ComparedBytes)} | candidatos retidos: {result.TotalCandidates}");
            AppendCandidates(sb, "Int32", result.Int32Candidates);
            AppendCandidates(sb, "Int64", result.Int64Candidates);
            AppendCandidates(sb, "Float", result.FloatCandidates);
            sb.AppendLine();
        }

        AppendStructuralCorrelation(sb);

        if (_results.Count == 0)
            sb.AppendLine("Nenhuma etapa de alteração foi registrada.");

        return sb.ToString();
    }

    private void AppendStructuralCorrelation(StringBuilder sb)
    {
        if (!_results.TryGetValue("VIDA", out var hp)
            || !_results.TryGetValue("VIGOR", out var stamina)
            || !_results.TryGetValue("ESPÍRITO", out var spirit))
            return;

        sb.AppendLine("=== CORRELAÇÃO ESTRUTURAL ===");
        sb.AppendLine("Procura tripletas com os espaçamentos públicos HP→Vigor/Espírito do Crimson Desert.");

        var matches = new List<StructuralMatch>();
        AddStructuralMatches(matches, "Int64", hp.Int64Candidates, stamina.Int64Candidates, spirit.Int64Candidates);
        AddStructuralMatches(matches, "Int32", hp.Int32Candidates, stamina.Int32Candidates, spirit.Int32Candidates);
        AddStructuralMatches(matches, "Float", hp.FloatCandidates, stamina.FloatCandidates, spirit.FloatCandidates);

        foreach (var match in matches.OrderByDescending(m => m.Score).Take(30))
        {
            sb.AppendLine($"  {match.Type}/{match.Layout}: HP 0x{match.Health:X} | Vigor 0x{match.Stamina:X} | Espírito 0x{match.Spirit:X} | score {match.Score:F3}");
        }

        if (matches.Count == 0)
            sb.AppendLine("  Nenhuma tripla exata encontrada nos candidatos retidos.");

        sb.AppendLine();
    }

    private static void AddStructuralMatches(
        List<StructuralMatch> output,
        string type,
        IReadOnlyList<MemoryCandidate> hp,
        IReadOnlyList<MemoryCandidate> stamina,
        IReadOnlyList<MemoryCandidate> spirit)
    {
        var staminaMap = stamina.ToDictionary(c => c.Address.ToInt64(), c => c);
        var spiritMap = spirit.ToDictionary(c => c.Address.ToInt64(), c => c);

        foreach (var h in hp)
        {
            AddLayout("mai-2026", 0x510, 0x5A0);
            AddLayout("legado", 0x480, 0x510);

            void AddLayout(string layout, long staminaDelta, long spiritDelta)
            {
                var hAddress = h.Address.ToInt64();
                if (!staminaMap.TryGetValue(hAddress + staminaDelta, out var s))
                    return;
                if (!spiritMap.TryGetValue(hAddress + spiritDelta, out var p))
                    return;

                output.Add(new StructuralMatch(
                    type,
                    layout,
                    hAddress,
                    s.Address.ToInt64(),
                    p.Address.ToInt64(),
                    h.Score + s.Score + p.Score));
            }
        }
    }

    private static void AppendCandidates(StringBuilder sb, string type, IReadOnlyList<MemoryCandidate> candidates)
    {
        sb.AppendLine($"{type} - top {Math.Min(candidates.Count, 40)}:");
        foreach (var candidate in candidates.Take(40))
        {
            sb.AppendLine(
                $"  0x{candidate.Address.ToInt64():X} | {FormatNumber(candidate.Before)} -> {FormatNumber(candidate.After)} | score {candidate.Score:F3}");
        }
    }

    private static string FormatNumber(double value)
        => value.ToString("0.####", CultureInfo.InvariantCulture);

    private static string FormatBytes(long bytes)
    {
        if (bytes >= 1024L * 1024 * 1024)
            return $"{bytes / (1024d * 1024 * 1024):F2} GB";
        if (bytes >= 1024L * 1024)
            return $"{bytes / (1024d * 1024):F1} MB";
        return $"{bytes / 1024d:F1} KB";
    }

    private sealed record SnapshotRegion(nint BaseAddress, byte[] Bytes);
    private readonly record struct RegionSlice(nint BaseAddress, long Length, long PriorityDistance);
    private readonly record struct StructuralMatch(string Type, string Layout, long Health, long Stamina, long Spirit, double Score);
}

public enum ChangeDirection
{
    Decreased,
    Increased
}

public sealed record MemoryCandidate(nint Address, string Type, double Before, double After, double Score);

public sealed record DiscoveryResult(
    string Label,
    ChangeDirection Direction,
    long ComparedBytes,
    IReadOnlyList<MemoryCandidate> Int32Candidates,
    IReadOnlyList<MemoryCandidate> Int64Candidates,
    IReadOnlyList<MemoryCandidate> FloatCandidates,
    DateTime CapturedAt)
{
    public int TotalCandidates => Int32Candidates.Count + Int64Candidates.Count + FloatCandidates.Count;
}
