using Bizigo.ControlPlane;
using Microsoft.EntityFrameworkCore;

namespace Bizigo.Cli;

/// <summary>
/// Tek koşumluk komutun kontrol düzlemi fabrikası. Havuz kurmak, ömrü
/// saniyelerle ölçülen bir süreçte kazanç sağlamıyor.
///
/// <para>
/// <b>Bir iç tipten ÇIKARILDI, kopyalanmadı</b> (M16).
/// <c>SigmaSyncCommandHandler</c> içinde <c>private</c> duruyordu ve
/// <c>rca quota</c> aynısına ihtiyaç duydu. Dört satırlık bir tipi ikinci kez
/// yazmak küçük bir kopya gibi görünüyor — ama <c>DbContextOptions</c>'ı nasıl
/// kurduğu bir gün değişirse (bağlantı dayanıklılığı, komut zaman aşımı) iki
/// komut sessizce farklı davranırdı. §9'un ölçütü kopyanın boyu değil,
/// ayrışabilir olması.
/// </para>
/// </summary>
internal sealed class SingleContextFactory(DbContextOptions<ControlPlaneDbContext> options)
    : IDbContextFactory<ControlPlaneDbContext>
{
    public ControlPlaneDbContext CreateDbContext() => new(options);
}
