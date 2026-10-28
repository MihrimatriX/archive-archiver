# Arşivci

[Türkçe](README.md) · **English**

**A Windows app that turns scattered movie and TV files into a tidy archive with poster folder icons.**

Arşivci ("archivist") recognizes files like `Deadpool.2016.1080p.BluRay.x264-SPARKS.mkv` and looks them up on TMDb, OMDb and Fanart.tv. It then:

- moves the file into a `Deadpool (2016)` folder
- turns the film's artwork into an icon for that folder
- writes genre, cast, ratings, audio and subtitle languages and more into `Bilgi.html` ("info"), a one-page, Netflix-style page
- draws printable inserts for the clear sleeve of a DVD and a Blu-ray case (`kapak-dvd.jpg`, `kapak-bluray.jpg`; "kapak" means cover)

**[⬇ Download the latest release](https://github.com/MihrimatriX/archive-archiver/releases/latest)**: no installer, a single `.exe`, no .NET install needed. · **[Project page](https://mihrimatrix.github.io/archive-archiver/)**

> The app's interface is in Turkish. This README names its buttons in Turkish, with the English meaning in brackets. Movie and show details can be fetched in Turkish or English.

![Arşivci: after matching, every file is shown with the folder icon it will get](docs/screenshot.png)

## Before / after

```
E:\Downloads\
├── Deadpool.2016.1080p.BluRay.x264-SPARKS.mkv
├── Deadpool.2016.1080p.BluRay.x264-SPARKS.tr.srt
├── Breaking.Bad.S01E01.720p.HDTV.mkv
└── Breaking.Bad.S01E02.720p.HDTV.mkv
```

```
E:\Archive\
├── _arsivci_kayit.txt
├── Deadpool (2016)\
│   ├── Deadpool.2016.1080p.BluRay.x264-SPARKS.mkv
│   ├── Deadpool.2016.1080p.BluRay.x264-SPARKS.tr.srt
│   ├── Bilgi.html
│   ├── kapak-dvd.jpg
│   └── kapak-bluray.jpg
└── Breaking Bad (2008)\
    ├── Bilgi.html
    ├── kapak-dvd.jpg
    ├── kapak-bluray.jpg
    └── Season 01\
        ├── Breaking.Bad.S01E01.720p.HDTV.mkv
        ├── Breaking.Bad.S01E02.720p.HDTV.mkv
        ├── Bilgi.html
        ├── kapak-dvd.jpg
        └── kapak-bluray.jpg
```

Each folder also gets hidden `folder.ico`, `folder.jpg`, `fanart.jpg`, `logo.png`, `disc.png` and `desktop.ini` files. They make Explorer show the folder with its poster icon, and show the plot summary when you hover over it.

## Features

- **Understands release names.** Strips tags like `1080p`, `BluRay`, `x265`, `WEB-DL` and `TR Dublaj`, and extracts the year and the `S01E02` / `1x02` episode number. If a file is just `S01E01.mkv`, the show name comes from the parent folder (`Breaking Bad\Season 1\`).
- **Picks the right match.** Weighs title similarity, year and popularity, so a `12 Monkeys` file matches the 1995 film rather than a 2026 short. If a match is wrong, search for the right one in the side panel; for a show, the fix applies to every episode.
- **Says when it isn't sure.** If the year disagrees (`Dune 1984` → the 2021 film) or the title isn't an exact fit, the file is flagged yellow **Kontrol et** (Check), so you only look at those.
- **Preview first.** Nothing moves until you press *Uygula* (Apply).
  - The **grid** view shows each file with the folder icon it will get.
  - The **list** view shows each file's full target path.
- **Log.** The bottom panel lists every step and the result for each file (what it matched, where it moved, which error occurred), color-coded. The *Günlük* (Log) button in the status bar shows or hides it.
- **Safe with your files.**
  - Never overwrites a file.
  - Logs every move to `_arsivci_kayit.txt`.
  - Moves across drives copy first, so the source survives an interrupted move.
- **Keeps file names.** Only the folder is decided; the video keeps its own name. Subtitles named after the video (`.srt`, `.ass`, `.sub/.idx`, `.vtt`…) move with it.
- **Readable folder names.** The original title is used when it is in Latin script (`Le Fabuleux Destin d'Amélie Poulain`); otherwise the English title, so a Korean or Hindi title stays readable.
- **3D-looking poster folder icons.** The folder's front flap falls open toward you: paper peeks out of it, light comes from above, the flap has thickness and casts a shadow. The film's wide backdrop is on the front flap. The logo sits right-aligned and sticks out over the folder's top edge; without a logo, the title is written instead. Season folders also get an `S01` badge. Don't like the picture? Select the file and use the **Klasör ikonu** (folder icon) arrows in the right panel to pick another image; *Uygula* (Apply) uses that one.
- **Bilgi.html** is a one-page, Netflix-style page that opens in your browser:
  - on top, the film's backdrop, logo, year, runtime, genre, `1080p` / `HDR10` / `5.1` badges, tagline and plot
  - the poster, IMDb / Rotten Tomatoes / Metacritic / TMDb rating cards, director, cast, original title, awards, IMDb and TMDb links
  - per video: resolution, codec, HDR, audio languages and channels, embedded and external subtitles (via MediaInfo)
  - a spinning disc image and previews of the DVD and Blu-ray covers

  Its images are the files in the folder, so it works offline and from a burned disc too.
- **DVD and Blu-ray covers.** The paper insert for the case's clear sleeve, at 300 dpi; print at actual size and cut:
  - `kapak-dvd.jpg`: DVD case, 273 × 183 mm, with a black "DVD VIDEO" band
  - `kapak-bluray.jpg`: Blu-ray case, 269 × 148 mm, with a blue "BLU-RAY DISC" band

  The format band runs across the top of the front, and the format's name also heads the spine (plain text, not the licensed logos). The front has the high-resolution poster, the spine the logo and year (season for shows), the back a backdrop, tagline, plot, three stills, credits, technical badges and ratings. No free service offers scanned covers, so it is drawn from the artwork and details TMDb, OMDb and Fanart.tv provide.
- **Disc prep.** Prepares a folder for burning to CD/DVD/Blu-ray (details below).

## Usage

1. **Download and run.** The exe is unsigned, so Windows may show "Windows protected your PC": click **More info → Run anyway**.
2. Enter your API keys under **Ayarlar** (Settings). See the table below. **Test et** (Test) next to each key fetches a sample movie with it and says right below whether it works, even before you save.
3. Choose your folders:
   - **Kaynak** (Source): the folder with the scattered files.
   - **Kütüphane** (Library): where the organized archive should go. If left empty, the source folder is organized in place.
4. **1 Tara** (Scan) finds the videos. Hidden/system files and `sample` files under 300 MB are skipped.
5. **2 Eşleştir** (Match) fetches the details and shows the folder icons on the cards.
   - Select a wrongly matched file and fix it in the side panel.
   - Untick the files you don't want moved.
   - Switch to the **list** view (top right) to see the target paths.
6. **3 Uygula** (Apply) asks for confirmation, then moves the files, downloads the artwork and creates the icons, `Bilgi.html` pages and DVD/Blu-ray covers.
7. **Geri al** (Undo) reverts the library's last Apply: files go back and emptied folders are removed. A file whose old place is taken stays where it is.

**İkonları yenile** (Refresh icons) rebuilds the artwork, icons, `Bilgi.html` pages and DVD/Blu-ray covers of folders already organized in the library. A `Bilgi.txt` or `kapak.jpg` written by an older version is replaced by the new files along the way.

## API keys

All three keys are free. At least **TMDb** or **OMDb** is required; all three together give the best results.

| Service | Provides | Key |
|---|---|---|
| [TMDb](https://www.themoviedb.org/settings/api) | Search, localized title and plot, poster, backdrop, logo, cast | v3 API key or v4 read access token; both work |
| [OMDb](https://www.omdbapi.com/apikey.aspx) | IMDb, Rotten Tomatoes, Metacritic ratings, awards | Sent by email; it only works after you click the activation link in that email |
| [Fanart.tv](https://fanart.tv/get-an-api-key/) | HD logos, high-resolution posters, season posters, disc art | Optional |

With **Bilgi dili** (Info language) set to *Türkçe*, titles without a Turkish plot summary fall back to the English one. Settings are stored as plain text in `%APPDATA%\ArchiveArchiver\settings.json`.

## Disc prep

When you pick a folder with **Disk hazırla** (Prepare disc), Arşivci:

- adds `autorun.inf` and a disc icon
- if the folder has a single video, sets the disc to play it when inserted; with more than one, a poster menu (`menu.hta`) opens instead
- tells you which disc the total size fits on: CD, DVD, DVD DL, BD-25 or BD-50

Burn the folder's **contents**, not the folder itself, to the root of the disc.

## Notes

- **Folders show as "read-only".** Windows only shows a custom folder icon when this flag is set. The files inside are not affected.
- **Log file:** each line of `_arsivci_kayit.txt` holds `date ⇥ from ⇥ to`; *Geri al* reads it.
- **Only one window at a time.** Two windows could share the settings and try to move the same files.

## Building from source

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```powershell
dotnet publish -c Release -o publish
```

This produces `publish\ArchiveArchiver.exe`, a single file with .NET, WPF and MediaInfo inside.

### Self-test

```powershell
# name parsing and matching tests (exit code 0 = passed)
(Start-Process .\publish\ArchiveArchiver.exe '--selftest' -Wait -PassThru).ExitCode

# + move, icon, Bilgi.html, DVD/Blu-ray cover and disc tests; runs in a temp folder, no network
(Start-Process .\publish\ArchiveArchiver.exe '--selftest','C:\path\sample.mp4' -Wait -PassThru).ExitCode
```

### Sandbox mode

```powershell
.\publish\ArchiveArchiver.exe --deneme
```

Try the whole window without touching your real files. It builds a fake downloads folder and an empty library under `%TEMP%\arsivci-deneme` (fresh on every start) and uses its own `settings.json`; your API keys are only read from the real settings. Scan, Match, Apply and Undo are all safe here.

### Releasing

```powershell
git tag v1.2.0
git push origin v1.2.0
```

The [release workflow](.github/workflows/release.yml) builds the exe, runs the self-test, creates the release and attaches `Arsivci-1.2.0-win-x64.exe`. Every push to `main` runs the same build and test; the exe is kept as an artifact on the Actions page.

## Built with

[WPF-UI](https://github.com/lepoco/wpfui) · [MediaInfo](https://mediaarea.net/MediaInfo) · [TMDb](https://www.themoviedb.org) · [OMDb](https://www.omdbapi.com) · [Fanart.tv](https://fanart.tv)

<sub>This product uses the TMDB API but is not endorsed or certified by TMDB.</sub>
