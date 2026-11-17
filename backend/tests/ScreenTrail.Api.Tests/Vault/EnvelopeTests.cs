using System.Security.Cryptography;
using System.Text;
using ScreenTrail.Api.Vault;

namespace ScreenTrail.Api.Tests.Vault;

/// <summary>
/// The envelope (ST-009): a per-record data key seals the secret, and the master key seals the data key.
///
/// Two layers so that rotating the master key is a rewrap of a few dozen bytes per row rather than a
/// re-encryption of every secret, and so that a cloud KMS can take over the outer layer later without
/// touching the rows. The master key is configuration today; the shape is the KMS's.
/// </summary>
public sealed class EnvelopeTests
{
    private static readonly byte[] Master = RandomNumberGenerator.GetBytes(32);
    private static readonly byte[] Secret = Encoding.UTF8.GetBytes("acme+PUBLIC:PRIVATE-9876");

    [Fact]
    public void WhatIsSealedComesBackAndNothingElseDoes()
    {
        var sealed_ = Envelope.Seal(Secret, Master);

        Assert.Equal(Secret, Envelope.Open(sealed_, Master));
        Assert.DoesNotContain(Secret, sealed_.Ciphertext);
        Assert.DoesNotContain(Secret, sealed_.WrappedKey);
    }

    [Fact]
    public void AnotherMasterKeyCannotOpenIt()
    {
        var sealed_ = Envelope.Seal(Secret, Master);

        Assert.ThrowsAny<CryptographicException>(() => Envelope.Open(sealed_, RandomNumberGenerator.GetBytes(32)));
    }

    [Fact]
    public void ATamperedByteIsRefusedNotDecryptedToGarbage()
    {
        // AES-GCM authenticates; a bit flipped anywhere in the ciphertext fails the open rather than
        // yielding a credential with one wrong character that a provider would then send.
        var sealed_ = Envelope.Seal(Secret, Master);
        var tampered = sealed_ with { Ciphertext = (byte[])sealed_.Ciphertext.Clone() };
        tampered.Ciphertext[3] ^= 0x01;

        Assert.ThrowsAny<CryptographicException>(() => Envelope.Open(tampered, Master));
    }

    [Fact]
    public void SealingTheSameSecretTwiceNeverLooksTheSame()
    {
        // A fresh data key and nonce each time, so two tenants with the same API key do not share a row
        // that says so.
        var first = Envelope.Seal(Secret, Master);
        var second = Envelope.Seal(Secret, Master);

        Assert.NotEqual(first.Ciphertext, second.Ciphertext);
        Assert.NotEqual(first.WrappedKey, second.WrappedKey);
    }

    [Fact]
    public void RewrappingUnderANewKeyLeavesThePayloadAlone()
    {
        // Rotation is this and nothing more: the row's secret bytes are untouched, only the small
        // wrapped key changes hands, and the old master key stops working for the row.
        var next = RandomNumberGenerator.GetBytes(32);
        var sealed_ = Envelope.Seal(Secret, Master);

        var rewrapped = Envelope.Rewrap(sealed_, Master, next);

        Assert.Equal(sealed_.Ciphertext, rewrapped.Ciphertext);
        Assert.Equal(sealed_.Nonce, rewrapped.Nonce);
        Assert.Equal(Envelope.KeyIdOf(next), rewrapped.KeyId);
        Assert.Equal(Secret, Envelope.Open(rewrapped, next));
        Assert.ThrowsAny<CryptographicException>(() => Envelope.Open(rewrapped, Master));
    }

    [Fact]
    public void ABoundSecretOpensOnlyWithItsOwnBinding()
    {
        // 2026-11-15 review: the tenant, provider and host are associated data, so the same sealed bytes
        // under the right key still refuse to open for any other row, or with no binding at all.
        var tenant = Guid.NewGuid();
        var binding = Envelope.BindingFor(tenant, "connectwise", "https://na.myconnectwise.net");
        var sealed_ = Envelope.Seal(Secret, Master, binding);

        Assert.Equal(Secret, Envelope.Open(sealed_, Master, binding));
        Assert.Equal(Secret, Envelope.Open(sealed_, Master, Envelope.BindingFor(tenant, "connectwise", "HTTPS://NA.myconnectwise.net/v4_6_release/")));
        Assert.ThrowsAny<CryptographicException>(() => Envelope.Open(sealed_, Master));
        Assert.ThrowsAny<CryptographicException>(() => Envelope.Open(sealed_, Master, Envelope.BindingFor(Guid.NewGuid(), "connectwise", "https://na.myconnectwise.net")));
        Assert.ThrowsAny<CryptographicException>(() => Envelope.Open(sealed_, Master, Envelope.BindingFor(tenant, "hudu", "https://na.myconnectwise.net")));
        Assert.ThrowsAny<CryptographicException>(() => Envelope.Open(sealed_, Master, Envelope.BindingFor(tenant, "connectwise", "https://evil.example.com")));
        Assert.ThrowsAny<CryptographicException>(() => Envelope.Open(sealed_, Master, Envelope.BindingFor(tenant, "connectwise", "https://na.myconnectwise.net:8443")));
    }

    [Fact]
    public void RewrappingKeepsTheBinding()
    {
        var next = RandomNumberGenerator.GetBytes(32);
        var binding = Envelope.BindingFor(Guid.NewGuid(), "hudu", "https://acme.huducloud.com");
        var rewrapped = Envelope.Rewrap(Envelope.Seal(Secret, Master, binding), Master, next);

        Assert.Equal(Secret, Envelope.Open(rewrapped, next, binding));
        Assert.ThrowsAny<CryptographicException>(() => Envelope.Open(rewrapped, next));
    }

    [Fact]
    public void AKeyIdIsNotTheKey()
    {
        // Stored beside every row so rotation knows which master key wrapped it. Short, one-way, and the
        // same for the same key on every machine.
        var id = Envelope.KeyIdOf(Master);

        Assert.Equal(16, id.Length);
        Assert.Equal(id, Envelope.KeyIdOf((byte[])Master.Clone()));
        Assert.DoesNotContain(Convert.ToHexString(Master), id, StringComparison.OrdinalIgnoreCase);
    }
}
