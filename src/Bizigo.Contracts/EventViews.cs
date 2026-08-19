namespace Bizigo.Contracts;

/// <summary>
/// Bir olayın <b>alternatif şema görünümleri</b> (F1 §5, K8).
///
/// <para>
/// Eşleme ClickHouse görünümünde tanımlı (<c>db/clickhouse/0003_ocsf_otel_views.sql</c>)
/// ve orada kalması bilinçli: F3'ün Sigma derleyicisi ve doğrudan SQL konuşan
/// araçlar (Grafana, HyperDX) aynı adları görüyor. Bu numaralandırma yalnızca
/// <b>hangi görünüm</b> sorusunu taşıyor; alan adlarının kendisi hiçbir yerde
/// ikinci kez yazılmıyor.
/// </para>
/// </summary>
public enum EventViewKind
{
    Ocsf = 1,
    Otel = 2,
}

/// <summary>
/// Görünümden okunan tek bir alan: adı ve metne çevrilmiş değeri.
///
/// <para>
/// Sözlük değil <b>sıralı liste</b>: görünümdeki kolon sırası anlam taşıyor
/// (kimlik → sınıf → uçlar → cihaz) ve sözlüğe çevirmek o sırayı kaybetmeyi
/// serbest bırakırdı.
/// </para>
/// </summary>
public sealed record EventFieldView(string Name, string Value);
