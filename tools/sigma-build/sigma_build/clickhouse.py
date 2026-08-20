"""ClickHouse'a tek ifadelik HTTP isteği — Kapı 2 ve Kapı 3'ün ortak yüzeyi.

Kapı 3 yazılırken `explain_gate._post`'u içe aktarmak akla ilk gelen şeydi ve
yanlış olurdu: özel bir ada dışarıdan bağlanmak, o adı değiştiren kişinin
kimseyi kırmadığını sanmasına yol açar. §9'un "ortak yüzey varsa genişlet,
kopyalama" kuralının bu depodaki hâli — kopyalamak da bağlanmak da değil,
**taşımak**.
"""

from __future__ import annotations

import urllib.error
import urllib.request

__all__ = ["post_sql"]


def post_sql(
    sql: str,
    *,
    url: str,
    user: str = "bizigo",
    password: str = "bizigo",
    database: str = "bizigo",
    timeout: float = 20.0,
) -> tuple[bool, str]:
    """`(kabul_edildi, gövde)`. HTTP hatası gövdeyle döner, bağlantı hatası **atar**.

    Ayrım önemli: ClickHouse'un reddi bir **kural** kusuru, bağlantının
    kurulamaması bir **kurulum** kusuru. İkincisini birincisi gibi raporlamak,
    ortam bozukken "bütün kurallar kırık" yazdırırdı — ölçüm aracının kendi
    sessiz yanlışı (T30'un ön kontrol protokolüyle aynı ayrım).
    """
    if not url.startswith(("http://", "https://")):
        raise ValueError(f"Beklenen http(s) adresi: {url!r}")

    request = urllib.request.Request(  # noqa: S310 — şema yukarıda doğrulandı
        url,
        data=sql.encode("utf-8"),
        headers={
            "X-ClickHouse-User": user,
            "X-ClickHouse-Key": password,
            "X-ClickHouse-Database": database,
        },
        method="POST",
    )
    try:
        with urllib.request.urlopen(request, timeout=timeout) as response:  # noqa: S310
            return True, response.read().decode("utf-8", errors="replace")
    except urllib.error.HTTPError as error:
        return False, error.read().decode("utf-8", errors="replace")
    except OSError as error:
        raise ConnectionError(f"ClickHouse'a ulaşılamadı ({url}): {error}") from error
