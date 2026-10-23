using PConTRoL.Core;

namespace PConTRoL.Tests;

public class PartColumnsTests
{
    [Fact]
    public void Normalize_keeps_user_order_and_names_and_repairs_the_rest()
    {
        var saved = new List<PartColumnSetting?>
        {
            new() { Key = "size", Header = "  Abmessung ", Visible = true },
            new() { Key = "rd", Header = "", Visible = false },
            new() { Key = "gibtsnicht", Header = "X" },
            new() { Key = "size", Header = "doppelt" },
            null,
        };

        var result = PartColumnCatalog.Normalize(saved);

        Assert.Equal(["size", "rd", "description", "article", "number"], result.Select(c => c.Key));
        Assert.Equal("Abmessung", result[0].Header);
        Assert.Equal("RD-Nr.", result[1].Header); // leer -> Standardname
        Assert.False(result[1].Visible);
        Assert.All(result.Skip(2), c => Assert.True(c.Visible));
    }

    [Fact]
    public void Missing_settings_give_the_defaults()
    {
        Assert.Equal(PartColumnCatalog.All.Select(d => d.DefaultHeader), PartColumnCatalog.Normalize(null).Select(c => c.Header));
    }

    [Fact]
    public void Identical_parts_are_grouped_with_count_in_first_order()
    {
        PartInfo P(string rd, string desc, string size) => new(rd, desc, "Art", size, "7");
        var parts = new[]
        {
            P("RD1", "Seite", "500 x 300"),
            P("RD2", "Boden", "800 x 300"),
            P("RD1", "Seite", "500 x 300"),
            P("RD1", "Seite ", "500 x 300"), // Leerzeichen am Ende zählt nicht
            P("RD1", "Seite", "600 x 300"),
        };
        var all = PartColumnCatalog.All.Select(d => d.Key).ToList();

        var rows = PartColumnCatalog.Group(parts, all);

        Assert.Equal([3, 1, 1], rows.Select(r => r.Count));
        Assert.Equal(["500 x 300", "800 x 300", "600 x 300"], rows.Select(r => r.Size));

        // Maß ausgeblendet: dann sind beide Seiten gleich.
        var withoutSize = PartColumnCatalog.Group(parts, all.Where(k => k != "size"));
        Assert.Equal([4, 1], withoutSize.Select(r => r.Count));
    }

    [Fact]
    public void Reorder_by_header_drag_keeps_hidden_columns_in_place()
    {
        var columns = PartColumnCatalog.Defaults();
        columns.Single(c => c.Key == "article").Visible = false; // rd, description, (article), size, number

        var result = PartColumnCatalog.Reorder(columns, ["number", "rd", "description", "size"]);

        Assert.Equal(["number", "rd", "article", "description", "size"], result.Select(c => c.Key));
    }
}
