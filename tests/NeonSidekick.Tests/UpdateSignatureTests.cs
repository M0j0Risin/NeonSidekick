using System.Security.Cryptography;
using System.Text;
using NeonSidekick.Updates;

namespace NeonSidekick.Tests;

public class UpdateSignatureTests
{
    // A lasting contract, pinned: copies already out there check new releases against this format. Made once with .NET's own ECDsa and its
    // private half thrown away, so nothing can sign with it. If this fails, old copies would refuse every new release.
    private const string VectorKey = "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAE2uT9vXdHzgWQMmnk8nihzJiGoa51ZegQxaBX2bXjySIc0wjkyGJlZhOrxOnx0BfSq4k9hEu9eDBng+5Ma6WBQQ==";
    private const string VectorSignature = "rzolao6571alijy0BKyICaD1qR4sL3aJykWzdb/F2E10VqC21t+ZDGFLMUGJVDlHqB8ETRKa0x3JCOSk2PIgXA==";
    private static readonly byte[] VectorSigned = Encoding.ASCII.GetBytes("abc123 *NeonSidekick-v0.5.7-win-x64.zip\n# def456 update-manifest.json\n");

    private static readonly byte[] Signed = Encoding.ASCII.GetBytes("hash *archive.zip\n# other update-manifest.json\n");

    [Fact]
    public void TheVector_StillVerifies()
    {
        Assert.True(UpdateSignature.Verify(VectorSigned, VectorSignature, [VectorKey]));
        Assert.Equal(UpdateSignature.SignatureBytes, Convert.FromBase64String(VectorSignature).Length);
    }

    [Fact]
    public void TheVector_FailsOnChangedBytes()
    {
        byte[] changed = [.. VectorSigned];
        changed[0] ^= 1;
        Assert.False(UpdateSignature.Verify(changed, VectorSignature, [VectorKey]));
    }

    [Fact]
    public void WhatItSigns_ItVerifies()
    {
        using var key = UpdateSignature.NewKey();
        string line = UpdateSignature.SignatureLine(Signed, key);
        Assert.True(UpdateSignature.Verify(Signed, line, [UpdateSignature.PublicKeyText(key)]));
    }

    [Fact]
    public void AWrongKey_IsRefused()
    {
        using var signer = UpdateSignature.NewKey();
        using var other = UpdateSignature.NewKey();
        string line = UpdateSignature.SignatureLine(Signed, signer);
        Assert.False(UpdateSignature.Verify(Signed, line, [UpdateSignature.PublicKeyText(other)]));
    }

    [Fact]
    public void TamperedBytes_AreRefused()
    {
        using var key = UpdateSignature.NewKey();
        string line = UpdateSignature.SignatureLine(Signed, key);
        byte[] tampered = [.. Signed, (byte)' '];
        Assert.False(UpdateSignature.Verify(tampered, line, [UpdateSignature.PublicKeyText(key)]));
    }

    [Fact]
    public void AReplayedAssetName_IsRefused()
    {
        using var key = UpdateSignature.NewKey();
        byte[] older = Encoding.ASCII.GetBytes("hash *NeonSidekick-v0.5.6-win-x64.zip\n");
        byte[] newer = Encoding.ASCII.GetBytes("hash *NeonSidekick-v0.5.7-win-x64.zip\n");
        string line = UpdateSignature.SignatureLine(older, key);
        Assert.False(UpdateSignature.Verify(newer, line, [UpdateSignature.PublicKeyText(key)]));
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void TwoLines_EitherKeyIsEnough(string ending)
    {
        using var first = UpdateSignature.NewKey();
        using var second = UpdateSignature.NewKey();
        string sig = UpdateSignature.SignatureLine(Signed, first) + ending + UpdateSignature.SignatureLine(Signed, second) + ending;
        Assert.True(UpdateSignature.Verify(Signed, sig, [UpdateSignature.PublicKeyText(first)]));
        Assert.True(UpdateSignature.Verify(Signed, sig, [UpdateSignature.PublicKeyText(second)]));
    }

    [Fact]
    public void ALineThisCopyCannotRead_LeavesTheOneItCan()
    {
        using var key = UpdateSignature.NewKey();
        string sig = "not base64 at all\nQUJD\n" + UpdateSignature.SignatureLine(Signed, key);
        Assert.True(UpdateSignature.Verify(Signed, sig, [UpdateSignature.PublicKeyText(key)]));
    }

    [Fact]
    public void ABadKey_LeavesTheOneThatWorks()
    {
        using var key = UpdateSignature.NewKey();
        string line = UpdateSignature.SignatureLine(Signed, key);
        Assert.True(UpdateSignature.Verify(Signed, line, ["not a key", "QUJD", UpdateSignature.PublicKeyText(key)]));
    }

    [Theory]
    [InlineData("")]
    [InlineData("\n\n")]
    [InlineData("garbage")]
    public void NoSignature_IsRefused(string sig)
    {
        using var key = UpdateSignature.NewKey();
        Assert.False(UpdateSignature.Verify(Signed, sig, [UpdateSignature.PublicKeyText(key)]));
    }

    [Fact]
    public void NoKeys_RefuseEverything()
    {
        using var key = UpdateSignature.NewKey();
        Assert.False(UpdateSignature.Verify(Signed, UpdateSignature.SignatureLine(Signed, key), []));
    }

    [Fact]
    public void ADerSignature_IsRefused()
    {
        // Tools differ on this, which is why the format is spelled out: DER from another signer must not pass as if it were checked.
        using var key = UpdateSignature.NewKey();
        string der = Convert.ToBase64String(key.SignData(Signed, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence));
        Assert.False(UpdateSignature.Verify(Signed, der, [UpdateSignature.PublicKeyText(key)]));
    }

    [Fact]
    public void AnotherCurve_IsNotAKey()
    {
        using var p384 = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        Assert.False(UpdateSignature.IsPublicKey(UpdateSignature.PublicKeyText(p384)));
    }

    [Fact]
    public void IsPublicKey_ReadsAKeyAndNothingElse()
    {
        using var key = UpdateSignature.NewKey();
        Assert.True(UpdateSignature.IsPublicKey(UpdateSignature.PublicKeyText(key)));
        Assert.True(UpdateSignature.IsPublicKey(VectorKey));
        Assert.False(UpdateSignature.IsPublicKey(""));
        Assert.False(UpdateSignature.IsPublicKey("not a key"));
        Assert.False(UpdateSignature.IsPublicKey("QUJD"));
    }

    [Fact]
    public void AKeyFile_OpensWithItsPassword_AndSignsTheSame()
    {
        using var key = UpdateSignature.NewKey();
        string file = UpdateSignature.EncryptedKeyFile(key, "correct horse battery");

        Assert.Contains("BEGIN ENCRYPTED PRIVATE KEY", file, StringComparison.Ordinal);
        using var opened = UpdateSignature.LoadKeyFile(file, "correct horse battery");
        Assert.Equal(UpdateSignature.PublicKeyText(key), UpdateSignature.PublicKeyText(opened));
        Assert.True(UpdateSignature.Verify(Signed, UpdateSignature.SignatureLine(Signed, opened), [UpdateSignature.PublicKeyText(key)]));
    }

    [Fact]
    public void AKeyFile_RefusesAWrongPassword()
    {
        using var key = UpdateSignature.NewKey();
        string file = UpdateSignature.EncryptedKeyFile(key, "correct horse battery");
        Assert.ThrowsAny<CryptographicException>(() => UpdateSignature.LoadKeyFile(file, "wrong"));
    }

    [Fact]
    public void TheEmbeddedList_HoldsTheTwoKeys_Different()
    {
        Assert.Equal(UpdateKeys.Required, UpdateKeys.Embedded.Count);
        Assert.Equal(UpdateKeys.Required, UpdateKeys.Embedded.Distinct(StringComparer.Ordinal).Count());
        Assert.All(UpdateKeys.Embedded, key => Assert.True(UpdateSignature.IsPublicKey(key)));
    }
}
