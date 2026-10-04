using Grayjay.Desktop.POC;
using Grayjay.Engine.Models.Video.Sources;
using System.Buffers.Binary;
using System.Text;

namespace Grayjay.ClientServer.Helpers;

public static class Mp4MetadataHelper
{
    // Init range runs from the start through moov, index range is sidx; null without moov before media data.
    public static StreamMetaData? FindOnDemandRanges(Func<long, int, byte[]?> fetchBytes)
    {
        long offset = 0;
        long? initEnd = null;
        long? indexStart = null;
        long? indexEnd = null;

        for (int boxIndex = 0; boxIndex < 64; boxIndex++)
        {
            var header = fetchBytes(offset, 16);
            if (header == null || header.Length < 8)
                break;

            long size = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(0, 4));
            string type = Encoding.ASCII.GetString(header, 4, 4);
            if (size == 1)
            {
                if (header.Length < 16)
                    break;
                ulong largeSize = BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(8, 8));
                if (largeSize < 16 || largeSize > long.MaxValue)
                    break;
                size = (long)largeSize;
            }
            else if (size < 8)
            {
                break;
            }

            if (size > long.MaxValue - offset)
                break;

            if (type == "moov")
                initEnd = offset + size - 1;
            else if (type == "sidx" && indexStart == null)
            {
                indexStart = offset;
                indexEnd = offset + size - 1;
            }
            else if (type == "moof" || type == "mdat")
                break;

            if (initEnd != null && indexStart != null)
                break;

            offset += size;
        }

        static int? ToOffset(long? value)
        {
            if (value == null || value.Value > int.MaxValue)
                return null;
            return (int)value.Value;
        }

        if (initEnd == null)
            return null;

        var fileInitEnd = ToOffset(initEnd);
        var fileIndexStart = ToOffset(indexStart);
        var fileIndexEnd = ToOffset(indexEnd);
        if (fileInitEnd == null || (indexStart != null && fileIndexStart == null) || (indexEnd != null && fileIndexEnd == null))
            Logger.w(nameof(Mp4MetadataHelper), $"MP4 box offset exceeds int range (init end {initEnd}, sidx {indexStart}-{indexEnd}); dropping the affected range.");

        if (fileInitEnd == null)
            return null;

        return new StreamMetaData()
        {
            FileInitStart = 0,
            FileInitEnd = fileInitEnd.Value,
            FileIndexStart = fileIndexStart,
            FileIndexEnd = fileIndexEnd
        };
    }

    private static readonly byte[] WidevineSystemId = Convert.FromHexString("edef8ba979d64acea3c827dcd51d21ed");

    public static List<byte[]> FindWidevinePsshData(byte[] initSegment)
    {
        var result = new List<byte[]>();
        int moovContentStart = -1;
        int moovEnd = -1;
        int offset = 0;
        while (TryReadBox(initSegment, offset, initSegment.Length, out var type, out var contentStart, out var boxEnd))
        {
            if (type == "moov")
            {
                moovContentStart = contentStart;
                moovEnd = boxEnd;
                break;
            }
            offset = boxEnd;
        }
        if (moovContentStart < 0)
            return result;

        offset = moovContentStart;
        while (TryReadBox(initSegment, offset, moovEnd, out var type, out var contentStart, out var boxEnd))
        {
            if (type == "pssh")
            {
                var data = ReadWidevinePsshData(initSegment.AsSpan(contentStart, boxEnd - contentStart));
                if (data != null)
                    result.Add(data);
            }
            offset = boxEnd;
        }
        return result;
    }

    public static byte[]? WrapBareWidevinePsshData(byte[] data)
    {
        if (data.Length == 0)
            return null;
        if (TryReadBox(data, 0, data.Length, out var type, out _, out var boxEnd) && type == "pssh" && boxEnd == data.Length)
            return null;

        // Header: size(4) type(4) version and flags(4, zero) SystemID(16) DataSize(4).
        var box = new byte[32 + data.Length];
        BinaryPrimitives.WriteUInt32BigEndian(box.AsSpan(0, 4), (uint)box.Length);
        Encoding.ASCII.GetBytes("pssh").CopyTo(box, 4);
        WidevineSystemId.CopyTo(box, 12);
        BinaryPrimitives.WriteUInt32BigEndian(box.AsSpan(28, 4), (uint)data.Length);
        data.CopyTo(box, 32);
        return box;
    }

    private static byte[]? ReadWidevinePsshData(ReadOnlySpan<byte> content)
    {
        // Full box: version(1) flags(3) SystemID(16) [v1: KID_count(4) KIDs(16 each)] DataSize(4) Data.
        if (content.Length < 24)
            return null;
        int version = content[0];
        if (!content.Slice(4, 16).SequenceEqual(WidevineSystemId))
            return null;

        int position = 20;
        if (version > 0)
        {
            uint keyIdCount = BinaryPrimitives.ReadUInt32BigEndian(content.Slice(position, 4));
            position += 4;
            if (keyIdCount > (uint)(content.Length - position) / 16)
                return null;
            position += (int)keyIdCount * 16;
        }
        if (content.Length - position < 4)
            return null;

        uint dataSize = BinaryPrimitives.ReadUInt32BigEndian(content.Slice(position, 4));
        position += 4;
        if (dataSize > (uint)(content.Length - position))
            return null;
        return content.Slice(position, (int)dataSize).ToArray();
    }

    private static bool TryReadBox(byte[] data, int offset, int limit, out string type, out int contentStart, out int boxEnd)
    {
        type = "";
        contentStart = 0;
        boxEnd = 0;
        if (limit - offset < 8)
            return false;

        long size = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset, 4));
        type = Encoding.ASCII.GetString(data, offset + 4, 4);
        int headerSize = 8;
        if (size == 1)
        {
            if (limit - offset < 16)
                return false;
            ulong largeSize = BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(offset + 8, 8));
            if (largeSize > long.MaxValue)
                return false;
            size = (long)largeSize;
            headerSize = 16;
        }
        else if (size == 0)
        {
            size = limit - offset;
        }

        if (size < headerSize || size > limit - offset)
            return false;

        contentStart = offset + headerSize;
        boxEnd = offset + (int)size;
        return true;
    }
}

// A non-206 response means the server ignored Range, so its body is kept as the whole file.
public sealed class Mp4RangeReader(Func<long, int, (byte[]? Bytes, int Code)> fetchRange)
{
    private const int ReadAheadBytes = 64 * 1024;

    private byte[]? _fullBody;
    private byte[]? _window;
    private long _windowOffset;
    private bool _windowReachesEnd;

    public byte[]? Read(long offset, int count)
    {
        if (offset < 0 || count <= 0)
            return null;
        if (_fullBody != null)
            return Slice(_fullBody, 0, offset, count);

        if (_window != null && offset >= _windowOffset)
        {
            long windowEnd = _windowOffset + _window.Length;
            if (offset + count <= windowEnd || _windowReachesEnd)
                return Slice(_window, _windowOffset, offset, count);
        }

        int requestLength = Math.Max(count, ReadAheadBytes);
        var response = fetchRange(offset, requestLength);
        if (response.Bytes == null)
            return null;

        if (response.Code == 206)
        {
            if (response.Bytes.Length > requestLength)
                return null;
            _window = response.Bytes;
            _windowOffset = offset;
            _windowReachesEnd = response.Bytes.Length < requestLength;
            return Slice(_window, _windowOffset, offset, count);
        }

        _fullBody = response.Bytes;
        return Slice(_fullBody, 0, offset, count);
    }

    private static byte[]? Slice(byte[] source, long sourceOffset, long offset, int count)
    {
        long start = offset - sourceOffset;
        if (start < 0 || start >= source.Length)
            return null;
        int available = (int)Math.Min(count, source.Length - start);
        var slice = new byte[available];
        Array.Copy(source, start, slice, 0, available);
        return slice;
    }
}
