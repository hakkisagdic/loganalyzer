#!/bin/bash
# Vendor CLI davranışı — TEK KAYNAK (S06).
#
# Dağıtıcı (`komut-dagitici`) ve etkileşimli kabuk (`cli-oykunmesi`) aynı
# soruları soruyor: bu komut ne istiyor, bu vendor hangi promptu basıyor, bu
# vendor bilinmeyen komuta ne diyor. İki yerde cevaplansaydı ayrışırlardı ve
# ayrışma sessiz olurdu: exec kanalı komutu tanır, kabuk tanımaz — ve ikisini
# karşılaştıran hiçbir test yok.
#
# NE TAKLİT EDİLİYOR, NE EDİLMİYOR (FS §11, S06 kapsamı):
#
#   Buradaki metinler BİZİM YAZDIĞIMIZ biçim. Toplayıcıyı sınıyorlar,
#   vendor'ı DEĞİL. FortiGate 7.2 ile 7.4'ün aynı komuta farklı cevap vermesi
#   kapsam dışı ve kalıcı olarak dışarıda: bir müşteri cihazından alınan tek
#   bir gerçek çıktı, buradaki on satırdan çok şey söyler.

# Öykünmenin tanıdığı vendor'lar.
#
# Liste burada duruyor ve açılışta SINANIYOR (`entrypoint`). Bilinmeyen bir
# vendor sessizce genel bir hata metnine düşseydi, yeni bir vendor profili
# eklendiği gün öykünme onu "tanıyor gibi" davranırdı — §7'nin sessiz sınıfı.
CLI_VENDORS="fortinet cisco mikrotik"

cli_vendor_bilinen() {
    case " ${CLI_VENDORS} " in
        *" ${1} "*) return 0 ;;
    esac

    return 1
}

# Profil YAML'ından tek bir üst düzey alan. YAML ayrıştırıcı YOK: okunan üç
# alan da (`vendor`, `hostname`, `product`) şemanın kökünde ve S01 onları
# sabitliyor. Tam bir ayrıştırıcı, alanların yerini ikinci kez tarif etmek
# olurdu.
cli_profil_alani() {
    sed -n "s/^${2}:[[:space:]]*//p" "${1}" | head -n 1 | tr -d '\r'
}

# Prompt. Cisco ailesinde `>` ile `#` ARASINDA gerçek bir fark var (enable);
# FortiGate ve RouterOS'ta yok ve uydurmak yanlış olurdu.
cli_prompt() {
    _vendor="${1}"
    _hostname="${2}"
    _mod="${3}"

    case "${_vendor}" in
        cisco)
            if [ "${_mod}" = "enable" ]; then
                printf '%s# ' "${_hostname}"
            else
                printf '%s> ' "${_hostname}"
            fi
            ;;
        fortinet)
            printf '%s # ' "${_hostname}"
            ;;
        mikrotik)
            printf '[%s@%s] > ' "${SIM_USERNAME:-admin}" "${_hostname}"
            ;;
    esac
}

# Vendor'ın KENDİ hata metni.
#
# S06'nın ikinci kabul kriteri buna dayanıyor: "komut yanlış" ile "cihaz cevap
# vermedi" farklı şeyler. Simülatör genel bir ret bassaydı toplayıcı ikisini
# ayırt edemezdi ve ayırt edememesi de görünmezdi.
cli_hata() {
    _vendor="${1}"
    _komut="${2}"

    case "${_vendor}" in
        cisco)
            # ASA `^` işaretçisini hatalı belirtecin ALTINA koyuyor. Burada
            # komutun başına konuyor: konumun kendisi taklit edilmiyor, çünkü
            # ürün onu okumuyor ve okumayan bir şeyi taklit etmek, taklidin
            # doğruluğunu sınanamaz kılar.
            printf '%s\n' "${_komut}"
            printf '    ^\n'
            printf "%% Invalid input detected at '^' marker.\n"
            ;;
        fortinet)
            printf "command parse error before '%s'\n" "${_komut}"
            printf 'Command fail. Return code -61\n'
            ;;
        mikrotik)
            printf 'bad command name %s (line 1 column 1)\n' "${_komut}"
            ;;
    esac
}

# Komutun NE İSTEDİĞİ — komutun kendisi değil.
#
# S03'ün kararı korunuyor: ürünün komut tablosu burada TEKRARLANMIYOR. Tabloyu
# ikinci kez yazmak, ürün komutunu değiştirdiğinde simülatörün eskisini
# tanımaya devam etmesi ve testin "cihaz cevap verdi" diye yeşil kalması
# demekti.
#
# Dönen değerler: `sayfalama-kapat`, `sayfalama-baglam`, `baglam-bitir`,
# `config`, `enable`, `cikis`, `bos`, `bilinmeyen`.
cli_komut_turu() {
    case "${1}" in
        "")
            printf 'bos\n'
            ;;
        "enable"|"en")
            printf 'enable\n'
            ;;
        "exit"|"quit"|"logout")
            printf 'cikis\n'
            ;;
        # FortiGate sayfalamayı ÜÇ SATIRDA kapatıyor: `config system console`
        # bir bağlama giriyor, `set output standard` ayarı yazıyor, `end`
        # çıkıyor. Toplayıcı bu üçünü tek bir komut dizesi olarak gönderiyor —
        # ama etkileşimli kabukta üç ayrı satır olarak geliyorlar ve bağlamı
        # olmayan bir öykünme ikincisini tanımazdı.
        *"system console"*)
            printf 'sayfalama-baglam\n'
            ;;
        *"output standard"*|*"terminal pager"*|*"terminal length"*|*"set output"*)
            printf 'sayfalama-kapat\n'
            ;;
        "end")
            printf 'baglam-bitir\n'
            ;;
        "show"|*"running-config"*|*"export terse"*|*"full-configuration"*)
            printf 'config\n'
            ;;
        *)
            printf 'bilinmeyen\n'
            ;;
    esac
}

# Config'i basıyor — sayfalayarak ya da sayfalamadan.
#
# SAYFALAMA AÇIKKEN NE OLDUĞU, BU TICKET'IN TAŞIYICI CÜMLESİ:
#
#   Cihaz bir sayfa basıyor, `--More--` yazıyor ve tuş bekliyor. Cevap
#   gelirse devam ediyor. GELMEZSE oturumu bırakıyor ve GERİYE KALANI HİÇ
#   BASMIYOR — çıkış kodu yine 0. Yani okuyan taraf yarım config'i BAŞARILI
#   bir çekim olarak alıyor: hata yok, sayaç yok, belirti yok.
#
#   Alternatif modelleme (`--More--`de sonsuza kadar bloke olmak) da gerçek,
#   ama zaman aşımına düşen bir çekim GÖRÜLÜR bir arıza üretiyor. Buradaki
#   model bilerek sessiz olanı: ölçülmek istenen sınıf o.
cli_bas() {
    _dosya="${1}"
    _sayfalama="${2}"

    if [ "${_sayfalama}" != "acik" ]; then
        cat "${_dosya}"
        return 0
    fi

    _sayfa=0

    # Config DOSYASI 3 numaralı tanıtıcıdan okunuyor, stdin'den değil.
    #
    # Aksi hâlde `--More--` için beklenen tuş, oturumdan değil config
    # dosyasının bir sonraki satırından okunurdu: sayfalama kendi kendine
    # ilerler, her sayfa bir satır yutar ve çıktı SESSİZCE eksilirdi. Bu tam
    # olarak ölçmeye çalıştığımız hata sınıfının, ölçüm aracının içindeki hâli.
    while IFS= read -r _satir <&3; do
        printf '%s\n' "${_satir}"
        _sayfa=$((_sayfa + 1))

        if [ "${_sayfa}" -lt "${SIM_PAGE_LINES:-24}" ]; then
            continue
        fi

        printf -- '--More--'

        # Duvar saati burada bir ÖLÇÜT değil, cihazın davranışı: gerçek bir
        # terminal de sonsuza kadar beklemiyor. Testler bu süreye değil
        # ÇIKTININ İÇERİĞİNE bakıyor (§6).
        if IFS= read -r -n 1 -t "${SIM_MORE_TIMEOUT:-2}" _tus; then
            # Gerçek cihaz `--More--` satırını siliyor; kalsaydı config'e
            # ait olmayan bir satır fark motoruna girerdi.
            printf '\r        \r'
            _sayfa=0
        else
            printf '\n'
            return 0
        fi
    done 3< "${_dosya}"

    return 0
}
