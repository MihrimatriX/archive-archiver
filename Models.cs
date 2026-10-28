using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows.Media;

namespace ArchiveArchiver;

public enum MediaKind { Movie, Series }

/// One search hit from TMDb or OMDb; enough to show in a picker and to fetch full details later.
public record Candidate(
    MediaKind Kind, string Title, string OriginalTitle, int? Year,
    int? TmdbId, string? ImdbId, double Popularity, string? PosterThumb, string Overview)
{
    public string Display => Year is null ? Title : $"{Title} ({Year})";
}

/// Everything collected from all APIs for one movie or show.
public class Meta
{
    public MediaKind Kind { get; set; }
    public string Title { get; set; } = "";
    public string OriginalTitle { get; set; } = "";
    public string EnglishTitle { get; set; } = "";
    public int? Year { get; set; }
    public string Overview { get; set; } = "";
    public string Tagline { get; set; } = "";
    public List<string> Genres { get; set; } = [];
    public int? Runtime { get; set; }
    public List<string> Directors { get; set; } = [];
    public List<string> Cast { get; set; } = [];
    public List<string> Ratings { get; set; } = [];
    public string Awards { get; set; } = "";
    public int? TmdbId { get; set; }
    public string? ImdbId { get; set; }
    public int? TvdbId { get; set; }
    // Ordered best-first; the downloader takes the first one that works.
    public List<string> Posters { get; set; } = [];
    public List<string> Backdrops { get; set; } = [];
    public List<string> Logos { get; set; } = [];
    public List<string> Discs { get; set; } = [];
    public Dictionary<int, List<string>> SeasonPosters { get; set; } = [];

    public string Display => Year is null ? Title : $"{Title} ({Year})";
    public string RatingsText => string.Join("  ·  ", Ratings);
    public string GenresText => string.Join(", ", Genres) + (Runtime is > 0 ? $"  ·  {Runtime} dk" : "");
}

public class MediaItem : INotifyPropertyChanged
{
    public required string SourcePath { get; init; }
    public required ParsedName Parsed { get; init; }
    public long Size { get; init; }

    public string FileName => Path.GetFileName(SourcePath);
    public string SizeText => Organizer.FormatSize(Size);
    public MediaKind Kind => Parsed.IsEpisode ? MediaKind.Series : MediaKind.Movie;
    public string KindText => Parsed.IsEpisode ? $"Dizi · S{Parsed.Season:00}E{Parsed.Episode:00}" : "Film";
    // Episodes of the same show share one match.
    public string GroupKey => Kind == MediaKind.Series ? "tv:" + Parsed.Title.ToLowerInvariant() : "movie:" + SourcePath;

    public List<Candidate> Candidates { get; set => Set(ref field, value); } = [];
    public Meta? Meta { get; set { Set(ref field, value); OnPropertyChanged(nameof(MatchText)); OnPropertyChanged(nameof(Title)); OnPropertyChanged(nameof(PosterThumb)); OnPropertyChanged(nameof(PosterLarge)); } }
    public string? PosterLarge => Meta?.Posters.FirstOrDefault();
    /// Meta's image lists were reordered in place; the poster bindings don't notice that on their own.
    public void PostersChanged()
    {
        OnPropertyChanged(nameof(PosterLarge));
        OnPropertyChanged(nameof(PosterThumb));
    }
    // Small versions for the list: TMDb serves any width, Fanart.tv has /preview/.
    public string? PosterThumb => PosterLarge?.Replace("/t/p/w780/", "/t/p/w185/").Replace("/fanart/", "/preview/");
    public string MatchText => Meta?.Display ?? "—";
    // Preview of the folder.ico Apply will create; set after matching.
    public ImageSource? FolderIcon { get; set => Set(ref field, value); }
    // Tile caption: the match once there is one, the parsed name before that.
    public string Title => Meta?.Display ?? Parsed.Title;
    public string TargetPath { get; set => Set(ref field, value); } = "";
    public string Status { get; set { Set(ref field, value); OnPropertyChanged(nameof(Tone)); } } = "Bekliyor";
    public string Tone => ToneOf(Status);
    public bool Include { get; set => Set(ref field, value); } = true;

    /// Colors the status dot on tiles, in the table and in the log: "ok", "warn", "error", "busy" or "" (grey).
    /// Gets item statuses ("Eşleşti", "Hata: …", "Aranıyor…") and the window's log messages ("Bitti, 2 hata var…").
    public static string ToneOf(string status) => status switch
    {
        "Eşleşti" or "Zaten düzenli" or "Taşındı" => "ok",
        _ when status.StartsWith("Hata") || status.Contains(" hata var") => "error",
        // fixable by the user: no match, stale library path, a step skipped, an optional API left out
        _ when status.Contains("bulunamadı", StringComparison.OrdinalIgnoreCase) || status.Contains('⚠') || status == "Kontrol et"
            || status.StartsWith("Kütüphane") || status.StartsWith("Önce") || status.StartsWith("Sonuç yok") => "warn",
        _ when status.EndsWith('…') => "busy",   // "Aranıyor…", "Taşınıyor…"
        _ when status.StartsWith("Bitti") || status.StartsWith("Eşleştirme bitti") || status.Contains("eşleştirildi") => "ok",
        _ => "",
    };

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new(n));
    private void Set<T>(ref T f, T v, [CallerMemberName] string? n = null)
    {
        if (EqualityComparer<T>.Default.Equals(f, v)) return;
        f = v;
        OnPropertyChanged(n);
    }
}

public record LogEntry(string Time, string Text, string Tone);

public class Settings
{
    public string TmdbKey { get; set; } = "";
    public string OmdbKey { get; set; } = "";
    public string FanartKey { get; set; } = "";
    public string Language { get; set; } = "tr-TR";
    public string SourcePath { get; set; } = "";
    public string LibraryPath { get; set; } = "";

    // ponytail: keys stored as plain JSON in the user profile; switch to DPAPI if they ever become paid keys.
    // --deneme points this at a throwaway copy
    public static string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ArchiveArchiver", "settings.json");

    public static Settings Load()
    {
        try { return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new(); }
        catch { return new(); }
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}
