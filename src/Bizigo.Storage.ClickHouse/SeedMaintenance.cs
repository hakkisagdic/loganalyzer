using System.Globalization;

namespace Bizigo.Storage.ClickHouse;

/// <summary>
/// Altın örnek yükleyicisinin (T39) kendi satırlarını sayması ve silmesi.
///
/// <para>
/// <b>Neden ayrı bir tip:</b> yükleyici iki kez koşturulduğunda veriyi ikiye
/// katlarsa ölçüm sessizce bozulur — hacim iki katına çıkar, "ilk-görülen"
/// oranı düşer ve hiçbir yerde hata görünmez. Bunu engellemenin yolu yazmadan
/// önce bakmak; bakmanın yolu da bir sorgu, ve ham sürücüye erişim yalnızca bu
/// derlemede olabiliyor (K17 / mimari testi).
/// </para>
///
/// <para>
/// <b>Her iki metot da <c>owner_group</c> ile sınırlı ve sınır zorunlu.</b>
/// Yükleyici kendi grubuna yazıyor; ClickHouse'ta duran başka veri (ör. bir
/// milyon satırlık kıyaslama kümesi) başka bir gruba ait ve bu yolun ona
/// dokunabileceği bir imza <b>yok</b>. "Tabloyu temizle" diye bir metot bilerek
/// açılmadı: var olsaydı bir gün bir bayrağın arkasından çağrılırdı.
/// </para>
/// </summary>
public sealed class SeedMaintenance(ClickHouseContext context)
{
    private readonly ClickHouseContext _context = context
        ?? throw new ArgumentNullException(nameof(context));

    public async Task<long> CountAsync(string ownerGroup, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerGroup);

        await using var connection = _context.CreateConnection();
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT count() FROM {_context.Options.EventsTable} WHERE owner_group = {Literal(ownerGroup)}";

        var scalar = await command.ExecuteScalarAsync(cancellationToken);
        return scalar is null or DBNull ? 0 : Convert.ToInt64(scalar, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Yalnızca verilen grubun satırlarını siler ve <b>mutasyonun bitmesini
    /// bekler</b> (<c>mutations_sync = 2</c>). Beklemezsek yükleyici hemen
    /// ardından yazar ve silme onun satırlarını da yakalayabilir — kimsenin
    /// aramayacağı bir yarış.
    /// </summary>
    public async Task DeleteAsync(string ownerGroup, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerGroup);

        await using var connection = _context.CreateConnection();
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText =
            $"ALTER TABLE {_context.Options.EventsTable} DELETE WHERE owner_group = {Literal(ownerGroup)} " +
            "SETTINGS mutations_sync = 2";

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// Kolon adı parametreleştirilemediği için değer elle tırnaklanıyor; tek
    /// kaçış gereken karakter tek tırnak ve ters bölü.
    /// </summary>
    private static string Literal(string value) =>
        "'" + value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("'", "\\'", StringComparison.Ordinal)
        + "'";
}
