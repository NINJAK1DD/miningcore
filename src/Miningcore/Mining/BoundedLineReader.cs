using System.Text;

namespace Miningcore.Mining;

internal sealed class BoundedLineReader : IDisposable
{
    public BoundedLineReader(TextReader reader, int maximumLength,
        string description, string failureGuidance = null)
    {
        ArgumentNullException.ThrowIfNull(reader);

        if(maximumLength <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumLength));

        this.reader = reader;
        this.maximumLength = maximumLength;
        this.description = description;
        this.failureGuidance = failureGuidance;
    }

    private readonly TextReader reader;
    private readonly int maximumLength;
    private readonly string description;
    private readonly string failureGuidance;
    private readonly char[] buffer = new char[4096];
    private int position;
    private int available;
    private long lineNumber;

    public string ReadLine()
    {
        StringBuilder builder = null;
        var length = 0;

        while(true)
        {
            if(position >= available)
            {
                available = reader.Read(buffer, 0, buffer.Length);
                position = 0;

                if(available == 0)
                {
                    if(builder == null && length == 0)
                        return null;

                    lineNumber++;
                    return Finish(builder, length);
                }
            }

            var newline = Array.IndexOf(buffer, '\n', position,
                available - position);
            var end = newline >= 0 ? newline : available;
            var segmentLength = end - position;
            length = checked(length + segmentLength);

            if(length > maximumLength)
                throw new InvalidDataException(
                    $"{description} contains a record line longer than " +
                    $"{maximumLength} characters near line {lineNumber + 1}." +
                    failureGuidance);

            builder ??= new StringBuilder(Math.Min(maximumLength,
                Math.Max(128, length)));
            builder.Append(buffer, position, segmentLength);
            position = newline >= 0 ? newline + 1 : available;

            if(newline < 0)
                continue;

            lineNumber++;
            return Finish(builder, length);
        }
    }

    private static string Finish(StringBuilder builder, int length)
    {
        if(builder == null)
            return string.Empty;

        if(length > 0 && builder[length - 1] == '\r')
            builder.Length--;

        return builder.ToString();
    }

    public void Dispose()
    {
        // The caller owns the underlying reader and stream.
    }
}
