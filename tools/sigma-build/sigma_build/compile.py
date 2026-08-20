"""Derleme hattının giriş noktası — tek komut, tekrarlanabilir çıktı (T32).

Pipeline **kopyalanmıyor, içe aktarılıyor**
-------------------------------------------
Derleme, sidecar'ın `app/sigma_pipeline.py`'ını (T31) doğrudan kullanıyor. Bir
kopya çıkarmak akla ilk gelen şeydi ve yanlış olurdu: aynı pipeline sidecar'ın
`/v1/sigma/compile` ucundan UI'daki "bu kuralı derle ve önizle" akışını da
besliyor. İki kopya ayrıştığı gün ekran bir SQL gösterir, build-time başka bir
SQL üretir, ve **hiçbir şey bunu söylemez.**

Sürüm sabitleri de aynı sebeple bekçili (`test_requirements.py`).

Boş korpus ile eksik kurulum aynı şey değil
--------------------------------------------
Kural yoksa çıktı sıfır kuraldır ve bu doğru bir cevap. Ama kural **varken**
pySigma kurulu değilse, sessizce sıfır kural üretmek felaket olurdu: sürüklenme
kapısı "depoda 21 dosya var, üretilen 0" der ve kırmızı yanar, ama kırmızının
sebebi kural setinde sanılır. Daha kötüsü, çıktı boşken `--write` çalıştıran biri
**bütün kuralları silerdi.**

O yüzden korpus doluyken backend yoksa `_load_backend()` **atıyor**.

`counts.written = 0` bugün doğru sayı: çivilenmiş korpus boş
(`catalog/sigma/ruleset.json`, `commit: null`). `run.ruleset_commit = null` bunun
sebebini söylüyor — "hiç kural yok" ile "hangi kuralları alacağımıza karar
verilmedi" farklı şeyler.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import subprocess
import sys
from pathlib import Path

from sigma_build.gate import (
    GATE_COLUMN_EXISTENCE,
    KIND_UNKNOWN_COLUMN,
    KIND_UNSUPPORTED_CONSTRUCT,
    REMEDY_PIPELINE_OR_SCHEMA,
    REMEDY_SCHEMA,
    REMEDY_UNKNOWN,
    Blocker,
    check_columns,
)
from sigma_build.manifest import (
    MANIFEST_NAME,
    OUTPUT_DIR,
    STATUS_FAILED,
    STATUS_GATED,
    STATUS_WRITTEN,
    RuleOutcome,
    RunHeader,
    build_manifest,
    check_output,
    transition_summary,
    write_output,
)
from sigma_build.ruleset import CATALOG_DIR, RULES_SUBDIR, load_pin
from sigma_build.view_columns import repo_root, view_definition

#: T31'in pipeline'ı sidecar paketinde. İçe aktarmak için `sidecar/` sys.path'e
#: giriyor — kopyalamak yerine (bkz. modül açıklaması).
SIDECAR_DIR = Path("sidecar")

#: Protokol eşleme tablosunun **depodaki** yeri.
#:
#: T31'in varsayılanı `/app/mappings` ve o **konteyner içindeki** yol — sidecar
#: imajında doğru, derleme zamanında yok. Ölçüldü: varsayılanla çağrıldığında
#: `FileNotFoundError: /app/mappings/ip_proto_name.yaml`.
#:
#: Aynı dosya iki yerden okunuyor ve bu bilinçli (F1 §9, tek kaynak): .NET tarafı
#: da aynı tabloyu okuyor, iki kopya olsaydı `proto` metni sessizce ayrışırdı.
#: Değişen tek şey yol.
MAPPINGS_DIR = Path("catalog") / "mappings"


def _load_pipeline():
    """T31'in pipeline modülü. Yoksa **atıyor**, sessizce boş dönmüyor."""
    import sys

    sidecar = repo_root() / SIDECAR_DIR
    if str(sidecar) not in sys.path:
        sys.path.insert(0, str(sidecar))
    from app import sigma_pipeline  # noqa: PLC0415

    return sigma_pipeline

__all__ = ["collect_outcomes", "current_header", "main"]


#: Sürümler **çividen** okunuyor, kurulu ortamdan değil.
#:
#: Kurulu sürümü yazmak daha "dürüst" görünüyor ama kriter A'yı kırıyordu:
#: manifest o zaman ortama duyarlı olur, pySigma'sız bir makinede `null`,
#: başka sürümlü bir makinede başka şey çıkar — yani *aynı girdi, aynı SQL*
#: iddiası **makineye** bağlanır. Girdinin tanımı çivi.
#:
#: Kurulu sürümün çividen farklı olması ayrı bir sorun ve ayrı bir bekçisi var
#: (`_assert_environment_matches_pin`): sessizce manifest'e sızmak yerine
#: derleme anında **atıyor**.
REQUIREMENTS_PATH = Path("tools") / "sigma-build" / "requirements.txt"


def _pinned_versions() -> dict[str, str]:
    import re  # noqa: PLC0415

    text = (repo_root() / REQUIREMENTS_PATH).read_text(encoding="utf-8")
    return {
        match.group(1): match.group(2)
        for match in re.finditer(r"^([A-Za-z0-9_.-]+)==(\S+)\s*$", text, re.MULTILINE)
    }


def _installed_version(name: str) -> str | None:
    from importlib.metadata import PackageNotFoundError, version  # noqa: PLC0415

    try:
        return version(name)
    except PackageNotFoundError:
        return None


def _assert_environment_matches_pin() -> None:
    """Kurulu pySigma çiviyle aynı mı — **derleme anında**, sessizce değil.

    Ayrışmış bir ortamda üretilen SQL çivinin vaat ettiğinden başka bir şey
    olur ve manifest bunu göstermez, çünkü manifest çiviyi yazıyor. Tek doğru
    davranış burada durmak.
    """
    pinned = _pinned_versions()
    for package in ("pySigma", "pysigma-backend-clickhouse"):
        expected = pinned.get(package)
        actual = _installed_version(package)
        if expected is None:
            raise RuntimeError(f"{package} {REQUIREMENTS_PATH} içinde sabitlenmemiş.")
        if actual is None:
            raise RuntimeError(
                f"{package} kurulu değil ama derlenecek kural var. "
                f"`pip install -r {REQUIREMENTS_PATH}` — sessizce sıfır kural üretmek, "
                "`--write` koşturan birinin bütün çıktıyı silmesi demek olurdu."
            )
        if actual != expected:
            raise RuntimeError(
                f"{package} ayrışmış: kurulu {actual}, çivide {expected}. "
                "Üretilen SQL çivinin vaat ettiğinden başka bir şey olurdu."
            )


def current_header() -> RunHeader:
    """Koşumun girdi tanımı — hepsi **girdinin** parçası, koşumun değil.

    Kural seti sabit bir commit SHA'sına çivili ve elle yükseltiliyor (§4).
    `pipeline_sha` T31'in kaynak dosyasının özeti: pipeline değiştiğinde bütün
    çıktılar değişiyor ve `--summary` bunu kaynak değişiminden ayırıyor.
    """
    root = repo_root()
    pipeline_path = root / SIDECAR_DIR / "app" / "sigma_pipeline.py"

    pipeline_sha = None
    pipeline_version = None
    if pipeline_path.is_file():
        digest = hashlib.sha256(pipeline_path.read_bytes()).hexdigest()
        pipeline_sha = f"sha256:{digest}"
        # Sürüm etiketi kaynağın özetinden türüyor. Elle tutulan bir etiket,
        # pipeline değişip etiket değişmediğinde 269 dosyayı aynı ad altında
        # yeniden anlamlandırırdı (§4).
        pipeline_version = f"bizigo-events-ocsf/{digest[:12]}"

    pin = load_pin(root / CATALOG_DIR)
    pinned = _pinned_versions()
    return RunHeader(
        view="events_ocsf",
        ruleset_commit=pin.commit,
        pipeline_version=pipeline_version,
        pipeline_sha=pipeline_sha,
        pysigma_version=pinned.get("pySigma"),
        backend_version=pinned.get("pysigma-backend-clickhouse"),
    )


def _declared_blockers(pipeline, text: str):
    """T31'in **beyanlarından** engel üretir — istisna metninden değil.

    İki ayrı beyan var ve ikisi de yapısal:

    * `describe()["schema_gaps"]` — hiçbir parser'ın üretmediği alanlar. T31 bunu
      `{alan: {remedy, reason}}` olarak veriyor, yani `remedy`'yi de **o**
      söylüyor; bizim tahmin etmemize gerek yok.
    * `VENDOR_EMPTY_COLUMNS` — belirli bir vendor'da hep boş kalan kolonlar
      (`routeros` + `activity_name` gibi). Bu, `describe()`'de açık değil ama
      modül düzeyinde **açık bir ad**; özel bir ada bağlanmıyoruz.

    Anahtar dikkat: `VENDOR_EMPTY_COLUMNS` **eşlenmiş kolon adıyla** anahtarlı
    (`activity_name`), kural ise Sigma adını taşıyor (`action`). Eşleme
    yapılmadan kesişim boş çıkar ve engel sessizce `unknown`'a düşerdi —
    ölçüldü, `routeros_drop_input` tam olarak öyle düşüyordu.

    Raporlanan ad **Sigma adı**, çünkü kuralı düzeltecek kişinin değiştireceği o;
    eşlendiği kolon mesajda duruyor.
    """
    gaps = pipeline.describe().get("schema_gaps", {}) or {}
    field_map = getattr(pipeline, "FIELD_MAP", {})
    vendor_empty = getattr(pipeline, "VENDOR_EMPTY_COLUMNS", {})
    empty_columns = {column for columns in vendor_empty.values() for column in columns}

    for field in pipeline.rule_fields(text):
        declared = gaps.get(field) if isinstance(gaps, dict) else None
        if declared is not None:
            yield Blocker(
                kind=KIND_UNKNOWN_COLUMN,
                column=field,
                message=f"`{field}` bu şemada eşlenemiyor: {declared.get('reason', '')}".strip(),
                remedy=declared.get("remedy") or REMEDY_PIPELINE_OR_SCHEMA,
            )
            continue

        column = field_map.get(field, field)
        if column in empty_columns:
            yield Blocker(
                kind=KIND_UNKNOWN_COLUMN,
                column=field,
                message=(
                    f"`{field}` → `{column}` bu vendor'da her zaman boş; "
                    "parser onu bilerek doldurmuyor"
                ),
                remedy=REMEDY_SCHEMA,
            )


def _transformation_error() -> type[BaseException]:
    """T31'in bilinçli reddi hangi tiple geliyor.

    Tembel çözülüyor: pySigma kurulu olmayan bir ortamda modülün içe
    aktarılması bunun yüzünden düşmesin.
    """
    from sigma.exceptions import SigmaTransformationError  # noqa: PLC0415

    return SigmaTransformationError


def _rule_id_of(text: str, path: Path) -> str:
    import re  # noqa: PLC0415

    match = re.search(r"^id:\s*(\S+)\s*$", text, re.MULTILINE)
    return match.group(1).strip("'\"") if match else path.stem


def _title_of(text: str, path: Path) -> str:
    import re  # noqa: PLC0415

    match = re.search(r"^title:\s*(.+?)\s*$", text, re.MULTILINE)
    return match.group(1).strip("'\"") if match else path.stem


def _rule_files() -> list[Path]:
    rules_dir = repo_root() / CATALOG_DIR / RULES_SUBDIR
    return sorted(rules_dir.rglob("*.yml")) if rules_dir.is_dir() else []


def collect_outcomes() -> list[RuleOutcome]:
    """Çivilenmiş korpusu derler, Kapı 1'den geçirir ve sonuçları döndürür.

    Kural yoksa boş liste — doğru cevap. Kural **varken** pySigma yoksa bu
    fonksiyon atıyor: sessizce boş dönmek, `--write` koşturan birinin bütün
    kuralları silmesi demek olurdu.
    """
    files = _rule_files()
    if not files:
        return []

    _assert_environment_matches_pin()
    pipeline = _load_pipeline()
    backend = pipeline.bizigo_backend(mappings_path=repo_root() / MAPPINGS_DIR)

    from sigma.collection import SigmaCollection  # noqa: PLC0415

    allowed = view_definition("events_ocsf", repo_root() / "db" / "clickhouse").column_set
    root = repo_root()
    outcomes: list[RuleOutcome] = []

    for path in files:
        text = path.read_text(encoding="utf-8")
        source_sha = "sha256:" + hashlib.sha256(text.encode("utf-8")).hexdigest()
        # Depo kökünün dışındaki bir yol (testlerde geçici dizin) `relative_to`
        # ile atardı; kaynak yolu bir kimlik değil bir etiket, mutlak kalması
        # bir şeyi bozmuyor.
        try:
            relative = path.relative_to(root).as_posix()
        except ValueError:
            relative = path.as_posix()

        # `SigmaTransformationError` ile diğer hatalar AYRI sınıflanıyor.
        #
        # T31 eşlenemeyen bir alanı **bilerek** reddediyor ve bunu o tiple
        # yapıyor. `failed` saymak yanlış olurdu: `failed` "pipeline kırık"
        # demek ve sabit sıfır olması bekleniyor. Eşlenemeyen bir alan kırıklık
        # değil bir **iş kalemi** — `gated`, ve yol haritasının çözünürlüğü
        # ondan geliyor ("`dns_query_name` eklersek iki kural açılır").
        #
        # ⚠️ `unsupported_fields()` bu ayrım için KULLANILAMIYOR ve bu ölçüldü:
        # `dns_query_name` için boş liste dönüyor, çünkü o fonksiyon "ad tanındı
        # mı" sorusunu cevaplıyor, "eşlendi mi" sorusunu değil — T31'in kendi
        # docstring'i de bunu söylüyor. Engellenen alanlar bu yüzden T31'in
        # `describe()["schema_gaps"]` beyanıyla kesiştirilerek bulunuyor.
        try:
            collection = SigmaCollection.from_yaml(text)
            (rule,) = collection.rules
            queries = backend.convert(collection)
        except _transformation_error() as error:
            blockers = tuple(_declared_blockers(pipeline, text)) or (
                # Hangi alan olduğunu çıkaramadıysak sebep YUTULMUYOR: ham metin
                # engelin içinde kalıyor. Çözünürlük kaybı var, sessiz kayıp yok.
                Blocker(
                    kind=KIND_UNSUPPORTED_CONSTRUCT,
                    message="pipeline kuralı reddetti",
                    remedy=REMEDY_UNKNOWN,
                    detail=f"{type(error).__name__}: {error}",
                ),
            )
            outcomes.append(
                RuleOutcome(
                    rule_id=_rule_id_of(text, path), title=_title_of(text, path),
                    source_path=relative, source_sha=source_sha,
                    status=STATUS_GATED, gate=GATE_COLUMN_EXISTENCE, blockers=blockers,
                )
            )
            continue
        except Exception as error:  # noqa: BLE001 — sebebi manifest'e yazılıyor
            outcomes.append(
                RuleOutcome(
                    rule_id=path.stem,
                    title=path.stem,
                    source_path=relative,
                    source_sha=source_sha,
                    status=STATUS_FAILED,
                    error=f"{type(error).__name__}: {error}",
                )
            )
            continue

        rule_id = str(getattr(rule, "id", None) or path.stem)
        title = str(getattr(rule, "title", None) or path.stem)
        logsource = {
            key: str(value)
            for key, value in (
                ("category", getattr(rule.logsource, "category", None)),
                ("product", getattr(rule.logsource, "product", None)),
                ("service", getattr(rule.logsource, "service", None)),
            )
            if value
        }

        if len(queries) != 1:
            # Tek kuraldan birden fazla sorgu: hangisinin dosya olacağı belirsiz
            # ve "bir dosya = bir kural" değişmezi kırılır.
            outcomes.append(
                RuleOutcome(
                    rule_id=rule_id, title=title, source_path=relative, source_sha=source_sha,
                    status=STATUS_FAILED, logsource=logsource,
                    error=f"tek kuraldan {len(queries)} sorgu üretildi — bir dosya bir kuraldır",
                )
            )
            continue

        sql = str(queries[0])
        verdict = check_columns(sql, allowed, view="events_ocsf")
        if verdict.passed:
            outcomes.append(
                RuleOutcome(
                    rule_id=rule_id, title=title, source_path=relative, source_sha=source_sha,
                    status=STATUS_WRITTEN, logsource=logsource, sql=sql,
                )
            )
        else:
            outcomes.append(
                RuleOutcome(
                    rule_id=rule_id, title=title, source_path=relative, source_sha=source_sha,
                    status=STATUS_GATED, logsource=logsource,
                    gate=GATE_COLUMN_EXISTENCE, blockers=verdict.blockers,
                )
            )

    return outcomes


def _committed_manifest(target: Path) -> str:
    path = target / MANIFEST_NAME
    return path.read_text(encoding="utf-8") if path.is_file() else ""


def _git_head_manifest(target: Path) -> str:
    """`HEAD`'deki manifest — iki koşum arasındaki geçişi okumak için.

    Ayrı bir duruma gerek yok: manifest commit'li, önceki koşum git'te.
    Git yoksa ya da dosya `HEAD`'de yoksa boş metin; geçiş özeti "hepsi yeni" der.
    """
    root = repo_root()
    relative = target.resolve().relative_to(root)
    try:
        result = subprocess.run(
            ["git", "-C", str(root), "show", f"HEAD:{relative.as_posix()}/{MANIFEST_NAME}"],
            capture_output=True,
            text=True,
            check=False,
        )
    except OSError:
        return ""
    return result.stdout if result.returncode == 0 else ""


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="Sigma kurallarını derleyip repoya yazar.")
    mode = parser.add_mutually_exclusive_group(required=True)
    mode.add_argument("--write", action="store_true", help=f"{OUTPUT_DIR} dizinini yeniden üretir")
    mode.add_argument("--check", action="store_true", help="Depodaki çıktı üretilenle aynı mı (CI kapısı)")
    mode.add_argument("--summary", action="store_true", help="HEAD'deki manifest'e göre ne açıldı, ne kapandı")
    parser.add_argument("--output", type=Path, default=None, help=f"Çıktı dizini (varsayılan: {OUTPUT_DIR})")
    args = parser.parse_args(argv)

    target = args.output or (repo_root() / OUTPUT_DIR)
    outcomes = collect_outcomes()
    header = current_header()

    if args.write:
        write_output(target, outcomes, header)
        counts = json.loads(build_manifest(outcomes, header))["counts"]
        print(f"✓ {OUTPUT_DIR} güncellendi — {counts}")
        if not _rule_files():
            print(
                "  ⚠️ Çivilenmiş korpus boş (catalog/sigma/ruleset.json, commit: null) — "
                "sıfır kural derlendi. Bu bugünkü doğru sayı."
            )
        return 0

    if args.summary:
        summary = transition_summary(_git_head_manifest(target), build_manifest(outcomes, header))
        print(json.dumps(summary, indent=2, ensure_ascii=False))
        return 0

    problems = check_output(target, outcomes, header)
    if not problems:
        print(f"✓ {OUTPUT_DIR} üretilenle birebir aynı.")
        return 0

    for problem in problems:
        print(f"  {problem}")
    print(
        f"\n✗ Üretilen çıktı depodakinden farklı.\n"
        "  Çözüm: tools/sigma-build içinde `python -m sigma_build.compile --write` "
        "çalıştırıp sonucu commit edin.",
        file=sys.stderr,
    )
    return 1


if __name__ == "__main__":
    raise SystemExit(main())
