"""HTTP sözleşmesi (F1 §9). .NET istemcisi tam olarak bu alanları okuyor."""

import pytest
from fastapi.testclient import TestClient

from app.main import app

RULE = """
title: Basarisiz oturum acma
id: 2f1d0c8a-6c1f-4b2c-9c9d-0a1b2c3d4e5f
status: test
logsource:
    product: linux
detection:
    selection:
        action: 'denied'
        user_name: 'admin'
    condition: selection
"""


@pytest.fixture(scope="module")
def client():
    with TestClient(app) as test_client:
        yield test_client


def test_healthz(client):
    response = client.get("/healthz")

    assert response.status_code == 200
    assert response.headers["X-Bizigo-Sidecar-Api"] == "v1"

    body = response.json()
    assert body["api_version"] == "v1"
    assert body["masks_version"] >= 1
    assert "IPV4" in body["mask_names"]


def test_readyz_redis_yokken_degraded(client):
    body = client.get("/readyz").json()

    # Redis yok ama servis ayakta: unutkan bir sidecar, olmayan sidecar'dan iyi.
    assert body["status"] == "degraded"
    assert body["redis"] == "down"
    assert body["max_clusters"] > 0


def test_mine_batch_ogreniyor(client):
    payload = {
        "source_key": "test-batch",
        "messages": [
            {"id": "1", "text": "Failed password for admin from 10.1.2.3 port 51234 ssh2"},
            {"id": "2", "text": "Failed password for root from 10.1.2.9 port 40001 ssh2"},
        ],
    }

    body = client.post("/v1/mine/batch", json=payload).json()

    assert body["api_version"] == "v1"
    assert [r["id"] for r in body["results"]] == ["1", "2"]

    first, second = body["results"]
    assert first["is_new"] is True
    # İkinci satır aynı şablona düşmeli — maskeleme sayıları ve IP'yi yuttu.
    assert second["template_id"] == first["template_id"]
    assert first["template_id"].startswith("test-batch:")
    assert "<IPV4>" in first["template"]
    assert body["cluster_count"] == 1


def test_mine_batch_masked_alani_yerel_imzayla_ayni(client):
    """`masked`, .NET'in yerel olarak hesapladığı imzanın aynısı olmalı."""
    text = "job 6f9619ff-8b86-d011-b42d-00cf4fc964ff wrote /var/log/bizigo/ingest.log (0x1FE bytes)"

    body = client.post(
        "/v1/mine/batch",
        json={"source_key": "test-masked", "messages": [{"id": "1", "text": text}]},
    ).json()

    assert body["results"][0]["masked"] == (
        "job <UUID> wrote <UNIXPATH> (<BASE16NUM> bytes)"
    )


def test_mine_batch_parametreleri_maske_adiyla_donuyor(client):
    body = client.post(
        "/v1/mine/batch",
        json={
            "source_key": "test-params",
            "messages": [{"id": "1", "text": "deny from 10.0.0.7 to 10.0.0.9"}],
        },
    ).json()

    masks = [p["mask"] for p in body["results"][0]["params"]]
    assert masks == ["IPV4", "IPV4"]


def test_mine_match_ogrenmiyor(client):
    client.post(
        "/v1/mine/batch",
        json={
            "source_key": "test-match",
            "messages": [{"id": "1", "text": "session opened for user 1001"}],
        },
    )

    before = client.get("/v1/clusters/test-match").json()["cluster_count"]

    body = client.post(
        "/v1/mine/match",
        json={
            "source_key": "test-match",
            "messages": [
                {"id": "a", "text": "session opened for user 2002"},
                {"id": "b", "text": "tamamen alakasiz bir satir burada duruyor"},
            ],
        },
    ).json()

    assert body["results"][0]["template_id"] is not None
    assert body["results"][1]["template_id"] is None
    # Eşleşmeyen satır yeni küme YARATMAMALI.
    assert client.get("/v1/clusters/test-match").json()["cluster_count"] == before


def test_clusters_ucu_maske_adlarini_veriyor(client):
    client.post(
        "/v1/mine/batch",
        json={
            "source_key": "test-clusters",
            "messages": [{"id": "1", "text": "accepted 10.0.0.1 port 22"}],
        },
    )

    body = client.get("/v1/clusters/test-clusters").json()

    assert body["cluster_count"] == 1
    entry = body["clusters"][0]
    assert entry["template_id"] == f"test-clusters:{entry['cluster_id']}"
    assert set(entry["mask_names"]) >= {"IPV4", "NUMBER"}


def test_bos_batch_reddediliyor(client):
    assert client.post("/v1/mine/batch", json={"source_key": "x", "messages": []}).status_code == 400


def test_batch_siniri_asilinca_413(client):
    payload = {
        "source_key": "x",
        "messages": [{"id": str(i), "text": "x"} for i in range(1001)],
    }

    assert client.post("/v1/mine/batch", json=payload).status_code == 413


def test_sigma_compile_clickhouse_sql_uretiyor(client):
    response = client.post(
        "/v1/sigma/compile", json={"rule_yaml": RULE, "target": "clickhouse"}
    )

    if response.status_code == 503:
        pytest.skip("pySigma ClickHouse backend'i kurulu değil")

    assert response.status_code == 200
    body = response.json()
    assert body["queries"], "en az bir sorgu bekleniyor"
    assert "admin" in body["sql"]

    # Hedef `events` DEĞİL `events_ocsf` (T31).
    #
    # Bu test eskiden `events` bekliyordu ve geçiyordu — ama geçmesi bir şey
    # ifade etmiyordu: üretilen SQL ham Sigma alan adlarına vuruyordu
    # (`user_name`, `action`) ve `events` tablosunda o adlar YOK. Yani uç,
    # hiçbir zaman koşamayacak bir sorguyu doğru gibi gösteriyordu ve test de
    # onu çiviliyordu.
    #
    # Artık Bizigo pipeline'ı bağlı: adlar `events_ocsf` görünümünün
    # kolonlarına eşleniyor ve üretilen SQL gerçekten koşabiliyor. Varsayılanı
    # değiştirmek şu an bedavaydı — ucun hiç tüketicisi yok.
    assert body["table"] == "events_ocsf"
    assert "FROM events_ocsf" in body["sql"]

    # Eşlemenin uygulandığının kanıtı: ham ad gitti, kolon adı geldi.
    assert "actor_user_name" in body["sql"], f"eşleme uygulanmamış: {body['sql']}"
    assert "activity_name" in body["sql"]


def test_sigma_compile_desteklenmeyen_hedef(client):
    response = client.post("/v1/sigma/compile", json={"rule_yaml": RULE, "target": "splunk"})

    # Literal["clickhouse"] doğrulaması pydantic'te; 422 bekleniyor.
    assert response.status_code == 422


def test_sigma_compile_bozuk_kural(client):
    response = client.post(
        "/v1/sigma/compile", json={"rule_yaml": "bu: gecerli bir sigma kurali degil"}
    )

    if response.status_code == 503:
        pytest.skip("pySigma ClickHouse backend'i kurulu değil")

    assert response.status_code == 422
