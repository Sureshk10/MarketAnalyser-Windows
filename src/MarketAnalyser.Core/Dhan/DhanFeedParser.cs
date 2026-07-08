using System.Buffers.Binary;

namespace MarketAnalyser.Core.Dhan;

public static class DhanFeedParser
{
    public static IReadOnlyList<DhanFeedPacket> ParseMany(ReadOnlySpan<byte> bytes)
    {
        var packets = new List<DhanFeedPacket>();
        var offset = 0;

        while (offset + 8 <= bytes.Length)
        {
            var messageLength = BinaryPrimitives.ReadInt16LittleEndian(bytes[(offset + 1)..(offset + 3)]);
            if (messageLength <= 0 || offset + messageLength > bytes.Length)
            {
                break;
            }

            var packet = Parse(bytes.Slice(offset, messageLength));
            if (packet is not null)
            {
                packets.Add(packet);
            }

            offset += messageLength;
        }

        return packets;
    }

    private static DhanFeedPacket? Parse(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 8)
        {
            return null;
        }

        var responseCode = bytes[0];
        var messageLength = BinaryPrimitives.ReadInt16LittleEndian(bytes[1..3]);
        var exchangeSegment = bytes[3];
        var securityId = BinaryPrimitives.ReadInt32LittleEndian(bytes[4..8]);

        return responseCode switch
        {
            2 when bytes.Length >= 16 => new DhanFeedPacket(
                responseCode,
                messageLength,
                exchangeSegment,
                securityId,
                LastPrice: ReadFloat(bytes, 8),
                LastTradeTime: ReadEpoch(bytes, 12)),

            4 when bytes.Length >= 50 => new DhanFeedPacket(
                responseCode,
                messageLength,
                exchangeSegment,
                securityId,
                LastPrice: ReadFloat(bytes, 8),
                Volume: BinaryPrimitives.ReadInt32LittleEndian(bytes[22..26]),
                LastTradeTime: ReadEpoch(bytes, 14)),

            5 when bytes.Length >= 12 => new DhanFeedPacket(
                responseCode,
                messageLength,
                exchangeSegment,
                securityId,
                OpenInterest: BinaryPrimitives.ReadInt32LittleEndian(bytes[8..12])),

            8 when bytes.Length >= 62 => new DhanFeedPacket(
                responseCode,
                messageLength,
                exchangeSegment,
                securityId,
                LastPrice: ReadFloat(bytes, 8),
                Volume: BinaryPrimitives.ReadInt32LittleEndian(bytes[22..26]),
                OpenInterest: BinaryPrimitives.ReadInt32LittleEndian(bytes[34..38]),
                LastTradeTime: ReadEpoch(bytes, 14)),

            _ => new DhanFeedPacket(responseCode, messageLength, exchangeSegment, securityId)
        };
    }

    private static decimal ReadFloat(ReadOnlySpan<byte> bytes, int start)
    {
        var value = BinaryPrimitives.ReadSingleLittleEndian(bytes[start..(start + 4)]);
        return decimal.Round((decimal)value, 2);
    }

    private static DateTimeOffset ReadEpoch(ReadOnlySpan<byte> bytes, int start)
    {
        var epoch = BinaryPrimitives.ReadInt32LittleEndian(bytes[start..(start + 4)]);
        return DateTimeOffset.FromUnixTimeSeconds(epoch);
    }
}
