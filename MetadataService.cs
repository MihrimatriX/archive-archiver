using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;

namespace ArchiveArchiver;

/// TMDb finds the title and hands out IMDb/TVDB ids; OMDb and Fanart.tv are queried by those ids,
/// never by name, so every API describes the same movie.
public class MetadataService(Settings s)
{
    /// Problems with the optional providers (OMDb, Fanart.tv): reported once, never fail a match.
    public System.Collections.Concurrent.ConcurrentDictionary<string, string> Warnings { get; } = new();

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private const string Img = "https://image.tmdb.org/t/p/";

    static MetadataService() => Http.DefaultRequestHeaders.UserAgent.ParseAdd("ArchiveArchiver/1.0");

    // ---------- search ----------

    public async Task<List<Candidate>> Search(MediaKind kind, string title, int? year)
    {
        var list = new List<Candidate>();
        if (title.Length == 0) return list;

        if (s.TmdbKey != "")
        {
            list = await SearchTmdb(kind, title, year);
            // Years in file names are often off by one or belong to the release, not the film.
            if (list.Count == 0 && year is not null) list = await SearchTmdb(kind, title, null);
        }
        if (list.Count == 0 && s.OmdbKey != "")
        {
            list = await SearchOmdb(kind, title, year);
            if (list.Count == 0 && year is not null) list = await SearchOmdb(kind, title, null);
        }
        return list;
    }

    private async Task<List<Candidate>> SearchTmdb(MediaKind kind, string title, int? year)
    {
        var tv = kind == MediaKind.Series;
        var q = "&query=" + Uri.EscapeDataString(title)
              + (year is null ? "" : tv ? $"&first_air_date_year={year}" : $"&year={year}");
        var r = await Tmdb(tv ? "search/tv" : "search/movie", q);
        if (r is not { } root || !root.TryGetProperty("results", out var results)) return [];

        return results.EnumerateArray().Select(x => new Candidate(
            kind,
            Str(x, tv ? "name" : "title"),
            Str(x, tv ? "original_name" : "original_title"),
            YearOf(Str(x, tv ? "first_air_date" : "release_date")),
            x.GetProperty("id").GetInt32(),
            null,
            x.TryGetProperty("popularity", out var p) ? p.GetDouble() : 0,
            Str(x, "poster_path") is { Length: > 0 } pp ? Img + "w185" + pp : null,
            Str(x, "overview"))).ToList();
    }

    private async Task<List<Candidate>> SearchOmdb(MediaKind kind, string title, int? year)
    {
        var r = await Omdb($"&s={Uri.EscapeDataString(title)}&type={(kind == MediaKind.Series ? "series" : "movie")}"
                           + (year is null ? "" : $"&y={year}"));
        if (r is not { } root || !root.TryGetProperty("Search", out var results)) return [];

        return results.EnumerateArray().Select(x => new Candidate(
            kind, Str(x, "Title"), Str(x, "Title"), YearOf(Str(x, "Year")), null, Str(x, "imdbID"),
            0, NA(Str(x, "Poster")), "")).ToList();
    }

    // ---------- details ----------

    public async Task<Meta> Details(Candidate c)
    {
        var m = new Meta
        {
            Kind = c.Kind, Title = c.Title, OriginalTitle = c.OriginalTitle, Year = c.Year,
            Overview = c.Overview, TmdbId = c.TmdbId, ImdbId = c.ImdbId,
        };
        if (c.TmdbId is int id && s.TmdbKey != "") await FillTmdb(m, id);
        // Both only need the ids TMDb just filled in, so they run side by side; each locks m while writing.
        await Task.WhenAll(
            m.ImdbId is { Length: > 0 } && s.OmdbKey != "" ? Optional("OMDb", () => FillOmdb(m)) : Task.CompletedTask,
            s.FanartKey != "" ? Optional("Fanart.tv", () => FillFanart(m)) : Task.CompletedTask);
        if (c.PosterThumb is not null && m.Posters.Count == 0) m.Posters.Add(c.PosterThumb);
        return m;
    }

    private async Task Optional(string name, Func<Task> fill)
    {
        try { await fill(); }
        catch (Exception e) when (e is InvalidOperationException or HttpRequestException or TaskCanceledException or JsonException)
        {
            Warnings[name] = e.Message;
        }
    }

    private async Task FillTmdb(Meta m, int id)
    {
        var tv = m.Kind == MediaKind.Series;
        var path = (tv ? "tv/" : "movie/") + id;
        var lang2 = s.Language[..2];
        var r = await Tmdb(path, $"&append_to_response=external_ids,credits,images,translations&include_image_language={lang2},en,null");
        if (r is not { } x) return;

        m.Title = Str(x, tv ? "name" : "title") is { Length: > 0 } t ? t : m.Title;
        m.OriginalTitle = Str(x, tv ? "original_name" : "original_title");
        m.Year = YearOf(Str(x, tv ? "first_air_date" : "release_date")) ?? m.Year;
        m.Overview = Str(x, "overview");
        if (x.TryGetProperty("translations", out var tr) && tr.TryGetProperty("translations", out var trs))
            m.EnglishTitle = trs.EnumerateArray()
                .Where(t => Str(t, "iso_639_1") == "en" && t.TryGetProperty("data", out _))
                .Select(t => Str(t.GetProperty("data"), tv ? "name" : "title"))
                .FirstOrDefault(t => t.Length > 0) ?? "";
        m.Tagline = Str(x, "tagline");
        m.Genres = Names(x, "genres");
        if (tv)
        {
            m.Runtime = x.TryGetProperty("episode_run_time", out var rt) && rt.GetArrayLength() > 0 ? rt[0].GetInt32() : null;
            m.Directors = Names(x, "created_by");
            if (x.TryGetProperty("seasons", out var seasons))
                foreach (var se in seasons.EnumerateArray())
                    if (Str(se, "poster_path") is { Length: > 0 } sp)
                        Add(m.SeasonPosters, se.GetProperty("season_number").GetInt32(), Img + "w780" + sp);
        }
        else
        {
            m.Runtime = x.TryGetProperty("runtime", out var rt) && rt.ValueKind == JsonValueKind.Number ? rt.GetInt32() : null;
            if (x.TryGetProperty("credits", out var cr) && cr.TryGetProperty("crew", out var crew))
                m.Directors = crew.EnumerateArray().Where(c => Str(c, "job") == "Director").Select(c => Str(c, "name")).ToList();
        }
        if (x.TryGetProperty("credits", out var credits) && credits.TryGetProperty("cast", out var cast))
            m.Cast = cast.EnumerateArray().Take(6).Select(c => Str(c, "name")).ToList();
        if (x.TryGetProperty("external_ids", out var ext))
        {
            m.ImdbId = NullIfEmpty(Str(ext, "imdb_id")) ?? m.ImdbId;
            if (ext.TryGetProperty("tvdb_id", out var tvdb) && tvdb.ValueKind == JsonValueKind.Number) m.TvdbId = tvdb.GetInt32();
        }
        if (x.TryGetProperty("vote_average", out var va) && va.GetDouble() > 0)
            m.Ratings.Add($"TMDb {va.GetDouble():0.0}/10");

        if (Str(x, "poster_path") is { Length: > 0 } poster) m.Posters.Add(Img + "w780" + poster);
        if (Str(x, "backdrop_path") is { Length: > 0 } bd) m.Backdrops.Add(Img + "w1280" + bd);
        if (x.TryGetProperty("images", out var imgs))
        {
            m.Posters.AddRange(Images(imgs, "posters", "w780", lang2).Except(m.Posters));
            m.Backdrops.AddRange(Images(imgs, "backdrops", "w1280", null).Except(m.Backdrops));
            m.Logos.AddRange(Images(imgs, "logos", "original", lang2));
        }

        // No Turkish overview on TMDb yet -> fall back to English rather than leave it empty.
        if (m.Overview == "" && !s.Language.StartsWith("en"))
            if (await Tmdb(path, "", "en-US") is { } en) m.Overview = Str(en, "overview");
    }

    private async Task FillOmdb(Meta m)
    {
        if (await Omdb($"&i={m.ImdbId}&plot=full") is not { } x) return;

        lock (m)
        {
            // IMDb, Rotten Tomatoes, Metacritic go before TMDb's own score.
            if (x.TryGetProperty("Ratings", out var ratings))
                m.Ratings.InsertRange(0, ratings.EnumerateArray().Select(r =>
                    $"{(Str(r, "Source") == "Internet Movie Database" ? "IMDb" : Str(r, "Source"))} {Str(r, "Value")}"));
            m.Awards = NA(Str(x, "Awards")) ?? "";
            if (m.Overview == "") m.Overview = NA(Str(x, "Plot")) ?? "";
            if (m.Genres.Count == 0) m.Genres = Split(Str(x, "Genre"));
            if (m.Directors.Count == 0) m.Directors = Split(Str(x, "Director"));
            if (m.Cast.Count == 0) m.Cast = Split(Str(x, "Actors"));
            if (m.Runtime is null && int.TryParse(Str(x, "Runtime").Split(' ')[0], out var rt)) m.Runtime = rt;
            if (NA(Str(x, "Poster")) is { } p) m.Posters.Add(p.Replace("_SX300", "_SX1000"));
        }
    }

    private async Task FillFanart(Meta m)
    {
        var tv = m.Kind == MediaKind.Series;
        string? key = tv ? m.TvdbId?.ToString() : m.TmdbId?.ToString() ?? m.ImdbId;
        if (key is null) return;
        if (await Get($"https://webservice.fanart.tv/v3/{(tv ? "tv" : "movies")}/{key}?api_key={s.FanartKey}") is not { } x) return;

        lock (m)
        {
            // Fanart.tv posters go right after TMDb's main poster: usually higher resolution, same artwork pool.
            var posters = Fanart(x, tv ? "tvposter" : "movieposter");
            m.Posters.InsertRange(Math.Min(1, m.Posters.Count), posters.Except(m.Posters));
            m.Logos.InsertRange(0, Fanart(x, tv ? "hdtvlogo" : "hdmovielogo"));
            m.Backdrops.AddRange(Fanart(x, tv ? "showbackground" : "moviebackground"));
            if (!tv) m.Discs.AddRange(Fanart(x, "moviedisc"));
            if (tv && x.TryGetProperty("seasonposter", out var sp))
                foreach (var p in sp.EnumerateArray())
                    if (int.TryParse(Str(p, "season"), out var n)) Add(m.SeasonPosters, n, Str(p, "url"));
        }
    }

    // ---------- downloads ----------

    /// Tries each url in order and keeps the first real image. Returns false if none worked.
    public static async Task<bool> Download(IEnumerable<string> urls, string dest)
    {
        if (await Fetch(urls) is not { } bytes) return false;
        Organizer.WriteVisible(dest, f => File.WriteAllBytes(f, bytes));
        return true;
    }

    /// The first url that returns a real image, as bytes; null if none worked.
    public static async Task<byte[]?> Fetch(IEnumerable<string> urls)
    {
        foreach (var url in urls)
        {
            try
            {
                using var res = await Http.GetAsync(url);
                if (!res.IsSuccessStatusCode || res.Content.Headers.ContentType?.MediaType?.StartsWith("image/") != true) continue;
                var bytes = await res.Content.ReadAsByteArrayAsync();
                if (bytes.Length >= 1024) return bytes;
            }
            catch (HttpRequestException) { }
            catch (TaskCanceledException) { }
        }
        return null;
    }

    // ---------- http ----------

    private Task<JsonElement?> Tmdb(string path, string query, string? lang = null)
    {
        var bearer = s.TmdbKey.StartsWith("eyJ");   // v4 read token vs v3 api key
        var url = $"https://api.themoviedb.org/3/{path}?language={lang ?? s.Language}{query}"
                  + (bearer ? "" : "&api_key=" + s.TmdbKey);
        return Get(url, bearer ? s.TmdbKey : null);
    }

    private async Task<JsonElement?> Omdb(string query)
    {
        var r = await Get($"https://www.omdbapi.com/?apikey={s.OmdbKey}{query}");
        return r is { } x && Str(x, "Response") == "True" ? x : null;
    }

    private static async Task<JsonElement?> Get(string url, string? bearer = null)
    {
        for (var attempt = 0; ; attempt++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            if (bearer is not null) req.Headers.Authorization = new("Bearer", bearer);
            using var res = await Http.SendAsync(req);

            if (res.StatusCode == HttpStatusCode.TooManyRequests && attempt < 4)
            {
                await Task.Delay(res.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(2 << attempt));
                continue;
            }
            if (res.StatusCode == HttpStatusCode.Unauthorized)
                throw new InvalidOperationException(req.RequestUri!.Host.Contains("omdb")
                    ? "anahtar geçersiz ya da henüz etkinleştirilmemiş (OMDb e-postasındaki linke tıkla)"
                    : $"{req.RequestUri.Host}: API anahtarı geçersiz");
            if (!res.IsSuccessStatusCode) return null;   // 404 = no data for this title, not an error

            using var doc = await JsonDocument.ParseAsync(await res.Content.ReadAsStreamAsync());
            return doc.RootElement.Clone();
        }
    }

    // ---------- json helpers ----------

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";

    private static List<string> Names(JsonElement e, string name) =>
        e.TryGetProperty(name, out var arr) ? arr.EnumerateArray().Select(x => Str(x, "name")).ToList() : [];

    private static int? YearOf(string date) => date.Length >= 4 && int.TryParse(date[..4], out var y) ? y : null;
    private static string? NA(string v) => v is "" or "N/A" ? null : v;
    private static string? NullIfEmpty(string v) => v == "" ? null : v;
    private static List<string> Split(string v) => NA(v)?.Split(", ").ToList() ?? [];

    private static void Add(Dictionary<int, List<string>> d, int k, string v)
    {
        if (!d.TryGetValue(k, out var l)) d[k] = l = [];
        l.Add(v);
    }

    /// TMDb image list: preferred language first, then English, then textless; best voted first.
    private static IEnumerable<string> Images(JsonElement imgs, string kind, string size, string? lang) =>
        imgs.TryGetProperty(kind, out var arr)
            ? arr.EnumerateArray()
                .OrderBy(i => LangRank(i.TryGetProperty("iso_639_1", out var l) && l.ValueKind == JsonValueKind.String ? l.GetString() : null, lang))
                .ThenByDescending(i => i.GetProperty("vote_average").GetDouble())
                .Select(i => Img + size + Str(i, "file_path"))
            : [];

    private IEnumerable<string> Fanart(JsonElement x, string kind) =>
        x.TryGetProperty(kind, out var arr)
            ? arr.EnumerateArray()
                .OrderBy(i => LangRank(Str(i, "lang") is "00" or "" ? null : Str(i, "lang"), s.Language[..2]))
                .ThenByDescending(i => int.TryParse(Str(i, "likes"), out var n) ? n : 0)
                .Select(i => Str(i, "url"))
            : [];

    private static int LangRank(string? lang, string? preferred) =>
        lang == preferred ? 0 : lang == "en" ? 1 : lang is null ? 2 : 3;
}
