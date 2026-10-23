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
    public void Reorder_by_header_drag_keeps_hidden_columns_in_place()
    {
        var columns = PartColumnCatalog.Defaults();
        columns.Single(c => c.Key == "article").Visible = false; // rd, description, (article), size, number

        var result = PartColumnCatalog.Reorder(columns, ["number", "rd", "description", "size"]);

        Assert.Equal(["number", "rd", "article", "description", "size"], result.Select(c => c.Key));
    }
}
