"""Bizigo `events_ocsf` görünümü için Sigma ProcessingPipeline — **prototip** (T30).

⚠️ Bu kod **atılabilir**. T30'un teslim ettiği şey kod değil bir sayı: Sigma
kuralı başına eşleme maliyeti. Kalıcı pipeline T31'in işi ve muhtemelen bunun
şeklini almayacak; buradaki değer ölçümün kendisi ve ortaya çıkan tuzaklar.

Neden kendi pipeline'ımızı yazıyoruz
------------------------------------
Ölçüldü: `SigmaHQ/pySigma-pipeline-ocsf` SigmaHQ kataloğunun %80'ine dokunuyor
ama bizim görünümümüze karşı **0 kural** olduğu gibi çalışıyor. Sebebi tek
cümle: o pipeline OCSF 1.5'in **noktalı** yol adlarını üretiyor
(`dst_endpoint.ip`), K30'un görünümü ise **düzleştirilmiş** ad kullanıyor
(`dst_endpoint_ip`). Derleme başarılı olur, SQL var olmayan kolona referans
verir — asıl tehlike bu, derleme hatası değil.

Dört bilinen tuzak ve buradaki karşılıkları
-------------------------------------------
1. **Noktalı yol → düzleştirilmiş ad.** `FIELD_MAP` bunu yapıyor.
2. **`FROM logs`.** Backend sabit tablo adı yazıyor; `TABLE` ile değiştiriliyor.
3. **Tutarsız tırnaklama.** Düzleştirilmiş adlar tırnak GEREKTİRMİYOR — nokta
   kalmadığı için sorun kaynağında kuruyor.
4. **`unmapped.X`.** Bizde `unmapped` bir `Map(String, String)`; nokta erişimi
   ClickHouse'ta derlenmiyor. `unmapped['X']` üretiliyor.
"""

from __future__ import annotations

from sigma.processing.conditions import (
    IncludeFieldCondition,
    LogsourceCondition,
)
from sigma.processing.pipeline import ProcessingItem, ProcessingPipeline
from sigma.processing.transformations import (
    AddConditionTransformation,
    FieldMappingTransformation,
    SetStateTransformation,
)

#: Görünümün gerçek adı. Backend'in `FROM logs`'u bununla değiştiriliyor.
TABLE = "events_ocsf"

#: OCSF sınıf kimlikleri — K8'in yazdığı tek kolon çifti.
#: `4001` Network Activity, `4003` DNS Activity.
CLASS_NETWORK_ACTIVITY = 4001
CLASS_DNS_ACTIVITY = 4003

#: Sigma taxonomy → `events_ocsf` kolon adı.
#:
#: Bu sözlüğün **boyutu ölçümün birimi**: kural başına kaç satır eşleme
#: gerektiğini buradan sayıyoruz. Yalnızca görünümde GERÇEKTEN var olan kolonlar
#: yazılı — olmayan bir ada eşlemek, derlenen ama koşmayan SQL üretir ve T30'un
#: kapatmak istediği tuzağın ta kendisidir.
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
    # --- Cihaz ---
    "hostname": "device_hostname",
    "Computer": "device_hostname",
    "host": "device_hostname",
    "vendor": "device_vendor_name",
    "product": "metadata_product_name",
    # --- Sonuç ---
    "action": "activity_name",
    "status": "status",
    # --- Serbest metin ---
    "message": "raw_data",
    "Message": "raw_data",
}

#: Görünümde karşılığı OLMAYAN ama kurallarda sık geçen alanlar.
#:
#: Bunlar `unmapped` Map'ine düşüyor. Ayrı bir liste, çünkü ölçümün ikinci
#: sayısı bu: kural başına kaç alan düzleştirilmiş kolona değil Map'e gidiyor.
#: Map erişimi ClickHouse'ta çalışıyor ama **indekslenmiyor** — yani maliyet
#: doğruluk değil hız.
UNMAPPED_FIELDS: tuple[str, ...] = (
    "dns_query_name",
    "query",
    "QueryName",
    "answer",
    "url",
    "http_method",
    "user_agent",
    "rule_name",
    "policy_id",
)


def unmapped_expression(field: str) -> str:
    """`unmapped` Map'ine erişim ifadesi.

    Pipeline'ın ürettiği `unmapped.X` ClickHouse'ta **derlenmiyor**: nokta
    erişimi Tuple/Nested içindir, bizim kolonumuz `Map(String, String)`.
    Doğru biçim köşeli parantez.
    """
    return f"unmapped['{field}']"


def bizigo_pipeline() -> ProcessingPipeline:
    """Örneklemin gerektirdiği en küçük pipeline.

    <b>Bilerek dar.</b> T30'un sorusu "kaç satır yazmak gerekiyor"; kapsamı
    gereğinden geniş tutmak o sayıyı şişirir ve kapsam kararını yanlış bilgiyle
    verdirir.
    """
    items: list[ProcessingItem] = [
        # 1) Alan adı eşlemesi. Ölçümün birinci sayısı bu sözlüğün boyutu.
        ProcessingItem(
            identifier="bizigo_field_names",
            transformation=FieldMappingTransformation(FIELD_MAP),
        ),
        # 2) Sınıf ayırıcısı. `ocsf_pipeline` bunu `type_uid` ile yapıyor;
        #    bizde yazılan tek OCSF kolonu `class_uid` (K8) ve `type_uid`
        #    görünümde YOK — üretilen SQL'in koşmamasının sebeplerinden biri.
        ProcessingItem(
            identifier="bizigo_class_network",
            transformation=AddConditionTransformation({"class_uid": CLASS_NETWORK_ACTIVITY}),
            rule_conditions=[
                LogsourceCondition(category="firewall"),
                LogsourceCondition(category="network_connection"),
            ],
            rule_condition_linking=any,
        ),
        ProcessingItem(
            identifier="bizigo_class_dns",
            transformation=AddConditionTransformation({"class_uid": CLASS_DNS_ACTIVITY}),
            rule_conditions=[
                LogsourceCondition(category="dns"),
                LogsourceCondition(category="dns_query"),
            ],
            rule_condition_linking=any,
        ),
        # 3) Vendor daraltması. F1 kataloğu dört vendor tanıyor; kuralın
        #    `product`'ı bunlardan biriyse sorguya `device_vendor_name`
        #    koşulu ekleniyor. Olmasaydı bir FortiGate kuralı Cisco satırlarını
        #    da tarardı — ve `dst_endpoint_ip` her ikisinde de dolu olduğu için
        #    bunu hiçbir şey belli etmezdi.
        ProcessingItem(
            identifier="bizigo_vendor_fortinet",
            transformation=AddConditionTransformation({"device_vendor_name": "Fortinet"}),
            rule_conditions=[LogsourceCondition(product="fortigate")],
        ),
        ProcessingItem(
            identifier="bizigo_vendor_cisco",
            transformation=AddConditionTransformation({"device_vendor_name": "Cisco"}),
            rule_conditions=[LogsourceCondition(product="asa")],
        ),
        ProcessingItem(
            identifier="bizigo_vendor_mikrotik",
            transformation=AddConditionTransformation({"device_vendor_name": "MikroTik"}),
            rule_conditions=[LogsourceCondition(product="routeros")],
        ),
        ProcessingItem(
            identifier="bizigo_vendor_nginx",
            transformation=AddConditionTransformation({"metadata_product_name": "nginx"}),
            rule_conditions=[LogsourceCondition(product="nginx")],
        ),
        # 4) Tablo adı. Backend `FROM logs` yazıyor; durum değişkeni ile
        #    değiştiriliyor. Backend bunu okumazsa `measure.py` SQL üstünde
        #    metin ikamesi yapıyor ve bunu RAPORLUYOR — sessiz düzeltme,
        #    ölçümün kendisini yalanlardı.
        ProcessingItem(
            identifier="bizigo_table",
            transformation=SetStateTransformation("table", TABLE),
        ),
    ]

    return ProcessingPipeline(name="bizigo-events-ocsf-prototype", priority=10, items=items)


def mapped_field_count() -> int:
    """Eşleme sözlüğünün boyutu — ölçümün birinci sayısı."""
    return len(FIELD_MAP)


def pipeline_line_count() -> int:
    """Bu dosyanın **anlamlı** satır sayısı (yorum ve boş satır hariç).

    Kural başına maliyet bundan türetiliyor: toplam satır / eşlenen kural.
    Yorumları saymamak bilinçli — ölçülen şey bakım yükü değil, yazılması
    gereken eşleme miktarı.
    """
    from pathlib import Path

    source = Path(__file__).read_text(encoding="utf-8").splitlines()
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
