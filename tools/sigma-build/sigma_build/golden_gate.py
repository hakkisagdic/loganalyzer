"""Kapı 3 — üretilen SQL altın örneklere karşı doğru sonucu veriyor mu (T32).

İki soru, iki ayrı yer — bilerek
---------------------------------
Bu kapı **iki farklı şeyi** ölçebilirdi ve ikisini tek kapıya koymak, hangisinin
kırıldığını belirsiz bırakırdı:

| Soru | Nerede | Neden orada |
| --- | --- | --- |
| **Derleme doğruluğu** — beyan edilmiş bir kural beklediği şeyi buluyor mu | **Kapı** | Kural başına, verinin şeklinden bağımsız: ya bulur ya bulmaz |
| **Kapsam** — kaç kural bir şey yakalıyor | **Ölçüm** | Verinin şekline bağlı; kapı yapılırsa bir vendor'ın payı kaydığında kırmızı yanar ve sebebi kuralda değil veride olur |

`CLAUDE.md` §6: *"bir testin geçme sebebinin duvar saatiyle ilgisi olmamalı"* —
buradaki karşılığı, bir kapının kırılma sebebinin **veri dağılımıyla** ilgisi
olmaması. Beyan edilmiş bir beklenti (`at_least_one` / `none`) dağılımdan
bağımsız; "24 kuralın 6'sı eşleşiyor" değil.

Veri yoksa ölçüm **yapılmıyor** — T30'un protokolü
--------------------------------------------------
Boş bir görünüme karşı koşulan kapı her kural için "eşleşme yok" üretir ve o
tablo "kurallar bozuk" diye okunur. Oysa bozuk olan veri.

Bu yüzden ön kontrol var ve **geçemezse kapı hiç koşmuyor** (çıkış kodu 3).
T30'un `measure.py`'ında aynı kontrol duruyor ve gerekçesi orada ölçülmüş: bir
koşumda tabloda önceki turdan kalma tek vendor'lı sentetik veri vardı, "boş mu"
sorusunun cevabı hayırdı, ölçüm geçti ve **%0 eşleşme** üretti. O sıfır,
eşlemenin değil verinin sonucuydu.

Ön kontrol üç durumu ayrı raporluyor, çünkü üçünün cevabı farklı: sorgu hata
verdi (kurulum), tablo boş (yükleyici koşmamış), vendor eksik (o vendor'ın
kuralları ölçülemez).

Kapı boşken sessizce yeşil kalmıyor
------------------------------------
Beyan listesi boşsa kapı sıfır sorgu sorup geçerdi — Kapı 2'de `EXPLAIN SYNTAX`
kusurunun kural seti üretime çıkana kadar görünmemesinin sebebi tam olarak
buydu. Burada aynı hatayı yapmamak için kapı **yapısal** bir şey istiyor: en az
bir `at_least_one` **ve** en az bir `none` beklentisi. İkisi birden yoksa kapı
"eşleşen ile eşleşmeyeni ayırt edebildiğini" hiç göstermemiş olur.

Eşik yok — sayı yok
-------------------
Kapıda "en az N kural eşleşmeli" diye bir şey yok. Eşik, verinin şekli
değiştiğinde kırmızı yanar ve o kırmızının sebebi kural setinde değil veride
olur. Kapsam sayısı **raporlanıyor**, kapı yapılmıyor.
"""

from __future__ import annotations

import json
import re
from dataclasses import dataclass
from pathlib import Path

from sigma_build.clickhouse import post_sql

__all__ = [
    "EXPECTATIONS_PATH",
    "UndeclaredNote",
    "load_undeclared",
    "rule_titles",
    "EXPECT_AT_LEAST_ONE",
    "EXPECT_NONE",
    "CLASS_INVARIANT",
    "CLASS_CORPUS_GAP",
    "Expectation",
    "PrecheckResult",
    "load_expectations",
    "expectations_text",
    "check_corpus_shape",
    "precheck",
    "evaluate",
    "GoldenResult",
]

#: Beyanlar **girdi** — `catalog/` altında, üretilen SQL'in yanında değil.
EXPECTATIONS_PATH = Path("catalog") / "sigma" / "expectations.json"

#: Kural en az bir satır döndürmeli. "Bu kural gerçekten bir şey yakalıyor."
EXPECT_AT_LEAST_ONE = "at_least_one"

#: Kural hiçbir satır döndürmemeli. Yanlış pozitif bekçisi — ve kapının
#: **ayırt edebildiğinin** kanıtı: her şeyi eşleştiren bozuk bir kapı burada
#: kırmızı yanar.
EXPECT_NONE = "none"

_EXPECTATIONS = frozenset({EXPECT_AT_LEAST_ONE, EXPECT_NONE})

#: `none` beklentisinin **hangi iddiayı** taşıdığı. İki tür var ve kırmızı
#: yandıklarında **zıt** şeyler söylüyorlar:
#:
#: * `invariant` — "bu kural bu veride asla eşleşmemeli". Kırmızı = **kötü
#:   haber**: kural ya eşleme bozuldu, yanlış pozitif doğdu.
#: * `corpus_gap` — "bu desen altın örneklerde henüz yok". Kırmızı = **iyi
#:   haber**: korpus genişledi, artık veri var, beyan `at_least_one`'a
#:   dönüştürülebilir.
#:
#: Tek kutuda dursalardı kırmızının anlamı okunamazdı — ve sayıları da
#: karışırdı: biri azalması beklenmeyen, diğeri azalması **beklenen** taraf.
#: Bu turda aynı ayrımı dördüncü kez kuruyoruz (`gated_closeable` /
#: `gated_upstream`, `EXPECTED_GATED_*`, `undeclared`, ve şimdi bu).
CLASS_INVARIANT = "invariant"
CLASS_CORPUS_GAP = "corpus_gap"
_CLASSES = frozenset({CLASS_INVARIANT, CLASS_CORPUS_GAP})

#: Altın örneklerin taşıdığı vendor'lar (F1 kataloğu). Ön kontrol her birinin
#: veride görünüp görünmediğine ayrı bakıyor: biri eksikse o vendor'ın kuralları
#: **ölçülemez** ve sonuçları "kural bozuk" diye okunmamalı.
GOLDEN_VENDORS: tuple[tuple[str, str], ...] = (
    ("device_vendor_name", "Cisco"),
    ("device_vendor_name", "Fortinet"),
    ("device_vendor_name", "MikroTik"),
    ("metadata_product_name", "nginx"),
)


@dataclass(frozen=True)
class Expectation:
    rule_id: str
    file_name: str
    expect: str
    #: Beklentinin **neden** o olduğu. Boş bırakılamaz: gerekçesiz bir beklenti,
    #: bir gün kırıldığında "herhâlde veri değişmiştir" diye gevşetilir.
    why: str
    #: Yalnızca `none` için: `invariant` mı `corpus_gap` mı. Kırmızının anlamı
    #: buna bağlı ve `none` beyanlarında **zorunlu**.
    kind: str | None = None

    def __post_init__(self) -> None:
        if self.expect not in _EXPECTATIONS:
            raise ValueError(
                f"{self.rule_id}: bilinmeyen beklenti {self.expect!r}. "
                f"Beklenen: {EXPECT_AT_LEAST_ONE} | {EXPECT_NONE}"
            )
        if not self.why.strip():
            raise ValueError(
                f"{self.rule_id}: beklentinin gerekçesi yok. Gerekçesiz bir beklenti, "
                "kırıldığı gün 'herhâlde veri değişmiştir' diye gevşetilir."
            )
        if self.expect == EXPECT_NONE:
            if self.kind not in _CLASSES:
                raise ValueError(
                    f"{self.rule_id}: `{EXPECT_NONE}` beklentisi `kind` istiyor "
                    f"({CLASS_INVARIANT} | {CLASS_CORPUS_GAP}). Kırmızı yandığında "
                    "'kural bozuldu' mu 'korpus genişledi' mi dediği buna bağlı — "
                    "ikisi zıt haberler."
                )
        elif self.kind is not None:
            raise ValueError(
                f"{self.rule_id}: `kind` yalnızca `{EXPECT_NONE}` beklentileri için."
            )


@dataclass(frozen=True)
class UndeclaredNote:
    """**Bilerek** beyan edilmemiş bir kural — ve sebebi.

    §8'in `Pending`/`Exempt` ayrımının buradaki hâli. İki tür beyansızlık var ve
    tek listede durursalar "liste doldu mu" sorusu asla cevaplanamaz:

    * **Ölçüm bekleyen** — beyan yazılacak, henüz veri yok. Azalması beklenen.
    * **Bilerek beyansız** — beyan yazılamaz ve sebebi kayıtlı. Azalması
      beklenmeyen.

    En pahalı örnek `asa_teardown_rst`'ti: `--discover` "eşleşti" diyordu ama
    eşleşme `first`/`burst` sözcüklerindendi. `at_least_one` yazmak bir yanlış
    pozitifi kutsardı; beyansız bırakmanın **sebebi** ancak burada durabilirdi.
    """

    rule_id: str
    why: str

    def __post_init__(self) -> None:
        if not self.why.strip():
            raise ValueError(f"{self.rule_id}: beyansızlığın gerekçesi yok.")


@dataclass(frozen=True)
class PrecheckResult:
    ok: bool
    problems: tuple[str, ...]
    vendor_rows: dict[str, int]
    total_rows: int


@dataclass(frozen=True)
class GoldenResult:
    file_name: str
    expect: str
    rows: int

    @property
    def passed(self) -> bool:
        return self.rows > 0 if self.expect == EXPECT_AT_LEAST_ONE else self.rows == 0


def load_expectations(path: Path) -> list[Expectation]:
    if not path.is_file():
        return []
    document = json.loads(path.read_text(encoding="utf-8"))
    return [
        Expectation(
            rule_id=entry["rule_id"],
            file_name=entry["file_name"],
            expect=entry["expect"],
            why=entry["why"],
            kind=entry.get("kind"),
        )
        for entry in document.get("expectations", [])
    ]


def load_undeclared(path: Path) -> list[UndeclaredNote]:
    if not path.is_file():
        return []
    document = json.loads(path.read_text(encoding="utf-8"))
    return [
        UndeclaredNote(rule_id=entry["rule_id"], why=entry["why"])
        for entry in document.get("undeclared", [])
    ]


def rule_titles(output_dir: Path) -> dict[str, str]:
    """Dosya adı → kural başlığı, manifest'ten.

    `--discover` çıktısı UUID basıyordu ve beyan yazacak kişi her seferinde
    `grep -rl` ile kural adını çıkarmak zorunda kalıyordu. Ad zaten manifest'te.
    """
    manifest = output_dir / "manifest.json"
    if not manifest.is_file():
        return {}
    document = json.loads(manifest.read_text(encoding="utf-8"))
    return {
        f"{rule['rule_id']}.sql": (rule.get("source_path", "").split("/")[-1] or rule.get("title", ""))
        for rule in document.get("rules", [])
    }


def expectations_text(
    expectations: list[Expectation] | tuple[Expectation, ...],
    undeclared: list[UndeclaredNote] | tuple[UndeclaredNote, ...] | None = None,
) -> str:
    """Beyan dosyasının metni. Tarihsiz ve sıralı — üretilen her şey gibi."""
    return (
        json.dumps(
            {
                "_comment": (
                    "Kural başına beklenen altın örnek sonucu. Kapı 3 bunları canlı "
                    "ClickHouse'a karşı doğruluyor. KAPSAM SAYISI BURADA DEĞİL: kapsam "
                    "verinin şekline bağlı ve kapı yapılırsa bir vendor'ın payı "
                    "kaydığında kırmızı yanar."
                ),
                "expectations": [
                    {
                        "rule_id": expectation.rule_id,
                        "file_name": expectation.file_name,
                        "expect": expectation.expect,
                        **({"kind": expectation.kind} if expectation.kind else {}),
                        "why": expectation.why,
                    }
                    for expectation in sorted(expectations, key=lambda e: e.rule_id)
                ],
                # BİLEREK beyansız kurallar — "ölçüm bekliyor"dan ayrı liste.
                # İkisi tek listede olsaydı "beyan listesi tamamlandı mı"
                # sorusunun cevabı asla evet olamazdı (§8).
                "undeclared": [
                    {"rule_id": note.rule_id, "why": note.why}
                    for note in sorted(undeclared or (), key=lambda n: n.rule_id)
                ],
            },
            indent=2,
            ensure_ascii=False,
        )
        + "\n"
    )


def check_corpus_shape(
    expectations: list[Expectation] | tuple[Expectation, ...],
    produced_rules: int = 1,
) -> list[str]:
    """Kapı, ayırt edebildiğini gösterebiliyor mu — **ClickHouse gerektirmiyor**.

    Boş bir beyan listesi kapıyı sıfır sorgu sorup geçen bir şeye çevirirdi.
    Yalnızca `at_least_one` beyanları olsa da yetmez: her şeyi eşleştiren bozuk
    bir kapı da hepsini geçerdi. En az bir `none` beklentisi, kapının **ayırt
    ettiğini** gösteren tek şey.

    `produced_rules` neden var
    --------------------------
    Kapının koşulu **"üretilen her kural kümesi beyanlı olmalı"**; sıfır kural
    üretiliyorsa sıfır beyan gerekiyor ve bu bir gevşetme değil, koşulun kendisi.

    Ayrım önemli çünkü iki tuzağın arasından geçiyor. Kapıyı "kural seti gelince
    bağlarız" diye ertelemek, bu turda bulunan **hazırlanmış ama bağlanmamış**
    desenini kurardı. Ama bugün koşulsuz zorlamak da, ilgisiz bir işin bitmesini
    bekleyen ve o yüzden **ilk günden kırmızı** yanan bir kapı yaratırdı — ve
    `ci.yml`'ın `yamllint` notunda yazdığı gibi, ilk gün kırmızı yanan bir kapı
    ya gevşetilir ya devre dışı bırakılır.

    Koşul üretilen kural sayısına bağlandığında ikisi de olmuyor: kapı bugünden
    CI'da, ve ilk kural üretildiği anda **kendiliğinden** diş kazanıyor.
    """
    problems: list[str] = []

    if not expectations:
        if produced_rules == 0:
            return problems
        problems.append(
            f"{produced_rules} kural üretiliyor ama beyan listesi boş — kapı sıfır sorgu "
            f"sorup geçerdi. {EXPECTATIONS_PATH} içine en az bir `{EXPECT_AT_LEAST_ONE}` "
            f"ve bir `{EXPECT_NONE}` beklentisi gerekiyor."
        )
        return problems

    kinds = {expectation.expect for expectation in expectations}
    if EXPECT_AT_LEAST_ONE not in kinds:
        problems.append(f"hiç `{EXPECT_AT_LEAST_ONE}` beklentisi yok — kapı 'eşleşiyor' diyemiyor.")
    if EXPECT_NONE not in kinds:
        problems.append(
            f"hiç `{EXPECT_NONE}` beklentisi yok — her şeyi eşleştiren bozuk bir kapı da "
            "bu listeyi geçerdi."
        )

    duplicates = sorted(
        {e.rule_id for e in expectations if sum(1 for x in expectations if x.rule_id == e.rule_id) > 1}
    )
    if duplicates:
        problems.append(f"aynı kural için birden fazla beklenti: {duplicates}")

    return problems


def precheck(**connection: object) -> PrecheckResult:
    """Altın örnekler gerçekten yüklü mü. Geçemezse kapı **hiç koşmamalı**."""
    problems: list[str] = []
    vendor_rows: dict[str, int] = {}

    ok, body = post_sql("SELECT count() FROM events_ocsf", **connection)  # type: ignore[arg-type]
    if not ok:
        return PrecheckResult(
            ok=False,
            problems=(f"`events_ocsf` sorgulanamadı — görünüm yok ya da kimlik yanlış: {body.strip()[:200]}",),
            vendor_rows={},
            total_rows=0,
        )

    total = int(body.strip() or 0)
    if total == 0:
        return PrecheckResult(
            ok=False,
            problems=("`events_ocsf` boş — altın örnek yükleyicisi koşmamış. Kapı ölçüm yapmamalı.",),
            vendor_rows={},
            total_rows=0,
        )

    for column, value in GOLDEN_VENDORS:
        ok, body = post_sql(
            f"SELECT count() FROM events_ocsf WHERE {column} = '{value}'",
            **connection,  # type: ignore[arg-type]
        )
        rows = int(body.strip() or 0) if ok else 0
        vendor_rows[value] = rows
        if rows == 0:
            problems.append(
                f"`{value}` için veri yok — o vendor'ın kurallarının sonucu "
                "'kural bozuk' diye okunmamalı."
            )

    return PrecheckResult(ok=not problems, problems=tuple(problems), vendor_rows=vendor_rows, total_rows=total)


#: Üretilen dosyanın başlığındaki yorumlar SQL'in parçası ve olduğu gibi
#: gönderiliyor; sayım için sorguyu sarmalamak gerekiyor.
_TRAILING_SEMICOLON = re.compile(r";\s*$")


def count_rows(sql: str, *, limit: int = 1, **connection: object) -> int:
    """Kuralın döndürdüğü satır sayısı — `limit` ile sınırlı.

    `LIMIT 1` yeterli: soru "hiç mi, en az bir mi", "kaç tane" değil. Tam sayım
    1,1 milyon satırlık bir görünümde ölçtüğümüz şeyi değil makinenin o anki
    yükünü ölçerdi.
    """
    inner = _TRAILING_SEMICOLON.sub("", sql.strip())
    ok, body = post_sql(f"SELECT count() FROM ({inner} LIMIT {limit})", **connection)  # type: ignore[arg-type]
    if not ok:
        raise RuntimeError(f"Kural sorgusu reddedildi (Kapı 2 bunu yakalamalıydı): {body.strip()[:300]}")
    return int(body.strip() or 0)


def evaluate(output_dir: Path, expectations: list[Expectation] | tuple[Expectation, ...], **connection: object) -> list[GoldenResult]:
    results: list[GoldenResult] = []
    for expectation in sorted(expectations, key=lambda e: e.rule_id):
        path = output_dir / expectation.file_name
        if not path.is_file():
            raise FileNotFoundError(
                f"{expectation.rule_id}: beyan var ama üretilmiş SQL yok ({path.name}). "
                "Kural kapıya takıldıysa beyanı da kaldırılmalı — yoksa beyan listesi "
                "var olmayan bir kapsamı iddia eder."
            )
        rows = count_rows(path.read_text(encoding="utf-8"), **connection)
        results.append(GoldenResult(file_name=expectation.file_name, expect=expectation.expect, rows=rows))
    return results


def _main(argv: list[str] | None = None) -> int:
    import argparse
    import sys

    from sigma_build.manifest import MANIFEST_NAME, OUTPUT_DIR
    from sigma_build.view_columns import repo_root

    parser = argparse.ArgumentParser(description="Kapı 3: altın örneklere karşı doğru sonuç.")
    parser.add_argument("--clickhouse-url", default="http://localhost:8123")
    parser.add_argument("--user", "--clickhouse-user", dest="user", default="bizigo")
    parser.add_argument("--password", "--clickhouse-password", dest="password", default="bizigo")
    parser.add_argument("--database", "--clickhouse-database", dest="database", default="bizigo")
    parser.add_argument("--output", type=Path, default=None)
    parser.add_argument("--expectations", type=Path, default=None)
    parser.add_argument(
        "--shape-only",
        action="store_true",
        help="Yalnızca beyan listesinin şekli — ClickHouse'a hiç bağlanmaz (CI kapısı)",
    )
    parser.add_argument(
        "--discover",
        action="store_true",
        help="Beyan YAZMAZ: her üretilen kuralı koşturup hangisinin satır döndürdüğünü basar",
    )
    args = parser.parse_args(argv)

    connection = {
        "url": args.clickhouse_url,
        "user": args.user,
        "password": args.password,
        "database": args.database,
        "timeout": 60.0,
    }
    output_dir = args.output or (repo_root() / OUTPUT_DIR)
    expectations_path = args.expectations or (repo_root() / EXPECTATIONS_PATH)

    if args.shape_only:
        # ClickHouse'a HİÇ bağlanmıyor: bu yarı, veri yüklü olmayan bir CI işinde
        # de anlamlı tek soruyu soruyor.
        produced = len(list(output_dir.glob("*.sql")))
        shape = check_corpus_shape(load_expectations(expectations_path), produced)
        for problem in shape:
            print(f"  {problem}", file=sys.stderr)
        if shape:
            print("\n✗ Beyan listesi kapının ayırt edebildiğini göstermiyor.", file=sys.stderr)
            return 1
        print(
            f"✓ Beyan listesinin şekli tutarlı — {produced} üretilen kural, "
            f"{len(load_expectations(expectations_path))} beyan."
        )
        return 0

    # ÖN KONTROL — geçemezse ölçüm hiç yapılmıyor (çıkış 3).
    state = precheck(**connection)
    print(f"events_ocsf: {state.total_rows} satır · vendor dağılımı: {state.vendor_rows}")
    if not state.ok:
        for problem in state.problems:
            print(f"  {problem}", file=sys.stderr)
        print("\n✗ Altın örnekler hazır değil — kapı ölçüm YAPMADI.", file=sys.stderr)
        return 3

    if args.discover:
        # Beyan üretmiyor, **basıyor**. Beyan bir karar; aracın kendi ölçümünden
        # otomatik doğması, kapıyı bugünkü davranışın fotoğrafına çevirirdi ve
        # o kapı hiçbir şeyi kanıtlamaz.
        titles = rule_titles(output_dir)
        for path in sorted(output_dir.glob("*.sql")):
            rows = count_rows(path.read_text(encoding="utf-8"), **connection)
            ad = titles.get(path.name, "")
            print(f"  {'eşleşti ' if rows else 'boş     '} {ad:<34} {path.name}")
        print(
            "\n⚠️ `eşleşti` **doğru sebeple eşleşti** demek değil. Ölçüldü: "
            "`asa_teardown_rst` eşleşiyordu ve eşleşmesi `first`/`burst` "
            "sözcüklerindendi. Beyan yazarken örnek dosyanın içeriğine bakın, "
            "bu sayıya değil."
        )
        return 0

    expectations = load_expectations(expectations_path)
    produced = len(list(output_dir.glob("*.sql")))

    shape = check_corpus_shape(expectations, produced)
    if shape:
        for problem in shape:
            print(f"  {problem}", file=sys.stderr)
        print("\n✗ Beyan listesi kapının ayırt edebildiğini göstermiyor.", file=sys.stderr)
        return 1

    if produced == 0:
        print(
            "⚠️ Sıfır kural üretilmiş (kural seti çivisi boş) — Kapı 3'ün doğrulayacağı "
            "bir şey yok. Kapı koşuyor ve ilk kural üretildiği anda beyan istemeye başlıyor."
        )
        return 0

    results = evaluate(output_dir, expectations, **connection)
    failed = [result for result in results if not result.passed]

    for result in results:
        mark = "✓" if result.passed else "✗"
        print(f"  {mark} {result.file_name:<44} {result.expect:<14} {result.rows} satır")

    # KAPSAM — ölçüm, kapı DEĞİL. Sayı raporlanıyor; eşiği yok, çünkü eşik
    # verinin şekli değiştiğinde kırılır ve sebebi kuralda değil veride olur.
    print(
        f"\nkapsam (ÖLÇÜM, kapı değil): {len(expectations)}/{produced} üretilen kural beyanlı · "
        f"{sum(1 for r in results if r.expect == EXPECT_AT_LEAST_ONE and r.rows)} tanesi satır döndürdü"
    )
    if (output_dir / MANIFEST_NAME).is_file():
        counts = json.loads((output_dir / MANIFEST_NAME).read_text(encoding="utf-8"))["counts"]
        print(f"manifest: {counts}")

    # KAPININ KENDİ KAPSAMI — sessiz bir eksik, ölçüldüğü sanılan bir eksiktir.
    # İki liste ayrı (§8): bilerek beyansız olanlar azalması beklenmeyen taraf,
    # ölçüm bekleyenler azalması beklenen taraf.
    titles = rule_titles(output_dir)
    beyanli = {e.file_name for e in expectations}
    bilerek = {note.rule_id: note.why for note in load_undeclared(expectations_path)}
    bekleyen = [
        path.name
        for path in sorted(output_dir.glob("*.sql"))
        if path.name not in beyanli and path.stem not in bilerek
    ]

    if bilerek:
        print(f"\nbilerek beyansız ({len(bilerek)}) — azalması beklenmiyor:")
        for rule_id, why in sorted(bilerek.items()):
            print(f"  {titles.get(rule_id + '.sql', ''):<34} {why[:100]}")
    if bekleyen:
        print(f"\nölçüm bekleyen ({len(bekleyen)}) — beyan yazılacak:")
        for name in bekleyen:
            print(f"  {titles.get(name, ''):<34} {name}")

    if failed:
        print("\n✗ Beyan edilen sonucu vermeyen kural var.", file=sys.stderr)
        return 1

    print("✓ Beyan edilen her kural beklediği sonucu verdi.")
    return 0


if __name__ == "__main__":
    raise SystemExit(_main())
