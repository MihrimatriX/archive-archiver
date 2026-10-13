using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
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

    // ---------- folder decoration ----------

    /// Downloads artwork, builds folder.ico + desktop.ini and writes Bilgi.txt. Must run on the UI thread (WPF imaging).
    public static async Task Decorate(string folder, Meta m, int? season)
    {
        var posters = season is int n && m.SeasonPosters.TryGetValue(n, out var sp) ? sp.Concat(m.Posters) : m.Posters;
        var jpg = Path.Combine(folder, "folder.jpg");
        var ico = Path.Combine(folder, "folder.ico");

        // The artwork files don't depend on each other: download them all at once.
        List<(IEnumerable<string> Urls, string File)> artwork = [(posters, "folder.jpg")];
        if (season is null) artwork.AddRange([(m.Backdrops, "fanart.jpg"), (m.Logos, "logo.png"), (m.Discs, "disc.png")]);
        await Task.WhenAll(artwork.Where(a => !File.Exists(Path.Combine(folder, a.File)))
                              .Select(a => MetadataService.Download(a.Urls, Path.Combine(folder, a.File))));

        // Front panel: the film's wide backdrop; season folders use their own poster so seasons look different.
        var front = season is null && File.Exists(Path.Combine(folder, "fanart.jpg")) ? Path.Combine(folder, "fanart.jpg") : jpg;
        var logo = Path.Combine(season is null ? folder : Path.GetDirectoryName(folder)!, "logo.png");
        if (File.Exists(front))
        {
            var icon = await MakeIcon(front, File.Exists(logo) ? logo : null, FolderTitle(m), season);
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

        var info = await Task.Run(() => InfoText(folder, m, season));   // MediaInfo reads can take a while per file
        WriteVisible(Path.Combine(folder, "Bilgi.txt"), f => File.WriteAllText(f, info, new UTF8Encoding(true)));
    }

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
            return await Application.Current.Dispatcher.InvokeAsync(() => Render(256, f, l, FolderTitle(m), season));
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
        var frames = sizes.Select(size => Render(size, front, logo, title, season)).ToList();   // frozen, so the pool can encode them
        var pngs = await Task.Run(() => frames.Select(Png).ToList());

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

    private static readonly Geometry FolderBack =
        Geometry.Parse("M 14,44 Q 14,30 28,30 H 90 Q 99,30 105,37 L 115,48 H 228 Q 242,48 242,62 V 214 Q 242,228 228,228 H 28 Q 14,228 14,214 Z");
    private static readonly Rect Panel = new(8, 70, 240, 166);
    private static readonly Rect TextBox = new(22, 148, 212, 78);     // title fallback: bottom, room for long names
    // With a logo the folder shrinks toward the bottom-left corner, freeing the top-right for the logo to hang over
    // the folder's edge. The logo's right edge and vertical middle are pinned at LogoAnchor (unscaled space).
    private const double FolderScale = 0.8;
    private static readonly Point LogoAnchor = new(252, 96);
    private static readonly Size LogoMax = new(156, 88);

    /// Draws in a 256x256 design space; Png() scales it to the target size.
    private static void DrawFolder(DrawingContext dc, BitmapSource front, BitmapSource? logo, string? title, int? season)
    {
        if (logo is not null) dc.PushTransform(new MatrixTransform(FolderScale, 0, 0, FolderScale, 0, 256 * (1 - FolderScale)));
        dc.DrawGeometry(new LinearGradientBrush(Color.FromRgb(0xF4, 0xBE, 0x55), Color.FromRgb(0xD8, 0x8F, 0x22), 90), null, FolderBack);

        var panel = new RectangleGeometry(Panel, 14, 14);
        dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(80, 0, 0, 0)), null, Rect.Offset(Panel, 0, 4), 14, 14);   // shadow
        dc.PushClip(panel);
        dc.DrawRectangle(new ImageBrush(front) { Stretch = Stretch.UniformToFill }, null, Panel);
        // Darken where the text goes so it reads on any image: the top-right corner for a logo, the bottom for a title.
        if (logo is not null)
            dc.DrawRectangle(new RadialGradientBrush(
                [new GradientStop(Color.FromArgb(210, 0, 0, 0), 0), new GradientStop(Color.FromArgb(0, 0, 0, 0), 1)])
            { Center = new Point(1, 0), GradientOrigin = new Point(1, 0), RadiusX = 0.75, RadiusY = 0.9 }, null, Panel);
        else if (title is not null)
            dc.DrawRectangle(new LinearGradientBrush(
                [new GradientStop(Color.FromArgb(0, 0, 0, 0), 0.3), new GradientStop(Color.FromArgb(220, 0, 0, 0), 1)], 90), null, Panel);
        dc.Pop();
        dc.DrawGeometry(null, new Pen(new SolidColorBrush(Color.FromArgb(90, 255, 255, 255)), 2), panel);

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
            var r = new Rect(Panel.Left + 10, Panel.Bottom - badge.Height - 14, badge.Width + 16, badge.Height + 4);   // bottom-left; the logo owns top-right
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(0xD8, 0x8F, 0x22)), null, r, 8, 8);
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
    private static BitmapSource Render(int size, BitmapSource front, BitmapSource? logo, string? title, int? season)
    {
        // Logo, title and season badge are unreadable at 32px and below; small sizes show the folder and image only.
        var big = size >= 48;
        var dv = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(dv, BitmapScalingMode.HighQuality);
        using (var dc = dv.RenderOpen())
        {
            dc.PushTransform(new ScaleTransform(size / 256.0, size / 256.0));
            DrawFolder(dc, front, big ? logo : null, big ? title : null, big ? season : null);
            dc.Pop();
        }
        var rtb = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        rtb.Freeze();
        return rtb;
    }

    private static byte[] Png(BitmapSource image)
    {
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(image));
        using var ms = new MemoryStream();
        enc.Save(ms);
        return ms.ToArray();
    }

    private static BitmapImage Load(byte[] bytes)
    {
        var img = new BitmapImage();
        img.BeginInit();
        img.CacheOption = BitmapCacheOption.OnLoad;
        img.DecodePixelWidth = 512;                              // icons are 256px; no need to decode a 4K backdrop
        img.StreamSource = new MemoryStream(bytes);
        img.EndInit();
        img.Freeze();
        return img;
    }

    // ---------- Bilgi.txt ----------

    private static string InfoText(string folder, Meta m, int? season)
    {
        var sb = new StringBuilder();
        sb.AppendLine(season is null ? m.Display : $"{m.Display} — Sezon {season}");
        sb.AppendLine(new string('═', 60));
        if (m.OriginalTitle.Length > 0 && m.OriginalTitle != m.Title) sb.AppendLine($"Orijinal ad : {m.OriginalTitle}");
        if (m.Tagline.Length > 0) sb.AppendLine($"Slogan      : {m.Tagline}");
        if (m.Genres.Count > 0) sb.AppendLine($"Tür         : {string.Join(", ", m.Genres)}");
        if (m.Runtime is > 0) sb.AppendLine($"Süre        : {m.Runtime} dk{(m.Kind == MediaKind.Series ? " (bölüm)" : "")}");
        if (m.Directors.Count > 0) sb.AppendLine($"{(m.Kind == MediaKind.Series ? "Yaratıcı    " : "Yönetmen    ")}: {string.Join(", ", m.Directors)}");
        if (m.Cast.Count > 0) sb.AppendLine($"Oyuncular   : {string.Join(", ", m.Cast)}");
        if (m.Ratings.Count > 0) sb.AppendLine($"Puanlar     : {string.Join("  ·  ", m.Ratings)}");
        if (m.Awards.Length > 0) sb.AppendLine($"Ödüller     : {m.Awards}");
        if (m.ImdbId is not null) sb.AppendLine($"IMDb        : https://www.imdb.com/title/{m.ImdbId}/");
        if (m.TmdbId is not null) sb.AppendLine($"TMDb        : https://www.themoviedb.org/{(m.Kind == MediaKind.Series ? "tv" : "movie")}/{m.TmdbId}");
        if (m.Overview.Length > 0) sb.AppendLine().AppendLine("Konu").AppendLine(m.Overview);

        foreach (var video in Directory.EnumerateFiles(folder).Where(IsVideo).Order())
            sb.AppendLine().Append(TechText(video));
        return sb.ToString();
    }

    private static string TechText(string file)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"── {Path.GetFileName(file)} ──");
        sb.AppendLine($"Boyut       : {FormatSize(new FileInfo(file).Length)}");

        MediaInfoWrapper mi;
        try { mi = new MediaInfoWrapper(file, null); }
        catch (Exception e) { return sb.AppendLine($"(Teknik bilgi okunamadı: {e.Message})").ToString(); }
        if (!mi.Success) return sb.AppendLine("(Teknik bilgi okunamadı)").ToString();

        sb.AppendLine($"Süre        : {TimeSpan.FromMilliseconds(mi.Duration):h\\:mm\\:ss}    Kapsayıcı: {mi.Format}");
        foreach (var v in mi.VideoStreams)
            sb.AppendLine($"Video       : {v.Format} {v.Width}x{v.Height}  {v.FrameRate:0.###} fps"
                          + (v.Bitrate > 0 ? $"  {v.Bitrate / 1e6:0.0} Mbps" : "")
                          + (v.BitDepth > 8 ? $"  {v.BitDepth}-bit" : "")
                          + (v.Hdr != Hdr.None ? $"  {v.Hdr}" : ""));
        var i = 1;
        foreach (var a in mi.AudioStreams)
            sb.AppendLine($"Ses {i++,-8}: {Lang(a.Language)}  {a.Format} {a.AudioChannelsFriendly}"
                          + (a.Bitrate > 0 ? $"  {a.Bitrate / 1000:0} kbps" : "")
                          + (a.Default ? "  (varsayılan)" : ""));
        if (mi.Subtitles.Count > 0)
            sb.AppendLine($"Altyazı     : {string.Join(", ", mi.Subtitles.Select(t => $"{Lang(t.Language)} {t.Format}{(t.Forced ? " (zorunlu)" : "")}"))}");

        var baseName = Path.GetFileNameWithoutExtension(file);
        var external = Directory.EnumerateFiles(Path.GetDirectoryName(file)!, baseName + ".*")
            .Where(f => SubExt.Contains(Path.GetExtension(f))).Select(Path.GetFileName).ToList();
        if (external.Count > 0) sb.AppendLine($"Harici alt. : {string.Join(", ", external)}");
        return sb.ToString();
    }

    private static string Lang(string l) => string.IsNullOrWhiteSpace(l) ? "?" : l;

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
