using System.Text;
using Bizigo.Ingest.Wal;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

// T63 · WAL yazarı — ack verir, sonra ÖLDÜRÜLMEYİ bekler.
//
// Neden ayrı bir süreç: `SIGKILL` yakalanamaz, yani ölçüm kodu öldürülen sürecin
// DIŞINDA durmak zorunda. Bir birim testi bunu yapamaz — kendini öldürür.
//
// argv: <dizin> <batchSayisi> <batchBasinaSatir> <flushToDisk>

var directory = args[0];
var batches = int.Parse(args[1]);
var perBatch = int.Parse(args[2]);
var flush = bool.Parse(args[3]);

var options = new WalOptions
{
    Directory = directory,

    // §6'nın kusuru BURADA ve üretim kodunu değiştirmeye gerek yok:
    // `FlushToDisk` bir bayrak, ve `false` iken ack artık dayanıklı değil.
    // Kapalıyken bu ölçüm kırmızı yanmalı — yanmazsa ölçüm fsync'i ölçmüyor.
    FlushToDisk = flush,
};

using var wal = new WriteAheadLog(
    Options.Create(options),
    NullLogger<WriteAheadLog>.Instance);

var acked = 0;

for (var b = 0; b < batches; b++)
{
    // Üretimin şekli: batch'in TAMAMI tek WAL çerçevesi
    // (`IngestGateway.cs:101` — `AppendAsync(batch, ...)`). Kayıt başına çerçeve
    // yazmak başka bir ürünü ölçmek olurdu.
    var lines = new StringBuilder();

    for (var r = 0; r < perBatch; r++)
    {
        lines.Append("t63-batch").Append(b).Append("-kayit").Append(r).Append('\n');
    }

    // ÜRÜNÜN SIRASI: yaz + fsync, SONRA ack. `AppendAsync`in dönmesi, ack'in
    // verilebildiği an.
    await wal.AppendAsync(Encoding.UTF8.GetBytes(lines.ToString()), CancellationToken.None);

    acked++;
}

// Ack'lerin hepsi verildi. Ölçüm bu satırı okuyup HEMEN öldürüyor — araya konan
// her bekleme sorulan soruyu değiştirir.
Console.Out.WriteLine("ACKED " + acked.ToString());
await Console.Out.FlushAsync();

// Kendi başına ÇIKMAMALI: düzgün çıkış `Dispose`u çağırır, o da fsync eder ve
// ölçüm fsync'i değil kapanışı ölçmüş olur.
await Task.Delay(Timeout.Infinite);
