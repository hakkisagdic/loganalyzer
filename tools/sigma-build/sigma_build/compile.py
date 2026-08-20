"""Derleme hattının giriş noktası — tek komut, tekrarlanabilir çıktı (T32).

Bugünkü durum: **kural seti ve pipeline henüz bağlı değil.**

Bu, hattın eksik olduğu anlamına geliyor ama **kapının** eksik olduğu anlamına
gelmiyor, ve ikisini ayırmak bilinçli. Kapı bugünden CI'da koşuyor ve bugünkü
boş çıktıyı bekçiliyor; T31'in pipeline'ı geldiğinde kuralların belirmesi bir
git diff'i olarak görünüyor.

Sebep, bu turda tam da bu sınıftan bir hata bulmuş olmamız: `bizigo_pipeline.py`
`UNMAPPED_FIELDS` listesini tanımlamış ama hiçbir dönüşüme vermemişti;
`unmapped_expression()` yazılmış ama hiç çağrılmamıştı. **Hazırlanmış ama
bağlanmamış**, ve bağlanmamış olması hiçbir yerde belirti üretmiyordu — on
kuralın sekizi sessizce koşmuyordu. Kapıyı "hat bitince bağlarız" diye bekletmek
aynı deseni bir kez daha kurmak olurdu.

`counts.written = 0` bugün **doğru** sayı: sıfır Sigma kuralı derleniyor.
`run.pipeline_version = null` bunun sebebini söylüyor — "hiç kural yok" ile
"henüz derlemiyoruz" farklı şeyler ve manifest ikisini karıştırmıyor.
"""

from __future__ import annotations

import argparse
import json
import subprocess
import sys
from pathlib import Path

from sigma_build.manifest import (
    MANIFEST_NAME,
    OUTPUT_DIR,
    RuleOutcome,
    RunHeader,
    build_manifest,
    check_output,
    transition_summary,
    write_output,
)
from sigma_build.view_columns import repo_root

__all__ = ["collect_outcomes", "current_header", "main"]


def current_header() -> RunHeader:
    """Koşumun girdi tanımı.

    Kural seti **sabit bir commit SHA'sına** çivilenecek ve elle yükseltilecek
    (T32 tasarımı §4). Günlük cron değil: kriter A "aynı girdi, aynı SQL" diyorsa
    kural setinin sürümü girdinin tanımına giriyor, ve cron kapıyı kimsenin bir
    şey değiştirmediği bir sabah kendi kendine kırmızı yandırırdı.

    Bugün üçü de `None`: çivilenecek bir şey henüz yok.
    """
    return RunHeader(view="events_ocsf")


def collect_outcomes() -> list[RuleOutcome]:
    """Derlenecek kurallar.

    Bugün boş — kural seti bağlı değil (T31). Bu fonksiyon bilerek ayrı duruyor:
    T31 geldiğinde değişen tek yer burası, kapı ve yazım dokunulmadan kalıyor.
    """
    return []


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
        if header.pipeline_version is None:
            print("  ⚠️ pipeline bağlı değil (T31): sıfır kural derlendi. Bu bugünkü doğru sayı.")
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
