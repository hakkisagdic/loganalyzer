using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Bizigo.ControlPlane.Migrations
{
    /// <summary>
    /// T47: <c>correct_finding_rank</c> kolonu ve
    /// <c>ContradictingEvidenceVerdict</c>'in yeniden numaralanması.
    ///
    /// <para>
    /// <b>Üretilen göç eksikti ve eksikliği sessizdi.</b> <c>dotnet ef migrations
    /// add</c> yalnızca yeni kolonu gördü; enum değerlerinin <b>kaydığını</b>
    /// görmedi, çünkü enum kodda bir sayı ve veritabanında da bir sayı — şema
    /// açısından hiçbir şey değişmemiş görünüyor. Ama <c>contradicting_evidence
    /// = 1</c> dün <c>Sound</c>, bugün <c>NotPresent</c>.
    /// </para>
    ///
    /// <para>
    /// Bu, bu depodaki en pahalı <b>önlenmiş</b> hatanın aynısı: EF'in ürettiği
    /// <c>enabled → status</c> göçü her pasif kuralı sessizce açıyordu. Aynı
    /// sınıf, aynı sebep — <b>üretilen göç, anlamın değiştiğini göremez.</b>
    /// </para>
    /// </summary>
    public partial class AddReviewFindingRank : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "correct_finding_rank",
                schema: "bizigo",
                table: "golden_reviews",
                type: "integer",
                nullable: true);

            // Sorunun SORULUP sorulmadığı ayrı bir kolon. `correct_finding_rank`
            // tek başına yetmiyor: `null` "hiçbiri doğru değildi" demek ve o bir
            // ölçüm. Var olan satırlar için `false` doğru varsayılan — o gün soru
            // sorulmuyordu.
            migrationBuilder.AddColumn<bool>(
                name: "correct_finding_rank_asked",
                schema: "bizigo",
                table: "golden_reviews",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            // Eski kodlar → yeni kodlar. `Unspecified = 0` araya girdiği için
            // hepsi bir kayıyor:
            //
            //   0 NotPresent -> 1 NotPresent
            //   1 Sound      -> 2 Sound
            //   2 Trivial    -> 3 Trivial
            //   3 Unknown    -> 4 Unknown
            //
            // SIRA ŞART: artan yönde güncellemek 0→1 yaptıktan sonra aynı
            // satırı 1→2 diye tekrar yakalar ve bütün tablo tek değere çöker.
            // Azalan yönde her satır tam bir kez taşınıyor.
            //
            // Sürüm 1 satırları `Unspecified` ALMIYOR: o gün alanı gerçekten
            // "bölüm yoktu" diye yazmışlardı, ve `Unspecified` "kimse söylemedi"
            // demek. Yazılmış bir kararı bilinmeyene çevirmek, düzeltmeye
            // çalıştığımız sessiz anlam kaymasının aynısı olurdu.
            foreach (var (eski, yeni) in new[] { (3, 4), (2, 3), (1, 2), (0, 1) })
            {
                migrationBuilder.Sql(
                    $"UPDATE bizigo.golden_reviews SET contradicting_evidence = {yeni} " +
                    $"WHERE contradicting_evidence = {eski};");
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Ters yönde de sıra şart — bu kez artan.
            foreach (var (yeni, eski) in new[] { (1, 0), (2, 1), (3, 2), (4, 3) })
            {
                migrationBuilder.Sql(
                    $"UPDATE bizigo.golden_reviews SET contradicting_evidence = {eski} " +
                    $"WHERE contradicting_evidence = {yeni};");
            }

            migrationBuilder.DropColumn(
                name: "correct_finding_rank_asked",
                schema: "bizigo",
                table: "golden_reviews");

            migrationBuilder.DropColumn(
                name: "correct_finding_rank",
                schema: "bizigo",
                table: "golden_reviews");
        }
    }
}
