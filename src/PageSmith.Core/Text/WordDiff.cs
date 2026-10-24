namespace PageSmith.Core.Text;

public enum DiffOp
{
    Equal,
    Insert,
    Delete,
}

/// <summary>A run of tokens: <see cref="OldLength"/> tokens of the old sequence and/or <see cref="NewLength"/> of the new.</summary>
public readonly record struct DiffSegment(DiffOp Op, int OldStart, int OldLength, int NewStart, int NewLength);

/// <summary>
/// Token-level diff using Myers' O(ND) algorithm with the linear-space "middle snake" bisection
/// (as in diff-match-patch), so long documents with many changes stay within memory.
/// </summary>
public static class WordDiff
{
    public static List<DiffSegment> Diff<T>(IReadOnlyList<T> oldTokens, IReadOnlyList<T> newTokens, IEqualityComparer<T>? comparer = null, CancellationToken ct = default)
    {
        comparer ??= EqualityComparer<T>.Default;
        // Map tokens to integers once; comparisons in the hot loop are then integer compares.
        var ids = new Dictionary<T, int>(comparer);
        int Id(T t)
        {
            if (!ids.TryGetValue(t, out var id))
            {
                id = ids.Count;
                ids[t] = id;
            }
            return id;
        }
        var a = oldTokens.Select(Id).ToArray();
        var b = newTokens.Select(Id).ToArray();
        var result = new List<DiffSegment>();
        Recurse(a, 0, a.Length, b, 0, b.Length, result, ct);
        return Merge(result);
    }

    private static void Recurse(int[] a, int aLo, int aHi, int[] b, int bLo, int bHi, List<DiffSegment> output, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        // Common prefix.
        var prefix = 0;
        while (aLo + prefix < aHi && bLo + prefix < bHi && a[aLo + prefix] == b[bLo + prefix]) prefix++;
        if (prefix > 0) output.Add(new DiffSegment(DiffOp.Equal, aLo, prefix, bLo, prefix));
        aLo += prefix;
        bLo += prefix;

        // Common suffix (emitted after the middle).
        var suffix = 0;
        while (aHi - suffix > aLo && bHi - suffix > bLo && a[aHi - suffix - 1] == b[bHi - suffix - 1]) suffix++;
        aHi -= suffix;
        bHi -= suffix;

        var n = aHi - aLo;
        var m = bHi - bLo;
        if (n == 0 && m > 0) output.Add(new DiffSegment(DiffOp.Insert, aLo, 0, bLo, m));
        else if (m == 0 && n > 0) output.Add(new DiffSegment(DiffOp.Delete, aLo, n, bLo, 0));
        else if (n > 0 && m > 0)
        {
            var (x, y) = Bisect(a, aLo, n, b, bLo, m, ct);
            if (x < 0)
            {
                output.Add(new DiffSegment(DiffOp.Delete, aLo, n, bLo, 0));
                output.Add(new DiffSegment(DiffOp.Insert, aLo + n, 0, bLo, m));
            }
            else
            {
                Recurse(a, aLo, aLo + x, b, bLo, bLo + y, output, ct);
                Recurse(a, aLo + x, aHi, b, bLo + y, bHi, output, ct);
            }
        }

        if (suffix > 0) output.Add(new DiffSegment(DiffOp.Equal, aHi, suffix, bHi, suffix));
    }

    /// <summary>Finds the middle snake; returns the split point relative to the ranges, or (-1, -1).</summary>
    private static (int X, int Y) Bisect(int[] a, int aLo, int n, int[] b, int bLo, int m, CancellationToken ct)
    {
        var maxD = (n + m + 1) / 2;
        var offset = maxD;
        var length = 2 * maxD + 2;
        var v1 = new int[length];
        var v2 = new int[length];
        Array.Fill(v1, -1);
        Array.Fill(v2, -1);
        v1[offset + 1] = 0;
        v2[offset + 1] = 0;
        var delta = n - m;
        var front = delta % 2 != 0;
        int k1Start = 0, k1End = 0, k2Start = 0, k2End = 0;
        for (var d = 0; d < maxD; d++)
        {
            if ((d & 255) == 0) ct.ThrowIfCancellationRequested();
            for (var k1 = -d + k1Start; k1 <= d - k1End; k1 += 2)
            {
                var k1Offset = offset + k1;
                var x1 = k1 == -d || (k1 != d && v1[k1Offset - 1] < v1[k1Offset + 1]) ? v1[k1Offset + 1] : v1[k1Offset - 1] + 1;
                var y1 = x1 - k1;
                while (x1 < n && y1 < m && a[aLo + x1] == b[bLo + y1])
                {
                    x1++;
                    y1++;
                }
                v1[k1Offset] = x1;
                if (x1 > n) k1End += 2;
                else if (y1 > m) k1Start += 2;
                else if (front)
                {
                    var k2Offset = offset + delta - k1;
                    if (k2Offset >= 0 && k2Offset < length && v2[k2Offset] != -1 && x1 >= n - v2[k2Offset])
                        return (x1, y1);
                }
            }
            for (var k2 = -d + k2Start; k2 <= d - k2End; k2 += 2)
            {
                var k2Offset = offset + k2;
                var x2 = k2 == -d || (k2 != d && v2[k2Offset - 1] < v2[k2Offset + 1]) ? v2[k2Offset + 1] : v2[k2Offset - 1] + 1;
                var y2 = x2 - k2;
                while (x2 < n && y2 < m && a[aLo + n - x2 - 1] == b[bLo + m - y2 - 1])
                {
                    x2++;
                    y2++;
                }
                v2[k2Offset] = x2;
                if (x2 > n) k2End += 2;
                else if (y2 > m) k2Start += 2;
                else if (!front)
                {
                    var k1Offset = offset + delta - k2;
                    if (k1Offset >= 0 && k1Offset < length && v1[k1Offset] != -1)
                    {
                        var x1 = v1[k1Offset];
                        var y1 = offset + x1 - k1Offset;
                        if (x1 >= n - x2) return (x1, y1);
                    }
                }
            }
        }
        return (-1, -1);
    }

    private static List<DiffSegment> Merge(List<DiffSegment> segments)
    {
        var merged = new List<DiffSegment>();
        foreach (var s in segments)
        {
            if (s.OldLength == 0 && s.NewLength == 0) continue;
            if (merged.Count > 0 && merged[^1].Op == s.Op)
            {
                var last = merged[^1];
                merged[^1] = last with { OldLength = last.OldLength + s.OldLength, NewLength = last.NewLength + s.NewLength };
            }
            else
            {
                merged.Add(s);
            }
        }
        return merged;
    }
}
