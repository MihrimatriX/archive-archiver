using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Navigation;
using Microsoft.Win32;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;
using MessageBox = Wpf.Ui.Controls.MessageBox;
using MessageBoxResult = Wpf.Ui.Controls.MessageBoxResult;

namespace ArchiveArchiver;

public partial class MainWindow : FluentWindow
{
    private const string Matched = "Eşleşti", Unsure = "Kontrol et", Organized = "Zaten düzenli", Moved = "Taşındı", NotFound = "Bulunamadı";

    private readonly Settings settings = Settings.Load();
    private readonly ObservableCollection<MediaItem> items = [];
    private readonly ObservableCollection<LogEntry> log = [];
    private bool busy;

    public MainWindow()
    {
        InitializeComponent();
        SystemThemeWatcher.Watch(this);
        List.ItemsSource = Tiles.ItemsSource = items;
        LogList.ItemsSource = log;
        SourceBox.Text = settings.SourcePath;
        LibraryBox.Text = settings.LibraryPath;
        SizeChanged += (_, _) => Responsive();
        Loaded += (_, _) =>
        {
            Responsive();
            if (settings.TmdbKey == "" && settings.OmdbKey == "") OpenSettings();
        };
    }

    // ---------- 1. scan ----------

    private async void Scan_Click(object sender, RoutedEventArgs e)
    {
        var src = SourceBox.Text.Trim();
        if (!Directory.Exists(src)) { Say("Kaynak klasör bulunamadı."); return; }
        SavePaths();

        await Run("Taranıyor…", async () =>
        {
            var found = await Task.Run(() => Directory
                .EnumerateFiles(src, "*", new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = true,
                    AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,   // $RECYCLE.BIN etc.
                })
                .Where(Organizer.IsVideo)
                .Select(f => new FileInfo(f))
                .Where(f => !(f.Name.Contains("sample", StringComparison.OrdinalIgnoreCase) && f.Length < 300L << 20))
                .OrderBy(f => f.FullName)
                .Select(f => new MediaItem { SourcePath = f.FullName, Parsed = NameParser.Parse(f.FullName), Size = f.Length })
                .ToList());

            items.Clear();
            foreach (var it in found) items.Add(it);
            Say(found.Count == 0 ? "Hiç video bulunamadı." : $"{found.Count} video bulundu. Şimdi Eşleştir'e bas.");
        });
    }

    // ---------- 2. match ----------

    private async void Match_Click(object sender, RoutedEventArgs e)
    {
        if (!HasKeys()) return;
        var groups = items.Where(i => i.Status != Moved && i.Meta is null).GroupBy(i => i.GroupKey).ToList();
        if (groups.Count == 0) { Say(items.Count == 0 ? "Önce Tara'ya bas." : "Eşleşmemiş dosya kalmadı."); return; }

        var svc = new MetadataService(settings);
        var lib = Library();
        await Run("Eşleştiriliyor…", async () =>
        {
            var done = 0;
            Step(0, groups.Count);
            // ponytail: 8 parallel lookups stays under TMDb's ~40 req/s; a 429 still waits and retries in MetadataService.Get.
            await Parallel.ForEachAsync(groups, new ParallelOptions { MaxDegreeOfParallelism = 8 }, async (g, ct) =>
            {
                var group = g.ToList();
                var first = group[0];
                try
                {
                    foreach (var it in group) it.Status = "Aranıyor…";
                    var candidates = await svc.Search(first.Kind, first.Parsed.Title, first.Parsed.Year);
                    var best = Matcher.PickBest(candidates, first.Parsed);
                    if (best is null)
                        foreach (var it in group) { it.Candidates = candidates; it.Status = NotFound; }
                    else
                        await ApplyMatch(group, candidates, best, svc, lib);
                }
                catch (Exception ex)
                {
                    foreach (var it in group) it.Status = "Hata: " + ex.Message;
                }
                var name = group.Count == 1 ? first.FileName : $"{first.Parsed.Title} ({group.Count} bölüm)";
                Log($"{name}  →  {first.Meta?.Display ?? first.Status}", first.Status);
                var n = Interlocked.Increment(ref done);
                _ = Dispatcher.InvokeAsync(() => Step(n, groups.Count));   // don't hold the worker while the UI is busy
            });
            Say("Eşleştirme bitti, henüz hiçbir dosya taşınmadı. Hedef sütununu kontrol et, yanlışları sağ panelden düzelt, sonra 3 Uygula'ya bas." + Warnings(svc));
        });
    }

    private static string Warnings(MetadataService svc) =>
        svc.Warnings.IsEmpty ? "" : "   ⚠ " + string.Join("  ", svc.Warnings.Select(w => $"{w.Key}: {w.Value}, atlandı."));

    /// manual: the user picked it in the detail pane, so it needs no "Kontrol et".
    private static async Task ApplyMatch(List<MediaItem> group, List<Candidate> candidates, Candidate pick, MetadataService svc, string lib, bool manual = false)
    {
        var meta = await svc.Details(pick);
        var sure = manual || Matcher.Confident(pick, group[0].Parsed);
        foreach (var it in group)
        {
            it.Candidates = candidates;
            it.Meta = meta;
            it.TargetPath = Organizer.Target(it, lib);
            it.Status = string.Equals(it.TargetPath, it.SourcePath, StringComparison.OrdinalIgnoreCase) ? Organized : sure ? Matched : Unsure;
        }
        // Tiles swap the poster for the folder icon Apply will create: one per movie, one per season folder.
        foreach (var folder in group.GroupBy(i => i.Kind == MediaKind.Series ? i.Parsed.Season : null))
        {
            var icon = await Organizer.PreviewIcon(meta, folder.Key);
            foreach (var it in folder) it.FolderIcon = icon;
        }
    }

    // ---------- manual fix ----------

    private void SearchBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) Search_Click(sender, e);
    }

    private async void Search_Click(object sender, RoutedEventArgs e)
    {
        if (List.SelectedItem is not MediaItem it || !HasKeys()) return;
        var q = NameParser.ParseName(SearchBox.Text.Trim());   // "Avatar 2009" -> title + year
        await Run("Aranıyor…", async () =>
        {
            it.Candidates = await new MetadataService(settings).Search(it.Kind, q.Title, q.Year);
            Say(it.Candidates.Count == 0 ? "Sonuç yok, adı farklı yazmayı dene." : "Doğru olanı listeden seç.");
        });
    }

    private async void Candidate_Selected(object sender, SelectionChangedEventArgs e)
    {
        if (CandidatesList.SelectedItem is not Candidate pick || List.SelectedItem is not MediaItem it || busy) return;
        if (it.Meta is { } m && (m.TmdbId, m.ImdbId) == (pick.TmdbId, pick.ImdbId ?? m.ImdbId)) return;

        var group = items.Where(i => i.GroupKey == it.GroupKey && i.Status != Moved).ToList();
        await Run("Bilgiler alınıyor…", async () =>
        {
            await ApplyMatch(group, it.Candidates, pick, new MetadataService(settings), Library(), manual: true);
            Say($"{group.Count} dosya '{pick.Display}' ile eşleştirildi.");
        });
    }

    // ---------- 3. apply ----------

    private async void Apply_Click(object sender, RoutedEventArgs e)
    {
        var todo = items.Where(i => i.Include && i.Meta is not null && i.Status != Moved && i.Status != NotFound).ToList();
        if (todo.Count == 0) { Say("Uygulanacak eşleşmiş dosya yok."); return; }

        var lib = Path.GetFullPath(Library()).TrimEnd('\\') + '\\';
        // Targets were computed at match time; if the library box changed since, they point to the old place.
        foreach (var stale in todo.Where(i => !i.TargetPath.StartsWith(lib, StringComparison.OrdinalIgnoreCase)))
            stale.Status = "Kütüphane değişti, yeniden eşleştir";
        todo = todo.Where(i => i.TargetPath.StartsWith(lib, StringComparison.OrdinalIgnoreCase)).ToList();
        if (todo.Count == 0) { Say("Kütüphane yolu eşleştirmeden sonra değişmiş; önce yeniden Eşleştir."); Summary(); return; }

        var answer = await new MessageBox
        {
            Title = "Onay",
            Content = $"{todo.Count} dosya şu klasöre taşınacak:\n{lib}\n\nAfişler indirilecek; klasör ikonları, Bilgi.html sayfaları ve DVD/Blu-ray kapakları oluşturulacak." +
                      "Hiçbir dosyanın üzerine yazılmaz; yapılan her taşıma _arsivci_kayit.txt dosyasına yazılır.",
            PrimaryButtonText = "Taşı",
            CloseButtonText = "Vazgeç",
        }.ShowDialogAsync();
        if (answer != MessageBoxResult.Primary) return;

        await Run("Uygulanıyor…", async () =>
        {
            Directory.CreateDirectory(lib);
            var log = Path.Combine(lib, "_arsivci_kayit.txt");
            Organizer.StartRun(log);   // so Geri al knows which moves were this run
            var folders = new Dictionary<string, (Meta Meta, int? Season)>(StringComparer.OrdinalIgnoreCase);
            int done = 0, failed = 0;
            Step(0, todo.Count);

            foreach (var it in todo)
            {
                try
                {
                    it.Status = "Taşınıyor…";
                    await Organizer.Move(it, log);
                    it.Status = Moved;
                    var dir = Path.GetDirectoryName(it.TargetPath)!;
                    if (it.Kind == MediaKind.Series)
                    {
                        folders[dir] = (it.Meta!, it.Parsed.Season);
                        folders[Path.GetDirectoryName(dir)!] = (it.Meta!, null);
                    }
                    else folders[dir] = (it.Meta!, null);
                }
                catch (Exception ex)
                {
                    it.Status = "Hata: " + ex.Message;
                    failed++;
                }
                Log($"{it.FileName}  →  {(it.Status == Moved ? Path.GetDirectoryName(it.TargetPath) : it.Status)}", it.Status);
                Step(++done, todo.Count);
            }

            Say("Afişler ve ikonlar hazırlanıyor…");
            done = 0;
            Step(0, folders.Count);
            // Several folders at once: their downloads overlap. Everything here resumes on the UI thread,
            // so done/failed need no locking and Decorate can still draw its icon.
            var gate = new SemaphoreSlim(6);
            async Task DecorateOne(string dir, Meta meta, int? season)
            {
                await gate.WaitAsync();
                try { await Organizer.Decorate(dir, meta, season); }
                catch (Exception ex) { failed++; Log($"Hata: {Path.GetFileName(dir)} afiş/ikon hazırlanamadı: {ex.Message}"); }
                finally { gate.Release(); }
                Step(++done, folders.Count);
            }
            // Roots first: a season folder draws its icon from the logo.png its show's root folder downloads.
            await Task.WhenAll(folders.Where(f => f.Value.Season is null).Select(f => DecorateOne(f.Key, f.Value.Meta, null)));
            await Task.WhenAll(folders.Where(f => f.Value.Season is not null).Select(f => DecorateOne(f.Key, f.Value.Meta, f.Value.Season)));
            Organizer.RefreshShellIcons();
            Say(failed == 0 ? $"Bitti. {todo.Count} dosya düzenlendi." : $"Bitti, {failed} hata var; Durum sütununa bak.");
        });
    }

    // ---------- undo ----------

    private async void Undo_Click(object sender, RoutedEventArgs e)
    {
        var lib = Library();
        var log = Path.Combine(lib, "_arsivci_kayit.txt");
        if (Organizer.LastRun(log) is not var (id, moves)) { Say("Bu kütüphanede geri alınacak bir Uygula yok."); return; }
        // Count videos like Uygula does; their subtitles ride along
        var videos = moves.Count(m => Organizer.IsVideo(m.To));

        var answer = await new MessageBox
        {
            Title = "Geri al",
            Content = $"Son Uygula'da taşınan {videos} dosya (altyazılarıyla) eski yerine geri taşınacak.\n\n" +
                      "Eski yerinde aynı isimde dosya varsa o dosya olduğu yerde kalır. Boşalan klasörlerin afiş, ikon ve Bilgi.txt dosyaları silinir.",
            PrimaryButtonText = "Geri al",
            CloseButtonText = "Vazgeç",
        }.ShowDialogAsync();
        if (answer != MessageBoxResult.Primary) return;

        await Run("Geri alınıyor…", async () =>
        {
            var skipped = await Organizer.Undo(log, lib, id, moves);
            foreach (var s in skipped) Log($"⚠ Geri alınamadı (yerinde yok ya da eski yerde aynı isimde dosya var): {s}");
            Organizer.RefreshShellIcons();
            items.Clear();   // the list's paths and statuses describe the state before the undo
            Say(skipped.Count == 0
                ? $"Bitti. {videos} dosya geri taşındı."
                : $"Bitti, {videos - skipped.Count(Organizer.IsVideo)} dosya geri taşındı; {skipped.Count} dosya ⚠ atlandı, günlüğe bak.");
        });
    }

    // ---------- refresh ----------

    /// Redraws icons, Bilgi.html and the DVD and Blu-ray covers for folders Arşivci made earlier, e.g. after adding a Fanart.tv key.
    /// Folders from older versions get Bilgi.html in place of their Bilgi.txt. Videos stay put.
    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        if (settings.TmdbKey == "") { OpenSettings(); Say("İkonları yenilemek için TMDb anahtarı gerekli."); return; }
        var lib = Library();
        var folders = Directory.Exists(lib)
            ? Directory.EnumerateDirectories(lib).Where(d => Organizer.InfoFile(d) is not null).Order().ToList()
            : [];
        if (folders.Count == 0) { Say("Kütüphanede Arşivci'nin düzenlediği klasör yok."); return; }

        var answer = await new MessageBox
        {
            Title = "İkonları yenile",
            Content = $"{lib} içindeki {folders.Count} klasörün afişleri, ikonları, Bilgi.html sayfaları ve DVD/Blu-ray kapakları yeniden oluşturulacak.\n\n" +
                      "Videolara dokunulmaz. İndirilemeyen bir resim yerine eskisi kalır.",
            PrimaryButtonText = "Yenile",
            CloseButtonText = "Vazgeç",
        }.ShowDialogAsync();
        if (answer != MessageBoxResult.Primary) return;

        await Run("İkonlar yenileniyor…", async () =>
        {
            var svc = new MetadataService(settings);
            int done = 0, failed = 0;
            Step(0, folders.Count);
            // ponytail: one folder at a time; run them through a SemaphoreSlim like Apply if big libraries feel slow.
            foreach (var dir in folders)
            {
                var name = Path.GetFileName(dir);
                try
                {
                    if (Organizer.TmdbIdFrom(await File.ReadAllTextAsync(Organizer.InfoFile(dir)!)) is not var (kind, id))
                        Log($"⚠ {name}: bilgi dosyasında TMDb bağlantısı yok, atlandı.");
                    else if (await svc.Details(new Candidate(kind, "", "", null, id, null, 0, null, "")) is not { Title.Length: > 0 } meta)
                        Log($"⚠ {name}: TMDb bu kaydı vermedi, eski hâli kaldı.");   // never write a nameless page over a good one
                    else
                    {
                        await Organizer.Decorate(dir, meta, null, refresh: true);
                        foreach (var season in Directory.EnumerateDirectories(dir, "Season *"))
                            if (int.TryParse(Path.GetFileName(season)["Season ".Length..], out var n))
                                await Organizer.Decorate(season, meta, n, refresh: true);
                        Log($"{name}  →  yenilendi");
                    }
                }
                catch (Exception ex)
                {
                    failed++;
                    Log($"Hata: {name}: {ex.Message}");
                }
                Step(++done, folders.Count);
            }
            Organizer.RefreshShellIcons();
            Say(failed == 0 ? $"Bitti. {folders.Count} klasör yenilendi." + Warnings(svc) : $"Bitti, {failed} hata var; günlüğe bak.");
        });
    }

    // ---------- artwork choice ----------

    private void PrevArt_Click(object sender, RoutedEventArgs e) => CycleArt(back: true);
    private void NextArt_Click(object sender, RoutedEventArgs e) => CycleArt(back: false);

    /// Image lists are ordered best-first and both the preview and Apply take the first one that downloads,
    /// so rotating the list in place is the whole "choice": nothing else has to remember it.
    private async void CycleArt(bool back)
    {
        if (List.SelectedItem is not MediaItem { Meta: { } m } it) return;
        int? season = it.Kind == MediaKind.Series ? it.Parsed.Season : null;
        // Same order Decorate uses: season poster, else the film's backdrop, else the poster.
        var list = season is int n && m.SeasonPosters.GetValueOrDefault(n) is { Count: > 0 } sp ? sp
                 : season is null && m.Backdrops.Count > 0 ? m.Backdrops
                 : m.Posters;
        if (list.Count < 2) { Say("Bunun için başka resim yok."); return; }

        await Run("Resim değiştiriliyor…", async () =>
        {
            if (back) { list.Insert(0, list[^1]); list.RemoveAt(list.Count - 1); }
            else { list.Add(list[0]); list.RemoveAt(0); }
            var icon = await Organizer.PreviewIcon(m, season);
            foreach (var i in items.Where(i => i.Meta == m && (i.Kind == MediaKind.Series ? i.Parsed.Season : null) == season))
            {
                i.FolderIcon = icon;
                i.PostersChanged();
            }
            Say("İkon resmi değişti; Uygula bunu kullanacak.");
        });
    }

    // ---------- disc ----------

    private async void Disc_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog
        {
            Title = "Diske yazılacak klasörü seç (içindeki film klasörleri diskin kökü olacak)",
            InitialDirectory = Directory.Exists(Library()) ? Library() : null,
        };
        if (dlg.ShowDialog(this) != true) return;

        await Run("Disk hazırlanıyor…", async () =>
        {
            var result = await Task.Run(() => Organizer.PrepareDisc(dlg.FolderName));
            Say(result);
            await new MessageBox
            {
                Title = "Disk hazır",
                Content = result + "\n\nBu klasörün İÇİNDEKİLERİ diskin köküne yaz (klasörün kendisini değil). " +
                          "Disk takılınca Otomatik Kullan penceresinde oynatma seçeneği çıkar.",
                CloseButtonText = "Tamam",
            }.ShowDialogAsync();
        });
    }

    // ---------- settings ----------

    private void Settings_Click(object sender, RoutedEventArgs e) => OpenSettings();

    private void OpenSettings()
    {
        TmdbBox.Text = settings.TmdbKey;
        OmdbBox.Text = settings.OmdbKey;
        FanartBox.Text = settings.FanartKey;
        LanguageBox.SelectedValue = settings.Language;
        TmdbResult.Text = OmdbResult.Text = FanartResult.Text = "";
        SettingsOverlay.Visibility = Visibility.Visible;
    }

    private void SettingsSave_Click(object sender, RoutedEventArgs e)
    {
        settings.TmdbKey = TmdbBox.Text.Trim();
        settings.OmdbKey = OmdbBox.Text.Trim();
        settings.FanartKey = FanartBox.Text.Trim();
        settings.Language = LanguageBox.SelectedValue as string ?? "tr-TR";
        settings.Save();
        SettingsOverlay.Visibility = Visibility.Collapsed;
        Say("Ayarlar kaydedildi.");
    }

    private void SettingsCancel_Click(object sender, RoutedEventArgs e) => SettingsOverlay.Visibility = Visibility.Collapsed;

    /// Tries the key as typed, before Kaydet, and says under its box what came back.
    private async void TestKey_Click(object sender, RoutedEventArgs e)
    {
        var service = (string)((FrameworkElement)sender).Tag;
        var (box, result) = service switch { "TMDb" => (TmdbBox, TmdbResult), "OMDb" => (OmdbBox, OmdbResult), _ => (FanartBox, FanartResult) };
        ((UIElement)sender).IsEnabled = false;
        result.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
        result.Text = "Deneniyor…";
        try
        {
            result.Text = "✓ Çalışıyor: " + await MetadataService.Test(service, box.Text.Trim());
            result.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "SystemFillColorSuccessBrush");
        }
        catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            result.Text = "✗ " + (ex is TaskCanceledException ? "zaman aşımı, internet bağlantısını kontrol et" : ex.Message);
            result.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "SystemFillColorCriticalBrush");
        }
        finally { ((UIElement)sender).IsEnabled = true; }
    }

    private void Link_Navigate(object sender, RequestNavigateEventArgs e) =>
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });

    // ---------- helpers ----------

    private void BrowseSource_Click(object sender, RoutedEventArgs e) => Browse(SourceBox);
    private void BrowseLibrary_Click(object sender, RoutedEventArgs e) => Browse(LibraryBox);

    private void Browse(System.Windows.Controls.TextBox box)
    {
        var dlg = new OpenFolderDialog { InitialDirectory = Directory.Exists(box.Text) ? box.Text : null };
        if (dlg.ShowDialog(this) != true) return;
        box.Text = dlg.FolderName;
        SavePaths();
    }

    private string Library() => LibraryBox.Text.Trim() is { Length: > 0 } l ? l : SourceBox.Text.Trim();

    private void SavePaths()
    {
        settings.SourcePath = SourceBox.Text.Trim();
        settings.LibraryPath = LibraryBox.Text.Trim();
        settings.Save();
    }

    private bool HasKeys()
    {
        if (settings.TmdbKey != "" || settings.OmdbKey != "") return true;
        OpenSettings();
        Say("Önce en az bir API anahtarı gir.");
        return false;
    }

    private async Task Run(string label, Func<Task> work)
    {
        if (busy) return;
        busy = true;
        Commands.IsEnabled = DetailBody.IsEnabled = false;
        Progress.Visibility = Visibility.Visible;
        Progress.IsIndeterminate = true;
        Say(label);
        try { await work(); }
        catch (Exception ex) { Say("Hata: " + ex.Message); }
        finally
        {
            busy = false;
            Commands.IsEnabled = DetailBody.IsEnabled = true;
            Progress.Visibility = Visibility.Collapsed;
            Summary();
        }
    }

    private void Step(int done, int total)
    {
        Progress.IsIndeterminate = false;
        Progress.Maximum = Math.Max(total, 1);
        Progress.Value = done;
    }

    private void Say(string text)
    {
        StatusText.Text = text;
        Log(text);
    }

    /// Callable from any thread: matching runs on the thread pool. Queued, so workers never wait for the UI.
    private void Log(string text, string? status = null) => Dispatcher.InvokeAsync(() =>
    {
        var entry = new LogEntry(DateTime.Now.ToString("HH:mm:ss"), text, MediaItem.ToneOf(status ?? text));
        log.Add(entry);
        LogList.ScrollIntoView(entry);
    });

    private void LogToggle_Click(object sender, RoutedEventArgs e) =>
        LogPanel.Visibility = LogPanel.IsVisible ? Visibility.Collapsed : Visibility.Visible;

    private void View_Checked(object sender, RoutedEventArgs e)
    {
        if (List is null) return;   // fires while InitializeComponent is still building the window
        var grid = GridViewButton.IsChecked == true;
        Tiles.Visibility = grid ? Visibility.Visible : Visibility.Collapsed;
        List.Visibility = grid ? Visibility.Collapsed : Visibility.Visible;
    }

    // Both views show the same items; the detail pane and the search follow List.SelectedItem.
    private void Tiles_SelectionChanged(object sender, SelectionChangedEventArgs e) => List.SelectedItem = Tiles.SelectedItem;

    private void Summary()
    {
        EmptyHint.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (items.Count == 0) { SummaryText.Text = ""; return; }
        int Count(string s) => items.Count(i => i.Status == s);
        SummaryText.Text = $"{items.Count} dosya  ·  {Count(Matched) + Count(Organized)} eşleşti"
                         + (Count(Unsure) > 0 ? $"  ·  {Count(Unsure)} kontrol et" : "")
                         + $"  ·  {Count(NotFound)} bulunamadı  ·  {Count(Moved)} taşındı";
    }

    private void List_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var it = List.SelectedItem as MediaItem;
        Tiles.SelectedItem = it;
        DetailBody.Visibility = it is null ? Visibility.Collapsed : Visibility.Visible;
        DetailHint.Visibility = it is null ? Visibility.Visible : Visibility.Collapsed;
        if (it is not null) SearchBox.Text = it.Parsed.Year is null ? it.Parsed.Title : $"{it.Parsed.Title} {it.Parsed.Year}";
    }

    /// Wide: details on the right, poster above text. Narrow: details below the list, poster beside text.
    private void Responsive()
    {
        var w = ActualWidth;
        PathsGrid.Columns = w >= 900 ? 2 : 1;
        TargetColumn.Visibility = w >= 1050 ? Visibility.Visible : Visibility.Collapsed;

        var wide = w >= 1100;
        Grid.SetRow(Detail, wide ? 0 : 1);
        Grid.SetColumn(Detail, wide ? 1 : 0);
        Detail.Width = wide ? 380 : double.NaN;
        Detail.MaxHeight = wide ? double.PositiveInfinity : 330;
        Detail.Margin = wide ? new Thickness(12, 0, 0, 0) : new Thickness(0, 12, 0, 0);

        Grid.SetRow(DetailInfo, wide ? 1 : 0);
        Grid.SetColumn(DetailInfo, wide ? 0 : 1);
        Grid.SetColumnSpan(DetailInfo, wide ? 2 : 1);
        DetailPoster.Margin = wide ? new Thickness(0, 0, 0, 14) : new Thickness(0, 0, 16, 0);
        DetailPoster.VerticalAlignment = VerticalAlignment.Top;
    }
}
