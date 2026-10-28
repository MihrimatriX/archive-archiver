using System.Text;

namespace ArchiveArchiver;

public static class Matcher
{
    /// Picks the search hit to use automatically. The user can still override it in the detail pane.
    /// Title similarity and year decide; popularity breaks ties ("12 Monkeys" -> the 1995 film, not a 2026 short).
    public static Candidate? PickBest(IReadOnlyList<Candidate> candidates, ParsedName parsed) =>
        candidates.MaxBy(c => Score(c, parsed));

    public static double Score(Candidate c, ParsedName p) => TitleScore(c, p) + YearScore(c, p) + Math.Log10(1 + c.Popularity);

    /// 3 = same title (localized or original), 1.5 = one is the other plus more words ("Avatar" / "Avatar 2"), 0 = neither.
    public static double TitleScore(Candidate c, ParsedName p)
    {
        var want = Norm(p.Title);
        return new[] { c.Title, c.OriginalTitle }.Select(Norm).Max(t =>
            t == want ? 3 : t.Length > 0 && (t.StartsWith(want + " ") || want.StartsWith(t + " ")) ? 1.5 : 0);
    }

    /// A year in the file name is strong evidence; off by one is common (festival vs. release date).
    /// 0 when either side has no year, 3 = same, 2 = off by one, -3 = further apart.
    public static int YearScore(Candidate c, ParsedName p) =>
        p.Year is null || c.Year is null ? 0 : Math.Abs(p.Year.Value - c.Year.Value) switch { 0 => 3, 1 => 2, _ => -3 };

    /// Whether the automatic pick can be trusted, or the file should be flagged "Kontrol et" for the user to look at.
    /// Flagged files still move on Apply; this only decides which ones the user is asked to check.
    /// Trusted: the year doesn't contradict it, and title + year say "same film" at least as loudly as an exact title
    /// alone ("Inception.mkv" -> Inception; "Amelie 2001" -> Le Fabuleux Destin d'Amélie Poulain, 2001).
    public static bool Confident(Candidate c, ParsedName p) =>
        YearScore(c, p) >= 0 && TitleScore(c, p) + YearScore(c, p) >= 3;

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
