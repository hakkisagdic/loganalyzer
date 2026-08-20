"""Üç kutunun bekçileri (T30 · 3. ölçüm).

Araç ClickHouse gerektirmiyor, testleri de gerektirmiyor: sınıflandırma saf
metin işi ve doğruluğu canlı veriden bağımsız.

Ölçülen şey: **iki farklı sebebin tek sayıya indirgenmemesi.** `match_ratio`
düşükse sebep ya eşleme ya örneklem, ve ikisinin cevabı zıt — biri "eşlemeyi
düzelt", diğeri "örneklemi genişlet ya da paydadan düş".

    python3 test_explain_misses.py
"""

from __future__ import annotations

import sys

import explain_misses as em


def test_dizge_hic_yoksa_ABSENT() -> None:
    """Örneklem o deseni taşımıyorsa kural eşleşemez — ve bu ürünün kusuru DEĞİL."""
    verdict, swallowed, lines = em.classify("hicboylebirsey", "sample line one\nsample line two")

    assert verdict == em.ABSENT
    assert swallowed == []
    assert lines == 0


def test_sozcuk_sinirinda_varsa_PRESENT() -> None:
    """Desen gerçekten var; kural yine de eşleşmiyorsa suç eşlemede."""
    verdict, _, lines = em.classify("Reset", "%ASA-6-302014: Teardown TCP connection Reset-I")

    assert verdict == em.PRESENT
    assert lines == 1


def test_yalnizca_sozcuk_ICINDE_ise_SUBSTRING_ONLY() -> None:
    """**Bu testin karşılığı gerçek bir kuraldı ve EŞLEŞİYORDU.**

    `asa_teardown_rst` `message|contains: 'RST'` yazıyordu ve satır
    döndürüyordu. Ama ASA sıfırlamayı `Reset-I` diye yazıyor, `RST` diye hiç
    yazmıyor: kural örneklerdeki `first` ve `burst` sözcüklerinin İÇİNE denk
    geliyordu.

    Eşleşme sayısı yukarı, doğruluk sıfır. Ne derleme kapısı ne canlı koşum
    bunu söyleyebilir — sorgu koşar, satır döner, sayaç artar. Yalnızca
    eşleşen metnin kendisine bakınca görülüyor.
    """
    corpus = "drop rate-1 exceeded. Current burst rate is 0\nfirst attempt failed"

    verdict, swallowed, lines = em.classify("RST", corpus)

    assert verdict == em.SUBSTRING_ONLY
    assert "burst" in swallowed and "first" in swallowed
    assert lines == 2


def test_hem_icinde_hem_sinirinda_ise_PRESENT() -> None:
    """Bir kez bile kendi başına duruyorsa desen gerçekten var.

    Bekçinin ölçüsü: aşırı hevesli olsaydı `Reset` gibi meşru bir dizgeyi de
    `Reset-I` yüzünden şüpheli sayar ve gerçek kuralları gürültüye boğardı.
    """
    corpus = "Teardown ... Reset-I ...\nthe connection was Reset by peer"

    verdict, swallowed, _ = em.classify("Reset", corpus)

    assert verdict == em.PRESENT
    assert swallowed == []


def test_tire_sozcugu_bolmuyor() -> None:
    """`Reset-I` içinde `Reset` sınırda sayılmalı: tire bir sözcük ayırıcısı.

    Aksi hâlde ASA'nın kendi sözlüğüyle yazılmış DOĞRU bir kural şüpheli
    kutusuna düşerdi — ve o kutu iş kalemi üretiyor.
    """
    verdict, _, _ = em.classify("Reset", "Teardown TCP connection Reset-I")

    assert verdict == em.PRESENT


def test_kuralin_kutusu_EN_KOTU_dizgeden_geliyor() -> None:
    """`condition: selection` bir AND: tek bir dizge yoksa kural eşleşemez.

    İyimser tarafa yuvarlamak, eşleşmeyen bir kuralı "eşleme sorunu" diye
    raporlardı — yani örneklemin darlığını ürünün yetersizliği gibi gösterirdi.
    """
    report = em.RuleReport(name="x", product="asa")
    report.literals = [
        em.Literal("message", "contains", "Teardown", em.PRESENT),
        em.Literal("message", "contains", "hicyok", em.ABSENT),
    ]

    assert report.verdict == em.ABSENT

    report.literals[1].verdict = em.SUBSTRING_ONLY
    assert report.verdict == em.SUBSTRING_ONLY

    report.literals[1].verdict = em.PRESENT
    assert report.verdict == em.PRESENT


def test_sayilar_atlaniyor() -> None:
    """`dstport: 443` bir metin araması değil.

    Örnek satırında `443` dizgesini aramak `10443`, `4432` gibi her sayıya denk
    gelir ve ölçüm gürültüye boğulur. Port eşleşmesi kolonun kendisinde
    çözülüyor, burada değil.
    """
    rule = (
        "logsource:\n  product: asa\n"
        "detection:\n  selection:\n"
        "    dstport: 443\n"
        "    message|contains: 'Teardown'\n"
        "  condition: selection\n"
    )

    values = [item.value for item in em.rule_literals(rule)]

    assert values == ["Teardown"]


def test_liste_degerleri_teker_teker_okunuyor() -> None:
    """`|all` listesi AND, düz liste OR — ikisinde de her dizge ayrı ayrı aranmalı."""
    rule = (
        "logsource:\n  product: asa\n"
        "detection:\n  selection:\n"
        "    message|contains|all:\n"
        "      - 'Teardown'\n"
        "      - 'Reset'\n"
        "  condition: selection\n"
    )

    literals = em.rule_literals(rule)

    assert [item.value for item in literals] == ["Teardown", "Reset"]
    assert all(item.operator == "contains|all" for item in literals)


def test_yorum_satiri_deger_sanilmiyor() -> None:
    """Korpustaki kurallar yoğun yorumlu; yorumu dizge sanmak ölçümü boğar."""
    rule = (
        "logsource:\n  product: asa\n"
        "detection:\n  selection:\n"
        "    # `RST` kaldırıldı: ASA sıfırlamayı `Reset-I` diye yazıyor\n"
        "    message|contains: 'Reset'\n"
        "  condition: selection\n"
    )

    assert [item.value for item in em.rule_literals(rule)] == ["Reset"]


def test_gercek_ASA_orneginde_RST_yakalaniyor() -> None:
    """**Uçtan uca**: gerçek örnek dosyaya karşı, kurgu korpusa değil.

    Yukarıdaki testler sınıflandırıcıyı ölçüyor; bu, örneklerin gerçekten o
    şekilde olduğunu ölçüyor. İkisi ayrı: sınıflandırıcı doğru olup örnek
    dosyalar değişmiş olabilir ve o zaman ölçüm sessizce başka bir şey söyler.
    """
    root = em.repo_root()
    assert root is not None, "depo kökü bulunamadı"

    corpus = em.load_samples(root, "asa")
    assert corpus, "ASA altın örnekleri okunamadı"

    rst, swallowed, _ = em.classify("RST", corpus)
    reset, _, _ = em.classify("Reset", corpus)

    assert rst == em.SUBSTRING_ONLY, "RST kendi başına duruyorsa bu ölçüm eskimiş"
    assert "burst" in swallowed
    assert reset == em.PRESENT, "ASA `Reset` yazıyor; yazmıyorsa kural yanlış"




def test_kelime_hatasi_ORNEKLEM_boslugundan_ayriliyor() -> None:
    """**`absent` iki zıt şey olabilir ve cevapları zıt.**

    Ölçülmüş vaka: `fortigate_user_auth_fail` `status: 'failure'` arıyor,
    FortiGate örneği `status="failed"` yazıyor. İkisini de düz "yok" diye
    raporlamak, DÜZELTİLEBİLİR bir kelime hatasını ölçülemez bir örneklem
    boşluğu gibi gösterirdi — ve kapsam kararı ona göre verilirdi.

    `RST`/`Reset` ile aynı sınıf: kural vendor'ın sözlüğünü değil kendi
    sözlüğünü kullanıyor.
    """
    corpus = 'type="event" status="failed" user="bob"'

    assert em.classify("failure", corpus)[0] == em.ABSENT
    assert em.near_misses("failure", corpus) == ["failed"]


def test_gercek_olmayan_senaryo_yakin_sozcuk_uretmiyor() -> None:
    """Bekçinin ölçüsü: her 'yok'u kelime hatası ilan etseydi gürültü olurdu."""
    corpus = '198.51.100.13 - - [13/Aug/2026:01:02:17] "GET /test1 HTTP/1.1" 404'

    assert em.near_misses("sqlmap", corpus) == []
    assert em.near_misses("/admin", corpus) == []


def test_kisa_dizge_yakinlik_aranmiyor() -> None:
    """Üç harfli bir dizgenin öneki her şeye benzer; ölçüm gürültüye boğulurdu."""
    assert em.near_misses("RST", "burst first reset") == []




def test_yapisal_alanda_onek_eslesmesi_ANLAM_gurultu_degil() -> None:
    """**Kutu 2 bir yanlış pozitif üretti ve sezgisel bu yüzden daraltıldı.**

    `fortigate_admin_from_wan` `srcip|startswith: '203.0.113.'` yazıyor ve
    `203.0.113.7` ile eşleşiyor — tam istenen şey. Ham gövdede bakınca "daha
    uzun bir sözcüğün içinde" görünüyor, çünkü IP'lerde nokta sözcük sınırı
    değil.

    Ayrım alanın TÜRÜNDE: serbest metinde içinde-geçmek gürültü, yapısal bir
    alanda önek eşleşmesi anlam. Yapısal alan zaten kolonun kendisinde
    karşılaştırılıyor.
    """
    corpus = "srcip=203.0.113.7 srcport=443 dstip=10.1.100.11"

    assert em.classify("203.0.113.", corpus, free_text=False)[0] == em.PRESENT
    # Serbest metin sayılsaydı şüpheli kutusuna düşerdi — eski davranış.
    assert em.classify("203.0.113.", corpus, free_text=True)[0] == em.SUBSTRING_ONLY


def test_serbest_metin_alanlari_URUNDEN_tureiyor() -> None:
    """Elle yazılsaydı `message` başka bir kolona gittiği gün sessizce ayrışırdı."""
    fields = em.free_text_fields()

    assert "message" in fields
    # Yapısal alanlar listede OLMAMALI: olsalardı kutu 2 onlarda da çalışır
    # ve bugünkü yanlış pozitifi geri getirirdi.
    assert "srcip" not in fields
    assert "action" not in fields


def test_kapali_deger_uzayindaki_deger_ABSENT_degil() -> None:
    """**Bu kör nokta doğru bir kuralı bozdu ve ölçüldükten sonra geri alındı.**

    `fortigate_user_auth_fail` `status: 'failure'` arıyor. Ham FortiGate satırı
    `status="failed"` yazıyor, dolayısıyla metin ekseni `failure`'ı bulamıyor
    ve "örneklem boşluğu" diyor.

    Ama `catalog/mappings/auth_outcome.yaml` ingest sırasında
    `failed → failure` ÇEVİRİYOR: kolonda duran değer `failure`. Kural baştan
    doğruydu ve `failed`'a çevrilmesi onu kolonun hiç taşımadığı bir değere
    bağladı.

    Yani `absent` kutusu bir ÜST SINIR: her elemanı örneklem boşluğu değil.
    """
    root = em.repo_root()
    spaces = em.column_value_spaces(root)

    assert "status" in spaces, "`status` kolonunun değer uzayı çözülemedi"
    assert "failure" in spaces["status"]
    # Cihazın kendi sözcüğü ANAHTAR, kolona yazılan DEĞER. Kural değeri arıyor.
    assert "failed" not in spaces["status"]

    report = em.examine(
        "logsource:\n  product: fortigate\n"
        "detection:\n  selection:\n    status: 'failure'\n  condition: selection\n",
        "x.yml",
        'type="event" status="failed" user="admin"',
        "fortigate",
        frozenset(),
        spaces,
        {"status": "status"},
    )

    assert report.verdict == em.PRESENT
    assert report.literals[0].in_value_space


def test_deger_uzayi_bulunamayan_alan_ABSENT_kaliyor() -> None:
    """Bekçinin ölçüsü: her `absent`'i "çevrilmiştir" saysaydı kutu boşalırdı.

    `url` bir kapalı uzay taşımıyor — serbest bir dizge. Örneklerde yoksa
    gerçekten yok.
    """
    root = em.repo_root()
    spaces = em.column_value_spaces(root)

    report = em.examine(
        "logsource:\n  product: nginx\n"
        "detection:\n  selection:\n    url|contains: '/admin'\n  condition: selection\n",
        "y.yml",
        '198.51.100.13 - - "GET /test1 HTTP/1.1" 404',
        "nginx",
        frozenset(),
        spaces,
        {"url": "unmapped['otel.url.path']"},
    )

    assert report.verdict == em.ABSENT
    assert not report.literals[0].in_value_space


def test_deger_uzaylari_UC_kaynaktan_zincirleniyor() -> None:
    """Zincir elle yazılmıyor: görünüm → parser → sözlük. Üçü de tek kaynak.

    Elle yazılsaydı yeni bir sözlük eklendiği gün sessizce eksik kalırdı — bu
    depoda elle liste dört kez patladı.
    """
    spaces = em.column_value_spaces(em.repo_root())

    # `outcome AS status` (görünüm) + `outcome: {table: auth_outcome}` (parser)
    assert "failure" in spaces["status"] and "success" in spaces["status"]
    # `proto AS connection_info_protocol_name` + `ip_proto_name`
    assert "tcp" in spaces["connection_info_protocol_name"]


def main() -> int:
    """Koşucu.

    **Dosyadaki `def test_` sayısı ile koşulan sayı karşılaştırılıyor** ve bu
    bir kolaylık değil bir bekçi: koşucu dosyanın sonunda olmadığı için
    altına eklenen testler `globals()` dolduğunda henüz tanımlı değildi ve
    **hiç koşmadan** paket yeşil kalıyordu. Bu, iki kez oldu — ikisinde de
    sayıya bakıp geçilebilirdi.

    §7'nin deseni: bir bekçinin sessizce atlaması, bekçinin kendisinden
    tehlikeli.
    """
    import re
    from pathlib import Path

    tests = [value for name, value in sorted(globals().items()) if name.startswith("test_")]
    declared = len(re.findall(r"^def (test_\w+)", Path(__file__).read_text(encoding="utf-8"), re.M))

    if declared != len(tests):
        print(
            f"✗ KOŞUM EKSİK: dosyada {declared} test tanımlı, {len(tests)} tanesi toplandı.\n"
            "  Koşucu dosyanın SONUNDA değil; altına eklenen testler hiç koşmuyor.",
            file=sys.stderr,
        )
        return 1

    failed = 0

    for test in tests:
        try:
            test()
            print(f"✓ {test.__name__}")
        except AssertionError as exc:
            failed += 1
            print(f"✗ {test.__name__}: {exc or 'assertion failed'}")

    print(f"\n{len(tests) - failed}/{len(tests)} geçti")

    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
