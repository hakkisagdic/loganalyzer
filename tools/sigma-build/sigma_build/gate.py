"""Kapı 1 — üretilen SQL var olmayan bir kolona gidiyor mu (T32).

Ne yakalıyor, ne yakalamıyor
----------------------------
Bu kapı **kolon varlığına** bakıyor: SQL'de kolon olarak geçen her ad
`events_ocsf` görünümünde gerçekten var mı. Yakalayamadığı şey **tip
uyuşmazlığı** — kolon var ama karşılaştırma geçersiz. Örneklemde ikisi de
gerçekten var:

* `url ILIKE '%/config/%'` → `url` diye kolon yok (bu kapı yakalıyor)
* `connection_info_protocol_name=6` → kolon var, `LowCardinality(String)`, tamsayı
  ile karşılaştırılıyor (bu kapı **yakalayamaz**, Kapı 2'nin işi)
* `src_endpoint_ip ILIKE '203.0.113.%'` → kolon var, tipi `IPv6`, `ILIKE` String
  istiyor (yine Kapı 2)

Bu yüzden iki kapı var; birini diğerinin yerine koymak yakalanmayan sınıfı sessiz
bırakır.

Ad çıkarımı neden gürültülü tarafa yanılıyor
--------------------------------------------
SQL'den kolon adı çıkarmak tam bir ayrıştırıcı olmadan kesin değil. Bilinmeyen
bir ClickHouse anahtar sözcüğü kolon sanılabilir. Bu **kasıtlı olarak** kabul
edilen taraf: yanlış tanınan bir ad kuralı reddettirir — gürültülü, birisi fark
eder ve listeye bir sözcük eklenir. Ters yanılgı — bir kolon referansının
görülmemesi — kuralı kapıdan **geçirir** ve çalışma zamanında kırar. Hata yok,
sayaç yok, belirti yok.

Kapının kural bazında olması
----------------------------
Kapı geçer ya da geçmez; kısmi derleme yok. Eşlenemeyen bir alanı düşürmek
kuralın anlamını değiştiriyor ve yönü ağaçtaki yerine bağlı: bir `and` kolunu
düşürmek eşleşmeyi genişletir (yanlış pozitif), bir `or` kolunu düşürmek
daraltır (yanlış negatif). İkisinde de başlığının söylediğinden başka bir şey
yapan bir kural yayınlanır.

**Teşhis** yine de alan bazında: `blockers` her engeli ayrı taşıyor, çünkü
manifest yalnızca dürüstlük değil F3'ün yol haritası — "`url` eklersek kaç kural
açılır" sorusu ancak öyle cevaplanabiliyor.
"""

from __future__ import annotations

import re
from dataclasses import dataclass
from pathlib import Path

__all__ = [
    "Blocker",
    "GateVerdict",
    "KIND_UNKNOWN_COLUMN",
    "KIND_TYPE_MISMATCH",
    "KIND_UNSUPPORTED_CONSTRUCT",
    "KIND_NO_GOLDEN_SAMPLE",
    "REMEDY_PIPELINE",
    "REMEDY_SCHEMA",
    "REMEDY_PIPELINE_OR_SCHEMA",
    "REMEDY_UPSTREAM",
    "REMEDY_UNKNOWN",
    "REMEDY_DATA",
    "CLOSEABLE_REMEDIES",
    "GATE_COLUMN_EXISTENCE",
    "GATE_EXPLAIN",
    "GATE_GOLDEN_SAMPLE",
    "referenced_columns",
    "check_columns",
]

KIND_UNKNOWN_COLUMN = "unknown_column"
KIND_TYPE_MISMATCH = "type_mismatch"
KIND_UNSUPPORTED_CONSTRUCT = "unsupported_construct"
KIND_NO_GOLDEN_SAMPLE = "no_golden_sample"

GATE_COLUMN_EXISTENCE = "column_existence"
GATE_EXPLAIN = "explain"
GATE_GOLDEN_SAMPLE = "golden_sample"

#: `unknown_column` için düzeltmenin nereye gittiği **derleme zamanında
#: bilinemiyor**: alan ya `unmapped['X']`e eşlenecek (pipeline) ya kolona terfi
#: edecek (şema kararı, T30'un `unmapped` listesi tam olarak bu). Tek bir değer
#: yazmak, bilinmeyeni veri gibi göstermek olurdu.
REMEDY_PIPELINE_OR_SCHEMA = "pipeline_or_schema"
REMEDY_PIPELINE = "pipeline"
REMEDY_SCHEMA = "schema"
REMEDY_DATA = "data"

#: **Kimsenin yapamayacağı iş.** Yukarı akış ya da backend değişmeden kapanmayan
#: engel. Diğer bütün değerler "birinin yapabileceği bir iş" adlandırıyor; bu
#: ayrım olmazsa `gated` listesinin tamamı kapanabilir görünür ve hiç kapanmaz —
#: `Pending` ile `Exempt`'in tek listede durması hâli (§8).
#:
#: Hiçbir sınıflandırıcı bunu **kendiliğinden** atamıyor: muafiyet gibi, bilinçli
#: bir hareketle konuluyor ve sayısı ayrıca sabitleniyor.
REMEDY_UPSTREAM = "upstream"

#: Sınıflandırıcı karar veremedi. `upstream` ile karıştırılmamalı: "kapanamaz"
#: demek değil, "kapanır mı bilmiyoruz" demek. Sayımda **kapanabilirler**
#: tarafında duruyor — bilinmeyeni muafiyete yazmak, işi listeden gizlerdi.
REMEDY_UNKNOWN = "unknown"

#: Azalması **beklenen** engeller. `upstream` dışındaki her şey burada:
#: bilinmeyen dahil, çünkü bilinmeyeni muafiyete yazmak işi gizlemek olurdu.
CLOSEABLE_REMEDIES = frozenset(
    {REMEDY_PIPELINE, REMEDY_SCHEMA, REMEDY_PIPELINE_OR_SCHEMA, REMEDY_DATA, REMEDY_UNKNOWN}
)


@dataclass(frozen=True)
class Blocker:
    """Bir kuralın neden koşamadığı — **eyleme çevrilebilecek** kadar somut.

    "Çalışamaz" bir iş kalemi değil; "kolon yok: `url`" bir iş kalemi.
    """

    kind: str
    message: str
    remedy: str
    column: str | None = None
    detail: str | None = None

    def as_dict(self) -> dict[str, str]:
        document = {"kind": self.kind, "message": self.message, "remedy": self.remedy}
        if self.column is not None:
            document["column"] = self.column
        if self.detail is not None:
            document["detail"] = self.detail
        return document


@dataclass(frozen=True)
class GateVerdict:
    gate: str
    blockers: tuple[Blocker, ...]

    @property
    def passed(self) -> bool:
        return not self.blockers


# --------------------------------------------------------------------------- #
# SQL'den kolon adı çıkarımı
# --------------------------------------------------------------------------- #

#: Kolon sanılmaması gereken sözcükler. Liste eksikse sonuç **gürültü** —
#: bilinmeyen sözcük "kolon yok" diye raporlanır ve buraya eklenir. Eksikliğin
#: sessiz bir sonucu yok; bu, listenin elle tutulabilir olmasının sebebi.
SQL_KEYWORDS = frozenset(
    word.casefold()
    for word in (
        "SELECT FROM WHERE PREWHERE AND OR NOT IN IS NULL LIKE ILIKE BETWEEN AS ON "
        "JOIN LEFT RIGHT INNER OUTER FULL CROSS ANY ALL GLOBAL USING "
        "GROUP BY ORDER HAVING LIMIT OFFSET DISTINCT WITH UNION EXCEPT INTERSECT "
        "CASE WHEN THEN ELSE END ASC DESC NULLS FIRST LAST "
        "TRUE FALSE ARRAY TUPLE MAP INTERVAL SETTINGS FORMAT EXISTS "
        "SECOND MINUTE HOUR DAY WEEK MONTH QUARTER YEAR"
    ).split()
)

#: Tırnaksız ad, backtick'li ad, ya da çift tırnaklı ad.
_IDENTIFIER = re.compile(r"`([^`]+)`|\"([^\"]+)\"|([A-Za-z_]\w*)")


def _strip_string_literals(sql: str) -> str:
    """Tek tırnaklı metinleri boşlukla değiştirir; uzunluk korunmuyor, gerek yok.

    `''` ve `\\'` kaçışlarının ikisi de ClickHouse'ta geçerli ve örneklemde
    ikisi de var (`nginx_sqli_probe` `'%'' OR ''1''=''1%'` üretiyor).
    """
    out: list[str] = []
    i = 0
    n = len(sql)
    while i < n:
        if sql[i] != "'":
            out.append(sql[i])
            i += 1
            continue

        i += 1
        while i < n:
            if sql[i] == "\\" and i + 1 < n:
                i += 2
                continue
            if sql[i] == "'":
                if i + 1 < n and sql[i + 1] == "'":
                    i += 2
                    continue
                break
            i += 1
        i += 1
        out.append(" ")
    return "".join(out)


def referenced_columns(sql: str) -> tuple[str, ...]:
    """SQL'de **kolon olarak** geçen adlar, ilk görülme sırasında, tekrarsız.

    Elenenler: metin sabitleri, anahtar sözcükler, fonksiyon adları (ardından
    `(` gelen adlar) ve `FROM`'u izleyen tablo adı.
    """
    text = _strip_string_literals(sql)

    # `FROM <tablo>` — tablo adı kolon değil. Sadece ilk FROM'a bakmak yeterli:
    # bu hattın ürettiği sorgular tek tablolu (görünüm), ve alt sorgu doğarsa
    # fazladan bir ad "kolon yok" diye raporlanır — gürültülü taraf.
    table_spans: list[tuple[int, int]] = []
    for from_match in re.finditer(r"\bFROM\s+", text, re.IGNORECASE):
        table = _IDENTIFIER.match(text, from_match.end())
        if table is not None:
            table_spans.append(table.span())

    seen: dict[str, None] = {}
    for match in _IDENTIFIER.finditer(text):
        if match.span() in table_spans:
            continue

        name = match.group(1) or match.group(2) or match.group(3)

        # Tırnaksız ad ve ardından `(` geliyorsa fonksiyon adı.
        if match.group(3) is not None:
            if text[match.end() :].lstrip().startswith("("):
                continue
            if name.casefold() in SQL_KEYWORDS:
                continue

        seen.setdefault(name, None)

    return tuple(seen)


def check_columns(sql: str, allowed: frozenset[str] | set[str], *, view: str) -> GateVerdict:
    """Kapı 1. `allowed` **türetilmiş** küme olmalı, elle yazılmış liste değil.

    Bkz. `view_columns.py` — elle yazılmış bir liste `CLAUDE.md` §7'deki
    `Produces<T>` deliğini yeniden kurar ve sürüklenmenin tehlikeli yönü sessiz
    olandır.
    """
    blockers = tuple(
        Blocker(
            kind=KIND_UNKNOWN_COLUMN,
            column=column,
            message=f"kolon yok: `{column}` ({view})",
            remedy=REMEDY_PIPELINE_OR_SCHEMA,
        )
        for column in referenced_columns(sql)
        if column not in allowed
    )
    return GateVerdict(gate=GATE_COLUMN_EXISTENCE, blockers=blockers)


def _main(argv: list[str] | None = None) -> int:
    import argparse
    import json
    import sys

    from sigma_build.view_columns import repo_root, view_definition

    parser = argparse.ArgumentParser(description="Kapı 1: SQL var olmayan kolona gidiyor mu.")
    parser.add_argument("sql", nargs="?", help="SQL metni; verilmezse stdin'den okunur")
    parser.add_argument("--view", default="events_ocsf")
    parser.add_argument("--migrations", type=Path, default=None)
    args = parser.parse_args(argv)

    sql = args.sql if args.sql is not None else sys.stdin.read()
    allowed = view_definition(args.view, args.migrations or repo_root() / "db" / "clickhouse").column_set
    verdict = check_columns(sql, allowed, view=args.view)

    print(json.dumps([blocker.as_dict() for blocker in verdict.blockers], indent=2, ensure_ascii=False))
    return 0 if verdict.passed else 1


if __name__ == "__main__":
    raise SystemExit(_main())
