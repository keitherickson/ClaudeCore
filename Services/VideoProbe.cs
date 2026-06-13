using System.Buffers.Binary;
using System.Text;

namespace ClaudeCore.Services;

/// <summary>
/// Minimal dependency-free MP4 probe: reads the display height from a video
/// track's <c>tkhd</c> box (width/height are the last 8 bytes, 16.16 fixed-point).
/// Used to pick a valid integer upscale factor for Maxine SuperRes.
/// </summary>
public static class VideoProbe
{
    private readonly record struct Box(string Type, long PayloadStart, long End);

    public static int? TryGetHeight(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            int? best = null;
            foreach (var moov in Boxes(fs, 0, fs.Length))
            {
                if (moov.Type != "moov") continue;
                foreach (var trak in Boxes(fs, moov.PayloadStart, moov.End))
                {
                    if (trak.Type != "trak") continue;
                    foreach (var tkhd in Boxes(fs, trak.PayloadStart, trak.End))
                    {
                        if (tkhd.Type != "tkhd") continue;
                        var h = ReadTkhdHeight(fs, tkhd);
                        if (h.HasValue && (best is null || h.Value > best.Value)) best = h.Value;
                    }
                }
            }
            return best;
        }
        catch
        {
            return null;
        }
    }

    private static IEnumerable<Box> Boxes(Stream s, long start, long end)
    {
        long pos = start;
        var hdr = new byte[8];
        while (pos + 8 <= end)
        {
            s.Position = pos;
            if (ReadFully(s, hdr, 8) != 8) yield break;

            long size = BinaryPrimitives.ReadUInt32BigEndian(hdr);
            string type = Encoding.ASCII.GetString(hdr, 4, 4);
            long headerLen = 8;

            if (size == 1) // 64-bit extended size
            {
                var big = new byte[8];
                if (ReadFully(s, big, 8) != 8) yield break;
                size = (long)BinaryPrimitives.ReadUInt64BigEndian(big);
                headerLen = 16;
            }
            else if (size == 0)
            {
                size = end - pos; // box extends to end
            }

            if (size < headerLen || pos + size > end) yield break;
            yield return new Box(type, pos + headerLen, pos + size);
            pos += size;
        }
    }

    private static int? ReadTkhdHeight(Stream s, Box tkhd)
    {
        long whPos = tkhd.End - 8; // width(4) + height(4), each 16.16 fixed-point
        if (whPos < tkhd.PayloadStart) return null;
        s.Position = whPos;
        var buf = new byte[8];
        if (ReadFully(s, buf, 8) != 8) return null;
        uint height = BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(4, 4));
        int h = (int)(height >> 16);
        return h > 0 ? h : null;
    }

    private static int ReadFully(Stream s, byte[] buf, int count)
    {
        int total = 0;
        while (total < count)
        {
            int r = s.Read(buf, total, count - total);
            if (r <= 0) break;
            total += r;
        }
        return total;
    }
}
