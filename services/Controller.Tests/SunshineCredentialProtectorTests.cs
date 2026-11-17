using KamenRaider.Controller;
using KamenRaider.Controller.Security;
using System.Security.Cryptography;

namespace Controller.Tests;

public class SunshineCredentialProtectorTests
{
    private static string NewKey() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    private static ControllerOptions ValidOptions(string sunshineCredentialKey) => new()
    {
        InternalUrl = "http://127.0.0.1:5081",
        AgentUrl = "https://localhost:7443",
        DatabasePath = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "unused-sunshine-options.db")),
        ServiceCredential = "test",
        SunshineCredentialKey = sunshineCredentialKey,
    };

    [Fact]
    public void ControllerOptionsAcceptsOnlyAnEmptyOrExactly32ByteBase64Key()
    {
        Assert.True(ValidOptions("").IsValid());
        Assert.True(ValidOptions(NewKey()).IsValid());
        Assert.False(ValidOptions("not-base64").IsValid());
        Assert.False(ValidOptions(Convert.ToBase64String(RandomNumberGenerator.GetBytes(31))).IsValid());
        Assert.False(ValidOptions(Convert.ToBase64String(RandomNumberGenerator.GetBytes(33))).IsValid());
    }

    [Fact]
    public void EncryptionRoundtripUsesARandomNonceAndRejectsTampering()
    {
        var protector = new SunshineCredentialProtector(NewKey());
        var first = protector.Protect("Fleet-Secret!");
        var second = protector.Protect("Fleet-Secret!");

        Assert.Equal(12, first.Nonce.Length);
        Assert.Equal(16, first.Tag.Length);
        Assert.False(first.Nonce.SequenceEqual(second.Nonce));
        Assert.False(first.Ciphertext.SequenceEqual(second.Ciphertext));
        Assert.Equal("Fleet-Secret!", protector.Unprotect(first.Ciphertext, first.Nonce, first.Tag));

        var tamperedCiphertext = (byte[])first.Ciphertext.Clone();
        tamperedCiphertext[0] ^= 0xFF;
        Assert.Throws<InvalidOperationException>(() => protector.Unprotect(tamperedCiphertext, first.Nonce, first.Tag));

        var tamperedTag = (byte[])first.Tag.Clone();
        tamperedTag[0] ^= 0xFF;
        Assert.Throws<InvalidOperationException>(() => protector.Unprotect(first.Ciphertext, first.Nonce, tamperedTag));

        Assert.Throws<InvalidOperationException>(() => protector.Unprotect(first.Ciphertext, new byte[11], first.Tag));
        Assert.Throws<InvalidOperationException>(() => protector.Unprotect([], first.Nonce, first.Tag));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-base64")]
    [InlineData("Zm9v")]
    public void InvalidKeysAreRejectedAtConstruction(string key)
    {
        Assert.False(SunshineCredentialProtector.IsValidKey(key));
        Assert.Throws<ArgumentException>(() => new SunshineCredentialProtector(key));
    }
}
