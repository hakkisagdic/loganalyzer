using Bizigo.ControlPlane;
using Microsoft.EntityFrameworkCore;

namespace Bizigo.IntegrationTests;

/// <summary>
/// Config anlık görüntülerinin <b>gerçek</b> Postgres'e karşı sözleşmesi (T26).
///
/// <para>
/// Birim testleri fark algoritmasını ve gürültü elemesini kapsıyor; buradaki
/// soru başka: "bu connector'ın en son anlık görüntüsü" sorgusunun gerçek
/// şemada beklenen satırı getirdiği ve saklama kesiminin taban çizgisini
/// <b>silmediği</b>.
/// </para>
///
/// <para>
/// İkincisi sessiz bir tuzak: temizlik en son anlık görüntüyü de silerse bir
/// sonraki çekim taban çizgisi bulamaz, config'in tamamını "yeni" sayar ve
/// <c>change_events</c>'e devasa bir sahte değişiklik düşer — hem de tam olarak
/// kimsenin bakmadığı bir anda.
/// </para>
/// </summary>
[Collection(DevStackCollection.Name)]
public sealed class ChangeConfigSnapshotTests(DevStackFixture stack) : IAsyncLifetime
{
    private IDbContextFactory<ControlPlaneDbContext> _factory = null!;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _factory = new ControlPlaneFactory(stack.PostgresConnectionString);

        await using var db = await _factory.CreateDbContextAsync(Token);
        await db.Database.MigrateAsync(Token);
        await db.ChangeConfigSnapshots.ExecuteDeleteAsync(Token);
        await db.ChangeConnectors.ExecuteDeleteAsync(Token);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task En_son_anlik_goruntu_dogru_getiriliyor()
    {
        var connectorId = Guid.CreateVersion7();
        var other = Guid.CreateVersion7();
        var now = DateTimeOffset.UtcNow;

        await using var db = await _factory.CreateDbContextAsync(Token);

        db.ChangeConfigSnapshots.AddRange(
            Snapshot(connectorId, now.AddHours(-2), "eski"),
            Snapshot(connectorId, now.AddMinutes(-5), "guncel"),
            // Başka bir connector'ın daha yeni kaydı sonucu etkilememeli.
            Snapshot(other, now, "baska-connector"));

        await db.SaveChangesAsync(Token);

        var latest = await db.ChangeConfigSnapshots
            .AsNoTracking()
            .Where(s => s.ConnectorId == connectorId)
            .OrderByDescending(s => s.CapturedAt)
            .FirstAsync(Token);

        Assert.Equal("guncel", latest.Sha256);
    }

    [Fact]
    public async Task Saklama_temizligi_taban_cizgisini_silmiyor()
    {
        // Temizlik "kesimden eski olanı sil" derse, hiç değişmeyen bir cihazın
        // TEK anlık görüntüsü de silinir ve bir sonraki çekim config'in
        // tamamını sahte bir değişiklik olarak raporlar.
        var connectorId = Guid.CreateVersion7();
        var now = DateTimeOffset.UtcNow;
        var cutoff = now.AddDays(-90);

        await using var db = await _factory.CreateDbContextAsync(Token);

        db.ChangeConfigSnapshots.AddRange(
            Snapshot(connectorId, now.AddDays(-200), "cok-eski"),
            Snapshot(connectorId, now.AddDays(-150), "eski"),
            // Cihaz 120 gündür değişmedi: en son kayıt da kesimden eski.
            Snapshot(connectorId, now.AddDays(-120), "taban-cizgisi"));

        await db.SaveChangesAsync(Token);

        var keep = await db.ChangeConfigSnapshots
            .Where(s => s.ConnectorId == connectorId)
            .OrderByDescending(s => s.CapturedAt)
            .Select(s => s.Id)
            .FirstAsync(Token);

        var deleted = await db.ChangeConfigSnapshots
            .Where(s => s.CapturedAt < cutoff && s.Id != keep)
            .ExecuteDeleteAsync(Token);

        Assert.Equal(2, deleted);

        var survivor = await db.ChangeConfigSnapshots
            .AsNoTracking()
            .SingleAsync(s => s.ConnectorId == connectorId, Token);

        Assert.Equal("taban-cizgisi", survivor.Sha256);
    }

    private static ChangeConfigSnapshotEntity Snapshot(
        Guid connectorId,
        DateTimeOffset capturedAt,
        string digest) => new()
        {
            ConnectorId = connectorId,
            CapturedAt = capturedAt,
            Sha256 = digest,
            // Şifreli gövdenin taklidi; bu test şifrelemeyi değil şemayı sınıyor.
            Body = "c2FodGUtc2lmcmVsaS1nb3ZkZQ==",
            LineCount = 42,
        };
}
