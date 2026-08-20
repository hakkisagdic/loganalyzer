# Third-party notices

This project is licensed under the [MIT License](LICENSE). It also redistributes
the following third-party material.

## catalog/patterns/

Grok pattern sets copied verbatim from
[logstash-plugins/logstash-patterns-core](https://github.com/logstash-plugins/logstash-patterns-core),
version **4.3.4**.

- **License:** Apache License, Version 2.0
- **Full text:** [`catalog/patterns/LICENSE`](catalog/patterns/LICENSE)

These files are treated as data and are never edited in place. Syntax that
Oniguruma accepts but .NET does not (`\h`, POSIX bracket classes, `X?*`) is
translated inside the grok compiler, so upgrading to a newer upstream release
stays a plain file copy. See
[`catalog/patterns/README.md`](catalog/patterns/README.md).

## src/Bizigo.Ingest/Otlp/proto/

Protocol Buffer definitions copied verbatim from
[open-telemetry/opentelemetry-proto](https://github.com/open-telemetry/opentelemetry-proto),
version **v1.9.0**.

- **License:** Apache License, Version 2.0
- **Upstream:** https://github.com/open-telemetry/opentelemetry-proto/blob/v1.9.0/LICENSE

Only the message types reachable from `ExportLogsServiceRequest` are vendored.
C# classes are generated at build time (`Grpc.Tools`, `GrpcServices="None"`) and
are not committed; the `.proto` files are the single source. Upgrading is a plain
file copy.

## NuGet packages

Runtime and build-time dependencies are declared in
[`Directory.Packages.props`](Directory.Packages.props) and are not redistributed
in source form. Their licenses are available on nuget.org.

## catalog/sigma/ — bugün üçüncü taraf DEĞİL

Buradaki 24 Sigma kuralı **bizim** (T30 örnekleminden terfi ettirildi) ve çivi
bunu söylüyor: `catalog/sigma/ruleset.json` → `source: "bizigo/prototip"`.
Dolayısıyla bugün bu bölümde bildirilecek bir üçüncü taraf malzemesi yok.

**Gerçek SigmaHQ alt kümesi çivilendiğinde burası doldurulmalı.** O gün
bakılacak şey: SigmaHQ kuralları **Detection Rule License** (DRL) altında —
Apache/MIT değil — ve DRL yeniden dağıtım için atıf istiyor. `catalog/patterns/`
zaten aynı deseni izliyor (logstash grok pattern'leri, yukarıda kayıtlı), yani
biçim orada hazır: kaynak deposu, sürüm/commit, lisans adı.

Bu not, o günü yaşayan kişinin soruyu sıfırdan sormaması için duruyor.

