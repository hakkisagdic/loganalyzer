using Microsoft.EntityFrameworkCore;

namespace Bizigo.ControlPlane;

/// <param name="Claimed">
/// <see langword="true"/> ise talep bu çağrıya ait; değişiklik olayı yazılmalı.
/// <see langword="false"/> ise teslimat daha önce işlendi.
/// </param>
/// <param name="ChangeId">
/// Talebi kazanan çağrının ürettiği değişiklik kimliği. Mükerrer teslimatta
/// <b>ilk</b> kaydın kimliği dönüyor — sağlayıcı ikinci kez sorduğunda aynı
/// cevabı alıyor.
/// </param>
public readonly record struct DeliveryClaim(bool Claimed, Guid ChangeId);

/// <summary>
/// Webhook idempotans anahtarı (T24 kabul kriteri: aynı webhook iki kez gelirse
/// ikinci kayıt oluşmaz).
///
/// <para>
/// <b>Neden Postgres:</b> <c>change_events</c> düz bir ClickHouse
/// <c>MergeTree</c>'si ve tekillik garantisi vermiyor. "Bu teslimat daha önce
/// geldi mi" sorusu bir benzersizlik kısıtı istiyor; onu veren tek yer kontrol
/// düzlemi. Uygulama katmanında "önce SELECT, sonra INSERT" yapmak iki
/// eşzamanlı teslimatın ikisini de geçirirdi — kısıt yarışı veritabanında
/// kesiyor.
/// </para>
///
/// <para>
/// <b>Sıra da karar:</b> talep önce yazılıyor, değişiklik olayı sonra. Ters
/// sırada yarışın iki tarafı da ClickHouse'a satır düşürür ve kısıt geç kalırdı.
/// Bedeli, yazma başarısız olursa talebin geri alınması gerektiği —
/// <see cref="ReleaseAsync"/> onun için var.
/// </para>
/// </summary>
public sealed class ChangeWebhookDeliveryLog(IDbContextFactory<ControlPlaneDbContext> factory)
{
    public async Task<DeliveryClaim> ClaimAsync(
        ChangeWebhookDeliveryEntity delivery,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(delivery);

        await using var db = await factory.CreateDbContextAsync(cancellationToken);

        // Hızlı yol: mükerrer teslimat olağan (sağlayıcılar yeniden deniyor),
        // ve bu okuma çoğu durumda kısıt ihlalinin maliyetinden ucuz.
        var existing = await Find(db, delivery.DeliveryKey, cancellationToken);

        if (existing is not null)
        {
            return new DeliveryClaim(false, existing.ChangeId);
        }

        db.ChangeWebhookDeliveries.Add(delivery);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return new DeliveryClaim(true, delivery.ChangeId);
        }
        catch (DbUpdateException)
        {
            // Yarışı kaybettik. Kazananın kimliğini okuyup aynı cevabı veriyoruz.
            // Satır gerçekten yoksa sorun benzersizlik değil; hata yeniden
            // fırlıyor — `DbUpdateException`'ı topluca "mükerrer" saymak, gerçek
            // bir yazma hatasını başarı gibi gösterirdi.
            db.ChangeTracker.Clear();

            var winner = await Find(db, delivery.DeliveryKey, cancellationToken);

            if (winner is null)
            {
                throw;
            }

            return new DeliveryClaim(false, winner.ChangeId);
        }
    }

    /// <summary>
    /// Talebi geri alır. Değişiklik olayı yazılamadığında çağrılıyor: kayıt
    /// durursa sağlayıcının yeniden denemesi sessizce "mükerrer" sayılır ve tek
    /// bir geçici ClickHouse hatası olayı <b>kalıcı olarak</b> kaybettirirdi.
    /// </summary>
    public async Task ReleaseAsync(string deliveryKey, CancellationToken cancellationToken = default)
    {
        await using var db = await factory.CreateDbContextAsync(cancellationToken);

        await db.ChangeWebhookDeliveries
            .Where(d => d.DeliveryKey == deliveryKey)
            .ExecuteDeleteAsync(cancellationToken);
    }

    private static Task<ChangeWebhookDeliveryEntity?> Find(
        ControlPlaneDbContext db,
        string deliveryKey,
        CancellationToken cancellationToken) =>
        db.ChangeWebhookDeliveries
            .AsNoTracking()
            .FirstOrDefaultAsync(d => d.DeliveryKey == deliveryKey, cancellationToken);
}
