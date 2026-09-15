#!/usr/bin/env python3
"""M18 · §6 kırmızı ölçümü — `ScopedQueryConsumerTests` kırmızı yanabiliyor mu.

Desen M10–M16'dan: `shutil.copy` (copy2 DEĞİL — zaman damgası değişmezse MSBuild
derlemeyi atlar ve ölçüm hiç olmaz), `finally` içinde geri yükleme, sonda ağaçta
`KIRMIZI-` grep'i, ve kırmızı/yeşil ölçütü **çıkış kodu** (yerelleştirilmiş çıktı
ayrıştırılmıyor).

Bu turun özel sorusu: bekçi elle tutulan bir listenin yerini aldı. O yüzden iki
yönü de ölçüyorum — kümenin BÜYÜMESİ ve KÜÇÜLMESİ. Küçülme asıl olan: bir katman
kapıyı kullanmayı bırakırsa (atlamaya başlarsa) yanmalı.
"""

from __future__ import annotations

import shutil
import subprocess
import sys
from dataclasses import dataclass, field
from pathlib import Path

KOK = Path(__file__).resolve().parent.parent

BEKCI = "ScopedQueryConsumerTests"

TEST = KOK / "tests/Bizigo.UnitTests/ScopedQueryConsumerTests.cs"
BELGE = KOK / "src/Bizigo.Query/IScopedQuery.cs"


@dataclass
class Kusur:
    ad: str
    dosya: Path
    eski: str
    yeni: str
    kirmizi_bekleniyor: bool = True
    not_: str = ""
    yesil_kalmali: list[str] = field(default_factory=list)


KUSURLAR = [
    # ---- KÜME KÜÇÜLMESİ: asıl yön ----
    Kusur(
        ad="KIRMIZI-kume-kuculdu-Alerting-kapiyi-birakti",
        dosya=TEST,
        eski='        // Alarm motoru: kural değerlendirmesi kapsamlı sayım yapıyor.\n        "Bizigo.Alerting",',
        yeni='        // KIRMIZI-kume-kuculdu: Alerting kapıyı kullanmayı bıraktı gibi davran.',
        not_="Beyandan bir ad düşerse türetilen küme fazla eleman taşır ve yanmalı. "
        "Bu, gerçekte bir katmanın kapıyı atlamaya başlamasının aynadaki hâli.",
    ),
    Kusur(
        ad="KIRMIZI-kume-buyudu-uydurma-tuketici",
        dosya=TEST,
        eski='        "Bizigo.Mcp.Product",\n    ];',
        yeni='        "Bizigo.Mcp.Product",\n        "Bizigo.Replay",  // KIRMIZI-kume-buyudu: tüketmiyor.\n    ];',
        not_="Beyan gerçekte tüketmeyen bir derlemeyi sayarsa yanmalı — yoksa "
        "beyan bir dilek listesi olur.",
    ),
    # ---- ÖLÇÜM ARACININ KENDİSİ ----
    Kusur(
        ad="KIRMIZI-arac-her-zaman-true",
        dosya=TEST,
        eski="        var metadata = reader.GetMetadataReader();",
        yeni="        if (assembly is not null) { return true; }  // KIRMIZI-arac-her-zaman-true\n"
        "        var metadata = reader.GetMetadataReader();",
        not_="Kapı ayırt etmeyi bırakırsa `Olcum_araci...` yanmalı. Bu, M16'nın "
        "dersinin bu turdaki hâli: bekçinin kendisi de ölçülmeli.",
    ),
    Kusur(
        ad="KIRMIZI-arac-her-zaman-false",
        dosya=TEST,
        eski="        foreach (var handle in metadata.TypeReferences)",
        yeni="        if (assembly is not null) { return false; }  // KIRMIZI-arac-her-zaman-false\n"
        "        foreach (var handle in metadata.TypeReferences)",
        not_="Ters yön: hiçbir şeyi tanımayan bir kapı, boş bir kümeyi 'beyanla "
        "aynı' bulamaz ve iki test de yanmalı.",
    ),
    Kusur(
        ad="KIRMIZI-arac-yorumlari-da-sayar",
        dosya=TEST,
        eski="        foreach (var handle in metadata.TypeReferences)\n        {\n"
        "            if (string.Equals(\n"
        "                metadata.GetString(metadata.GetTypeReference(handle).Name),\n"
        "                nameof(IScopedQuery),\n"
        "                StringComparison.Ordinal))",
        yeni="        // KIRMIZI-arac-yorumlari-da-sayar: meta veri yerine ham bayta bak.\n"
        "        if (File.ReadAllBytes(assembly.Location) is { } bytes\n"
        "            && System.Text.Encoding.ASCII.GetString(bytes).Contains(nameof(IScopedQuery), StringComparison.Ordinal))\n"
        "        {\n"
        "            return true;\n"
        "        }\n\n"
        "        foreach (var handle in metadata.TypeReferences)\n        {\n"
        "            if (string.Equals(\n"
        "                metadata.GetString(metadata.GetTypeReference(handle).Name),\n"
        "                nameof(IScopedQuery),\n"
        "                StringComparison.Ordinal))",
        not_="M18'in İLK ölçümünün yaptığı hatanın kendisi: metinde adı bulmak. "
        "Bu hâlde `Bizigo.Query` (tipi TANIMLAYAN) de kümeye girer ve yanmalı. "
        "Yani bekçi, kendisini yazan kişinin hatasına karşı da kapalı.",
    ),
    # ---- KEŞİF ----
    Kusur(
        ad="KIRMIZI-kesif-test-derlemelerini-de-alir",
        dosya=TEST,
        eski='            if (name.Contains("Tests", StringComparison.Ordinal)',
        yeni='            if (false  // KIRMIZI-kesif-test-derlemelerini-de-alir\n'
        '                || string.Equals(name, "__yok__", StringComparison.Ordinal)',
        not_="Test derlemesi kümeye girerse (kendisi `IScopedQuery` tanıyor) "
        "beyan onu saymadığı için yanmalı — keşif filtresi gerçekten çalışıyor mu.",
    ),
    # ---- YEŞİL KALMALI: belge metni bekçinin öznesi değil ----
    Kusur(
        ad="belge-metnini-boz",
        dosya=BELGE,
        eski="/// <b>Kural, liste değil:</b>",
        yeni="/// <b>KIRMIZI-belge-metni-bozuldu:</b>",
        kirmizi_bekleniyor=False,
        not_="Bekçi kod ölçüyor, belge metnini değil. Yanarsa bekçi yanlış şeye "
        "bakıyor demek. Belgenin doğruluğunu tutan şey bekçi DEĞİL — bu sınırın "
        "yazılı olması gerekiyor ve sınıf belgesinde duruyor.",
    ),
]


def kos(bekci: str) -> int:
    return subprocess.run(
        [
            "dotnet", "test", "tests/Bizigo.UnitTests",
            "--filter", f"FullyQualifiedName~{bekci}",
        ],
        cwd=KOK,
        capture_output=True,
        text=True,
    ).returncode


def main() -> int:
    print(f"=== M18 §6 ölçümü · {len(KUSURLAR)} kusur\n")

    taban = kos(BEKCI)
    if taban != 0:
        print(f"DUR: taban zaten kırmızı (çıkış {taban}). Ölçüm anlamsız.")
        return 1
    print("taban: YEŞİL ✓\n")

    basarisiz: list[str] = []

    for kusur in KUSURLAR:
        yedek = kusur.dosya.with_suffix(kusur.dosya.suffix + ".m18yedek")
        shutil.copy(kusur.dosya, yedek)

        try:
            metin = kusur.dosya.read_text(encoding="utf-8")
            if kusur.eski not in metin:
                print(f"  ATLANDI (çapa yok): {kusur.ad}")
                basarisiz.append(f"{kusur.ad}: çapa bulunamadı")
                continue

            kusur.dosya.write_text(metin.replace(kusur.eski, kusur.yeni, 1), encoding="utf-8")

            # §6: kusurun DOSYADA olduğunu geri okuyarak doğrula.
            geri = kusur.dosya.read_text(encoding="utf-8")
            if kusur.yeni.strip().splitlines()[0] not in geri:
                print(f"  ATLANDI (kusur yazılamadı): {kusur.ad}")
                basarisiz.append(f"{kusur.ad}: kusur dosyada değil")
                continue

            cikis = kos(BEKCI)
            kirmizi = cikis != 0

            if kirmizi == kusur.kirmizi_bekleniyor:
                isaret = "KIRMIZI ✓" if kirmizi else "YEŞİL ✓ (beklendiği gibi)"
                print(f"  {isaret}  {kusur.ad}")
            else:
                beklenen = "kırmızı" if kusur.kirmizi_bekleniyor else "yeşil"
                print(f"  BAŞARISIZ  {kusur.ad} — {beklenen} beklendi, çıkış {cikis}")
                basarisiz.append(kusur.ad)

            if kusur.not_:
                print(f"      {kusur.not_}")
        finally:
            shutil.copy(yedek, kusur.dosya)
            yedek.unlink()

    son = kos(BEKCI)
    print(f"\ngeri yükleme sonrası: {'YEŞİL ✓' if son == 0 else f'KIRMIZI ✗ (çıkış {son})'}")

    kalinti = subprocess.run(
        ["grep", "-rn", "KIRMIZI-", "src/", "tests/"],
        cwd=KOK, capture_output=True, text=True,
    ).stdout.strip()
    print(f"ağaçta KIRMIZI- kalıntısı: {'YOK ✓' if not kalinti else 'VAR ✗'}")
    if kalinti:
        print(kalinti[:600])

    if basarisiz:
        print(f"\n{len(basarisiz)} kalem başarısız:")
        for ad in basarisiz:
            print(f"  - {ad}")
        return 1

    print(f"\nTÜMÜ GEÇTİ · {len(KUSURLAR)}/{len(KUSURLAR)}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
