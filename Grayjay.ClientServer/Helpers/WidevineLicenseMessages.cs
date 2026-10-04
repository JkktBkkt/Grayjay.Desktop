using Google.Protobuf;

namespace Grayjay.ClientServer.Helpers;

/// <summary>
/// Reads the CDM session id from Widevine license messages (license_protocol.proto), without the generated types.
/// </summary>
public static class WidevineLicenseMessages
{
    private const int SignedMessageTypeField = 1;
    private const int SignedMessageMsgField = 2;
    private const int SignedMessageLicenseRequest = 1;
    private const int SignedMessageLicense = 2;
    private const int LicenseRequestContentIdField = 2;
    private const int ContentIdentificationExistingLicenseField = 3;
    private const int ExistingLicenseLicenseIdField = 1;
    private const int LicenseIdField = 1;
    private const int LicenseIdentificationSessionIdField = 2;

    /// <summary>
    /// Returns License.id.session_id from a license response, or null when the message is not a parsable license.
    /// </summary>
    public static byte[]? TryGetLicenseSessionId(byte[] license)
    {
        var licenseMessage = ReadSignedMessage(license, SignedMessageLicense);
        var licenseId = ReadLengthDelimitedField(licenseMessage, LicenseIdField);
        return ReadLengthDelimitedField(licenseId, LicenseIdentificationSessionIdField);
    }

    /// <summary>
    /// Returns the session id a renewal or release request names in content_id.existing_license, or null for
    /// new requests and messages that do not parse.
    /// </summary>
    public static byte[]? TryGetRenewalSessionId(byte[] challenge)
    {
        var licenseRequest = ReadSignedMessage(challenge, SignedMessageLicenseRequest);
        var contentId = ReadLengthDelimitedField(licenseRequest, LicenseRequestContentIdField);
        var existingLicense = ReadLengthDelimitedField(contentId, ContentIdentificationExistingLicenseField);
        var licenseId = ReadLengthDelimitedField(existingLicense, ExistingLicenseLicenseIdField);
        return ReadLengthDelimitedField(licenseId, LicenseIdentificationSessionIdField);
    }

    private static byte[]? ReadSignedMessage(byte[] signedMessage, int expectedType)
    {
        int? type = null;
        byte[]? msg = null;
        try
        {
            var input = new CodedInputStream(signedMessage);
            while (!input.IsAtEnd)
            {
                var tag = input.ReadTag();
                var fieldNumber = WireFormat.GetTagFieldNumber(tag);
                var wireType = WireFormat.GetTagWireType(tag);
                if (fieldNumber == SignedMessageTypeField && wireType == WireFormat.WireType.Varint)
                {
                    type = input.ReadInt32();
                }
                else if (fieldNumber == SignedMessageMsgField && wireType == WireFormat.WireType.LengthDelimited)
                {
                    msg = input.ReadBytes().ToByteArray();
                }
                else
                {
                    input.SkipLastField();
                }
            }
        }
        catch (InvalidProtocolBufferException)
        {
            return null;
        }
        return type == expectedType ? msg : null;
    }

    private static byte[]? ReadLengthDelimitedField(byte[]? message, int fieldNumber)
    {
        if (message == null)
        {
            return null;
        }

        byte[]? value = null;
        try
        {
            var input = new CodedInputStream(message);
            while (!input.IsAtEnd)
            {
                var tag = input.ReadTag();
                if (WireFormat.GetTagFieldNumber(tag) == fieldNumber && WireFormat.GetTagWireType(tag) == WireFormat.WireType.LengthDelimited)
                {
                    value = input.ReadBytes().ToByteArray();
                }
                else
                {
                    input.SkipLastField();
                }
            }
        }
        catch (InvalidProtocolBufferException)
        {
            return null;
        }
        return value;
    }
}
