using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using SportsVenueApi.DTOs.Venues;

namespace SportsVenueApi.Models;

[Table("venues")]
public class Venue
{
    [Key]
    [Column("id")]
    [MaxLength(32)]
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];

    [Column("name")]
    [MaxLength(255)]
    public string Name { get; set; } = "";

    [Column("name_ar")]
    [MaxLength(255)]
    public string? NameAr { get; set; }

    [Column("owner_id")]
    [MaxLength(32)]
    public string OwnerId { get; set; } = "";

    [Column("sports", TypeName = "longtext")]
    public string SportsJson { get; set; } = "[]";

    [Column("city")]
    [MaxLength(255)]
    public string? City { get; set; }

    [Column("city_ar")]
    [MaxLength(255)]
    public string? CityAr { get; set; }

    [Column("address")]
    [MaxLength(500)]
    public string? Address { get; set; }

    [Column("address_ar")]
    [MaxLength(500)]
    public string? AddressAr { get; set; }

    [Column("price_per_hour")]
    public double PricePerHour { get; set; }

    [Column("status")]
    [MaxLength(50)]
    public string Status { get; set; } = "pending";

    [Column("description", TypeName = "text")]
    public string? Description { get; set; }

    [Column("description_ar", TypeName = "text")]
    public string? DescriptionAr { get; set; }

    [Column("images", TypeName = "json")]
    public string ImagesJson { get; set; } = "[]";

    [Column("latitude")]
    public double? Latitude { get; set; }

    [Column("longitude")]
    public double? Longitude { get; set; }

    [Column("cliq_alias")]
    [MaxLength(255)]
    public string? CliqAlias { get; set; }

    [Column("operating_hours", TypeName = "json")]
    public string OperatingHoursJson { get; set; } = "{}";

    [Column("min_booking_duration")]
    public int MinBookingDuration { get; set; } = 60;

    [Column("max_booking_duration")]
    public int MaxBookingDuration { get; set; } = 180;

    [Column("deposit_percentage")]
    public double DepositPercentage { get; set; } = 20.0;

    /// <summary>
    /// Cancelling at least this many hours before the start refunds what was paid; later than
    /// that, it is kept. 0 = always free to cancel.
    /// </summary>
    [Column("free_cancel_hours")]
    public int FreeCancelHours { get; set; } = 24;

    [Column("parent_size")]
    [MaxLength(8)]
    public string? ParentSize { get; set; }

    [Column("sub_sizes", TypeName = "longtext")]
    public string SubSizesJson { get; set; } = "[]";

    [Column("size_prices", TypeName = "longtext")]
    public string SizePricesJson { get; set; } = "{}";

    // Per-sport configuration. When a venue offers multiple sports and each
    // sport has its own price / schedule (optionally split config for football),
    // this holds that map. Keyed by sport name. Empty object = legacy single-
    // sport mode — fall back to the venue-level PricePerHour / OperatingHours.
    [Column("sports_config", TypeName = "longtext")]
    public string SportsConfigJson { get; set; } = "{}";

    // When true, bookings for different sports don't collide with each other
    // (e.g. one football pitch + one basketball court side-by-side). Default
    // false = all sports share the physical space — any booking blocks every
    // sport at that time.
    [Column("sports_isolated")]
    /// <summary>
    /// DEAD. Nothing reads this — it was persisted, echoed in the DTO and auto-assigned by
    /// the venue form (as "this venue has more than one sport"), but no capacity, availability
    /// or booking rule ever consulted it. The API surface and the client write are gone.
    ///
    /// The column stays because dropping it would be this project's first destructive
    /// migration, and an unread nullable column costs nothing. Do not start using it without
    /// deciding what it means first — the old auto-assigned values are meaningless.
    /// </summary>
    public bool SportsIsolated { get; set; } = false;

    // Multi-pitch venues: a JSON array of physically independent pitches, each
    // with its own sport/size/price/(optional) operating hours. Empty = legacy
    // single-pitch venue — resolved at read time via PitchSizes.ResolvedPitches(v)
    // which synthesises one implicit pitch per entry in Sports. See PitchDto.
    [Column("pitches", TypeName = "longtext")]
    public string PitchesJson { get; set; } = "[]";

    /// <summary>Ids of the catalog features this venue offers. See <see cref="VenueFeature"/>.</summary>
    [Column("feature_ids", TypeName = "longtext")]
    public string FeatureIdsJson { get; set; } = "[]";

    /// <summary>Features the owner typed that are not in the catalog. Private to this venue.</summary>
    [Column("custom_features", TypeName = "longtext")]
    public string CustomFeaturesJson { get; set; } = "[]";

    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [ForeignKey("OwnerId")]
    public User Owner { get; set; } = null!;

    [NotMapped]
    public List<string> Sports
    {
        get => System.Text.Json.JsonSerializer.Deserialize<List<string>>(SportsJson) ?? [];
        set => SportsJson = System.Text.Json.JsonSerializer.Serialize(value);
    }

    // Like every JSON accessor here, these build a new list on each read: assign the list
    // back after changing it, or the change is lost.
    [NotMapped]
    public List<string> FeatureIds
    {
        get => string.IsNullOrWhiteSpace(FeatureIdsJson)
            ? []
            : System.Text.Json.JsonSerializer.Deserialize<List<string>>(FeatureIdsJson) ?? [];
        set => FeatureIdsJson = System.Text.Json.JsonSerializer.Serialize(value ?? []);
    }

    [NotMapped]
    public List<string> CustomFeatures
    {
        get => string.IsNullOrWhiteSpace(CustomFeaturesJson)
            ? []
            : System.Text.Json.JsonSerializer.Deserialize<List<string>>(CustomFeaturesJson) ?? [];
        set => CustomFeaturesJson = System.Text.Json.JsonSerializer.Serialize(value ?? []);
    }

    [NotMapped]
    public List<string> Images
    {
        get => System.Text.Json.JsonSerializer.Deserialize<List<string>>(ImagesJson) ?? [];
        set => ImagesJson = System.Text.Json.JsonSerializer.Serialize(value);
    }

    [NotMapped]
    public Dictionary<string, object>? OperatingHours
    {
        get => System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object>>(OperatingHoursJson);
        set => OperatingHoursJson = System.Text.Json.JsonSerializer.Serialize(value ?? new Dictionary<string, object>());
    }

    [NotMapped]
    public List<string> SubSizes
    {
        // Tolerate legacy rows that landed with empty-string or null JSON after
        // the AddSubdividablePitch migration (MySQL defaults longtext to "").
        get => string.IsNullOrWhiteSpace(SubSizesJson)
            ? []
            : System.Text.Json.JsonSerializer.Deserialize<List<string>>(SubSizesJson) ?? [];
        set => SubSizesJson = System.Text.Json.JsonSerializer.Serialize(value);
    }

    [NotMapped]
    public Dictionary<string, double> SizePrices
    {
        get => string.IsNullOrWhiteSpace(SizePricesJson)
            ? []
            : System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, double>>(SizePricesJson) ?? [];
        set => SizePricesJson = System.Text.Json.JsonSerializer.Serialize(value);
    }

    [NotMapped]
    public Dictionary<string, SportConfigDto> SportsConfig
    {
        get => string.IsNullOrWhiteSpace(SportsConfigJson)
            ? []
            : System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, SportConfigDto>>(SportsConfigJson) ?? [];
        set => SportsConfigJson = System.Text.Json.JsonSerializer.Serialize(value ?? new Dictionary<string, SportConfigDto>());
    }

    [NotMapped]
    public List<PitchDto> Pitches
    {
        get => string.IsNullOrWhiteSpace(PitchesJson)
            ? []
            : System.Text.Json.JsonSerializer.Deserialize<List<PitchDto>>(PitchesJson) ?? [];
        set => PitchesJson = System.Text.Json.JsonSerializer.Serialize(value ?? new List<PitchDto>());
    }
}
