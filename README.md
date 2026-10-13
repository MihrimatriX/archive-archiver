# Arşivci

**Türkçe** · [English](README.en.md)

**Dağınık film ve dizi dosyalarını afişli, düzenli bir arşive çeviren Windows uygulaması.**

`Deadpool.2016.1080p.BluRay.x264-SPARKS.mkv` gibi dosyaları tanır, bilgilerini TMDb, OMDb ve Fanart.tv'den bulur, `Deadpool (2016)` klasörüne taşır, klasöre filmin görselinden bir ikon yapar ve tür, oyuncular, puanlar, ses/altyazı dilleri gibi bilgileri `Bilgi.txt`'ye yazar.

**[⬇ Son sürümü indir](https://github.com/MihrimatriX/archive-archiver/releases/latest)**: kurulum yok, tek `.exe`, .NET kurmana gerek yok.

![Arşivci: eşleştirmeden sonra her dosya, oluşturulacak klasör ikonuyla görünür](docs/screenshot.png)

## Önce / sonra

```
E:\İndirilenler\
├── Deadpool.2016.1080p.BluRay.x264-SPARKS.mkv
├── Deadpool.2016.1080p.BluRay.x264-SPARKS.tr.srt
├── Breaking.Bad.S01E01.720p.HDTV.mkv
└── Breaking.Bad.S01E02.720p.HDTV.mkv
```

```
E:\Arşiv\
├── _arsivci_kayit.txt
├── Deadpool (2016)\
│   ├── Deadpool.2016.1080p.BluRay.x264-SPARKS.mkv
│   ├── Deadpool.2016.1080p.BluRay.x264-SPARKS.tr.srt
│   └── Bilgi.txt
└── Breaking Bad (2008)\
    ├── Bilgi.txt
    └── Season 01\
        ├── Breaking.Bad.S01E01.720p.HDTV.mkv
        ├── Breaking.Bad.S01E02.720p.HDTV.mkv
        └── Bilgi.txt
```

Her klasörde ayrıca gizli `folder.ico`, `folder.jpg`, `fanart.jpg`, `logo.png`, `desktop.ini` dosyaları bulunur. Bunlar Gezgin'de klasörün afişli ikonla görünmesini ve üzerine gelince konu özetinin çıkmasını sağlar.

## Özellikler

- **Dosya adını anlar.** `1080p`, `BluRay`, `x265`, `WEB-DL`, `TR Dublaj` gibi etiketleri atar; yılı ve `S01E02` / `1x02` bölüm bilgisini çıkarır. Dosyanın adı sadece `S01E01.mkv` ise dizi adını üst klasörden (`Breaking Bad\Season 1\`) alır.
- **Doğru eşleşmeyi seçer.** Başlık benzerliğine, yıla ve popülerliğe bakar: `12 Monkeys` dosyası 2026 yapımı bir kısa filmle değil, 1995 filmiyle eşleşir. Yanlış eşleşen bir dosyayı sağ panelden arayıp düzeltebilirsin; düzeltme dizinin tüm bölümlerine uygulanır.
- **Önce önizleme.** *Uygula*'ya basana kadar hiçbir dosya taşınmaz.
  - **Izgara** görünümünde her dosya, oluşturulacak klasör ikonuyla görünür.
  - **Liste** görünümünde her dosyanın tam hedef yolu görünür.
- **Günlük.** Alttaki panel her adımı ve dosya başına sonucu (neyle eşleşti, nereye taşındı, hangi hata çıktı) renkli olarak listeler. Durum çubuğundaki *Günlük* düğmesiyle gizlenip açılır.
- **Dosyalarına zarar vermez.**
  - Hiçbir dosyanın üzerine yazmaz.
  - Her taşıma `_arsivci_kayit.txt` dosyasına kaydedilir.
  - Farklı sürücüye taşırken önce kopyalar; işlem yarıda kalırsa kaynak yerinde durur.
- **Dosya adlarına dokunmaz.** Uygulama yalnızca klasörü belirler. Videoyla aynı adı taşıyan altyazılar (`.srt`, `.ass`, `.sub/.idx`, `.vtt`…) videoyla birlikte taşınır.
- **Okunur klasör adları.** Orijinal ad Latin alfabesindeyse klasör onu kullanır (`Le Fabuleux Destin d'Amélie Poulain`). Değilse İngilizce adı kullanır, böylece Korece ya da Hintçe bir başlık da okunabilir kalır.
- **Afişli klasör ikonu.** Ön yüzde filmin geniş görseli var. Logo sağa yaslı durur ve klasörün üst kenarından dışarı taşar; logo yoksa filmin adı yazılır. Dizi sezonlarında bir de `S01` rozeti bulunur.
- **Bilgi.txt** şunları içerir:
  - orijinal ad, tür, süre, yönetmen, oyuncular
  - IMDb / Rotten Tomatoes / Metacritic / TMDb puanları, ödüller, konu
  - her video için çözünürlük, codec, HDR, ses dilleri ve kanalları, gömülü ve harici altyazılar (MediaInfo ile)
- **Disk hazırla.** Bir klasörü CD/DVD/Blu-ray'e hazırlar (ayrıntılar aşağıda).

## Kullanım

1. **İndir ve çalıştır.** Exe imzasız olduğu için Windows "bilgisayarınızı korudu" uyarısı verebilir: **Ek bilgi → Yine de çalıştır**.
2. **Ayarlar**'dan API anahtarlarını gir (aşağıdaki tabloya bak).
3. **Kaynak** olarak dağınık dosyaların olduğu klasörü, **Kütüphane** olarak düzenli arşivin gideceği klasörü seç. Kütüphane boş bırakılırsa kaynak klasörün içi düzenlenir.
4. **1 Tara**: videoları bulur. Gizli/sistem dosyaları ve 300 MB'tan küçük `sample` dosyaları atlanır.
5. **2 Eşleştir**: bilgileri getirir ve kartlarda klasör ikonlarını gösterir.
   - Yanlış eşleşen bir dosyayı seçip sağ panelden düzelt.
   - Taşınmasını istemediklerinin işaretini kaldır.
   - Hedef yollarını görmek için sağ üstten **Liste** görünümüne geçebilirsin.
6. **3 Uygula**: onay verdiğinde dosyaları taşır, afişleri indirir, ikonları ve `Bilgi.txt`'yi oluşturur.

## API anahtarları

Üç anahtar da ücretsiz. En az **TMDb** ya da **OMDb** gerekli; en iyi sonucu üçü birlikte verir.

| Servis | Ne getirir | Anahtar |
|---|---|---|
| [TMDb](https://www.themoviedb.org/settings/api) | Arama, Türkçe ad ve konu, afiş, arka plan, logo, oyuncular | v3 API anahtarı ya da v4 okuma token'ı, ikisi de çalışır |
| [OMDb](https://www.omdbapi.com/apikey.aspx) | IMDb, Rotten Tomatoes, Metacritic puanları, ödüller | Anahtar e-postayla gelir; e-postadaki etkinleştirme linkine tıklamadan çalışmaz |
| [Fanart.tv](https://fanart.tv/get-an-api-key/) | HD logo, yüksek çözünürlüklü afiş, sezon afişleri, disk görseli | İsteğe bağlı |

**Bilgi dili** *Türkçe* seçiliyken Türkçe konu özeti olmayan yapımlarda İngilizce özet kullanılır. Ayarlar `%APPDATA%\ArchiveArchiver\settings.json` dosyasında düz metin olarak saklanır.

## Disk hazırlama

**Disk hazırla** ile bir klasör seçtiğinde Arşivci şunları yapar:

- `autorun.inf` ve disk ikonunu ekler.
- Klasörde tek video varsa disk takılınca o video oynatılır. Birden fazla varsa afişlerden oluşan bir menü (`menu.hta`) açılır.
- Toplam boyuta göre hangi diske sığdığını söyler: CD, DVD, DVD DL, BD-25 ya da BD-50.

Diske klasörün **kendisini değil, içindekileri** kök dizine yaz.

## Notlar

- **Klasörler "salt okunur" görünüyor.** Windows özel klasör ikonunu ancak bu işaretle gösterir. İçindeki dosyalar etkilenmez.
- **Geri almak istersen:** `_arsivci_kayit.txt` her satırda `tarih ⇥ nereden ⇥ nereye` tutar.
- **Aynı anda tek pencere açılır.** İki pencere aynı ayarları paylaşıp aynı dosyaları taşımaya çalışabilirdi.

## Kaynaktan derleme

[.NET 10 SDK](https://dotnet.microsoft.com/download) gerekir.

```powershell
dotnet publish -c Release -o publish
```

Komut `publish\ArchiveArchiver.exe` dosyasını üretir. Bu tek dosyanın içinde .NET, WPF ve MediaInfo vardır.

### Kendi kendini test

```powershell
# ad ayrıştırma ve eşleştirme testleri (çıkış kodu 0 = geçti)
(Start-Process .\publish\ArchiveArchiver.exe '--selftest' -Wait -PassThru).ExitCode

# + taşıma, ikon, Bilgi.txt ve disk testleri; geçici bir klasörde çalışır, internet kullanmaz
(Start-Process .\publish\ArchiveArchiver.exe '--selftest','C:\yol\ornek.mp4' -Wait -PassThru).ExitCode
```

### Sürüm çıkarma

```powershell
git tag v1.2.0
git push origin v1.2.0
```

[Release workflow'u](.github/workflows/release.yml) exe'yi derler, self-test'i çalıştırır, release'i oluşturur ve `Arsivci-1.2.0-win-x64.exe` dosyasını ekler. Her `main` push'unda da aynı derleme ve test çalışır; exe, Actions sayfasında artifact olarak durur.

## Kullanılanlar

[WPF-UI](https://github.com/lepoco/wpfui) · [MediaInfo](https://mediaarea.net/MediaInfo) · [TMDb](https://www.themoviedb.org) · [OMDb](https://www.omdbapi.com) · [Fanart.tv](https://fanart.tv)

<sub>This product uses the TMDB API but is not endorsed or certified by TMDB.</sub>
