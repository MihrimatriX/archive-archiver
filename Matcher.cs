using System.Text;

namespace ArchiveArchiver;

public static class Matcher
{
    /// Picks the search hit to use automatically. The user can still override it in the detail pane.
    /// Title similarity and year decide; popularity breaks ties ("12 Monkeys" -> the 1995 film, not a 2026 short).
    public static Candidate? PickBest(IReadOnlyList<Candidate> candidates, ParsedName parsed) =>
        candidates.MaxBy(c => Score(c, parsed));

    public static double Score(Candidate c, ParsedName p)
    {
        var want = Norm(p.Title);
        var title = new[] { c.Title, c.OriginalTitle }.Select(Norm).Max(t =>
            t == want ? 3 : t.Length > 0 && (t.StartsWith(want + " ") || want.StartsWith(t + " ")) ? 1.5 : 0);
        // A year in the file name is strong evidence; off by one is common (festival vs. release date).
        var year = p.Year is null || c.Year is null ? 0 : Math.Abs(p.Year.Value - c.Year.Value) switch { 0 => 3, 1 => 2, _ => -3 };
        return title + year + Math.Log10(1 + c.Popularity);
    }

    /// "300: Rise of an Empire" == "300 rise of an empire", "Süper" == "super", "ı" == "i".
    public static string Norm(string s)
    {
        var sb = new StringBuilder();
        foreach (var ch in s.ToLowerInvariant().Replace('ı', 'i').Normalize(NormalizationForm.FormD))
            if (char.IsLetterOrDigit(ch)) sb.Append(ch);
            else if (char.IsWhiteSpace(ch) || char.IsPunctuation(ch) || char.IsSymbol(ch)) sb.Append(' ');
        return string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }
}
