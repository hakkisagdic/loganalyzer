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

## NuGet packages

Runtime and build-time dependencies are declared in
[`Directory.Packages.props`](Directory.Packages.props) and are not redistributed
in source form. Their licenses are available on nuget.org.
