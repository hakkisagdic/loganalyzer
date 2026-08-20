"""Eşleşmeyen kural neden eşleşmiyor — **üç kutu**, karıştırılmıyor (T30 · 3. ölçüm).

Neden bu ölçüm
--------------
`match_ratio = %25` elimizde ama **neyin sayısı olduğu bilinmiyor.** İki
tamamen farklı dünya aynı oranı üretir:

* Eşleme eksik → kapsam kararı *"önce eşlemeyi düzelt"* olmalı.
* Örneklemde desen yok → oran **kapsamın değil örneklemin** kusuru ve kapsamı
  daraltmak yanlış olur.

Ölçülmeden ikisi ayırt edilemez, ve T30 kapsam kararı buna dayanacak. Belge
aynı tuzağa iki kez düştü; bu, üçüncü kez düşmemek için.

Üçüncü kutu neden var
---------------------
`asa_teardown_rst` **eşleşiyordu** — `message|contains: 'RST'` ile. Ama ASA
sıfırlamayı `Reset-I`/`Reset-O` diye yazıyor, `RST` diye hiç yazmıyor: kural
örneklerdeki `first` ve `burst` sözcüklerinin **içine** denk geliyordu.

Yani eşleşme sayısı yukarı, doğruluk sıfır. Ne derleme kapısı ne canlı koşum
bunu söyleyebilir: sorgu koşar, satır döner, sayaç artar. Yalnızca **eşleşen
metnin kendisine** bakınca görülüyor.

Bu yüzden üç kutu:

* ``absent``          — aradığı dizge örneklerde **hiç yok**
* ``present``         — sözcük sınırında var; eşleşmiyorsa suç eşlemede
* ``substring_only``  — **yalnızca** daha uzun bir sözcüğün içinde var

Üçüncüsü *"eşleşiyor ama yanlış sebeple"*nin ölçülebilir hâli.

ClickHouse **gerekmiyor**: üçü de örnek dosyalara karşı statik. Canlı koşumun
söylediği "eşleşti mi", bu aracın söylediği "eşleşmeli miydi".

Koşum:

    python3 explain_misses.py                    # bütün korpus
    python3 explain_misses.py --json out.json
"""

from __future__ import annotations

import argparse
import json
import re
import sys
from dataclasses import asdict, dataclass, field
from pathlib import Path

#: Korpusun kanonik yeri (T32 terfisi). Prototip dizini emekli.
CORPUS = Path("catalog/sigma/rules")

#: `logsource.product` → altın örnek dizini.
SAMPLES: dict[str, str] = {
    "fortigate": "catalog/parsers/fortinet.fortigate/samples",
    "asa": "catalog/parsers/cisco.asa/samples",
    "routeros": "catalog/parsers/mikrotik.routeros/samples",
    "nginx": "catalog/parsers/nginx.access/samples",
}

ABSENT = "absent"
PRESENT = "present"
SUBSTRING_ONLY = "substring_only"


def free_text_fields() -> frozenset[str]:
    """`raw_data`'ya inen Sigma alanları — kutu 2'nin **tek** geçerli alanı.

    Neden alan türü önemli
    ----------------------
    Kutu 2 (*"yalnızca daha uzun sözcüğün içinde"*) `asa_teardown_rst`'ten
    doğdu: `message|contains: 'RST'` serbest metinde `burst`'ün içine denk
    geliyordu ve bu gürültüydü.

    Ama ölçüm bir **yanlış pozitif** üretti: `fortigate_admin_from_wan`
    `srcip|startswith: '203.0.113.'` yazıyor ve bu tam olarak istenen şey —
    `203.0.113.7` ile eşleşiyor. Ham metinde bakınca "daha uzun bir sözcüğün
    içinde" görünüyor, çünkü IP'lerde nokta sözcük sınırı değil.

    Ayrım alanın **türünde**: serbest metinde içinde-geçmek gürültü, yapısal
    bir alanda önek eşleşmesi **anlam**. Yapısal alan zaten kolonun kendisinde
    karşılaştırılıyor; ham gövdedeki komşuluğu bir şey söylemiyor.

    Liste ürünün eşleme tablosundan türetiliyor, elle yazılmıyor: `message`
    bir gün başka bir kolona giderse burası kendiliğinden izliyor.
    """
    shipping = _shipping()

    if shipping is None:
        # Sessizce boş küme dönmek, kutu 2'yi bütün alanlarda çalıştırırdı —
        # yani bugünkü yanlış pozitifi geri getirirdi. Bilinmiyorsa ölçüm
        # yapılmamalı; çağıran bunu bir arıza olarak görüyor.
        raise RuntimeError(
            "`sidecar/app/sigma_pipeline` import edilemedi; serbest metin alanları "
            "bilinemiyor. Araç depo ağacından koşmalı."
        )

    return frozenset(
        field for field, column in shipping.FIELD_MAP.items() if column == "raw_data"
    )


def _shipping():
    """Ürünün eşleme modülü. Kopyalanmıyor — kopya sessizce ayrışır."""
    import importlib
    import sys

    root = repo_root()

    if root is None:
        return None

    sidecar = str(root / "sidecar")

    if sidecar not in sys.path:
        sys.path.insert(0, sidecar)

    return importlib.import_module("app.sigma_pipeline")


def repo_root() -> Path | None:
    """Depo kökü, bulunamadıysa **None**.

    `here.parent`'a geri çekilmiyor: o sessiz geri çekilme bir kez ölçümü
    bozdu (bkz. `measure.py` ön kontrolü) — kök yanlış çözülünce örnek
    dizinleri bulunamıyor ve araç "hiçbir şey yok" diye rapor veriyor.
    """
    for parent in Path(__file__).resolve().parents:
        if (parent / "Bizigo.sln").exists():
            return parent

    return None


@dataclass
class Literal:
    """Kuralın aradığı tek bir dizge ve örneklerdeki durumu."""

    field: str
    operator: str
    value: str
    verdict: str = ABSENT

    #: `substring_only` için: dizgeyi içine alan sözcükler. Kanıt olmadan
    #: "yanlış sebeple eşleşiyor" bir iddia; bunlarla bir gözlem.
    swallowed_by: list[str] = field(default_factory=list)

    #: Kaç örnek satırında geçtiği — sıfır olmayan ama tek satırlık bir
    #: eşleşme, örneklemin o deseni zar zor taşıdığını söylüyor.
    lines: int = 0

    #: `absent` için: örneklerde duran **yakın** sözcükler.
    #:
    #: Ölçülmüş bir vaka: `fortigate_user_auth_fail` `status: 'failure'`
    #: arıyor, FortiGate `status="failed"` yazıyor. Araç ikisini de "yok" diye
    #: raporlasaydı, kelime hatası **örneklem boşluğu** gibi okunurdu — ve
    #: örneklem boşluğu "yapacak bir şey yok" demek, kelime hatası ise
    #: "kuralı düzelt".
    #:
    #: `RST`/`Reset` ile aynı sınıf: kural vendor'ın sözlüğünü değil kendi
    #: sözlüğünü kullanıyor.
    near_misses: list[str] = field(default_factory=list)


@dataclass
class RuleReport:
    name: str
    product: str
    literals: list[Literal] = field(default_factory=list)

    @property
    def verdict(self) -> str:
        """Kuralın kutusu — **en kötü** dizgeden geliyor.

        Gerekçe: kural bütün koşullarını sağlamak zorunda (`condition:
        selection` bir AND). Tek bir dizge örneklerde yoksa kural eşleşemez,
        diğerleri ne kadar sağlam olursa olsun. İyimser tarafa yuvarlamak,
        eşleşmeyen bir kuralı "eşleme sorunu" diye raporlardı.
        """
        if not self.literals:
            return PRESENT

        verdicts = {item.verdict for item in self.literals}

        if ABSENT in verdicts:
            return ABSENT
        if SUBSTRING_ONLY in verdicts:
            return SUBSTRING_ONLY

        return PRESENT


#: `detection:` bloğundaki `alan|operatör: değer` satırları.
_FIELD_LINE = re.compile(r"^\s{4}([A-Za-z_][A-Za-z0-9_]*)((?:\|[a-z]+)*):\s*(.*)$")
_LIST_ITEM = re.compile(r"^\s{6}-\s*(.+)$")


def _clean(raw: str) -> str:
    text = raw.strip()

    if text.startswith("#") or not text:
        return ""

    text = text.split(" #")[0].strip()

    for quote in ("'", '"'):
        if len(text) >= 2 and text.startswith(quote) and text.endswith(quote):
            return text[1:-1]

    return text


def rule_literals(rule_text: str) -> list[Literal]:
    """Kuralın aradığı dizgeler. Sayısal değerler **atlanıyor**.

    Sebep: `dstport: 443` bir metin araması değil; örnek satırında `443`
    dizgesini aramak `443` içeren her sayıya denk gelir ve ölçüm gürültüye
    boğulur. Port/sayı eşleşmesi kolonun kendisinde çözülüyor.
    """
    body = re.search(r"(?:^|\n)detection:", rule_text)

    if body is None:
        return []

    block = re.split(r"\n[a-z_]+:", rule_text[body.end() :])[0]
    found: list[Literal] = []
    pending: Literal | None = None

    for line in block.splitlines():
        item = _LIST_ITEM.match(line)

        if item and pending is not None:
            value = _clean(item.group(1))

            if value and not value.isdigit():
                found.append(Literal(pending.field, pending.operator, value))
            continue

        match = _FIELD_LINE.match(line)

        if match is None:
            continue

        name, operators, inline = match.groups()

        if name == "condition":
            continue

        pending = Literal(name, operators.lstrip("|") or "equals", "")
        value = _clean(inline)

        if value and not value.isdigit():
            found.append(Literal(pending.field, pending.operator, value))

    return found


def classify(value: str, corpus: str, free_text: bool = True) -> tuple[str, list[str], int]:
    """Dizgenin örnek gövdesindeki durumu: (karar, yutan sözcükler, satır sayısı).

    Ölçüt **sözcük sınırı**. `RST` örneklerde geçiyordu ama yalnızca `first` ve
    `burst` içinde; bir varlık kontrolü onu "var" der ve kuralı sağlam sanardı.
    Aranan şey dizgenin kendi başına durup durmadığı.

    `free_text=False` ise sözcük sınırı **aranmıyor**: yapısal bir alanda
    (IP, port, eşlenmiş kolon) önek eşleşmesi kuralın kastettiği şeyin ta
    kendisi. `srcip|startswith: '203.0.113.'` ham gövdede `203.0.113.7`'nin
    içinde görünüyor ve bu bir kusur değil, **doğru davranış**.
    """
    if not value:
        return PRESENT, [], 0

    lowered = corpus.lower()
    needle = value.lower()

    if needle not in lowered:
        return ABSENT, [], 0



    lines = sum(1 for line in corpus.splitlines() if needle in line.lower())

    # Sözcük sınırında en az bir kez geçiyor mu.
    bounded = re.compile(
        r"(?<![A-Za-z0-9])" + re.escape(needle) + r"(?![A-Za-z0-9])"
    )

    if bounded.search(lowered) or not free_text:
        return PRESENT, [], lines

    # Yalnızca daha uzun sözcüklerin içinde. Yutanları topluyoruz: iddia değil
    # gözlem sunmak için.
    swallowed = sorted(
        {
            word
            for word in re.findall(r"[A-Za-z0-9_-]+", corpus)
            if needle in word.lower() and word.lower() != needle
        }
    )[:5]

    return SUBSTRING_ONLY, swallowed, lines


#: Yakınlık ölçütü: dizgenin ilk bu kadar karakteri.
#:
#: Dört, `fail`i (`failure` ↔ `failed`) yakalayacak kadar kısa, `Tear`ı bütün
#: `Teardown`lara bağlamayacak kadar uzun. Kısaltmak gürültü, uzatmak sessizlik.
NEAR_PREFIX = 4


def near_misses(value: str, corpus: str) -> list[str]:
    """Örneklerde duran **yakın** sözcükler — kelime hatası mı, boşluk mu.

    `absent` iki tamamen farklı şey olabilir ve cevapları zıt:

    * Örneklem o senaryoyu hiç taşımıyor → **yapacak bir şey yok**, kural
      paydadan düşmeli.
    * Kural vendor'ın sözlüğünü kullanmıyor → **kuralı düzelt**.

    İkisini tek "yok" altında toplamak, düzeltilebilir bir hatayı ölçülemez
    bir eksiklik gibi gösterirdi.
    """
    prefix = value[:NEAR_PREFIX].lower()

    if len(value) <= NEAR_PREFIX or not prefix.isalpha():
        return []

    return sorted(
        {
            word
            for word in re.findall(r"[A-Za-z][A-Za-z0-9_-]*", corpus)
            if word.lower().startswith(prefix) and word.lower() != value.lower()
        }
    )[:5]


def load_samples(root: Path, product: str) -> str:
    directory = root / SAMPLES.get(product, "")

    if not product or not directory.is_dir():
        return ""

    return "\n".join(
        path.read_text(encoding="utf-8", errors="replace")
        for path in sorted(directory.glob("*.log"))
    )


def examine(
    rule_text: str, name: str, samples: str, product: str, text_fields: frozenset[str]
) -> RuleReport:
    report = RuleReport(name=name, product=product)

    for literal in rule_literals(rule_text):
        literal.verdict, literal.swallowed_by, literal.lines = classify(
            literal.value, samples, free_text=literal.field in text_fields
        )

        if literal.verdict == ABSENT:
            literal.near_misses = near_misses(literal.value, samples)
        report.literals.append(literal)

    return report


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="Eşleşmeyen kural neden eşleşmiyor (T30)")
    parser.add_argument("--corpus", default="", help=f"varsayılan {CORPUS}")
    parser.add_argument("--json", default="")
    args = parser.parse_args(argv)

    root = repo_root()

    if root is None:
        print("Depo kökü bulunamadı (`Bizigo.sln`). Araç depo ağacından koşmalı.", file=sys.stderr)
        return 2

    corpus = Path(args.corpus) if args.corpus else root / CORPUS

    # Boş korpusa "0 kural incelendi" demek, ölçümün yapıldığı izlenimi
    # bırakırdı. Korpus T32'de taşındı; eski yolu okuyan bir araç sessizce
    # sıfır rapor ederdi ve o sessizlik iki koşum boyunca sürdü.
    if not corpus.is_dir():
        print(
            f"Kural korpusu bulunamadı: {corpus}\n"
            "Korpus T32'de `catalog/sigma/rules/`'a taşındı; `prototypes/t30-sigma/rules`\n"
            "artık boş. Eski yolu okuyan bir araç sıfır kural rapor eder ve o sıfır\n"
            "'ölçüm yapıldı' diye okunur.",
            file=sys.stderr,
        )
        return 3

    rules = sorted(corpus.glob("*.yml"))

    if not rules:
        print(f"Korpus BOŞ: {corpus}", file=sys.stderr)
        return 3

    cache: dict[str, str] = {}
    reports: list[RuleReport] = []
    text_fields = free_text_fields()

    for path in rules:
        text = path.read_text(encoding="utf-8")
        product = ""
        source = re.search(r"logsource:(.*?)(?:\ndetection:|\Z)", text, re.S)

        if source:
            found = re.search(r"product:\s*(\S+)", source.group(1))
            product = found.group(1).strip("'\"") if found else ""

        if product not in cache:
            cache[product] = load_samples(root, product)

        reports.append(examine(text, path.name, cache[product], product, text_fields))

    buckets = {ABSENT: [], SUBSTRING_ONLY: [], PRESENT: []}

    for report in reports:
        buckets[report.verdict].append(report)

    # `relative_to` depo dışı bir korpusta patlıyordu (`--corpus /tmp/...`),
    # yani aracın kendisi ölçümü düşürüyordu. Yol gösterimi bir kolaylık;
    # kolaylık ölçümü düşüremez.
    try:
        shown = corpus.relative_to(root)
    except ValueError:
        shown = corpus

    print(f"Korpus: {shown} · {len(reports)} kural\n")
    print(f"{'Kutu':<18} {'Kural':>6}   Anlamı")
    print(f"{'-'*18} {'-'*6}   {'-'*46}")
    print(f"{'desen YOK':<18} {len(buckets[ABSENT]):>6}   örneklem kusuru — kapsam kararına GİRMEZ")
    print(f"{'yalnızca içinde':<18} {len(buckets[SUBSTRING_ONLY]):>6}   eşleşse bile YANLIŞ sebeple")
    print(f"{'desen var':<18} {len(buckets[PRESENT]):>6}   eşleşmiyorsa suç EŞLEMEDE")

    for title, key in (
        ("Örneklemde deseni olmayan kurallar", ABSENT),
        ("Yalnızca daha uzun sözcüklerin içinde geçenler", SUBSTRING_ONLY),
    ):
        if not buckets[key]:
            continue

        print(f"\n{title}:")

        for report in buckets[key]:
            for literal in report.literals:
                if literal.verdict != key:
                    continue

                if literal.swallowed_by:
                    detail = f"  ← {', '.join(literal.swallowed_by)}"
                elif literal.near_misses:
                    detail = f"  ⚠ örnekte var: {', '.join(literal.near_misses)}"
                else:
                    detail = ""
                print(
                    f"  {report.name:<34} {literal.field}|{literal.operator} "
                    f"= {literal.value!r}{detail}"
                )

    vocabulary = [
        (r.name, l)
        for r in reports
        for l in r.literals
        if l.verdict == ABSENT and l.near_misses
    ]

    if vocabulary:
        print(
            f"\n⚠ {len(vocabulary)} dizge 'yok' ama örneklerde YAKIN bir sözcük var — "
            "bunlar örneklem boşluğu DEĞİL, kuralın vendor sözlüğünü kullanmaması:"
        )

        for name, literal in vocabulary:
            print(f"  {name:<34} {literal.value!r} ↔ {', '.join(literal.near_misses)}")

    print(
        "\nOkuma notu: 'desen YOK' kutusundaki kurallar `match_ratio`'yu aşağı çekiyor\n"
        "ama sebebi EŞLEME DEĞİL — örneklem o deseni hiç taşımıyor. Kapsam kararı\n"
        "bunları paydadan düşmeden verilirse, örneklemin darlığı ürünün yetersizliği\n"
        "diye okunur."
    )

    if args.json:
        Path(args.json).write_text(
            json.dumps([asdict(r) | {"verdict": r.verdict} for r in reports], ensure_ascii=False, indent=2),
            encoding="utf-8",
        )
        print(f"\nJSON: {args.json}")

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
