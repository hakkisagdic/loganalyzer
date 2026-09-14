using System.Globalization;
using System.Text.RegularExpressions;

namespace Bizigo.Commands.Seeding;

/// <summary>
/// <c>events</c> tablosunun saklama süresini <b>şema dosyasından</b> okur.
///
/// <para>
/// <b>Neden sabit yazılmadı:</b> süresi dolmuş bir satır yazıldığında ClickHouse
/// hata vermiyor — yazımı kabul ediyor, satır sayısını dönüyor, sonra satırı
/// siliyor. Yani yükleyici "yazdım" diyor ve tabloda hiçbir şey yok. Bu hata
/// sınıfına karşı tek savunma <b>yazmadan önce sormak</b>, ve sorunun cevabı
/// <c>TTL … INTERVAL N DAY</c> satırında. C# tarafına <c>90</c> yazsaydık şema
/// değiştiği gün ikisi sessizce ayrışır ve savunma kendisi yanlış cevabı
/// verirdi.
/// </para>
///
/// <para>
/// Bulunamazsa <b>istisna atıyor</b>: "TTL yok" ile "TTL'i okuyamadım" aynı
/// şeye inseydi, okuyamama hâli "sınırsız saklama" diye okunur ve kapı hiç
/// kapanmazdı.
/// </para>
/// </summary>
public static class EventRetention
{
    private static readonly Regex Ttl = new(
        @"TTL\s+toDateTime\(\s*ts\s*\)\s*\+\s*INTERVAL\s+(?<days>\d+)\s+DAY",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.ExplicitCapture);

    public static TimeSpan Read(string migrationsDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(migrationsDirectory);

        if (!Directory.Exists(migrationsDirectory))
        {
            throw new DirectoryNotFoundException($"Göç dizini yok: {migrationsDirectory}");
        }

        var found = new List<(string File, int Days)>();

        foreach (var file in Directory
                     .EnumerateFiles(migrationsDirectory, "*.sql")
                     .OrderBy(static path => path, StringComparer.Ordinal))
        {
            foreach (Match match in Ttl.Matches(File.ReadAllText(file)))
            {
                found.Add((
                    Path.GetFileName(file),
                    int.Parse(match.Groups["days"].Value, CultureInfo.InvariantCulture)));
            }
        }

        if (found.Count == 0)
        {
            throw new InvalidOperationException(
                $"{migrationsDirectory} altında `events` için `TTL … INTERVAL N DAY` bulunamadı. " +
                "Sessizce 'sınırsız saklama' varsaymak, süresi dolmuş satırların " +
                "yazıldığı sanılmasına yol açardı — ClickHouse onları hata vermeden siliyor.");
        }

        // Göçler sırayla uygulanıyor; TTL birden çok yerde tanımlanmışsa
        // geçerli olan SONUNCUSU. Hangisinin geçerli olduğunu tahmin etmek
        // yerine dosya sırasına güveniyoruz ve kaçının bulunduğunu söyleyecek
        // bilgiyi çağırana bırakmıyoruz: son dosya kazanır, çünkü göç sırası
        // budur.
        return TimeSpan.FromDays(found[^1].Days);
    }
}
