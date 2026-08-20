"""`view_columns` testleri — Kapı 1'in dayandığı kolon kümesi doğru mu.

İki bölüm var ve ayrı şeyler kanıtlıyorlar:

* **Davranış testleri** sentetik göçlerle çalışıyor. Ayrıştırıcının ne yaptığını
  çiviliyorlar; depo şeması değiştiğinde kırılmıyorlar.
* **Gerçek göç testleri** `db/clickhouse/` altındaki dosyaları okuyor. Tasarımın
  üstüne kurulduğu OLGULARI çiviliyorlar — en önemlisi `type_uid`'in gerçekten
  yok olduğu.

Gerçek göçlerin kolon listesi **bilerek birebir çivilenmedi.** Çıkarıcının işi
göçleri takip etmek; bir göç kolon eklerse küme kendiliğinden genişlemeli ve
bunun ayrı bir testi kırması gerekmez (T30 zaten `unmapped` alanlarının kolona
terfi edeceğini öngörüyor). Ayrıştırıcının ClickHouse'un gerçekte yaptığından
ayrıştığı hâli yakalayan bekçi ayrı: canlı `DESCRIBE` karşılaştırması, ve o
entegrasyon testinde duruyor.
"""

from __future__ import annotations

import json
import subprocess
import sys
from pathlib import Path

import pytest

from sigma_build.view_columns import (
    SNAPSHOT_PATH,
    MigrationParseError,
    load_view_definitions,
    migration_files,
    repo_root,
    snapshot_text,
    split_statements,
    view_definition,
)

MIGRATIONS = repo_root() / "db" / "clickhouse"


def write_migrations(tmp_path: Path, files: dict[str, str]) -> Path:
    directory = tmp_path / "clickhouse"
    directory.mkdir()
    for name, body in files.items():
        (directory / name).write_text(body, encoding="utf-8")
    return directory


# --------------------------------------------------------------------------- #
# İfade ayırıcı — `SqlStatementSplitter.cs` ile aynı fikirde olduğu hâller
# --------------------------------------------------------------------------- #

def test_noktali_virgul_yorumun_icinde_ayrac_degil():
    assert split_statements("SELECT 1 -- burada ; var\nFROM t;") == ["SELECT 1 \nFROM t"]


def test_noktali_virgul_metnin_icinde_ayrac_degil():
    statements = split_statements("SELECT 'a;b' AS x FROM t;")
    assert statements == ["SELECT 'a;b' AS x FROM t"]


def test_kacirilmis_tirnak_metni_bitirmiyor():
    statements = split_statements("SELECT 'a''b;c' AS x FROM t;")
    assert statements == ["SELECT 'a''b;c' AS x FROM t"]


def test_blok_yorumu_dusuyor():
    assert split_statements("SELECT /* ; yorum ; */ 1 FROM t;") == ["SELECT  1 FROM t"]


def test_son_ifade_noktali_virgulsuz_de_okunuyor():
    assert split_statements("SELECT 1 FROM t") == ["SELECT 1 FROM t"]


# --------------------------------------------------------------------------- #
# Göç sırası
# --------------------------------------------------------------------------- #

def test_son_tanim_kazaniyor(tmp_path):
    views = load_view_definitions(
        write_migrations(
            tmp_path,
            {
                "0001_ilk.sql": "CREATE VIEW v AS SELECT a AS eski FROM t;",
                "0002_ikinci.sql": "DROP VIEW IF EXISTS v;\nCREATE VIEW v AS SELECT a AS yeni FROM t;",
            },
        )
    )
    assert views["v"].columns == ("yeni",)
    assert views["v"].source_file == "0002_ikinci.sql"


def test_if_not_exists_var_olan_gorunumu_yeniden_tanimlamiyor(tmp_path):
    """ClickHouse bu ifadeyi atlar; biz de atlamalıyız.

    Atlamayan bir model, canlıda olmayan bir kolon kümesi üretir ve Kapı 1
    "kolon var" diyerek koşmayan SQL'i geçirir — sessiz taraf.
    """
    views = load_view_definitions(
        write_migrations(
            tmp_path,
            {
                "0001_ilk.sql": "CREATE VIEW IF NOT EXISTS v AS SELECT a AS ilk FROM t;",
                "0002_ikinci.sql": "CREATE VIEW IF NOT EXISTS v AS SELECT b AS ikinci FROM t;",
            },
        )
    )
    assert views["v"].columns == ("ilk",)
    assert views["v"].source_file == "0001_ilk.sql"


def test_drop_sonrasi_if_not_exists_yeniden_tanimliyor(tmp_path):
    views = load_view_definitions(
        write_migrations(
            tmp_path,
            {
                "0001_ilk.sql": "CREATE VIEW IF NOT EXISTS v AS SELECT a AS ilk FROM t;",
                "0002_ikinci.sql": "DROP VIEW IF EXISTS v;\nCREATE VIEW IF NOT EXISTS v AS SELECT b AS ikinci FROM t;",
            },
        )
    )
    assert views["v"].columns == ("ikinci",)


def test_drop_gorunumu_kaldiriyor(tmp_path):
    views = load_view_definitions(
        write_migrations(
            tmp_path,
            {
                "0001_ilk.sql": "CREATE VIEW v AS SELECT a AS x FROM t;",
                "0002_ikinci.sql": "DROP VIEW IF EXISTS v;",
            },
        )
    )
    assert "v" not in views


def test_or_replace_yeniden_tanimliyor(tmp_path):
    views = load_view_definitions(
        write_migrations(
            tmp_path,
            {
                "0001_ilk.sql": "CREATE VIEW v AS SELECT a AS eski FROM t;",
                "0002_ikinci.sql": "CREATE OR REPLACE VIEW v AS SELECT b AS yeni FROM t;",
            },
        )
    )
    assert views["v"].columns == ("yeni",)


def test_dosyalar_migratorun_sirasiyla_okunuyor(tmp_path):
    """`ClickHouseMigrator.cs`: `OrderBy(f => f, StringComparer.Ordinal)`."""
    directory = write_migrations(
        tmp_path,
        {
            "0002_b.sql": "CREATE VIEW v AS SELECT b AS ikinci FROM t;",
            "0010_c.sql": "CREATE OR REPLACE VIEW v AS SELECT c AS onuncu FROM t;",
            "0001_a.sql": "CREATE VIEW v AS SELECT a AS birinci FROM t;",
        },
    )
    assert [p.name for p in migration_files(directory)] == ["0001_a.sql", "0002_b.sql", "0010_c.sql"]
    # Sıfır dolgu sayesinde ordinal sıra sayısal sırayla aynı: 0010 en sonda.
    assert load_view_definitions(directory)["v"].columns == ("onuncu",)


def test_ad_kuralina_uymayan_dosya_reddediliyor(tmp_path):
    """Dolgusuz bir ad ordinal sırayı sayısal sıradan ayırır — sessizce yanlış sıra."""
    directory = write_migrations(
        tmp_path,
        {"0001_ilk.sql": "CREATE VIEW v AS SELECT a AS x FROM t;", "10_gec.sql": "SELECT 1;"},
    )
    with pytest.raises(MigrationParseError, match="Ad kuralına uymayan"):
        load_view_definitions(directory)


# --------------------------------------------------------------------------- #
# Kolon adı çıkarımı
# --------------------------------------------------------------------------- #

@pytest.mark.parametrize(
    ("select_list", "expected"),
    [
        ("a AS x", ("x",)),
        ("a", ("a",)),
        ("t.a", ("a",)),
        ('a AS "host.name"', ("host.name",)),
        ("a AS `garip ad`", ("garip ad",)),
        ("a AS x, b AS y", ("x", "y")),
        ("a as x", ("x",)),
        ("CAST(a AS String) AS x", ("x",)),
        ("toString(a) AS x", ("x",)),
    ],
)
def test_kolon_adlari(tmp_path, select_list, expected):
    directory = write_migrations(tmp_path, {"0001_v.sql": f"CREATE VIEW v AS SELECT {select_list} FROM t;"})
    assert load_view_definitions(directory)["v"].columns == expected


def test_fonksiyon_icindeki_virgul_kolon_bolmuyor(tmp_path):
    """`0004` gerçekten `multiIf(...)` içeriyor — bu varsayım değil, ölçülmüş hâl."""
    directory = write_migrations(
        tmp_path,
        {
            "0001_v.sql": (
                "CREATE VIEW v AS SELECT\n"
                "    multiIf(s = 1, 9, s = 2, 13, 0) AS SeverityNumber,\n"
                "    s AS ham\n"
                "FROM t;"
            )
        },
    )
    assert load_view_definitions(directory)["v"].columns == ("SeverityNumber", "ham")


def test_fonksiyon_icindeki_from_kolon_listesini_bitirmiyor(tmp_path):
    directory = write_migrations(
        tmp_path,
        {"0001_v.sql": "CREATE VIEW v AS SELECT EXTRACT(YEAR FROM ts) AS yil, a AS b FROM t;"},
    )
    assert load_view_definitions(directory)["v"].columns == ("yil", "b")


def test_tirnakli_ad_icindeki_virgul_bolmuyor(tmp_path):
    directory = write_migrations(tmp_path, {"0001_v.sql": 'CREATE VIEW v AS SELECT a AS "x,y" FROM t;'})
    assert load_view_definitions(directory)["v"].columns == ("x,y",)


def test_yorumlar_kolon_sayilmiyor(tmp_path):
    directory = write_migrations(
        tmp_path,
        {
            "0001_v.sql": (
                "CREATE VIEW v AS SELECT\n"
                "    -- bu bir yorum, kolon değil\n"
                "    a AS x,  -- satır sonu yorumu\n"
                "    b AS y\n"
                "FROM t;"
            )
        },
    )
    assert load_view_definitions(directory)["v"].columns == ("x", "y")


# --------------------------------------------------------------------------- #
# Reddedilenler — kapının kırmızı yanabildiği yerler
# --------------------------------------------------------------------------- #

def test_adsiz_ifade_reddediliyor(tmp_path):
    """Adsız kolona Sigma kuralı vuramaz; sessizce atlamak "kolon yok" derdi."""
    directory = write_migrations(tmp_path, {"0001_v.sql": "CREATE VIEW v AS SELECT count() FROM t;"})
    with pytest.raises(MigrationParseError, match="adsız ifade"):
        load_view_definitions(directory)


def test_yildiz_reddediliyor(tmp_path):
    directory = write_migrations(tmp_path, {"0001_v.sql": "CREATE VIEW v AS SELECT * FROM t;"})
    with pytest.raises(MigrationParseError, match=r"`\*`"):
        load_view_definitions(directory)


def test_tekrarlanan_kolon_reddediliyor(tmp_path):
    directory = write_migrations(tmp_path, {"0001_v.sql": "CREATE VIEW v AS SELECT a AS x, b AS x FROM t;"})
    with pytest.raises(MigrationParseError, match="Tekrarlanan|tekrarlanan"):
        load_view_definitions(directory)


def test_dengesiz_parantez_reddediliyor(tmp_path):
    directory = write_migrations(tmp_path, {"0001_v.sql": "CREATE VIEW v AS SELECT f(a AS x FROM t;"})
    with pytest.raises(MigrationParseError):
        load_view_definitions(directory)


def test_olmayan_gorunum_sorulunca_hata(tmp_path):
    directory = write_migrations(tmp_path, {"0001_v.sql": "CREATE VIEW v AS SELECT a AS x FROM t;"})
    with pytest.raises(MigrationParseError, match="yok"):
        view_definition("baska", directory)


def test_bos_dizin_reddediliyor(tmp_path):
    directory = tmp_path / "bos"
    directory.mkdir()
    with pytest.raises(MigrationParseError, match="göçü yok"):
        load_view_definitions(directory)


# --------------------------------------------------------------------------- #
# Gerçek göçler — tasarımın dayandığı olgular
# --------------------------------------------------------------------------- #

def test_events_ocsf_turetilebiliyor():
    definition = view_definition("events_ocsf", MIGRATIONS)
    assert definition.source_file == "0003_ocsf_otel_views.sql"
    # Kapı 1'in her gün kullanacağı adlar.
    assert {"src_endpoint_ip", "dst_endpoint_port", "unmapped", "raw_data", "class_uid"} <= definition.column_set


def test_events_ocsf_type_uid_icermiyor():
    """T30'un beşinci tuzağı ve T31'in "zincire koyma" kararının dayanağı.

    `ocsf_pipeline` sınıf ayırıcısını `type_uid` üzerinden ekliyor; K8 gereği
    kolona yazılan tek OCSF alanı `class_uid` + `activity_id`. Bu olgu bir
    belgede yazılı olmakla kalmamalı — burada ölçülüyor.
    """
    columns = view_definition("events_ocsf", MIGRATIONS).column_set
    assert "type_uid" not in columns
    assert {"class_uid", "activity_id"} <= columns


def test_events_otel_son_tanimi_0004ten_geliyor():
    """Sıranın gerçek göçlerde de işlediğinin kanıtı.

    `0003` `events_otel`'i tanımlıyor, `0004` DROP edip yeniden yaratıyor.
    Yalnızca `0003`'ü okuyan bir çıkarıcı burada yanlış cevap verir.
    """
    definition = view_definition("events_otel", MIGRATIONS)
    assert definition.source_file == "0004_fix_otel_severity_scale.sql"
    # `0004`'ün eklediği, `0003`'te olmayan kolon.
    assert "bizigo.ocsf_severity_id" in definition.column_set


def test_events_otel_multiif_tek_kolon_olarak_okunuyor():
    """Naif virgül bölmesi buradan yedi sahte kolon üretirdi."""
    columns = view_definition("events_otel", MIGRATIONS).columns
    assert columns.count("SeverityNumber") == 1
    assert not any(column.isdigit() for column in columns)
    assert "host.name" in columns  # tırnaklı ad tırnaksız okunuyor


def test_gercek_gocler_hicbir_ayristirma_hatasi_vermiyor():
    """Depodaki bütün göçler bu modülün modelleyebildiği biçimde."""
    views = load_view_definitions(MIGRATIONS)
    assert set(views) == {"events_ocsf", "events_otel"}


# --------------------------------------------------------------------------- #
# Anlık görüntü ve sürüklenme kapısı
# --------------------------------------------------------------------------- #

def test_depodaki_anlik_goruntu_gocletle_ayni():
    """`--check`'in birim testi karşılığı. Ayrışırsa burada kırmızı yanıyor."""
    committed = (repo_root() / SNAPSHOT_PATH).read_text(encoding="utf-8")
    assert committed == snapshot_text()


def test_anlik_goruntude_tarih_yok():
    """Tarih olsaydı her koşum farklı bayt üretir ve kapı birebir karşılaştıramazdı."""
    document = json.loads(snapshot_text())
    assert set(document) == {"_comment", "views"}
    for view in document["views"].values():
        assert set(view) == {"source_file", "columns"}


def test_anlik_goruntu_goc_degisince_degisiyor(tmp_path):
    """Kapının boş bir söz vermediğinin kanıtı: içerik gerçekten göçü izliyor."""
    directory = write_migrations(tmp_path, {"0001_v.sql": "CREATE VIEW v AS SELECT a AS x FROM t;"})
    once = snapshot_text(directory)
    (directory / "0002_v.sql").write_text(
        "DROP VIEW IF EXISTS v;\nCREATE VIEW v AS SELECT a AS x, b AS y FROM t;", encoding="utf-8"
    )
    assert snapshot_text(directory) != once
    assert "y" in json.loads(snapshot_text(directory))["views"]["v"]["columns"]


def test_cli_json_basiyor():
    result = subprocess.run(
        [sys.executable, "-m", "sigma_build.view_columns", "--view", "events_ocsf", "--json"],
        cwd=repo_root() / "tools" / "sigma-build",
        capture_output=True,
        text=True,
        check=True,
    )
    assert '"events_ocsf"' in result.stdout
    assert "src_endpoint_ip" in result.stdout
