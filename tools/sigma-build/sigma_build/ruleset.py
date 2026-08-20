"""Kural setinin sabitlenmesi — girdinin nereden geldiği (T32).

`pipeline_sha` ve `source_sha` "neyle derledik" ve "neyi derledik" sorularını
cevaplıyordu; eksik olan **girdinin nereden geldiğiydi**. Kural seti bir gün
başka bir commit'ten gelirse, "aynı girdi aynı SQL" iddiası sessizce başka bir
şey ölçmeye başlar.

Kurallar depoya kopyalanıyor, koşum anında indirilmiyor
-------------------------------------------------------
Üç gerekçe, üçü de bu depoda yaşanmış:

1. **Ağ, kapının gerekçesi olamaz.** `ci.yml` bunu zaten anlatıyor:
   `actions/setup-dotnet` `codeload.github.com`'dan iniyordu ve GitHub orayı
   sınırlandırdığında iş **kurulumda** ölüyordu — tek test koşmadan, kırmızı bir
   CI ve tamamen ilgisiz bir hata mesajıyla. Tek oturumda üç kez. Kural setini
   her koşumda indirmek aynı kapıyı kurar.
2. **Ticket'ın kendi gerekçesi.** Build-time derlemenin üçüncü sebebi "backend
   üç aylık, iki yıldızlı, tek geliştiricili; proje terk edilse bile mevcut
   kurallar çalışmaya devam eder" idi. Kaynak kurallar yalnızca yukarı akışta
   duruyorsa o dayanıklılık yarım kalıyor: SQL depoda ama **yeniden
   üretilemiyor**, yani sürüklenme kapısı da koşamıyor.
3. **Kapsam bir liste olmalı, bir filtre değil.** Yukarı akışa karşı
   değerlendirilen bir filtre, yukarı akış kural eklediğinde korpusumuzu
   **sessizce** değiştirir. Kopyalanmış bir liste ile kural eklemek bir commit.

Ağ yalnızca **yükseltme** anında, elle. CI ağa hiç çıkmıyor; yaptığı tek şey
kopyalanmış ağacın çiviye uyduğunu doğrulamak.

⚠️ **Yükseltme yolu henüz yazılmadı** ve bu bilinçli: hangi kuralların
kopyalanacağı T30'un kapsam ölçümüne bağlı ve o ölçüm gelmedi. Bugün çivi boş
(`commit: null`, sıfır kural) ve doğrulama yine de koşuyor — çivi mekanizması
kapsam kararından bağımsız, kapsam kararı da çiviyi beklemesin diye.

Var olmayan bir komutu mesajlarda anmıyoruz: bu turda tam o desenden bir hata
bulundu (`unmapped_expression()` yazılmış, hiç çağrılmamış).

Lisans
------
SigmaHQ kuralları Detection Rule License altında. `catalog/patterns/` zaten
aynı deseni izliyor (logstash grok pattern'leri, `THIRD-PARTY-NOTICES.md`'de
kayıtlı) — kural seti çivilendiğinde oraya bir bölüm daha gerekiyor.
"""

from __future__ import annotations

import hashlib
import json
from dataclasses import dataclass
from pathlib import Path

__all__ = ["CATALOG_DIR", "PIN_NAME", "RULES_SUBDIR", "Pin", "load_pin", "verify",
           "pin_text", "refresh_pin", "hash_tree"]

#: Kaynak kurallar **girdi**, o yüzden `catalog/` altında — `detections/` çıktı
#: için. `catalog/patterns/` ile aynı desen: kopyalanmış üçüncü taraf içerik.
CATALOG_DIR = Path("catalog") / "sigma"
PIN_NAME = "ruleset.json"
RULES_SUBDIR = "rules"


@dataclass(frozen=True)
class Pin:
    source: str
    commit: str | None
    license: str
    #: `path` → `sha256`. Kopyalanmış ağacın tamamı; eksik ya da fazla dosya hata.
    rules: dict[str, str]

    @property
    def is_pinned(self) -> bool:
        return self.commit is not None


def _sha256_bytes(data: bytes) -> str:
    return "sha256:" + hashlib.sha256(data).hexdigest()


def load_pin(catalog_dir: Path) -> Pin:
    path = catalog_dir / PIN_NAME
    if not path.is_file():
        raise FileNotFoundError(f"Çivi dosyası yok: {path}")

    document = json.loads(path.read_text(encoding="utf-8"))
    return Pin(
        source=document["source"],
        commit=document["commit"],
        license=document["license"],
        rules={entry["path"]: entry["sha256"] for entry in document["rules"]},
    )


def pin_text(pin: Pin) -> str:
    document = {
        "_comment": (
            "Kural setinin çivisi. Kurallar catalog/sigma/rules/ altında KOPYALI; "
            "CI ağa çıkmıyor, yalnızca bu dosyadaki özetlerin tuttuğunu doğruluyor. "
            "Yükseltme elle yapılıyor ve henüz yazılmadı: hangi kuralların "
            "kopyalanacağı T30'un kapsam ölçümüne bağlı."
        ),
        "source": pin.source,
        "commit": pin.commit,
        "license": pin.license,
        "rules": [{"path": path, "sha256": pin.rules[path]} for path in sorted(pin.rules)],
    }
    return json.dumps(document, indent=2, ensure_ascii=False) + "\n"


def hash_tree(catalog_dir: Path) -> dict[str, str]:
    """Kopyalanmış ağacın `yol → sha256` haritası."""
    rules_dir = catalog_dir / RULES_SUBDIR
    if not rules_dir.is_dir():
        return {}
    return {
        path.relative_to(rules_dir).as_posix(): _sha256_bytes(path.read_bytes())
        for path in sorted(rules_dir.rglob("*.yml"))
    }


def refresh_pin(catalog_dir: Path) -> Pin:
    """Çiviyi diskteki ağaçtan yeniden üretir; **üstverisi korunur**.

    Neden bir komut: çivi bu turda üç kez elle yenilendi ve dördüncüsünde
    yenilenmedi — bir ajan kuralı düzeltti, çiviyi yenilemedi, CI kırmızı yandı.
    Kapı doğru bağırdı ama tekrarı kesindi: elle yapılan bir adım unutulur.

    `source`, `commit` ve `license` **değişmiyor**. Bunlar kararlar, özet değil;
    yeniden üretim onlara dokunursa "hangi sürümden geldi" sorusunun cevabı
    sessizce kaybolur. Kural setini gerçekten yükseltmek (yeni `commit`) ayrı bir
    hareket ve ağ gerektiriyor; bu komut yalnızca **yerel** ağacı çiviyle
    hizalıyor.
    """
    mevcut = load_pin(catalog_dir)
    yeni = Pin(source=mevcut.source, commit=mevcut.commit, license=mevcut.license,
               rules=hash_tree(catalog_dir))
    (catalog_dir / PIN_NAME).write_text(pin_text(yeni), encoding="utf-8")
    return yeni


def verify(catalog_dir: Path) -> list[str]:
    """Kopyalanmış ağaç çiviye uyuyor mu. Boş liste = uyuyor. **Ağ kullanmıyor.**

    Üç sürüklenme yönü de ayrı raporlanıyor, çünkü üçünün cevabı farklı:
    eksik dosya (kopyalama yarım kalmış), fazla dosya (çiviye girmemiş kural —
    derlenir ama nereden geldiği kayıtsız), değişmiş içerik (elle düzenlenmiş
    ya da bozulmuş kural).
    """
    pin = load_pin(catalog_dir)
    rules_dir = catalog_dir / RULES_SUBDIR

    on_disk = {
        path.relative_to(rules_dir).as_posix(): path
        for path in sorted(rules_dir.rglob("*.yml"))
    } if rules_dir.is_dir() else {}

    problems: list[str] = []

    for missing in sorted(set(pin.rules) - set(on_disk)):
        problems.append(f"eksik (çivide var, diskte yok): {missing}")

    for extra in sorted(set(on_disk) - set(pin.rules)):
        problems.append(f"fazla (diskte var, çivide yok): {extra}")

    for shared in sorted(set(pin.rules) & set(on_disk)):
        actual = _sha256_bytes(on_disk[shared].read_bytes())
        if actual != pin.rules[shared]:
            problems.append(f"değişmiş: {shared}")

    return problems


def _main(argv: list[str] | None = None) -> int:
    import argparse
    import sys

    from sigma_build.view_columns import repo_root

    parser = argparse.ArgumentParser(description="Kural setinin çivisini doğrular.")
    parser.add_argument("--catalog", type=Path, default=None)
    parser.add_argument("--verify", action="store_true", help="Kopyalanmış ağaç çiviye uyuyor mu (ağsız)")
    parser.add_argument(
        "--refresh",
        action="store_true",
        help="Çiviyi diskteki ağaçtan yeniden üretir (üstveri korunur, ağsız)",
    )
    args = parser.parse_args(argv)

    catalog_dir = args.catalog or (repo_root() / CATALOG_DIR)

    if args.refresh:
        onceki = load_pin(catalog_dir)
        yeni = refresh_pin(catalog_dir)
        degisen = sorted(
            set(onceki.rules) ^ set(yeni.rules)
            | {k for k in set(onceki.rules) & set(yeni.rules) if onceki.rules[k] != yeni.rules[k]}
        )
        if degisen:
            print(f"✓ Çivi yenilendi — {len(yeni.rules)} kural, değişen: {degisen}")
            print("  ⚠️ Üretilen SQL de yenilenmeli: `python -m sigma_build.compile --write`")
        else:
            print(f"✓ Çivi zaten güncel — {len(yeni.rules)} kural.")
        return 0

    pin = load_pin(catalog_dir)

    if not args.verify:
        state = pin.commit or "çivilenmemiş"
        print(f"{pin.source} @ {state} — {len(pin.rules)} kural, lisans {pin.license}")
        return 0

    problems = verify(catalog_dir)
    if not problems:
        if not pin.is_pinned:
            print("✓ Çivi tutarlı — henüz bir commit'e çivilenmemiş, sıfır kural.")
        else:
            print(f"✓ {len(pin.rules)} kural çiviyle birebir aynı ({pin.commit}).")
        return 0

    for problem in problems:
        print(f"  {problem}")
    print(
        "\n✗ Kopyalanmış kural ağacı çivisiyle uyuşmuyor — hangi yönde olduğu yukarıda.\n"
        "  Yükseltme yolu henüz yazılmadı (kapsam T30'u bekliyor); bugün bu fark "
        "elle yapılmış bir değişiklik demek.",
        file=sys.stderr,
    )
    return 1


if __name__ == "__main__":
    raise SystemExit(_main())
