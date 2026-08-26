using System.Reflection;
using Bizigo.ControlPlane;
using Bizigo.Rca;

namespace Bizigo.UnitTests;

/// <summary>
/// Koşum durumunun bekçileri (T46, F4 kota kararı §9).
///
/// <para>
/// Hepsi saf fonksiyon üzerinde: geçiş kararı bir veritabanı satırından ya da
/// bir saatten değil yalnızca girdilerden çıkıyor, dolayısıyla test ne bekleme
/// ne sahte bir depo istiyor.
/// </para>
/// </summary>
public sealed class RcaRunLifecycleTests
{
    /// <summary>
    /// <b>Ölçüt: operatör yapılandırmaya bakarak öngörebilir miydi?</b>
    /// Öngörebilirse <c>Cancelled</c>, öngöremezse <c>Failed</c>.
    /// </summary>
    [Theory]
    [InlineData(RcaStopReason.EvidenceDurationExceeded)]
    [InlineData(RcaStopReason.ReasoningDurationExceeded)]
    [InlineData(RcaStopReason.TokenBudgetExhausted)]
    [InlineData(RcaStopReason.TotalDurationExceeded)]
    [InlineData(RcaStopReason.OperatorCancelled)]
    public void Yapilandirmadan_ongorulebilen_kesinti_Cancelled(RcaStopReason reason)
    {
        Assert.Equal(RcaRunState.Cancelled, RcaRunLifecycle.Classify(reason));
    }

    [Theory]
    [InlineData(RcaStopReason.ProviderFailure)]
    [InlineData(RcaStopReason.ModelFailure)]
    [InlineData(RcaStopReason.Unexpected)]
    public void Ongorulemeyen_ariza_Failed(RcaStopReason reason)
    {
        Assert.Equal(RcaRunState.Failed, RcaRunLifecycle.Classify(reason));
    }

    [Fact]
    public void Kesinti_sebebi_olmadan_siniflandirma_yapilamiyor()
    {
        // `None` bir kesinti değil; sessizce bir duruma düşmek, "bitti" ile
        // "kesildi"yi aynı kefeye koymak olurdu.
        Assert.Throws<ArgumentOutOfRangeException>(
            () => RcaRunLifecycle.Classify(RcaStopReason.None));
    }

    /// <summary>
    /// <b>§9 birinci bulgu.</b> "Reddedilen koşum düşülmez" yalnızca <i>girişte</i>
    /// reddedilen için geçerli; süre ya da token tavanına takılan koşum kanıt
    /// topladı, maliyeti gerçekten ödendi.
    /// </summary>
    [Fact]
    public void Yalnizca_kapida_reddedilen_kotadan_dusulmuyor()
    {
        Assert.False(RcaRunLifecycle.CountsAgainstQuota(RcaRunState.Rejected));

        foreach (var state in Enum.GetValues<RcaRunState>().Where(s => s != RcaRunState.Rejected))
        {
            Assert.True(
                RcaRunLifecycle.CountsAgainstQuota(state),
                $"{state} kotadan düşülmeli — iş başladı, maliyet ödendi.");
        }
    }

    /// <summary>
    /// <b>İki eksen ayrı.</b> Token bütçesi dolan koşum <c>Cancelled</c> <b>ve</b>
    /// düşülüyor. Yazılmasaydı "iptal edildi, o hâlde bedava" çıkarımı doğardı.
    /// </summary>
    [Fact]
    public void Iptal_edilen_kosum_bedava_degil()
    {
        var state = RcaRunLifecycle.Classify(RcaStopReason.TokenBudgetExhausted);

        Assert.Equal(RcaRunState.Cancelled, state);
        Assert.True(RcaRunLifecycle.CountsAgainstQuota(state));
    }

    [Fact]
    public void Kuyrukta_bekleyen_ve_kosan_terminal_degil()
    {
        // Eşzamanlılık muhasebesi bunu okuyor: `Queued` slot tutmuyor,
        // `Running` tutuyor, gerisi bıraktı.
        Assert.False(RcaRunLifecycle.IsTerminal(RcaRunState.Queued));
        Assert.False(RcaRunLifecycle.IsTerminal(RcaRunState.Running));

        foreach (var state in Enum.GetValues<RcaRunState>()
                     .Where(s => s is not (RcaRunState.Queued or RcaRunState.Running)))
        {
            Assert.True(RcaRunLifecycle.IsTerminal(state), $"{state} terminal olmalı.");
        }
    }

    /// <summary>
    /// <b>§4.1'in taşıyıcı kuralı:</b> kota yüzünden RCA üretilmemiş bir alarm,
    /// RCA'sı boş çıkmış alarmdan ayırt edilebilmeli. Ayrımı ekranın yorumuna
    /// bırakmak, üç ekranın üç farklı cümle uydurması demekti.
    /// </summary>
    [Fact]
    public void Kota_reddi_ile_bos_sonuc_ayri_cumleler()
    {
        var quota = RcaRunLifecycle.Describe(RcaRunState.Rejected, RcaRejectionReason.QuotaExceeded);
        var empty = RcaRunLifecycle.Describe(RcaRunState.Empty, RcaRejectionReason.None);

        Assert.NotEqual(quota, empty);
        Assert.Contains("hiç çalıştırılmadı", quota, StringComparison.Ordinal);
        Assert.Contains("bulamadı", empty, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>§9 ikinci bulgu.</b> Eşzamanlılık bir ret değil bir bekletme; tek bir
    /// "şu an çalıştırılamıyor" mesajı kullanıcıyı bekleyeceği yerde kotasını
    /// sorgulamaya gönderirdi.
    /// </summary>
    [Fact]
    public void Sirada_beklemek_kota_dolmasindan_farkli_okunuyor()
    {
        var queued = RcaRunLifecycle.Describe(RcaRunState.Queued, RcaRejectionReason.None);
        var quota = RcaRunLifecycle.Describe(RcaRunState.Rejected, RcaRejectionReason.QuotaExceeded);

        Assert.NotEqual(queued, quota);
        Assert.Contains("kota değil", queued, StringComparison.Ordinal);
    }

    [Fact]
    public void Her_durum_ve_her_ret_sebebi_bir_cumle_uretiyor()
    {
        // Kapalı kümenin bedeli: yeni bir değer eklenirse cümlesi de eklenmeli.
        // "Bilinmeyen durum" fallback'ine düşen bir değer, ekranda sessizce
        // anlamsız bir metin gösterirdi.
        foreach (var state in Enum.GetValues<RcaRunState>())
        {
            var text = RcaRunLifecycle.Describe(state, RcaRejectionReason.None);
            Assert.False(string.IsNullOrWhiteSpace(text));
            Assert.NotEqual("Bilinmeyen durum.", text);
        }

        foreach (var reason in Enum.GetValues<RcaRejectionReason>().Where(r => r != RcaRejectionReason.None))
        {
            var text = RcaRunLifecycle.Describe(RcaRunState.Rejected, reason);
            Assert.NotEqual("RCA çalıştırılmadı.", text);
        }
    }
}

/// <summary>
/// <b>Statü tek yerde durur</b> — koordinatörün çivilediği sınır (T46).
///
/// <para>
/// <c>rca_runs</c> koşumun başına gelen her şeyin sahibi; <c>rca_report</c> —
/// geldiğinde — <b>üretilen belgenin</b> sahibi olacak ve statü taşımayacak.
/// İkiye bölünürlerse "kota mı, boş mu" sorusu bir <c>join</c>'e döner ve
/// join'in iki sessiz hâli var: satır ikisinde birden ya da hiçbirinde. İkisi de
/// belirti üretmez — ekran bir şey gösterir, yanlış olduğunu kimse görmez.
/// </para>
///
/// <para>
/// Bu bekçi <c>rca_report</c> <b>var olmadan</b> yazıldı ve bilerek: sınırı
/// yoruma bırakmak, dördüncü tekrarı davet etmek olurdu. T44 belgeyi eklediğinde
/// buraya bir statü kolonu koyarsa test kırmızı yanacak.
/// </para>
/// </summary>
public sealed class RcaReportStatusGuardTests
{
    private static readonly Assembly ControlPlane = typeof(RcaRunEntity).Assembly;

    [Fact]
    public void RcaRunState_yalnizca_rca_runs_uzerinde()
    {
        var offenders = ControlPlane.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false } && t != typeof(RcaRunEntity))
            .Where(t => t.GetProperties().Any(p => p.PropertyType == typeof(RcaRunState)))
            .Select(t => t.Name)
            .ToArray();

        Assert.True(
            offenders.Length == 0,
            "Koşum durumu `rca_runs` dışında da taşınıyor: " + string.Join(", ", offenders) +
            ". Durum tek kapalı kümede ve tek tabloda kalmalı.");
    }

    [Fact]
    public void Rca_belge_varliklari_statu_benzeri_kolon_tasimiyor()
    {
        // Ad üzerinden arama, çünkü T44 kendi enum'ını yazabilir — tip
        // kontrolü onu yakalamaz.
        var reportTypes = ControlPlane.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false })
            .Where(t => t.Name.StartsWith("RcaReport", StringComparison.Ordinal))
            .ToArray();

        foreach (var type in reportTypes)
        {
            var statusLike = type.GetProperties()
                .Where(p => p.Name.Contains("State", StringComparison.Ordinal)
                    || p.Name.Contains("Status", StringComparison.Ordinal))
                .Select(p => $"{type.Name}.{p.Name}")
                .ToArray();

            Assert.True(
                statusLike.Length == 0,
                "RCA belge varlığı statü taşıyor: " + string.Join(", ", statusLike) +
                ". Statü `rca_runs`'ın; belge tablosu üretilen metnin sahibi.");
        }
    }
}
