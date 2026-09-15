using Bizigo.Ingest.Wal;

// T63 · WAL okuyucu — öldürülmüş sürecin ardından diskte KAÇ çerçeve kaldığını
// sayar.
//
// Neden `ListSealedSegments()` değil: öldürülen süreç son segmenti MÜHÜRLEMEDİ,
// yani o API onu göstermez — ve tam o segment ölçümün konusu.
//
// Neden sayıyor, "dosya var mı" demiyor: kriterin sözü ack'lenen olayın
// KAYBOLMAMASI. Bir çerçevenin kaybı "dosya var" sorusuyla görünmez.

var count = 0;

foreach (var path in Directory
    .EnumerateFiles(args[0], "wal-*.log")
    .Order(StringComparer.Ordinal))
{
    foreach (var _ in WriteAheadLog.ReadFrames(path))
    {
        count++;
    }
}

Console.Out.WriteLine(count.ToString());
