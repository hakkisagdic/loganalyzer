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

mod="kullanici"
baglam=""

# FortiGate ve RouterOS'ta `enable` yok; ilk prompt zaten ayrıcalıklı.
if [ "${vendor}" != "cisco" ]; then
    mod="enable"
fi

printf 'N3 öykünmesi: %s (%s), profil %s\n' "${hostname}" "${vendor}" "${profil}"

while :; do
    cli_prompt "${vendor}" "${hostname}" "${mod}"

    if ! IFS= read -r satir; then
        break
    fi

    komut="$(printf '%s' "${satir}" | tr -d '\r')"
    tur="$(cli_komut_turu "${komut}")"

    case "${tur}" in
        bos)
            ;;

        cikis)
            break
            ;;

        enable)
            mod="enable"
            ;;

        # `config system console` bir BAĞLAMA giriyor. Bağlam olmadan
        # `set output standard` tek başına anlamsız bir satır olurdu ve
        # öykünme onu tanıyıp sayfalamayı kapatırdı — yani toplayıcının
        # gönderdiğinden DAHA GEVŞEK davranırdı. Gevşek bir öykünme, sıkı bir
        # cihazda kırılacak bir toplayıcıyı yeşil gösterir.
        sayfalama-baglam)
            baglam="konsol"
            ;;

        sayfalama-kapat)
            if [ "${vendor}" = "fortinet" ] && [ "${baglam}" != "konsol" ]; then
                cli_hata "${vendor}" "${komut}"
            else
                sayfalama="kapali"
            fi
            ;;

        baglam-bitir)
            baglam=""
            ;;

        config)
            if [ ! -f "${config}" ]; then
                # Var olmayan senaryo sessizce baseline'a DÜŞMÜYOR — N1 ve
                # dağıtıcı ile aynı karar.
                printf 'config bulunamadı: %s\n' "${config}"
            else
                cli_bas "${config}" "${sayfalama}"
            fi
            ;;

        *)
            # Vendor'ın KENDİ hata metni. Genel bir ret, "komut yanlış" ile
            # "cihaz cevap vermedi"yi tek değere indirirdi (S06 kabul kriteri).
            cli_hata "${vendor}" "${komut}"
            ;;
    esac
done

exit 0
