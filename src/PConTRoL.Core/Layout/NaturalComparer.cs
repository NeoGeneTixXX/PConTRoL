namespace PConTRoL.Core.Layout;

/// <summary>Vergleicht Namen wie der Explorer: Zahlenblöcke numerisch („Platte_2“ vor „Platte_10“), sonst ohne Groß-/Kleinschreibung.</summary>
public sealed class NaturalComparer : IComparer<string>
{
    public static readonly NaturalComparer Instance = new();

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y)) return 0;
        if (x == null) return -1;
        if (y == null) return 1;

        int i = 0, j = 0;
        while (i < x.Length && j < y.Length)
        {
            if (char.IsAsciiDigit(x[i]) && char.IsAsciiDigit(y[j]))
            {
                var si = i;
                var sj = j;
                while (i < x.Length && char.IsAsciiDigit(x[i])) i++;
                while (j < y.Length && char.IsAsciiDigit(y[j])) j++;
                var a = x[si..i].TrimStart('0');
                var b = y[sj..j].TrimStart('0');
                var c = a.Length != b.Length ? a.Length.CompareTo(b.Length) : string.CompareOrdinal(a, b);
                if (c != 0) return c;
                continue;
            }

            var d = char.ToUpperInvariant(x[i]).CompareTo(char.ToUpperInvariant(y[j]));
            if (d != 0) return d;
            i++;
            j++;
        }

        var rest = (x.Length - i).CompareTo(y.Length - j);
        return rest != 0 ? rest : string.Compare(x, y, StringComparison.OrdinalIgnoreCase);
    }
}
