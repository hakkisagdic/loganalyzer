"""M14 — `rca.trigger` / `rca.runs` bekçilerinin KIRMIZI YANABİLDİĞİNİ ölçen araç.

Yordam `m13-kirmizi-olcumu.py`'den devralındı.

M14'ün kusurları iki eksende ve `yesil_kalmali` ikisini ayırıyor:

  * YAZMA SÖZLEŞMESİ — anahtar sunucudan mı, kapsam genişletilebiliyor mu,
    araç yazma olarak mı ilan ediliyor, ve muafiyet sayılı mı.
  * T46 SADAKATİ — üç yönlü ayrım olduğu gibi mi taşınıyor.

Bir eksendeki kusur diğerini düşürmemeli; düşürüyorsa iki bekçi aynı şeyi
ölçüyor demektir ve biri gereksiz (§9).
"""

from __future__ import annotations

import os
import shutil
import subprocess
import sys
from dataclasses import dataclass, field
from pathlib import Path

KOK = Path(__file__).resolve().parent.parent
YEDEK = KOK / ".m14-olcum-yedek"

TRIGGER = "src/Bizigo.Mcp.Product/Tools/RcaTriggerTool.cs"
RUNS = "src/Bizigo.Mcp.Product/Tools/RcaRunsTool.cs"
WRITE = "src/Bizigo.Mcp.Product/ProductWriteTool.cs"


@dataclass
class Kusur:
    ad: str
    dosya: str
    bul: str
    koy: str
    kirmizi_bekleniyor: list[str] = field(default_factory=list)
    yesil_kalmali: list[str] = field(default_factory=list)
    not_: str = ""


ANAHTAR = "Rca_trigger_anahtari_sunucudan_ureiyor_ve_idempotent"
SEMA = "Rca_trigger_semasi_idempotency_argumani_kabul_etmiyor"
KAPSAM = "Rca_trigger_kapsam_disi_gruba_yazmiyor"
YAZMA_ILAN = "Rca_trigger_yazma_olarak_ilan_ediliyor"
RED_SATIR = "Rca_runs_reddedilen_satiri_gizlemiyor"
BOS = "Rca_runs_hic_tetiklenmemis_icin_bos_liste"
RUNS_KAPSAM = "Rca_runs_baska_grubun_kosumunu_dondurmuyor"
TABAN = "Urun_yuzeyindeki_her_arac_kapsam_tabanindan_turuyor"
MUAFIYET = "Yazma_cagiran_urun_araclari_sayili_muafiyette"


KUSURLAR = [
    Kusur(
        # M05'in bütün gerekçesinin tersi: anahtar hiç verilmiyor, yani her
        # çağrı yeni koşum ve model kotayı defalarca yiyor.
        ad="idempotency anahtarı hiç verilmiyor",
        dosya=TRIGGER,
        bul="                IdempotencyKey = McpIdempotency.KeyFor(scope.Subject, [ownerGroup], from, to),",
        koy="                IdempotencyKey = null,   // KIRMIZI-A",
        kirmizi_bekleniyor=[ANAHTAR],
        yesil_kalmali=[SEMA, KAPSAM, RED_SATIR],
        not_="şema bekçisi bu kusuru GÖRMÜYOR — argüman yokluğu anahtarın varlığını kanıtlamıyor",
    ),
    Kusur(
        # Daha sinsi: anahtar VAR ama sunucunun fonksiyonundan değil.
        ad="anahtar `KeyFor` yerine elde üretiliyor",
        dosya=TRIGGER,
        bul="                IdempotencyKey = McpIdempotency.KeyFor(scope.Subject, [ownerGroup], from, to),",
        koy='                IdempotencyKey = $"mcp:{Guid.NewGuid()}",   // KIRMIZI-B',
        kirmizi_bekleniyor=[ANAHTAR],
        yesil_kalmali=[SEMA, YAZMA_ILAN],
        not_="anahtar var ama her çağrıda farklı — bekçinin DEĞERİ karşılaştırmasının gerekçesi",
    ),
    Kusur(
        ad="kapsam kontrolü düşüyor: başka grubun adına yazılıyor",
        dosya=TRIGGER,
        # ŞEKİL ÖLÇÜLEREK DÜZELTİLDİ: `if (false)` ulaşılamaz kod üretiyor
        # (CS0162, bu depoda hata) ve ölçülen şey bekçi değil benim sözdizimim
        # olurdu. Derleyicinin sabit olarak çözemediği bir koşul gerekiyor.
        bul="        if (!scope.Allows(ownerGroup))",
        koy="        if (!scope.Allows(ownerGroup) && ownerGroup.Length < 0)   // KIRMIZI-C",
        kirmizi_bekleniyor=[KAPSAM],
        yesil_kalmali=[ANAHTAR, RED_SATIR],
    ),
    Kusur(
        ad="araç okuma gibi ilan ediliyor (`ReadOnlyHint` yalan söylüyor)",
        dosya=WRITE,
        bul="    public sealed override bool IsReadOnly => false;",
        koy="    public sealed override bool IsReadOnly => true;   // KIRMIZI-D",
        kirmizi_bekleniyor=[YAZMA_ILAN],
        yesil_kalmali=[ANAHTAR, KAPSAM],
        not_="kota tüketen bir aracı 'serbestçe deneyebilirsin' diye ilan etmek",
    ),
    Kusur(
        # Yazma tabanı kendi kapsam kontrolünü yazıyor: ikinci kapı.
        ad="yazma tabanı ortak kapsam statiğini çağırmıyor",
        dosya=WRITE,
        # ŞEKİL ÖLÇÜLEREK DÜZELTİLDİ: ilk hâl ternary'yi `is { }` ile
        # birleştiriyordu ve öncelik yüzünden derlenmiyordu. Çok satırlı çapa,
        # ortak statiği çağırmayan İKİNCİ BİR KAPI yazıyor — bekçinin ölçtüğü şey.
        bul="        if (ProductReadTool.ScopeRejection(scope, readsScopedData: true) is { } rejection)\n"
            "        {\n"
            "            return ValueTask.FromResult(McpToolResult.Failure(rejection));\n"
            "        }",
        koy="        if (scope.IsEmpty)   // KIRMIZI-E\n"
            "        {\n"
            "            return ValueTask.FromResult(McpToolResult.Failure(\n"
            "                new McpToolError(McpToolError.NotFound, \"boş kapsam\")));\n"
            "        }",
        kirmizi_bekleniyor=[TABAN],
        yesil_kalmali=[ANAHTAR, MUAFIYET],
        not_="§9: tek kapı iki çağıran — ikinci kapı yazıldığında taban bekçisi görüyor",
    ),
    Kusur(
        ad="reddedilen satırlar yükten gizleniyor",
        dosya=RUNS,
        bul="        var rows = await query",
        koy="        query = query.Where(r => r.State != RcaRunState.Rejected);   // KIRMIZI-F\n"
            "        var rows = await query",
        kirmizi_bekleniyor=[RED_SATIR],
        yesil_kalmali=[BOS, RUNS_KAPSAM],
        not_="boş listenin 'hiç tetiklenmedi' garantisi reddin satır olmasına bağlı",
    ),
    Kusur(
        # T46'nın cümlesi yerine MCP'ye özel bir metin: ikinci gösterim.
        ad="`reason` için ikinci bir cümle üretici yazılıyor",
        dosya=RUNS,
        bul="            RcaRunLifecycle.Describe(run.State, run.Rejection),",
        koy='            run.State == RcaRunState.Rejected ? "RCA yok" : "tamam",   // KIRMIZI-G',
        kirmizi_bekleniyor=[RED_SATIR],
        yesil_kalmali=[BOS, RUNS_KAPSAM],
        not_="MCP'ye özel gösterim — motorun gerekçesiyle modelin gerekçesi ayrışır",
    ),
    Kusur(
        ad="`rca.runs` kapsam filtresi düşüyor",
        dosya=RUNS,
        bul="            var groups = scope.OwnerGroups.ToArray();\n            query = query.Where(r => groups.Contains(r.OwnerGroup));",
        koy="            // KIRMIZI-H: kapsam filtresi düştü",
        kirmizi_bekleniyor=[RUNS_KAPSAM],
        yesil_kalmali=[RED_SATIR, BOS],
    ),
]


def kos(argv: list[str]) -> subprocess.CompletedProcess[str]:
    ortam = dict(os.environ)
    ortam["DOTNET_ROOT"] = str(Path.home() / ".dotnet")
    ortam["PATH"] = f"{Path.home() / '.dotnet'}:{ortam.get('PATH', '')}"

    return subprocess.run(
        argv, cwd=KOK, env=ortam, capture_output=True, text=True, check=False)


def yedekle(yollar: set[str]) -> None:
    YEDEK.mkdir(exist_ok=True)

    for yol in yollar:
        # `copy2` DEĞİL: zaman damgasını taşımak geri almayı derlemeye
        # ulaştırmıyor (T44'te ölçüldü).
        shutil.copy(KOK / yol, YEDEK / yol.replace("/", "__"))


def geri_al(yollar: set[str]) -> None:
    for yol in yollar:
        shutil.copy(YEDEK / yol.replace("/", "__"), KOK / yol)
        (KOK / yol).touch()


def uygula(kusur: Kusur) -> None:
    yol = KOK / kusur.dosya
    metin = yol.read_text(encoding="utf-8")

    if kusur.bul not in metin:
        raise SystemExit(f"[{kusur.ad}] ÇAPA BULUNAMADI: {kusur.dosya}. Ölçüm durduruluyor.")

    yol.write_text(metin.replace(kusur.bul, kusur.koy, 1), encoding="utf-8")
    yol.touch()


def iddia_et(kusur: Kusur) -> None:
    """§6'nın İDDİA ADIMI: kusur dosyada gerçekten var mı."""
    metin = (KOK / kusur.dosya).read_text(encoding="utf-8")

    if kusur.koy not in metin:
        raise SystemExit(f"[{kusur.ad}] İDDİA DÜŞTÜ: kusur dosyada yok, ölçüm yalancı olurdu.")

    if kusur.bul not in kusur.koy and kusur.bul in metin:
        raise SystemExit(f"[{kusur.ad}] İDDİA DÜŞTÜ: eski metin hâlâ orada.")

    print(f"    iddia: kusur `{kusur.dosya}` içinde DOĞRULANDI")


def derle() -> tuple[bool, str]:
    sonuc = kos(["dotnet", "build", "--nologo"])
    return sonuc.returncode == 0, sonuc.stdout + sonuc.stderr


def test_kos(filtre: str) -> int:
    """Ölçüt ÇIKIŞ KODU: 0 yeşil, değilse kırmızı.

    Sayıları çıktı metninden ayıklamak yerel dile bağlı olurdu (`Başarısız:` ↔
    `Failed:`) ve ayıklama düştüğünde sessizce sıfır üretirdi.
    """
    return kos([
        "dotnet", "test", "tests/Bizigo.UnitTests", "--no-build", "--nologo", "--filter", filtre,
    ]).returncode


def main() -> int:
    dosyalar = {k.dosya for k in KUSURLAR}
    yedekle(dosyalar)

    print(f"yedek: {YEDEK}")
    print(f"{len(KUSURLAR)} kusur ölçülecek\n")

    rapor: list[tuple[str, str]] = []

    try:
        for kusur in KUSURLAR:
            print(f"[{kusur.ad}]")
            uygula(kusur)
            iddia_et(kusur)

            basarili, cikti = derle()

            if not basarili:
                hata = next(
                    (s.strip() for s in cikti.splitlines() if ": error " in s),
                    "(hata satırı okunamadı)")
                rapor.append((kusur.ad, f"DERLENMEDİ: {hata[:110]}"))
                print(f"    DERLENMEDİ: {hata[:110]}\n")
                geri_al({kusur.dosya})
                continue

            for test in kusur.kirmizi_bekleniyor:
                durum = "KIRMIZI ✓" if test_kos(f"FullyQualifiedName~{test}") != 0 else "YEŞİL KALDI ✗"
                rapor.append((f"{kusur.ad} → {test}", durum))
                print(f"    {test}: {durum}")

            for test in kusur.yesil_kalmali:
                durum = ("yeşil kaldı ✓ (bekçiler ayrı şey ölçüyor)"
                         if test_kos(f"FullyQualifiedName~{test}") == 0 else "KIRILDI ✗")
                rapor.append((f"{kusur.ad} → {test} [yeşil kalmalı]", durum))
                print(f"    {test}: {durum}")

            if kusur.not_:
                rapor.append((f"    ↳ {kusur.ad}", kusur.not_))

            print()
            geri_al({kusur.dosya})
    finally:
        # Geri alma `finally` içinde ve döngüden ÇIKARKEN koşuyor — M10'da bir
        # kabuk betiğinin `trap`'i çalıştı, geri aldı, ve döngü DEVAM EDİP bir
        # sonraki kusuru uyguladı. Çalışmış bir geri alma durmuş bir koşum
        # değildir.
        geri_al(dosyalar)
        shutil.rmtree(YEDEK, ignore_errors=True)

    print("\n=== geri alındı; kusur ARANIYOR (varsaymıyoruz) ===")

    kalan = [
        f"{yol}:{no}"
        for yol in dosyalar
        for no, satir in enumerate((KOK / yol).read_text(encoding="utf-8").splitlines(), 1)
        if "KIRMIZI-" in satir
    ]

    if kalan:
        print("  ✗ AĞAÇTA KUSUR KALDI: " + ", ".join(kalan))
        return 1

    print("  ✓ hiçbir dosyada `KIRMIZI-` izi yok")

    print("\n=== TAM PAKET yeniden koşuyor ===")

    basarili, _ = derle()

    if not basarili:
        print("GERİ ALMA DERLEMEYE ULAŞMADI.")
        return 1

    kod = test_kos("FullyQualifiedName!~SidecarLive")
    print("tam paket: " + ("YEŞİL" if kod == 0 else "KIRMIZI"))

    print("\n=== ÖZET ===")
    for ad, durum in rapor:
        print(f"  {ad}: {durum}")

    return 0 if kod == 0 else 1


if __name__ == "__main__":
    sys.exit(main())
