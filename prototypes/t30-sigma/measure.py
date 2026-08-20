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
import re
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

    #: Pipeline'da eşleme dalı olmayan alanlar — **statik**, ClickHouse gerekmiyor.
    unhandled: list[str] = field(default_factory=list)

    #: ClickHouse'un tanımadığı kolon adları — reddedilen SQL'in SEBEBİ.
    #:
    #: Sayı "on kural düştü" der; bu liste "hangi kolon yüzünden" der ve
    #: T31'in eşleme tablosunun ilk taslağı odur.
    rejected_columns: list[str] = field(default_factory=list)

    #: Bu kuralın vendor'ına ait HİÇ satır yok.
    #:
    #: `matches=False` ile karıştırılmamalı: biri "kural eşleşmedi", diğeri
    #: "ölçülecek veri yoktu". İkisini tek sayıya indirmek, kapsam kararını
    #: yüklenmemiş bir fixture'a dayandırmak olurdu.
    no_data: bool = False

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

    no_data: int = 0

    #: Eşleme dalı olmayan alana giden kural sayısı — prototip boşluğu.
    unhandled_rules: int = 0

    #: Alan → kaç kuralda geçiyor. T31'in bağlaması gereken liste.
    unhandled_by_field: dict[str, int] = field(default_factory=dict)

    #: Kolon adı → kaç kuralı düşürdü. En çok düşüreni en önce ele alınmalı.
    rejected_columns: dict[str, int] = field(default_factory=dict)

    #: `events_ocsf` satır sayısı ve vendor dağılımı — ön kontrolden.
    view_rows: int = 0
    vendor_rows: dict[str, int] = field(default_factory=dict)

    outcomes: list[RuleOutcome] = field(default_factory=list)
    notes: list[str] = field(default_factory=list)

    @property
    def measurable(self) -> int:
        """Verisi olan kurallar — kapsam oranının paydası.

        `no_data` olanlar düşülüyor: ölçülemeyen bir kuralı "eşleşmedi"
        saymak, oranı fixture eksikliğiyle aşağı çekerdi.
        """
        return self.rules - self.no_data

    @property
    def match_ratio(self) -> float:
        """Kapsam kararının dayanağı: eşleşen / ölçülebilir."""
        return self.matches / self.measurable if self.measurable else 0.0

    @property
    def mapping_lines_per_rule(self) -> float:
        """Kapsam kararının birimi: eşleme satırı / eşlenen kural.

        Eşleşen kural yoksa **sıfır** dönüyor, `inf` değil: `inf` bir ölçüm
        gibi görünüyor ama ölçüm yapılamadığını anlatıyor ve çıktıda aracın
        kendine güvenini zedeliyor. Çağıran `matches == 0` durumunu zaten
        ayrıca görüyor.
        """
        return self.pipeline_lines / self.matches if self.matches else 0.0

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


#: Vendor anahtarı → altın örnek dosyaları.
#:
#: Sonda bu dosyalardan **çalışma anında** türetiliyor; sabit bir dizge
#: yazsaydık örnekler değiştiği gün kontrol sessizce yalan söylemeye başlardı.
GOLDEN_SAMPLES: dict[str, str] = {
    "Fortinet": "catalog/parsers/fortinet.fortigate/samples",
    "Cisco": "catalog/parsers/cisco.asa/samples",
    "MikroTik": "catalog/parsers/mikrotik.routeros/samples",
    "nginx": "catalog/parsers/nginx.access/samples",
}


def _repo_root() -> Path | None:
    """Depo kökü, ya da bulunamadıysa **None**.

    Eskiden bulunamadığında `here.parent` dönüyordu. O sessiz geri çekilme
    ölçümü bir kez bozdu: dosya depo dışına kopyalanıp koşturulunca kök yanlış
    çözüldü, altın örnek dizinleri bulunamadı, sonda listesi boş kaldı ve **ön
    kontrol kendini kapattı**. `None` dönmek o durumu bir cevaba değil bir
    hataya çeviriyor.
    """
    here = Path(__file__).resolve()

    for parent in here.parents:
        if (parent / "Bizigo.sln").exists():
            return parent

    return None


#: Sondanın İÇİNE ALMAYACAĞI bölgeler: yükleyici bunları yeniden yazıyor.
#:
#: Örnekler 2015–2024 tarihleri taşıyor; yükleyici onları ölçüm penceresine
#: taşımak için damgayı **yeniden yazıyor**. Damgayla kesişen bir sonda, veri
#: doğru yüklenmiş olsa bile tutmaz — bekçi doğru veriyi reddeder.
_VOLATILE = re.compile(
    r"\d{4}-\d{2}-\d{2}[T ]?\d{2}:\d{2}:\d{2}"          # 2019-05-10 11:37:47
    r"|date=\d{4}-\d{2}-\d{2}|time=\d{2}:\d{2}:\d{2}"    # date=... time=...
    r"|eventtime=\d+"                                    # epoch nanosaniye
    r"|\d{2}/[A-Z][a-z]{2}/\d{4}:\d{2}:\d{2}:\d{2}"      # 09/Jun/2020:12:15:39
    r"|[A-Z][a-z]{2}\s{1,2}\d{1,2}\s\d{2}:\d{2}:\d{2}"   # May  5 19:02:25
    r"|\d{2}:\d{2}:\d{2}"                                # çıplak saat
    r"|\d{10,}"                                          # epoch / uzun sayı
)

#: Vendor başına kaç sonda türetiliyor.
#:
#: Tek sonda kırılgan: yükleyici satırları tekilleştiriyor ya da bir alt küme
#: yüklüyorsa, seçtiğimiz **o** satır tabloda olmayabilir — ve yokluğu "veri
#: yanlış" diye okunur. Birkaç farklı satırdan sonda alıp "herhangi biri
#: tutsun" demek bu yanlış negatifi kapatıyor.
PROBES_PER_VENDOR = 5

#: Sonda uzunluğu. Kısa sonda ayırt etmiyor: sentetik benchmark verisi de aynı
#: vendor'ın söz dizimini taşıyor ve `level="notice"` gibi bir parça onda da var.
PROBE_LENGTH = 44


def _stable_window(line: str, width: int) -> str:
    """Satırın, damga taşımayan `width` karakterlik bir dilimi (yoksa "").

    Pencere ortadan başlayıp iki yana taranıyor: satırın ortası en çok alan
    taşıyan, dolayısıyla benzersiz değer (IP, port, oturum kimliği) bulunma
    olasılığı en yüksek bölge.
    """
    forbidden = [match.span() for match in _VOLATILE.finditer(line)]
    middle = max(0, (len(line) - width) // 2)

    # Ortadan dışa doğru: önce ortaya en yakın pencereler denensin.
    starts = sorted(range(0, len(line) - width + 1), key=lambda i: abs(i - middle))

    for start in starts:
        end = start + width

        if not any(begin < end and start < finish for begin, finish in forbidden):
            return line[start:end]

    return ""


def golden_probes() -> dict[str, list[str]]:
    """Vendor → altın örneklerden türetilmiş **birkaç** ayırt edici dizge.

    Neden birden çok, ve neden damgasız
    -----------------------------------
    İlk hâli vendor başına **tek** sonda üretiyordu: en uzun satırın
    ortasından 60 karakter. İki yönden kırılgandı ve ikisi de yaşandı.

    * **Damga.** Yükleyici örneklerin 2015–2024 tarihlerini ölçüm penceresine
      taşımak için damgayı yeniden yazıyor. Sonda o bölgeyle kesişirse veri
      doğru yüklenmiş olsa bile tutmaz. Artık `_stable_window()` damga
      taşımayan bir dilim seçiyor.
    * **Tek satıra bağlılık.** Yükleyici satırları tekilleştiriyor ya da bir
      alt küme yüklüyorsa, seçtiğimiz *o* satır tabloda olmayabilir — ve
      yokluğu "veri yanlış" diye okunur. Artık farklı satırlardan
      `PROBES_PER_VENDOR` sonda alınıyor ve **herhangi birinin** tutması yetiyor.

    Uzun satırlar önce deneniyor: en çok alan taşıyan, dolayısıyla içinde
    benzersiz değer (IP, port, oturum kimliği) bulunma olasılığı en yüksek olan.

    Boş dönmek bir cevap değil bir **arıza**: çağıran tarafın ölçümü
    reddetmesi gerekiyor, geçirmesi değil.
    """
    probes: dict[str, list[str]] = {}
    root = _repo_root()

    if root is None:
        return {}

    for vendor, relative in GOLDEN_SAMPLES.items():
        directory = root / relative

        if not directory.is_dir():
            continue

        lines: list[str] = []

        for path in sorted(directory.glob("*.log")):
            for raw in path.read_text(encoding="utf-8", errors="replace").splitlines():
                line = raw.strip()

                if len(line) >= PROBE_LENGTH + 20:
                    lines.append(line)

        found: list[str] = []

        for line in sorted(lines, key=len, reverse=True):
            window = _stable_window(line, PROBE_LENGTH)

            if window and window not in found:
                found.append(window)

            if len(found) == PROBES_PER_VENDOR:
                break

        if found:
            probes[vendor] = found

    return probes


@dataclass
class Preflight:
    """Ölçüme başlamadan önce verinin gerçekten orada olduğunun kanıtı."""

    ok: bool
    reason: str = ""
    rows: int = 0
    vendors: dict[str, int] = field(default_factory=dict)

    #: Vendor → altın örnek sondası tablo içinde bulundu mu.
    golden: dict[str, bool] = field(default_factory=dict)

    #: Vendor → aranan sondalar. Reddedilen bir koşumda basılıyor: bekçinin
    #: "bulamadım" demesi ile operatörün elle doğrulayabilmesi arasındaki fark.
    probes: dict[str, list[str]] = field(default_factory=dict)


def preflight(url: str, user: str, password: str, timeout: float) -> Preflight:
    """`events_ocsf` ölçülebilir durumda mı.

    **Neden reddetmek gerekiyor:** altın örnekler yüklenmemişse her kural
    `runs=true, matches=false` verir. O tablo, "kural eşleşmedi" diye okunur ve
    kapsam kararı yüklenmemiş bir fixture'a dayandırılır — protokolün
    engellemek için var olduğu sessiz yanlış sonuç sınıfının ta kendisi.

    Üç durumu ayırıyor, çünkü üçünün cevabı farklı:

    * Sorgu **hata** verdi   → görünüm yok ya da kimlik yanlış; ölçüm yapılamaz
    * Satır sayısı **sıfır** → veri yüklenmemiş; ölçüm yapılmamalı
    * Satır var              → ölçülebilir, ama vendor dağılımı da raporlanıyor
    """
    total, error = run_on_clickhouse("SELECT * FROM events_ocsf", url, user, password, timeout)

    if error:
        return Preflight(
            ok=False,
            reason=(
                "`events_ocsf` sorgulanamadı. Görünüm oluşturulmamış ya da kimlik bilgisi "
                f"yanlış olabilir. ClickHouse yanıtı: {error}"
            ),
        )

    if total == 0:
        return Preflight(
            ok=False,
            rows=0,
            reason=(
                "`events_ocsf` BOŞ. Altın örnekler yüklenmeden ölçüm yapılırsa her kural "
                "`runs=true, matches=false` verir ve bu 'kural eşleşmedi' diye okunur. "
                "Önce örnekleri yükleyin; yalnızca derleme sayıları isteniyorsa "
                "`--clickhouse-url` VERMEDEN koşun (statik kip)."
            ),
        )

    # Vendor dağılımı: yalnızca FortiGate yüklüyse Cisco kurallarının
    # `matches=0` vermesi eşlemenin değil fixture'ın eksikliği.
    vendors: dict[str, int] = {}

    for vendor in ("Fortinet", "Cisco", "MikroTik"):
        rows, _ = run_on_clickhouse(
            f"SELECT * FROM events_ocsf WHERE device_vendor_name = '{vendor}'",
            url, user, password, timeout,
        )
        vendors[vendor] = rows

    rows, _ = run_on_clickhouse(
        "SELECT * FROM events_ocsf WHERE metadata_product_name = 'nginx'",
        url, user, password, timeout,
    )
    vendors["nginx"] = rows

    # --- Asıl kapı: altın örnekler GERÇEKTEN yüklü mü --------------------
    #
    # "Tablo boş değil" ile "doğru veri yüklü" aynı şey değil ve aradaki fark
    # bir kez pahalıya patladı: tabloda önceki bir turdan kalma 1.000.000
    # satırlık tek-vendor'lu sentetik benchmark verisi vardı. Ön kontrol
    # "boş mu" diye sordu, cevap hayırdı, geçirdi — ve ölçüm `%0` eşleşme
    # üretti. O sıfır eşlemenin değil verinin sonucuydu.
    #
    # Bu yüzden kontrol artık bir YOKLUK kanıtı değil VARLIK kanıtı arıyor:
    # altın örnek satırının kendisi gövdede duruyor mu.
    probes = golden_probes()

    # Sonda türetilemediyse ölçüm YAPILMAZ.
    #
    # Eski kapı `if probes and not any(...)` yazıyordu ve o `probes and`
    # bekçiyi kendi eliyle kapatıyordu: sonda listesi boşsa koşul her zaman
    # False, ölçüm geçiyor, üstelik dört vendor da "altın örnek YOK" bayrağı
    # alıyor — çünkü boş sözlükte `.get()` None dönüyor. Bekçi hem yanlış
    # konuşuyor hem sözünü tutmuyordu. Bir kez tam olarak böyle oldu.
    #
    # Bu, deponun §7'sindeki desenin aynısı: bekçinin sessizce atlaması,
    # bekçinin kendisinden tehlikeli.
    if not probes:
        root = _repo_root()
        looked = (
            f"kök `{root}` altında " if root else "depo kökü BULUNAMADI (`Bizigo.sln` yok), "
        )
        return Preflight(
            ok=False,
            rows=total,
            vendors=vendors,
            reason=(
                "Altın örnek sondası TÜRETİLEMEDİ, dolayısıyla verinin doğru olduğu "
                f"kanıtlanamıyor. {looked}şu dizinlere bakıldı: "
                f"{', '.join(sorted(GOLDEN_SAMPLES.values()))}. "
                "Bu bir veri sorunu değil KURULUM sorunu: `measure.py` depo ağacının "
                "içinden koşmalı. Sonda üretilemeden yapılan ölçüm, verisi doğrulanmamış "
                "bir ölçümdür ve sayısı kapsam kararına dayanak olamaz."
            ),
        )

    golden: dict[str, bool] = {}

    for vendor, candidates in probes.items():
        found = False

        for probe in candidates:
            escaped = probe.replace("\\", "\\\\").replace("'", "''")
            hits, error = run_on_clickhouse(
                f"SELECT * FROM events_ocsf WHERE position(raw_data, '{escaped}') > 0",
                url, user, password, timeout,
            )

            # Sorgu hatası "bulamadım" DEĞİL. Eskiden hata yutuluyordu ve
            # kırık bir sorgu ile yüklenmemiş veri aynı görünüyordu.
            if error:
                return Preflight(
                    ok=False,
                    rows=total,
                    vendors=vendors,
                    probes=probes,
                    reason=(
                        f"Altın örnek sondası SORGULANAMADI ({vendor}). Bu bir veri "
                        f"sorunu değil; ön kontrol kendi sorgusunu koşturamıyor. "
                        f"ClickHouse yanıtı: {error}"
                    ),
                )

            if hits > 0:
                found = True
                break

        golden[vendor] = found

    # Satırı OLAN ama altın örneği OLMAYAN vendor: veri yabancı.
    #
    # Eskiden yalnızca "hiçbiri bulunamadı" reddediliyordu; bir vendor'ın
    # yabancı veriyle dolu olması uyarıyla geçiyordu. O uyarı, tam da
    # engellemek için yazıldığı şeyi üretir: o vendor'ın kuralları
    # `matches=false` verir ve sıfır "kapsam düşük" diye okunur.
    foreign = sorted(v for v, rows in vendors.items() if rows and not golden.get(v))

    if foreign:
        return Preflight(
            ok=False,
            rows=total,
            vendors=vendors,
            golden=golden,
            probes=probes,
            reason=(
                f"Şu vendor'ların satırı VAR ama hiçbiri altın örnek değil: "
                f"{', '.join(foreign)}. Tablodaki veri başka bir turdan kalmış olabilir "
                "(ör. sentetik benchmark verisi). Bu hâlde o vendor'ın kuralları "
                "`matches=false` verir ve o sıfır eşlemenin değil VERİNİN sonucudur. "
                "Önce tabloyu temizleyip altın örnekleri yükleyin. "
                "Aranan sondalar aşağıda; elle doğrulamak için "
                "`SELECT count() FROM events_ocsf WHERE position(raw_data, '<sonda>') > 0`."
            ),
        )

    return Preflight(ok=True, rows=total, vendors=vendors, golden=golden, probes=probes)


#: `events_ocsf` görünümünün kolonları (db/clickhouse/0003, 0004).
#:
#: Elle yazılı, çünkü statik kontrol ClickHouse'suz koşabilmeli. Ayrışırsa
#: canlı koşum yakalar: orada kolonu ClickHouse'un kendisi reddediyor.
VIEW_COLUMNS: frozenset[str] = frozenset({
    "time", "uid", "class_uid", "activity_id", "severity_id",
    "src_endpoint_ip", "src_endpoint_port", "dst_endpoint_ip", "dst_endpoint_port",
    "connection_info_protocol_name", "activity_name", "status",
    "actor_user_name", "device_hostname", "device_vendor_name",
    "metadata_product_name", "metadata_version", "unmapped", "raw_data",
})


def unhandled_fields(rule_text: str, field_map: dict[str, str]) -> list[str]:
    """Kuralın, pipeline'da **eşleme dalı olmayan** alanları.

    Neden ayrı sayılıyor: `runs < compiled` farkı iki ayrı sebepten doğuyor ve
    ikisi farklı şeyler söylüyor.

    * **Şema boşluğu** — alan bizim modelimizde gerçekten yok.
    * **Prototip boşluğu** — alan biliniyor (`UNMAPPED_FIELDS`) ama hiçbir
      dönüşüme bağlanmamış, dolayısıyla ham Sigma adıyla SQL'e iniyor.

    İkincisi ölçülen sayıyı **olduğundan kötü** gösteriyor ve kapsam kararını
    şemanın değil prototipin eksikliğine dayandırırdı. Bu yüzden görünür
    sayılıyor; düzeltilmesi T31'in kapsamında.
    """
    body = rule_text.split("selection:")
    if len(body) < 2:
        return []

    selection = body[1].split("\n  condition:")[0]
    found: list[str] = []

    for match in re.finditer(r"^\s{4}([A-Za-z_][A-Za-z0-9_]*)(\|[a-z]+)?:", selection, re.M):
        field = match.group(1)
        target = field_map.get(field, field)

        if target not in VIEW_COLUMNS and target not in found:
            found.append(target)

    return found


#: Kuralın `logsource.product` değeri → ön kontroldeki vendor anahtarı.
PRODUCT_TO_VENDOR = {
    "fortigate": "Fortinet",
    "asa": "Cisco",
    "routeros": "MikroTik",
    "nginx": "nginx",
}


#: ClickHouse'un bilinmeyen kolonu üç ayrı cümleyle anlatıyor; üçünü de yakalıyoruz.
_UNKNOWN_COLUMN = re.compile(
    r"Unknown expression identifier '([^']+)'"
    r"|Missing columns:((?:\s*'[^']+')+)"
    r"|Unknown identifier:?\s*[`']?([A-Za-z_][A-Za-z0-9_.]*)"
)


def rejected_columns(error: str) -> list[str]:
    """Hata gövdesinden tanınmayan kolon adlarını çıkarır.

    Sayı tek başına "on kural düştü" diyor; asıl işe yarayan bilgi **hangi
    kolon**. Bir sonraki koşumda hem sayı hem sebep gelsin diye çıktıya
    özet olarak basılıyor — ve o özet T31'in eşleme tablosunun ilk taslağı.
    """
    found: list[str] = []

    for match in _UNKNOWN_COLUMN.finditer(error or ""):
        single, group, bare = match.groups()

        if single:
            found.append(single)
        elif group:
            found.extend(re.findall(r"'([^']+)'", group))
        elif bare:
            found.append(bare)

    # Sıra korunuyor ama tekrar atılıyor: aynı kolon iki kez geçtiğinde
    # ağırlığı artmamalı, kuralı bir kez düşürüyor.
    seen: set[str] = set()
    unique = []

    for name in found:
        if name not in seen:
            seen.add(name)
            unique.append(name)

    return unique


def measure(args: argparse.Namespace) -> Report:
    import yaml

    from bizigo_pipeline import FIELD_MAP, TABLE, mapped_field_count, pipeline_line_count

    report = Report(
        mapped_fields=mapped_field_count(),
        pipeline_lines=pipeline_line_count(),
    )

    checked = getattr(args, "_preflight", None)

    if checked is not None:
        report.view_rows = checked.rows
        report.vendor_rows = dict(checked.vendors)

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

        # Statik: ClickHouse gerekmiyor, koşum yapılmasa da doluyor.
        outcome.unhandled = unhandled_fields(text, FIELD_MAP)

        sql, error = with_pipeline[name]
        outcome.compiled = bool(sql) and not error
        outcome.error = error

        if outcome.compiled:
            outcome.untouched = sql == baseline[name][0]
            outcome.unmapped_hits = sql.count("unmapped[")
            sql, outcome.table_rewritten = rewrite_table(sql, TABLE)
            outcome.sql = sql

            if args.clickhouse_url:
                vendor = PRODUCT_TO_VENDOR.get(outcome.product, "")
                outcome.no_data = bool(vendor) and report.vendor_rows.get(vendor, 0) == 0

                rows, run_error = run_on_clickhouse(
                    sql, args.clickhouse_url, args.clickhouse_user, args.clickhouse_password, args.timeout
                )
                outcome.runs = not run_error
                outcome.rows = rows

                # Vendor'ın hiç satırı yoksa `matches=False` bir SONUÇ değil,
                # ölçümün yapılamadığının işareti; oranın paydasından düşülüyor.
                outcome.matches = outcome.runs and rows > 0

                if run_error:
                    outcome.error = run_error
                    outcome.rejected_columns = rejected_columns(run_error)

        report.outcomes.append(outcome)

    report.rules = len(report.outcomes)
    report.compiled = sum(1 for o in report.outcomes if o.compiled)
    report.runs = sum(1 for o in report.outcomes if o.runs)
    report.matches = sum(1 for o in report.outcomes if o.matches)
    report.untouched = sum(1 for o in report.outcomes if o.untouched)
    report.table_rewrites = sum(1 for o in report.outcomes if o.table_rewritten)
    report.unmapped_rules = sum(1 for o in report.outcomes if o.unmapped_hits > 0)
    report.no_data = sum(1 for o in report.outcomes if o.no_data)
    report.unhandled_rules = sum(1 for o in report.outcomes if o.unhandled)

    for outcome in report.outcomes:
        for column in outcome.unhandled:
            report.unhandled_by_field[column] = report.unhandled_by_field.get(column, 0) + 1

    if report.unhandled_rules:
        report.notes.append(
            f"{report.unhandled_rules} kural, pipeline'da eşleme dalı OLMAYAN bir alana gidiyor. "
            "Bu kuralların SQL'i ham Sigma adıyla iniyor ve ClickHouse reddediyor — yani "
            "`runs < compiled` farkının bir kısmı ŞEMANIN değil PROTOTİPİN eksikliği. "
            "`UNMAPPED_FIELDS` tanımlı ama hiçbir dönüşüme bağlı değil; bağlanması T31'de."
        )

    for outcome in report.outcomes:
        for column in outcome.rejected_columns:
            report.rejected_columns[column] = report.rejected_columns.get(column, 0) + 1

    if report.no_data:
        missing = sorted({o.product for o in report.outcomes if o.no_data})
        report.notes.append(
            f"{report.no_data} kuralın vendor'ına ait HİÇ satır yok ({', '.join(missing)}). "
            "Bunların `matches=false` olması eşlemenin değil fixture'ın eksikliği; "
            "kapsam oranının paydasından düşüldüler."
        )

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

    # ÖN KONTROL — ölçümden önce, ve geçemezse ölçüm HİÇ yapılmıyor.
    #
    # Sebep protokolün kendisi: boş bir görünüme karşı koşulan ölçüm her kural
    # için `matches=false` üretir ve o tablo "kapsam düşük" diye okunur. Sıfırı
    # sonuç sanmak, T30'un engellemek için var olduğu sessiz yanlış sonucun
    # aynısı — bu sefer ölçüm aracının kendisinde.
    if args.clickhouse_url:
        checked = preflight(
            args.clickhouse_url, args.clickhouse_user, args.clickhouse_password, args.timeout
        )

        if not checked.ok:
            print("ÖLÇÜM YAPILMADI.", file=sys.stderr)
            print(checked.reason, file=sys.stderr)

            if checked.vendors:
                print("\nVendor dağılımı:", file=sys.stderr)

                for vendor, rows in sorted(checked.vendors.items()):
                    mark = "altın örnek bulundu" if checked.golden.get(vendor) else "BULUNAMADI"
                    print(f"  {vendor:<10} {rows:>8}   {mark}", file=sys.stderr)

            # Sondaları basmak, "bulamadım" ile "yanlış yerde aradım" arasını
            # operatörün elle ayırabilmesi için. Aksi hâlde bekçinin reddi
            # teşhis edilemez bir çıkmaz sokak.
            if checked.probes:
                print("\nAranan sondalar (herhangi birinin tutması yeterliydi):", file=sys.stderr)

                for vendor, candidates in sorted(checked.probes.items()):
                    for probe in candidates:
                        print(f"  {vendor:<10} |{probe}|", file=sys.stderr)

            return 3

        print(f"Ön kontrol: events_ocsf {checked.rows} satır")

        for vendor, rows in sorted(checked.vendors.items()):
            # Satırı olup altın örneği olmayan vendor buraya gelemiyor:
            # ön kontrol onu reddediyor. Kalan tek belirsizlik "hiç veri yok".
            if checked.golden.get(vendor):
                flag = "   altın örnek bulundu"
            else:
                flag = "   ← veri yok, bu vendor'ın kuralları ölçülemez"

            print(f"  {vendor:<10} {rows:>8}{flag}")

        print()
        args._preflight = checked

    try:
        report = measure(args)
    except ImportError as exc:
        print(f"Bağımlılık eksik: {exc}", file=sys.stderr)
        print("Kurulum için dosyanın başındaki komuta bakın.", file=sys.stderr)
        return 2

    print(f"Örneklem            : {report.rules} kural")
    print(f"Ölçülebilir         : {report.measurable} (verisi olan)")
    print(f"Derlendi            : {report.compiled}")
    print(f"ClickHouse kabul etti: {report.runs}")
    print(f"Satır döndürdü      : {report.matches}")

    if args.clickhouse_url:
        print(f"Eşleşme oranı       : {report.match_ratio:.0%}  ← kapsam kararının dayanağı")
    print(f"Pipeline'a dokunmadı: {report.untouched}")
    print(f"Eşleme satırı       : {report.pipeline_lines} ({report.mapped_fields} alan)")
    if report.matches:
        print(f"Kural başına eşleme : {report.mapping_lines_per_rule:.2f} satır")
    else:
        print("Kural başına eşleme : ölçülemedi (eşleşen kural yok)")
    print(f"Kural başına süre   : {report.seconds_per_rule * 1000:.1f} ms")
    print(f"unmapped kullanan   : {report.unmapped_rules} kural")

    if report.unhandled_by_field:
        print(f"\nEşleme dalı olmayan alanlar ({report.unhandled_rules} kuralı etkiliyor):")

        for column, count in sorted(
            report.unhandled_by_field.items(), key=lambda pair: (-pair[1], pair[0])
        ):
            print(f"  {column:<32} {count}")

        print("  → Bunlar STATİK; ClickHouse koşmadan da biliniyor.")

    if report.rejected_columns:
        print("\nClickHouse'un tanımadığı kolonlar (kaç kuralı düşürdü):")

        for column, count in sorted(
            report.rejected_columns.items(), key=lambda pair: (-pair[1], pair[0])
        ):
            print(f"  {column:<32} {count}")

        print("  → T31'in eşleme tablosunun ilk taslağı bu liste.")

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
