#!/usr/bin/env python3
"""M07'nin ABONELİK yarısının kırmızı ölçümü (§6).

Koşum yordamı ortak (`kirmizi_olcumu.py`) — bu dosyanın taşıdığı tek şey KUSUR
LİSTESİ. M07'nin birinci turu kendi yordamını yazmıştı ve o kopya bu turda
kaldırıldı: yordam M11'de ortaklaştı ve ikinci kopya tutmak §9'un yasakladığı
şey.

Birinci turda ölçüm aracının kendisinde üç kusur bulunmuştu; ikisi ortak
yordamda zaten kapalı (yedeği diske yazmak, iddiayı dosyanın tamamıyla
karşılaştırmak), üçüncüsü — iki `--filter` argümanının `dotnet test`'i kullanım
yardımına düşürmesi — ortak yordamın `test_kos`'unda tek filtre kullanıldığı için
doğmuyor.
"""

from __future__ import annotations

import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

from kirmizi_olcumu import Kusur, olc  # noqa: E402

MCP = "src/Bizigo.Mcp"
PROD = "src/Bizigo.Mcp.Product"
RCA = "src/Bizigo.Rca"

KUSURLAR = [
    Kusur(
        ad="1 · Abonelik bütün kaynaklarda açılıyor",
        dosya=f"{PROD}/Resources/ParserDefinitionResource.cs",
        bul="    public override bool ReadsScopedData => false;",
        koy="    public override bool ReadsScopedData => false;\n\n"
            "    /// <summary>KUSUR</summary>\n"
            "    public override bool SupportsSubscription => true;",
        kirmizi_bekleniyor=["Abonelik_destekleyen_tek_kaynak_var"],
    ),
    Kusur(
        ad="2 · Yetenek taşımanın gönderebilmesinden koparılıyor",
        dosya=f"{MCP}/BizigoMcpServer.cs",
        bul="            Subscribe = subscriptionsDeliverable\n"
            "                && resources.Any(static resource => resource.SupportsSubscription),",
        koy="            Subscribe = resources.Any(static resource => resource.SupportsSubscription), // KUSUR",
        kirmizi_bekleniyor=[
            "Yetenek_tasimanin_gonderebilmesine_bagli",
            "Desteklenmeyen_yetenek_ilan_edilmiyor",
        ],
    ),
    Kusur(
        ad="3 · Yayın kanalı defterden çıkarmıyor (sızıntı)",
        dosya=f"{MCP}/McpResourceUpdates.cs",
        bul="                owner._servers.Remove(server);",
        koy="                _ = owner._servers.Contains(server); // KUSUR: çıkarma yok",
        kirmizi_bekleniyor=["Kayit_cikarilinca_defter_bosaliyor"],
    ),
    Kusur(
        ad="4 · Kabul edilen koşum duyurulmuyor",
        dosya=f"{RCA}/RcaAdmission.cs",
        bul="        await DuyurAsync(run, cancellationToken).ConfigureAwait(false);\n\n"
            "        return new RcaAdmissionResult(run, Existing: false);",
        koy="        return new RcaAdmissionResult(run, Existing: false); // KUSUR",
        kirmizi_bekleniyor=["Kosum_basina_uc_bildirim"],
    ),
    Kusur(
        ad="5 · Dinleyicinin hatası koşumu düşürüyor",
        dosya=f"{RCA}/RcaAdmission.cs",
        bul="            catch (Exception error) when (error is not OperationCanceledException)\n"
            "            {\n"
            "                logger.LogWarning(",
        koy="            catch (Exception error) when (error is OperationCanceledException) // KUSUR\n"
            "            {\n"
            "                logger.LogWarning(",
        kirmizi_bekleniyor=["Dinleyicinin_hatasi_kosumu_dusurmuyor"],
    ),
    Kusur(
        ad="6 · Bildirim kaydın ÖNCESİNDE gönderiliyor",
        dosya=f"{RCA}/RcaAdmission.cs",
        bul="        run.State = state;\n"
            "        run.FinishedAt = _time.GetUtcNow();\n"
            "        run.StateDetail = detail.Length <= 512 ? detail : detail[..512];",
        koy="        run.State = state;\n"
            "        run.FinishedAt = _time.GetUtcNow();\n"
            "        run.StateDetail = detail.Length <= 512 ? detail : detail[..512];\n\n"
            "        // KUSUR: kayıttan ÖNCE duyuru\n"
            "        await DuyurAsync(run, cancellationToken).ConfigureAwait(false);",
        kirmizi_bekleniyor=["Kosum_basina_uc_bildirim"],
    ),
    Kusur(
        ad="7 · Kaynak gövdesi redaksiyon kapısını atlıyor",
        dosya=f"{PROD}/Resources/RcaRunsResource.cs",
        bul="            RedactedPrompt.Redact(result.Payload.GetRawText()),",
        koy="            RedactedPrompt.Redact(string.Empty), // KUSUR",
        kirmizi_bekleniyor=["Kosum_listesi_govdesi_kapidan_geciyor"],
    ),
    Kusur(
        ad="8 · Dördüncü belge türü sessizce kayboluyor",
        dosya=f"{PROD}/Resources/RcaRunsResource.cs",
        bul='public const string ResourceKind = "rca-runs";',
        koy='public const string ResourceKind = "rca-runs-v2"; // KUSUR',
        kirmizi_bekleniyor=["Dort_belge_turu_de_ilan_ediliyor"],
    ),
]


if __name__ == "__main__":
    sys.exit(olc(KUSURLAR, "m07-abonelik"))
