using System.ComponentModel.DataAnnotations;

namespace ScreenTail.Api.Data;

/// <summary>
/// An MSP. Everything else in the model hangs off one, and every query is filtered by one: a bug that
/// returned another tenant's data would be the worst failure this service could have.
/// </summary>
public sealed class Tenant
{
    public Guid Id { get; set; }

    [MaxLength(200)]
    public required string Name { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Seats bought. Activation refuses a device past this (ST-010).</summary>
    public int Seats { get; set; }

    /// <summary>Soft-deleted rather than removed, so an offboarded tenant's audit trail survives.</summary>
    public DateTimeOffset? DisabledAt { get; set; }

    public ICollection<User> Users { get; } = [];

    public ICollection<Policy> Policies { get; } = [];
}

/// <summary>A technician.</summary>
public sealed class User
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Tenant? Tenant { get; set; }

    /// <summary>
    /// Their work address, and the only personal data this service holds. Lower-cased on write so the
    /// unique index means what it looks like it means.
    /// </summary>
    [MaxLength(320)]
    public required string Email { get; set; }

    [MaxLength(200)]
    public required string DisplayName { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? DisabledAt { get; set; }

    public ICollection<Device> Devices { get; } = [];
}

/// <summary>
/// One installation of the client on one machine.
///
/// Tokens are held as a hash, never as the token: this table is what an attacker with read access to the
/// database would go for, and a stolen device token drives capture on a technician's machine.
/// </summary>
public sealed class Device
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid UserId { get; set; }

    public User? User { get; set; }

    /// <summary>What the technician sees in the seat list. A machine name, never anything captured.</summary>
    [MaxLength(200)]
    public required string Name { get; set; }

    /// <summary>SHA-256 of the refresh token, hex. Comparing hashes means a database leak is not a key ring.</summary>
    [MaxLength(64)]
    public required string TokenHash { get; set; }

    public DateTimeOffset ActivatedAt { get; set; }

    public DateTimeOffset? LastSeenAt { get; set; }

    /// <summary>Set when a technician leaves or a machine is lost. Checked on every request.</summary>
    public DateTimeOffset? RevokedAt { get; set; }
}

/// <summary>
/// Credentials for a PSA or documentation platform.
///
/// The secret itself is not here. ST-009 builds the encrypted vault; this row holds only which provider
/// a tenant has connected and whether it is working, so that a database dump is not a set of keys to a
/// customer's ConnectWise.
/// </summary>
/// <summary>
/// A PSA company mapped to a documentation-platform company (ST-097). One row per tenant per PSA name;
/// written by an exact match at publish or by a person, and read before every knowledge-base article.
/// A name, an id and a word about how it was decided; nothing from any session.
/// </summary>
public sealed class CompanyMapping
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    [MaxLength(200)]
    public required string PsaCompany { get; set; }

    [MaxLength(64)]
    public required string DocCompanyId { get; set; }

    [MaxLength(200)]
    public required string DocCompanyName { get; set; }

    /// <summary><c>exact</c> or <c>manual</c>. A likely match is never written without a person.</summary>
    [MaxLength(16)]
    public required string Confidence { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class Integration
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    [MaxLength(50)]
    public required string Provider { get; set; }

    /// <summary>Opaque reference into the vault (ST-009). Never the secret.</summary>
    /// <summary>Where the provider lives for this tenant. Not a secret.</summary>
    [MaxLength(500)]
    public string? SiteUrl { get; set; }

    /// <summary>The credential's last four characters, in the clear, so a list never has to open a row.</summary>
    [MaxLength(4)]
    public string? SecretHint { get; set; }

    // The envelope (ST-009, Vault.Envelope): the secret under a per-row data key, the data key under
    // the master key named by KeyId. Every column is safe to read; none opens without the master key.
    public byte[]? SecretCiphertext { get; set; }

    public byte[]? SecretNonce { get; set; }

    public byte[]? DataKeyWrapped { get; set; }

    public byte[]? DataKeyNonce { get; set; }

    [MaxLength(16)]
    public string? KeyId { get; set; }

    public DateTimeOffset? RotatedAt { get; set; }

    public DateTimeOffset? ConnectedAt { get; set; }

    public DateTimeOffset? LastCheckedAt { get; set; }

    [MaxLength(500)]
    public string? LastError { get; set; }
}

/// <summary>
/// The tenant's settings, enforced on the client (INV-11).
///
/// Versioned rather than mutated so a session can say which policy was in force when it was captured,
/// and so a change is visible rather than silent.
/// </summary>
public sealed class Policy
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Tenant? Tenant { get; set; }

    [MaxLength(50)]
    public required string Version { get; set; }

    /// <summary>INV-12's window. Days, and the client purges to it.</summary>
    public int RetentionDays { get; set; } = 7;

    /// <summary>INV-8. When the admin sets it, the technician cannot turn it off.</summary>
    public bool LocalOnly { get; set; }

    public bool LocalOnlyLocked { get; set; }

    /// <summary>Capture scope default: remote-tool windows only unless the admin opens it (INV-5).</summary>
    public bool CaptureAllWindows { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// One row per session, for the pilot's metrics (ST-098) and nothing else.
///
/// <b>Counts and durations only.</b> No note text, no OCR, no window titles, no frames — INV-7 says the
/// backend never persists a capture, and this is the table where that would quietly stop being true. The
/// columns are chosen so that there is nowhere to put such a thing even by accident.
/// </summary>
public sealed class SessionMetric
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    public Guid DeviceId { get; set; }

    /// <summary>The client's session id. An opaque identifier, not a name.</summary>
    [MaxLength(64)]
    public required string SessionId { get; set; }

    public DateTimeOffset StartedAt { get; set; }

    public long DurationMs { get; set; }

    public int Frames { get; set; }

    public int TranscriptSegments { get; set; }

    public long FramesPurgedUnredacted { get; set; }

    /// <summary>Share of the draft the technician changed, for the < 25% goal. A ratio, never the text.</summary>
    public double? EditRatio { get; set; }

    public bool Published { get; set; }
}

/// <summary>
/// What one drafting call cost (ST-063).
///
/// A tenant, a session id, a provider name and a number. No prompt, no draft, no frame — INV-7 holds
/// here as everywhere, and the daily cap is computed by summing this rather than by trusting a counter
/// that a restart would reset.
/// </summary>
public sealed class DraftCost
{
    public Guid Id { get; set; }

    public Guid TenantId { get; set; }

    /// <summary>The client's session id. An opaque identifier, not a name.</summary>
    [MaxLength(64)]
    public required string SessionId { get; set; }

    /// <summary>Which model answered. A name, never a key.</summary>
    [MaxLength(50)]
    public required string Provider { get; set; }

    /// <summary>US dollars. Stored exact rather than as a float, because it is money.</summary>
    public decimal CostUsd { get; set; }

    public DateTimeOffset At { get; set; }
}
