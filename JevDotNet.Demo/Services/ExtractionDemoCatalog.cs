using System.Collections.ObjectModel;
using ExtractionsDotNet;

namespace JevDotNet.Demo.Services;

public sealed record ExtractionDemo(string Model, IReadOnlyDictionary<string, ExtractionField> Fields);

public sealed record ExtractionDemoAsset(string Key, string FileName, ExtractionAssetFormat Format)
{
    public string Path => $"extraction-samples/{FileName}";
}

public static class ExtractionDemoCatalog
{
    private const string Clairic = "cloudflare/clairic";

    public const string DispatchSample = """
        🧭 North Ridge Survey — field dispatch
        Filed at 2026-07-14T18:45:00-06:00. The expedition departs on 2026-07-16.
        First radio check: 06:30 local time. Expected travel time: 5 hours 30 minutes.

        Packed 42 supply crates. Crew: Mira Chen, Luca Reyes, Ada Okafor.
        Resupply dates: 2026-07-19, 2026-07-23, 2026-07-28.

        Water use (liters per day), oldest to latest: 21.5, 23.0, 24.5, 26.0, 27.5.
        Each day has been warmer than the last. The weather bulletin calls for the same warming trend over the next three days, with no change in crew size.
        """;

    public static ExtractionDemo Dispatch { get; } = Create(Clairic, new()
    {
        ["expedition_name"] = new TextField { Instructions = "Name of the expedition at the top of the dispatch." },
        ["supply_crates"] = new NumberField { Integer = true, Instructions = "Number of supply crates packed." },
        ["departure_date"] = new DateField { Instructions = "Calendar date on which the expedition departs." },
        ["radio_check"] = new TimeField { Instructions = "Local time of the first radio check." },
        ["filed_at"] = new DateTimeField { Instructions = "Timestamp at which the dispatch was filed, including the stated offset." },
        ["travel_duration"] = new DurationField { Unit = DurationUnit.Hours, Decimals = 2, Instructions = "Expected travel duration in hours." },
        ["crew"] = new ListField(ListItemType.Text) { Instructions = "Names of the crew members." },
        ["water_history"] = new ListField(ListItemType.Number) { Unit = "liters", Decimals = 1, Instructions = "Observed daily water use, oldest to latest." },
        ["resupply_dates"] = new ListField(ListItemType.Date) { Instructions = "Scheduled resupply dates in order." },
        ["next_day_water"] = new EstimateField(0, 80) { Unit = "liters", Step = 0.5, Instructions = "Estimate water use in liters on the next day given the observations and weather bulletin." },
        ["water_outlook"] = new ForecastField(0, 80, 3, ForecastInterval.Day) { Unit = "liters", Step = 0.5, Instructions = "Forecast water use in liters for each of the next three days after the last observation." }
    });

    public static ExtractionDemo CargoTag { get; } = Create(Clairic, new()
    {
        ["crate_id"] = new TextField { Instructions = "The printed crate ID." },
        ["gross_weight"] = new NumberField { Unit = "kg", Decimals = 2, Instructions = "The printed gross weight in kilograms." },
        ["inspection_date"] = new DateField { Instructions = "The printed inspection date." },
        ["contents"] = new ListField(ListItemType.Text) { Instructions = "Each item on its own line under Contents." }
    });

    public static IReadOnlyList<ExtractionDemoAsset> CargoFormats { get; } = Array.AsReadOnly<ExtractionDemoAsset>(
    [
        new("cargo-tag", "cargo-tag.png", ExtractionAssetFormat.Png),
        new("cargo-tag", "cargo-tag.jpg", ExtractionAssetFormat.Jpeg),
        new("cargo-tag", "cargo-tag.webp", ExtractionAssetFormat.Webp)
    ]);

    public static ExtractionDemo Permit { get; } = Create("scaledown/extract", new()
    {
        ["permit_id"] = new TextField { Instructions = "The alphanumeric expedition permit identifier." },
        ["issuing_office"] = new TextField { Instructions = "The name of the office listed as the issuing office." },
        ["approved_activities"] = new ListField(ListItemType.Text) { Instructions = "Individual activities listed under Approved Activities." },
        ["emergency_phone"] = new TextField { Instructions = "The emergency contact phone number, if printed on the permit." }
    });

    public static ExtractionDemoAsset PermitFile { get; } =
        new("expedition-permit", "north-ridge-permit.pdf", ExtractionAssetFormat.Pdf);

    private static ExtractionDemo Create(string model, Dictionary<string, ExtractionField> fields) =>
        new(model, new ReadOnlyDictionary<string, ExtractionField>(fields));
}
