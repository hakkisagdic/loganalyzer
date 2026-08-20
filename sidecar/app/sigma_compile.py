"""Sigma → ClickHouse SQL derlemesi (F1 §9, T12 kapsamı yalnızca bu uç).

**Sıcak yolda Python yok**: F3'te kural kataloğu build-time derlenecek, bu uç
yalnızca UI'daki "bu kuralı derle ve önizle" akışı için var.

Backend import'u **tembel** ve arızası uca hapsedilmiş: pySigma backend'i
kurulu değilse ya da API'si değişmişse `/v1/sigma/compile` 503 döner,
mining uçları çalışmaya devam eder. Aynı imajdaki iki yetenek birbirini
düşürmemeli.
"""

from __future__ import annotations

from dataclasses import dataclass
from functools import lru_cache


class SigmaBackendUnavailable(RuntimeError):
    """Backend kurulu değil / import edilemiyor — 503."""


class UnsupportedTarget(ValueError):
    """İstenen hedef derlenemiyor — 400.

    pySigma'nın kendi hataları da `ValueError` türevi olduğu için ayrı bir tip
    gerekiyor: "hedefi bilmiyorum" (istemci yanlış çağırdı) ile "kural bozuk"
    (kullanıcı yanlış yazdı) farklı yanıtlar hak ediyor.
    """


@dataclass(frozen=True)
class CompiledRule:
    queries: list[str]
    warnings: list[str]


@lru_cache(maxsize=1)
def backend_name() -> str:
    try:
        from sigma.backends.clickhouse.clickhouse import ClickhouseBackend  # noqa: F401
    except Exception as exc:  # noqa: BLE001
        return f"unavailable ({type(exc).__name__})"
    # Eşleme adı da rapor ediliyor: sağlık ucu "backend var" derken hangi
    # ŞEMAYA derlediğini söylemezse, pipeline'sız bir dağıtım fark edilmez.
    return "sigma.backends.clickhouse.ClickhouseBackend + bizigo-events-ocsf"


def compile_rule(
    rule_yaml: str,
    target: str,
    table_name: str = "events_ocsf",
    full_log_column: str = "raw_data",
    mappings_path: str | None = None,
) -> CompiledRule:
    """Sigma kuralını ClickHouse SQL'ine çevirir — **Bizigo eşlemesiyle**.

    `table_name`/`full_log_column` **bilinçli olarak dışarıdan** geliyor:
    backend'in varsayılanları `logs` ve `full_log`. Varsayılanla bırakılsaydı
    üretilen SQL derlenir, gözden geçirmede doğru görünür ve ancak
    çalıştırıldığında "böyle tablo yok" derdi.

    Varsayılan hedef `events` değil **`events_ocsf`** (T31)
    ------------------------------------------------------
    Eskiden bu uç ham `events` tablosuna ve HAM Sigma alan adlarına karşı SQL
    üretiyordu. O SQL hiçbir zaman koşamazdı: kural `srcip` yazıyor, tabloda
    kolon `src_ip`. Yani uç, çalıştırılamayan bir çıktıyı doğru gibi
    gösteriyordu.

    Değişiklik şimdi yapıldı çünkü ucun **hiç tüketicisi yok** — depoda
    `/v1/sigma/compile` çağıran tek satır bile aranıp bulunamadı. Dışarıdan bir
    tüketici doğduktan sonra aynı düzeltme ya pahalı olur ya hiç yapılmaz.
    """
    if target != "clickhouse":
        raise UnsupportedTarget(f"Desteklenmeyen hedef: {target!r}. Yalnızca 'clickhouse'.")

    try:
        from sigma.collection import SigmaCollection

        from .sigma_pipeline import bizigo_backend
    except Exception as exc:  # noqa: BLE001
        raise SigmaBackendUnavailable(
            f"pySigma ClickHouse backend'i yüklenemedi: {exc}"
        ) from exc

    collection = SigmaCollection.from_yaml(rule_yaml)
    backend = bizigo_backend(table=table_name, mappings_path=mappings_path)
    backend.full_log_column = full_log_column
    queries = backend.convert(collection)

    warnings: list[str] = []
    # pySigma uyarıları backend üzerinde birikiyor; sürümler arasında adı
    # değiştiği için savunmacı okuyoruz — uyarı kaybetmek, uyarı yüzünden
    # derlemeyi düşürmekten iyi.
    for attribute in ("errors", "warnings"):
        for item in getattr(backend, attribute, None) or []:
            warnings.append(str(item))

    for rule in collection.rules:
        for error in getattr(rule, "errors", None) or []:
            warnings.append(str(error))

    return CompiledRule(queries=[str(q) for q in queries], warnings=warnings)
