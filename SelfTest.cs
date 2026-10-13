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
        if (sampleVideo is null) return;

        var root = Path.Combine(Path.GetTempPath(), "arsivci-test-" + Guid.NewGuid().ToString("N")[..6]);
        var src = Directory.CreateDirectory(Path.Combine(root, "indirilenler")).FullName;
        var lib = Path.Combine(root, "arsiv");
        try
        {
            var video = Path.Combine(src, "Deadpool.2016.1080p.BluRay.mp4");
            File.Copy(sampleVideo, video);
            File.WriteAllText(Path.Combine(src, "Deadpool.2016.1080p.BluRay.tr.srt"), "1\n00:00:01,000 --> 00:00:02,000\nMerhaba\n");

            var meta = new Meta { Title = "Deadpool", OriginalTitle = "Deadpool", Year = 2016, Overview = "Çılgın paralı asker ğüşiöç.", Genres = ["Aksiyon"] };
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

            // decorate: icon, desktop.ini, Bilgi.txt with MediaInfo data
            FakePoster(Path.Combine(dir, "folder.jpg"));
            await Organizer.Decorate(dir, meta, null);
            var ico = File.ReadAllBytes(Path.Combine(dir, "folder.ico"));
            Check(ico[0] == 0 && ico[2] == 1 && ico[4] == 6, "ico başlığı bozuk");
            Check(File.GetAttributes(Path.Combine(dir, "desktop.ini")).HasFlag(FileAttributes.System), "desktop.ini sistem değil");
            Check(File.ReadAllText(Path.Combine(dir, "desktop.ini")).Contains("IconResource=folder.ico"), "desktop.ini ikon satırı yok");
            Check(File.GetAttributes(dir).HasFlag(FileAttributes.ReadOnly), "klasör salt okunur değil");
            var info = File.ReadAllText(Path.Combine(dir, "Bilgi.txt"));
            Check(info.Contains("Video       :") && info.Contains("ğüşiöç") && info.Contains("tr.srt"), "Bilgi.txt eksik:\n" + info);
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
        }
        finally
        {
            foreach (var d in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories)) File.SetAttributes(d, FileAttributes.Directory);
            foreach (var f in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(root, true);
        }
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
