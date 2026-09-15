#!/usr/bin/env python3
"""M20'nin kırmızı ölçümü (§6) — etiketleme, kapsam süzgeci, devralma bedeli.

Koşum yordamı ortak (`kirmizi_olcumu.py`); bu dosyanın taşıdığı tek şey KUSUR
LİSTESİ.

İKİ KALEM `yesil_kalmali` TAŞIYOR ve ikisi de bir iddianın ikinci yarısı:
kapsam süzgecini kırmak etiketleme bekçisini KIRMIYOR, ve etiketi kaldırmak
kapsam bekçisini kırmıyor. Yani iki bekçi birbirinin kopyası değil — M20'nin iki
sınırı gerçekten iki ayrı şey.
"""

from __future__ import annotations

import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

from kirmizi_olcumu import Kusur, olc  # noqa: E402

MCP = "src/Bizigo.Mcp"
PROD = "src/Bizigo.Mcp.Product"

KUSURLAR = [
    Kusur(
        ad="1 · Bildirim etiketsiz gönderiliyor",
        dosya=f"{MCP}/McpResourceUpdates.cs",
        bul="                        Meta = new JsonObject\n"
            "                        {\n"
            "                            [MetaKeys.SubscriptionId] = subscriptionId,\n"
            "                        },",
        koy="                        // KUSUR: etiket yok",
        kirmizi_bekleniyor=[
            "Bildirim_abonelik_kimligiyle_etiketli",
            "Iki_abonelik_etiketle_ayirt_edilebiliyor",
        ],
        yesil_kalmali=["Kapsam_disi_grubun_degisikligi_aboneye_gitmiyor"],
    ),
    Kusur(
        ad="2 · Kapsam süzgeci kaldırılıyor (yan kanal geri açılıyor)",
        dosya=f"{MCP}/McpSubscriptionRegistry.cs",
        bul="                    .Where(entry => entry.Value.Uris.Contains(uri, StringComparer.Ordinal)\n"
            "                        && Delivers(entry.Value.Scope, ownerGroup))",
        koy="                    .Where(entry => entry.Value.Uris.Contains(uri, StringComparer.Ordinal)) // KUSUR",
        kirmizi_bekleniyor=["Kapsam_disi_grubun_degisikligi_aboneye_gitmiyor"],
        yesil_kalmali=["Bildirim_abonelik_kimligiyle_etiketli"],
    ),
    Kusur(
        ad="3 · Grubu bilinmeyen değişiklik gönderiliyor",
        dosya=f"{MCP}/McpSubscriptionRegistry.cs",
        bul="        if (string.IsNullOrWhiteSpace(ownerGroup))\n        {\n            return false;\n        }",
        koy="        if (string.IsNullOrWhiteSpace(ownerGroup))\n        {\n            return true; // KUSUR\n        }",
        kirmizi_bekleniyor=["Grubu_bilinmeyen_degisiklik_gonderilmiyor"],
    ),
    Kusur(
        ad="4 · Adres süzgeci kaldırılıyor (istenmeyen adrese bildirim)",
        dosya=f"{MCP}/McpSubscriptionRegistry.cs",
        bul="                    .Where(entry => entry.Value.Uris.Contains(uri, StringComparer.Ordinal)\n"
            "                        && Delivers(entry.Value.Scope, ownerGroup))",
        koy="                    .Where(entry => Delivers(entry.Value.Scope, ownerGroup)) // KUSUR",
        kirmizi_bekleniyor=["Iki_abonelik_etiketle_ayirt_edilebiliyor"],
    ),
    Kusur(
        ad="5 · Abonelik kaydı akış kapanınca silinmiyor (sızıntı)",
        dosya=f"{MCP}/McpSubscriptionRegistry.cs",
        bul="                owner._abonelikler.Remove(key);",
        koy="                _ = owner._abonelikler.ContainsKey(key); // KUSUR: silme yok",
        kirmizi_bekleniyor=["Akis_kapaninca_abonelik_defterden_dusuyor"],
    ),
    Kusur(
        ad="6 · Filtre kurulmuyor (abonelik deftere hiç girmiyor)",
        dosya=f"{MCP}/BizigoMcpServer.cs",
        bul="            options.Filters.Message.IncomingFilters.Add(registry.Filter());",
        koy="            _ = registry; // KUSUR: filtre kurulmuyor",
        kirmizi_bekleniyor=["Abonelik_onaylaniyor_ve_bildirim_ulasiyor"],
    ),
    Kusur(
        ad="7 · Koşum başlangıcı duyurulmuyor (dördüncü yayın noktası)",
        dosya="src/Bizigo.Rca/RcaAdmission.cs",
        bul="        // M07 · YAYIN NOKTASI 4/4 — koşum BAŞLADI (`Running`).",
        koy="        // KUSUR: dördüncü nokta kaldırıldı\n        /*",
        derleme_kirilmali=True,
    ),
    Kusur(
        ad="8 · Yan kanal ölçümünün sayısı bozuluyor",
        dosya=f"{PROD}/Resources/McpRcaRunChangeListener.cs",
        bul="        return updates.PublishAsync(RcaRunsResource.Uri, change.OwnerGroup, cancellationToken);",
        koy="        return updates.PublishAsync(RcaRunsResource.Uri, \"network/core\", cancellationToken); // KUSUR",
        kirmizi_bekleniyor=["Kopru_degisikligin_grubunu_tasiyor"],
    ),
]


if __name__ == "__main__":
    sys.exit(olc(KUSURLAR, "m20-abonelik-kimligi"))
