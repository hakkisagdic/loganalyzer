using Bizigo.Ingest.Discovery;
using Microsoft.Extensions.Time.Testing;

namespace Bizigo.UnitTests;

/// <summary>
/// Devre kesici (F1 §9: ardışık N hata → 5 dk kapalı, sağlık ucunda görünür).
///
/// <para>
/// Buradaki asıl kabul kriteri "sidecar öldüğünde ne oluyor" değil — o zaten
/// her istekte bağlantı reddiyle çözülür. Kritik olan <b>sonrası</b>: devre
/// açıldıktan sonra hiç denenmemesi ve süre dolunca kendi kendine geri gelmesi.
/// Elle müdahale gerektiren bir devre kesici, olmayan bir devre kesicidir.
/// </para>
/// </summary>
public sealed class SidecarCircuitBreakerTests
{
    private static readonly DateTimeOffset Start = new(2026, 8, 16, 9, 0, 0, TimeSpan.Zero);

    private static (SidecarCircuitBreaker Breaker, FakeTimeProvider Time) Build(
        int threshold = 3,
        int breakMinutes = 5)
    {
        var time = new FakeTimeProvider(Start);
        var options = new SidecarOptions
        {
            FailureThreshold = threshold,
            BreakDuration = TimeSpan.FromMinutes(breakMinutes),
        };

        return (new SidecarCircuitBreaker(options, time), time);
    }

    [Fact]
    public void Baslangicta_kapali_devre_istekler_geciyor()
    {
        var (breaker, _) = Build();

        Assert.Equal(CircuitState.Closed, breaker.State);
        Assert.True(breaker.TryAcquire());
    }

    [Fact]
    public void Ardisik_N_hata_devreyi_aciyor()
    {
        var (breaker, _) = Build(threshold: 3);

        breaker.RecordFailure("bağlantı reddedildi");
        breaker.RecordFailure("bağlantı reddedildi");
        Assert.Equal(CircuitState.Closed, breaker.State);

        breaker.RecordFailure("bağlantı reddedildi");

        Assert.Equal(CircuitState.Open, breaker.State);
        Assert.False(breaker.TryAcquire());
        Assert.Equal(1, breaker.OpenedCount);
        Assert.Equal("bağlantı reddedildi", breaker.LastError);
    }

    [Fact]
    public void Araya_giren_basari_sayaci_sifirliyor()
    {
        var (breaker, _) = Build(threshold: 3);

        breaker.RecordFailure("x");
        breaker.RecordFailure("x");
        breaker.RecordSuccess();
        breaker.RecordFailure("x");
        breaker.RecordFailure("x");

        // Ardışık değil: dört hata var ama araya başarı girdi.
        Assert.Equal(CircuitState.Closed, breaker.State);
    }

    [Fact]
    public void Bes_dakika_sonra_yarim_aciliyor_ve_tek_yoklama_geciyor()
    {
        var (breaker, time) = Build(threshold: 1, breakMinutes: 5);
        breaker.RecordFailure("öldü");

        time.Advance(TimeSpan.FromMinutes(4));
        Assert.Equal(CircuitState.Open, breaker.State);
        Assert.False(breaker.TryAcquire());

        time.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(CircuitState.HalfOpen, breaker.State);

        Assert.True(breaker.TryAcquire());
        // Yoklama tek olmalı: geri gelen sidecar'ın üstüne tüm kuyruğu yollamıyoruz.
        Assert.False(breaker.TryAcquire());
    }

    [Fact]
    public void Yoklama_basarili_olunca_devre_kapaniyor()
    {
        var (breaker, time) = Build(threshold: 1, breakMinutes: 5);
        breaker.RecordFailure("öldü");
        time.Advance(TimeSpan.FromMinutes(5));

        Assert.True(breaker.TryAcquire());
        breaker.RecordSuccess();

        Assert.Equal(CircuitState.Closed, breaker.State);
        Assert.True(breaker.TryAcquire());
        Assert.Null(breaker.LastError);
    }

    [Fact]
    public void Yoklama_duserse_devre_hemen_yeniden_aciliyor()
    {
        var (breaker, time) = Build(threshold: 3, breakMinutes: 5);
        breaker.RecordFailure("x");
        breaker.RecordFailure("x");
        breaker.RecordFailure("x");
        time.Advance(TimeSpan.FromMinutes(5));

        Assert.True(breaker.TryAcquire());
        breaker.RecordFailure("hâlâ ölü");

        // Yarı açıkta eşik beklenmiyor: tek hata yeter.
        Assert.Equal(CircuitState.Open, breaker.State);
        Assert.Equal(2, breaker.OpenedCount);
    }

    [Fact]
    public void Surum_uyusmazligi_esik_beklemeden_aciyor()
    {
        var (breaker, _) = Build(threshold: 10);

        breaker.TripOnVersionMismatch("beklenen 'v1', gelen 'v2'");

        // Yanlış sürümle konuşmak sessizce yanlış veri üretir; dokuz hata daha
        // beklemek o veriyi yazmak demek olurdu.
        Assert.Equal(CircuitState.Open, breaker.State);
        Assert.Contains("v2", breaker.LastError, StringComparison.Ordinal);
    }
}
