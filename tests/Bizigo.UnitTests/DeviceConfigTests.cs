using Bizigo.Devices;

namespace Bizigo.UnitTests;

/// <summary>
/// Cihaz config fark tespiti (T26).
///
/// <para>
/// <b>En önemli test <see cref="Yalnizca_zaman_damgasi_degisince_fark_yok"/>:</b>
/// gürültü elenmezse her çekim bir "değişiklik" üretir ve <c>change_events</c>
/// RCA'nın F3'te arayacağı sinyal yerine kendi gürültüsüyle dolar. Ticket bu
/// kriteri ayrıca yazıyor çünkü belirtisi sessiz: tablo dolu görünür, içindeki
/// hiçbir satır bir şey anlatmaz.
/// </para>
/// </summary>
public sealed class DeviceConfigTests
{
    // ------------------------------------------------------------ FortiGate

    /// <summary>
    /// Gerçek bir FortiGate <c>show</c> çıktısının şekli: sürüm başlığı, iç içe
    /// <c>config</c>/<c>edit</c> blokları ve şifrelenmiş bir ön-paylaşımlı
    /// anahtar.
    /// </summary>
    private const string FortiBefore = """
        #config-version=FGT60F-7.2.8-FW-build1639-230929:opmode=0:vdom=0
        #conf_file_ver=17498562398745
        #buildno=1639
        #global_vdom=1
        config system interface
            edit "wan1"
                set vdom "root"
                set ip 203.0.113.10 255.255.255.0
                set allowaccess ping https ssh
            next
            edit "internal"
                set vdom "root"
                set ip 10.20.0.1 255.255.255.0
            next
        end
        config firewall policy
            edit 1
                set srcintf "internal"
                set dstintf "wan1"
                set action accept
                set schedule "always"
            next
        end
        config vpn ipsec phase1-interface
            edit "to-branch"
                set interface "wan1"
                set psksecret ENC 7Yh2KpLmN0qRsTuVwXyZ1234567890abcdef
            next
        end
        """;

    [Fact]
    public void Yalnizca_zaman_damgasi_degisince_fark_yok()
    {
        // Aynı config, farklı yazım: sürüm başlığındaki sayaç değişmiş.
        var after = FortiBefore
            .Replace("#conf_file_ver=17498562398745", "#conf_file_ver=17498599999999", StringComparison.Ordinal)
            .Replace("#buildno=1639", "#buildno=1640", StringComparison.Ordinal);

        var diff = ConfigDiff.Compare(
            ConfigNormalizer.Normalize(ConfigNormalizer.FortiGate, FortiBefore),
            ConfigNormalizer.Normalize(ConfigNormalizer.FortiGate, after));

        Assert.False(diff.HasChanges);
        Assert.Equal(0, diff.Added);
        Assert.Equal(0, diff.Removed);
    }

    [Fact]
    public void Gercek_degisiklik_dogru_bolumde_cikiyor()
    {
        var after = FortiBefore.Replace(
            "set allowaccess ping https ssh",
            "set allowaccess ping https",
            StringComparison.Ordinal);

        var diff = ConfigDiff.Compare(
            ConfigNormalizer.Normalize(ConfigNormalizer.FortiGate, FortiBefore),
            ConfigNormalizer.Normalize(ConfigNormalizer.FortiGate, after));

        Assert.True(diff.HasChanges);
        Assert.Equal(1, diff.Added);
        Assert.Equal(1, diff.Removed);

        // "Hangi bölüm" cevabı: değişiklik wan1 arayüzünde.
        var section = Assert.Single(diff.Sections);
        Assert.Contains("system interface", section.Section, StringComparison.Ordinal);
        Assert.Contains("wan1", section.Section, StringComparison.Ordinal);
    }

    [Fact]
    public void Ozet_degisen_satirin_icerigini_tasimiyor()
    {
        // Özet `change_events.summary`'ye yazılıyor ve config satırları sır
        // taşıyabiliyor. Ne değiştiği bölüm adıyla söyleniyor, ne olduğu değil.
        var after = FortiBefore.Replace(
            "set psksecret ENC 7Yh2KpLmN0qRsTuVwXyZ1234567890abcdef",
            "set psksecret ENC ZZZZyeniAnahtar9876543210fedcba",
            StringComparison.Ordinal);

        var diff = ConfigDiff.Compare(
            ConfigNormalizer.Normalize(ConfigNormalizer.FortiGate, FortiBefore),
            ConfigNormalizer.Normalize(ConfigNormalizer.FortiGate, after));

        var summary = diff.Describe("fw-core-01");

        Assert.DoesNotContain("7Yh2KpLmN0qRsTuVwXyZ", summary, StringComparison.Ordinal);
        Assert.DoesNotContain("ZZZZyeniAnahtar", summary, StringComparison.Ordinal);
        Assert.Contains("fw-core-01", summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Anahtar_rotasyonu_fark_olarak_gorunuyor_ama_sir_saklanmiyor()
    {
        // Gizli değeri SİLMEK, dönen bir anahtarı görünmez yapardı. Maskeleniyor:
        // özet değişince fark yakalanıyor, değer hiçbir yere yazılmıyor.
        var after = FortiBefore.Replace(
            "set psksecret ENC 7Yh2KpLmN0qRsTuVwXyZ1234567890abcdef",
            "set psksecret ENC ZZZZyeniAnahtar9876543210fedcba",
            StringComparison.Ordinal);

        var before = ConfigNormalizer.Normalize(ConfigNormalizer.FortiGate, FortiBefore);
        var fresh = ConfigNormalizer.Normalize(ConfigNormalizer.FortiGate, after);

        Assert.True(ConfigDiff.Compare(before, fresh).HasChanges);

        var stored = ConfigDiff.Serialize(fresh);

        Assert.DoesNotContain("ZZZZyeniAnahtar", stored, StringComparison.Ordinal);
        Assert.DoesNotContain("7Yh2KpLmN0qRsTuVwXyZ", stored, StringComparison.Ordinal);
        Assert.Contains("<gizli:", stored, StringComparison.Ordinal);

        // Anahtar adı korunuyor: "hangi sır değişti" görünür kalmalı.
        Assert.Contains("psksecret", stored, StringComparison.Ordinal);
    }

    [Fact]
    public void Ayni_gizli_deger_ayni_maskeyi_uretiyor()
    {
        // Aksi hâlde her çekim sahte bir değişiklik olurdu.
        var once = ConfigNormalizer.Normalize(ConfigNormalizer.FortiGate, FortiBefore);
        var twice = ConfigNormalizer.Normalize(ConfigNormalizer.FortiGate, FortiBefore);

        Assert.Equal(ConfigDiff.Serialize(once), ConfigDiff.Serialize(twice));
    }

    // ----------------------------------------------------------- Cisco ASA

    private const string AsaBefore = """
        : Saved
        : Written by admin at 10:32:11.123 UTC Tue Aug 18 2026
        : Hardware:   ASA5516, 8192 MB RAM, CPU Atom C2000 2416 MHz
        ASA Version 9.12(4)
        hostname asa-izmir-02
        interface GigabitEthernet1/1
         nameif outside
         security-level 0
         ip address 198.51.100.2 255.255.255.0
        interface GigabitEthernet1/2
         nameif inside
         security-level 100
         ip address 10.30.0.1 255.255.255.0
        access-list outside_in extended permit tcp any host 10.30.0.10 eq https
        access-list outside_in extended deny ip any any log
        snmp-server community S3cr3tC0mmunity
        ntp clock-period 17179738
        Cryptochecksum:8a7b6c5d4e3f2a1b0c9d8e7f6a5b4c3d
        """;

    [Fact]
    public void Asa_yeniden_yaziminda_yalnizca_gurultu_degisince_fark_yok()
    {
        // ASA her `write mem`'de zaman damgasını ve cryptochecksum'ı değiştiriyor.
        var after = AsaBefore
            .Replace("10:32:11.123 UTC Tue Aug 18 2026", "14:07:52.900 UTC Wed Aug 19 2026", StringComparison.Ordinal)
            .Replace("Cryptochecksum:8a7b6c5d4e3f2a1b0c9d8e7f6a5b4c3d", "Cryptochecksum:1111222233334444555566667777888", StringComparison.Ordinal)
            .Replace("ntp clock-period 17179738", "ntp clock-period 17179999", StringComparison.Ordinal);

        var diff = ConfigDiff.Compare(
            ConfigNormalizer.Normalize(ConfigNormalizer.CiscoAsa, AsaBefore),
            ConfigNormalizer.Normalize(ConfigNormalizer.CiscoAsa, after));

        Assert.False(diff.HasChanges);
    }

    [Fact]
    public void Asa_acl_eklemesi_yakalaniyor()
    {
        var after = AsaBefore.Replace(
            "access-list outside_in extended deny ip any any log",
            "access-list outside_in extended permit tcp any host 10.30.0.11 eq ssh\naccess-list outside_in extended deny ip any any log",
            StringComparison.Ordinal);

        var diff = ConfigDiff.Compare(
            ConfigNormalizer.Normalize(ConfigNormalizer.CiscoAsa, AsaBefore),
            ConfigNormalizer.Normalize(ConfigNormalizer.CiscoAsa, after));

        Assert.Equal(1, diff.Added);
        Assert.Equal(0, diff.Removed);
    }

    [Fact]
    public void Asa_snmp_community_si_saklanmiyor()
    {
        var stored = ConfigDiff.Serialize(
            ConfigNormalizer.Normalize(ConfigNormalizer.CiscoAsa, AsaBefore));

        Assert.DoesNotContain("S3cr3tC0mmunity", stored, StringComparison.Ordinal);
        Assert.Contains("<gizli:", stored, StringComparison.Ordinal);
    }

    [Fact]
    public void Asa_girintili_satir_ust_bolume_ait()
    {
        var lines = ConfigNormalizer.Normalize(ConfigNormalizer.CiscoAsa, AsaBefore);

        var nameif = Assert.Single(lines, l => l.Text == "nameif outside");
        Assert.Equal("interface GigabitEthernet1/1", nameif.Section);
    }

    // ------------------------------------------------------------ MikroTik

    private const string MikroBefore = """
        # aug/18/2026 10:32:11 by RouterOS 7.14.3
        # software id = ABCD-1234
        # model = CRS326-24G-2S+
        # serial number = HG108KL9M2
        /interface bridge
        add name=bridge-lan protocol-mode=rstp
        /ip address
        add address=10.40.0.1/24 interface=bridge-lan network=10.40.0.0
        /ip firewall filter
        add action=accept chain=input protocol=icmp
        add action=drop chain=input in-interface=ether1
        /snmp community
        set [ find default=yes ] name=public
        """;

    [Fact]
    public void Mikrotik_export_basligi_fark_uretmiyor()
    {
        // Export başlığı her çekimde o anın tarihini taşıyor.
        var after = MikroBefore.Replace(
            "# aug/18/2026 10:32:11 by RouterOS 7.14.3",
            "# aug/19/2026 03:11:47 by RouterOS 7.14.3",
            StringComparison.Ordinal);

        var diff = ConfigDiff.Compare(
            ConfigNormalizer.Normalize(ConfigNormalizer.MikroTik, MikroBefore),
            ConfigNormalizer.Normalize(ConfigNormalizer.MikroTik, after));

        Assert.False(diff.HasChanges);
    }

    [Fact]
    public void Mikrotik_yol_satiri_bolumu_belirliyor()
    {
        var lines = ConfigNormalizer.Normalize(ConfigNormalizer.MikroTik, MikroBefore);

        var drop = Assert.Single(lines, l => l.Text.Contains("action=drop", StringComparison.Ordinal));
        Assert.Equal("ip firewall filter", drop.Section);
    }

    // ----------------------------------------------------- fark algoritması

    [Fact]
    public void Bolum_icinde_yer_degistiren_satir_sahte_fark_uretmiyor()
    {
        // Bildirimsel config'te aynı bölümdeki iki ayarın sırası anlam
        // taşımıyor ve cihazlar yeniden yazımda sırayı değiştirebiliyor. LCS
        // tabanlı bir fark burada yüzlerce sahte değişiklik üretirdi.
        var before = new List<ConfigLine>
        {
            new("firewall", "permit a"),
            new("firewall", "permit b"),
            new("firewall", "deny all"),
        };

        var after = new List<ConfigLine>
        {
            new("firewall", "deny all"),
            new("firewall", "permit a"),
            new("firewall", "permit b"),
        };

        Assert.False(ConfigDiff.Compare(before, after).HasChanges);
    }

    [Fact]
    public void Bolumler_arasi_tasinan_satir_iki_bolumde_de_gorunuyor()
    {
        var before = new List<ConfigLine> { new("a", "kural") };
        var after = new List<ConfigLine> { new("b", "kural") };

        var diff = ConfigDiff.Compare(before, after);

        Assert.Equal(1, diff.Added);
        Assert.Equal(1, diff.Removed);
        Assert.Equal(2, diff.Sections.Count);
    }

    [Fact]
    public void Tekrar_sayisi_onemli()
    {
        // Aynı ACL satırının iki kez geçmesi ile bir kez geçmesi farklı
        // config'ler; küme farkı bunu kaçırırdı.
        var before = new List<ConfigLine> { new("acl", "permit x") };
        var after = new List<ConfigLine> { new("acl", "permit x"), new("acl", "permit x") };

        var diff = ConfigDiff.Compare(before, after);

        Assert.Equal(1, diff.Added);
        Assert.Equal(0, diff.Removed);
    }

    [Fact]
    public void Anlik_goruntu_gidip_geliyor()
    {
        var lines = ConfigNormalizer.Normalize(ConfigNormalizer.FortiGate, FortiBefore);
        var roundTripped = ConfigDiff.Deserialize(ConfigDiff.Serialize(lines));

        Assert.Equal(lines.Count, roundTripped.Count);
        Assert.False(ConfigDiff.Compare(lines, roundTripped).HasChanges);
    }

    [Fact]
    public void En_cok_degisen_bolum_basta()
    {
        // "cok" bölümünde üç satır kayboluyor, "az" bölümünde bir satır
        // değişiyor: sıralama toplam değişiklik sayısına göre olmalı.
        var before = new List<ConfigLine>
        {
            new("az", "bir"), new("cok", "a"), new("cok", "b"), new("cok", "c"),
        };

        var after = new List<ConfigLine> { new("az", "iki") };

        var diff = ConfigDiff.Compare(before, after);

        // Özet ilk üç bölümü yazıyor; en ilgili olanlar başta olmalı.
        Assert.Equal("cok", diff.Sections[0].Section);
    }

    [Fact]
    public void Bos_cikti_fark_uretmiyor()
    {
        // Yarım okunmuş bir config, silinmiş yüzlerce satır gibi görünürdü;
        // boş çıktı hiç satır üretmiyor ve çağıran onu hata olarak ele alıyor.
        Assert.Empty(ConfigNormalizer.Normalize(ConfigNormalizer.FortiGate, ""));
        Assert.Empty(ConfigNormalizer.Normalize(ConfigNormalizer.FortiGate, null));
        Assert.False(ConfigDiff.Compare([], []).HasChanges);
    }

    // ----------------------------------------------------------- toplayıcı

    [Fact]
    public async Task Erisilemeyen_cihaz_istisna_degil_sonuc_uretiyor()
    {
        // Çekim döngüsü tek bir erişilemez cihaz yüzünden ölmemeli.
        var service = new DeviceConfigService(
            new FailingTransport("Cihaza ağ üzerinden ulaşılamadı."),
            [new FortiGateCollector()]);

        var capture = await service.CaptureAsync(Target(), TestContext.Current.CancellationToken);

        Assert.False(capture.Ok);
        Assert.Empty(capture.Lines);
        Assert.Contains("ulaşılamadı", capture.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Bilinmeyen_vendor_reddediliyor()
    {
        var service = new DeviceConfigService(new FailingTransport("olmaz"), [new FortiGateCollector()]);

        var capture = await service.CaptureAsync(
            Target("checkpoint.gaia"),
            TestContext.Current.CancellationToken);

        Assert.False(capture.Ok);
        Assert.Contains("toplayıcı yok", capture.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Es_zamanlilik_siniri_asilmiyor()
    {
        // Yüzlerce cihaza aynı anda bağlanmak izlenen cihazı yorar; sınır
        // gerçekten uygulanıyor mu, ölçülüyor.
        var transport = new CountingTransport();
        var service = new DeviceConfigService(transport, [new FortiGateCollector()], maxConcurrency: 3);

        await Task.WhenAll(Enumerable.Range(0, 20).Select(_ =>
            service.CaptureAsync(Target(), TestContext.Current.CancellationToken)));

        Assert.True(
            transport.MaxConcurrent <= 3,
            $"Eşzamanlılık sınırı aşıldı: {transport.MaxConcurrent}");

        Assert.Equal(20, transport.Calls);
    }

    [Fact]
    public void Hedef_kimlik_bilgisini_basmiyor()
    {
        // `record` olsaydı üretilmiş `ToString()` parolayı da basardı.
        var target = Target();

        Assert.DoesNotContain("p4rol4", target.ToString(), StringComparison.Ordinal);
        Assert.Equal("fortinet.fortigate@10.0.0.1:22", target.ToString());
    }

    /// <summary>
    /// Dört belirteçli SNMP satırı da maskeleniyor.
    ///
    /// <para>
    /// ASA/IOS'un yaygın biçimi anahtar kelimeden önce dört belirteç taşıyor:
    /// <c>snmp-server host &lt;arayüz&gt; &lt;ip&gt; community &lt;anahtar&gt;</c>.
    /// Önek sınırı buna yetmediği sürece anahtar ham hâlde normalize edilmiş
    /// config'te kalıyordu — ve orası saklanan anlık görüntünün metni.
    /// </para>
    /// </summary>
    [Fact]
    public void Dort_belirtecli_snmp_satiri_maskeleniyor()
    {
        const string config = """
            hostname asa-dc-01
            !
            snmp-server host inside 10.1.1.9 community S3cr3tC0mmun1ty
            """;

        var masked = Flatten(ConfigNormalizer.Normalize(ConfigNormalizer.CiscoAsa, config));

        Assert.DoesNotContain("S3cr3tC0mmun1ty", masked, StringComparison.Ordinal);
        Assert.Contains("<gizli:", masked, StringComparison.Ordinal);

        // Anahtarın ADI korunuyor: "hangi sır değişti" görünür kalmalı.
        Assert.Contains("community", masked, StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>Açıklama alanı maskelenmiyor.</b>
    ///
    /// <para>
    /// Önek sınırı genişletilince doğan kusur buydu: <c>secret</c> sözcüğü bir
    /// açıklamanın içinde geçtiğinde desen tutuyor ve operatörün yazdığı metin
    /// bir özete dönüşüyordu. Sır sızıntısı değil, <b>sessiz veri kaybı</b> —
    /// fark raporu açıklamayı yok ediyor ve kimse silindiğini görmüyor.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(ConfigNormalizer.CiscoAsa, "description shared secret for the Ankara tunnel", "Ankara tunnel")]
    [InlineData(ConfigNormalizer.CiscoAsa, "remark allow community access from branch", "access from branch")]
    [InlineData(ConfigNormalizer.FortiGate, "set comments \"rotate the psksecret every quarter\"", "every quarter")]
    public void Aciklama_alani_maskelenmiyor(string vendor, string line, string korunmali)
    {
        var masked = Flatten(ConfigNormalizer.Normalize(vendor, "hostname x\n" + line));

        Assert.Contains(korunmali, masked, StringComparison.Ordinal);
        Assert.DoesNotContain("<gizli:", masked, StringComparison.Ordinal);
    }

    private static string Flatten(IReadOnlyList<ConfigLine> lines) =>
        string.Join("\n", lines.Select(l => l.Text));

    private static DeviceTarget Target(string vendor = ConfigNormalizer.FortiGate) => new()
    {
        Vendor = vendor,
        Host = "10.0.0.1",
        Username = "readonly",
        Credential = "p4rol4-cok-gizli",
    };

    private sealed class FailingTransport(string error) : IDeviceTransport
    {
        public Task<DeviceCommandResult> RunAsync(
            DeviceTarget target,
            IReadOnlyList<string> commands,
            CancellationToken cancellationToken) =>
            Task.FromResult(new DeviceCommandResult(false, string.Empty, error));
    }

    private sealed class CountingTransport : IDeviceTransport
    {
        private int _current;

        public int MaxConcurrent { get; private set; }

        public int Calls { get; private set; }

        public async Task<DeviceCommandResult> RunAsync(
            DeviceTarget target,
            IReadOnlyList<string> commands,
            CancellationToken cancellationToken)
        {
            var now = Interlocked.Increment(ref _current);

            lock (this)
            {
                Calls++;
                MaxConcurrent = Math.Max(MaxConcurrent, now);
            }

            // Gerçek bir bekleme değil: iş parçacığını bırakmak, eşzamanlı
            // çağrıların gerçekten üst üste binmesi için yeterli.
            await Task.Yield();

            Interlocked.Decrement(ref _current);

            return new DeviceCommandResult(true, "config system global\nend", string.Empty);
        }
    }
}
