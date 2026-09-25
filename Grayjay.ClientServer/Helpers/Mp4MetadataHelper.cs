using Grayjay.Desktop.POC;
using Grayjay.Engine.Models.Video.Sources;
using System;
using System.Buffers.Binary;
using System.Text;

namespace Grayjay.ClientServer.Helpers;

public static class Mp4MetadataHelper
{
    /// <summary>
    /// Scans top-level MP4 boxes to derive on-demand DASH byte ranges: init (start through moov)
    /// and index (sidx). Returns null when no moov box is found before media data starts.
    /// </summary>
    /// <param name="fetchBytes">Reads up to count bytes at offset; null or short reads end the scan.</param>
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
}
