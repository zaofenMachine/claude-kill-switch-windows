using System.Globalization;

namespace ClaudeSafe.Manager;

public static class DesktopBenchmarkRanker
{
    public static bool IsPassing(BenchmarkSample sample, BenchmarkTarget target)
    {
        if (!sample.Complete || !sample.TlsVerified || !double.IsFinite(sample.TotalMs) || sample.TotalMs < 0 || sample.Bytes < 0 ||
            sample.ExpectedBytes is < 0 || (sample.ExpectedBytes is long length && length != sample.Bytes) ||
            sample.ActualProtocol.Length > 0 && sample.ActualProtocol != sample.Protocol)
            return false;
        if (target.Kind == "resource")
            return sample.ResourceValid && sample.Bytes > 0 && sample.Http is >= 200 and < 300 && MimeMatches(target.ExpectedType, sample.ContentType);
        return sample.Http is >= 200 and < 400 || sample.Http == 404 && target.Url == "https://api.anthropic.com/";
    }

    public static bool MimeMatches(string expected, string actual)
    {
        var mime = actual.Split(';')[0].Trim().ToLowerInvariant();
        return expected switch { "text/javascript" => mime is "text/javascript" or "application/javascript", "text/css" => mime == "text/css", _ => false };
    }

    public static BenchmarkRank Rank(string name, IReadOnlyList<BenchmarkTarget> targets, IReadOnlyList<string> protocols, int rounds, IReadOnlyList<BenchmarkSample> samples)
    {
        var result = new BenchmarkRank { Name = name, TotalTargets = targets.Select(t => t.Id).Distinct(StringComparer.Ordinal).Count() };
        if (rounds is < 1 or > 10 || targets.Count == 0 || protocols.Count == 0 ||
            result.TotalTargets != targets.Count || protocols.Distinct(StringComparer.Ordinal).Count() != protocols.Count || protocols.Any(p => p is not ("http/1.1" or "h2"))) return result;
        result.ExpectedSlots = checked(targets.Count * protocols.Count * rounds);
        var targetPass = targets.ToDictionary(t => t.Id, _ => true, StringComparer.Ordinal);
        var medians = new List<double>(); var times = new List<double>(); bool metricsComplete = true;
        foreach (var target in targets)
        foreach (var protocol in protocols)
        {
            var group = samples.Where(s => s.Name == name && s.TargetId == target.Id && s.Protocol == protocol).ToArray();
            var columnTimes = new List<double>(); bool columnPassed = true;
            for (int round = 1; round <= rounds; round++)
            {
                var slot = group.Where(s => s.Round == round).ToArray();
                if (slot.Length > 0) result.CoveredSlots++;
                if (slot.Length == 1 && IsPassing(slot[0], target))
                {
                    result.PassedSlots++; columnTimes.Add(slot[0].TotalMs); times.Add(slot[0].TotalMs);
                }
                else columnPassed = false;
            }
            if (!columnPassed) targetPass[target.Id] = false;
            if (columnTimes.Count == rounds) medians.Add(Median(columnTimes)); else metricsComplete = false;
        }
        result.PassedTargets = targetPass.Count(p => p.Value);
        result.AllPassed = result.ExpectedSlots > 0 && result.PassedSlots == result.ExpectedSlots;
        if (metricsComplete && medians.Count > 0) { result.WorstMedianMs = medians.Max(); result.MeanMedianMs = medians.Average(); }
        if (times.Count > 0) result.SlowestMs = times.Max();
        return result;
    }

    private static double Median(List<double> values)
    {
        values.Sort(); int half = values.Count / 2;
        return values.Count % 2 == 1 ? values[half] : (values[half - 1] + values[half]) / 2;
    }

    public static int Compare(BenchmarkRank a, BenchmarkRank b)
    {
        int diff = b.AllPassed.CompareTo(a.AllPassed);
        if (diff == 0) diff = b.PassedTargets.CompareTo(a.PassedTargets);
        if (diff == 0) diff = b.PassedSlots.CompareTo(a.PassedSlots);
        if (diff == 0) diff = Metric(a.WorstMedianMs).CompareTo(Metric(b.WorstMedianMs));
        if (diff == 0) diff = Metric(a.MeanMedianMs).CompareTo(Metric(b.MeanMedianMs));
        if (diff == 0) diff = Metric(a.SlowestMs).CompareTo(Metric(b.SlowestMs));
        return diff == 0 ? CompareNaturalNames(a.Name, b.Name) : diff;
    }

    private static double Metric(double? value) => value is double d && double.IsFinite(d) ? d : double.PositiveInfinity;
    private static int CompareNaturalNames(string a, string b)
    {
        int i = 0, j = 0;
        while (i < a.Length && j < b.Length)
        {
            bool da = char.IsAsciiDigit(a[i]), db = char.IsAsciiDigit(b[j]);
            int ai = i, bj = j;
            if (da && db)
            {
                while (i < a.Length && char.IsAsciiDigit(a[i])) i++;
                while (j < b.Length && char.IsAsciiDigit(b[j])) j++;
                var x = a[ai..i].TrimStart('0'); var y = b[bj..j].TrimStart('0');
                int difference = x.Length.CompareTo(y.Length); if (difference == 0) difference = string.CompareOrdinal(x, y);
                if (difference != 0) return difference;
            }
            else
            {
                i++; j++;
                while (i < a.Length && !char.IsAsciiDigit(a[i])) i++;
                while (j < b.Length && !char.IsAsciiDigit(b[j])) j++;
                int difference = CultureInfo.GetCultureInfo("zh-CN").CompareInfo.Compare(a[ai..i], b[bj..j], CompareOptions.None);
                if (difference != 0) return difference;
            }
        }
        int remaining = (a.Length - i).CompareTo(b.Length - j);
        return remaining == 0 ? string.CompareOrdinal(a, b) : remaining;
    }
}
