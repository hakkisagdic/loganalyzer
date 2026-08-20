"""`db/clickhouse/*.sql` göçlerinden görünüm kolon kümesini türetir (T32, Kapı 1).

Neden türetiliyor, elle yazılmıyor
----------------------------------
Kapı 1 bir Sigma kuralının ürettiği SQL'in var olmayan bir kolona gidip
gitmediğine bakıyor. O kontrolün karşılaştıracağı liste **elle yazılırsa**
`CLAUDE.md` §7'deki `Produces<T>` deliğinin aynısı doğuyor — orada uçlar elle
yazılmış bir listeden toplanıyordu, üç uç dosyası listede yoktu, 16 uç kapıya
hiç görünmedi ve üç test yeşil kaldı.

Sürüklenme burada **asimetrik**, ve tehlikeli yönü sessiz olan:

* Görünüme kolon eklenir, liste güncellenmez → yeni kolona giden kural
  yanlışlıkla reddedilir. Gürültülü; birisi fark eder.
* Görünümden kolon çıkar, liste güncellenmez → o kolona giden kural kapıdan
  **geçer** ve ancak çalışma zamanında kırılır. Hiçbir şey kırmızı yanmaz.

Göç sırası neden şart
---------------------
"En son tanım kazanır" bu depoda teorik bir ihtimal değil: `0004` `events_otel`'i
`DROP VIEW` + `CREATE VIEW` ile yeniden yaratıyor. Yalnızca `0003`'ü okuyan bir
çıkarıcı, `events_ocsf` için aynı şey yapıldığı gün sessizce bayatlar.

Sıra `ClickHouseMigrator.cs`'nin sırasıyla **aynı** olmak zorunda, yoksa bu modül
canlı veritabanından başka bir gerçekliği modeller — sessiz ayrışmanın ta
kendisi. Migrator `StringComparer.Ordinal` ile dosya adına göre sıralıyor; biz de
öyle yapıyoruz. Ordinal sıra ancak `NNNN_` dolgulu ad kuralı korunduğu sürece
sayısal sırayla aynı şey, o yüzden ad kuralı burada **zorlanıyor**: uymayan bir
dosya sessizce yanlış yere sıralanmaktansa hata versin.

`CREATE VIEW IF NOT EXISTS` bir yeniden tanım DEĞİLDİR
------------------------------------------------------
Görünüm zaten varsa ClickHouse o ifadeyi **atlar**. "Son gördüğüm CREATE kazanır"
diyen bir model, `IF NOT EXISTS`'li ikinci bir tanımı canlıda olmayan bir
gerçeklik olarak okur. `0003` tam olarak bu biçimi kullanıyor.

Bu modül pySigma GEREKTİRMEZ; metin işi. Kapı 1'in ClickHouse'suz koşabilmesinin
sebebi bu.
"""

from __future__ import annotations

import json
import re
import sys
from dataclasses import dataclass
from pathlib import Path

__all__ = [
    "MigrationParseError",
    "ViewDefinition",
    "load_view_definitions",
    "view_definition",
    "split_statements",
    "snapshot_text",
    "repo_root",
    "MIGRATION_NAME_PATTERN",
    "SNAPSHOT_PATH",
]

#: `db/clickhouse/README.md`: "Dosya adı: `NNNN_kisa_ad.sql` — sıralı, sıfır dolgulu."
MIGRATION_NAME_PATTERN = re.compile(r"^\d{4}_[A-Za-z0-9_]+\.sql$")


class MigrationParseError(RuntimeError):
    """Göç dosyası bu modülün modelleyemediği bir şey içeriyor.

    **Bilerek sert.** Anlaşılmayan bir ifadeyi atlamak, kolon kümesini sessizce
    eksik bırakır ve Kapı 1 o eksikliği "kolon yok" diye okur — yani yanlış
    tarafta hata yapar. Anlaşılmayan şey görünür olsun.
    """


@dataclass(frozen=True)
class ViewDefinition:
    """Bir görünümün göçler uygulandıktan **sonraki** hâli."""

    name: str
    columns: tuple[str, ...]
    #: Bu tanımı yazan göç dosyasının adı — hata mesajlarında "nereye bakayım".
    source_file: str

    @property
    def column_set(self) -> frozenset[str]:
        return frozenset(self.columns)


# --------------------------------------------------------------------------- #
# İfade ayırıcı
# --------------------------------------------------------------------------- #

def split_statements(sql: str) -> list[str]:
    """Bir .sql metnini çalıştırılabilir ifadelere böler.

    ⚠️ Bu, `src/Bizigo.Storage.ClickHouse/SqlStatementSplitter.cs`'nin **ikinci
    kopyası** ve normalde §9 bunu yasaklar ("ortak yüzey varsa genişlet,
    kopyalama"). Sapmanın gerekçesi: o taraf C#, bu taraf Python ve ortak yüzey
    diye bir şey yok. Bedeli ayrışma riski; karşılığı `test_view_columns.py`
    içindeki, iki tarafın aynı fikirde olduğu hâlleri çivileyen testler
    (yorumdaki `;`, metin içindeki `;`, `''` kaçışı, blok yorum).

    Ayrıştırma kuralları C# tarafındakiyle birebir: `--` satır yorumu ve
    `/* */` blok yorumu düşürülür, tırnaklı metin (`'`, `"`, `` ` ``) olduğu gibi
    korunur ve içindeki `;` ayraç sayılmaz.
    """
    statements: list[str] = []
    current: list[str] = []
    i = 0
    n = len(sql)

    while i < n:
        c = sql[i]
        nxt = sql[i + 1] if i + 1 < n else "\0"

        if c == "-" and nxt == "-":
            while i < n and sql[i] != "\n":
                i += 1
            current.append("\n")
            i += 1
            continue

        if c == "/" and nxt == "*":
            i += 2
            while i + 1 < n and not (sql[i] == "*" and sql[i + 1] == "/"):
                i += 1
            i += 2
            continue

        if c in ("'", '"', "`"):
            quote = c
            current.append(c)
            i += 1
            while i < n:
                if sql[i] == "\\" and i + 1 < n:
                    current.append(sql[i])
                    current.append(sql[i + 1])
                    i += 2
                    continue

                current.append(sql[i])

                if sql[i] == quote:
                    # '' → kaçırılmış tırnak, metin devam ediyor
                    if i + 1 < n and sql[i + 1] == quote:
                        current.append(sql[i + 1])
                        i += 2
                        continue
                    break

                i += 1
            i += 1
            continue

        if c == ";":
            text = "".join(current).strip()
            if text:
                statements.append(text)
            current.clear()
            i += 1
            continue

        current.append(c)
        i += 1

    text = "".join(current).strip()
    if text:
        statements.append(text)

    return statements


# --------------------------------------------------------------------------- #
# Üst düzey tarama
# --------------------------------------------------------------------------- #

def _top_level_mask(text: str) -> list[bool]:
    """Her karakter için "parantez derinliği 0 ve tırnak dışında mı".

    Virgülle bölmek ve `FROM`'u bulmak için gerekiyor. Naif bir virgül bölmesi
    `multiIf(severity_num = 1, 9, …)` ifadesini yedi ayrı kolon sanardı —
    `0004` tam olarak bunu içeriyor, yani bu bir varsayım değil ölçülmüş bir
    hâl.
    """
    mask = [False] * len(text)
    depth = 0
    i = 0
    n = len(text)

    while i < n:
        c = text[i]

        if c in ("'", '"', "`"):
            quote = c
            i += 1
            while i < n:
                if text[i] == "\\" and i + 1 < n:
                    i += 2
                    continue
                if text[i] == quote:
                    if i + 1 < n and text[i + 1] == quote:
                        i += 2
                        continue
                    break
                i += 1
            i += 1
            continue

        if c == "(":
            depth += 1
        elif c == ")":
            depth -= 1
            if depth < 0:
                raise MigrationParseError(f"Fazladan ')' — dengesiz parantez: {text[:80]!r}…")
        else:
            mask[i] = depth == 0

        i += 1

    if depth != 0:
        raise MigrationParseError(f"Kapanmamış parantez: {text[:80]!r}…")

    return mask


def _find_top_level(text: str, mask: list[bool], keyword: str) -> int:
    """`keyword`'ün üst düzeydeki ilk konumu, yoksa -1.

    Üst düzey olmak şart: `SELECT extract(x FROM y) AS z FROM t` içinde ilk
    `FROM` kolon listesinin sonu DEĞİL.
    """
    for match in re.finditer(rf"\b{keyword}\b", text, re.IGNORECASE):
        if mask[match.start()]:
            return match.start()
    return -1


def _split_top_level_commas(text: str, mask: list[bool]) -> list[str]:
    items: list[str] = []
    start = 0
    for i, c in enumerate(text):
        if c == "," and mask[i]:
            items.append(text[start:i])
            start = i + 1
    items.append(text[start:])
    return [item.strip() for item in items if item.strip()]


# --------------------------------------------------------------------------- #
# Kolon adı çıkarımı
# --------------------------------------------------------------------------- #

_ALIAS = re.compile(
    r"\bAS\s+(?:`(?P<backtick>[^`]+)`|\"(?P<quoted>[^\"]+)\"|(?P<plain>[A-Za-z_]\w*))\s*$",
    re.IGNORECASE | re.DOTALL,
)

_BARE = re.compile(
    r"^(?:[A-Za-z_]\w*\s*\.\s*)?"
    r"(?:`(?P<backtick>[^`]+)`|\"(?P<quoted>[^\"]+)\"|(?P<plain>[A-Za-z_]\w*))$"
)


def _column_name(item: str, *, view: str, source_file: str) -> str:
    """Bir SELECT öğesinin görünümde göründüğü ad.

    Adsız ifade (`count()` gibi) **hata**: ClickHouse ona ifade metninden bir ad
    üretir ve o ada bir Sigma kuralının güvenilir biçimde vurması mümkün değil.
    Sessizce atlamak, kolonu "yok" göstererek yanlış tarafta hata yapardı.
    """
    item = item.strip()
    mask = _top_level_mask(item)

    alias = _ALIAS.search(item)
    if alias is not None and mask[alias.start()]:
        return alias.group("backtick") or alias.group("quoted") or alias.group("plain")

    bare = _BARE.match(item)
    if bare is not None:
        return bare.group("backtick") or bare.group("quoted") or bare.group("plain")

    raise MigrationParseError(
        f"{source_file}: `{view}` görünümünde adsız ifade: {item!r}. "
        "Görünüm kolonlarının adı olmalı — adsız bir kolona Sigma kuralı vuramaz."
    )


# --------------------------------------------------------------------------- #
# İfade tanıma
# --------------------------------------------------------------------------- #

_IDENT = r"(?:`[^`]+`|\"[^\"]+\"|[A-Za-z_]\w*)"

_DROP_VIEW = re.compile(rf"^DROP\s+VIEW\s+(?:IF\s+EXISTS\s+)?({_IDENT})\s*$", re.IGNORECASE | re.DOTALL)

_CREATE_VIEW = re.compile(
    r"^CREATE\s+(?:OR\s+REPLACE\s+)?(?:MATERIALIZED\s+)?VIEW\s+"
    r"(?P<if_not_exists>IF\s+NOT\s+EXISTS\s+)?"
    rf"(?P<name>{_IDENT})\s+"
    r"(?:TO\s+\S+\s+)?"
    r"AS\s+(?P<body>SELECT\b.*)$",
    re.IGNORECASE | re.DOTALL,
)


def _unquote(identifier: str) -> str:
    if identifier.startswith("`") and identifier.endswith("`"):
        return identifier[1:-1]
    if identifier.startswith('"') and identifier.endswith('"'):
        return identifier[1:-1]
    return identifier


def _select_columns(body: str, *, view: str, source_file: str) -> tuple[str, ...]:
    mask = _top_level_mask(body)

    select_at = _find_top_level(body, mask, "SELECT")
    if select_at != 0:
        raise MigrationParseError(f"{source_file}: `{view}` gövdesi SELECT ile başlamıyor.")

    from_at = _find_top_level(body, mask, "FROM")
    if from_at < 0:
        raise MigrationParseError(f"{source_file}: `{view}` içinde üst düzey FROM bulunamadı.")

    select_list = body[len("SELECT") : from_at]
    items = _split_top_level_commas(select_list, _top_level_mask(select_list))
    if not items:
        raise MigrationParseError(f"{source_file}: `{view}` kolon listesi boş.")

    if any(item.strip() == "*" or item.strip().endswith(".*") for item in items):
        raise MigrationParseError(
            f"{source_file}: `{view}` içinde `*` var. Kolon kümesi metinden türetilemez; "
            "görünüm kolonlarını açıkça yazın."
        )

    columns = [_column_name(item, view=view, source_file=source_file) for item in items]

    duplicates = {name for name in columns if columns.count(name) > 1}
    if duplicates:
        raise MigrationParseError(
            f"{source_file}: `{view}` içinde tekrarlanan kolon adı: {sorted(duplicates)}"
        )

    return tuple(columns)


# --------------------------------------------------------------------------- #
# Göçlerin uygulanması
# --------------------------------------------------------------------------- #

def migration_files(migrations_dir: Path) -> list[Path]:
    """Göç dosyaları, `ClickHouseMigrator.cs` ile **aynı** sırada.

    Migrator `Directory.GetFiles(dir, "*.sql").OrderBy(f => f, StringComparer.Ordinal)`
    yapıyor. Ordinal sıra ile sayısal sıra ancak `NNNN_` dolgusu korunduğu sürece
    aynı şey; uymayan bir ad sessizce yanlış yere düşeceği için burada reddediliyor.
    """
    if not migrations_dir.is_dir():
        raise MigrationParseError(f"Göç dizini yok: {migrations_dir}")

    files = sorted(migrations_dir.glob("*.sql"), key=lambda p: p.name)

    bad = [p.name for p in files if not MIGRATION_NAME_PATTERN.match(p.name)]
    if bad:
        raise MigrationParseError(
            f"Ad kuralına uymayan göç dosyası: {bad}. Beklenen biçim `NNNN_kisa_ad.sql` "
            "(db/clickhouse/README.md). Ordinal sıralamanın sayısal sırayla aynı olması buna bağlı."
        )

    if not files:
        raise MigrationParseError(f"{migrations_dir} içinde .sql göçü yok.")

    return files


def load_view_definitions(migrations_dir: Path | None = None) -> dict[str, ViewDefinition]:
    """Bütün göçleri sırayla uygular ve görünümlerin **son** hâlini döndürür."""
    migrations_dir = migrations_dir or (repo_root() / "db" / "clickhouse")

    views: dict[str, ViewDefinition] = {}

    for path in migration_files(migrations_dir):
        for statement in split_statements(path.read_text(encoding="utf-8")):
            dropped = _DROP_VIEW.match(statement)
            if dropped is not None:
                views.pop(_unquote(dropped.group(1)), None)
                continue

            created = _CREATE_VIEW.match(statement)
            if created is None:
                continue

            name = _unquote(created.group("name"))

            # `IF NOT EXISTS` + zaten tanımlı → ClickHouse bu ifadeyi ATLAR.
            # Burada da atlanmalı, yoksa canlıda olmayan bir gerçeklik modellenir.
            if created.group("if_not_exists") and name in views:
                continue

            views[name] = ViewDefinition(
                name=name,
                columns=_select_columns(created.group("body"), view=name, source_file=path.name),
                source_file=path.name,
            )

    return views


def view_definition(view: str, migrations_dir: Path | None = None) -> ViewDefinition:
    views = load_view_definitions(migrations_dir)
    if view not in views:
        raise MigrationParseError(
            f"Göçlerden sonra `{view}` diye bir görünüm yok. Tanımlı olanlar: {sorted(views)}"
        )
    return views[view]


def repo_root(start: Path | None = None) -> Path:
    """`Bizigo.sln` barındıran ilk üst dizin."""
    current = (start or Path(__file__)).resolve()
    for candidate in [current, *current.parents]:
        if (candidate / "Bizigo.sln").is_file():
            return candidate
    raise MigrationParseError(f"Depo kökü bulunamadı ({current} üzerinden `Bizigo.sln` aranıyor).")


# --------------------------------------------------------------------------- #
# Anlık görüntü — çıkarıcının canlı `DESCRIBE` ile sınanabilmesi için
# --------------------------------------------------------------------------- #

#: Depodaki türetilmiş kolon kümesi. Entegrasyon testi (C#) bunu okuyup canlı
#: ClickHouse'un `system.columns` çıktısıyla karşılaştırıyor.
#:
#: Neden dosya, neden Python'ı çağırmak değil: entegrasyon testi C#. Ortada
#: **dosya** olduğu için iki dil birbirini çağırmadan aynı sözleşmeye bakıyor —
#: `ui/openapi/bizigo-api.json`'ın oynadığı rolün aynısı.
SNAPSHOT_PATH = Path("detections") / "schema" / "view-columns.json"


def snapshot_text(migrations_dir: Path | None = None) -> str:
    """Türetilmiş kümenin dosyaya yazılacak metni.

    ⚠️ **Tarih yok** — bilerek. Sürüklenme kapısı birebir bayt karşılaştırması
    yapıyor; her koşumda değişen bir alan kapıyı yapısal olarak imkânsız kılar.
    Aynı gerekçe üretilen kural SQL'leri için de geçerli (T32 tasarımı §4).
    """
    views = load_view_definitions(migrations_dir)
    document = {
        "_comment": (
            "ÜRETİLMİŞ DOSYA — elle düzenlemeyin. "
            "Kaynak: db/clickhouse/*.sql, üretici: tools/sigma-build/sigma_build/view_columns.py. "
            "Yeniden üretmek için: python -m sigma_build.view_columns --write"
        ),
        "views": {
            name: {"source_file": definition.source_file, "columns": list(definition.columns)}
            for name, definition in sorted(views.items())
        },
    }
    return json.dumps(document, indent=2, ensure_ascii=False) + "\n"


def _main(argv: list[str] | None = None) -> int:
    import argparse

    parser = argparse.ArgumentParser(description="ClickHouse görünümlerinin kolon kümesini göçlerden türetir.")
    parser.add_argument("--migrations", type=Path, default=None, help="Göç dizini (varsayılan: db/clickhouse)")
    parser.add_argument("--view", default=None, help="Tek bir görünüm; verilmezse hepsi listelenir")
    parser.add_argument("--json", action="store_true", help="JSON bas")
    parser.add_argument("--write", action="store_true", help=f"{SNAPSHOT_PATH} dosyasını yeniden üret")
    parser.add_argument(
        "--check",
        action="store_true",
        help=f"{SNAPSHOT_PATH} depodakiyle birebir aynı mı — değilse farkı basıp düşer (CI kapısı)",
    )
    args = parser.parse_args(argv)

    if args.write or args.check:
        target = repo_root() / SNAPSHOT_PATH
        produced = snapshot_text(args.migrations)

        if args.write:
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_text(produced, encoding="utf-8")
            print(f"✓ {SNAPSHOT_PATH} güncellendi.")
            return 0

        # `--check` depodaki dosyaya DOKUNMUYOR: üzerine yazsaydı, düşen bir
        # kapıdan sonra aynı komutun ikinci koşumu sebepsiz yere geçerdi.
        # (`ui/scripts/generate-api-types.sh` aynı gerekçeyi taşıyor.)
        import difflib

        committed = target.read_text(encoding="utf-8") if target.is_file() else ""
        if committed == produced:
            print(f"✓ {SNAPSHOT_PATH} göçlerle birebir aynı.")
            return 0

        diff = difflib.unified_diff(
            committed.splitlines(keepends=True),
            produced.splitlines(keepends=True),
            fromfile=f"depo/{SNAPSHOT_PATH}",
            tofile="göçlerden üretilen",
        )
        print("".join(diff), end="")
        print(
            f"\n✗ Görünüm şeması değişmiş ama {SNAPSHOT_PATH} güncellenmemiş.\n"
            "  Çözüm: tools/sigma-build içinde `python -m sigma_build.view_columns --write` "
            "çalıştırıp sonucu commit edin.",
            file=sys.stderr,
        )
        return 1

    views = load_view_definitions(args.migrations)
    if args.view is not None:
        if args.view not in views:
            parser.error(f"`{args.view}` yok. Tanımlı: {sorted(views)}")
        views = {args.view: views[args.view]}

    if args.json:
        print(
            json.dumps(
                {
                    name: {"columns": list(v.columns), "source_file": v.source_file}
                    for name, v in sorted(views.items())
                },
                indent=2,
                ensure_ascii=False,
            )
        )
        return 0

    for name, definition in sorted(views.items()):
        print(f"{name}  ({len(definition.columns)} kolon, {definition.source_file})")
        for column in definition.columns:
            print(f"    {column}")
    return 0


if __name__ == "__main__":
    raise SystemExit(_main())
