#!/bin/bash
# Gelen SSH komutunu profil çıktısına çeviren dağıtıcı (S03).
#
# KOMUT TABLOSU BURADA TEKRARLANMIYOR — ürünün `ConfigCollector`'ı ne
# gönderiyorsa o tanınıyor. Tabloyu ikinci kez yazmak §9'un yasakladığı kopya
# olurdu ve ayrışma tam olarak şurada görünürdü: ürün komutu değiştirir,
# simülatör eskisini tanımaya devam eder, ve test "cihaz cevap verdi" diye
# yeşil kalır. Bu yüzden eşleşme KOMUTUN KENDİSİYLE değil, komutun NE İSTEDİĞİ
# ile yapılıyor — config mi istiyor, yoksa bir hazırlık komutu mu.
set -uo pipefail

komut="${SSH_ORIGINAL_COMMAND:-}"
profil="$(cat /sim-profil 2>/dev/null || echo '')"

if [ -z "${profil}" ]; then
    echo "simülatör profili ayarlanmamış" >&2
    exit 70
fi

# Kabuk isteği (komutsuz oturum) REDDEDİLİYOR.
#
# Cihazlar kabuk vermiyor; veren bir simülatör, ürünün asla karşılaşmayacağı
# bir yüzeyi taklit ederdi. Ayrıca S06'nın etkileşimli kabuğu bu değil — orada
# gelen şey vendor'ın kendi CLI'ı, Linux kabuğu değil.
if [ -z "${komut}" ]; then
    echo "bu cihaz etkileşimli kabuk vermiyor" >&2
    exit 65
fi

senaryo="${SIM_SCENARIO:-}"

# Senaryo dosyası: profil YAML'ındaki `config.scenarios` ile aynı düzen.
# Okuyucu YAML ayrıştırmıyor — dizin düzeni sözleşme ve S01 onu sabitliyor.
if [ -n "${senaryo}" ]; then
    config="/sim/profiller/${profil}/${senaryo}.conf"
else
    config="/sim/profiller/${profil}/baseline.conf"
fi

# HAZIRLIK KOMUTLARI: sayfalama kapatma ve konsol ayarı.
#
# Üç vendor da config'i almadan önce sayfalamayı kapatıyor
# (`config system console…`, `terminal pager 0`). Gerçek cihaz bunlara sessizce
# ve BAŞARIYLA cevap veriyor; simülatör de öyle yapmalı. Hata dönseydi
# toplayıcı ilk komutta durur ve config'e hiç gelmezdi.
case "${komut}" in
    *"system console"*|*"terminal pager"*|*"terminal length"*)
        exit 0
        ;;
esac

# CONFIG KOMUTLARI: üç vendor, üç farklı ifade, tek anlam.
case "${komut}" in
    "show"|*"running-config"*|*"export terse"*|*"full-configuration"*)
        if [ ! -f "${config}" ]; then
            # Var olmayan senaryo SESSİZCE baseline'a düşmüyor — N1'in aynı
            # kararı (`SimulatedDeviceTransport`). Düşseydi adı yanlış yazılmış
            # bir senaryo testi yeşil bırakır ve "fark yok" sonucu doğru
            # sanılırdı.
            echo "config bulunamadı: ${config}" >&2
            exit 66
        fi

        cat "${config}"
        exit 0
        ;;
esac

# TANINMAYAN KOMUT: cihazın kendi hata biçimi değil, açık bir ret.
#
# Vendor hata metinlerini taklit etmek S06'nın işi. Burada taklit etmek, S06
# yazıldığında iki ayrı taklidin ayrışması demek olurdu.
echo "tanınmayan komut: ${komut}" >&2
exit 127
