using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace SportsVenueApi.Models;

/// <summary>
/// One entry in the platform-wide catalog of venue features — parking, showers, floodlights.
///
/// Curated by super_admin. Owners pick from it when describing their venue, and players filter
/// by it in the app. A venue records the ids it chose in <see cref="Venue.FeatureIds"/>; labels an
/// owner types that are not in the catalog stay on that one venue as
/// <see cref="Venue.CustomFeatures"/> and never enter this table.
/// </summary>
[Table("venue_features")]
public class VenueFeature
{
    /// <summary>
    /// A readable slug ("vf-parking"), minted once from the English name and never changed.
    /// Venues store it, so renaming a feature must not orphan them.
    /// </summary>
    [Key]
    [Column("id")]
    [MaxLength(40)]
    public string Id { get; set; } = "";

    [Column("name_en")]
    [MaxLength(60)]
    public string NameEn { get; set; } = "";

    [Column("name_ar")]
    [MaxLength(60)]
    public string NameAr { get; set; } = "";

    /// <summary>One of <see cref="Constants.VenueFeatureIcons.All"/>.</summary>
    [Column("icon")]
    [MaxLength(40)]
    public string Icon { get; set; } = "";

    [Column("sort_order")]
    public int SortOrder { get; set; }

    /// <summary>
    /// Retired features are hidden from the owner's picker and the app's filter, but venues that
    /// already chose one keep showing it — it is still true of them. A feature can only be
    /// deleted outright once no venue uses it.
    /// </summary>
    [Column("is_active")]
    public bool IsActive { get; set; } = true;

    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [Column("updated_at")]
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
