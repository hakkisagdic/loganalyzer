"""Üretilen SQL'in yazımı, manifest'i ve sürüklenme kapısı (T32).

Var olan bir dosya bir iddiadır
-------------------------------
`detections/sigma/<id>.sql` dosyasının varlığı **"bu kural koşuyor"** demek.
Bu yüzden kapıya takılan bir kural dosya üretmiyor; manifest'te `gated` olarak
sebebiyle duruyor. `RuleOutcome` bu değişmezi tipin kendisinde zorluyor —
`gated` bir sonuca SQL iliştirmek hata veriyor, yorum satırıyla rica edilmiyor.

Bayat dosya bırakılamaz, çünkü bırakılabileceği kod yolu yok
------------------------------------------------------------
`clicksiem/sigma_rules`'ta ölçülen tuzak: dönüşüm başarısız olduğunda eski çıktı
depoda kalıyor ve dosya sayıları %100 uyum gibi görünüyor. Çözüm "silmeyi
unutma" değil: çıktı **geçici dizine** üretiliyor ve sonunda hedefle **takas
ediliyor**. Kaybolan kural git'te silinme olarak görünüyor, manifest neden
kaybolduğunu söylüyor.

Manifest'te derleme tarihi **yok** — ticket'tan bilinçli sapma
--------------------------------------------------------------
Ticket "kural kimliği, kaynak sürümü ve **derleme tarihi** ile birlikte" diyor.
Üçü birden olamıyor:

* Kabul kriteri A: *aynı girdi, aynı SQL.*
* Kabul kriteri B: *çıktı depodakiyle aynı değilse CI düşer.*
* Derleme tarihi: her koşumda değişir.

Tarihi kural dosyalarına koymak kapıyı 269 dosyada öldürüyordu; manifest'e
koymak **manifest'in kendi kapısını** öldürüyor — manifest de karşılaştırılan
çıktının parçası. Kalan tek yol, kapının o alanı görmezden gelmesi, yani
kapıyı yumuşatmak.

Tarih atıldı, çünkü **git zaten tutuyor** ve daha güvenilir tutuyor: manifest
commit'li, `git log detections/sigma/manifest.json` "ne zaman derlendi"
sorusunun cevabı. Kaybedilen bilgi yok; kaybedilen tek şey aynı bilginin ikinci,
sürüklenebilir kopyası.

Kural setinin sürümü (`ruleset_commit`) ve pipeline'ın sürümü duruyor — onlar
girdinin parçası, koşumun değil, ve değiştiklerinde çıktı da değişiyor.
"""

from __future__ import annotations

import hashlib
import json
import os
import re
import shutil
import tempfile
from dataclasses import dataclass, field
from pathlib import Path

from sigma_build.gate import Blocker

__all__ = [
    "STATUS_WRITTEN",
    "STATUS_GATED",
    "STATUS_FAILED",
    "OUTPUT_DIR",
    "MANIFEST_NAME",
    "RuleOutcome",
    "RunHeader",
    "build_manifest",
    "rule_file_text",
    "write_output",
    "check_output",
    "transition_summary",
]

STATUS_WRITTEN = "written"
STATUS_GATED = "gated"
STATUS_FAILED = "failed"

#: `.gitignore`'daki `artifacts/` satırı .NET derleme çıktısı için. Üretilen
#: SQL'i adında `artifacts` geçen bir yola koymak, çıktının sessizce
#: commitlenmemesi demek olurdu.
OUTPUT_DIR = Path("detections") / "sigma"
MANIFEST_NAME = "manifest.json"

#: Kural kimliği dosya adı oluyor. Yol ayracı ya da `..` içeren bir kimlik,
#: üretilen çıktıyı hedef dizinin dışına yazdırırdı.
_RULE_ID = re.compile(r"^[A-Za-z0-9][A-Za-z0-9._-]*$")


@dataclass(frozen=True)
class RuleOutcome:
    """Tek bir kuralın derleme sonucu.

    Değişmezler `__post_init__`'te zorlanıyor: bir sonucun durumu ile taşıdığı
    veri tutarsız olamıyor. En önemlisi **`gated` bir sonuç SQL taşıyamaz** —
    taşısaydı dosya yazılır ve "bu kural koşuyor" iddiası sessizce yalan olurdu.
    """

    rule_id: str
    title: str
    source_path: str
    source_sha: str
    status: str
    logsource: dict[str, str] = field(default_factory=dict)
    sql: str | None = None
    gate: str | None = None
    blockers: tuple[Blocker, ...] = ()
    error: str | None = None

    def __post_init__(self) -> None:
        if not _RULE_ID.match(self.rule_id):
            raise ValueError(
                f"Kural kimliği dosya adı olarak güvenli değil: {self.rule_id!r}. "
                "Yol ayracı ve `..` kabul edilmiyor."
            )

        if self.status == STATUS_WRITTEN:
            if self.sql is None:
                raise ValueError(f"{self.rule_id}: `written` ama SQL yok.")
            if self.blockers:
                raise ValueError(f"{self.rule_id}: `written` ama engel taşıyor.")
        elif self.status == STATUS_GATED:
            if self.sql is not None:
                raise ValueError(
                    f"{self.rule_id}: `gated` bir kural SQL taşıyamaz — dosya yazılır ve "
                    "var olan bir dosya 'bu kural koşuyor' iddiasıdır."
                )
            if not self.blockers:
                raise ValueError(f"{self.rule_id}: `gated` ama sebep yok.")
            if self.gate is None:
                raise ValueError(f"{self.rule_id}: `gated` ama hangi kapı olduğu yazılmamış.")
        elif self.status == STATUS_FAILED:
            if self.sql is not None:
                raise ValueError(f"{self.rule_id}: `failed` bir kural SQL taşıyamaz.")
            if not self.error:
                raise ValueError(f"{self.rule_id}: `failed` ama hata metni yok.")
        else:
            raise ValueError(
                f"{self.rule_id}: bilinmeyen durum {self.status!r}. "
                f"Beklenen: {STATUS_WRITTEN} | {STATUS_GATED} | {STATUS_FAILED}"
            )

    @property
    def file_name(self) -> str:
        return f"{self.rule_id}.sql"

    def as_dict(self) -> dict[str, object]:
        document: dict[str, object] = {
            "rule_id": self.rule_id,
            "title": self.title,
            "source_path": self.source_path,
            "source_sha": self.source_sha,
            "logsource": dict(sorted(self.logsource.items())),
            "status": self.status,
        }
        if self.status == STATUS_WRITTEN:
            document["output_sha"] = _sha256(self.sql or "")
        if self.gate is not None:
            document["gate"] = self.gate
        if self.blockers:
            document["blockers"] = [blocker.as_dict() for blocker in self.blockers]
        if self.error is not None:
            document["error"] = self.error
        return document


@dataclass(frozen=True)
class RunHeader:
    """Koşumun **girdisini** tanımlayan alanlar.

    Hepsi girdinin parçası: değiştiklerinde çıktı da değişiyor. Koşuma özgü ama
    girdiye ait olmayan hiçbir şey burada yok — derleme tarihi dahil (bkz. modül
    açıklaması).
    """

    view: str = "events_ocsf"
    ruleset_commit: str | None = None
    pipeline_version: str | None = None
    pipeline_sha: str | None = None
    pysigma_version: str | None = None
    backend_version: str | None = None

    def as_dict(self) -> dict[str, object]:
        return {
            "view": self.view,
            "ruleset_commit": self.ruleset_commit,
            "pipeline_version": self.pipeline_version,
            "pipeline_sha": self.pipeline_sha,
            "pysigma_version": self.pysigma_version,
            "backend_version": self.backend_version,
        }


def _sha256(text: str) -> str:
    return "sha256:" + hashlib.sha256(text.encode("utf-8")).hexdigest()


def build_manifest(outcomes: list[RuleOutcome] | tuple[RuleOutcome, ...], header: RunHeader) -> str:
    """Manifest metni. Kural sırası kimliğe göre sabit — diff'in okunabilir olması için."""
    ordered = sorted(outcomes, key=lambda outcome: outcome.rule_id)

    duplicates = sorted({o.rule_id for o in ordered if sum(1 for x in ordered if x.rule_id == o.rule_id) > 1})
    if duplicates:
        raise ValueError(f"Aynı kural kimliği birden fazla kez: {duplicates}")

    counts = {
        "total": len(ordered),
        STATUS_WRITTEN: sum(1 for o in ordered if o.status == STATUS_WRITTEN),
        STATUS_GATED: sum(1 for o in ordered if o.status == STATUS_GATED),
        STATUS_FAILED: sum(1 for o in ordered if o.status == STATUS_FAILED),
    }

    document = {
        "_comment": (
            "ÜRETİLMİŞ DOSYA — elle düzenlemeyin. "
            "Üretici: tools/sigma-build. Derleme tarihi BİLEREK yok: sürüklenme kapısı "
            "birebir bayt karşılaştırıyor ve her koşumda değişen bir alan kapıyı "
            "imkânsız kılar. 'Ne zaman derlendi' sorusunun cevabı `git log` bu dosyada."
        ),
        "run": header.as_dict(),
        "counts": counts,
        "rules": [outcome.as_dict() for outcome in ordered],
    }
    return json.dumps(document, indent=2, ensure_ascii=False) + "\n"


def rule_file_text(outcome: RuleOutcome, header: RunHeader) -> str:
    """Tek bir kuralın SQL dosyası — **tarihsiz** (bkz. modül açıklaması)."""
    if outcome.status != STATUS_WRITTEN:
        raise ValueError(f"{outcome.rule_id}: yalnızca `written` sonuçlar dosya üretir.")

    lines = [
        "-- ÜRETİLMİŞ DOSYA — elle düzenlemeyin.",
        "-- Sigma kuralından derleme zamanında üretildi (T32).",
        "--",
        f"-- kural      : {outcome.title}",
        f"-- kimlik     : {outcome.rule_id}",
        f"-- kaynak     : {outcome.source_path}",
        f"-- kaynak sha : {outcome.source_sha}",
        f"-- kural seti : {header.ruleset_commit or '—'}",
        f"-- pipeline   : {header.pipeline_version or '—'} ({header.pipeline_sha or '—'})",
        "--",
        "-- Derleme tarihi bilerek yazılmadı: sürüklenme kapısı bayt karşılaştırıyor.",
        "-- Yeniden üretmek: tools/sigma-build içinde `python -m sigma_build.compile --write`",
        "",
        outcome.sql or "",
    ]
    return "\n".join(lines).rstrip("\n") + "\n"


def _materialize(directory: Path, outcomes: list[RuleOutcome] | tuple[RuleOutcome, ...], header: RunHeader) -> None:
    directory.mkdir(parents=True, exist_ok=True)
    for outcome in outcomes:
        if outcome.status == STATUS_WRITTEN:
            (directory / outcome.file_name).write_text(rule_file_text(outcome, header), encoding="utf-8")
    (directory / MANIFEST_NAME).write_text(build_manifest(outcomes, header), encoding="utf-8")


def write_output(
    target: Path,
    outcomes: list[RuleOutcome] | tuple[RuleOutcome, ...],
    header: RunHeader,
) -> None:
    """Çıktıyı geçici dizinde üretip hedefle **takas eder**.

    Neden takas: başarısız ya da kapıya takılmış bir kuralın eski dosyası,
    "silmeyi unut" diye bir kod yolu olmadığı için hayatta kalamıyor. Bu,
    `clicksiem/sigma_rules`'ta ölçülen tuzağın kapatılma biçimi.

    Eski dizin önce yana alınıyor, yeni dizin yerine geçtikten **sonra**
    siliniyor: takas ortasında bir hata olursa eski çıktı geri konuyor. Aksi
    hâlde bir istisna, üretilmiş çıktıyı silmiş ve yerine bir şey koymamış olurdu.
    """
    target = target.resolve()
    target.parent.mkdir(parents=True, exist_ok=True)

    staging = Path(tempfile.mkdtemp(prefix=".sigma-build-", dir=target.parent))
    previous: Path | None = None
    try:
        _materialize(staging, outcomes, header)

        if target.exists():
            previous = Path(tempfile.mkdtemp(prefix=".sigma-build-old-", dir=target.parent))
            previous.rmdir()
            os.rename(target, previous)

        os.rename(staging, target)
        staging = target  # takas bitti; `finally` artık staging'i silmemeli
    except BaseException:
        if previous is not None and not target.exists():
            os.rename(previous, target)
            previous = None
        raise
    finally:
        if staging != target and staging.exists():
            shutil.rmtree(staging, ignore_errors=True)
        if previous is not None:
            shutil.rmtree(previous, ignore_errors=True)


def check_output(
    target: Path,
    outcomes: list[RuleOutcome] | tuple[RuleOutcome, ...],
    header: RunHeader,
) -> list[str]:
    """Sürüklenme kapısı. Boş liste = depodaki çıktı üretilenle birebir aynı.

    Hedefe **dokunmuyor**: üzerine yazsaydı, düşen bir kapıdan sonra aynı
    komutun ikinci koşumu sebepsiz yere geçerdi
    (`ui/scripts/generate-api-types.sh` aynı gerekçeyi taşıyor).
    """
    problems: list[str] = []
    staging = Path(tempfile.mkdtemp(prefix=".sigma-build-check-"))
    try:
        _materialize(staging, outcomes, header)

        produced = {path.name: path.read_text(encoding="utf-8") for path in sorted(staging.iterdir())}
        committed = (
            {path.name: path.read_text(encoding="utf-8") for path in sorted(target.iterdir()) if path.is_file()}
            if target.is_dir()
            else {}
        )

        for name in sorted(set(produced) - set(committed)):
            problems.append(f"eksik (üretiliyor ama depoda yok): {name}")
        for name in sorted(set(committed) - set(produced)):
            problems.append(f"fazla (depoda var ama üretilmiyor): {name}")
        for name in sorted(set(produced) & set(committed)):
            if produced[name] != committed[name]:
                problems.append(f"farklı: {name}")

        return problems
    finally:
        shutil.rmtree(staging, ignore_errors=True)


def transition_summary(previous_manifest: str, current_manifest: str) -> dict[str, list[str]]:
    """İki koşum arasında hangi kurallar açıldı, hangileri kapandı.

    `gated` durumu her derlemede yeniden hesaplanıyor — türetilmiş veri,
    saklansaydı ikinci bir gerçek kaynak olurdu. Ama geçiş bir **olay**:
    "42 kuraldan 7'si artık derleniyor" söylenmeye değer bir haber.

    Ayrı bir duruma gerek yok, çünkü manifest commit'li: önceki koşum git'te
    duruyor ve fark, manifest'in kendi diff'i.
    """
    def statuses(text: str) -> dict[str, str]:
        if not text.strip():
            return {}
        return {rule["rule_id"]: rule["status"] for rule in json.loads(text).get("rules", [])}

    before = statuses(previous_manifest)
    after = statuses(current_manifest)

    return {
        "opened": sorted(
            rule_id
            for rule_id, status in after.items()
            if status == STATUS_WRITTEN and before.get(rule_id) in {STATUS_GATED, STATUS_FAILED}
        ),
        "closed": sorted(
            rule_id
            for rule_id, status in after.items()
            if status in {STATUS_GATED, STATUS_FAILED} and before.get(rule_id) == STATUS_WRITTEN
        ),
        "added": sorted(set(after) - set(before)),
        "removed": sorted(set(before) - set(after)),
    }
