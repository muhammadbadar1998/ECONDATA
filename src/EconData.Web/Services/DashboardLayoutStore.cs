using System.Text.Json;

namespace EconData.Web.Services;

public sealed class DashboardLayoutStore
{
    public const int CurrentVersion = 1;
    public const int MaxWidgets = 24;
    public const int GridColumns = 12;

    public static readonly IReadOnlySet<string> AllowedTypes =
        new HashSet<string>(StringComparer.Ordinal) { "series", "coverage", "snapshot", "runs" };

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly SemaphoreSlim _gate = new(1, 1);

    public DashboardLayoutStore(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        FilePath = filePath;
    }

    public string FilePath { get; }

    public DashboardLayout Load()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                return DefaultLayout();
            }

            var layout = JsonSerializer.Deserialize<DashboardLayout>(File.ReadAllText(FilePath), JsonOptions);
            return Validate(layout) is null ? layout! : DefaultLayout();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return DefaultLayout();
        }
    }

    public async Task SaveAsync(DashboardLayout layout, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(layout);
        var error = Validate(layout);
        if (error is not null)
        {
            throw new ArgumentException(error, nameof(layout));
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var directory = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var tempPath = FilePath + ".tmp";
            await File.WriteAllTextAsync(tempPath, JsonSerializer.Serialize(layout, JsonOptions), cancellationToken).ConfigureAwait(false);
            File.Move(tempPath, FilePath, overwrite: true);
        }
        finally
        {
            _gate.Release();
        }
    }

    public static string? Validate(DashboardLayout? layout)
    {
        if (layout is null)
        {
            return "Layout is required.";
        }

        if (layout.Version != CurrentVersion)
        {
            return $"Unsupported layout version: {layout.Version}.";
        }

        if (layout.Widgets is null || layout.Widgets.Count == 0)
        {
            return "At least one widget is required.";
        }

        if (layout.Widgets.Count > MaxWidgets)
        {
            return $"At most {MaxWidgets} widgets are allowed.";
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var widget in layout.Widgets)
        {
            if (widget is null)
            {
                return "Widget entries cannot be null.";
            }

            if (string.IsNullOrWhiteSpace(widget.Id) || widget.Id.Length > 64)
            {
                return "Widget id must be between 1 and 64 characters.";
            }

            if (!ids.Add(widget.Id))
            {
                return $"Duplicate widget id: {widget.Id}.";
            }

            if (!AllowedTypes.Contains(widget.Type ?? string.Empty))
            {
                return $"Unknown widget type: {widget.Type}.";
            }

            if (widget.Title is null || widget.Title.Length > 120)
            {
                return "Widget title must be at most 120 characters.";
            }

            if (widget.X is < 0 or >= GridColumns || widget.Y is < 0 or > 1000)
            {
                return $"Widget '{widget.Id}' has an invalid position.";
            }

            if (widget.W is < 1 or > GridColumns || widget.H is < 1 or > GridColumns)
            {
                return $"Widget '{widget.Id}' has an invalid size.";
            }

            if (widget.Config is null)
            {
                return $"Widget '{widget.Id}' is missing its config.";
            }

            if (TooLong(widget.Config.Entity) || TooLong(widget.Config.Metric) || TooLong(widget.Config.Category))
            {
                return $"Widget '{widget.Id}' has an invalid config value.";
            }
        }

        return null;
    }

    public static DashboardLayout DefaultLayout() => new(
        CurrentVersion,
        new[]
        {
            new DashboardWidget(
                "w_series", "series", "USA - GDP (current US$)", 0, 0, 6, 3,
                new DashboardWidgetConfig("USA", "gdp_usd", null, null, null, null, null, null)),
            new DashboardWidget(
                "w_coverage", "coverage", "Observations by metric", 6, 0, 6, 3,
                new DashboardWidgetConfig(null, null, null, null, null, null, null, null)),
            new DashboardWidget(
                "w_snapshot", "snapshot", "USA - latest values", 0, 3, 6, 3,
                new DashboardWidgetConfig("USA", null, null, null, null, null, null, null)),
            new DashboardWidget(
                "w_runs", "runs", "Execution seconds per run", 6, 3, 6, 3,
                new DashboardWidgetConfig(null, null, null, null, null, null, null, null)),
        });

    private static bool TooLong(string? value) => value is { Length: > 64 };
}
