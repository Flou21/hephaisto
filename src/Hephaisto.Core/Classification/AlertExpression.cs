namespace Hephaisto.Core.Classification;

/// <summary>
/// The rule's expression, recovered from the generator URL Prometheus puts on every alert.
/// </summary>
/// <remarks>
/// Backlog #135. For an alert about a pipeline the expression is the most useful sentence there
/// is - "which series, compared to what" - and Alertmanager does not send it as a field. It does
/// send <c>generatorURL</c>, a link to Prometheus's graph page whose <c>g0.expr</c> parameter is
/// the expression, URL-encoded. Null for anything else: a Grafana-managed rule's URL, a missing
/// one, or one that does not parse. A wrong expression would be worse than none.
/// </remarks>
public static class AlertExpression
{
    public static string? FromGeneratorUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return null;
        }

        foreach (var part in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=', StringComparison.Ordinal);
            if (eq <= 0 || !string.Equals(part[..eq], "g0.expr", StringComparison.Ordinal))
            {
                continue;
            }

            try
            {
                var expr = Uri.UnescapeDataString(part[(eq + 1)..].Replace('+', ' ')).Trim();
                return expr.Length > 0 ? expr : null;
            }
            catch (UriFormatException)
            {
                return null;
            }
        }

        return null;
    }
}
