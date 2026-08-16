"""Miner kayıt defteri: bellek sınırları ve kalıcılık (K14)."""

import pytest

from app.masks import load_masks
from app.miners import MinerRegistry, RedisStateStore, template_id_for
from app.settings import Settings
from conftest import MASKS_PATH


class FakeRedis:
    """`get`/`set` — kalıcılığı gerçek Redis olmadan sınamak için."""

    def __init__(self) -> None:
        self.store: dict[str, bytes] = {}
        self.fail = False

    def set(self, key, value):
        if self.fail:
            raise ConnectionError("redis down")
        self.store[key] = value

    def get(self, key):
        if self.fail:
            raise ConnectionError("redis down")
        return self.store.get(key)


@pytest.fixture
def catalog():
    return load_masks(MASKS_PATH)


def _settings(**overrides) -> Settings:
    base = dict(max_clusters=5, max_miners=2, snapshot_interval_minutes=0)
    base.update(overrides)
    settings = Settings(**base)
    settings.validate()
    return settings


def test_max_clusters_sinirsiz_birakilamaz():
    with pytest.raises(ValueError, match="DRAIN_MAX_CLUSTERS"):
        Settings(max_clusters=0).validate()


def test_max_clusters_kume_sayisini_sinirliyor(catalog):
    registry = MinerRegistry(_settings(max_clusters=5), catalog, None)
    handle = registry.get("firewall")

    # Her satır farklı bir şablon: sınır olmasa 200 küme olurdu.
    for index in range(200):
        handle.miner.add_log_message(f"alpha{index} bravo{index} charlie{index} delta{index}")

    assert len(handle.miner.drain.clusters) <= 5


def test_max_miners_LRU_ile_tahliye_ediyor(catalog):
    redis = FakeRedis()
    registry = MinerRegistry(_settings(max_miners=2), catalog, redis)

    registry.get("a").miner.add_log_message("login ok from 10.0.0.1")
    registry.get("b").miner.add_log_message("login ok from 10.0.0.2")
    registry.get("c").miner.add_log_message("login ok from 10.0.0.3")

    assert registry.evicted_miners == 1
    assert set(registry.loaded_miners) == {"b", "c"}
    # Tahliye kayıp değil: durum tahliyeden önce Redis'e yazılıyor.
    assert any(key.endswith(":a") for key in redis.store)


def test_durum_redisten_geri_yukleniyor(catalog):
    redis = FakeRedis()
    settings = _settings()

    first = MinerRegistry(settings, catalog, redis)
    handle = first.get("router")
    for index in range(5):
        handle.miner.add_log_message(f"interface Gi0/{index} changed state to up")
    handle.miner.save_state("test")
    template_before = sorted(c.get_template() for c in handle.miner.drain.clusters)

    # Süreç yeniden başladı: yeni kayıt defteri, aynı Redis.
    second = MinerRegistry(settings, catalog, redis)
    restored = second.get("router")

    assert sorted(c.get_template() for c in restored.miner.drain.clusters) == template_before


def test_redis_arizasi_mining_i_durdurmuyor(catalog):
    redis = FakeRedis()
    redis.fail = True
    registry = MinerRegistry(_settings(), catalog, redis)

    outcome = registry.get("firewall").miner.add_log_message("deny tcp 10.0.0.1 -> 10.0.0.2")

    assert outcome["cluster_id"] == 1


def test_redis_yoksa_bellek_ici_calisiyor(catalog):
    registry = MinerRegistry(_settings(), catalog, None)
    assert registry.get("x").miner.add_log_message("hello world")["cluster_id"] == 1


def test_state_store_arizada_istisna_firlatmiyor():
    redis = FakeRedis()
    redis.fail = True
    store = RedisStateStore(redis, "k")

    store.save_state(b"x")
    assert store.load_state() is None
    assert store.last_error


def test_template_id_kaynak_sinifiyla_niteleniyor():
    assert template_id_for("firewall", 7) == "firewall:7"
