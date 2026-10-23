using PConTRoL.Core.Parsing;
using PConTRoL.Core.Scanning;

namespace PConTRoL.Tests;

/// <summary>
/// Die Dateien in TestData sind erfundene Beispiele im Aufbau echter BetterNest-Ausgaben
/// (.hop ANSI, .lbl UTF-16). Keine echten Auftragsdaten ins Repo legen.
/// </summary>
public class ParserTests
{
    private static string Data(string name) => Path.Combine(AppContext.BaseDirectory, "TestData", name);

    [Fact]
    public void Sample_with_priority_is_read_from_lbl_and_hop_header()
    {
        const string name = "MUSTER_KW01_1_Montag(XX)_Beispielplatte_12mm_01";
        var info = NestFileParser.Parse(Data(name + ".hop"), Data(name + ".lbl"));

        Assert.Equal(1, info.BoardId);
        Assert.Equal(27, info.BoardCount);
        Assert.Equal("Beispielplatte 12mm", info.Material);
        Assert.Equal(5600, info.DimX);
        Assert.Equal(2100, info.DimY);
        Assert.Equal(12, info.DimZ);
        Assert.Equal(3, info.PartsOnBoard);
        Assert.Equal(55, info.Priority);
        Assert.Equal(3, info.Parts.Count);
        Assert.Equal("RD9000001", info.Parts[0].RdNumber);
        Assert.Equal("Teil 1.1", info.Parts[0].Description);
        Assert.Equal("MUSTER 1000 A", info.Parts[0].Article);
        Assert.Equal("MUSTER_KW01_1_Montag(XX)_Beispielplatte_12mm", FolderScanner.DeriveNestJob(name, info));
    }

    [Fact]
    public void Utf16_lbl_with_umlauts_and_production_id_is_read()
    {
        const string name = "MUSTER Typ 1000__08_15_00_000_2";
        var info = NestFileParser.Parse(Data(name + ".hop"), Data(name + ".lbl"));

        Assert.Equal(2, info.BoardId);
        Assert.Equal(4, info.BoardCount);
        Assert.Equal("Beispielboard S 22mm", info.Material);
        Assert.Null(info.Priority);
        Assert.Equal("900001", info.ProductionId);
        Assert.Contains(info.Parts, p => p.Description == "Prüfteil Außenecke");
        Assert.Equal("MUSTER Typ 1000__08_15_00_000", FolderScanner.DeriveNestJob(name, info));
    }

    [Fact]
    public void Nest_job_falls_back_to_name_without_board_number()
    {
        Assert.Equal("Auftrag_X", FolderScanner.DeriveNestJob("Auftrag_X_07", new NestFileInfo()));
    }

    [Fact]
    public void Production_id_is_replaced_in_place_and_nothing_else_changes()
    {
        var original = File.ReadAllBytes(Data("MUSTER Typ 1000__08_15_00_000_2.hop"));
        var updated = HopProductionId.Apply(original, 234541);

        var expected = Text1252(original).Replace("_hhdata_ProductionID := 900001;", "_hhdata_ProductionID := 234541;");
        Assert.Equal(expected, Text1252(updated));
        Assert.Equal(original.Length, updated.Length); // gleich viele Ziffern
        Assert.Equal(updated, HopProductionId.Apply(updated, 234541));
    }

    [Fact]
    public void Missing_production_id_is_inserted_right_before_start()
    {
        var original = File.ReadAllBytes(Data("MUSTER_KW01_1_Montag(XX)_Beispielplatte_12mm_01.hop"));
        var updated = HopProductionId.Apply(original, 1001);

        var expected = Text1252(original).Replace("_hhdata_ProdPrio := 55;\r\nSTART\r\n", "_hhdata_ProdPrio := 55;\r\n_hhdata_ProductionID := 1001;\r\nSTART\r\n");
        Assert.Equal(expected, Text1252(updated));

        var info = new NestFileInfo();
        NestFileParser.ApplyHopHeader(info, Text1252(updated).Split("\r\n"));
        Assert.Equal("1001", info.ProductionId);
        Assert.Equal(55, info.Priority);
    }

    [Fact]
    public void Hop_without_start_line_is_refused()
    {
        var bytes = System.Text.Encoding.Latin1.GetBytes("VARS\r\n   DX := 1;\r\n");
        Assert.Throws<FormatException>(() => HopProductionId.Apply(bytes, 1001));
    }

    private static string Text1252(byte[] bytes) => System.Text.Encoding.Latin1.GetString(bytes);
}
