using Google.Protobuf;
using Grayjay.ClientServer.Controllers;
using Grayjay.ClientServer.Helpers;
using Grayjay.Engine.Models.Video.Sources;

namespace Grayjay.Desktop.Tests;

[TestClass]
public class WidevineLicenseSelectorTests
{
    private const int SignedMessageLicenseRequest = 1;
    private const int SignedMessageLicense = 2;
    private const int LicenseRequestNew = 1;
    private const int LicenseRequestRenewal = 2;

    private static readonly byte[] VideoPssh = Enumerable.Range(0, 32).Select(value => (byte)(0x10 + value)).ToArray();
    private static readonly byte[] AudioPssh = Enumerable.Range(0, 32).Select(value => (byte)(0x80 + value)).ToArray();
    private static readonly byte[] VideoSessionId = Convert.FromHexString("0102030405060708090A0B0C0D0E0F10");
    private static readonly byte[] AudioSessionId = Convert.FromHexString("A1A2A3A4A5A6A7A8A9AAABACADAEAFB0");

    private static byte[] Message(Action<CodedOutputStream> writeFields)
    {
        using var memoryStream = new MemoryStream();
        var output = new CodedOutputStream(memoryStream);
        writeFields(output);
        output.Flush();
        return memoryStream.ToArray();
    }

    private static void WriteBytes(CodedOutputStream output, int fieldNumber, byte[] value)
    {
        output.WriteTag(fieldNumber, WireFormat.WireType.LengthDelimited);
        output.WriteBytes(ByteString.CopyFrom(value));
    }

    private static void WriteVarint(CodedOutputStream output, int fieldNumber, int value)
    {
        output.WriteTag(fieldNumber, WireFormat.WireType.Varint);
        output.WriteInt32(value);
    }

    private static byte[] LicenseIdentification(byte[] sessionId) => Message(output =>
    {
        WriteBytes(output, 1, Convert.FromHexString("CAFE"));
        WriteBytes(output, 2, sessionId);
    });

    private static byte[] SignedMessage(int type, byte[] innerMessage) => Message(output =>
    {
        WriteVarint(output, 1, type);
        WriteBytes(output, 2, innerMessage);
        WriteBytes(output, 3, new byte[32]);
    });

    private static byte[] NewChallenge(byte[] pssh)
    {
        var widevinePsshData = Message(output => WriteBytes(output, 1, pssh));
        var contentIdentification = Message(output => WriteBytes(output, 1, widevinePsshData));
        var licenseRequest = Message(output =>
        {
            WriteBytes(output, 2, contentIdentification);
            WriteVarint(output, 3, LicenseRequestNew);
        });
        return SignedMessage(SignedMessageLicenseRequest, licenseRequest);
    }

    private static byte[] RenewalChallenge(byte[] sessionId)
    {
        var existingLicense = Message(output => WriteBytes(output, 1, LicenseIdentification(sessionId)));
        var contentIdentification = Message(output => WriteBytes(output, 3, existingLicense));
        var licenseRequest = Message(output =>
        {
            WriteBytes(output, 2, contentIdentification);
            WriteVarint(output, 3, LicenseRequestRenewal);
        });
        return SignedMessage(SignedMessageLicenseRequest, licenseRequest);
    }

    private static byte[] LicenseResponse(byte[] sessionId)
    {
        var license = Message(output => WriteBytes(output, 1, LicenseIdentification(sessionId)));
        return SignedMessage(SignedMessageLicense, license);
    }

    private sealed class Fixture : IDisposable
    {
        public DetailsController.DetailsState DetailsState { get; } = new DetailsController.DetailsState();
        public VideoUrlWidevineSource Video { get; } = new VideoUrlWidevineSource() { LicenseUri = "https://video-license.example.com" };
        public AudioUrlWidevineSource Audio { get; } = new AudioUrlWidevineSource() { LicenseUri = "https://audio-license.example.com" };

        public Fixture()
        {
            long generation = DetailsState.CachedDashGeneration;
            DetailsState.TrySetWidevinePsshData(generation, Video, new List<byte[]>() { VideoPssh });
            DetailsState.TrySetWidevinePsshData(generation, Audio, new List<byte[]>() { AudioPssh });
        }

        public IWidevineSource? Select(byte[] challenge) =>
            DetailsController.SelectWidevineLicenseSource(Video, Audio, challenge, DetailsState.GetWidevinePsshData, DetailsState.GetLicenseSessionSource);

        public void Record(IWidevineSource source, byte[] license)
        {
            var sessionId = WidevineLicenseMessages.TryGetLicenseSessionId(license);
            Assert.IsNotNull(sessionId);
            Assert.IsTrue(DetailsState.TrySetLicenseSessionSource(DetailsState.CachedDashGeneration, sessionId, source));
        }

        public void Dispose() => DetailsState.Dispose();
    }

    [TestMethod]
    public void TryGetLicenseSessionId_ReadsLicenseId()
    {
        CollectionAssert.AreEqual(AudioSessionId, WidevineLicenseMessages.TryGetLicenseSessionId(LicenseResponse(AudioSessionId)));
    }

    [TestMethod]
    public void TryGetRenewalSessionId_ReadsExistingLicenseId()
    {
        CollectionAssert.AreEqual(AudioSessionId, WidevineLicenseMessages.TryGetRenewalSessionId(RenewalChallenge(AudioSessionId)));
    }

    [TestMethod]
    public void TryGetRenewalSessionId_IgnoresNewRequestsAndLicenses()
    {
        Assert.IsNull(WidevineLicenseMessages.TryGetRenewalSessionId(NewChallenge(AudioPssh)));
        Assert.IsNull(WidevineLicenseMessages.TryGetRenewalSessionId(LicenseResponse(AudioSessionId)));
        Assert.IsNull(WidevineLicenseMessages.TryGetLicenseSessionId(RenewalChallenge(AudioSessionId)));
    }

    [TestMethod]
    public void MalformedMessages_ReturnNull()
    {
        var truncated = RenewalChallenge(AudioSessionId)[..12];
        Assert.IsNull(WidevineLicenseMessages.TryGetRenewalSessionId(truncated));
        Assert.IsNull(WidevineLicenseMessages.TryGetRenewalSessionId(new byte[] { 0xFF, 0xFF, 0xFF }));
        Assert.IsNull(WidevineLicenseMessages.TryGetLicenseSessionId(Array.Empty<byte>()));
    }

    [TestMethod]
    public void InitialRequests_RouteByPssh()
    {
        using var fixture = new Fixture();

        Assert.AreSame(fixture.Audio, fixture.Select(NewChallenge(AudioPssh)));
        Assert.AreSame(fixture.Video, fixture.Select(NewChallenge(VideoPssh)));
    }

    [TestMethod]
    public void AudioRenewal_RoutesToAudio()
    {
        using var fixture = new Fixture();
        fixture.Record(fixture.Audio, LicenseResponse(AudioSessionId));
        fixture.Record(fixture.Video, LicenseResponse(VideoSessionId));

        Assert.AreSame(fixture.Audio, fixture.Select(RenewalChallenge(AudioSessionId)));
        Assert.AreSame(fixture.Video, fixture.Select(RenewalChallenge(VideoSessionId)));
    }

    [TestMethod]
    public void UnknownSessionRenewal_FallsBackToVideo()
    {
        using var fixture = new Fixture();
        fixture.Record(fixture.Audio, LicenseResponse(AudioSessionId));

        Assert.AreSame(fixture.Video, fixture.Select(RenewalChallenge(VideoSessionId)));
    }

    [TestMethod]
    public void MalformedChallenge_FallsBackWithoutThrowing()
    {
        using var fixture = new Fixture();
        fixture.Record(fixture.Audio, LicenseResponse(AudioSessionId));

        Assert.AreSame(fixture.Video, fixture.Select(new byte[] { 0x08, 0x01, 0x12, 0x7F, 0x00 }));
    }

    [TestMethod]
    public void StaleGenerationRecord_IsIgnored()
    {
        using var fixture = new Fixture();
        long staleGeneration = fixture.DetailsState.CachedDashGeneration;
        fixture.DetailsState.ClearCachedDash();

        Assert.IsFalse(fixture.DetailsState.TrySetLicenseSessionSource(staleGeneration, AudioSessionId, fixture.Audio));
        Assert.IsNull(fixture.DetailsState.GetLicenseSessionSource(AudioSessionId));
    }
}
