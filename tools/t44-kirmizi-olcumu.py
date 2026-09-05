#!/usr/bin/env python3
"""T44 — bekçilerin KIRMIZI yanabildiğinin ölçümü (CLAUDE.md §6).

Yordam üç adımlı ve üçüncüsü zorunlu:

    kusuru yaz → dosyayı OKU ve kusurun orada olduğunu İDDİA ET → koştur → geri al

İddia adımı olmazsa ölçüm kendi başarısızlığını sessizce başarı diye
raporluyor: kırmızı beklenen bir koşumda yeşil sonuç iki şey anlatabiliyor —
*"kusur etkisiz"* ya da **"kusur hiç uygulanmadı"** — ve ikisi aynı çıktıyı
veriyor. Bir turda üç ajan bağımsız olarak bu tuzağa düştü.

Ayrıca her ölçüm bir de KONTROL testi koşturuyor: kusurdan **etkilenmemesi
gereken** bir test. Yalnızca "kırmızı yandı" ölçülseydi, her şeyi düşüren bir
kusur da aynı çıktıyı verirdi — yani kapının neyi tuttuğu değil, yalnızca
tuttuğu ölçülmüş olurdu.
"""

import pathlib
import shutil
import subprocess
import sys

KOK = pathlib.Path(__file__).resolve().parent.parent
DOTNET = str(pathlib.Path.home() / ".dotnet" / "dotnet")

BAGLAYICI = KOK / "src/Bizigo.Rca/Reasoning/SentenceBinder.cs"
GORUS = KOK / "src/Bizigo.Rca/Reasoning/StepEvidenceView.cs"
PROMPT = KOK / "src/Bizigo.Rca/Reasoning/ScenarioPromptBuilder.cs"
BELGE = KOK / "src/Bizigo.Rca/Reasoning/RcaReportDocument.cs"
DEPO = KOK / "src/Bizigo.Rca/Reasoning/RcaReportStore.cs"
EKRAN = KOK / "ui/src/app/rca/[id]/ReportView.tsx"

OLCUMLER = [
    {
        "ad": "A · Referanssız cümle rapora GİRERSE",
        "dosya": BAGLAYICI,
        "eski": "                // ATILIYOR: rapora hiç girmiyor. Sayısı yukarıdaki listede kalıyor.\n                continue;\n",
        "yeni": "                // KIRMIZI-A: atma kaldırıldı, cümle rapora giriyor.\n",
        "isaret": "KIRMIZI-A",
        "kirmizi": "SentenceBindingGateTests.Referanssiz_cumle_rapora_girmiyor",
        # Sayaç `Bound` bayrağından besleniyor, atma davranışından değil:
        # "atmak" ile "saymak" ayrı iddialar ve ayrı testleri olmalı.
        "kontrol": "SentenceBindingGateTests.Atilan_cumle_sayiliyor",
    },
    {
        "ad": "B · Atılan cümle SAYILMAZSA",
        "dosya": BAGLAYICI,
        "eski": "    public int Dropped => Sentences.Count(s => !s.Bound);",
        "yeni": "    public int Dropped => 0; // KIRMIZI-B: sayaç susturuldu",
        "isaret": "KIRMIZI-B",
        "kirmizi": "SentenceBindingGateTests.Atilan_cumle_sayiliyor",
        "kontrol": "SentenceBindingGateTests.Referanssiz_cumle_rapora_girmiyor",
    },
    {
        "ad": "C · Doğrulama PAKETİN TAMAMINA karşı yapılırsa",
        "dosya": GORUS,
        "eski": "                foreach (var id in document.CitedIds)",
        "yeni": "                foreach (var id in bundle.Items.Select(i => i.Id)) // KIRMIZI-C",
        "isaret": "KIRMIZI-C",
        "kirmizi": "ConstraintGateTests.Adimin_gormedigi_kanita_atif_reddediliyor",
        # Görmediği kanıt reddi düşse bile GÖRDÜĞÜ kanıt hâlâ geçmeli; yoksa
        # kusur kapıyı değil her şeyi bozmuş olur ve ölçüm hiçbir şey söylemez.
        "kontrol": "ConstraintGateTests.Adimin_gordugu_kanita_atif_geciyor",
    },
    {
        "ad": "D · Prompt tipi kurucu DIŞINDA üretilebilirse",
        "dosya": PROMPT,
        "eski": "    private ScenarioStepPrompt(string stepId, int attempt, RedactedPrompt system, RedactedPrompt user)",
        "yeni": (
            "    // KIRMIZI-D: kapı bir tip olmaktan çıkıp çağrı alışkanlığına dönüyor.\n"
            "    public ScenarioStepPrompt(string stepId, int attempt, RedactedPrompt system, RedactedPrompt user)"
        ),
        "isaret": "KIRMIZI-D",
        "kirmizi": "PromptGateTests.Prompt_tipini_yalnizca_kurucu_uretebiliyor",
        "kontrol": "PromptGateTests.Kanit_metnindeki_sir_prompta_girmiyor",
    },

    # --------------------------------------------------------------- T51 ----
    {
        "ad": "E · Ölçülemeyen oran SIFIR yazılırsa",
        "dosya": BELGE,
        "eski": "ProducedSentenceCount > 0 ? (double)DroppedSentenceCount / ProducedSentenceCount : null;",
        "yeni": "ProducedSentenceCount > 0 ? (double)DroppedSentenceCount / ProducedSentenceCount : 0d; // KIRMIZI-E",
        "isaret": "KIRMIZI-E",
        "kirmizi": "RcaReportPersistenceTests.Payda_sifirken_oran_null_sifir_degil",
        # Kontrol, kusurun DAR olduğunu gösteriyor: ölçülen oranlar hâlâ doğru,
        # bozulan tek şey ölçülemeyen hâlin mükemmel görünmesi.
        "kontrol": "RcaReportPersistenceTests.Bildirilmemis_belirtec_telde_de_null",
    },
    {
        "ad": "F · 'Son rapor' en ESKİyi döndürürse",
        "dosya": DEPO,
        "eski": "            .OrderByDescending(r => r.CreatedAt)\n            .ThenByDescending(r => r.Id)\n            .FirstOrDefaultAsync(cancellationToken);",
        "yeni": "            .OrderBy(r => r.CreatedAt) // KIRMIZI-F: en YENİ yerine en ESKİ\n            .ThenBy(r => r.Id)\n            .FirstOrDefaultAsync(cancellationToken);",
        "isaret": "KIRMIZI-F",
        "kirmizi": "RcaReportPersistenceTests.Ayni_paketin_iki_raporu_da_saklaniyor",
        "kontrol": "RcaReportPersistenceTests.Bulgu_sirasi_depolama_boyunca_korunuyor",
    },
    {
        "ad": "G · Her cümlesi atılan rapor 'bulgu yok' diye çizilirse",
        "dosya": EKRAN,
        "eski": "            ? `Model ${dropped} cümle üretti ve hiçbiri kanıta bağlanamadı",
        "yeni": "            ? `Model hiçbir hipotez üretmedi. KIRMIZI-G ${dropped}",
        "isaret": "KIRMIZI-G",
        "kirmizi": "her cümlesi atılan rapor",
        "kontrol": "model hiç koşmadıysa bölüm sessizce kaybolmuyor",
        "runner": "vitest",
    },
]


def vitest_kosum(ad):
    """Ekran tarafı. `-t` testi ADINA göre filtreliyor."""
    sonuc = subprocess.run(
        ["npx", "vitest", "run", "tests/rca-screen.test.tsx", "-t", ad],
        cwd=KOK / "ui", capture_output=True, text=True,
    )
    ciktı = sonuc.stdout + sonuc.stderr

    # "Hiç test koşmadı" yeşille aynı çıkış kodunu veriyor; ayırt ediliyor.
    if "No test files found" in ciktı or "Tests  0 passed" in ciktı:
        return None, "HİÇ TEST KOŞMADI (filtre eşleşmedi)"

    ozet = next((s.strip() for s in ciktı.splitlines() if "Tests " in s), "özet okunamadı")
    return sonuc.returncode == 0, ozet


def kosum(filtre, runner="dotnet"):
    """Tek bir testi koşturur; (gecti, ozet) döndürür."""
    if runner == "vitest":
        return vitest_kosum(filtre)

    sonuc = subprocess.run(
        [DOTNET, "test", "tests/Bizigo.UnitTests", "--nologo", "-v", "q",
         "--filter", f"FullyQualifiedName~{filtre}"],
        cwd=KOK, capture_output=True, text=True,
        env={**__import__("os").environ,
             "DOTNET_ROOT": str(pathlib.Path.home() / ".dotnet"),
             "PATH": f"{pathlib.Path.home() / '.dotnet'}:{__import__('os').environ['PATH']}"},
    )
    ciktı = sonuc.stdout + sonuc.stderr

    # Testin HİÇ BULUNAMAMASI da yeşil görünüyor: "0 test koştu" bir kapı
    # geçişi değil, kapının hiç sorulmaması. Ayrı ayırt ediliyor.
    if "Toplam:     0" in ciktı or "Total:     0" in ciktı:
        return None, "HİÇ TEST KOŞMADI (filtre eşleşmedi)"

    ozet = next((s.strip() for s in ciktı.splitlines()
                 if "Başarısız:" in s or "Failed:" in s), "özet okunamadı")
    return sonuc.returncode == 0, ozet


def main():
    rapor = []

    for olcum in OLCUMLER:
        dosya = olcum["dosya"]
        yedek = dosya.with_suffix(dosya.suffix + ".yedek")
        shutil.copy2(dosya, yedek)

        try:
            metin = dosya.read_text(encoding="utf-8")

            # 1 · Kusuru uygula.
            if olcum["eski"] not in metin:
                print(f"[{olcum['ad']}] ÇAPA BULUNAMADI — ölçüm yapılamadı.")
                rapor.append((olcum["ad"], "ÇAPA YOK", "-"))
                continue

            dosya.write_text(metin.replace(olcum["eski"], olcum["yeni"], 1), encoding="utf-8")

            # 2 · DOSYAYI OKU ve kusurun orada olduğunu İDDİA ET.
            #     Bu adım olmadan yeşil bir sonuç "kusur hiç uygulanmadı"
            #     anlamına da gelebiliyor ve ikisi ayırt edilemiyor.
            tazeden = dosya.read_text(encoding="utf-8")
            assert olcum["isaret"] in tazeden, f"{olcum['ad']}: kusur dosyada YOK"
            assert olcum["eski"] not in tazeden, f"{olcum['ad']}: eski satır hâlâ duruyor"
            print(f"[{olcum['ad']}] kusur dosyada doğrulandı ({olcum['isaret']}).")

            # 3 · Koştur.
            runner = olcum.get("runner", "dotnet")
            kirmizi, k_ozet = kosum(olcum["kirmizi"], runner)
            kontrol, n_ozet = kosum(olcum["kontrol"], runner)

            print(f"  hedef  {olcum['kirmizi']}: {'KIRMIZI ✓' if kirmizi is False else 'YEŞİL ✗'} — {k_ozet}")
            print(f"  kontrol {olcum['kontrol']}: {'yeşil ✓' if kontrol is True else 'KIRMIZI ✗'} — {n_ozet}")

            rapor.append((
                olcum["ad"],
                "KIRMIZI ✓" if kirmizi is False else f"YEŞİL ✗ ({k_ozet})",
                "yeşil ✓" if kontrol is True else f"KIRMIZI ✗ ({n_ozet})",
            ))
        finally:
            # 4 · Geri al — her hâlükârda.
            #
            # `copy2` DEĞİL `copy`, ve sonra mtime'ı ŞİMDİ'ye çekiyoruz. Sebebi
            # ölçüldü: `copy2` zaman damgasını da geri yüklüyor, dolayısıyla
            # düzeltilmiş dosya derlenmiş DLL'den ESKİ görünüyor ve MSBuild
            # projeyi "güncel" sayıp atlıyor. Kaynak temiz, ikili kusurlu —
            # ve sonraki koşum kusuru geri almış gibi görünürken kusurlu
            # ikiliyi ölçüyor.
            #
            # Bu, §6'nın adını koyduğu sınıfın tersten hâli: orada yeşil bir
            # sonuç "ölçüm yapılmadı" anlamına gelebiliyordu, burada kırmızı bir
            # sonuç "geri alma görülmedi" anlamına geliyor. İkisi de ölçüm
            # aracının kendi sessiz yanlışı.
            shutil.copy(yedek, dosya)
            dosya.touch()
            yedek.unlink()

    print("\n=== ÖZET ===")
    for ad, hedef, kontrol in rapor:
        print(f"{ad}\n    hedef: {hedef}\n    kontrol: {kontrol}")

    return 0 if all(h.startswith("KIRMIZI ✓") and k.startswith("yeşil ✓") for _, h, k in rapor) else 1


if __name__ == "__main__":
    sys.exit(main())
