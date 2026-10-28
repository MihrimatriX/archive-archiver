using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MediaInfo;
using MediaInfo.Model;

namespace ArchiveArchiver;

public static class Organizer
{
    public static readonly HashSet<string> VideoExt = new(StringComparer.OrdinalIgnoreCase)
        { ".mkv", ".mp4", ".avi", ".m4v", ".mov", ".wmv", ".ts", ".m2ts", ".mpg", ".mpeg", ".flv", ".webm", ".divx" };
    private static readonly HashSet<string> SubExt = new(StringComparer.OrdinalIgnoreCase)
        { ".srt", ".sub", ".idx", ".ass", ".ssa", ".vtt", ".smi" };

    public static bool IsVideo(string path) => VideoExt.Contains(Path.GetExtension(path));

    // ---------- paths ----------

    public static string Safe(string name)
    {
        var bad = Path.GetInvalidFileNameChars();
        // "300: Rise of an Empire" -> "300 - Rise of an Empire"; Windows forbids ":" in names
        var s = new string(name.Replace(": ", " - ").Select(c => c == ':' ? '-' : bad.Contains(c) ? ' ' : c).ToArray());
        return string.Join(' ', s.Split(' ', StringSplitOptions.RemoveEmptyEntries)).TrimEnd('.', ' ');
    }

    /// Folder names keep the original title when it is Latin script ("Le Fabuleux Destin d'Amélie Poulain"),
    /// otherwise the localized one (a Korean title would be unreadable on most discs).
    public static string FolderTitle(Meta m)
    {
        static bool Latin(string t) => t.Length > 0 && t.All(c => c < 0x0250);
        // "12वीं फेल" -> "12th Fail" rather than the Turkish "Bitmeyen Sınav"
        return Safe(Latin(m.OriginalTitle) ? m.OriginalTitle : Latin(m.EnglishTitle) ? m.EnglishTitle : m.Title);
    }

    public static string FolderName(Meta m) => m.Year is null ? FolderTitle(m) : $"{FolderTitle(m)} ({m.Year})";

    /// Only the folder is decided here; the video keeps its own file name (and so do its subtitles).
    public static string Target(MediaItem it, string library)
    {
        var file = Path.GetFileName(it.SourcePath);
        var folder = Path.Combine(library, FolderName(it.Meta!));
        return it.Kind == MediaKind.Movie
            ? Path.Combine(folder, file)
            : Path.Combine(folder, $"Season {it.Parsed.Season:00}", file);
    }

    // ---------- move ----------

    /// Moves the video and its same-named subtitles. Never overwrites: an existing target is an error.
    public static async Task Move(MediaItem it, string logFile)
    {
        var src = it.SourcePath;
        var dst = it.TargetPath;
        if (string.Equals(Path.GetFullPath(src), Path.GetFullPath(dst), StringComparison.OrdinalIgnoreCase)) return;
        if (File.Exists(dst)) throw new IOException("Hedefte aynı isimde dosya var");

        var dstDir = Path.GetDirectoryName(dst)!;
        Directory.CreateDirectory(dstDir);

        var moves = new List<(string From, string To)> { (src, dst) };
        // subtitles named after the video ("Deadpool.2016.tr.srt") travel with it, names unchanged
        foreach (var sub in Directory.EnumerateFiles(Path.GetDirectoryName(src)!, Path.GetFileNameWithoutExtension(src) + ".*"))
        {
            var to = Path.Combine(dstDir, Path.GetFileName(sub));
            if (SubExt.Contains(Path.GetExtension(sub)) && !File.Exists(to)) moves.Add((sub, to));
        }

        foreach (var (from, to) in moves)
        {
            await Task.Run(() => File.Move(from, to));   // cross-drive moves copy first, so the source survives a failure
            File.AppendAllText(logFile, $"{DateTime.Now:yyyy-MM-dd HH:mm}\t{from}\t{to}{Environment.NewLine}");
        }
    }

    // ---------- undo ----------

    // The log groups moves under "#run ⇥ date ⇥ id"; undoing a run writes "#undone ⇥ id" before its moves back,
    // so those reverse moves never count as part of a run. Lines from before runs were marked belong to none.
    private static readonly string[] Made = ["folder.jpg", "folder.ico", "fanart.jpg", "logo.png", "disc.png", "desktop.ini", "Bilgi.html", "Bilgi.txt", "kapak.jpg", "kapak-dvd.jpg", "kapak-bluray.jpg"];

    public static void StartRun(string logFile) =>
        File.AppendAllText(logFile, $"#run\t{DateTime.Now:yyyy-MM-dd HH:mm}\t{Guid.NewGuid():N}{Environment.NewLine}");

    /// The newest run that moved something and isn't undone yet: its id and moves, newest move first.
    public static (string Id, List<(string From, string To)> Moves)? LastRun(string logFile)
    {
        if (!File.Exists(logFile)) return null;
        var runs = new List<(string Id, List<(string, string)> Moves)>();
        var undone = new HashSet<string>();
        List<(string, string)>? current = null;
        foreach (var p in File.ReadLines(logFile).Select(l => l.Split('\t')))
        {
            if (p is ["#run", _, var id]) runs.Add((id, current = []));
            else if (p is ["#undone", var u]) { undone.Add(u); current = null; }
            else if (p is [_, var from, var to] && current is not null) current.Add((from, to));
        }
        var last = runs.FindLast(r => !undone.Contains(r.Id) && r.Moves.Count > 0);
        return last.Id is null ? null : (last.Id, Enumerable.Reverse(last.Moves).ToList());
    }

    /// Moves a run's files back. Never overwrites: a file whose old place is taken (or that is gone) stays put and is
    /// returned as skipped. Folders under the library left without videos lose what Decorate made, then go if empty.
    public static async Task<List<string>> Undo(string logFile, string library, string id, List<(string From, string To)> moves)
    {
        File.AppendAllText(logFile, $"#undone\t{id}{Environment.NewLine}");
        var skipped = new List<string>();
        var lib = Path.GetFullPath(library).TrimEnd('\\');
        var dirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (from, to) in moves)
        {
            if (!File.Exists(to) || File.Exists(from)) { skipped.Add(to); continue; }
            Directory.CreateDirectory(Path.GetDirectoryName(from)!);
            await Task.Run(() => File.Move(to, from));
            File.AppendAllText(logFile, $"{DateTime.Now:yyyy-MM-dd HH:mm}\t{to}\t{from}{Environment.NewLine}");
            // the season folder and its show folder, up to (not including) the library
            for (var d = Path.GetDirectoryName(Path.GetFullPath(to)); d is not null && d.Length > lib.Length
                     && d.StartsWith(lib + '\\', StringComparison.OrdinalIgnoreCase); d = Path.GetDirectoryName(d))
                dirs.Add(d);
        }
        foreach (var d in dirs.OrderByDescending(d => d.Length))   // season folders before their show
        {
            if (!Directory.Exists(d) || Directory.EnumerateFiles(d, "*", SearchOption.AllDirectories).Any(IsVideo)) continue;
            foreach (var f in Made.Select(f => Path.Combine(d, f)).Where(File.Exists))
            {
                File.SetAttributes(f, FileAttributes.Normal);
                File.Delete(f);
            }
            File.SetAttributes(d, FileAttributes.Directory);   // drop the read-only flag the folder icon needed
            if (!Directory.EnumerateFileSystemEntries(d).Any()) Directory.Delete(d);
        }
        return skipped;
    }

    /// The info file Decorate wrote: Bilgi.html, or Bilgi.txt from before it. Null in folders Arşivci didn't make.
    public static string? InfoFile(string folder) =>
        new[] { "Bilgi.html", "Bilgi.txt" }.Select(f => Path.Combine(folder, f)).FirstOrDefault(File.Exists);

    /// The TMDb link in an info file ("https://www.themoviedb.org/tv/1396") -> (Series, 1396); lets a refresh refetch it.
    public static (MediaKind Kind, int Id)? TmdbIdFrom(string infoText) =>
        Regex.Match(infoText, @"themoviedb\.org/(movie|tv)/(\d+)") is { Success: true } m
            ? (m.Groups[1].Value == "tv" ? MediaKind.Series : MediaKind.Movie, int.Parse(m.Groups[2].Value))
            : null;

    // ---------- folder decoration ----------

    /// Downloads artwork, builds folder.ico + desktop.ini, the DVD and Blu-ray covers, and writes Bilgi.html.
    /// Must run on the UI thread (WPF imaging).
    /// Artwork already in the folder is kept unless refresh; a refresh only replaces what downloads again.
    public static async Task Decorate(string folder, Meta m, int? season, bool refresh = false)
    {
        var posters = season is int n && m.SeasonPosters.TryGetValue(n, out var sp) ? sp.Concat(m.Posters) : m.Posters;
        var jpg = Path.Combine(folder, "folder.jpg");
        var ico = Path.Combine(folder, "folder.ico");

        // The artwork files don't depend on each other: download them all at once.
        List<(IEnumerable<string> Urls, string File)> artwork = [(posters, "folder.jpg")];
        if (season is null) artwork.AddRange([(m.Backdrops, "fanart.jpg"), (m.Logos, "logo.png"), (m.Discs, "disc.png")]);
        await Task.WhenAll(artwork.Where(a => refresh || !File.Exists(Path.Combine(folder, a.File)))
                              .Select(a => MetadataService.Download(a.Urls, Path.Combine(folder, a.File))));

        // Front panel: the film's wide backdrop; season folders use their own poster so seasons look different.
        var front = season is null && File.Exists(Path.Combine(folder, "fanart.jpg")) ? Path.Combine(folder, "fanart.jpg") : jpg;
        if (File.Exists(front))
        {
            var icon = await MakeIcon(front, Art(folder, season, "logo.png"), FolderTitle(m), season);
            WriteVisible(ico, f => File.WriteAllBytes(f, icon));
            var tip = m.Overview.Replace("\r", " ").Replace("\n", " ");
            if (tip.Length > 250) tip = tip[..250] + "…";
            WriteVisible(Path.Combine(folder, "desktop.ini"), f => File.WriteAllText(f,
                $"[.ShellClassInfo]\r\nInfoTip={tip}\r\n[ViewState]\r\nMode=\r\nVid=\r\nFolderType=Videos\r\n",
                Encoding.Unicode));   // UTF-16 so Turkish characters in InfoTip survive

            foreach (var art in new[] { "folder.jpg", "folder.ico", "fanart.jpg", "logo.png", "disc.png" })
                if (File.Exists(Path.Combine(folder, art))) File.SetAttributes(Path.Combine(folder, art), FileAttributes.Hidden);
            File.SetAttributes(Path.Combine(folder, "desktop.ini"), FileAttributes.Hidden | FileAttributes.System);
            // The official API adds IconResource (kept relative, so it still works from a CD), marks the folder
            // read-only and tells Explorer properly; a hand-written desktop.ini is often ignored until a reboot.
            SetFolderIcon(folder, "folder.ico");
            File.SetAttributes(folder, File.GetAttributes(folder) | FileAttributes.ReadOnly);
        }

        // MediaInfo reads can take a while per file
        var tech = await Task.Run(() => Directory.EnumerateFiles(folder).Where(IsVideo).Order().Select(ReadTech).ToList());
        if (refresh || Covers.Any(c => !File.Exists(Path.Combine(folder, c.File))))
            foreach (var (file, jpeg) in await MakeCovers(folder, m, season, posters, tech))
                WriteVisible(Path.Combine(folder, file), f => File.WriteAllBytes(f, jpeg));
        WriteVisible(Path.Combine(folder, "Bilgi.html"), f => File.WriteAllText(f, InfoHtml(folder, m, season, tech), new UTF8Encoding(true)));
        if (refresh)   // what older versions wrote: the plain-text page, the single DVD cover
            foreach (var old in new[] { "Bilgi.txt", "kapak.jpg" }) File.Delete(Path.Combine(folder, old));
    }

    /// The folder's own artwork file or, for a season folder, its show's. Full path; null if neither has it.
    private static string? Art(string folder, int? season, string name) =>
        new[] { folder, season is null ? null : Path.GetDirectoryName(Path.GetFullPath(folder)) }
            .Select(d => d is null ? null : Path.Combine(d, name)).FirstOrDefault(File.Exists);

    /// The folder.ico Decorate will make, drawn straight from the urls (same image choice) so the list can show it
    /// before anything moves. Downloads on any thread, draws on the UI thread. Null when no image downloads.
    // ponytail: Apply downloads the same images again; keep the bytes on Meta if big libraries feel slow.
    public static async Task<BitmapSource?> PreviewIcon(Meta m, int? season)
    {
        var fronts = season is int n ? m.SeasonPosters.GetValueOrDefault(n, []).Concat(m.Posters) : m.Backdrops.Concat(m.Posters);
        var logoBytes = MetadataService.Fetch(m.Logos);   // both downloads at once
        if (await MetadataService.Fetch(fronts) is not { } front) return null;
        var logo = await logoBytes;
        try
        {
            // Decode on the pool; the frozen bitmaps are then drawn on the UI thread, the only place WPF can draw.
            var (f, l) = await Task.Run(() => (Load(front), logo is null ? null : Load(logo)));
            return await Application.Current.Dispatcher.InvokeAsync(() => Render(256, LeanImage(f), l, FolderTitle(m), season));
        }
        catch (Exception e) when (e is NotSupportedException or FileFormatException)
        {
            return null;   // served as image/* but not decodable; the tile keeps the poster
        }
    }

    /// Hidden/system files can't be overwritten in place; clear the flags, write, let the caller re-hide.
    public static void WriteVisible(string path, Action<string> write)
    {
        if (File.Exists(path)) File.SetAttributes(path, FileAttributes.Normal);
        write(path);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern void SHChangeNotify(int eventId, uint flags, string? item1, IntPtr item2);

    /// Explorer caches folder icons by file name; after regenerating folder.ico files, make it re-read them.
    public static void RefreshShellIcons() => SHChangeNotify(0x08000000 /*SHCNE_ASSOCCHANGED*/, 0 /*SHCNF_IDLIST*/, null, IntPtr.Zero);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFOLDERCUSTOMSETTINGS
    {
        public uint dwSize, dwMask;
        public IntPtr pvid;
        public string? pszWebViewTemplate;
        public uint cchWebViewTemplate;
        public string? pszWebViewTemplateVersion;
        public string? pszInfoTip;
        public uint cchInfoTip;
        public IntPtr pclsid;
        public uint dwFlags;
        public string? pszIconFile;
        public uint cchIconFile;
        public int iIconIndex;
        public string? pszLogo;
        public uint cchLogo;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHGetSetFolderCustomSettings(ref SHFOLDERCUSTOMSETTINGS pfcs, string pszPath, uint dwReadWrite);

    private static void SetFolderIcon(string folder, string icon)
    {
        var s = new SHFOLDERCUSTOMSETTINGS { dwMask = 0x10 /*FCSM_ICONFILE*/, pszIconFile = icon };
        s.dwSize = (uint)Marshal.SizeOf(s);
        var hr = SHGetSetFolderCustomSettings(ref s, folder, 2 /*FCS_FORCEWRITE*/);
        if (hr != 0) Marshal.ThrowExceptionForHR(hr);
    }

    /// Folder-shaped icon: amber back with a tab, the film image as the front panel, its logo (or title) on top.
    /// Every size is PNG-compressed inside one .ico; returns the file's bytes.
    /// Call from the UI thread: decoding and PNG encoding go to the pool, only the drawing stays there.
    public static async Task<byte[]> MakeIcon(string frontPath, string? logoPath, string? title, int? season)
    {
        var (front, logo) = await Task.Run(() =>
            (Load(File.ReadAllBytes(frontPath)), logoPath is null ? null : Load(File.ReadAllBytes(logoPath))));

        int[] sizes = [256, 128, 64, 48, 32, 16];
        var card = LeanImage(front);
        var frames = sizes.Select(size => Render(size, card, logo, title, season)).ToList();   // frozen, so the pool can encode them
        var pngs = await Task.Run(() => frames.Select(f => Encode(new PngBitmapEncoder(), f)).ToList());

        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        bw.Write((short)0); bw.Write((short)1); bw.Write((short)sizes.Length);   // ICONDIR
        var offset = 6 + 16 * sizes.Length;
        for (var i = 0; i < sizes.Length; i++)                                    // ICONDIRENTRY
        {
            var d = (byte)(sizes[i] >= 256 ? 0 : sizes[i]);                       // 0 means 256
            bw.Write(d); bw.Write(d); bw.Write((byte)0); bw.Write((byte)0);
            bw.Write((short)1); bw.Write((short)32);
            bw.Write(pngs[i].Length); bw.Write(offset);
            offset += pngs[i].Length;
        }
        foreach (var png in pngs) bw.Write(png);
        return ms.ToArray();
    }

    // As wide as the card's bottom edge: the card hinges there, so nothing of the back shows beside its lower corners.
    private static readonly Geometry FolderBack =
        Geometry.Parse("M 20,44 Q 20,30 34,30 H 90 Q 99,30 105,37 L 115,48 H 222 Q 236,48 236,62 V 214 Q 236,228 222,228 H 34 Q 20,228 20,214 Z");
    private static readonly Rect Panel = new(8, 70, 240, 166);
    // The front card falls open toward the viewer, Windows 7 style: its bottom edge is Lean narrower on each side.
    private const double Lean = 12;
    private static readonly Geometry Card = Rounded(14, Panel.TopLeft, Panel.TopRight,
        new Point(Panel.Right - Lean, Panel.Bottom), new Point(Panel.Left + Lean, Panel.Bottom));
    private static readonly Rect TextBox = new(22, 148, 212, 78);     // title fallback: bottom, room for long names
    // With a logo the folder shrinks toward the bottom-left corner, freeing the top-right for the logo to hang over
    // the folder's edge. The logo's right edge and vertical middle are pinned at LogoAnchor (unscaled space).
    private const double FolderScale = 0.8;
    private static readonly Point LogoAnchor = new(252, 96);
    private static readonly Size LogoMax = new(156, 88);

    private static Brush Vertical(params (byte A, byte R, byte G, byte B, double At)[] stops) =>
        new LinearGradientBrush(new GradientStopCollection(stops.Select(s => new GradientStop(Color.FromArgb(s.A, s.R, s.G, s.B), s.At))), 90);

    /// Polygon with its corners rounded off by r.
    private static Geometry Rounded(double r, params Point[] p)
    {
        Point Near(int i, int j) => p[i] + (p[j] - p[i]) * (r / (p[j] - p[i]).Length);   // r along the edge from corner i toward j
        var g = new StreamGeometry();
        using (var c = g.Open())
        {
            c.BeginFigure(Near(0, 1), true, true);
            for (var i = 1; i <= p.Length; i++)
            {
                var k = i % p.Length;
                c.LineTo(Near(k, i - 1), true, true);
                c.QuadraticBezierTo(p[k], Near(k, (k + 1) % p.Length), true, true);
            }
        }
        g.Freeze();
        return g;
    }

    /// The image fitted onto the leaning card. WPF has no perspective for 2D drawing, so it is squeezed row by row:
    /// full width along the top, Lean narrower per side along the bottom. Whole-pixel rows leave no seams between them.
    private static BitmapSource LeanImage(BitmapSource image)
    {
        const int k = 2;   // drawn at twice the 256 design size, so it stays sharp in the 256px frame
        int w = (int)Panel.Width * k, h = (int)Panel.Height * k;
        var flat = Draw(w, h, dc => dc.DrawRectangle(new ImageBrush(image) { Stretch = Stretch.UniformToFill }, null, new Rect(0, 0, w, h)));
        return Draw(w, h, dc =>
        {
            for (var y = 0; y < h; y++)
            {
                var inset = Lean * k * y / (h - 1.0);
                dc.DrawImage(new CroppedBitmap(flat, new Int32Rect(0, y, w, 1)), new Rect(inset, y, w - 2 * inset, 1));
            }
        });
    }

    /// Draws in a 256x256 design space; Render() scales it to the target size. card comes from LeanImage.
    /// Depth without real 3D: light from above, a back in shadow, paper peeking out, a front card with thickness, a floor shadow.
    private static void DrawFolder(DrawingContext dc, BitmapSource card, BitmapSource? logo, string? title, int? season)
    {
        if (logo is not null) dc.PushTransform(new MatrixTransform(FolderScale, 0, 0, FolderScale, 0, 256 * (1 - FolderScale)));

        dc.DrawEllipse(new RadialGradientBrush(Color.FromArgb(110, 0, 0, 0), Color.FromArgb(0, 0, 0, 0)), null, new Point(128, 241), 126, 13);

        // Back: darker toward the bottom, a bright rim where the light catches its top edge.
        dc.DrawGeometry(Vertical((255, 0xF7, 0xC8, 0x62, 0), (255, 0xE0, 0x98, 0x2C, 0.35), (255, 0xB8, 0x6A, 0x12, 1)),
            new Pen(Vertical((200, 0xFF, 0xEB, 0xB0, 0), (0, 0xFF, 0xEB, 0xB0, 0.2), (90, 0x7A, 0x40, 0x00, 1)), 2), FolderBack);
        dc.PushClip(FolderBack);
        dc.DrawRectangle(Vertical((0, 0, 0, 0, 0), (110, 0x5A, 0x2E, 0x00, 1)), null, new Rect(0, 46, 256, 26));   // inside of the folder
        dc.Pop();

        // Two sheets of paper inside, slightly askew; only their tops show above the front card.
        foreach (var (angle, dx, shade) in new[] { (-3.5, -4.0, (byte)0xE4), (2.5, 6.0, (byte)0xFB) })
        {
            dc.PushTransform(new RotateTransform(angle, 128 + dx, 140));
            dc.DrawRoundedRectangle(Vertical((255, shade, shade, shade, 0), (255, (byte)(shade - 40), (byte)(shade - 40), (byte)(shade - 36), 0.25)),
                new Pen(new SolidColorBrush(Color.FromArgb(70, 0x50, 0x30, 0x00)), 1.2), new Rect(34 + dx, 55, 188, 120), 5, 5);
            dc.Pop();
        }

        // Front card: the soft shadow it casts, then a thin lit edge under it for the cardboard's thickness.
        dc.PushTransform(new TranslateTransform(0, 5));
        dc.DrawGeometry(new SolidColorBrush(Color.FromArgb(40, 0, 0, 0)), new Pen(new SolidColorBrush(Color.FromArgb(14, 0, 0, 0)), 10), Card);
        dc.Pop();
        dc.PushTransform(new TranslateTransform(0, 2.5));
        dc.DrawGeometry(new SolidColorBrush(Color.FromRgb(0xDC, 0x9C, 0x3E)), null, Card);
        dc.Pop();
        dc.PushClip(Card);
        dc.DrawImage(card, Panel);
        // Lit from above: a sheen across the top, the bottom falling gently into shade.
        dc.DrawRectangle(Vertical((95, 255, 255, 255, 0), (0, 255, 255, 255, 0.28), (0, 0, 0, 0, 0.65), (55, 0, 0, 0, 1)), null, Panel);
        // Darken where the text goes so it reads on any image: the top-right corner for a logo, the bottom for a title.
        if (logo is not null)
            dc.DrawRectangle(new RadialGradientBrush(
                [new GradientStop(Color.FromArgb(210, 0, 0, 0), 0), new GradientStop(Color.FromArgb(0, 0, 0, 0), 1)])
            { Center = new Point(1, 0), GradientOrigin = new Point(1, 0), RadiusX = 0.75, RadiusY = 0.9 }, null, Panel);
        else if (title is not null)
            dc.DrawRectangle(new LinearGradientBrush(
                [new GradientStop(Color.FromArgb(0, 0, 0, 0), 0.3), new GradientStop(Color.FromArgb(220, 0, 0, 0), 1)], 90), null, Panel);
        dc.Pop();
        // Bevel: bright top edge, dark bottom edge.
        dc.DrawGeometry(null, new Pen(Vertical((230, 255, 255, 255, 0), (40, 255, 255, 255, 0.15), (0, 0, 0, 0, 0.5), (70, 0, 0, 0, 1)), 2), Card);

        if (logo is null && title is not null)
        {
            // Largest bold size where the title fits in two lines without breaking a word.
            FormattedText Text(double size, Brush brush) => new(title, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal), size, brush, 1.0)
            { MaxTextWidth = TextBox.Width, MaxLineCount = 2, TextAlignment = TextAlignment.Center, Trimming = TextTrimming.CharacterEllipsis };

            var size = 44.0;
            while (size > 18 && Text(size, Brushes.White) is var t && (t.MinWidth > TextBox.Width || t.Height > TextBox.Height)) size -= 2;
            var ft = Text(size, Brushes.White);
            var y = TextBox.Bottom - ft.Height;
            dc.DrawText(Text(size, new SolidColorBrush(Color.FromArgb(160, 0, 0, 0))), new Point(TextBox.X + 2, y + 2));
            dc.DrawText(ft, new Point(TextBox.X, y));
        }

        if (season is int s)
        {
            var badge = Text2($"S{s:00}");
            var r = new Rect(Panel.Left + Lean + 10, Panel.Bottom - badge.Height - 14, badge.Width + 16, badge.Height + 4);   // bottom-left; the logo owns top-right
            dc.DrawRoundedRectangle(Vertical((255, 0xF2, 0xAE, 0x45, 0), (255, 0xC2, 0x76, 0x14, 1)),
                new Pen(new SolidColorBrush(Color.FromArgb(120, 0x5A, 0x2E, 0x00)), 1.5), r, 8, 8);
            dc.DrawText(badge, new Point(r.X + 8, r.Y + 2));
        }

        if (logo is not null)
        {
            dc.Pop();   // back to full size: the logo is drawn over the shrunken folder's top-right corner
            var scale = Math.Min(LogoMax.Width / logo.PixelWidth, LogoMax.Height / logo.PixelHeight);
            double w = logo.PixelWidth * scale, h = logo.PixelHeight * scale;
            var r = new Rect(LogoAnchor.X - w, LogoAnchor.Y - h / 2, w, h);
            // Outside the folder the logo sits on Explorer's own (often white) background;
            // a dark halo cut from its silhouette keeps white logos readable there.
            var halo = new SolidColorBrush(Color.FromArgb(70, 0, 0, 0));
            foreach (var (dx, dy) in new[] { (-2.0, 0.0), (2.0, 0.0), (0.0, -2.0), (0.0, 2.0), (-1.5, -1.5), (1.5, -1.5), (-1.5, 1.5), (1.5, 1.5), (2.0, 3.5) })
            {
                dc.PushOpacityMask(new ImageBrush(logo));
                dc.DrawRectangle(halo, null, Rect.Offset(r, dx, dy));
                dc.Pop();
            }
            dc.DrawImage(logo, r);
        }

        static FormattedText Text2(string s) => new(s, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal), 26, Brushes.White, 1.0);
    }

    /// One icon frame. Must run on an STA thread (the UI thread in the app).
    private static BitmapSource Render(int size, BitmapSource card, BitmapSource? logo, string? title, int? season)
    {
        // Logo, title and season badge are unreadable at 32px and below; small sizes show the folder and image only.
        var big = size >= 48;
        return Draw(size, size, dc =>
        {
            dc.PushTransform(new ScaleTransform(size / 256.0, size / 256.0));
            DrawFolder(dc, card, big ? logo : null, big ? title : null, big ? season : null);
            dc.Pop();
        });
    }

    /// A frozen bitmap of what draw paints. Must run on an STA thread.
    private static BitmapSource Draw(int w, int h, Action<DrawingContext> draw, double dpi = 96)
    {
        var dv = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(dv, BitmapScalingMode.HighQuality);
        using (var dc = dv.RenderOpen()) draw(dc);
        var rtb = new RenderTargetBitmap(w, h, dpi, dpi, PixelFormats.Pbgra32);
        rtb.Render(dv);
        rtb.Freeze();
        return rtb;
    }

    private static byte[] Encode(BitmapEncoder enc, BitmapSource image)
    {
        enc.Frames.Add(BitmapFrame.Create(image));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }

    /// Whether WPF can actually draw these bytes; downloads use it to skip broken images.
    public static bool Decodes(byte[] bytes)
    {
        try { Load(bytes); return true; }
        catch (Exception e) when (e is NotSupportedException or FileFormatException or ArgumentException or IOException) { return false; }
    }

    /// width: icons are 256px, so 512 is plenty there; no need to decode a 4K backdrop. The printed cover asks for more.
    private static BitmapImage Load(byte[] bytes, int width = 512)
    {
        var img = new BitmapImage();
        img.BeginInit();
        img.CacheOption = BitmapCacheOption.OnLoad;
        img.DecodePixelWidth = width;
        img.StreamSource = new MemoryStream(bytes);
        img.EndInit();
        img.Freeze();
        return img;
    }

    // ---------- technical facts ----------

    /// What MediaInfo says about one video: label/value rows for Bilgi.html, and short badges ("1080p", "HDR10", "5.1")
    /// for the page's header and the back of the covers.
    private record Tech(string File, List<(string Label, string Value)> Rows, List<string> Badges);

    private static Tech ReadTech(string file)
    {
        var t = new Tech(Path.GetFileName(file), [("Boyut", FormatSize(new FileInfo(file).Length))], []);
        MediaInfoWrapper mi;
        try { mi = new MediaInfoWrapper(file, null); }
        catch (Exception e) { t.Rows.Add(("Teknik bilgi", "okunamadı: " + e.Message)); return t; }
        if (!mi.Success) { t.Rows.Add(("Teknik bilgi", "okunamadı")); return t; }

        t.Rows.Add(("Süre", $"{TimeSpan.FromMilliseconds(mi.Duration):h\\:mm\\:ss}"));
        t.Rows.Add(("Kapsayıcı", mi.Format));
        foreach (var v in mi.VideoStreams)
        {
            t.Rows.Add(("Video", $"{v.Format} {v.Width}x{v.Height}  {v.FrameRate:0.###} fps"
                                 + (v.Bitrate > 0 ? $"  {v.Bitrate / 1e6:0.0} Mbps" : "")
                                 + (v.BitDepth > 8 ? $"  {v.BitDepth}-bit" : "")
                                 + (v.Hdr != Hdr.None ? $"  {v.Hdr}" : "")));
            t.Badges.Add(Resolution(v.Width, v.Height));
            if (v.Hdr != Hdr.None) t.Badges.Add(v.Hdr == Hdr.DolbyVision ? "Dolby Vision" : v.Hdr.ToString());
        }
        var i = 1;
        foreach (var a in mi.AudioStreams)
            t.Rows.Add(($"Ses {i++}", $"{Lang(a.Language)}  {a.Format} {a.AudioChannelsFriendly}"
                                     + (a.Bitrate > 0 ? $"  {a.Bitrate / 1000:0} kbps" : "")
                                     + (a.Default ? "  (varsayılan)" : "")));
        if (mi.AudioStreams.Count > 0 && mi.AudioStreams.Max(a => a.Channel) is var ch and > 0)
            t.Badges.Add(ch >= 3 ? $"{ch - 1}.1" : ch == 2 ? "Stereo" : "Mono");
        if (mi.Subtitles.Count > 0)
            t.Rows.Add(("Altyazı", string.Join(", ", mi.Subtitles.Select(s => $"{Lang(s.Language)} {s.Format}{(s.Forced ? " (zorunlu)" : "")}"))));

        var external = Directory.EnumerateFiles(Path.GetDirectoryName(file)!, Path.GetFileNameWithoutExtension(file) + ".*")
            .Where(f => SubExt.Contains(Path.GetExtension(f))).Select(Path.GetFileName).ToList();
        if (external.Count > 0) t.Rows.Add(("Harici altyazı", string.Join(", ", external)));
        return t;
    }

    /// The badge for a video's picture size: "4K", "1080p", "720p" or "SD".
    /// Either side reaching a class counts: scope films are short (1920×800), HDV is narrow (1440×1080).
    internal static string Resolution(int width, int height) =>
        width >= 3200 || height >= 1800 ? "4K" : width >= 1700 || height >= 1000 ? "1080p" : width >= 1200 || height >= 700 ? "720p" : "SD";

    private static string Lang(string l) => string.IsNullOrWhiteSpace(l) ? "?" : l;

    // ---------- Bilgi.html ----------

    /// A one-page, Netflix-style detail page: the backdrop as a hero with the logo, facts, plot and cast over it, then
    /// ratings, links and every file's technical details. Images are the folder's own artwork files linked by relative
    /// path, so the page works offline and from a burned disc. The TMDb link doubles as the id a refresh reads back.
    private static string InfoHtml(string folder, Meta m, int? season, List<Tech> tech)
    {
        // Only what HTML itself gives meaning to; WebUtility.HtmlEncode would also turn ü, ç, ö into &#252; and the like.
        static string H(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");
        string? Rel(string name) => Art(folder, season, name) is { } p ? Path.GetRelativePath(folder, p).Replace('\\', '/') : null;
        var title = season is null ? m.Display : $"{m.Display} — Sezon {season}";
        var tv = m.Kind == MediaKind.Series;

        var facts = new List<string>();
        if (m.Year is not null) facts.Add($"<span class=\"year\">{m.Year}</span>");
        if (season is not null) facts.Add($"<span>Sezon {season}</span>");
        if (m.Runtime is > 0) facts.Add($"<span>{m.Runtime} dk{(tv ? " / bölüm" : "")}</span>");
        if (m.Genres.Count > 0) facts.Add($"<span>{H(string.Join(" · ", m.Genres))}</span>");
        facts.AddRange(tech.SelectMany(t => t.Badges).Distinct().Select(b => $"<span class=\"badge\">{H(b)}</span>"));

        var credits = new List<(string, string)>();
        if (m.Directors.Count > 0) credits.Add((tv ? "Yaratıcı" : "Yönetmen", string.Join(", ", m.Directors)));
        if (m.Cast.Count > 0) credits.Add(("Oyuncular", string.Join(", ", m.Cast)));
        if (m.OriginalTitle.Length > 0 && m.OriginalTitle != m.Title) credits.Add(("Orijinal ad", m.OriginalTitle));
        if (m.Awards.Length > 0) credits.Add(("Ödüller", m.Awards));
        static string Dl(IEnumerable<(string Label, string Value)> rows) =>
            "<dl>" + string.Concat(rows.Select(r => $"<dt>{H(r.Label)}</dt><dd>{H(r.Value)}</dd>")) + "</dl>";

        // "Rotten Tomatoes 87%" -> big value, small source
        var ratings = string.Concat(m.Ratings.Select(r => r.LastIndexOf(' ') is var i and > 0
            ? $"<div class=\"rating\"><b>{H(r[(i + 1)..])}</b><span>{H(r[..i])}</span></div>" : ""));
        var links = (m.ImdbId is null ? "" : $"<a href=\"https://www.imdb.com/title/{H(m.ImdbId)}/\">IMDb</a>")
                  + (m.TmdbId is null ? "" : $"<a href=\"https://www.themoviedb.org/{(tv ? "tv" : "movie")}/{m.TmdbId}\">TMDb</a>");

        var hero = Rel("fanart.jpg") is { } bg ? $" style=\"background-image:url('{bg}')\"" : "";
        var name = Rel("logo.png") is { } logo ? $"<img class=\"logo\" src=\"{logo}\" alt=\"{H(m.Title)}\">" : $"<h1>{H(m.Title)}</h1>";
        var poster = Rel("folder.jpg") is { } p ? $"<img class=\"poster\" src=\"{p}\" alt=\"\">" : "";
        var disc = Rel("disc.png") is { } d ? $"<img class=\"disc\" src=\"{d}\" alt=\"Disk\">" : "";
        var cover = string.Concat(Covers.Where(c => File.Exists(Path.Combine(folder, c.File))).Select(c =>
            $"<figure><a class=\"cover\" href=\"{c.File}\"><img src=\"{c.File}\" alt=\"{c.Name} kapağı\"></a>"
            + $"<figcaption>{c.Name} kapağı · {c.W:0} × {c.H:0} mm</figcaption></figure>"));
        if (cover.Length > 0)
            cover = $"<div class=\"covers\">{cover}</div><p class=\"hint\">Gerçek boyutta yazdır, kenarlardan kes, kutunun şeffaf kılıfına yerleştir.</p>";
        var files = tech.Count == 0 ? "" : "<section><h2>Dosyalar</h2>"
            + string.Concat(tech.Select(t => $"<div class=\"file\"><h3>{H(t.File)}</h3>{Dl(t.Rows)}</div>")) + "</section>";

        return $$"""
            <!DOCTYPE html>
            <html lang="tr"><head>
            <meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
            <title>{{H(title)}}</title>
            <style>
              :root { --bg: #0e0e11; --card: #17171c; --fg: #f2f2f2; --dim: #a1a1ab; --line: rgba(255,255,255,.12); --accent: #f2ae45; }
              * { box-sizing: border-box; }
              body { margin: 0; background: var(--bg); color: var(--fg); font: 16px/1.55 "Segoe UI Variable Text", "Segoe UI", system-ui, sans-serif; }
              .hero { position: relative; min-height: min(78vh, 760px); display: flex; align-items: flex-end; background: #000 center 20% / cover no-repeat; }
              .hero::before { content: ""; position: absolute; inset: 0;
                background: linear-gradient(90deg, rgba(14,14,17,.96) 0%, rgba(14,14,17,.75) 35%, rgba(14,14,17,0) 70%),
                            linear-gradient(0deg, var(--bg) 0%, rgba(14,14,17,0) 40%); }
              .hero-in { position: relative; max-width: 680px; padding: 0 4vw 6vh; }
              .logo { display: block; max-width: min(460px, 80%); max-height: 160px; margin-bottom: 24px; filter: drop-shadow(0 4px 18px rgba(0,0,0,.7)); }
              h1 { font-size: clamp(34px, 5vw, 64px); line-height: 1.05; margin: 0 0 20px; }
              .facts { display: flex; flex-wrap: wrap; align-items: center; gap: 8px 14px; color: var(--dim); }
              .year { color: var(--fg); font-weight: 600; }
              .badge { border: 1px solid rgba(255,255,255,.45); border-radius: 4px; padding: 0 6px; font-size: 12px; font-weight: 600; color: var(--fg); letter-spacing: .04em; }
              .tagline { margin: 20px 0 0; font-size: 19px; font-style: italic; color: var(--accent); }
              .overview { margin: 10px 0 0; font-size: 17px; text-shadow: 0 1px 10px rgba(0,0,0,.6); }
              main { display: grid; gap: 56px; padding: 12px 4vw 64px; }
              .about { display: grid; grid-template-columns: 240px 1fr; gap: 44px; align-items: start; }
              .poster { display: block; width: 100%; border-radius: 10px; box-shadow: 0 24px 48px rgba(0,0,0,.6); }
              .ratings { display: flex; flex-wrap: wrap; gap: 12px; margin-bottom: 28px; }
              .rating { min-width: 128px; padding: 12px 18px; background: var(--card); border: 1px solid var(--line); border-radius: 12px; }
              .rating b { display: block; font-size: 24px; line-height: 1.2; }
              .rating span { color: var(--dim); font-size: 13px; }
              dl { display: grid; grid-template-columns: max-content 1fr; gap: 10px 28px; margin: 0; }
              dt { color: var(--dim); }
              dd { margin: 0; }
              .links { margin-top: 28px; display: flex; flex-wrap: wrap; gap: 10px; }
              .links a { color: var(--fg); text-decoration: none; border: 1px solid var(--line); border-radius: 999px; padding: 6px 16px; font-size: 14px; }
              .links a:hover { border-color: var(--accent); color: var(--accent); }
              h2 { margin: 0 0 18px; font-size: 21px; font-weight: 600; }
              .file { background: var(--card); border: 1px solid var(--line); border-radius: 12px; padding: 18px 22px; margin-bottom: 14px; }
              .file h3 { margin: 0 0 12px; font-size: 15px; font-weight: 600; overflow-wrap: anywhere; }
              .file dl { gap: 6px 28px; font-size: 14px; }
              .extras { display: flex; flex-wrap: wrap; align-items: center; gap: 40px; }
              .disc { width: 220px; animation: spin 40s linear infinite; filter: drop-shadow(0 12px 24px rgba(0,0,0,.6)); }
              @keyframes spin { to { transform: rotate(360deg); } }
              .extras > div { flex: 1 1 480px; }
              .covers { display: flex; flex-wrap: wrap; align-items: flex-end; gap: 28px; }
              figure { margin: 0; flex: 0 1 480px; }
              .cover img { display: block; width: 100%; border-radius: 4px; box-shadow: 0 24px 48px rgba(0,0,0,.6); }
              figcaption, .hint { color: var(--dim); font-size: 13px; margin: 10px 0 0; }
              footer { color: var(--dim); font-size: 12px; padding: 0 4vw 32px; }
              @media (max-width: 760px) { .about { grid-template-columns: 1fr; } .poster { width: 180px; } }
              @media (prefers-reduced-motion: reduce) { .disc { animation: none; } }
            </style></head><body>
            <header class="hero"{{hero}}><div class="hero-in">
            {{name}}
            <div class="facts">{{string.Join("", facts)}}</div>
            {{(m.Tagline.Length > 0 ? $"<p class=\"tagline\">{H(m.Tagline)}</p>" : "")}}
            {{(m.Overview.Length > 0 ? $"<p class=\"overview\">{H(m.Overview)}</p>" : "")}}
            </div></header>
            <main>
            <section class="about">{{poster}}<div>
            {{(ratings.Length > 0 ? $"<div class=\"ratings\">{ratings}</div>" : "")}}
            {{Dl(credits)}}
            <div class="links">{{links}}</div>
            </div></section>
            {{files}}
            {{(disc + cover == "" ? "" : $"<section class=\"extras\">{disc}<div>{cover}</div></section>")}}
            </main>
            <footer>{{H(title)}} · Arşivci ile düzenlendi · bilgiler TMDb, OMDb ve Fanart.tv'den</footer>
            </body></html>
            """;
    }

    // ---------- DVD and Blu-ray covers ----------

    /// A case insert in millimetres, laid out back | spine | front. Mark is the format's name on its band (Light to
    /// Dark, top to bottom) across the front and at the top of the spine; plain text, not the licensed logos.
    private record CoverFormat(string File, string Name, double W, double H, double Spine, string Mark, Color Light, Color Dark)
    {
        public double Face => (W - Spine) / 2;
    }
    // Amaray DVD case with a black band, and the standard Blu-ray case: shorter, with its blue band.
    private static readonly CoverFormat[] Covers =
    [
        new("kapak-dvd.jpg", "DVD", 273, 183, 14, "DVD VIDEO", Color.FromRgb(0x2E, 0x2E, 0x34), Color.FromRgb(0x08, 0x08, 0x0B)),
        new("kapak-bluray.jpg", "Blu-ray", 269, 148, 12, "BLU-RAY DISC", Color.FromRgb(0x2A, 0xA4, 0xF4), Color.FromRgb(0x0B, 0x6E, 0xCC)),
    ];
    private const double CoverBand = 11;
    private const int CoverDpi = 300;

    /// The paper that slides under the case's clear sleeve, one per format: the back with plot, stills, credits and
    /// specs, the spine with the logo, the front with the poster. 300 dpi JPEG bytes; empty when there is no poster.
    /// Call from the UI thread.
    // ponytail: each 300 dpi render blocks the UI thread for a moment per folder; move it to its own STA thread if Apply stutters.
    private static async Task<List<(string File, byte[] Jpeg)>> MakeCovers(string folder, Meta m, int? season, IEnumerable<string> posters, List<Tech> tech)
    {
        // Print wants more pixels than the 780px poster the icon used; TMDb serves the original too.
        var front = MetadataService.Fetch(posters.Select(u => u.Replace("/t/p/w780/", "/t/p/original/")).Concat(posters));
        var stills = Task.WhenAll(m.Backdrops.Skip(1).Take(3).Select(u => MetadataService.Fetch([u])));
        var posterBytes = await front ?? (Art(folder, season, "folder.jpg") is { } jpg ? await File.ReadAllBytesAsync(jpg) : null);
        if (posterBytes is null) return [];
        var stillBytes = (await stills).OfType<byte[]>().ToList();

        BitmapImage? FromDisk(string name, int width) => Art(folder, season, name) is { } p ? Load(File.ReadAllBytes(p), width) : null;
        try
        {
            var (poster, backdrop, logo, shots) = await Task.Run(() =>
                (Load(posterBytes, 1600), FromDisk("fanart.jpg", 1600), FromDisk("logo.png", 1200), stillBytes.Select(b => Load(b, 640)).ToList()));
            var badges = tech.SelectMany(t => t.Badges).Distinct().ToList();
            if (m.Runtime is > 0) badges.Add($"{m.Runtime} dk");
            var covers = new List<(string, byte[])>();
            foreach (var f in Covers)
            {
                var image = Draw((int)Math.Round(f.W / 25.4 * CoverDpi), (int)Math.Round(f.H / 25.4 * CoverDpi), dc =>
                {
                    dc.PushTransform(new ScaleTransform(96 / 25.4, 96 / 25.4));   // millimetres from here on
                    DrawCover(dc, f, poster, backdrop, logo, shots, m, season, badges);
                    dc.Pop();
                }, CoverDpi);
                covers.Add((f.File, await Task.Run(() => Encode(new JpegBitmapEncoder { QualityLevel = 92 }, image))));
            }
            return covers;
        }
        catch (Exception e) when (e is NotSupportedException or FileFormatException or IOException)
        {
            return [];   // an artwork file that won't decode: no covers rather than no Bilgi.html
        }
    }

    private static void DrawCover(DrawingContext dc, CoverFormat f, BitmapSource poster, BitmapSource? backdrop, BitmapSource? logo,
                                  List<BitmapImage> shots, Meta m, int? season, List<string> badges)
    {
        const double pad = 9;
        double CoverW = f.W, CoverH = f.H, SpineW = f.Spine, Face = f.Face;
        var k = CoverH / 183;   // vertical layout was set on the DVD's height; the Blu-ray's shorter back scales with it
        var bg = Color.FromRgb(0x0E, 0x0E, 0x11);
        var dim = new SolidColorBrush(Color.FromRgb(0xA1, 0xA1, 0xAB));
        var accent = new SolidColorBrush(Color.FromRgb(0xF2, 0xAE, 0x45));
        FormattedText T(string s, double mm, Brush brush, double? maxWidth = null, FontWeight? weight = null, FontStyle? style = null) =>
            new(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                new Typeface(new FontFamily("Segoe UI"), style ?? FontStyles.Normal, weight ?? FontWeights.Normal, FontStretches.Normal),
                mm, brush, CoverDpi / 96.0) { MaxTextWidth = maxWidth ?? Face - 2 * pad, Trimming = TextTrimming.WordEllipsis };
        void Fit(BitmapSource img, Rect box)   // centred, aspect kept
        {
            var s = Math.Min(box.Width / img.PixelWidth, box.Height / img.PixelHeight);
            dc.DrawImage(img, new Rect(box.X + (box.Width - img.PixelWidth * s) / 2, box.Y + (box.Height - img.PixelHeight * s) / 2, img.PixelWidth * s, img.PixelHeight * s));
        }

        dc.DrawRectangle(new SolidColorBrush(bg), null, new Rect(0, 0, CoverW, CoverH));

        // ----- back: a still fading into the dark, logo over it, then plot, stills, credits, specs from the bottom up
        var band = new Rect(0, 0, Face, 78 * k);
        if (backdrop is not null)
        {
            dc.PushClip(new RectangleGeometry(band));
            dc.DrawRectangle(new ImageBrush(backdrop) { Stretch = Stretch.UniformToFill }, null, band);
            dc.DrawRectangle(Vertical((0, bg.R, bg.G, bg.B, 0.35), (255, bg.R, bg.G, bg.B, 1)), null, band);
            dc.Pop();
        }
        var y = 50 * k;
        if (logo is not null) { Fit(logo, new Rect(pad, y, Face - 2 * pad, 24 * k)); y += 30 * k; }
        else
        {
            var t = T(m.Title, 8, Brushes.White, weight: FontWeights.Bold);
            t.MaxLineCount = 2;
            dc.DrawText(t, new Point(pad, y));
            y += t.Height + 6;
        }
        if (m.Tagline.Length > 0)
        {
            var t = T(m.Tagline, 3.8, accent, style: FontStyles.Italic);
            t.MaxLineCount = 2;
            dc.DrawText(t, new Point(pad, y));
            y += t.Height + 3;
        }

        var bottom = CoverH - pad;
        // specs: badges on the left, the first two ratings on the right
        var x = pad;
        foreach (var b in badges)
        {
            var t = T(b, 2.6, Brushes.White, weight: FontWeights.SemiBold);
            var r = new Rect(x, bottom - t.Height - 1.2, t.Width + 3, t.Height + 1.2);
            if (r.Right > Face / 2 + 10) break;
            dc.DrawRoundedRectangle(null, new Pen(new SolidColorBrush(Color.FromArgb(150, 255, 255, 255)), 0.25), r, 0.8, 0.8);
            dc.DrawText(t, new Point(x + 1.5, r.Y + 0.6));
            x = r.Right + 1.8;
        }
        var rating = T(string.Join("   ", m.Ratings.Take(2)), 2.6, accent, weight: FontWeights.SemiBold);
        rating.TextAlignment = TextAlignment.Right;
        dc.DrawText(rating, new Point(pad, bottom - rating.Height - 0.6));
        bottom -= 10;

        var credits = string.Join("   ", new[]
        {
            m.Directors.Count > 0 ? $"{(m.Kind == MediaKind.Series ? "YARATICI" : "YÖNETMEN")} {string.Join(", ", m.Directors)}" : "",
            m.Cast.Count > 0 ? $"OYUNCULAR {string.Join(", ", m.Cast)}" : "",
        }.Where(s => s.Length > 0));
        if (credits.Length > 0)
        {
            var t = T(credits, 2.4, dim);
            t.MaxLineCount = 3;
            t.LineHeight = 3.3;
            bottom -= t.Height;
            dc.DrawText(t, new Point(pad, bottom));
            bottom -= 5;
        }

        if (shots.Count > 0)
        {
            const double gap = 2.5;
            var h = 20 * k;
            var w = (Face - 2 * pad - gap * (shots.Count - 1)) / shots.Count;
            bottom -= h;
            for (var i = 0; i < shots.Count; i++)
            {
                var r = new Rect(pad + i * (w + gap), bottom, w, h);
                dc.PushClip(new RectangleGeometry(r, 1, 1));
                dc.DrawRectangle(new ImageBrush(shots[i]) { Stretch = Stretch.UniformToFill }, null, r);
                dc.Pop();
            }
            bottom -= 6;
        }

        if (m.Overview.Length > 0 && bottom - y > 8)
        {
            var t = T(m.Overview, 3.1, new SolidColorBrush(Color.FromRgb(0xE6, 0xE6, 0xEA)));
            t.LineHeight = 4.5;
            t.MaxTextHeight = bottom - y;
            dc.DrawText(t, new Point(pad, y + 1));
        }

        // ----- spine: the format's mark, logo (or title), year, reading top to bottom
        var spine = new Rect(Face, 0, SpineW, CoverH);
        dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x06, 0x06, 0x08)), null, spine);
        var c = new Point(spine.X + SpineW / 2, CoverH / 2);
        dc.PushTransform(new RotateTransform(90, c.X, c.Y));   // local +x now runs down the spine
        var top = c.X - CoverH / 2;
        dc.DrawEllipse(null, new Pen(Brushes.White, 0.6), new Point(top + 6, c.Y), 2.4, 2.4);   // a plain disc
        dc.DrawEllipse(Brushes.White, null, new Point(top + 6, c.Y), 0.7, 0.7);
        var mark = T(f.Mark, 2.6, Brushes.White, 24, FontWeights.Bold);
        mark.MaxLineCount = 1;
        dc.DrawText(mark, new Point(top + 10, c.Y - mark.Height / 2));
        var name = new Rect(top + 36, c.Y - 4.5, CoverH - 81, 9);   // between the mark and the year
        var label = season is null ? m.Year?.ToString() ?? "" : $"SEZON {season}";
        if (logo is not null) Fit(logo, name);
        else
        {
            var t = T(m.Title, 5, Brushes.White, name.Width, FontWeights.Bold);
            t.MaxLineCount = 1;
            t.TextAlignment = TextAlignment.Center;
            dc.DrawText(t, new Point(name.X, c.Y - t.Height / 2));
        }
        var l = T(label, 3.6, accent, 30, FontWeights.SemiBold);
        l.TextAlignment = TextAlignment.Right;
        dc.DrawText(l, new Point(c.X + CoverH / 2 - pad - 30, c.Y - l.Height / 2));
        dc.Pop();

        // ----- front: the poster, edge to edge under the format's band. The Blu-ray front is nearly square, so a 2:3
        // poster loses a third of its height; cut it from the bottom (billing block), where the title rarely is.
        const double round = 5;   // the band's lower outer corner is rounded off, as on printed inserts
        var face = new Rect(Face + SpineW, CoverBand - round, Face, CoverH - CoverBand + round);
        dc.PushClip(new RectangleGeometry(face));
        dc.DrawRectangle(new ImageBrush(poster) { Stretch = Stretch.UniformToFill, AlignmentY = AlignmentY.Top }, null, face);
        dc.Pop();
        var tab = Geometry.Parse(FormattableString.Invariant(
            $"M {face.X},0 H {face.Right} V {CoverBand - round} Q {face.Right},{CoverBand} {face.Right - round},{CoverBand} H {face.X} Z"));
        dc.PushTransform(new TranslateTransform(0, 0.5));
        dc.DrawGeometry(new SolidColorBrush(Color.FromArgb(110, 0, 0, 0)), null, tab);   // its shadow on the poster
        dc.Pop();
        dc.DrawGeometry(new LinearGradientBrush(f.Light, f.Dark, 90), null, tab);
        var banner = T(f.Mark, CoverBand * 0.36, Brushes.White, Face, FontWeights.Bold);
        banner.TextAlignment = TextAlignment.Center;
        dc.DrawText(banner, new Point(face.X, (CoverBand - banner.Height) / 2));
    }

    public static string FormatSize(long bytes) =>
        bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):0.00} GB" : $"{bytes / (double)(1L << 20):0} MB";

    // ---------- disc ----------

    private static readonly (string Name, long Bytes)[] Discs =
        [("CD", 700L << 20), ("DVD", 4_700_372_992), ("DVD DL", 8_543_666_176), ("BD-25", 25_025_314_816), ("BD-50", 50_050_629_632)];

    /// Turns a folder into a disc root: autorun.inf + icon, and a poster menu when there is more than one video.
    public static string PrepareDisc(string root)
    {
        var videos = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Where(IsVideo).Order().ToList();
        if (videos.Count == 0) throw new InvalidOperationException("Bu klasörde video yok.");
        var folders = Directory.GetDirectories(root).Order().ToList();

        var label = folders.Count == 1 && Directory.GetFiles(root).All(f => !IsVideo(f)) ? Path.GetFileName(folders[0]) : Path.GetFileName(root);
        var icon = folders.Select(f => Path.Combine(f, "folder.ico")).FirstOrDefault(File.Exists);
        if (icon is not null)
        {
            WriteVisible(Path.Combine(root, "autorun.ico"), f => File.Copy(icon, f, true));
            File.SetAttributes(Path.Combine(root, "autorun.ico"), FileAttributes.Hidden);
        }

        string run, action;
        if (videos.Count == 1)
        {
            run = Path.GetRelativePath(root, videos[0]);
            action = $"{label} — Oynat";
        }
        else
        {
            File.WriteAllText(Path.Combine(root, "menu.hta"), MenuHta(root, label, folders), new UTF8Encoding(true));
            run = "menu.hta";
            action = $"{label} — Menüyü aç";
        }

        var inf = $"[autorun]\r\nlabel={label}\r\n" + (icon is null ? "" : "icon=autorun.ico\r\n")
                + $"shellexecute=\"{run}\"\r\naction={action}\r\n";
        WriteVisible(Path.Combine(root, "autorun.inf"), f => File.WriteAllText(f, inf, Encoding.Unicode));
        File.SetAttributes(Path.Combine(root, "autorun.inf"), FileAttributes.Hidden);

        var total = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length);
        var fit = Discs.FirstOrDefault(d => total <= d.Bytes).Name;
        return $"{videos.Count} video, toplam {FormatSize(total)}. "
             + (fit is null ? "Tek diske sığmıyor, klasörü bölmen gerek." : $"{fit} diske sığar.");
    }

    /// HTML Application (mshta) menu: unlike a browser page it can hand videos to the default player.
    private static string MenuHta(string root, string label, List<string> folders)
    {
        static string Js(string s) => JsonSerializer.Serialize(s);   // escapes quotes/backslashes for inline JS
        static string Url(string rel) => string.Join("/", rel.Split(Path.DirectorySeparatorChar).Select(Uri.EscapeDataString));
        static string Html(string s) => System.Net.WebUtility.HtmlEncode(s);

        var cards = new StringBuilder();
        foreach (var f in folders)
        {
            var rel = Path.GetRelativePath(root, f);
            var vids = Directory.EnumerateFiles(f, "*", SearchOption.AllDirectories).Where(IsVideo).ToList();
            if (vids.Count == 0) continue;
            // One video -> play it; a series or multi-part folder -> open it in Explorer, where the season icons show.
            var target = vids.Count == 1 ? Path.GetRelativePath(root, vids[0]) : rel;
            var poster = File.Exists(Path.Combine(f, "folder.jpg")) ? $"<img src=\"{Url(rel)}/folder.jpg\">" : "<div class=\"noimg\"></div>";
            var sub = vids.Count == 1 ? "Oynat" : $"{vids.Count} video";
            cards.Append($"<a class=\"card\" href=\"#\" onclick='go({Js(target)});return false'>{poster}<div class=\"t\">{Html(Path.GetFileName(f))}</div><div class=\"s\">{sub}</div></a>\n");
        }

        return $$"""
            <!DOCTYPE html>
            <html><head>
            <meta charset="utf-8"><meta http-equiv="x-ua-compatible" content="ie=edge">
            <title>{{Html(label)}}</title>
            <hta:application applicationname="ArsivciMenu" border="thin" innerborder="no" scroll="yes" singleinstance="yes" windowstate="maximize" icon="autorun.ico">
            <style>
              body { margin: 0; background: #111; color: #eee; font-family: "Segoe UI", sans-serif; }
              h1 { font-weight: 300; font-size: 32px; margin: 36px 48px 4px; }
              p.hint { color: #888; margin: 0 48px 16px; }
              .grid { padding: 8px 36px 48px; }
              .card { display: inline-block; vertical-align: top; width: 200px; margin: 12px; color: #eee; text-decoration: none; }
              .card img, .noimg { width: 200px; height: 300px; border-radius: 8px; background: #222; display: block; }
              .card:hover img, .card:hover .noimg { outline: 3px solid #4cc2ff; }
              .t { font-size: 15px; margin-top: 10px; }
              .s { font-size: 12px; color: #888; }
            </style>
            <script>
              var sh = new ActiveXObject("WScript.Shell");
              var me = unescape(document.URL).replace(/^file:\/+/i, "").replace(/\//g, "\\");
              var base = me.substring(0, me.lastIndexOf("\\"));
              function go(rel) { sh.Run('"' + base + "\\" + rel + '"'); }
            </script>
            </head><body>
            <h1>{{Html(label)}}</h1>
            <p class="hint">Oynatmak için afişe tıkla.</p>
            <div class="grid">
            {{cards}}</div>
            </body></html>
            """;
    }
}
