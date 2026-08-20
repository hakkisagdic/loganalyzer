"""T30 ölçüm koşumu — Sigma kuralı başına eşleme maliyeti.

⚠️ **Atılabilir kod.** Korunacak olan bu dosyanın ürettiği sayılar ve
`SONUCLAR.md`'ye yazılan kapsam kararı.

Ne ölçüyor
----------
Beş soru, T30 ticket'ından:

1. Kural başına kaç satır eşleme?      → `mapping_lines_per_rule`
2. Kural başına ne kadar süre?         → `seconds_per_rule` (269 kuralın maliyeti)
3. Kaçı **çalışır** hâle geldi?        → `working` (derlendi ≠ doğru sonuç veriyor)
4. SQL canlı ClickHouse'ta koşuyor mu? → `--clickhouse-url` verildiğinde
5. `unmapped` Map erişimi?             → `unmapped_hits`

Neden "derlendi" yetmiyor
-------------------------
Önceki ölçüm kolon listesine karşıydı ve **sorgu hiç çalıştırılmadı**. Asıl
tehlike derleme hatası değil: pipeline eşlemeyi atlarsa derleme yine başarılı
olur ve SQL **var olmayan bir kolona** referans verir. Bu koşum bu yüzden üç
ayrı kademe ölçüyor ve üçünü birbirine karıştırmıyor:

* `compiled`  — pySigma SQL üretti
* `runs`      — ClickHouse SQL'i kabul etti (kolonlar gerçekten var)
* `matches`   — sorgu altın örneklerimizden en az bir satır döndürdü

Bir kural `compiled` olup `runs` olmayabilir; `runs` olup `matches` olmayabilir.
Kapsam kararının dayanağı **`matches`**, `compiled` değil.

Kullanım
--------
    python3.13 -m venv .venv && .venv/bin/pip install \\
        'pySigma==1.5.0' 'pysigma-backend-clickhouse==1.1.1' 'PyYAML==6.0.3'

    # Yalnızca statik ölçüm (ClickHouse gerekmiyor):
    .venv/bin/python measure.py

    # Canlı koşum (T30 kabul kriteri):
    .venv/bin/python measure.py --clickhouse-url http://localhost:8123 \\
        --clickhouse-user bizigo --clickhouse-password bizigo
"""

from __future__ import annotations

import argparse
import json
import sys
import time
import urllib.error
import urllib.parse
import urllib.request
from dataclasses import asdict, dataclass, field
from pathlib import Path

RULES_DIR = Path(__file__).parent / "rules"

#: Backend'in yazdığı sabit tablo adı. Bizim tablomuz `events_ocsf`.
BACKEND_TABLE = "logs"


@dataclass
class RuleOutcome:
    """Tek bir kuralın üç kademedeki durumu."""

    name: str
    category: str
    product: str

    compiled: bool = False
    runs: bool = False
    matches: bool = False

    sql: str = ""
    error: str = ""

    #: Pipeline'sız çıktıyla BİREBİR aynı mı — yani pipeline bu kurala hiç
    #: dokunmadı mı. Araştırmanın "0 kural" sonucunun ölçülebilir hâli.
    untouched: bool = False

    #: `unmapped[...]` erişimi içeriyor mu (indekssiz, yani yavaş).
    unmapped_hits: int = 0

    #: Tablo adının elle düzeltilmesi gerekti mi. Gerekiyorsa backend durum
    #: değişkenini okumuyor demektir ve bu T31'in çözmesi gereken bir şey.
    table_rewritten: bool = False

    seconds: float = 0.0
    rows: int = 0


@dataclass
class Report:
    rules: int = 0
    compiled: int = 0
    runs: int = 0
    matches: int = 0
    untouched: int = 0

    mapped_fields: int = 0
    pipeline_lines: int = 0
    total_seconds: float = 0.0

    table_rewrites: int = 0
    unmapped_rules: int = 0

    outcomes: list[RuleOutcome] = field(default_factory=list)
    notes: list[str] = field(default_factory=list)

    @property
    def mapping_lines_per_rule(self) -> float:
        """Kapsam kararının birimi: eşleme satırı / eşlenen kural."""
        return self.pipeline_lines / self.matches if self.matches else float("inf")

    @property
    def seconds_per_rule(self) -> float:
        return self.total_seconds / self.rules if self.rules else 0.0


def load_rules() -> list[tuple[str, str]]:
    """Örneklem: dosya adı ve gövdesi."""
    return [(path.name, path.read_text(encoding="utf-8")) for path in sorted(RULES_DIR.glob("*.yml"))]


def compile_rules(with_pipeline: bool) -> dict[str, tuple[str, str]]:
    """Her kuralı derler. Dönen: ad → (sql, hata).

    Pipeline'lı ve pipeline'sız iki kez çağrılıyor: çıktısı birebir aynı kalan
    kurallar **eşlenmemiş** demek ve o sayı araştırmanın "0 kural" bulgusunun
    bizim şemamızdaki karşılığı.
    """
    from sigma.collection import SigmaCollection

    from bizigo_pipeline import bizigo_pipeline

    backend = _backend(bizigo_pipeline() if with_pipeline else None)
    results: dict[str, tuple[str, str]] = {}

    for name, text in load_rules():
        try:
            collection = SigmaCollection.from_yaml(text)
            queries = backend.convert(collection)
            results[name] = ("\n".join(queries), "")
        except Exception as exc:  # noqa: BLE001 — hangi hata olursa olsun ölçüme girsin
            results[name] = ("", f"{type(exc).__name__}: {exc}")

    return results


def _backend(pipeline):
    from sigma.backends.clickhouse.clickhouse import ClickhouseBackend

    return ClickhouseBackend(processing_pipeline=pipeline)


def rewrite_table(sql: str, table: str) -> tuple[str, bool]:
    """`FROM logs` → `FROM events_ocsf`.

    Backend durum değişkenini okumuyorsa ikame burada yapılıyor ve bu
    **raporlanıyor**: sessizce düzeltmek, ölçümün "SQL koşuyor" sonucunu
    kendi eliyle üretmek olurdu.
    """
    needle = f"FROM {BACKEND_TABLE}"

    if needle not in sql:
        return sql, False

    return sql.replace(needle, f"FROM {table}"), True


def run_on_clickhouse(sql: str, url: str, user: str, password: str, timeout: float) -> tuple[int, str]:
    """Sorguyu çalıştırır; (satır sayısı, hata) döner.

    `SELECT *` yerine `count()` sarmalayıcısı: ölçülen şey satırların içeriği
    değil, sorgunun **koşup koşmadığı** ve bir şey bulup bulmadığı.
    """
    wrapped = f"SELECT count() FROM ({sql})"
    query = urllib.parse.urlencode({"query": wrapped, "default_format": "TSV"})
    request = urllib.request.Request(f"{url}/?{query}", method="GET")

    if user:
        request.add_header("X-ClickHouse-User", user)
    if password:
        request.add_header("X-ClickHouse-Key", password)

    try:
        with urllib.request.urlopen(request, timeout=timeout) as response:  # noqa: S310
            return int(response.read().decode("utf-8").strip() or 0), ""
    except urllib.error.HTTPError as exc:
        # ClickHouse hatayı gövdede anlatıyor; kolon adı hatası burada görünüyor
        # ve T30'un aradığı tuzak tam olarak bu.
        return 0, exc.read().decode("utf-8", errors="replace").strip()[:400]
    except Exception as exc:  # noqa: BLE001
        return 0, f"{type(exc).__name__}: {exc}"


def measure(args: argparse.Namespace) -> Report:
    import yaml

    from bizigo_pipeline import TABLE, mapped_field_count, pipeline_line_count

    report = Report(
        mapped_fields=mapped_field_count(),
        pipeline_lines=pipeline_line_count(),
    )

    started = time.monotonic()
    with_pipeline = compile_rules(with_pipeline=True)
    baseline = compile_rules(with_pipeline=False)
    report.total_seconds = time.monotonic() - started

    for name, text in load_rules():
        meta = yaml.safe_load(text)
        source = meta.get("logsource", {})

        outcome = RuleOutcome(
            name=name,
            category=str(source.get("category", "")),
            product=str(source.get("product", "")),
        )

        sql, error = with_pipeline[name]
        outcome.compiled = bool(sql) and not error
        outcome.error = error

        if outcome.compiled:
            outcome.untouched = sql == baseline[name][0]
            outcome.unmapped_hits = sql.count("unmapped[")
            sql, outcome.table_rewritten = rewrite_table(sql, TABLE)
            outcome.sql = sql

            if args.clickhouse_url:
                rows, run_error = run_on_clickhouse(
                    sql, args.clickhouse_url, args.clickhouse_user, args.clickhouse_password, args.timeout
                )
                outcome.runs = not run_error
                outcome.rows = rows
                outcome.matches = outcome.runs and rows > 0

                if run_error:
                    outcome.error = run_error

        report.outcomes.append(outcome)

    report.rules = len(report.outcomes)
    report.compiled = sum(1 for o in report.outcomes if o.compiled)
    report.runs = sum(1 for o in report.outcomes if o.runs)
    report.matches = sum(1 for o in report.outcomes if o.matches)
    report.untouched = sum(1 for o in report.outcomes if o.untouched)
    report.table_rewrites = sum(1 for o in report.outcomes if o.table_rewritten)
    report.unmapped_rules = sum(1 for o in report.outcomes if o.unmapped_hits > 0)

    if not args.clickhouse_url:
        report.notes.append(
            "ClickHouse adresi verilmedi: `runs` ve `matches` ölçülmedi, sıfır görünmeleri "
            "başarısızlık DEĞİL. T30'un kabul kriteri canlı koşum istiyor."
        )

    if report.untouched:
        report.notes.append(
            f"{report.untouched} kural pipeline'dan etkilenmedi — çıktısı pipeline'sız hâliyle "
            "birebir aynı. Bunlar eşlenmemiş kurallar ve kapsam kararında sayılmamalı."
        )

    if report.table_rewrites:
        report.notes.append(
            f"{report.table_rewrites} SQL'de tablo adı ELLE düzeltildi (`FROM {BACKEND_TABLE}` → "
            f"`FROM {TABLE}`). Backend durum değişkenini okumuyor; T31 bunu kaynağında çözmeli."
        )

    return report


def main() -> int:
    parser = argparse.ArgumentParser(description="T30 Sigma eşleme maliyeti ölçümü")
    parser.add_argument("--clickhouse-url", default="", help="örn. http://localhost:8123")
    parser.add_argument("--clickhouse-user", default="bizigo")
    parser.add_argument("--clickhouse-password", default="bizigo")
    parser.add_argument("--timeout", type=float, default=15.0)
    parser.add_argument("--json", default="", help="ölçümü bu dosyaya JSON olarak yaz")
    args = parser.parse_args()

    try:
        report = measure(args)
    except ImportError as exc:
        print(f"Bağımlılık eksik: {exc}", file=sys.stderr)
        print("Kurulum için dosyanın başındaki komuta bakın.", file=sys.stderr)
        return 2

    print(f"Örneklem            : {report.rules} kural")
    print(f"Derlendi            : {report.compiled}")
    print(f"ClickHouse kabul etti: {report.runs}")
    print(f"Satır döndürdü      : {report.matches}")
    print(f"Pipeline'a dokunmadı: {report.untouched}")
    print(f"Eşleme satırı       : {report.pipeline_lines} ({report.mapped_fields} alan)")
    print(f"Kural başına eşleme : {report.mapping_lines_per_rule:.2f} satır")
    print(f"Kural başına süre   : {report.seconds_per_rule * 1000:.1f} ms")
    print(f"unmapped kullanan   : {report.unmapped_rules} kural")

    for note in report.notes:
        print(f"\n! {note}")

    if args.json:
        Path(args.json).write_text(
            json.dumps(asdict(report), ensure_ascii=False, indent=2), encoding="utf-8"
        )
        print(f"\nJSON: {args.json}")

    return 0


if __name__ == "__main__":
    sys.exit(main())
