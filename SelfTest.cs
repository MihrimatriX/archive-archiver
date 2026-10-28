using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace ArchiveArchiver;

/// ArchiveArchiver.exe --selftest [sample-video]   -> exit code 0 = ok. Runs offline in a temp folder.
public static class SelfTest
{
    public static async Task Run(string? sampleVideo)
    {
        NameParser.SelfTest();
        MatcherTest();
        Check(MediaItem.ToneOf("Eşleşti") == "ok" && MediaItem.ToneOf("Hata: zaman aşımı") == "error" && MediaItem.ToneOf("Bulunamadı") == "warn"
              && MediaItem.ToneOf("Taşınıyor…") == "busy" && MediaItem.ToneOf("Bitti, 2 hata var; Durum sütununa bak.") == "error", "durum renkleri");
        // native libmediainfo.dll must load from inside the single exe; Version stays empty if it didn't
        Check(!string.IsNullOrEmpty(new MediaInfo.MediaInfoWrapper(Environment.ProcessPath!, null).Version), "MediaInfo yüklenemedi");
        // an error page or a cut-off download served as image/* must be skipped, not drawn
        Check(!Organizer.Decodes(new byte[4096]) && !Organizer.Decodes("<html>503</html>"u8.ToArray()), "bozuk resim kabul edildi");
        // scope films are letterboxed (1920x800 is still a 1080p release), HDV is narrow (1440x1080), DVDs are 720 wide
        string[] res = [Organizer.Resolution(3840, 2160), Organizer.Resolution(3840, 1600), Organizer.Resolution(1920, 1080), Organizer.Resolution(1920, 800),
                        Organizer.Resolution(1440, 1080), Organizer.Resolution(1280, 534), Organizer.Resolution(720, 576)];
        Check(res.SequenceEqual(["4K", "4K", "1080p", "1080p", "1080p", "720p", "SD"]), "çözünürlük rozetleri: " + string.Join(", ", res));
        Check(Organizer.TmdbIdFrom("TMDb        : https://www.themoviedb.org/tv/1396\n") == (MediaKind.Series, 1396)   // Bilgi.txt of older versions
              && Organizer.TmdbIdFrom("IMDb        : https://www.imdb.com/title/tt0110912/") is null, "Bilgi.txt'den TMDb kimliği okunamadı");
        if (sampleVideo is null) return;

        var root = Path.Combine(Path.GetTempPath(), "arsivci-test-" + Guid.NewGuid().ToString("N")[..6]);
        var src = Directory.CreateDirectory(Path.Combine(root, "indirilenler")).FullName;
        var lib = Path.Combine(root, "arsiv");
        try
        {
            var video = Path.Combine(src, "Deadpool.2016.1080p.BluRay.mp4");
            File.Copy(sampleVideo, video);
            File.WriteAllText(Path.Combine(src, "Deadpool.2016.1080p.BluRay.tr.srt"), "1\n00:00:01,000 --> 00:00:02,000\nMerhaba\n");

            var meta = new Meta { Title = "Deadpool", OriginalTitle = "Deadpool", Year = 2016, Overview = "Çılgın paralı asker ğüşiöç <x>.", Genres = ["Aksiyon"], TmdbId = 293660 };
            var it = new MediaItem { SourcePath = video, Parsed = NameParser.Parse(video), Meta = meta };
            it.TargetPath = Organizer.Target(it, lib);
            var log = Path.Combine(root, "log.txt");

            // move: video + subtitle go into the folder with their names untouched, source gone, logged
            await Organizer.Move(it, log);
            var dir = Path.Combine(lib, "Deadpool (2016)");
            Check(File.Exists(Path.Combine(dir, "Deadpool.2016.1080p.BluRay.mp4")), "video taşınmadı ya da adı değişti");
            Check(File.Exists(Path.Combine(dir, "Deadpool.2016.1080p.BluRay.tr.srt")), "altyazı taşınmadı ya da adı değişti");
            Check(!File.Exists(video), "kaynak hâlâ duruyor");
            Check(File.ReadAllLines(log).Length == 2, "kayıt dosyası eksik");

            // never overwrite
            File.Copy(sampleVideo, video);
            var dup = new MediaItem { SourcePath = video, Parsed = it.Parsed, Meta = meta, TargetPath = it.TargetPath };
            var threw = false;
            try { await Organizer.Move(dup, log); } catch (IOException) { threw = true; }
            Check(threw && File.Exists(video), "var olan dosyanın üzerine yazdı");

            // decorate: icon, desktop.ini, Bilgi.html with MediaInfo data, the printable DVD and Blu-ray covers
            FakePoster(Path.Combine(dir, "folder.jpg"));
            Check(Organizer.Decodes(File.ReadAllBytes(Path.Combine(dir, "folder.jpg"))), "sağlam resim reddedildi");
            await Organizer.Decorate(dir, meta, null);
            var ico = File.ReadAllBytes(Path.Combine(dir, "folder.ico"));
            Check(ico[0] == 0 && ico[2] == 1 && ico[4] == 6, "ico başlığı bozuk");
            Check(File.GetAttributes(Path.Combine(dir, "desktop.ini")).HasFlag(FileAttributes.System), "desktop.ini sistem değil");
            Check(File.ReadAllText(Path.Combine(dir, "desktop.ini")).Contains("IconResource=folder.ico"), "desktop.ini ikon satırı yok");
            Check(File.GetAttributes(dir).HasFlag(FileAttributes.ReadOnly), "klasör salt okunur değil");
            var info = File.ReadAllText(Path.Combine(dir, "Bilgi.html"));
            Check(info.Contains("<dt>Video</dt>") && info.Contains("ğüşiöç") && info.Contains("tr.srt") && info.Contains("kapak-dvd.jpg") && info.Contains("kapak-bluray.jpg"), "Bilgi.html eksik:\n" + info);
            Check(info.Contains("&lt;x&gt;") && !info.Contains("<x>"), "Bilgi.html API metnini kaçışsız yazdı");
            Check(Organizer.TmdbIdFrom(info) == (MediaKind.Movie, 293660), "İkonları yenile Bilgi.html'den TMDb kimliğini okuyamaz");
            // 273x183 mm and 269x148 mm at 300 dpi
            foreach (var (file, w, h) in new[] { ("kapak-dvd.jpg", 3224, 2161), ("kapak-bluray.jpg", 3177, 1748) })
            {
                var cover = BitmapFrame.Create(new MemoryStream(File.ReadAllBytes(Path.Combine(dir, file))));
                Check(cover.PixelWidth == w && cover.PixelHeight == h && Math.Round(cover.DpiX) == 300,
                      $"{file} {w}x{h} / 300 dpi değil: {cover.PixelWidth}x{cover.PixelHeight} {cover.DpiX} dpi");
            }
            await Organizer.Decorate(dir, meta, null);   // second run must overwrite its own hidden files fine

            // disc with one movie: play it directly
            Organizer.PrepareDisc(lib);
            var inf = File.ReadAllText(Path.Combine(lib, "autorun.inf"));
            Check(inf.Contains("shellexecute=\"Deadpool (2016)\\Deadpool.2016.1080p.BluRay.mp4\"") && inf.Contains("icon=autorun.ico"), "autorun.inf:\n" + inf);

            // disc with two movies: poster menu
            var second = Directory.CreateDirectory(Path.Combine(lib, "Avatar (2009)")).FullName;
            File.Copy(sampleVideo, Path.Combine(second, "Avatar (2009).mp4"));
            Organizer.PrepareDisc(lib);
            Check(File.ReadAllText(Path.Combine(lib, "autorun.inf")).Contains("shellexecute=\"menu.hta\""), "menü autorun'a yazılmadı");
            Check(File.ReadAllText(Path.Combine(lib, "menu.hta")).Contains("Avatar (2009)"), "menüde film yok");

            // undo: the last run goes back, the folder Decorate made goes away, and the run can't be undone twice
            var src2 = Directory.CreateDirectory(Path.Combine(root, "geri")).FullName;
            var lib2 = Path.Combine(root, "arsiv2");
            var ulog = Path.Combine(root, "geri.txt");
            File.WriteAllText(ulog, "2020-01-01 10:00\tC:\\eski\\a.mkv\tC:\\yeni\\a.mkv\n");   // from before runs were marked: never undone
            var back = Path.Combine(src2, "Deadpool.2016.mp4");
            File.Copy(sampleVideo, back);
            var ui = new MediaItem { SourcePath = back, Parsed = NameParser.Parse(back), Meta = meta };
            ui.TargetPath = Organizer.Target(ui, lib2);
            Organizer.StartRun(ulog);
            await Organizer.Move(ui, ulog);
            var udir = Path.GetDirectoryName(ui.TargetPath)!;
            FakePoster(Path.Combine(udir, "folder.jpg"));
            await Organizer.Decorate(udir, meta, null);
            var run = Organizer.LastRun(ulog);
            Check(run is { Moves.Count: 1 }, "son çalıştırma kayıttan okunamadı");
            var skipped = await Organizer.Undo(ulog, lib2, run!.Value.Id, run.Value.Moves);
            Check(skipped.Count == 0 && File.Exists(back) && !Directory.Exists(udir), "geri alma dosyayı döndürmedi ya da klasörü silmedi");
            Check(Organizer.LastRun(ulog) is null, "aynı çalıştırma iki kez geri alınabiliyor");
        }
        finally { Wipe(root); }
    }

    /// ArchiveArchiver.exe --deneme   -> the real window on a throwaway setup: its own settings.json (API keys copied from
    /// the real one, which is only read), source and library under %TEMP%\arsivci-deneme, rebuilt on every start.
    /// Uygula / Geri al / Disk hazırla only ever see these copies.
    public static void Sandbox()
    {
        var real = Settings.Load();
        var root = Path.Combine(Path.GetTempPath(), "arsivci-deneme");
        if (Directory.Exists(root)) Wipe(root);
        var src = Directory.CreateDirectory(Path.Combine(root, "indirilenler")).FullName;
        foreach (var name in SandboxFiles)
        {
            var path = Path.Combine(src, name);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (!Organizer.IsVideo(path)) File.WriteAllText(path, "1\n00:00:01,000 --> 00:00:02,000\nMerhaba\n");
            else if (File.Exists(SampleVideo)) File.Copy(SampleVideo, path);
            else File.WriteAllBytes(path, new byte[4096]);   // still a "video" to the scanner, MediaInfo just finds nothing
        }
        Settings.FilePath = Path.Combine(root, "settings.json");
        new Settings
        {
            TmdbKey = real.TmdbKey, OmdbKey = real.OmdbKey, FanartKey = real.FanartKey, Language = real.Language,
            SourcePath = src, LibraryPath = Path.Combine(root, "arsiv"),
        }.Save();
    }

    private const string SampleVideo = @"C:\WINDOWS\SystemApps\Microsoft.Windows.CloudExperienceHost_cw5n1h2txyewy\media\oobe-intro.mp4";

    // One file per case worth seeing in the window. Paths are relative to the fake Downloads folder.
    private static readonly string[] SandboxFiles =
    [
        "Inception.2010.1080p.BluRay.x264.mkv",                     // clean match -> Eşleşti
        "Inception.2010.1080p.BluRay.x264.tr.srt",                  // subtitle travels with its video
        "Dune 1984.mp4",                                            // remake, year decides -> Kontrol et if it picks 2021
        "Breaking.Bad.S01E01.720p.mkv",                             // series episode -> Show/Season 01
        "Breaking.Bad.S01E02.720p.mkv",                             // second episode shares the match
        @"Filmler\12 Monkeys\12.Monkeys.1995.DVDRip.avi",           // nested folder, number in title
        "Asdfghjkl Qwerty 2031.mp4",                                // nothing on TMDb -> Bulunamadı
        "Avatar.mkv",                                               // no year, sequels exist -> still the 2009 film
        "Avatar.sample.mkv",                                        // small "sample" file -> Tara skips it
        "Breaking.Bad.S02E05.720p.mkv",                             // other season -> Season 02 next to Season 01
        "Yeşil.Yol.1999.mkv",                                       // Turkish title (The Green Mile), ş in the name
        "Kurtlar Vadisi Irak 2006.avi",                             // Turkish film, dotless ı
    ];

    /// Decorate marks folders read-only and desktop.ini system/hidden; clear that or Delete refuses.
    private static void Wipe(string root)
    {
        foreach (var d in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories)) File.SetAttributes(d, FileAttributes.Directory);
        foreach (var f in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
        Directory.Delete(root, true);
    }

    /// Real TMDb results (tr-TR) for files that the first version matched wrongly.
    private static void MatcherTest()
    {
        Candidate C(string t, string o, int y, double pop) => new(MediaKind.Movie, t, o, y, 1, null, pop, null, "");
        string Pick(string file, params Candidate[] cs) => Matcher.PickBest(cs, NameParser.ParseName(file))!.OriginalTitle;

        Check(Pick("12 Monkeys", C("Short cuts : L'Armée des 12 singes", "Short cuts : L'Armée des 12 singes", 2026, 0.9),
                                 C("12 Maymun", "Twelve Monkeys", 1995, 20.6)) == "Twelve Monkeys", "12 Monkeys");
        Check(Pick("101 Dalmatians", C("101 Dalmatians Christmas", "101 Dalmatians Christmas", 1998, 1.6),
                                     C("101 Dalmaçyalı", "101 Dalmatians", 1996, 18.2)) == "101 Dalmatians", "101 Dalmatians");
        Check(Pick("6 Super Kahraman", C("Büyük Kahraman", "Big Hero", 2010, 50),
                                       C("6 Süper Kahraman", "Big Hero 6", 2014, 39.6)) == "Big Hero 6", "Süper ~ Super");
        Check(Pick("Avatar.2009.1080p", C("Avatar: Suyun Yolu", "Avatar: The Way of Water", 2022, 300),
                                        C("Avatar", "Avatar", 2009, 100)) == "Avatar", "yıl popülerliği yenmeli");

        bool Sure(string file, Candidate c) => Matcher.Confident(c, NameParser.ParseName(file));
        Check(Sure("Inception", C("Başlangıç", "Inception", 2010, 30)), "yılsız tam ad güvenilir olmalı");
        Check(Sure("Amelie 2001", C("Amelie", "Le Fabuleux Destin d'Amélie Poulain", 2001, 20)), "aynı yıl güvenilir olmalı");
        Check(!Sure("Dune 1984", C("Dune: Çöl Gezegeni", "Dune", 2021, 90)), "yeniden çevrim (yıl tutmuyor) işaretlenmeli");
        Check(!Sure("Avatar", C("Avatar: Suyun Yolu", "Avatar: The Way of Water", 2022, 300)), "yılsız devam filmi işaretlenmeli");
    }

    private static void Check(bool ok, string message)
    {
        if (!ok) throw new Exception(message);
    }

    private static void FakePoster(string path)
    {
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen()) dc.DrawRectangle(Brushes.DarkRed, null, new Rect(0, 0, 400, 600));
        var rtb = new RenderTargetBitmap(400, 600, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        var enc = new JpegBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(path);
        enc.Save(fs);
    }
}
