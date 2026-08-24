# N2 — gerçek SSH sunucusu (S03)
#
# NEDEN CONTAINER, NEDEN SÜREÇ İÇİ KÜTÜPHANE DEĞİL:
#
#   1. Süreç içi bir sunucu, istemciyi KÜTÜPHANEYE karşı sınar. El sıkışma,
#      anahtar değişimi, kanal açılışı ve exec isteği gerçekten yaşanmaz —
#      oysa `SshDeviceTransport`'un bugüne kadar hiç koşmamış olan kısmı tam
#      olarak o.
#   2. S06 (CLI öykünmesi) aynı zemine ihtiyaç duyuyor; süreç içi bir çözüm
#      orada atılıp yeniden yazılırdı.
#   3. Testin dışında da kullanılabilir: bir geliştirici toplayıcıyı buraya
#      yöneltip elle deneyebilir.
#
# Alpine + OpenSSH bilinçli: gerçek bir OpenSSH sunucusu, öykünme değil. Ürünün
# karşılaşacağı sürüm ve davranış aynı ailedan geliyor.
FROM alpine:3.20

RUN apk add --no-cache openssh-server bash \
    && mkdir -p /var/run/sshd /sim

# Yapılandırma imaja giriyor, çalışma anında ÜRETİLMİYOR: sunucunun davranışı
# `docker compose up` ile `docker run` arasında değişmemeli.
COPY sshd_config /etc/ssh/sshd_config
COPY komut-dagitici.sh /usr/local/bin/komut-dagitici
COPY entrypoint.sh /usr/local/bin/entrypoint

RUN chmod +x /usr/local/bin/komut-dagitici /usr/local/bin/entrypoint

# Profil kataloğu MONTE ediliyor, kopyalanmıyor: bir config düzeltildiğinde
# imajı yeniden derlemek gerekmesin. Salt okunur bağlanıyor (compose).
VOLUME ["/sim"]

EXPOSE 22

ENTRYPOINT ["/usr/local/bin/entrypoint"]
