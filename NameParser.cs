using System.IO;
using System.Text.RegularExpressions;

namespace ArchiveArchiver;

public record ParsedName(string Title, int? Year, int? Season, int? Episode)
{
    public bool IsEpisode => Season is not null && Episode is not null;
}

public static partial class NameParser
{
    // S01E02, s1e2, S01.E02, 1x02
    [GeneratedRegex(@"\b[Ss](\d{1,2})[ ._-]*[Ee](\d{1,3})|\b(\d{1,2})x(\d{2,3})\b")]
    private static partial Regex EpisodeRx();

    [GeneratedRegex(@"(?<!\d)(19\d{2}|20\d{2})(?!\d)")]
    private static partial Regex YearRx();

    // Everything from the first release tag onwards is noise.
    [GeneratedRegex(@"\b(2160p|1080p|1080i|720p|576p|480p|4k|uhd|hdr|bluray|blu-ray|brrip|bdrip|webrip|web-dl|webdl|hdrip|dvdrip|dvdscr|hdtv|x264|x265|h264|h265|hevc|avc|xvid|divx|aac|ac3|dts|ddp?5\.1|remux|proper|repack|extended|unrated|directors\.?cut|imax|tr[ ._-]?dublaj|dublaj|türkçe|turkce|altyaz[ıi]l?[ıi]?|multi|dual)\b", RegexOptions.IgnoreCase)]
    private static partial Regex TagRx();

    [GeneratedRegex(@"\[[^\]]*\]|\{[^}]*\}")]
    private static partial Regex BracketRx();

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex SpacesRx();

    public static ParsedName Parse(string path)
    {
        var parsed = ParseName(Path.GetFileNameWithoutExtension(path));
        if (parsed.Title.Length > 0) return parsed;

        // "S01E01.mkv" inside "Breaking Bad\Season 1\" -> borrow the show name from the folders.
        var dir = Path.GetDirectoryName(path);
        while (dir is not null && Regex.IsMatch(Path.GetFileName(dir), @"^(season|sezon|s)\s*\d+$", RegexOptions.IgnoreCase))
            dir = Path.GetDirectoryName(dir);
        var fromFolder = dir is null ? null : ParseName(Path.GetFileName(dir));
        return parsed with { Title = fromFolder?.Title ?? "", Year = parsed.Year ?? fromFolder?.Year };
    }

    public static ParsedName ParseName(string name)
    {
        name = BracketRx().Replace(name, " ").Replace('_', ' ');
        int? season = null, episode = null, year = null;

        var ep = EpisodeRx().Match(name);
        if (ep.Success)
        {
            season = int.Parse(ep.Groups[1].Success ? ep.Groups[1].Value : ep.Groups[3].Value);
            episode = int.Parse(ep.Groups[2].Success ? ep.Groups[2].Value : ep.Groups[4].Value);
            name = name[..ep.Index];
        }

        var tag = TagRx().Match(name);
        if (tag.Success && tag.Index > 0) name = name[..tag.Index];

        // The last year that is not the very start of the name: "2012.2009" -> title "2012", year 2009.
        var years = YearRx().Matches(name).Where(m => m.Index > 0).ToList();
        if (years.Count > 0)
        {
            var last = years[^1];
            year = int.Parse(last.Value);
            name = name[..last.Index];
        }

        name = name.Replace('.', ' ').Trim(' ', '-', '(', ')', '[', ']');
        return new ParsedName(SpacesRx().Replace(name, " "), year, season, episode);
    }

    /// Run with: ArchiveArchiver.exe --selftest   (exit code 0 = ok)
    public static void SelfTest()
    {
        void Check(string input, string title, int? year, int? s = null, int? e = null)
        {
            var p = ParseName(input);
            if (p != new ParsedName(title, year, s, e))
                throw new Exception($"'{input}' -> {p}, beklenen ({title}, {year}, {s}, {e})");
        }
        Check("deadpool", "deadpool", null);
        Check("Deadpool.2016.1080p.BluRay.x264-SPARKS", "Deadpool", 2016);
        Check("Avatar (2009) [1080p]", "Avatar", 2009);
        Check("2012.2009.720p.BrRip", "2012", 2009);
        Check("Blade.Runner.2049.2017.2160p.UHD", "Blade Runner 2049", 2017);
        Check("Breaking.Bad.S01E02.720p.HDTV", "Breaking Bad", null, 1, 2);
        Check("the_office_2x05", "the office", null, 2, 5);
        Check("Dark.2017.S03E08.WEB-DL", "Dark", 2017, 3, 8);
        Check("Recep Ivedik 7 TR Dublaj 1080p", "Recep Ivedik 7", null);

        var p = Parse(@"D:\Diziler\Breaking Bad\Season 1\S01E01.mkv");
        if (p.Title != "Breaking Bad" || p.Season != 1) throw new Exception($"klasörden dizi adı: {p}");
    }
}
