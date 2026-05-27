using System.Globalization;
using System.Text;

namespace WebVideoDownloader.Services;

internal static class NetworkResponseReader
{
    public static async Task<byte[]> ReadBytesAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        return HasContentEncoding(response, "aws-chunked") ? TryDecodeAwsChunkedPayload(bytes) : bytes;
    }

    private static bool HasContentEncoding(HttpResponseMessage response, string encoding)
    {
        return response.Content.Headers.ContentEncoding.Any(value =>
            value.Equals(encoding, StringComparison.OrdinalIgnoreCase));
    }

    private static byte[] TryDecodeAwsChunkedPayload(byte[] bytes)
    {
        try
        {
            using var output = new MemoryStream(bytes.Length);
            var offset = 0;

            while (offset < bytes.Length)
            {
                var lineEnd = FindCrLf(bytes, offset);
                if (lineEnd < 0)
                {
                    return bytes;
                }

                var line = Encoding.ASCII.GetString(bytes, offset, lineEnd - offset);
                var semicolonIndex = line.IndexOf(';', StringComparison.Ordinal);
                var sizeText = semicolonIndex >= 0 ? line[..semicolonIndex] : line;
                if (!int.TryParse(sizeText, NumberStyles.HexNumber, null, out var chunkSize))
                {
                    return bytes;
                }

                offset = lineEnd + 2;
                if (chunkSize == 0)
                {
                    return output.ToArray();
                }

                if (offset + chunkSize > bytes.Length)
                {
                    return bytes;
                }

                output.Write(bytes, offset, chunkSize);
                offset += chunkSize;

                if (offset + 2 <= bytes.Length && bytes[offset] == '\r' && bytes[offset + 1] == '\n')
                {
                    offset += 2;
                }
            }

            return output.Length > 0 ? output.ToArray() : bytes;
        }
        catch
        {
            return bytes;
        }
    }

    private static int FindCrLf(byte[] bytes, int startOffset)
    {
        for (var index = startOffset; index + 1 < bytes.Length; index++)
        {
            if (bytes[index] == '\r' && bytes[index + 1] == '\n')
            {
                return index;
            }
        }

        return -1;
    }
}
