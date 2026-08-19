using Bizigo.Query;
using Microsoft.Extensions.DependencyInjection;

namespace Bizigo.Alerting;

/// <summary>
/// Değerlendirme başına <b>taze</b> bir sorgu kapısı.
///
/// <para>
/// <b>Bu soyutlama olmadan eşzamanlılık limiti bir hataya dönüşürdü.</b>
/// <see cref="IScopedQuery"/> istek kapsamlı (scoped) ve içinde bir
/// <c>DbContext</c> taşıyor — denetim kaydı ve envanter oradan okunuyor.
/// Tek bir örneği dört değerlendirmeye paylaştırmak, aynı <c>DbContext</c>'e
/// paralel erişim demek: EF bunu çalışma anında istisnayla reddediyor ve
/// belirtisi "alarmlar bazen çalışmıyor" olurdu.
/// </para>
///
/// <para>
/// Ayrı bir arayüz olmasının ikinci sebebi test: sahtelemek için DI konteyneri
/// kurmak gerekmiyor, tek satırlık bir uygulama yetiyor.
/// </para>
/// </summary>
public interface IAlertQuerySource
{
    AlertQueryLease Lease();
}

/// <summary>Kullanılıp bırakılan sorgu kapısı; bırakıldığında kapsamı da kapanıyor.</summary>
public sealed class AlertQueryLease(IScopedQuery query, IDisposable? scope) : IDisposable
{
    public IScopedQuery Query { get; } = query ?? throw new ArgumentNullException(nameof(query));

    public void Dispose() => scope?.Dispose();
}

/// <summary>Üretimdeki uygulama: her kiralama yeni bir DI kapsamı açıyor.</summary>
public sealed class ServiceScopeAlertQuerySource(IServiceScopeFactory scopes) : IAlertQuerySource
{
    private readonly IServiceScopeFactory _scopes = scopes ?? throw new ArgumentNullException(nameof(scopes));

    public AlertQueryLease Lease()
    {
        var scope = _scopes.CreateScope();

        try
        {
            return new AlertQueryLease(scope.ServiceProvider.GetRequiredService<IScopedQuery>(), scope);
        }
        catch
        {
            // Kapsam açıldıysa ve çözümleme düştüyse kapsamı sızdırmıyoruz;
            // arka plan işçisinde sızan kapsam bağlantı havuzunu tüketir.
            scope.Dispose();
            throw;
        }
    }
}
