#!/usr/bin/env python3
"""T54 — model sınırı muafiyetinin koşum kaydına bağlanmasının ölçümü (§6).

Yordam ortak (`kirmizi_olcumu.py`); bu dosyaya ait olan tek şey KUSUR LİSTESİ.

Beş kusur, beş ayrı iddia. Üçü **yazma/tel** tarafında, ikisi **tip** tarafında.

Tip tarafındakiler için bir şey ölçülerek öğrenildi ve yazılmadan bırakılamaz:
ikisi de **derlemeyi kırmıyor**. Damga parametresini isteğe bağlı yapmak
(`= null`) bugünkü tek çağıranı hiçbir şey değiştirmeye zorlamıyor, yani her şey
derleniyor ve bütün davranış testleri yeşil kalıyor. İlk tasarımda ikisi de
`derleme_kirilmali=True` yazılmıştı; o hâlde ölçüm **hiçbir şey ölçmeyecekti** ve
yeşil bir sonuç "kapı sağlam" diye okunacaktı. Kapının bu yüzden bir **yansıma
iddiası** olması gerekiyor — tipin şekli hakkındaki bir iddia, tipin şekline
bakarak sınanmalı.

Her kusurun yanında YEŞİL KALMASI beklenen bir bekçi var: yalnızca "kırmızı
yandı" ölçülseydi her şeyi düşüren bir kusur da aynı çıktıyı verirdi ve kapının
NEYİ tuttuğu değil, yalnızca tuttuğu ölçülmüş olurdu.
"""

from __future__ import annotations

import sys

from kirmizi_olcumu import Kusur, olc

KUSURLAR = [
    Kusur(
        # Asıl iddia: gerekçeyi boş dizeye çevirmek `null` ile onu aynı bayta
        # indiriyor — "muafiyet yok" ile "muafiyet var, gerekçesi yazılmamış".
        # `AuditFields()` tam bunu yapıyor ve ORADA doğru; telde yanlış.
        ad="tel gerekçeyi boş dizeye çeviriyor",
        dosya="src/Bizigo.Api/RcaRunEndpoints.cs",
        bul="            run.ModelBoundaryOverrideReason,",
        koy="            run.ModelBoundaryOverrideReason ?? string.Empty,",
        kirmizi_bekleniyor=[
            "Null_ile_bos_dize_ayni_seye_indirilmiyor",
            "Muafiyetsiz_kosum_da_bunu_soyluyor",
        ],
        # Damga tarafı bu kusurdan etkilenmiyor: tel dönüşümü ile tipin
        # garantisi ayrı iddialar.
        yesil_kalmali=["Damga_gerekceyi_ucun_kendisinden_aliyor"],
    ),
    Kusur(
        # Muaf koşum ile muafiyetsiz koşum telde aynı baytları üretiyor.
        #
        # BU KUSUR ÖLÇÜMÜN KENDİSİNİ İKİ KEZ DÜZELTTİ ve üçü de bu ajanın
        # hatasıydı, ürünün değil:
        #
        #   1. `Muafiyet_gerekcesi_telde_birebir` KONTROL olarak yazılmıştı ama
        #      sınır anahtarını da okuyor, dolayısıyla kusurdan etkilenmesi
        #      DOĞRU. Kontrol, tele hiç bakmayan tip düzeyi kapısına taşındı.
        #   2. `Muaf_kosum_...` YEŞİL KALDI. İlk teşhis "iki koşum gerekçe
        #      alanından da ayrılıyor" idi ve yalnızca sınırda ayrılan bir çift
        #      eklendi — YETMEDİ, ikinci koşumda yine yeşil kaldı.
        #   3. Asıl sebep: `RcaRunEntity.Id` varsayılanı `Guid.NewGuid()`,
        #      `RequestedAt` varsayılanı `DateTimeOffset.UtcNow`. Yani
        #      karşılaştırma sınırları değil KİMLİKLERİ karşılaştırıyordu ve
        #      HER ZAMAN geçiyordu. Fixture sabitlendi, ve testin başına
        #      iddianın boş olmadığını gösteren bir taban karşılaştırması
        #      eklendi.
        #
        # Ders, T44'ün mtime tuzağıyla aynı aile: yeşil bir sonuç "kusur
        # etkisiz" değil "ölçüm hiç yapılmadı" anlamına da geliyor — burada
        # ölçümü yalancı yapan şey ölçüm aracı değil BEKÇİNİN FIXTURE'IYDI.
        ad="sınır hâli telden düşüyor",
        dosya="src/Bizigo.Api/RcaRunEndpoints.cs",
        bul="            run.ModelBoundary.ToString().ToLowerInvariant(),",
        koy='            "verified",',
        kirmizi_bekleniyor=[
            "Muaf_kosum_muafiyetsizinden_ayirt_edilebiliyor",
            "Dort_hal_telde_ayri_gorunuyor",
        ],
        yesil_kalmali=["Damga_gerekceyi_ucun_kendisinden_aliyor"],
    ),
    Kusur(
        # YAZMA yolu: damga kurulabiliyor, tel taşıyabiliyor, ama arada satıra
        # yazan satır yoksa kayıt boş kalıyor ve hiçbir şey kırmızı yanmıyor.
        ad="damga satıra hiç yazılmıyor",
        dosya="src/Bizigo.Rca/RcaAdmission.cs",
        bul="        run.ModelBoundaryOverrideReason = stamp.Reason;",
        koy="        // KIRMIZI: gerekçe satıra yazılmıyor",
        kirmizi_bekleniyor=["TryStart_damgayi_satira_yaziyor"],
        # Tel tarafı yeşil kalıyor — ve kalması gerekiyor: tel doğru çalışıyor,
        # ona verilecek bir şey yok. İki bekçinin ayrı olmasının sebebi bu.
        yesil_kalmali=["Muafiyet_gerekcesi_telde_birebir"],
    ),
    Kusur(
        # Damga tipinin garantisi kalkıyor: public bir yapıcı, gerekçesiz bir
        # `Overridden` damgasını yeniden KURULABİLİR yapıyor. Kırmızısı bir
        # davranış testi değil, tipin şeklini ölçen kapı — çünkü iddia da tipin
        # şekli hakkında.
        ad="damga tipine public yapıcı açılıyor",
        dosya="src/Bizigo.Rca/RcaModelBoundaryStamp.cs",
        bul="    private RcaModelBoundaryStamp(RcaModelBoundary boundary, string? reason)",
        koy="    public RcaModelBoundaryStamp(RcaModelBoundary boundary, string? reason)",
        kirmizi_bekleniyor=["Damganin_gecersiz_hali_tip_duzeyinde_ulasilamaz"],
        yesil_kalmali=["Damga_gerekceyi_ucun_kendisinden_aliyor"],
    ),
    Kusur(
        # Parametreye varsayılan vermek damgayı yeniden UNUTULABİLİR yapıyor:
        # model yolu bağlandığı gün çağıran hiçbir şey değiştirmeden derlenir ve
        # kayıt sessizce `Unspecified` kalır.
        #
        # ÖLÇÜLDÜ: bu kusur DERLEMEYİ KIRMIYOR — bugünkü tek çağıran damgayı
        # açıkça geçiyor, dolayısıyla imza gevşetildiğinde her şey derleniyor ve
        # bütün testler yeşil kalıyor. Kapının bu yüzden bir yansıma iddiası
        # olması gerekiyordu; ilk tasarımda `derleme_kirilmali=True` yazılmıştı
        # ve o hâlde ölçüm hiçbir şey ölçmeyecekti.
        ad="damga parametresi isteğe bağlı hâle geliyor",
        dosya="src/Bizigo.Rca/RcaAdmission.cs",
        bul="        RcaModelBoundaryStamp stamp,",
        koy="        RcaModelBoundaryStamp? stamp = null,",
        kirmizi_bekleniyor=["Damganin_gecersiz_hali_tip_duzeyinde_ulasilamaz"],
        yesil_kalmali=["TryStart_damgayi_satira_yaziyor"],
    ),
]


if __name__ == "__main__":
    sys.exit(olc(KUSURLAR, ".t54-olcum-yedek"))
