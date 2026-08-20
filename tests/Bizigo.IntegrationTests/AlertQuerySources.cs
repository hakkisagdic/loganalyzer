using Bizigo.Alerting;
using Bizigo.Query;

namespace Bizigo.IntegrationTests;

/// <summary>
/// Testte tek bir <see cref="IScopedQuery"/> paylaşılıyor.
///
/// <para>
/// Üretimde her kiralama yeni bir DI kapsamı açıyor, çünkü değerlendirmeler
/// paralel koşuyor ve <c>DbContext</c> paylaşılamaz. Testlerde tur içindeki
/// kural sayısı bir olduğu için paylaşım güvenli — ve ölçülen şey zaten kapsam
/// kiralaması değil, SQL.
/// </para>
///
/// <para>
/// <b>Ortak yerde duruyor</b> (T27): <c>AlertingTests</c>'in içinde özel bir
/// sınıftı ve <c>AlertChainTests</c> yazılırken ikinci bir kopyaya ihtiyaç
/// doğdu. Kopyalamak yerine taşındı — protokol §9: ortak yüzey varsa genişlet,
/// kopyalama. İki kopya, biri değiştiğinde ötekinin sessizce ayrışması demekti.
/// </para>
/// </summary>
internal sealed class SingleQuerySource(IScopedQuery query) : IAlertQuerySource
{
    public AlertQueryLease Lease() => new(query, null);
}
