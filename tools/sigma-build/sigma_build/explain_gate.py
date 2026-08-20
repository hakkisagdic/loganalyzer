"""Kapı 2 — ClickHouse üretilen SQL'i kabul ediyor mu (T32).

Kapı 1 kolon **varlığına** bakıyor ve orada durmak zorunda: tipleri de
modellemek ClickHouse'un yarısını yeniden yazmak olurdu. Örneklemde tam olarak
bu boşluğa düşen iki kural var ve ikisi de Kapı 1'i geçiyor:

* `connection_info_protocol_name=6` — kolon `LowCardinality(String)`
* `src_endpoint_ip ILIKE '203.0.113.%'` — kolon `IPv6`, `ILIKE` String istiyor

Burada soru ClickHouse'a **soruluyor**. Veri gerekmiyor, yalnızca şema — bu
yüzden kapı altın örneklerden (Kapı 3) bağımsız koşabiliyor.

`EXPLAIN SYNTAX` tip denetimi YAPMIYOR — ölçüldü
------------------------------------------------
İlk hâli `EXPLAIN SYNTAX` soruyordu. Canlı ClickHouse 26.7.3'e karşı koşulduğunda
çıktı: o biçim yalnızca AST'yi yeniden yazıp geri veriyor, **tip denetimi
yapmıyor**. Bilinen iki kırık sorgunun ikisine de 200 dönüyor:

| Sorgu | `EXPLAIN SYNTAX` | `EXPLAIN` |
| --- | --- | --- |
| `connection_info_protocol_name=6` | kabul | **red — Code 386 `NO_COMMON_TYPE`** |
| `src_endpoint_ip ILIKE '203.0.113.%'` | kabul | **red — Code 43 `ILLEGAL_TYPE_OF_ARGUMENT`** |
| (sağlam sorgu) | kabul | kabul |

Yani kapı, kural seti geldiğinde 24 kuralın hepsini geçirecek ve ikisi üretimde
patlayacaktı: `classify_error`'ın `KIND_TYPE_MISMATCH` kolu o yoldan **asla**
tetiklenemezdi. §7'nin adını koyduğu şeyin tam kendisi — sessizce yeşil bekçi,
ve tam olarak Kapı 2'nin kapatmak için var olduğu sınıf.

Bunu `--self-test` buldu. Kip yazılmasaydı kusur, kural seti üretime çıkana kadar
kimseye görünmeyecekti; kapının kendi kusurunu kapının kendisi bildirdi.

Aynı koşum `0001_events.sql`'den okunan tipleri de doğruladı: `toTypeName` →
`LowCardinality(String)` ve `IPv6`.

Hangi biçim — ölçüldü
---------------------
Canlı ClickHouse 26.7.3, üç sorgu, üç tur:

| Biçim | Ayırt ediyor mu | Sonuçlar |
| --- | --- | --- |
| `EXPLAIN` | ✓ | `red, red, kabul` |
| `EXPLAIN PLAN` | ✓ | `red, red, kabul` |
| `EXPLAIN ESTIMATE` | ✓ | `red, red, kabul` |
| `EXPLAIN QUERY TREE` | ✗ | `kabul, red, kabul` — **kısmen** |
| `EXPLAIN SYNTAX` | ✗ | `kabul, kabul, kabul` |

**Maliyet ayırt edici değil.** Isınma çıkarıldığında üç doğru biçim de
~12–13 ms/sorgu bandında; 269 kural × ~13 ms ≈ **3,5 saniye**. CI'da bir kalem
değil. Geriye tek ölçüt olarak doğruluk kalıyor, o da üçünde eşit — bu yüzden en
açık olanı, `EXPLAIN`, duruyor. **Biçim seçimi maliyetle gerekçelendirilemez.**

⚠️ İlk ölçüm ısınmasızdı ve `EXPLAIN`'i `EXPLAIN PLAN`'in 2,3 katı gösterdi.
Çıplak `EXPLAIN` zaten `EXPLAIN PLAN`'in kendisi olduğu için bu imkânsız; ölçülen
şey biçim değil **listedeki sıraydı**. Sıra ters çevrildiğinde fark biçimi değil
ilk sırayı takip etti. `probe_forms` artık her biçimden önce sayılmayan bir
ısınma turu atıyor.

`EXPLAIN QUERY TREE` tablodaki en değerli satır
------------------------------------------------
`ILIKE ↔ IPv6`'yı **yakalıyor**, `tamsayı ↔ LowCardinality(String)`'i
**kaçırıyor**. Yani kısmen çalışıyor — ve **kısmen çalışan bir kapı hiç
çalışmayandan tehlikeli**. Biri bir gün "daha ucuz ve tip hatasını yakalıyor"
diye ona geçseydi kapı `ILIKE` hatalarını yakalamaya devam edeceği için
**çalışıyor görünürdü**; sessizce geçen tek sınıf tamsayı uyuşmazlıkları olurdu.
`EXPLAIN SYNTAX` en azından her şeye "kabul" diyerek kendini ele veriyordu.

Bu yüzden `probe_forms`'un ölçütü "üç sonucun **üçü de** beklenen mi" — "en az
bir red üretti mi" değil. Gevşek kriter tam bu satırı kaçırırdı.

Neden kendi CI işinde
---------------------
Var olan `integration` işi zaten Testcontainers kaldırıyor ve oraya eklemek ek
konteyner maliyeti getirmezdi. Yine de ayrı: derleme kapısını entegrasyon işine
bağlamak, o iş **ilgisiz bir sebeple** düştüğünde derleme kapısının da
körleşmesi demek. Bu depoda "başkasının hatası yüzünden sessizleşen bekçi"
deseninin bedeli ödendi.

Sınıflandırma tanımadığında da susmuyor
---------------------------------------
Hata metinlerinden `kind` çıkarımı desen eşlemesi, ve desenler **ölçülmedi** —
ClickHouse'a henüz sorulmadı, sürüm değiştikçe metinler de değişebilir. Bu
yüzden tanınmayan her hata `unsupported_construct` olarak, ham metniyle birlikte
raporlanıyor. Tanınmamak bir kuralı kapıdan geçirmiyor; yalnızca `kind`'ını
kabalaştırıyor. Sessiz kayıp yok, çözünürlük kaybı var.
"""

from __future__ import annotations

import json
import re
import urllib.error
import urllib.request
from dataclasses import dataclass
from pathlib import Path

from sigma_build.gate import (
    GATE_EXPLAIN,
    KIND_TYPE_MISMATCH,
    KIND_UNKNOWN_COLUMN,
    KIND_UNSUPPORTED_CONSTRUCT,
    REMEDY_PIPELINE,
    REMEDY_PIPELINE_OR_SCHEMA,
    REMEDY_UNKNOWN,
    Blocker,
    GateVerdict,
)

__all__ = [
    "classify_error",
    "explain_sql",
    "check_directory",
    "run_self_test",
    "probe_forms",
    "ExplainResult",
    "DEFAULT_EXPLAIN_FORM",
    "CANDIDATE_FORMS",
    "SELF_TEST_QUERIES",
]

#: Sorgunun önüne konan biçim. **Ölçülmüş varsayılan**: canlı 26.7.3'te
#: `EXPLAIN` iki kırık sorguyu da reddediyor, `EXPLAIN SYNTAX` ikisini de
#: geçiriyordu.
DEFAULT_EXPLAIN_FORM = "EXPLAIN"

#: `--probe-forms` bunları yan yana ölçüyor. `EXPLAIN SYNTAX` listede DURUYOR ve
#: bu bilinçli: onu listeden çıkarmak "denedik, olmadı" bilgisini siler ve bir
#: sonraki kişi aynı seçimi aynı gerekçeyle yeniden yapabilir.
CANDIDATE_FORMS: tuple[str, ...] = (
    "EXPLAIN",
    "EXPLAIN PLAN",
    "EXPLAIN QUERY TREE",
    "EXPLAIN ESTIMATE",
    "EXPLAIN SYNTAX",
)


#: ClickHouse hata metinlerinden `kind` çıkarımı.
#:
#: İkisi **ölçüldü** (canlı 26.7.3, Kapı 2'nin ilk koşumu):
#:
#: * `Code: 43 … Illegal type IPv6 of argument of function ilike`
#: * `Code: 386 … There is no supertype for types String, UInt8 …`
#:
#: İkincisi bir boşluk açığa çıkardı: `NO_COMMON_TYPE`'ın metni diğer hiçbir
#: desene uymuyordu ve tip uyuşmazlığı `unsupported_construct` diye
#: sınıflanıyordu. Güvenli tarafa bozulma çalışmıştı — kural yine engelleniyordu,
#: yalnızca `kind` kabalaşıyordu — ama `remedy` de `unknown` çıkıyordu, yani
#: **kapanabilir bir iş kalemi "kapanır mı bilmiyoruz" diye görünüyordu.**
#: Desen eklendi.
#:
#: ⚠️ Kalanlar hâlâ ölçülmedi; sürüm değiştikçe metinler de değişebilir.
#: Yanlış eşleşmenin bedeli kaba bir `kind`, kaçırılmış bir kural değil.
_PATTERNS: tuple[tuple[re.Pattern[str], str], ...] = (
    (re.compile(r"Missing columns?:\s*'([^']+)'", re.IGNORECASE), KIND_UNKNOWN_COLUMN),
    (re.compile(r"Unknown (?:expression )?identifier\s*'?([^'\s]+)'?", re.IGNORECASE), KIND_UNKNOWN_COLUMN),
    (re.compile(r"There is no column with name\s*'?([^'\s]+)'?", re.IGNORECASE), KIND_UNKNOWN_COLUMN),
    # ÖLÇÜLDÜ — Code 43, `src_endpoint_ip ILIKE …`
    (re.compile(r"Illegal type\s+(\S+)\s+of argument", re.IGNORECASE), KIND_TYPE_MISMATCH),
    (re.compile(r"Illegal types?\s+(\S+)\s+and\s+\S+\s+of arguments", re.IGNORECASE), KIND_TYPE_MISMATCH),
    # ÖLÇÜLDÜ — Code 386 `NO_COMMON_TYPE`, `connection_info_protocol_name=6`
    (re.compile(r"There is no supertype for types\s+([^,\s]+)", re.IGNORECASE), KIND_TYPE_MISMATCH),
    (re.compile(r"Cannot convert\s+(\S+)", re.IGNORECASE), KIND_TYPE_MISMATCH),
)


def classify_error(message: str) -> Blocker:
    """ClickHouse'un reddini eyleme çevrilebilir bir engele çevirir.

    Tanınmayan metin **yutulmuyor**: `unsupported_construct` olarak ham hâliyle
    geçiyor. "Çalışamaz" bir iş kalemi değil ama tam hata metni en azından
    okunabilir bir iş kalemi.
    """
    collapsed = " ".join(message.split())

    for pattern, kind in _PATTERNS:
        found = pattern.search(collapsed)
        if found is None:
            continue

        subject = found.group(1)
        if kind == KIND_UNKNOWN_COLUMN:
            return Blocker(
                kind=kind,
                column=subject,
                message=f"kolon yok: `{subject}` (ClickHouse reddetti)",
                remedy=REMEDY_PIPELINE_OR_SCHEMA,
                detail=collapsed,
            )
        return Blocker(
            kind=kind,
            message=f"tip uyuşmuyor: {subject}",
            remedy=REMEDY_PIPELINE,
            detail=collapsed,
        )

    return Blocker(
        kind=KIND_UNSUPPORTED_CONSTRUCT,
        message="ClickHouse sorguyu kabul etmedi",
        # `upstream` DEĞİL: sınıflandıramamak "kapanamaz" demek değil, "kapanır
        # mı bilmiyoruz" demek. Muafiyete yazmak işi listeden gizlerdi.
        remedy=REMEDY_UNKNOWN,
        detail=collapsed,
    )


@dataclass(frozen=True)
class ExplainResult:
    file_name: str
    verdict: GateVerdict


def _post(url: str, sql: str, *, user: str, password: str, database: str, timeout: float) -> tuple[bool, str]:
    request = urllib.request.Request(  # noqa: S310 — şema sabit, aşağıda doğrulanıyor
        url,
        data=sql.encode("utf-8"),
        headers={
            "X-ClickHouse-User": user,
            "X-ClickHouse-Key": password,
            "X-ClickHouse-Database": database,
        },
        method="POST",
    )
    try:
        with urllib.request.urlopen(request, timeout=timeout) as response:  # noqa: S310
            return True, response.read().decode("utf-8", errors="replace")
    except urllib.error.HTTPError as error:
        return False, error.read().decode("utf-8", errors="replace")
    except OSError as error:
        # Bağlantı kurulamadı: bu bir kural kusuru DEĞİL, kurulum kusuru.
        # Kural hatasıymış gibi raporlamak, ortam bozukken "269 kural kırık"
        # yazdırırdı — ölçüm aracının kendi sessiz yanlışı.
        raise ConnectionError(f"ClickHouse'a ulaşılamadı ({url}): {error}") from error


def explain_sql(
    sql: str,
    *,
    url: str,
    user: str = "bizigo",
    password: str = "bizigo",
    database: str = "bizigo",
    timeout: float = 20.0,
    form: str = DEFAULT_EXPLAIN_FORM,
) -> GateVerdict:
    """Tek bir sorguyu ClickHouse'a sorar. Veri okunmuyor, yalnızca çözümleniyor."""
    if not url.startswith(("http://", "https://")):
        raise ValueError(f"Beklenen http(s) adresi: {url!r}")
    if not form.upper().startswith("EXPLAIN"):
        raise ValueError(f"Biçim EXPLAIN ile başlamalı: {form!r}")

    ok, body = _post(url, f"{form} {sql.strip().rstrip(';')}", user=user, password=password,
                     database=database, timeout=timeout)
    if ok:
        return GateVerdict(gate=GATE_EXPLAIN, blockers=())
    return GateVerdict(gate=GATE_EXPLAIN, blockers=(classify_error(body),))


def check_directory(directory: Path, **kwargs: object) -> list[ExplainResult]:
    """`detections/sigma/*.sql` içindeki her sorguyu sınar.

    Yorum başlıkları olduğu gibi gönderiliyor — ClickHouse `--` yorumlarını
    kabul ediyor ve başlığı soymak, gönderilen metnin depodakinden farklı
    olması demek olurdu.
    """
    results: list[ExplainResult] = []
    for path in sorted(directory.glob("*.sql")):
        results.append(ExplainResult(path.name, explain_sql(path.read_text(encoding="utf-8"), **kwargs)))  # type: ignore[arg-type]
    return results


#: Kapının kendi kırmızı yanabilirlik sınavı.
#:
#: Bugün `detections/sigma/` boş (kural seti T31'i bekliyor), yani Kapı 2 sıfır
#: sorgu sorup sessizce yeşil kalırdı — ve "sessizce yeşil bekçi" bu deponun
#: adını koyduğu hata sınıfı. Bu kip, sonucu **bilinen** üç sorguyu soruyor:
#: ikisi reddedilmeli, biri kabul edilmeli. Kapı ilk günden kırmızı
#: yanabildiğini kanıtlıyor ve kural seti geldiğinde de kanıtlamaya devam ediyor.
#:
#: İlk iki sorgunun reddedileceği artık **ölçüldü**, çıkarım değil: canlı 26.7.3
#: `EXPLAIN` ile Code 386 (`NO_COMMON_TYPE`) ve Code 43
#: (`ILLEGAL_TYPE_OF_ARGUMENT`) veriyor, `toTypeName` de
#: `LowCardinality(String)` ve `IPv6` diyor. Kipin ilk koşumu bu tabloyu
#: doğruladı **ve** kapının kendi kusurunu buldu (bkz. modül açıklaması).
SELF_TEST_QUERIES: tuple[tuple[str, bool, str], ...] = (
    (
        "SELECT * FROM events_ocsf WHERE connection_info_protocol_name=6",
        False,
        "tamsayı ↔ LowCardinality(String) (fortigate_high_port_scan)",
    ),
    (
        "SELECT * FROM events_ocsf WHERE src_endpoint_ip ILIKE '203.0.113.%'",
        False,
        "ILIKE ↔ IPv6 (fortigate_admin_from_wan)",
    ),
    (
        "SELECT * FROM events_ocsf WHERE device_vendor_name='Cisco' AND dst_endpoint_port=445",
        True,
        "kabul edilmeli (asa_deny_inbound)",
    ),
)


def probe_forms(
    *,
    forms: tuple[str, ...] = CANDIDATE_FORMS,
    rounds: int = 3,
    **kwargs: object,
) -> list[dict[str, object]]:
    """Aday `EXPLAIN` biçimlerini yan yana ölçer: hangisi ayırt ediyor, ne kadar sürüyor.

    Biçim seçimi tahminle yapılmamalı — `EXPLAIN SYNTAX`'in seçilmiş olması zaten
    tahminin bedeliydi.

    Isınma turu neden var
    ---------------------
    İlk sürüm ısınmasızdı ve **yanıltıcı bir tablo üretti**: `EXPLAIN` üç turda da
    `EXPLAIN PLAN`'in ~2,3 katı çıktı. ClickHouse'ta çıplak `EXPLAIN` zaten
    `EXPLAIN PLAN`'in kendisi, yani aynı şeyin kendinden 2,3 kat pahalı olması
    imkânsız — ölçülen şey biçim değil **listedeki sıra** idi. Sıra ters
    çevrildiğinde fark biçimi değil ilk sırayı takip etti.

    `CLAUDE.md` §6'nın kuralı: bir ölçümün sonucunun duvar saatiyle ya da koşum
    sırasıyla ilgisi olmamalı. Her biçim önce **sayılmayan** bir tur atıyor;
    bağlantı ve önbellek ısınmasının bedelini ölçüm değil o tur ödüyor.

    `discriminates` alanı "üç sorgunun **üçü de** beklenen sonucu verdi mi"
    sorusunun cevabı — "en az bir red üretti mi" değil. Fark ölçüldü:
    `EXPLAIN QUERY TREE` `ILIKE ↔ IPv6`'yı yakalıyor ama tamsayı uyuşmazlığını
    kaçırıyor, yani **kısmen** çalışıyor. Gevşek bir kriter onu yeşil gösterirdi
    ve kısmen çalışan bir kapı hiç çalışmayandan tehlikeli: `EXPLAIN SYNTAX` her
    şeye "kabul" diyerek kendini ele veriyordu, `QUERY TREE` ise çalışıyor
    görünüp tek bir sınıfı sessizce geçirirdi.
    """
    import time

    expected = [ok for _, ok, _ in SELF_TEST_QUERIES]
    report: list[dict[str, object]] = []

    for form in forms:
        # Isınma turu — SAYILMIYOR.
        verdicts = [explain_sql(sql, form=form, **kwargs).passed for sql, _, _ in SELF_TEST_QUERIES]  # type: ignore[arg-type]

        durations: list[float] = []
        for _ in range(rounds):
            started = time.perf_counter()
            for sql, _, _ in SELF_TEST_QUERIES:
                explain_sql(sql, form=form, **kwargs)  # type: ignore[arg-type]
            durations.append(time.perf_counter() - started)

        # Turların **en hızlısı**: ortalama, arada geçen bir yavaşlamayı ölçüme
        # katardı ve ölçtüğümüz şey makinenin o anki yükü değil biçimin maliyeti.
        best = min(durations)

        report.append(
            {
                "form": form,
                "discriminates": verdicts == expected,
                "results": ["kabul" if ok else "red" for ok in verdicts],
                "seconds": round(best, 4),
                "per_query_ms": round(best * 1000 / len(SELF_TEST_QUERIES), 2),
                "rounds": rounds,
            }
        )
    return report


def run_self_test(**kwargs: object) -> list[str]:
    """Boş liste = kapı hem reddedebiliyor hem kabul edebiliyor."""
    problems: list[str] = []
    for sql, should_pass, label in SELF_TEST_QUERIES:
        verdict = explain_sql(sql, **kwargs)  # type: ignore[arg-type]
        if verdict.passed != should_pass:
            beklenen = "kabul" if should_pass else "red"
            alinan = "kabul" if verdict.passed else "red"
            detail = verdict.blockers[0].detail if verdict.blockers else ""
            problems.append(f"{label}: beklenen {beklenen}, alınan {alinan}. {detail}")
    return problems


def _main(argv: list[str] | None = None) -> int:
    import argparse
    import sys

    from sigma_build.manifest import OUTPUT_DIR
    from sigma_build.view_columns import repo_root

    parser = argparse.ArgumentParser(description="Kapı 2: ClickHouse üretilen SQL'i kabul ediyor mu.")
    parser.add_argument("--clickhouse-url", default="http://localhost:8123")
    # `--clickhouse-*` yazımı `--clickhouse-url`in yanında doğal duruyor ve elle
    # koşarken ilk denenen o oluyor; iki yazım da kabul ediliyor.
    parser.add_argument("--user", "--clickhouse-user", dest="user", default="bizigo")
    parser.add_argument("--password", "--clickhouse-password", dest="password", default="bizigo")
    parser.add_argument("--database", "--clickhouse-database", dest="database", default="bizigo")
    parser.add_argument("--output", type=Path, default=None)
    parser.add_argument(
        "--explain-form",
        default=DEFAULT_EXPLAIN_FORM,
        help=f"Sorgunun önüne konan biçim (varsayılan: {DEFAULT_EXPLAIN_FORM})",
    )
    parser.add_argument(
        "--self-test",
        action="store_true",
        help="Sonucu bilinen üç sorguyla kapının kırmızı yanabildiğini ölçer",
    )
    parser.add_argument(
        "--probe-forms",
        action="store_true",
        help="Aday EXPLAIN biçimlerini yan yana ölçer: hangisi ayırt ediyor, ne kadar sürüyor",
    )
    args = parser.parse_args(argv)

    connection = {
        "url": args.clickhouse_url,
        "user": args.user,
        "password": args.password,
        "database": args.database,
    }

    if args.probe_forms:
        report = probe_forms(**connection)
        for row in report:
            mark = "✓" if row["discriminates"] else "✗"
            print(f"  {mark} {row['form']:<20} {row['results']}  {row['per_query_ms']} ms/sorgu")
        ayirt_eden = [row["form"] for row in report if row["discriminates"]]
        print(f"\nAyırt eden biçimler: {ayirt_eden or 'HİÇBİRİ'}")
        return 0 if ayirt_eden else 1

    if args.self_test:
        problems = run_self_test(form=args.explain_form, **connection)
        if problems:
            for problem in problems:
                print(f"  {problem}", file=sys.stderr)
            print("\n✗ Kapı 2 beklendiği gibi davranmıyor.", file=sys.stderr)
            return 1
        print(f"✓ Kapı 2 sınavı geçti — {len(SELF_TEST_QUERIES)} sorgu, beklenen sonuçlar alındı.")
        return 0

    directory = args.output or (repo_root() / OUTPUT_DIR)
    if not directory.is_dir():
        print(f"✗ Çıktı dizini yok: {directory}", file=sys.stderr)
        return 2

    results = check_directory(directory, form=args.explain_form, **connection)

    rejected = [result for result in results if not result.verdict.passed]

    print(f"{len(results)} sorgu soruldu · {len(results) - len(rejected)} kabul · {len(rejected)} red")
    for result in rejected:
        print(f"\n  {result.file_name}")
        for blocker in result.verdict.blockers:
            print(f"    {json.dumps(blocker.as_dict(), ensure_ascii=False)}")

    if rejected:
        print(
            "\n✗ Derlenen SQL'in bir kısmını ClickHouse kabul etmiyor — yani o kurallar "
            "sessizce hiçbir şey yakalamazdı.",
            file=sys.stderr,
        )
        return 1

    return 0


if __name__ == "__main__":
    raise SystemExit(_main())
