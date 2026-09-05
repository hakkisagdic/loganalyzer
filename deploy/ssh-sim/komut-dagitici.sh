#!/bin/bash
# Gelen SSH oturumunu cihaz davranışına çeviren dağıtıcı (S03 · S06).
#
# KOMUT TABLOSU BURADA TEKRARLANMIYOR — ürünün `ConfigCollector`'ı ne
# gönderiyorsa o tanınıyor. Tabloyu ikinci kez yazmak §9'un yasakladığı kopya
# olurdu ve ayrışma tam olarak şurada görünürdü: ürün komutu değiştirir,
# simülatör eskisini tanımaya devam eder, ve test "cihaz cevap verdi" diye
# yeşil kalır. Bu yüzden eşleşme KOMUTUN KENDİSİYLE değil, komutun NE İSTEDİĞİ
# ile yapılıyor — ve o soru tek yerde cevaplanıyor (`vendor-cli`).
#
# İKİ YOL, TEK DAVRANIŞ TABLOSU:
#
#   exec kanalı  — `ssh host "show"`. Toplayıcının bugün kullandığı yol.
#   etkileşimli  — kabuk isteği. S06'nın açtığı yol; `cli-oykunmesi` yürütüyor.
set -uo pipefail

# shellcheck source=vendor-cli.sh
. /usr/local/bin/vendor-cli

# AYARLAR ORTAMDAN DEĞİL DOSYADAN OKUNUYOR — ve bu bir zevk meselesi değil.
#
# OpenSSH oturum çocuğuna temiz bir ortam kuruyor: `SIM_*` gibi keyfi
# değişkenler sshd'nin ortamında dursa bile ForceCommand'a ULAŞMIYOR. S03
# profili bu yüzden `/sim-profil`e yazmıştı; ama `SIM_SCENARIO` ortamdan
# okunmaya devam ediyordu ve compose onu ayarlıyordu. Sonuç: senaryo seçimi
# SESSİZCE yok sayılıyor, dağıtıcı her zaman baseline'ı veriyordu — hata yok,
# sayaç yok, belirti yok. Bir bekçi artık bu ayrışmayı sınıyor
# (`SshSimSettingsTests`).
if [ -r /sim-ayarlar ]; then
    # shellcheck source=/dev/null
    . /sim-ayarlar
fi

profil="${SIM_PROFILE:-}"
senaryo="${SIM_SCENARIO:-}"
vendor="${SIM_VENDOR:-}"
hostname="${SIM_HOSTNAME:-}"

if [ -z "${profil}" ] || [ -z "${vendor}" ]; then
    echo "simülatör ayarları okunamadı (/sim-ayarlar)" >&2
    exit 70
fi

# Senaryo dosyası: profil YAML'ındaki `config.scenarios` ile aynı düzen.
# Okuyucu YAML ayrıştırmıyor — dizin düzeni sözleşme ve S01 onu sabitliyor.
if [ -n "${senaryo}" ]; then
    config="/sim/profiller/${profil}/${senaryo}.conf"
else
    config="/sim/profiller/${profil}/baseline.conf"
fi

komut="${SSH_ORIGINAL_COMMAND:-}"

# KABUK İSTEĞİ ARTIK REDDEDİLMİYOR — S03'ün kararı S06'da genişledi.
#
# S03'ün gerekçesi ("cihazlar kabuk vermiyor") Linux kabuğu için doğru ve hâlâ
# geçerli: aşağıda `ls` çalışmıyor. Cihazların vermediği şey `sh`; KENDİ
# kabuklarını veriyorlar ve toplayıcının en kırılgan yolu orada.
if [ -z "${komut}" ]; then
    exec /usr/local/bin/cli-oykunmesi "${profil}" "${vendor}" "${hostname}" "${config}"
fi

# ---------------------------------------------------------------- exec kanalı

# EXEC KANALINDA SAYFALAMA VARSAYILAN OLARAK KAPALI.
#
# Gerekçe: exec kanalında PTY yok ve cihazların çoğu PTY yokluğunda sayfalamayı
# uygulamıyor. Bu bir ÖLÇÜLMÜŞ vendor olgusu değil, MODELLENMİŞ bir varsayım —
# FS §11'in "vendor sürüm farkları kapsam dışı" kalemine giriyor ve burada
# yazılı olmasının sebebi de o.
#
# `SIM_PAGING=daima` diyen bir koşum, sayfalayan cihazı modelliyor. S06'nın
# bulgusu o koşumdan çıkıyor: toplayıcı sayfalamayı komut 1'de kapatıyor ama
# komut 2 AYRI BİR EXEC KANALI, yani ayrı bir oturum — ayar taşınmıyor.
if [ "${SIM_PAGING:-etkilesimli}" = "daima" ]; then
    sayfalama="acik"
else
    sayfalama="kapali"
fi

tur="$(cli_komut_turu "${komut}")"

case "${tur}" in
    # HAZIRLIK KOMUTLARI: gerçek cihaz bunlara sessizce ve BAŞARIYLA cevap
    # veriyor; simülatör de öyle yapmalı. Hata dönseydi toplayıcı ilk komutta
    # durur ve config'e hiç gelmezdi.
    #
    # Toplayıcı FortiGate'te üç satırı TEK komut dizesi olarak gönderiyor;
    # `sayfalama-baglam` o dizeyi karşılıyor. Aynı çağrıda `config` de
    # istenmediği için burada kapatılan sayfalamanın taşıyacağı bir yer yok —
    # bulgunun mekanizması tam olarak bu.
    sayfalama-baglam|sayfalama-kapat|baglam-bitir|enable)
        exit 0
        ;;

    config)
        if [ ! -f "${config}" ]; then
            # Var olmayan senaryo SESSİZCE baseline'a düşmüyor — N1'in aynı
            # kararı (`SimulatedDeviceTransport`). Düşseydi adı yanlış yazılmış
            # bir senaryo testi yeşil bırakır ve "fark yok" sonucu doğru
            # sanılırdı.
            echo "config bulunamadı: ${config}" >&2
            exit 66
        fi

        cli_bas "${config}" "${sayfalama}"
        exit 0
        ;;
esac

# TANINMAYAN KOMUT: vendor'ın KENDİ hata metni.
#
# S03 burada açık bir ret basıyordu ve gerekçesi "vendor metinlerini taklit
# etmek S06'nın işi"ydi. S06 geldi. Metin stderr'e gidiyor ve çıkış kodu
# sıfırdan farklı: `SshDeviceTransport` ikisini de okuyor, yani "komut yanlış"
# ile "cihaz cevap vermedi" ürün tarafında AYRI değerler (S06 kabul kriteri).
cli_hata "${vendor}" "${komut}" >&2
exit 127
