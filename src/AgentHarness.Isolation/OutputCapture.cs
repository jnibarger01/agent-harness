using System.Text;

namespace AgentHarness.Isolation;

/// <summary>
/// Reads a redirected stream to EOF while retaining only the first <see cref="MaxCapturedChars"/>
/// characters. The read loop never stops early: a full pipe buffer blocks the child forever, and
/// that hang is indistinguishable from a model stall (see the boundary implementations' original
/// discard-only DrainAsync, which existed for exactly this reason). Bytes beyond the cap are read
/// and dropped so the child is never starved, and the result says so via <c>Truncated</c> instead
/// of silently losing the tail.
/// </summary>
internal static class OutputCapture
{
    public const int MaxCapturedChars = 64 * 1024;

    public static async Task<(string Text, bool Truncated)> CaptureAsync(
        StreamReader reader, CancellationToken cancellationToken)
    {
        var buffer = new char[4096];
        var sb = new StringBuilder();
        var truncated = false;

        while (true)
        {
            int read;
            try
            {
                read = await reader.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Boundary torn down mid-drain; return whatever was captured rather than fault
                // the caller's Task.WhenAll over an expected teardown race.
                break;
            }

            if (read == 0) break;

            var remaining = MaxCapturedChars - sb.Length;
            if (remaining <= 0)
            {
                truncated = true;
                continue;
            }

            var take = Math.Min(remaining, read);
            sb.Append(buffer, 0, take);
            if (take < read) truncated = true;
        }

        return (sb.ToString(), truncated);
    }

    public static async Task<CapturedOutput> BuildResultAsync(
        Task<(string Text, bool Truncated)> stdout, Task<(string Text, bool Truncated)> stderr)
    {
        var (outText, outTruncated) = await stdout.ConfigureAwait(false);
        var (errText, errTruncated) = await stderr.ConfigureAwait(false);
        return new CapturedOutput(outText, errText, outTruncated, errTruncated);
    }
}
