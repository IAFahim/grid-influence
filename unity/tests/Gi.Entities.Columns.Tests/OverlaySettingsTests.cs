using System.Text;
using Xunit;

namespace Gi.Entities.Columns.Tests;

public sealed class OverlaySettingsTests
{
    [Fact]
    public void TheJsonIsSortedInvariantAndStable()
    {
        var settings = new GiOverlaySettings { Heatmap = false, Footprints = true, ReachDiscs = true, GridBounds = false, Grid = 2, Layer = 5, ValueScale = 12.5f };
        var json = settings.ToJson();
        Assert.Equal(
            "{\n  \"footprints\": true,\n  \"grid\": 2,\n  \"gridBounds\": false,\n  \"heatmap\": false,\n  \"layer\": 5,\n  \"reachDiscs\": true,\n  \"valueScale\": 12.5\n}\n",
            json);
        Assert.Equal(json, settings.ToJson());
        Assert.DoesNotContain("\r", json);
        Assert.Equal(json, GiOverlaySettings.FromJson(json).ToJson());
        Assert.Equal(json.Length, Encoding.UTF8.GetByteCount(json));
    }

    [Fact]
    public void LoadingToleratesMissingUnknownAndBadValues()
    {
        var defaults = new GiOverlaySettings().ToJson();
        Assert.Equal(defaults, GiOverlaySettings.FromJson(null).ToJson());
        Assert.Equal(defaults, GiOverlaySettings.FromJson("").ToJson());
        Assert.Equal(defaults, GiOverlaySettings.FromJson("not json at all").ToJson());

        var loaded = GiOverlaySettings.FromJson("{\n  \"future\": 3,\n  \"grid\": -4,\n  \"layer\": 7,\n  \"valueScale\": 0,\n  \"heatmap\": false\n}\n");
        Assert.False(loaded.Heatmap);
        Assert.Equal(0, loaded.Grid);
        Assert.Equal(7, loaded.Layer);
        Assert.Equal(64f, loaded.ValueScale);
        Assert.True(loaded.GridBounds);
    }
}
