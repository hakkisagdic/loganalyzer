"""Kaynak sınıfı başına Drain3 miner'ı + Redis kalıcılığı (K14).

İki ayrı sınır var ve ikisi de bilinçli:

* **`max_clusters`** — bir miner'ın içindeki küme sayısı. Drain3 bunu LRU ile
  uygular. Sınırsız bırakılırsa ağ logları (her satırda değişen bir alan)
  bellek sızıntısı gibi davranıyor.
* **`max_miners`** — kaç kaynak sınıfının aynı anda bellekte tutulacağı.
  Drain3'te böyle bir sınır yok; kaynak sınıfı sayısı envanterle birlikte
  büyüdüğü için bunu biz koyuyoruz. Tahliye edilen miner'ın durumu Redis'te
  kalıyor, tekrar istenirse oradan geri yükleniyor — kayıp yok, yalnızca
  bir yeniden yükleme maliyeti.
"""

from __future__ import annotations

import logging
import threading
from collections import OrderedDict
from dataclasses import dataclass
from typing import Optional

import redis
from drain3 import TemplateMiner
from drain3.persistence_handler import PersistenceHandler
from drain3.template_miner_config import TemplateMinerConfig

from .masks import MaskCatalog
from .settings import Settings

logger = logging.getLogger(__name__)


class RedisStateStore(PersistenceHandler):
    """Drain3'ün `RedisPersistence`'ının URL alan ve **arızada ölmeyen** hâli.

    Upstream sürüm host/port/db/password'ü ayrı ayrı istiyor ve hata yakalamıyor:
    Redis düşerse `save_state` istisna fırlatıp `add_log_message`'ı da düşürüyor.
    Bizde kalıcılık bir konfor; kaybı mining'i durdurmamalı.
    """

    def __init__(self, client: Optional[redis.Redis], key: str) -> None:
        self.client = client
        self.key = key
        self.last_error: Optional[str] = None

    def save_state(self, state: bytes) -> None:
        if self.client is None:
            return
        try:
            self.client.set(self.key, state)
            self.last_error = None
        except Exception as exc:  # noqa: BLE001 — kalıcılık arızası mining'i durdurmaz
            self.last_error = str(exc)
            logger.warning("Redis'e durum yazılamadı (%s): %s", self.key, exc)

    def load_state(self) -> Optional[bytes]:
        if self.client is None:
            return None
        try:
            value = self.client.get(self.key)
            self.last_error = None
            return value
        except Exception as exc:  # noqa: BLE001
            self.last_error = str(exc)
            logger.warning("Redis'ten durum okunamadı (%s): %s", self.key, exc)
            return None


@dataclass
class MinerHandle:
    source_key: str
    miner: TemplateMiner
    lock: threading.Lock


class MinerRegistry:
    """Kaynak sınıfı → miner. Süreç içinde tek örnek."""

    def __init__(
        self,
        settings: Settings,
        catalog: MaskCatalog,
        client: Optional[redis.Redis],
    ) -> None:
        self._settings = settings
        self._catalog = catalog
        self._client = client
        self._miners: OrderedDict[str, MinerHandle] = OrderedDict()
        self._lock = threading.Lock()
        self.evicted_miners = 0

    @property
    def loaded_miners(self) -> list[str]:
        with self._lock:
            return list(self._miners.keys())

    def _build_config(self) -> TemplateMinerConfig:
        config = TemplateMinerConfig()
        config.drain_sim_th = self._settings.sim_th
        config.drain_depth = self._settings.depth
        config.drain_max_children = self._settings.max_children
        config.drain_max_clusters = self._settings.max_clusters
        config.masking_instructions = self._catalog.instructions()
        config.mask_prefix = self._catalog.mask_prefix
        config.mask_suffix = self._catalog.mask_suffix
        config.snapshot_interval_minutes = self._settings.snapshot_interval_minutes
        # Maskeleme sözlüğü sayıları zaten `<NUMBER>` yapıyor. Drain'in kendi
        # sayısal token parametrelemesi açık kalırsa `<*>` üretiyor ve mask adı
        # kayboluyor — grok taslağına dönüşecek bilgi tam olarak o ad.
        config.parametrize_numeric_tokens = False
        return config

    def get(self, source_key: str) -> MinerHandle:
        with self._lock:
            handle = self._miners.get(source_key)
            if handle is not None:
                self._miners.move_to_end(source_key)
                return handle

            # Redis yoksa persistence handler'ı **hiç bağlamıyoruz**.
            #
            # Drain3, handler `None` değilse her yeni kümede `save_state`
            # çağırıyor ve `save_state` tüm ağacı `jsonpickle` ile
            # seri hâle getiriyor. Handler'ı "yazmayan" bir nesneye bağlamak
            # yetmiyor: maliyet yazmada değil, serileştirmede. Canlı ölçümde
            # bunun bedeli görüldü — Redis'siz bir sidecar binlerce yeni şablon
            # gördüğünde her satırda büyüyen ağacı yeniden serileştirdiği için
            # kilitleniyordu. Kalıcılık yoksa serileştirme de yapılmamalı.
            store = (
                RedisStateStore(self._client, f"{self._settings.redis_key_prefix}{source_key}")
                if self._client is not None
                else None
            )
            miner = TemplateMiner(persistence_handler=store, config=self._build_config())
            handle = MinerHandle(source_key=source_key, miner=miner, lock=threading.Lock())
            self._miners[source_key] = handle

            while len(self._miners) > self._settings.max_miners:
                evicted_key, evicted = self._miners.popitem(last=False)
                self.evicted_miners += 1
                logger.info("Miner tahliye edildi (max_miners): %s", evicted_key)
                # Tahliyeden önce son durumu yaz; yoksa son snapshot'tan bu yana
                # öğrenilenler kaybolur.
                self._save(evicted, "eviction")

            return handle

    def save_all(self) -> None:
        """Kapanışta çağrılır — son snapshot'tan sonrası kaybolmasın."""
        for handle in list(self._miners.values()):
            self._save(handle, "shutdown")

    def _save(self, handle: MinerHandle, reason: str) -> None:
        # Kalıcılık bağlı değilse yazacak bir yer de yok. Drain3'ün `save_state`'i
        # bu durumda assert ile düşüyor; kontrolü burada yapmak, her tahliyede
        # yakalanmış bir istisnayı loglamaktan temiz.
        if handle.miner.persistence_handler is None:
            return

        try:
            with handle.lock:
                handle.miner.save_state(reason)
        except Exception as exc:  # noqa: BLE001
            logger.warning("Miner durumu yazılamadı (%s, %s): %s", handle.source_key, reason, exc)


def template_id_for(source_key: str, cluster_id: int) -> str:
    """`template_id` = kaynak sınıfı + küme kimliği.

    Küme kimlikleri miner içinde artan ve **yeniden kullanılmıyor** (LRU
    tahliyesinde bile sayaç geri gitmiyor), Redis'ten geri yüklendiğinde de
    korunuyor. Şablon metnini hash'lemek cazip görünüyor ama küme genelleştikçe
    metin değişiyor ve kimlik kayıyor — F3'ün "ilk görülen imza" korelasyonu
    tam olarak bunu kaldıramaz.
    """
    return f"{source_key}:{cluster_id}"
