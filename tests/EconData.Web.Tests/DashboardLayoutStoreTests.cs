using EconData.Web.Services;

namespace EconData.Web.Tests;

public class DashboardLayoutStoreTests : IDisposable
{
    private readonly string _directory;
    private readonly string _layoutPath;
    private readonly DashboardLayoutStore _store;

    public DashboardLayoutStoreTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "econdata-layout-" + Guid.NewGuid().ToString("N"));
        _layoutPath = Path.Combine(_directory, "dashboards.json");
        _store = new DashboardLayoutStore(_layoutPath);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public void Load_MissingFile_ReturnsDefaults()
    {
        var layout = _store.Load();

        Assert.Equal(DashboardLayoutStore.CurrentVersion, layout.Version);
        Assert.Equal(4, layout.Widgets.Count);
        Assert.Contains(layout.Widgets, widget => widget.Type == "series");
        Assert.Contains(layout.Widgets, widget => widget.Type == "coverage");
        Assert.Contains(layout.Widgets, widget => widget.Type == "snapshot");
        Assert.Contains(layout.Widgets, widget => widget.Type == "runs");
    }

    [Fact]
    public async Task Save_ThenLoad_RoundTrips()
    {
        var original = DashboardLayoutStore.DefaultLayout();
        var first = original.Widgets[0] with { X = 6, Y = 0, Title = "Moved widget" };
        var layout = original with { Widgets = new[] { first }.Concat(original.Widgets.Skip(1)).ToList() };

        await _store.SaveAsync(layout);

        Assert.True(File.Exists(_layoutPath));
        var loaded = _store.Load();
        Assert.Equal("Moved widget", loaded.Widgets[0].Title);
        Assert.Equal(6, loaded.Widgets[0].X);
        Assert.Equal(layout.Widgets.Count, loaded.Widgets.Count);
    }

    [Fact]
    public void Load_CorruptOrWrongVersion_ReturnsDefaults()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(_layoutPath, "{ not-json");
        Assert.Equal(4, _store.Load().Widgets.Count);

        File.WriteAllText(_layoutPath, """{"version":99,"widgets":[]}""");
        Assert.Equal(4, _store.Load().Widgets.Count);
    }

    [Fact]
    public void Validate_RejectsInvalidLayouts()
    {
        var defaults = DashboardLayoutStore.DefaultLayout();

        Assert.NotNull(DashboardLayoutStore.Validate(null));
        Assert.NotNull(DashboardLayoutStore.Validate(defaults with { Version = 2 }));

        var duplicate = defaults with
        {
            Widgets = new[] { defaults.Widgets[0], defaults.Widgets[0] }
        };
        Assert.NotNull(DashboardLayoutStore.Validate(duplicate));

        var badSize = defaults with
        {
            Widgets = new[] { defaults.Widgets[0] with { W = 0 } }
        };
        Assert.NotNull(DashboardLayoutStore.Validate(badSize));

        var badType = defaults with
        {
            Widgets = new[] { defaults.Widgets[0] with { Type = "pie" } }
        };
        Assert.NotNull(DashboardLayoutStore.Validate(badType));

        var tooMany = defaults with
        {
            Widgets = Enumerable.Range(0, DashboardLayoutStore.MaxWidgets + 1)
                .Select(index => defaults.Widgets[0] with { Id = $"w_{index}" })
                .ToList()
        };
        Assert.NotNull(DashboardLayoutStore.Validate(tooMany));

        Assert.Null(DashboardLayoutStore.Validate(defaults));
    }
}
