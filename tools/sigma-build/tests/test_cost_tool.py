"""`sigma_build.cost`'un kendisi — **süre iddiası olmadan**.

Bu dosya aracın *sayılarını* sınamıyor; sayıları üretecek koşum koordinatörde,
sessiz makinede (`CLAUDE.md` §6). Sınanan şey aracın **doğru şeyi ölçtüğü**:
ısınma turu atılıyor mu, kural başına maliyetin paydası kurulum içeriyor mu,
defter ekliyor mu üstüne mi yazıyor.

Ayrım niye önemli: koşturulmamış bir araç, bu deponun adını koyduğu
*"hazırlanmış ama bağlanmamış"* sınıfına girer — `unmapped_expression()` yazılıp
hiç çağrılmamıştı ve 24 kuralın 8'i sessizce koşmuyordu. Aracın kod yolları
sahte bir derleyiciyle burada koşuyor; koşmayan tek şey duvar saati ölçümü, ve
o **bilinçli**.
"""

from __future__ import annotations

import json

from sigma_build.cost import (
    ISINMA_TURU,
    Measurement,
    RunRecord,
    _append_record,
    _format,
    measure,
    scaling_factor,
    synthetic_rule,
)


class SahteDerleyici:
    """Çağrı sayan, anında dönen derleyici. Ölçülen tek şey **kaç kez** çağrıldığı."""

    def __init__(self, reddedilen: set[str] | None = None) -> None:
        self.calls: list[str] = []
        self.reddedilen = reddedilen or set()

    def __call__(self, text: str) -> bool:
        self.calls.append(text)
        return text not in self.reddedilen


# --------------------------------------------------------------------------- #
# Isınma turu ve paydalar
# --------------------------------------------------------------------------- #

def test_isinma_turu_atiliyor_ve_sayilmiyor() -> None:
    """Isınma turu olmadan ölçülen şey biçim değil sıra olur.

    `explain_gate.probe_forms` bu dersi ödedi: ısınmasız ölçüm `EXPLAIN`'i
    `EXPLAIN PLAN`'in 2,3 katı gösterdi — çıplak `EXPLAIN` zaten `EXPLAIN PLAN`
    olduğu için fiziksel olarak imkânsız bir sonuç.

    Süreyi test edemiyoruz ama **çağrı sayısını** edebiliyoruz: toplam çağrı,
    ısınma turlarını da içermeli. İçermiyorsa ısınma hiç koşmuyor demektir.
    """
    sahte = SahteDerleyici()
    turlar = 3
    measure(sizes=(5,), rounds=turlar, compiler=sahte, field_names=("a", "b"))

    beklenen = 5 * (ISINMA_TURU + turlar)
    assert len(sahte.calls) == beklenen, (
        f"{beklenen} çağrı bekleniyordu, {len(sahte.calls)} oldu. "
        "Isınma turu atlanıyorsa ilk sayılan tur pySigma'nın eklenti yükleme "
        "bedelini de ölçer."
    )


def test_kural_basina_maliyetin_paydasi_kural_sayisi() -> None:
    sonuclar, _ = measure(sizes=(4,), rounds=1, compiler=SahteDerleyici(), field_names=("a",))
    (olcum,) = sonuclar
    assert olcum.rules == 4
    # Payda gerçekten kural sayısı: toplam ÷ kural = kural başına. Yuvarlama
    # farkı dışında eşit olmalılar; eşit değillerse payda başka bir şey.
    assert abs(olcum.per_rule_ms - olcum.total_ms / 4) < 1e-3


def test_sahte_derleyiciyle_kurulum_sifir_sayiliyor() -> None:
    """Kurulum maliyeti enjekte edilen derleyiciye ait değil — payda kirlenmesin."""
    _, setup_ms = measure(sizes=(2,), rounds=1, compiler=SahteDerleyici(), field_names=("a",))
    assert setup_ms == 0.0


def test_reddedilen_kural_ayri_sayiliyor() -> None:
    """`compiled` ile `rejected` ayrı: ikisi farklı kod yolu, farklı maliyet."""
    rules = {synthetic_rule(i, ("a",)) for i in range(3)}
    sahte = SahteDerleyici(reddedilen={synthetic_rule(1, ("a",))})
    sonuclar, _ = measure(sizes=(3,), rounds=1, compiler=sahte, field_names=("a",))
    (olcum,) = sonuclar
    assert (olcum.compiled, olcum.rejected) == (2, 1)
    assert len(rules) == 3


# --------------------------------------------------------------------------- #
# Sentetik korpus
# --------------------------------------------------------------------------- #

def test_sentetik_kurallarin_kimlikleri_benzersiz() -> None:
    """Tekrarlanan kimlik `build_manifest`'i hata yoluna sokar; ölçüm derlemeyi ölçmez."""
    kimlikler = {synthetic_rule(i, ("a",)).split("id: ")[1].splitlines()[0] for i in range(50)}
    assert len(kimlikler) == 50


def test_sentetik_kural_istenen_alan_sayisini_tasiyor() -> None:
    metin = synthetic_rule(0, ("src_ip", "dst_ip", "proto"))
    for alan in ("src_ip", "dst_ip", "proto"):
        assert f"    {alan}: " in metin


def test_sentetik_kural_yaml_olarak_ayristirilabiliyor() -> None:
    yaml = __import__("yaml")
    belge = yaml.safe_load(synthetic_rule(7, ("a", "b")))
    assert belge["id"].startswith("00000007-")
    assert belge["detection"]["condition"] == "secim"
    assert len(belge["detection"]["secim"]) == 2


# --------------------------------------------------------------------------- #
# Ölçekleme oranı
# --------------------------------------------------------------------------- #

def _olcum(rules: int, per_rule_ms: float) -> Measurement:
    return Measurement(
        rules=rules, fields_per_rule=3, total_ms=per_rule_ms * rules,
        per_rule_ms=per_rule_ms, compiled=rules, rejected=0,
    )


def test_dogrusal_hat_oran_bir_veriyor() -> None:
    assert scaling_factor([_olcum(24, 2.0), _olcum(269, 2.0)]) == 1.0


def test_karesel_hat_orani_buyuk_veriyor() -> None:
    """Kural başına maliyet korpusla büyüyorsa oran 1'in belirgin üstünde."""
    assert scaling_factor([_olcum(24, 2.0), _olcum(269, 8.0)]) == 4.0


def test_tek_olcumde_oran_yok() -> None:
    """`None` dönüyor — 1.0 dönmek 'ölçüldü ve doğrusal' diye okunurdu."""
    assert scaling_factor([_olcum(24, 2.0)]) is None


# --------------------------------------------------------------------------- #
# Defter — koordinatörün açık isteği: iki koşum da kalsın
# --------------------------------------------------------------------------- #

def _kayit(label: str, scaling: float | None = 1.0) -> RunRecord:
    return RunRecord(
        label=label, note="", setup_ms=1.0, scaling=scaling,
        load_average=[0.5, 0.5, 0.5], python="3.13.14",
        measurements=[{"rules": 24, "fields_per_rule": 3, "total_ms": 48.0,
                       "per_rule_ms": 2.0, "compiled": 24, "rejected": 0}],
    )


def test_defter_ekliyor_ustune_yazmiyor(tmp_path) -> None:
    """İkinci koşum birincinin kanıtını silmemeli.

    K35: aynı ölçüm ajanda 1,46×, koordinatörde 1,62× çıktı. Defter üstüne
    yazsaydı geriye tek sayı kalırdı ve ayrışmanın kendisi — yani makinenin
    sessiz olmadığının **kanıtı** — kaybolurdu.
    """
    defter = tmp_path / "maliyet.json"

    assert _append_record(defter, _kayit("ajan")) == 1
    assert _append_record(defter, _kayit("koordinator")) == 2

    belge = json.loads(defter.read_text(encoding="utf-8"))
    assert [k["label"] for k in belge["runs"]] == ["ajan", "koordinator"]


def test_defter_yoksa_olusturuluyor(tmp_path) -> None:
    defter = tmp_path / "alt" / "dizin" / "maliyet.json"
    assert _append_record(defter, _kayit("ilk")) == 1
    assert defter.is_file()


def test_yuk_okunamadiginda_rapor_bunu_soyluyor() -> None:
    """`None` sessizce atlanmıyor — 'bakmadık' ile 'sıfırdı' ayrı şeyler."""
    kayit = _kayit("ajan")
    kayit.load_average = None
    metin = _format(kayit)
    assert "gösterilemiyor" in metin


def test_super_dogrusal_oran_raporda_adlandiriliyor() -> None:
    """Rapor sayıyı basmakla kalmıyor, ne anlama geldiğini söylüyor."""
    assert "süper-doğrusal" in _format(_kayit("ajan", scaling=4.0))
    assert "doğrusal" in _format(_kayit("ajan", scaling=1.0))
