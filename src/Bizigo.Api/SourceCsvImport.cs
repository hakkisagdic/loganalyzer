using System.Globalization;
using Bizigo.Contracts;

namespace Bizigo.Api;

/// <summary>
/// CSV envanter yüklemesinin <b>saf</b> kısmı: ayrıştırma, doğrulama ve kapsam
/// kontrolü.
///
/// <para>
/// Veritabanından ayrılmasının sebebi ölçülebilirlik: "ya hep ya hiç" kuralı ve
/// kapsam reddi bu üründe kapsam hatasına dönüşebilecek bir davranış, dolayısıyla
/// konteyner gerektirmeden sınanabilmeli. F1'in dersi buydu — beş hatanın dördü
/// baştan beri dosyada okunabilir birer sözleşme ihlaliydi.
/// </para>
/// </summary>
public static class SourceCsvImport
{
    private static readonly string[] RequiredColumns = ["source_id", "owner_group"];

    /// <param name="Rows">Yalnızca <see cref="Errors"/> boşken anlamlı.</param>
    /// <param name="Errors">Satır numarasıyla birlikte; kullanıcı hangisinin reddedildiğini görüyor.</param>
    public sealed record Result(IReadOnlyList<SourceUpsertRequest> Rows, IReadOnlyList<string> Errors)
    {
        public bool Ok => Errors.Count == 0;
    }

    /// <summary>
    /// CSV'yi satırlara çeviriyor.
    ///
    /// <para>
    /// <b>Ya hep ya hiç:</b> tek bir satır bile reddedilirse hiçbiri yazılmıyor.
    /// Yarı yüklenmiş bir envanter, hangi cihazın hangi gruba düştüğünü belirsiz
    /// bırakır ve o belirsizlik doğrudan bir kapsam hatasıdır — üstelik fark
    /// edilmesi için kimsenin bakmadığı bir yere bakmak gerekir.
    /// </para>
    ///
    /// <para>
    /// <b>Kapsam da burada zorlanıyor.</b> Bugün bu uç yalnızca <c>admin</c>
    /// rolüne açık ve admin sınırsız kapsamlı, yani kontrol pratikte bir no-op.
    /// Yine de var: rol tablosu bir gün grup yöneticisi tanırsa, kontrol
    /// olmadığında o kullanıcı başka bir ekibin cihazını kendi grubuna
    /// taşıyabilirdi ve bu, o ekibin verisini görmek demek olurdu.
    /// </para>
    /// </summary>
    public static Result Parse(string content, AccessScope scope)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(scope);

        var lines = content
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(static l => l.TrimEnd('\r'))
            .Where(static l => l.Length > 0 && !l.StartsWith('#'))
            .ToArray();

        if (lines.Length < 2)
        {
            return new Result([], ["CSV en az bir başlık ve bir veri satırı içermeli."]);
        }

        var header = lines[0].Split(',').Select(static h => h.Trim()).ToArray();

        foreach (var column in RequiredColumns)
        {
            if (!header.Contains(column, StringComparer.Ordinal))
            {
                return new Result([], [$"Zorunlu sütun eksik: '{column}'."]);
            }
        }

        var errors = new List<string>();
        var rows = new List<SourceUpsertRequest>();
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);

        for (var i = 1; i < lines.Length; i++)
        {
            // Kullanıcı dosyayı bir editörde açıyor: numara 1'den başlamalı ve
            // başlık satırını da saymalı.
            var lineNumber = i + 1;
            var cells = lines[i].Split(',');

            if (cells.Length != header.Length)
            {
                errors.Add(string.Create(
                    CultureInfo.InvariantCulture,
                    $"satır {lineNumber}: {header.Length} sütun bekleniyordu, {cells.Length} bulundu"));
                continue;
            }

            string Cell(string name)
            {
                var index = Array.IndexOf(header, name);
                return index < 0 ? string.Empty : cells[index].Trim();
            }

            var sourceId = Cell("source_id");
            var ownerGroup = Cell("owner_group");

            if (sourceId.Length == 0 || ownerGroup.Length == 0)
            {
                errors.Add($"satır {lineNumber}: source_id ve owner_group boş olamaz");
                continue;
            }

            // Aynı dosyada tekrarlanan kaynak: son satır sessizce kazanırdı ve
            // kullanıcı hangi grubun geçerli olduğunu dosyaya bakarak anlayamazdı.
            if (seen.TryGetValue(sourceId, out var first))
            {
                errors.Add($"satır {lineNumber}: '{sourceId}' zaten {first}. satırda var");
                continue;
            }

            seen[sourceId] = lineNumber;

            if (!scope.Allows(ownerGroup))
            {
                errors.Add(
                    $"satır {lineNumber}: '{ownerGroup}' grubuna yazma yetkiniz yok");
                continue;
            }

            rows.Add(new SourceUpsertRequest
            {
                SourceId = sourceId,
                OwnerGroup = ownerGroup,
                PeerAddress = Cell("peer_address"),
                Hostname = Cell("hostname"),
                Vendor = Cell("vendor"),
                Product = Cell("product"),
                ParserId = Cell("parser_id"),
                Encoding = Cell("encoding") is { Length: > 0 } encoding ? encoding : "auto",
                SourceClass = Cell("source_class") is { Length: > 0 } sourceClass ? sourceClass : "default",
            });
        }

        // Hata varsa satırların hiçbiri dönmüyor: çağıranın "yarısını yazayım"
        // diyebileceği bir ara hâl bırakmıyoruz.
        return errors.Count > 0 ? new Result([], errors) : new Result(rows, []);
    }
}
