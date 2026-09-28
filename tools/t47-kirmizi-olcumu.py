#!/usr/bin/env python3
"""T47 — cümle bölme düzeltmesinin bekçilerinin KIRMIZI yanabildiğinin ölçümü.

Koşum yordamı ortak (`kirmizi_olcumu`); burada kalan tek şey kusur listesi.

**A/B çifti bu ölçümün kalbi.** İki koruma ayrı ayrı eklendi ve ayrı ayrı
ölçülüyor: kısaltma korumasını düşürmek numaralı liste testini kırmızı
yakmamalı, ve tersi. Tek bir kusur ölçmek *"iki koruma tek sezgiye
indirilebilirdi"* iddiasını çürütemezdi — oysa ölçüm tam olarak onu çürütüyor
(`1. Kök` büyük harfle devam ediyor, küçük harf sezgisi onu kapatmıyor).

Üçüncü ve dördüncü kusur **ters yönü** ölçüyor: düzeltmenin fazla ileri gitmesi.
Bir koruma satır sonunu da yutarsa bütün liste tek cümle olur ve oran ters yöne
kayar — atıfsız maddeler atıflı bir maddenin arkasına saklanır.

    python3 tools/t47-kirmizi-olcumu.py
"""

from __future__ import annotations

import sys

from kirmizi_olcumu import Kusur, olc, vitest_kosumu  # noqa: F401  (vitest bu ticket'ta kullanılmıyor)
from kirmizi_olcumu import dotnet_kosumu

BINDER = "src/Bizigo.Rca/Reasoning/SentenceBinder.cs"

KUSURLAR = [
    Kusur(
        ad="kısaltma koruması kaldırılıyor",
        dosya=BINDER,
        bul=r'@"(?<=[.!?])(?<!\b(?:" + Abbreviations + @")\.)(?<!(?m:^)[ \t]*\d{1,3}\.)\s+|\r?\n+",',
        koy=r'@"(?<=[.!?])(?<!(?m:^)[ \t]*\d{1,3}\.)\s+|\r?\n+",',
        kirmizi_bekleniyor=["Kisaltma_cumleyi_artik_bolmuyor"],
        # İKİ KORUMA AYRI: numaralı liste testi bu kusurdan etkilenmemeli.
        yesil_kalmali=["Numarali_liste_maddesi_artik_bolunmuyor"],
    ),
    Kusur(
        ad="numaralı liste koruması kaldırılıyor",
        dosya=BINDER,
        bul=r'@"(?<=[.!?])(?<!\b(?:" + Abbreviations + @")\.)(?<!(?m:^)[ \t]*\d{1,3}\.)\s+|\r?\n+",',
        koy=r'@"(?<=[.!?])(?<!\b(?:" + Abbreviations + @")\.)\s+|\r?\n+",',
        kirmizi_bekleniyor=["Numarali_liste_maddesi_artik_bolunmuyor"],
        yesil_kalmali=["Kisaltma_cumleyi_artik_bolmuyor"],
    ),
    Kusur(
        # TERS YÖN: düzeltme fazla ileri gidiyor ve satır sonu sınırını yutuyor.
        # Bütün liste tek cümle olur; atıfsız maddeler atıflı bir maddenin
        # arkasına saklanır ve oran YANLIŞ YÖNDE iyileşir.
        ad="satır sonu sınırı kaldırılıyor (fazla düzeltme)",
        dosya=BINDER,
        bul=r'\s+|\r?\n+",',
        koy=r'\s+",',
        kirmizi_bekleniyor=["Maddeler_arasi_sinir_duruyor"],
    ),
    Kusur(
        # ÖLÇÜM ARACININ KENDİSİ: bölme hiç çalışmazsa "bölmüyor" bekleyen
        # testlerin çoğu SESSİZCE geçer. Bu kusur o sessizliği ölçüyor.
        ad="bölme hiç eşleşmiyor",
        dosya=BINDER,
        bul=r'\s+|\r?\n+",',
        koy=r'\s{9}|\r?\n{9}",',
        kirmizi_bekleniyor=["Bolme_gercekten_boluyor"],
        yesil_kalmali=["Kisaltma_cumleyi_artik_bolmuyor"],
    ),
]


if __name__ == "__main__":
    sys.exit(olc(KUSURLAR, ".t47-olcum-yedek", dotnet_kosumu()))
