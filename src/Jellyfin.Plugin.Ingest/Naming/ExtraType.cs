namespace Jellyfin.Plugin.Ingest.Naming;

/// <summary>
/// Kinds of extra content Jellyfin recognises by folder name.
/// </summary>
public enum ExtraType
{
    /// <summary>Not an extra.</summary>
    None = 0,

    /// <summary>Trailer or teaser.</summary>
    Trailer,

    /// <summary>Featurette.</summary>
    Featurette,

    /// <summary>Deleted or extended scene.</summary>
    DeletedScene,

    /// <summary>Behind the scenes / making of.</summary>
    BehindTheScenes,

    /// <summary>Interview.</summary>
    Interview,

    /// <summary>Short / webisode.</summary>
    ShortFilm,

    /// <summary>Clip.</summary>
    Clip,

    /// <summary>Scene.</summary>
    Scene,

    /// <summary>Sample; never filed into a library.</summary>
    Sample,

    /// <summary>Any other extra.</summary>
    Other,
}
