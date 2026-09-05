#!/bin/bash
# N3 — etkileşimli CLI öykünmesi (S06).
#
# S03 kabuk isteğini REDDEDİYORDU ve gerekçesi doğruydu: cihazlar Linux kabuğu
# vermiyor. Ama vermedikleri şey Linux kabuğu; KENDİ kabuklarını veriyorlar —
# prompt basan, `enable` bilen, çıktıyı sayfalayan bir kabuk. S06'nın açtığı
# yüzey bu, ve S03'ün reddettiği yüzey değil.
#
# TAŞIYICI CÜMLE: gerçek bir ASA `--More--` basar ve toplayıcı onu görmezse
# çıktının yarısını alır — HATASIZ. Bu dosya o cümleyi koşturulabilir yapıyor.
set -uo pipefail

# shellcheck source=vendor-cli.sh
. /usr/local/bin/vendor-cli

profil="${1}"
vendor="${2}"
hostname="${3}"
config="${4}"

# Sayfalama AÇIK başlıyor — cihazın varsayılanı bu.
#
# Kapalı başlasaydı öykünme, toplayıcının sayfalamayı kapatmayı unutmasını
# hiçbir zaman cezalandırmazdı ve testler "toplayıcı sayfalamayı biliyor" diye
# yeşil kalırdı. Yeşilliğin hiçbir şey ifade etmediği hâl.
if [ "${SIM_PAGING:-etkilesimli}" = "kapali" ]; then
    sayfalama="kapali"
else
    sayfalama="acik"
fi

# DURUM MAKİNESİ ORTAK (S08) — exec kanalı da aynısını kullanıyor.
#
# S06'da bu döngü kendi durumunu tutuyordu ve exec kanalı ayrı bir `case` ile
# sınıflandırıyordu. İkisi aynı soruları cevaplıyordu; ayrıştıkları gün
# ayrışma sessiz olurdu ve hangisinin doğru olduğunu söyleyen test yoktu.
cli_oturum_baslat "${vendor}" "${sayfalama}"

printf 'N3 öykünmesi: %s (%s), profil %s\n' "${hostname}" "${vendor}" "${profil}"

while :; do
    cli_prompt "${vendor}" "${hostname}" "${CLI_MOD}"

    if ! IFS= read -r satir; then
        break
    fi

    cli_komut_isle "${vendor}" "${config}" "${satir}"

    if [ "${CLI_CIKIS}" -ne 0 ]; then
        break
    fi
done

exit 0
