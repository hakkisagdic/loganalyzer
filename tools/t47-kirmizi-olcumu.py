#!/usr/bin/env python3
"""T47 — cümle bölme düzeltmesinin kırmızı ölçümü."""

from __future__ import annotations

import sys

from kirmizi_olcumu import Kusur, dotnet_kosumu, olc

BINDER = "src/Bizigo.Rca/Reasoning/SentenceBinder.cs"

KUSURLAR = [
    Kusur(
        ad="kısaltma koruması kaldırılıyor",
        dosya=BINDER,
        bul=r'@"(?<=[.!?])(?<!\b(?:" + Abbreviations + @")\.)(?<!(?m:^)[ \t]*\d{1,3}\.)\s+|\r?\n+",',
        koy=r'@"(?<=[.!?])(?<!(?m:^)[ \t]*\d{1,3}\.)\s+|\r?\n+",',
        kirmizi_bekleniyor=["Kisaltma_cumleyi_artik_bolmuyor"],
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
        ad="satır sonu sınırı kaldırılıyor",
        dosya=BINDER,
        bul=r'\s+|\r?\n+",',
        koy=r'\s+",',
        kirmizi_bekleniyor=["Maddeler_arasi_sinir_duruyor"],
    ),
    Kusur(
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
