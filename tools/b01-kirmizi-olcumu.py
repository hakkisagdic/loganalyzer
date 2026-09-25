#!/usr/bin/env python3
"""B01 — pacer ve hüküm bekçilerinin KIRMIZI yanabildiğinin ölçümü (§6).

Yordam ortak (`tools/kirmizi_olcumu.py`); buraya ait olan tek şey KUSUR
LİSTESİ.

On bir kusur ve her birinin yanında YEŞİL KALMASI beklenen bir bekçi var.
Yalnızca "kırmızı yandı" ölçülseydi her şeyi düşüren bir kusur da aynı çıktıyı
verirdi ve kapının NEYİ tuttuğu değil, yalnızca tuttuğu ölçülmüş olurdu.

Üç çift bilerek çapraz kurulu ve her biri bu ticket'ın bir kararını ayrı ayrı
kanıtlıyor:

  * `Unmeasured` ↔ `GeneratorLimited` — biri "yetişemedi", öteki "bilmiyorum".
    Hükmü birine çökerten kusur, ötekinin testini yeşil bırakıyor; yani ikisi
    ayrı iddia. Tek testte olsalar "arıza uydurmak" serbest kalırdı.
  * Etiket ↔ SONUÇ — `GENERATOR-LIMITED` basmak ile kaybı yorumlanamaz kılmak
    ayrı şeyler. Etiketi doğru basıp kararı ona bağlamamak bu depoda ölçülmüş
    bir hâl: bir rozeti okumayan için hiçbir şey değişmez.
  * Hedefi olmak ↔ olmamak — `max`'ın `NoTarget`'ı `Attained` değil.

Ve bir kusur özellikle tavanı sınıyor: taban %80'e çekilirse bugünkü ölçülmüş
davranış (1 000 EPS'de 824, yani %82) GEÇER. Bir tavanın işe yaradığının
kanıtı, mevcut davranışı kutsamamasıdır.
"""

from __future__ import annotations

import sys

from kirmizi_olcumu import Kusur, olc

PACER = "sim/Bizigo.Capacity/TokenBucketPacer.cs"
HUKUM = "sim/Bizigo.Capacity/GeneratorAttainment.cs"
PROFIL = "sim/Bizigo.Capacity/PaceProfile.cs"
MANIFEST = "sim/Bizigo.Capacity/CapacityRunManifest.cs"

KUSURLAR = [
    Kusur(
        # Profil borcu anlık hız × bütün aralık diye hesaplanıyor. Ramp'te son
        # hız bütün geçmişe uygulanır, burst'te üzerinden atlanan pencere yok
        # olur. İki profil de ancak integral ile doğru hedef sayısını taşır.
        ad="profil integrali anlık hedef çarpımına indirgeniyor",
        dosya=PACER,
        bul="        var produced = Profile.IssuedBetween(from, to);",
        koy=(
            "        var produced = Profile.TargetAt(to) is { } rate\n"
            "            ? rate * span.TotalSeconds\n"
            "            : (double?)null;"
        ),
        kirmizi_bekleniyor=["Profil_integrali_ramp_ve_burst_penceresini_koruyor"],
        yesil_kalmali=["Jeton_tukendiginde_bekleme_suresi_donuyor"],
    ),
    Kusur(
        # Değişken profilin hükmü koşum ortalamasını son andaki anlık hedefle
        # karşılaştırıyor. Kusursuz bir ramp böylece kendi kendine geride
        # kalmış görünür.
        ad="ramp hükmü integral yerine son hedefi kullanıyor",
        dosya=HUKUM,
        bul="            ? pacer.Issued / targetWindow.TotalSeconds",
        koy="            ? pacer.Profile.TargetAt(elapsed)",
        kirmizi_bekleniyor=["Kusursuz_ramp_son_hedefe_degil_integrale_gore_yargilaniyor"],
        yesil_kalmali=["Hedefe_ulasan_kosum_attained"],
    ),
    Kusur(
        # Koordinatörün istediği ölçüm: HIZ KONTROLÜNÜ DEVRE DIŞI BIRAK.
        # Bu, `Task.Delay` eşiğinin altına düşmüş hâlin birebir taklidi —
        # kova her zaman "hemen bas" diyor ve ayar hiçbir şey ifade etmiyor.
        ad="hız kontrolü devre dışı — kova hiç bekletmiyor",
        dosya=PACER,
        bul="        return wait > TimeSpan.Zero ? wait : TimeSpan.FromTicks(1);",
        koy="        return TimeSpan.Zero;",
        kirmizi_bekleniyor=["Jeton_tukendiginde_bekleme_suresi_donuyor"],
        # Hüküm kovanın bekletip bekletmediğini değil ÜRETİLEN SAYIYI okuyor,
        # dolayısıyla etkilenmemeli. Etkilenirse iki kapı aynı şeyi ölçüyordur.
        yesil_kalmali=["Hedefin_gerisinde_kalan_kosum_generator_limited"],
    ),
    Kusur(
        # Borç sessizce sıfırlanıyor: sapmanın görünür olduğu tek yer kayboluyor
        # ve %18 geride koşmak ile hedefe ulaşmak yeniden aynı çıktıyı veriyor.
        ad="borç sessizce sıfırlanıyor",
        dosya=PACER,
        bul="    public double? Debt => Profile.HasTarget ? Math.Max(0.0, Issued - Granted) : null;",
        koy="    public double? Debt => Profile.HasTarget ? 0.0 : null;",
        kirmizi_bekleniyor=["Kova_borcu_biriktiriyor"],
        # `max`'ta borç yine `null` kalıyor, yani "hedefi yok" ayrımı bu
        # kusurdan bağımsız.
        yesil_kalmali=["Max_profili_hedef_almiyor"],
    ),
    Kusur(
        # ÖLÇEMEDİM → ULAŞTI. Ölçülmemiş bir koşumu MÜKEMMEL göstermek: bu
        # deponun beş kez adını koyduğu sınıfın kapasite tarafındaki hâli.
        ad="ölçülemeyen koşum ulaşmış sayılıyor",
        dosya=HUKUM,
        bul="                GeneratorVerdict.Unmeasured,\n                target,\n                null,",
        koy="                GeneratorVerdict.Attained,\n                target,\n                null,",
        kirmizi_bekleniyor=["Olculemeyen_kosum_attained_sayilmiyor"],
        # ÇAPRAZ KANIT: `Unmeasured` ≠ `GeneratorLimited` iddiası bu kusurdan
        # etkilenmiyor, çünkü hüküm `GeneratorLimited`'a da düşmedi. İkisinin
        # ayrı test olmasının sebebi tam olarak bu.
        yesil_kalmali=["Olculemeyen_kosum_generator_limited_de_sayilmiyor"],
    ),
    Kusur(
        # ETİKET DOĞRU, SONUÇ YOK. `GENERATOR-LIMITED` basılıyor ama kayıp
        # yorumlanabilir sayılmaya devam ediyor — yani hüküm bir rozete iniyor.
        ad="GENERATOR-LIMITED kaybı yorumlanabilir bırakıyor",
        dosya=HUKUM,
        bul="        Verdict is GeneratorVerdict.Attained or GeneratorVerdict.NoTarget;",
        koy="        Verdict is GeneratorVerdict.Attained or GeneratorVerdict.NoTarget\n"
            "            or GeneratorVerdict.GeneratorLimited;",
        kirmizi_bekleniyor=["Generator_limited_kaybi_yorumlanamaz_kiliyor"],
        # Etiketin kendisi doğru basılmaya devam ediyor: iki iddia ayrı.
        yesil_kalmali=["Hedefin_gerisinde_kalan_kosum_generator_limited"],
    ),
    Kusur(
        # TAVAN BUGÜNKÜ DAVRANIŞI KUTSUYOR. %80'e çekilirse 824/1 000 (%82)
        # geçer — yani kapı, kaldırmak için var olduğu hâli onaylar.
        ad="ulaşım tabanı bugünkü davranışı geçirecek kadar düşüyor",
        dosya=HUKUM,
        bul="    public const double MinimumAttainment = 0.95;",
        koy="    public const double MinimumAttainment = 0.80;",
        kirmizi_bekleniyor=[
            "Hedefin_gerisinde_kalan_kosum_generator_limited",
            "Bekci_bos_kume_uzerinde_donmuyor",
        ],
        yesil_kalmali=["Olculemeyen_kosum_attained_sayilmiyor"],
    ),
    Kusur(
        # "HEDEFİ YOK" hâli siliniyor: `max` da hedefli sayılıyor ve hüküm
        # `Unmeasured`'a düşüyor. `NoTarget`'ın ayrı bir değer olması gerektiği
        # iddiası buradan ölçülüyor.
        ad="hedefi olmayan profil hedefli sayılıyor",
        dosya=PROFIL,
        bul="    public bool HasTarget => this is not Max;",
        koy="    public bool HasTarget => true;",
        kirmizi_bekleniyor=["Max_profili_hedef_almiyor"],
        yesil_kalmali=["Hedefe_ulasan_kosum_attained"],
    ),
    Kusur(
        # Manifest özeti UTF-8'e kayıyor. Fark yalnızca 0x7F ÜSTÜ baytlarda
        # görünüyor, yani ASCII bir örnekle sınanan bir test bunu HİÇ görmez.
        # Etkisi: ham arşivdeki baytlarla eşleşmeyen özet → `ProductArchived`
        # sessizce sıfır → kayıp yokken "her satır kayıp".
        ad="manifest özeti UTF-8'e kayıyor",
        dosya=MANIFEST,
        bul="        Convert.ToHexStringLower(SHA256.HashData(wireBytes));",
        koy=(
            "        Convert.ToHexStringLower(SHA256.HashData(\n"
            "            Encoding.UTF8.GetBytes(Encoding.Latin1.GetString(wireBytes))));"
        ),
        kirmizi_bekleniyor=[
            "Ozet_latin1_baytlarindan_aliniyor",
            "Ozet_gercek_tel_baytlarindan_alinabiliyor",
        ],
        yesil_kalmali=["Beklenen_sayi_ozet_listesinden_turuyor"],
    ),
    Kusur(
        # "Üretecin nerede koştuğu" şartı düşüyor: hüküm `Attained` olan bir
        # koşumun sayısı, neyin tavanı olduğu bilinmeden payda sayılıyor.
        ad="üretecin nerede koştuğu şartı düşüyor",
        dosya=MANIFEST,
        bul="        Attainment.LossIsInterpretable && GeneratorOnSameHost is not null;",
        koy="        Attainment.LossIsInterpretable;",
        kirmizi_bekleniyor=["Uretecin_nerede_kostugu_soylenmediyse_payda_olamiyor"],
        # Geride kalmış koşum yine payda olamıyor — iki şart AYRI iddialar.
        yesil_kalmali=["Geride_kalmis_kosum_kayip_hesabina_payda_olamiyor"],
    ),
    Kusur(
        # Sayı hükümsüz basılıyor: bir `Expected` değerini hükümsüz gören
        # okuyucu onu bir OLGU sanar.
        ad="sayı hükümsüz basılıyor",
        dosya=MANIFEST,
        bul=" · {host} · {Attainment.Describe()}\");",
        koy=" · {host}\");",
        kirmizi_bekleniyor=["Sayi_hukumsuz_basilmiyor"],
        yesil_kalmali=["Manifest_defterin_bekledigi_sekle_oturuyor"],
    ),
]


if __name__ == "__main__":
    sys.exit(olc(KUSURLAR, ".b01-olcum-yedek"))
