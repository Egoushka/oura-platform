namespace OuraPlatform.Oura.Models;

public sealed record RingConfiguration(
    string Id,
    // brushed_silver | glossy_black | ... | deep_rose
    string? Color,
    // heritage | balance | balance_diamond | horizon | ceramic
    string? Design,
    // gen1 | gen2 | gen2m | gen3 | gen4 | or5
    string? HardwareType,
    string? FirmwareVersion,
    int? Size,
    DateTimeOffset? SetUpAt);

/// <summary>Singleton document. Has no sandbox equivalent — the sandbox path 404s.</summary>
public sealed record PersonalInfo(
    string Id,
    int? Age,
    double? Weight,
    double? Height,
    string? BiologicalSex,
    string? Email);
