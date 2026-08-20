"""Bizigo `events_ocsf` görünümü için kalıcı Sigma ProcessingPipeline (T31).

T30 prototipinin yerini alıyor. Prototip bir **sayı** teslim etmişti; bu modül
o sayının dayandığı eşlemeyi kalıcı hâle getiriyor.

pySigma import'u neden tembel
-----------------------------
`sigma_compile.py` ile aynı gerekçe: pySigma kurulu değilse `/v1/sigma/compile`
503 dönmeli, mining uçları çalışmaya devam etmeli. Aynı imajdaki iki yetenek
birbirini düşürmemeli.

İkinci bir faydası var ve bu modül için asıl sebep o: **eşleme tablolarının
kendisi pySigma'sız test edilebiliyor.** Aşağıdaki sözlükler ve
`proto_to_text()` / `ip_text_expression()` saf Python; doğrulukları pySigma
sürümünden bağımsız ve CI'da pySigma olmadan da sınanıyor.

Üç ayrı boşluk sınıfı — karıştırılmamalı
----------------------------------------
T30 ölçümü `compiled=24 / runs=14` verdi ve aradaki 10 kural tek bir sebepten
düşmüyordu. Bu modül üçünü ayrı ele alıyor:

1. **Kolon var, ad farklı** → `FIELD_MAP`. Düz eşleme.
2. **Kolon yok ama veri `attrs`'ta var** → `ATTRS_MAP` + `unmapped['...']`.
3. **Veriyi hiçbir parser üretmiyor** → `SCHEMA_GAPS`. Bunlar eşlenmiyor,
   **derlemeyi düşürüyor**. Sebebi §7: eşlenseydi SQL koşar, sıfır satır
   döndürür ve bu "kural eşleşmedi" diye okunurdu. Hata yok, sayaç yok,
   belirti yok — bu deponun en pahalı hata sınıfı.

`attrs` anahtarları ad alanlı — düz ad YANLIŞ
--------------------------------------------
T30 prototipi `unmapped['url']` üretmeyi planlıyordu. **Ölçülmedi ve yanlıştı.**
`EventNormalizer.BuildAttributes` parser'ın `otel:` bloğunu `otel.` önekiyle,
`ocsf:` bloğunu `ocsf.` önekiyle yazıyor; yalnızca `fields:` öneksiz iniyor.
nginx parser'ı URL'i `otel."url.path"` altında tutuyor, yani gerçek anahtar
`otel.url.path`.

Düz `unmapped['url']` yazılsaydı SQL derlenir, koşar ve **sonsuza kadar sıfır
satır** döndürürdü. Aşağıdaki her anahtar `catalog/parsers/*/*.yaml`'daki
`otel:`/`fields:` bloklarına karşı **tek tek doğrulandı**.
"""

from __future__ import annotations

import os
import re
from dataclasses import dataclass
from pathlib import Path
from typing import Iterable

#: Görünümün adı. Backend varsayılanı `logs`; bizimki bu.
TABLE = "events_ocsf"

#: OCSF sınıf kimlikleri — K8 gereği kolona yazılan tek OCSF çifti
#: (`class_uid`, `activity_id`). `type_uid` bizde YOK; `ocsf_pipeline`'ın
#: zincire konmamasının sebebi de bu (T31 "Dışında").
CLASS_NETWORK_ACTIVITY = 4001
CLASS_HTTP_ACTIVITY = 4002
CLASS_DNS_ACTIVITY = 4003

#: `catalog/mappings/ip_proto_name.yaml` — imajda `/app/mappings` altında.
DEFAULT_MAPPINGS_PATH = "/app/mappings"


# ---------------------------------------------------------------------------
# 1) Kolonu olan alanlar
# ---------------------------------------------------------------------------

#: Sigma taxonomy → `events_ocsf` kolon adı.
#:
#: Yalnızca görünümde GERÇEKTEN var olan kolonlar (db/clickhouse/0003). Olmayan
#: bir ada eşlemek derlenen ama koşmayan SQL üretir — T30'un aramak için var
#: olduğu tuzağın ta kendisi.
#:
#: Adlar **düzleştirilmiş**, noktalı değil: `SigmaHQ/pySigma-pipeline-ocsf`
#: `dst_endpoint.ip` üretiyor ve bizim görünümümüzde o ad yok. Nokta hiç
#: doğmadığı için tırnaklama tutarsızlığı tuzağı da kaynağında kuruyor.
FIELD_MAP: dict[str, str] = {
    # --- Ağ uç noktaları ---
    "src_ip": "src_endpoint_ip",
    "srcip": "src_endpoint_ip",
    "SourceIp": "src_endpoint_ip",
    "src_port": "src_endpoint_port",
    "srcport": "src_endpoint_port",
    "SourcePort": "src_endpoint_port",
    "dst_ip": "dst_endpoint_ip",
    "dstip": "dst_endpoint_ip",
    "DestinationIp": "dst_endpoint_ip",
    "dst_port": "dst_endpoint_port",
    "dstport": "dst_endpoint_port",
    "DestinationPort": "dst_endpoint_port",
    # --- Taşıma ---
    "protocol": "connection_info_protocol_name",
    "proto": "connection_info_protocol_name",
    "Protocol": "connection_info_protocol_name",
    "network_protocol": "connection_info_protocol_name",
    # --- Kimlik ---
    "user": "actor_user_name",
    "User": "actor_user_name",
    "username": "actor_user_name",
    # `user_name` eksikti ve eksikliği SESSİZDİ: eşlenmeyince ham adıyla SQL'e
    # iniyor, `events_ocsf`'te öyle bir kolon yok, sorgu kırılıyor. Aşağıdaki
    # `bizigo_unmapped_field_guard` artık bu sınıfı topluca yakalıyor; bu satır
    # onun yakaladığı ilk vakanın düzeltmesi.
    "user_name": "actor_user_name",
    "TargetUserName": "actor_user_name",
    # --- Cihaz ---
    "hostname": "device_hostname",
    "Computer": "device_hostname",
    "host": "device_hostname",
    "vendor": "device_vendor_name",
    "product": "metadata_product_name",
    # --- Sonuç ---
    "action": "activity_name",
    "status": "status",
    # --- HTTP yöntemi KOLONA gidiyor, `attrs`'a değil ---
    #
    # nginx parser'ı `core.action: "{{ http_method }}"` yazıyor, yani yöntem
    # `activity_name` kolonunda duruyor. `unmapped['otel.http.request.method']`
    # de doğru cevabı verirdi ama Map erişimi indekslenmiyor; kolon indeksleniyor.
    # İki doğru cevaptan ucuz olanı.
    "http_method": "activity_name",
    "cs_method": "activity_name",
    # --- Serbest metin ---
    "message": "raw_data",
    "Message": "raw_data",
}

#: IP tutan kolonlar. Tipleri `IPv6` ve IPv4 adresler `::ffff:a.b.c.d` olarak
#: duruyor (db/clickhouse/0001_events.sql:45). Metin operatörleri için
#: `ip_text_expression()` gerekiyor — sebebi orada yazılı.
IP_COLUMNS: frozenset[str] = frozenset({"src_endpoint_ip", "dst_endpoint_ip"})


# ---------------------------------------------------------------------------
# 2) Kolonu olmayan ama `attrs` içinde duran alanlar
# ---------------------------------------------------------------------------

#: Sigma alanı → `unmapped` Map'indeki GERÇEK anahtar.
#:
#: ⚠️ Anahtarlar ad alanlı. `EventNormalizer.BuildAttributes`:
#:   * `parser.fields:` → öneksiz
#:   * `parser.ocsf:`   → `ocsf.` öneki
#:   * `parser.otel:`   → `otel.` öneki
#:
#: Her satır `catalog/parsers/*/*.yaml` içinde karşılığı görülerek yazıldı;
#: karşılığı olmayan alan buraya DEĞİL `SCHEMA_GAPS`'e gidiyor.
ATTRS_MAP: dict[str, str] = {
    # nginx: `otel."url.path": "{{ url_path }}"` (combined.yaml, access-json.yaml)
    # FortiGate: `otel."url.path": "{{ url }}"` (traffic.yaml)
    "url": "otel.url.path",
    "uri": "otel.url.path",
    "cs_uri_stem": "otel.url.path",
    # FortiGate: `otel."url.domain": "{{ hostname }}"`
    "url_domain": "otel.url.domain",
    # nginx: `otel."user_agent.original": "{{ agent }}"`
    "user_agent": "otel.user_agent.original",
    "cs_user_agent": "otel.user_agent.original",
    # nginx: `otel."http.response.status_code": "{{ response }}"`
    "status_code": "otel.http.response.status_code",
    "sc_status": "otel.http.response.status_code",
}

#: T32'nin `remedy` sözlüğü — **kopya değil, aynı değerler.**
#:
#: `tools/sigma-build/sigma_build/gate.py` kanonik kaynak. Sidecar imajı
#: `tools/` taşımadığı için oradan import edilemiyor; ayrışmayı bir test
#: yakalıyor (`test_remedy_sozlugu_T32_ile_ayni`). Ayrışırsa T32'nin manifesti
#: bizim engellerimizi yanlış tarafa sayar ve "liste boşaldı mı" sorusunun
#: cevabı sessizce bozulur.
REMEDY_SCHEMA = "schema"
REMEDY_UNKNOWN = "unknown"


@dataclass(frozen=True)
class SchemaGap:
    """Kapatılamayan bir alanın **gerekçesi ve sahibi**.

    Neden düz metin yetmiyor
    ------------------------
    Önce yalnızca gerekçe vardı ve gerekçe Türkçe bir cümleydi. T32'nin derleme
    hattı engelleri `remedy`'ye göre **kapanabilir** ve **kapanamaz** diye
    ayırıyor, ve o ayrım olmadan "gated listesi boşaldı mı" sorusunun cevabı
    asla evet olamaz (§8 — `Pending` ile `Exempt` aynı listede duramaz).

    Cümleden `remedy` çıkarmak, T32'ye Türkçe ayrıştırtmak olurdu.
    """

    reason: str
    remedy: str = REMEDY_SCHEMA


#: Kurallarda geçen ama **hiçbir parser'ın üretmediği** alanlar.
#:
#: Bunlar `ATTRS_MAP`'e konmuyor, çünkü var olmayan bir Map anahtarına erişim
#: ClickHouse'ta hata değil **boş dizge** döndürür: SQL koşar, sıfır satır
#: gelir, ve bu "kural eşleşmedi" diye okunur. Hata yok, sayaç yok, belirti yok.
#:
#: Bunun yerine derleme **düşürülüyor** — T31'in kabul kriteri: "eşlenemeyen
#: kural sessizce geçmiyor".
#:
#: Liste kısaldıkça kapsam büyür: bir alanın buradan çıkması için onu üreten
#: bir parser gerekiyor, pipeline satırı değil. Yani bu liste **parser
#: kataloğuna açılmış bir talep**.
#:
#: ⚠️ Hiçbir girdi `upstream` DEĞİL. T32'nin sözlüğünde `upstream` "kimsenin
#: yapamayacağı iş" demek ve bilinçli bir muafiyet gibi konuluyor, sayısı ayrıca
#: sabitleniyor. Muafiyet vermek **iki ayrı bilinçli hareket** gerektiriyor ve
#: ikincisi T32'nin sahibinde; buradan tek taraflı verilemez. Adayı `rule_name`
#: ve `unknown` olarak duruyor — "kapanamaz" değil "kapanır mı bilmiyoruz",
#: yani sayımda kapanabilirler tarafında ve listeden gizlenmiyor.
SCHEMA_GAPS: dict[str, SchemaGap] = {
    "dns_query_name": SchemaGap(
        "Hiçbir parser DNS sorgu adı üretmiyor. FortiGate `traffic`/`event` ve "
        "MikroTik `system`/`firewall` parser'larında DNS alanı yok. Kapatmak için "
        "önce bir DNS parser'ı gerekiyor."
    ),
    "query": SchemaGap(
        "Aynı boşluk. Ayrıca `nginx` logsource'unda DNS sorgusu aramak kuralın "
        "kendi hatası: nginx bir web sunucusu, DNS sorgusu üretmiyor."
    ),
    "QueryName": SchemaGap(
        "Bkz. `dns_query_name` — aynı boşluk, Windows adlandırmasıyla. Aynı DNS "
        "parser'ı ikisini de kapatır."
    ),
    "answer": SchemaGap(
        "DNS cevabı üreten parser yok. `dns_query_name` ile aynı boşluk; ikisi "
        "birlikte kapanır, çünkü ikisini de aynı DNS parser'ı üretecek."
    ),
    "rule_name": SchemaGap(
        "FortiGate `policyid` üretiyor ama kural ADI değil. Numarayı ada eşlemek "
        "cihazın yapılandırmasını gerektirir ve o yapılandırma log satırında hiç "
        "yok — yani bir parser değişikliği bunu KAPATMIYOR. `upstream` adayı, ama "
        "muafiyeti T32'nin sahibi verecek; o güne kadar `unknown`.",
        REMEDY_UNKNOWN,
    ),
    "policy_id": SchemaGap(
        "FortiGate ham satırında `policyid` var ama parser onu `fields:`'e "
        "almıyor, dolayısıyla `attrs`'a inmiyor. Parser değişikliği gerekiyor."
    ),
}


def unmapped_expression(attrs_key: str) -> str:
    """`unmapped` Map'ine erişim ifadesi.

    Nokta erişimi (`unmapped.X`) ClickHouse'ta Tuple/Nested içindir; bizim
    kolonumuz `Map(LowCardinality(String), String)`. Doğru biçim köşeli parantez.

    ⚠️ Var olmayan anahtar hata değil **boş dizge** döndürür. Olumlu koşullarda
    (`contains`, `startswith`) bu güvenli — eşleşme olmaz. Olumsuz koşullarda
    (`not contains`) tehlikeli: eksik anahtar koşulu SAĞLAR. Bu yüzden anahtar
    varlığı `SCHEMA_GAPS` ile derleme zamanında eleniyor, sorgu zamanında değil.
    """
    if "'" in attrs_key or "\\" in attrs_key:
        raise ValueError(f"attrs anahtarı tırnak/ters bölü taşıyamaz: {attrs_key!r}")

    return f"unmapped['{attrs_key}']"


# ---------------------------------------------------------------------------
# 3) Değer dönüşümleri
# ---------------------------------------------------------------------------

#: `::ffff:` önekini söken ifade — `ip_text_expression()` bunu üretiyor.
_IPV4_MAPPED_PREFIX = "^::ffff:"


def ip_text_expression(column: str) -> str:
    """IP kolonunun **metin** karşılığı; `startswith`/`contains` için.

    Sorun
    -----
    `src_endpoint_ip` tipi `IPv6` ve backend `ILIKE` üretiyor — `ILIKE` String
    ister, sorgu tip hatasıyla düşer. Ama asıl tuzak tip hatası DEĞİL.

    Düz `toString(src_endpoint_ip)` yazılsaydı sorgu koşardı: ClickHouse IPv4
    adresleri `::ffff:a.b.c.d` olarak saklıyor (0001_events.sql:45), yani
    `toString()` `'::ffff:203.0.113.7'` verir ve `ILIKE '203.0.113.%'`
    **hiçbir zaman** tutmaz. Derlenen, koşan, sonsuza kadar sıfır döndüren
    sorgu — §7'nin tarif ettiği sınıf. Önek bu yüzden söküIüyor.

    Neden CIDR değil
    ----------------
    `isIPAddressInRange(..., '203.0.113.0/24')` daha "doğru" görünüyor ama:

    * Yalnızca **oktet hizalı** önekler için çalışıyor. `srcip|startswith: '10.1'`
      için doğru bir CIDR **yok**; o dal yine metne düşerdi. Aynı operatörün iki
      farklı anlambilime sahip iki yolu olması, tek yolun kusurundan kötü.
    * Sigma'nın kendi anlambilimini **değiştirirdi**. `startswith` bir dizge
      önekidir; onu alt ağ iddiasına çevirmek kural yazarının niyetini bizim
      tahmin etmemiz olur. Kural burada başka bir backend'dekiyle aynı şeyi
      söylemeli.

    Bedeli
    ------
    İfade **indekslenmiyor** — `unmapped[...]` erişimiyle aynı maliyet sınıfı.
    Eşitlik karşılaştırmaları bu yoldan GEÇMİYOR, kolonun kendisini kullanıyor
    ve indeksli kalıyor.
    """
    return f"replaceRegexpOne(toString({column}), '{_IPV4_MAPPED_PREFIX}', '')"


#: Backend'in **tırnaklamaması** gereken alan ifadeleri.
#:
#: `ClickhouseBackend.field_quote_pattern` varsayılanı `^[a-zA-Z0-9_]*$` ve
#: eşleşmeyen her adı backtick'e alıyor. `unmapped['otel.url.path']` düz ad
#: olmadığı için `` `unmapped['otel.url.path']` `` oluyordu — ClickHouse bunu
#: bir KOLON ADI sanar ve "böyle kolon yok" der.
#:
#: **Bu ölçülerek bulundu.** Pipeline yazıldıktan sonra örneklem gerçekten
#: derlendi ve üretilen SQL okundu; tırnaklama olmasaydı hata ancak canlı
#: koşumda görünürdü.
#:
#: Desen bilinçli olarak **dar**: düz ad, bizim Map erişimimiz, bizim IP
#: ifademiz. Genişletmek "her ifadeyi tırnaksız bırak" demek olurdu ve gerçekten
#: tuhaf bir alan adı sessizce SQL'e sızardı.
FIELD_EXPRESSION_PATTERN = (
    r"^(?:"
    r"[a-zA-Z0-9_]*"
    r"|unmapped\['[a-zA-Z0-9_.]+'\]"
    r"|replaceRegexpOne\(toString\([a-zA-Z0-9_]+\), '[^']*', ''\)"
    r")$"
)


def known_field_pattern() -> str:
    """Eşleme sonrası **kabul edilebilir** alan biçimleri; genel bekçinin ölçütü.

    Üç şey geçerli: görünümün gerçek bir kolonu, bizim Map erişimimiz, bizim IP
    ifademiz. Başka her şey eşlenmemiş demektir ve derlemeyi düşürüyor.

    Kolon listesi burada `VIEW_COLUMNS`'tan türetiliyor, elle yazılmıyor:
    ikinci bir liste tutulsaydı biri güncellenip diğeri unutulurdu ve bekçi
    sessizce yanlış alanı geçirirdi.
    """
    columns = "|".join(sorted(re.escape(column) for column in VIEW_COLUMNS))

    return (
        r"^(?:"
        + columns
        + r"|unmapped\['[a-zA-Z0-9_.]+'\]"
        + r"|replaceRegexpOne\(toString\([a-zA-Z0-9_]+\), '[^']*', ''\)"
        + r")$"
    )


def load_proto_table(mappings_path: Path | str | None = None) -> dict[str, str]:
    """`ip_proto_name` tablosunu katalogdan okur.

    **Kopyalanmıyor, okunuyor.** `masks.py` ile aynı gerekçe: tablo burada
    yeniden yazılsaydı katalogla sessizce ayrışırdı. `catalog/mappings/`
    ingest tarafının da okuduğu tek kaynak — FortiGate `proto=6` yazıyor, Cisco
    `tcp` yazıyor, ikisi de o tabloda `tcp`'ye iniyor. Sigma tarafı farklı bir
    sözlük kullansaydı `proto: 6` içeren kural, ingest'in `tcp` yazdığı satırı
    bulamazdı.
    """
    import yaml

    root = Path(mappings_path or os.environ.get("BIZIGO_MAPPINGS_PATH", DEFAULT_MAPPINGS_PATH))
    document = yaml.safe_load((root / "ip_proto_name.yaml").read_text(encoding="utf-8")) or {}

    # Anahtarlar YAML'da tırnaklı ("6") ama bir sürüm tırnağı düşürürse int
    # olurlar; ikisini de metne indiriyoruz ki arama ordinal kalsın.
    return {str(key): str(value) for key, value in document.items()}


def proto_to_text(value: object, table: dict[str, str]) -> str | None:
    """Sigma'daki protokol değerini kolonun tuttuğu metne çevirir.

    `connection_info_protocol_name` `LowCardinality(String)` ve ingest oraya
    tablodan geçmiş küçük harfli adı yazıyor (`tcp`). Kural `proto: 6` yazarsa
    karşılaştırma **hiçbir zaman** tutmaz; `proto: TCP` yazarsa büyük/küçük
    harf yüzünden tutmaz.

    `None` dönmesi "bu değeri tanımıyorum" demek — çağıran değeri olduğu gibi
    bırakıyor. Tanımadığı bir değeri uydurmak, yanlış eşleşme üretirdi.
    """
    key = str(value).strip()

    if not key:
        return None

    if key in table:
        return table[key]

    upper = key.upper()
    if upper in table:
        return table[upper]

    # Zaten kanonik biçimdeyse (`tcp`) tablo değerleri arasında görünür.
    lower = key.lower()
    if lower in table.values():
        return lower

    return None


# ---------------------------------------------------------------------------
# Statik doğrulama — tabloların kendi tutarlılığı
# ---------------------------------------------------------------------------

#: `events_ocsf` kolonları (db/clickhouse/0003_ocsf_otel_views.sql).
#:
#: Elle yazılı, çünkü doğrulama ClickHouse'suz koşabilmeli. Ayrışırsa canlı
#: koşum yakalar: orada kolonu ClickHouse'un kendisi reddediyor.
VIEW_COLUMNS: frozenset[str] = frozenset({
    "time", "uid", "owner_group", "class_uid", "activity_id", "severity_id",
    "src_endpoint_ip", "src_endpoint_port", "dst_endpoint_ip", "dst_endpoint_port",
    "connection_info_protocol_name", "activity_name", "status",
    "actor_user_name", "device_hostname", "device_vendor_name",
    "metadata_product_name", "metadata_version", "unmapped", "raw_data", "raw_ref",
})


def validate_tables() -> list[str]:
    """Eşleme tablolarının kendi içinde tutarlı olup olmadığı; hata listesi.

    Neden bir fonksiyon ve neden testte çağrılıyor: bu üç sözlük elle
    büyüyecek ve büyürken en kolay yapılacak hata, görünümde olmayan bir kolona
    eşlemek. O hata derlemeyi geçer, SQL'i kırar ve hiçbir şey kırmızı yanmaz.
    """
    problems: list[str] = []

    for sigma_field, column in FIELD_MAP.items():
        if column not in VIEW_COLUMNS:
            problems.append(f"FIELD_MAP[{sigma_field!r}] → {column!r}: görünümde böyle kolon yok")

    for sigma_field in ATTRS_MAP:
        if sigma_field in FIELD_MAP:
            problems.append(f"{sigma_field!r} hem FIELD_MAP hem ATTRS_MAP içinde")

    for sigma_field in SCHEMA_GAPS:
        if sigma_field in FIELD_MAP or sigma_field in ATTRS_MAP:
            problems.append(f"{sigma_field!r} hem SCHEMA_GAPS hem bir eşleme içinde")

    for column in IP_COLUMNS:
        if column not in VIEW_COLUMNS:
            problems.append(f"IP_COLUMNS: {column!r} görünümde yok")

    return problems


def handled_fields() -> frozenset[str]:
    """Pipeline'ın bir cevabı olan Sigma alanları — boşluk ölçümünün paydası."""
    return frozenset(FIELD_MAP) | frozenset(ATTRS_MAP) | frozenset(SCHEMA_GAPS)


def rule_fields(rule_text: str) -> list[str]:
    """Kuralın `detection` bloğundaki alan adları (operatör eki atılmış).

    Saf metin işi: pySigma gerekmiyor, dolayısıyla kapsam ölçümü backend
    kurulu olmadan da alınabiliyor.
    """
    # `\n` ile aramak, `detection:` dosyanın ILK satırıysa bloğu hiç bulamıyordu
    # ve fonksiyon sessizce boş liste dönüyordu — "alan yok" ile "bakamadım"
    # aynı görünürdü. Başlangıç da bir sınır sayılıyor.
    match = re.search(r"(?:^|\n)detection:", rule_text)
    if match is None:
        return []

    body = re.split(r"\n[a-z_]+:", rule_text[match.end() :])[0]
    found: list[str] = []

    for match in re.finditer(r"^\s{4}([A-Za-z_][A-Za-z0-9_]*)(\|[a-z|]+)?:", body, re.M):
        field = match.group(1)

        if field not in found:
            found.append(field)

    return found


def unsupported_fields(rule_text: str) -> list[str]:
    """Kuralın, pipeline'ın hiçbir dalına uymayan alanları.

    Boş liste "bu kural derlenir" demek değil — yalnızca alan adlarının
    tanındığını söylüyor.
    """
    known = handled_fields()

    return [field for field in rule_fields(rule_text) if field not in known]


# ---------------------------------------------------------------------------
# pySigma pipeline — buradan aşağısı backend'e bağlı
# ---------------------------------------------------------------------------


def _proto_transformation(table: dict[str, str]):
    """`proto: 6` → `proto: 'tcp'`.

    Kolon `LowCardinality(String)` ve ingest oraya katalog tablosundan geçmiş
    küçük harfli adı yazıyor. Sayıyla karşılaştırma ClickHouse'ta **tip
    hatası** verir; `TCP` ile karşılaştırma hata vermez ama hiçbir zaman
    tutmaz — ikincisi daha tehlikeli olan.

    Tanınmayan değer **olduğu gibi bırakılıyor**: uydurulmuş bir çeviri,
    eşleşmemekten kötü bir yanlış eşleşme üretirdi.
    """
    from sigma.processing.transformations.base import ValueTransformation
    from sigma.types import SigmaString

    class _ProtoValue(ValueTransformation):
        def apply_value(self, field, val):
            text = proto_to_text(getattr(val, "to_plain", lambda: val)(), table)

            return None if text is None else SigmaString(text)

    return _ProtoValue()


def _ip_text_transformation():
    """IP kolonlarını, **yalnızca joker içeren** karşılaştırmalarda metne çevirir.

    Neden yalnızca joker: eşitlik (`srcip: 10.0.0.1`) kolonun kendi `IPv6`
    tipiyle çalışıyor ve **indeksli**. Hepsini metne çevirmek en sık kullanılan
    IP koşulunu indeksten düşürürdü — düzeltilen hatadan pahalı bir bedel.

    `startswith`/`contains`/`endswith` pySigma'da joker taşıyan `SigmaString`'e
    dönüşüyor; ayrımı oradan yapıyoruz.
    """
    from sigma.processing.transformations.base import DetectionItemTransformation
    from sigma.types import SigmaString

    class _IpText(DetectionItemTransformation):
        def apply_detection_item(self, detection_item):
            if detection_item.field not in IP_COLUMNS:
                return None

            values = detection_item.value or []
            if not any(
                isinstance(value, SigmaString) and value.contains_special() for value in values
            ):
                return None

            detection_item.field = ip_text_expression(detection_item.field)

            return None

    return _IpText()


def _logsource_items(processing_item, logsource_condition, add_condition):
    """Vendor ve sınıf daraltmaları.

    Vendor daraltması olmasaydı bir FortiGate kuralı Cisco satırlarını da
    tarardı — ve `dst_endpoint_ip` her ikisinde de dolu olduğu için bunu
    hiçbir şey belli etmezdi.
    """
    classes = (
        ("bizigo_class_network", CLASS_NETWORK_ACTIVITY, ("firewall", "network_connection")),
        ("bizigo_class_http", CLASS_HTTP_ACTIVITY, ("webserver", "proxy")),
        ("bizigo_class_dns", CLASS_DNS_ACTIVITY, ("dns", "dns_query")),
    )
    vendors = (
        ("bizigo_vendor_fortinet", "fortigate", "device_vendor_name", "Fortinet"),
        ("bizigo_vendor_cisco", "asa", "device_vendor_name", "Cisco"),
        ("bizigo_vendor_mikrotik", "routeros", "device_vendor_name", "MikroTik"),
        ("bizigo_vendor_nginx", "nginx", "metadata_product_name", "nginx"),
    )

    items = []

    for identifier, class_uid, categories in classes:
        items.append(
            processing_item(
                identifier=identifier,
                transformation=add_condition({"class_uid": class_uid}),
                rule_conditions=[logsource_condition(category=name) for name in categories],
                rule_condition_linking=any,
            )
        )

    for identifier, product, column, value in vendors:
        items.append(
            processing_item(
                identifier=identifier,
                transformation=add_condition({column: value}),
                rule_conditions=[logsource_condition(product=product)],
            )
        )

    return items


def bizigo_pipeline(mappings_path: Path | str | None = None):
    """`events_ocsf` için Sigma ProcessingPipeline.

    pySigma burada import ediliyor, modül düzeyinde değil: backend yoksa bu
    fonksiyon patlar, `sigma_compile.py` onu 503'e çevirir ve mining uçları
    etkilenmez.
    """
    from sigma.processing.pipeline import ProcessingItem, ProcessingPipeline
    from sigma.processing.conditions import (
        ExcludeFieldCondition,
        IncludeFieldCondition,
        LogsourceCondition,
    )
    from sigma.processing.transformations import (
        AddConditionTransformation,
        DetectionItemFailureTransformation,
        FieldMappingTransformation,
        SetStateTransformation,
    )

    problems = validate_tables()
    if problems:
        # Tutarsız tablolarla pipeline kurmak, kırık SQL'i sessizce üretmek
        # olurdu. Kurulum hatası kurulum anında patlamalı.
        raise ValueError("Sigma eşleme tabloları tutarsız:\n  " + "\n  ".join(problems))

    proto_table = load_proto_table(mappings_path)

    # `attrs` erişimleri de bir ad eşlemesi: `url` → `unmapped['otel.url.path']`.
    attrs_expressions = {
        field: unmapped_expression(key) for field, key in ATTRS_MAP.items()
    }

    items: list = [
        # 1) Şema boşlukları ÖNCE. Sıra önemli: sonraki eşlemeler bu alanlara
        #    dokunmadığı için ham adla SQL'e inerlerdi ve `unmapped` sanılırdı.
        #    Burada derleme düşüyor ve sebebi kuralın yanında yazılı oluyor.
        ProcessingItem(
            identifier=f"bizigo_schema_gap_{field}",
            transformation=DetectionItemFailureTransformation(
                f"`{field}` bu şemada eşlenemiyor [remedy={gap.remedy}]: {gap.reason}"
            ),
            field_name_conditions=[IncludeFieldCondition(fields=[field])],
        )
        for field, gap in SCHEMA_GAPS.items()
    ]

    items += [
        # 2) Kolonu olan alanlar.
        ProcessingItem(
            identifier="bizigo_field_names",
            transformation=FieldMappingTransformation(FIELD_MAP),
        ),
        # 3) Kolonu olmayan ama `attrs`'ta duran alanlar.
        ProcessingItem(
            identifier="bizigo_attrs_fields",
            transformation=FieldMappingTransformation(attrs_expressions),
        ),
        # 4) Değer dönüşümleri — ad eşlemesinden SONRA, çünkü ikisi de
        #    eşlenmiş kolon adına bakıyor.
        ProcessingItem(
            identifier="bizigo_proto_value",
            transformation=_proto_transformation(proto_table),
            field_name_conditions=[
                IncludeFieldCondition(fields=["connection_info_protocol_name"])
            ],
        ),
        ProcessingItem(
            identifier="bizigo_ip_text",
            transformation=_ip_text_transformation(),
        ),
    ]

    # 5) Genel bekçi — eşlemelerden SONRA, daraltmalardan ÖNCE.
    #
    # Buraya kadar eşlenmemiş bir alan `events_ocsf`'te var olmayan bir kolon
    # adıyla SQL'e iner. ClickHouse onu reddeder, ama reddi ancak sorgu
    # KOŞTURULDUĞUNDA görünür: derleme yeşil, kural kataloğa girer, ve hata
    # canlıda ortaya çıkar.
    #
    # `SCHEMA_GAPS` yalnızca ÖNGÖRÜLEN alanları kapsıyordu. Bu bekçi
    # öngörülmeyenleri de kapsıyor ve gerekçesi somut: `user_name` eşlemesi
    # eksikti, hiçbir şey kırmızı yanmıyordu, bunu bir API testi tesadüfen
    # yakaladı. Tesadüfe bırakılacak bir sınıf değil.
    items.append(
        ProcessingItem(
            identifier="bizigo_unmapped_field_guard",
            transformation=DetectionItemFailureTransformation(
                "Bu alan `events_ocsf` görünümüne eşlenmiyor. Kolonu varsa "
                "`FIELD_MAP`'e, `attrs` içindeyse `ATTRS_MAP`'e, hiç üretilmiyorsa "
                "`SCHEMA_GAPS`'e eklenmeli — eşlenmeden bırakılırsa üretilen SQL "
                "var olmayan bir kolona vurur ve bu ancak canlıda görünür."
            ),
            field_name_conditions=[
                ExcludeFieldCondition(fields=[known_field_pattern()], mode="re")
            ],
        )
    )

    items += _logsource_items(ProcessingItem, LogsourceCondition, AddConditionTransformation)

    items.append(
        ProcessingItem(
            identifier="bizigo_table",
            transformation=SetStateTransformation("table", TABLE),
        )
    )

    pipeline = ProcessingPipeline(name="bizigo-events-ocsf", priority=10, items=items)

    # Değer dönüşümleri pipeline'ın dışında tutuluyor; gerekçesi
    # `apply_value_transformations()` içinde.
    pipeline.vars = {"proto_table": proto_table}

    return pipeline


def bizigo_backend(table: str = TABLE, mappings_path: Path | str | None = None):
    """Pipeline'ı bağlanmış ve tırnaklaması düzeltilmiş ClickHouse backend'i.

    Backend'i ayrı kurup pipeline'ı elle takmak yerine tek yerden veriliyor:
    ikisi ayrı ayrı kurulabilseydi biri pipeline'sız backend kurar ve üretilen
    SQL **ham Sigma adlarına** vururdu. Derleme başarılı olur, sorgu kırılır.
    """
    from sigma.backends.clickhouse.clickhouse import ClickhouseBackend

    class BizigoClickhouseBackend(ClickhouseBackend):
        # Gerekçe `FIELD_EXPRESSION_PATTERN` üzerinde yazılı: bizim ürettiğimiz
        # ifadeler zaten geçerli SQL; backtick'lenirse kolon adı sanılıyorlar.
        field_quote_pattern = re.compile(FIELD_EXPRESSION_PATTERN)

    return BizigoClickhouseBackend(
        processing_pipeline=bizigo_pipeline(mappings_path),
        table_name=table,
    )


def mapped_field_count() -> int:
    """Eşlenen alan sayısı — T30 ölçüm biriminin payı."""
    return len(FIELD_MAP) + len(ATTRS_MAP)


def pipeline_line_count(path: Path | None = None) -> int:
    """Bu dosyanın anlamlı satır sayısı (yorum ve docstring hariç).

    T30'un ölçüm birimi buradan türüyor: eşleme satırı / eşleşen kural.
    Yorumları saymamak bilinçli — ölçülen şey bakım yükü değil, yazılması
    gereken eşleme miktarı.
    """
    source = (path or Path(__file__)).read_text(encoding="utf-8").splitlines()
    meaningful = 0
    in_docstring = False

    for raw in source:
        line = raw.strip()

        if line.startswith('"""') or line.endswith('"""'):
            in_docstring = not in_docstring
            continue
        if in_docstring or not line or line.startswith("#"):
            continue

        meaningful += 1

    return meaningful


def describe() -> dict[str, Iterable[str]]:
    """Pipeline'ın kapsamı — T32'nin derleme kapısı ve raporlama için."""
    return {
        "table": TABLE,
        "columns": sorted(set(FIELD_MAP.values())),
        "attrs_keys": sorted(ATTRS_MAP.values()),
        # Düz liste yerine remedy'ye göre: T33 ekranı "31'i şema bekliyor,
        # 11'i asla derlenmeyecek" diyebilmeli. Tek liste, iki cümleyi
        # ayıramaz.
        "schema_gaps": {
            field: {"remedy": gap.remedy, "reason": gap.reason}
            for field, gap in sorted(SCHEMA_GAPS.items())
        },
    }
