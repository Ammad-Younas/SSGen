using SkiaSharp;
using SSGen.Agent.Graph;
using SSGen.Agent.Observation;

namespace SSGen.Agent.Tests;

public class UiHierarchyParserTests
{
    private const string Xml = """
        <?xml version='1.0' encoding='UTF-8' standalone='yes' ?>
        <hierarchy rotation="0">
          <node class="android.widget.FrameLayout" package="com.app" enabled="true" bounds="[0,0][1080,2400]">
            <node class="android.widget.TextView" text="Notes" package="com.app" enabled="true" clickable="false" bounds="[40,100][400,180]" />
            <node class="android.widget.EditText" text="" resource-id="com.app:id/search" package="com.app" enabled="true" clickable="true" focusable="true" bounds="[40,200][1040,300]" />
            <node class="androidx.recyclerview.widget.RecyclerView" resource-id="com.app:id/list" package="com.app" enabled="true" scrollable="true" bounds="[0,320][1080,2100]">
              <node class="android.view.ViewGroup" package="com.app" enabled="true" clickable="true" bounds="[0,320][1080,520]">
                <node class="android.widget.TextView" text="Shopping list" package="com.app" enabled="true" bounds="[40,340][800,400]" />
              </node>
            </node>
            <node class="android.widget.FrameLayout" package="com.app" enabled="true" clickable="true" bounds="[0,2200][540,2400]">
              <node class="android.widget.TextView" text="Home" resource-id="com.app:id/nav_home" selected="true" package="com.app" enabled="true" clickable="true" bounds="[0,2200][540,2400]" />
            </node>
            <node class="android.widget.Button" text="Disabled" package="com.app" enabled="false" clickable="true" bounds="[0,0][10,10]" />
            <node class="android.widget.TextView" text="12:30" package="com.android.systemui" enabled="true" clickable="true" bounds="[0,0][100,40]" />
          </node>
        </hierarchy>
        """;

    [Fact]
    public void Extracts_interactive_elements_in_reading_order()
    {
        var ui = UiHierarchyParser.Parse(Xml);

        Assert.Equal("com.app", ui.RootPackage);
        Assert.Equal(["search", "list", "Shopping list", "Home"], ui.Elements.Select(e => e.Label));
        Assert.Equal([1, 2, 3, 4], ui.Elements.Select(e => e.Id));
        Assert.True(ui.Elements[0].Editable);
        Assert.True(ui.Elements[1].Scrollable);
        Assert.True(ui.Elements[3].Selected);
        Assert.DoesNotContain(ui.Elements, e => e.Label == "Disabled" || e.Label == "12:30");
        Assert.Contains("Notes", ui.AllText);
        Assert.Equal("[4] TextView 'Home' (selected)", ui.Elements[3].ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("<not xml")]
    public void Bad_input_is_empty(string? xml) => Assert.Same(UiSnapshot.Empty, UiHierarchyParser.Parse(xml));
}

public class FingerprintTests
{
    private static byte[] Png(SKColor background, int stripeY = -1, SKColor? stripe = null)
    {
        using var surface = SKSurface.Create(new SKImageInfo(270, 600));
        surface.Canvas.Clear(background);
        using var paint = new SKPaint { Color = stripe ?? SKColors.Black };
        surface.Canvas.DrawRect(0, 300, 270, 80, paint);
        if (stripeY >= 0)
        {
            surface.Canvas.DrawRect(0, stripeY, 135, 40, paint);
        }

        using var img = surface.Snapshot();
        return img.Encode(SKEncodedImageFormat.Png, 100).ToArray();
    }

    private static UiSnapshot Ui(string selected, params string[] ids) => new(
        ids.Select((id, i) => new UiElement
        {
            Id = i + 1, Class = "android.widget.Button", ResourceId = "com.app:id/" + id, Clickable = true,
            Bounds = new UiBounds(0, i * 100, 100, (i * 100) + 80), Selected = id == selected, Text = id,
        }).ToList(), [], "com.app");

    [Fact]
    public void Status_bar_changes_do_not_change_identity()
    {
        var a = ScreenFingerprint.DifferenceHash(Png(SKColors.White, stripeY: 5, stripe: SKColors.Black));
        var b = ScreenFingerprint.DifferenceHash(Png(SKColors.White, stripeY: 10, stripe: SKColors.Red));
        Assert.True(ScreenFingerprint.Hamming(a, b) <= 2);
    }

    /// <summary>Distinct layouts: blocks at different positions, so dHash genuinely differs (it ignores plain colour).</summary>
    private static byte[] Busy(int seed)
    {
        // Seeded pseudo-random "UI": many small dark rectangles, like text lines and icons on a real screen.
        var rng = new Random(seed);
        using var surface = SKSurface.Create(new SKImageInfo(270, 600));
        surface.Canvas.Clear(SKColors.White);
        using var paint = new SKPaint { Color = SKColors.Black };
        for (var i = 0; i < 60; i++)
        {
            surface.Canvas.DrawRect(rng.Next(0, 250), rng.Next(40, 580), rng.Next(10, 60), rng.Next(8, 30), paint);
        }

        using var img = surface.Snapshot();
        return img.Encode(SKEncodedImageFormat.Png, 100).ToArray();
    }

    [Fact]
    public void Same_structure_with_different_data_matches_but_other_tab_does_not()
    {
        var homePng = Busy(1);
        var otherDataPng = Busy(2);
        Assert.True(ScreenFingerprint.Hamming(ScreenFingerprint.DifferenceHash(homePng), ScreenFingerprint.DifferenceHash(otherDataPng)) > 6);

        var home = ScreenFingerprint.From(".Main", homePng, Ui("tab1", "tab1", "tab2", "row", "row", "fab"));
        var homeOtherData = ScreenFingerprint.From(".Main", otherDataPng, Ui("tab1", "tab1", "tab2", "row", "row", "fab"));
        var otherTab = ScreenFingerprint.From(".Main", otherDataPng, Ui("tab2", "tab1", "tab2", "row", "row", "fab"));
        var otherActivity = ScreenFingerprint.From(".Settings", Png(SKColors.White), Ui("tab1", "tab1", "tab2", "row", "row", "fab"));

        Assert.True(home.Matches(homeOtherData));
        Assert.False(home.Matches(otherTab));
        Assert.False(home.Matches(otherActivity));
    }

    [Fact]
    public void Graph_deduplicates_and_records_edges()
    {
        var graph = new ScreenGraph();
        var fp = ScreenFingerprint.From(".Main", Png(SKColors.White), Ui("a", "a", "b", "c"));
        var (n1, new1) = graph.Observe(fp);
        var (n1Again, new2) = graph.Observe(fp);
        var (n2, new3) = graph.Observe(ScreenFingerprint.From(".Detail", Png(SKColors.Gray), Ui("x", "x", "y", "z")));

        Assert.True(new1);
        Assert.False(new2);
        Assert.Same(n1, n1Again);
        Assert.Equal(2, n1.Visits);
        Assert.True(new3);

        Assert.NotNull(graph.AddEdge(n1.Id, n2.Id, "tap a"));
        Assert.Null(graph.AddEdge(n1.Id, n2.Id, "tap a"));
        Assert.Null(graph.AddEdge(n1.Id, n1.Id, "tap self"));
        Assert.Single(graph.Edges);
    }

    [Fact]
    public void Routes_lead_to_the_nearest_unfinished_screen_with_a_revisit_cap()
    {
        var graph = new ScreenGraph();
        ScreenNode Node(ulong hash, params string[] known)
        {
            var (n, _) = graph.Observe(new ScreenFingerprint(".A" + hash, hash, new HashSet<string>(), new HashSet<string>()));
            n.Known.UnionWith(known);
            n.Tried.UnionWith(known);
            return n;
        }

        var home = Node(1, "menu", "list");
        var drawer = Node(2, "settings");
        var settings = Node(3, "toggle");
        graph.AddEdge(home.Id, drawer.Id, "tap menu", "menu");
        graph.AddEdge(drawer.Id, settings.Id, "tap settings", "settings");
        graph.AddEdge(settings.Id, home.Id, "back");
        var onScreen = new HashSet<string> { "menu", "list" };

        Assert.Null(graph.FirstHopToUnfinished(home, onScreen)); // everything finished

        settings.Known.Add("about"); // two hops away, still has work
        Assert.Equal("menu", graph.FirstHopToUnfinished(home, onScreen)?.ElementKey);
        Assert.Null(graph.FirstHopToUnfinished(home, new HashSet<string> { "list" })); // first hop not on screen

        home.Revisits["menu"] = 3;
        Assert.Null(graph.FirstHopToUnfinished(home, onScreen)); // loop guard
    }
}
