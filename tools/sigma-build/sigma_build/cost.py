"""Derleme maliyeti — süre kural sayısıyla nasıl büyüyor (T32).

Bu bir **ölçüm**, kapı değil
-----------------------------
Mutlak bir süre bütçesi konulmuyor. Gerekçe `CLAUDE.md` §6'da iki kez ödendi:
`GrokPropertyTests` 2 saniyelik bütçeyle **makineyi** ölçüyordu,
`DiscoveryWorkerTests` 200 ms'lik zaman aşımıyla ThreadPool doygunluğunu. Bir
süre bütçesi, kural setinin maliyetini değil koşturan makinenin o günkü yükünü
kapıya bağlar; ve o kapı ilk yoğun günde gevşetilir.

Kural sayısıyla büyümeyi **kapı** olarak koruyan şey ayrı ve saatsiz:
`tests/test_cost.py` kural başına karşılaştırma sayısını sabitliyor. Burada
ölçülen şey o sayının **kaç milisaniye** ettiği — ve o soru yalnızca sessiz bir
makinede cevaplanabiliyor.

Neden iki koşum kaydediliyor
----------------------------
K35'te aynı ölçüm ajanda 1,46×, koordinatörde 1,62× çıktı; ikinci koşumda
*yalnız ayrıştırma* kolu *ayrıştırma+etiketleme*'den **yavaş** göründü — fiziksel
olarak imkânsız, yani makine sessiz değildi. §6'nın kuralı bu yüzden "doğru sayıyı
seç" değil **"iki koşumu da kaydet"**: iki kayıt yan yana durduğunda okuyan
kişi sessizliğin ölçülüp ölçülmediğini görüyor; tek kayıt bunu gizliyor.

`--record` her koşumu bir deftere **ekliyor**, üstüne yazmıyor. Üstüne yazsaydı
ikinci koşum birincinin kanıtını siler ve defter "son koşum" olurdu — yani tam
olarak §6'nın engellemek istediği tek sayı.

Neden kurulum maliyeti ayrı ölçülüyor
--------------------------------------
Backend'in kurulması, görünüm kolonlarının göçlerden türetilmesi ve pySigma'nın
eklenti yüklemesi **kural sayısından bağımsız**. Toplam süreyi kural sayısına
bölmek, küçük korpusta kurulumu kural başına maliyet sanır ve sayı korpus
büyüdükçe **düşer** — okuyan kişi bunu "ölçekleme süper-doğrusal değil, tam
tersi" diye okur. Ölçülen şey ise yalnızca sabit bir maliyetin daha çok kurala
bölünmesidir.

Bu, bu deponun `%25` yerine `%43` bulduğu payda hatasının süre eksenindeki
kardeşi: yanlış olan sayı değil **bölen**.
"""

from __future__ import annotations

import argparse
import json
import os
import sys
import tempfile
import time
from dataclasses import asdict, dataclass, field
from pathlib import Path

__all__ = ["Measurement", "measure", "synthetic_rule", "main"]

#: Ölçülen korpus boyları. 24 bugünkü korpus; 269 çivinin belgelerde adı geçen
#: hedef ölçeği; aradaki 100 doğrusaldan sapmayı görünür kılıyor — iki nokta
#: her eğriye doğru bir çizgi uydurur, üçüncüsü uymadığını söyler.
VARSAYILAN_BOYLAR = (24, 100, 269)

#: Sayılmayan ısınma turu **şart**. `explain_gate.probe_forms` bu dersi ödedi:
#: ısınmasız ölçüm `EXPLAIN`'i kendisinin 2,3 katı gösterdi ve ölçülen şey biçim
#: değil listedeki sıraydı.
ISINMA_TURU = 1
VARSAYILAN_TUR = 3


@dataclass(frozen=True)
class Measurement:
    """Tek bir korpus boyunun ölçümü."""

    rules: int
    fields_per_rule: int
    #: Turların **en hızlısı**. Ortalama, arada geçen bir yavaşlamayı ölçüme
    #: katardı ve ölçtüğümüz şey makinenin o anki yükü değil hattın maliyeti.
    total_ms: float
    per_rule_ms: float
    compiled: int
    rejected: int


@dataclass
class RunRecord:
    """Bir koşumun tamamı — sayılar **ve** koşuldukları ortam.

    Ortam alanları süs değil: iki koşum ayrıştığında hangisinin bağlayıcı
    olduğunu söyleyen şey onlar.
    """

    label: str
    note: str
    setup_ms: float
    scaling: float | None
    load_average: list[float] | None
    python: str
    measurements: list[dict] = field(default_factory=list)


# --------------------------------------------------------------------------- #
# Sentetik korpus
# --------------------------------------------------------------------------- #

def synthetic_rule(index: int, fields: tuple[str, ...]) -> str:
    """Derlenebilir bir Sigma kuralı — alan adları **pipeline'dan** geliyor.

    Alanları elle yazmak, ölçümü T31'in eşleme tablosunun bugünkü hâline
    çivilerdi: eşleme değiştiği gün sentetik kurallar reddedilmeye başlar ve
    ölçüm sessizce **istisna yolunu** ölçer — derleme yolunu değil. İkisi farklı
    maliyet, ve fark hiçbir yerde görünmez.

    Kimlikler benzersiz: aynı kimliği tekrarlamak `build_manifest`'in tekrar
    kontrolünü tetikler ve ölçüm derlemeyi değil hata yolunu ölçer.
    """
    detection = "\n".join(f"    {name}: 'olcum-{index}-{sira}'" for sira, name in enumerate(fields))
    return (
        f"title: Ölçüm kuralı {index}\n"
        f"id: {index:08d}-0000-4000-8000-000000000000\n"
        "status: test\n"
        "logsource:\n"
        "  category: network_connection\n"
        "detection:\n"
        "  secim:\n"
        f"{detection}\n"
        "  condition: secim\n"
    )


def _mapped_fields(pipeline, count: int) -> tuple[str, ...]:
    """T31'in eşlediği alanlardan ilk `count` tanesi, **sıralı**.

    Sıra sabit olmalı: küme yinelemesi tur başına farklı alanlar seçseydi iki
    koşum farklı kuralları derler ve karşılaştırılamazdı.
    """
    names = sorted(getattr(pipeline, "FIELD_MAP", {}))
    if not names:
        raise RuntimeError(
            "Pipeline'ın `FIELD_MAP`'i boş — sentetik kurallar derlenemez. "
            "Ölçüm yapılmıyor: reddedilen kuralları ölçmek derleme maliyetini ölçmek değil."
        )
    if len(names) < count:
        raise RuntimeError(f"Eşlenmiş alan sayısı {len(names)}, istenen {count}.")
    return tuple(names[:count])


# --------------------------------------------------------------------------- #
# Ölçüm
# --------------------------------------------------------------------------- #

def _default_compiler():
    """Gerçek hat: T31'in pipeline'ı + ClickHouse backend'i.

    Kurulum burada bir kez yapılıyor ve süresi **ayrı** dönüyor; kural başına
    maliyetin paydası kurulum içermiyor (modül açıklaması).
    """
    from sigma_build.compile import MAPPINGS_DIR, _load_pipeline  # noqa: PLC0415
    from sigma_build.view_columns import repo_root  # noqa: PLC0415

    started = time.perf_counter()
    pipeline = _load_pipeline()
    backend = pipeline.bizigo_backend(mappings_path=repo_root() / MAPPINGS_DIR)
    from sigma.collection import SigmaCollection  # noqa: PLC0415

    setup_ms = (time.perf_counter() - started) * 1000

    def compile_one(text: str) -> bool:
        """True = derlendi, False = pipeline reddetti (ikisi de geçerli sonuç)."""
        try:
            backend.convert(SigmaCollection.from_yaml(text))
        except Exception:  # noqa: BLE001 — burada sınıflandırma değil süre ölçülüyor
            return False
        return True

    return compile_one, setup_ms, pipeline


def measure(
    *,
    sizes: tuple[int, ...] = VARSAYILAN_BOYLAR,
    fields_per_rule: int = 3,
    rounds: int = VARSAYILAN_TUR,
    compiler=None,
    field_names: tuple[str, ...] | None = None,
) -> tuple[list[Measurement], float]:
    """Her boyu ölçer ve (ölçümler, kurulum_ms) döndürür."""
    if compiler is None:
        compile_one, setup_ms, pipeline = _default_compiler()
        names = field_names or _mapped_fields(pipeline, fields_per_rule)
    else:
        compile_one, setup_ms = compiler, 0.0
        names = field_names or tuple(f"alan{i}" for i in range(fields_per_rule))

    results: list[Measurement] = []
    for size in sizes:
        rules = [synthetic_rule(i, names) for i in range(size)]

        # Isınma turu — SAYILMIYOR. pySigma eklentileri ve YAML ayrıştırıcısı
        # ilk çağrıda kendi önbelleklerini kuruyor; o bedeli ölçüm değil bu tur
        # ödüyor.
        for _ in range(ISINMA_TURU):
            for text in rules:
                compile_one(text)

        durations: list[float] = []
        compiled = rejected = 0
        for tur in range(rounds):
            compiled = rejected = 0
            started = time.perf_counter()
            for text in rules:
                if compile_one(text):
                    compiled += 1
                else:
                    rejected += 1
            durations.append(time.perf_counter() - started)

        best = min(durations)
        results.append(
            Measurement(
                rules=size,
                fields_per_rule=len(names),
                total_ms=round(best * 1000, 3),
                per_rule_ms=round(best * 1000 / size, 4),
                compiled=compiled,
                rejected=rejected,
            )
        )
    return results, round(setup_ms, 3)


def scaling_factor(results: list[Measurement]) -> float | None:
    """En büyük korpusun kural başına maliyeti ÷ en küçüğünkü.

    **Oran**, mutlak sayı değil — §6: *"mutlak bütçe yerine aynı süreçte alınan
    bir tabana oran"*. İki sayı da aynı süreçte, aynı dakikada alındığı için
    makinenin genel hızı ikisinden de sadeleşiyor; geriye yalnızca ölçekleme
    kalıyor.

    ≈1 → doğrusal. Belirgin biçimde >1 → kural başına maliyet korpusla büyüyor,
    yani toplam süper-doğrusal.
    """
    if len(results) < 2:
        return None
    kucuk = min(results, key=lambda m: m.rules)
    buyuk = max(results, key=lambda m: m.rules)
    if kucuk.per_rule_ms == 0:
        return None
    return round(buyuk.per_rule_ms / kucuk.per_rule_ms, 3)


def _load_average() -> list[float] | None:
    """Makinenin yükü — **eksikse `None`, sessizce atlanmıyor.**

    Bu bir vekil ölçü: §6'nın adını koyduğu bağlayıcı rakam saniyedeki swap-in
    sayısı ve o bu süreçten okunamıyor. Yük ortalaması yine de iki koşumun aynı
    koşullarda alınıp alınmadığını söyleyecek kadar bilgi taşıyor — ve `None`
    yazmak "bakmadık" ile "sıfırdı"yı ayırıyor.
    """
    try:
        return [round(v, 2) for v in os.getloadavg()]
    except (OSError, AttributeError):
        return None


def _append_record(path: Path, record: RunRecord) -> int:
    """Deftere **ekler**. Üstüne yazmak ikinci koşumun birinciyi silmesi olurdu."""
    entries = []
    if path.is_file():
        entries = json.loads(path.read_text(encoding="utf-8")).get("runs", [])
    entries.append(asdict(record))
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(
        json.dumps(
            {
                "_comment": (
                    "Derleme maliyeti defteri. Her koşum EKLENİR, üstüne yazılmaz — "
                    "iki koşumu yan yana görmek, sessizliğin ölçülüp ölçülmediğini "
                    "okuyana gösteren tek şey (CLAUDE.md §6, K35)."
                ),
                "runs": entries,
            },
            indent=2,
            ensure_ascii=False,
        )
        + "\n",
        encoding="utf-8",
    )
    return len(entries)


def _format(record: RunRecord) -> str:
    satirlar = [
        f"Kurulum (kural sayısından bağımsız): {record.setup_ms} ms",
        "",
        "| Kural | Alan/kural | Toplam (ms) | Kural başına (ms) | Derlendi | Reddedildi |",
        "| --- | --- | --- | --- | --- | --- |",
    ]
    for m in record.measurements:
        satirlar.append(
            f"| {m['rules']} | {m['fields_per_rule']} | {m['total_ms']} | "
            f"{m['per_rule_ms']} | {m['compiled']} | {m['rejected']} |"
        )
    satirlar.append("")
    if record.scaling is None:
        satirlar.append("Ölçekleme oranı hesaplanamadı (tek boy ölçüldü).")
    else:
        yorum = "doğrusal" if record.scaling <= 1.25 else "süper-doğrusal — kural başına maliyet korpusla büyüyor"
        satirlar.append(f"Ölçekleme oranı (en büyük ÷ en küçük, kural başına): **{record.scaling}×** — {yorum}")
    satirlar.append(f"Yük ortalaması: {record.load_average}  ·  Python {record.python}")
    if record.load_average is None:
        satirlar.append("⚠️ Yük okunamadı — bu koşumun sessiz bir makinede alındığı **gösterilemiyor**.")
    return "\n".join(satirlar)


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(
        description="Derleme süresinin kural sayısıyla nasıl büyüdüğünü ölçer (kapı değil, ölçüm)."
    )
    parser.add_argument("--label", default="isimsiz", help="Koşumu kim aldı (ör. koordinator)")
    parser.add_argument("--note", default="", help="Makinenin durumu hakkında serbest not")
    parser.add_argument("--sizes", default=",".join(str(s) for s in VARSAYILAN_BOYLAR))
    parser.add_argument("--fields-per-rule", type=int, default=3)
    parser.add_argument("--rounds", type=int, default=VARSAYILAN_TUR)
    parser.add_argument("--json", action="store_true", help="Markdown yerine ham JSON")
    parser.add_argument(
        "--record",
        type=Path,
        default=None,
        help="Koşumu bu deftere EKLE (üstüne yazmaz)",
    )
    args = parser.parse_args(argv)

    sizes = tuple(int(s) for s in args.sizes.split(",") if s.strip())
    results, setup_ms = measure(
        sizes=sizes, fields_per_rule=args.fields_per_rule, rounds=args.rounds
    )
    record = RunRecord(
        label=args.label,
        note=args.note,
        setup_ms=setup_ms,
        scaling=scaling_factor(results),
        load_average=_load_average(),
        python=sys.version.split()[0],
        measurements=[asdict(m) for m in results],
    )

    print(json.dumps(asdict(record), indent=2, ensure_ascii=False) if args.json else _format(record))

    if args.record:
        toplam = _append_record(args.record, record)
        print(f"\n✓ Deftere eklendi: {args.record} — toplam {toplam} koşum.")
        if toplam < 2:
            print(
                "  ⚠️ Defterde tek koşum var. Bağlayıcı sayı **iki** koşum ister; "
                "tek koşum, makinenin sessiz olup olmadığını göstermiyor (§6, K35)."
            )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
