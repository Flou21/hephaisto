using Hephaisto.Agent.Llm;

namespace Hephaisto.Tests.Llm;

/// <summary>
/// Pins provider-level retry on.
/// </summary>
/// <remarks>
/// On 2026-08-28, 9 of 12 investigations on the dev cluster terminated Faulted, every one of
/// them on a transient "model is currently experiencing high demand" overload, because the
/// SDK's retry defaults to off and nothing turned it on. These tests fail if that regresses -
/// in particular if <c>HttpOptions</c> ever goes back to being built only when an endpoint
/// override happens to be configured, which is how the gap hid for so long.
/// </remarks>
public class GeminiRetryOptionsTests
{
    [Fact]
    public void Retry_is_enabled_on_the_default_configuration()
    {
        var http = GeminiChatClientFactory.BuildHttpOptions(new LlmOptions());

        Assert.NotNull(http.RetryOptions);
        Assert.True(http.RetryOptions!.Attempts > 1);
    }

    [Fact]
    public void Retry_is_enabled_even_with_no_endpoint_override()
    {
        // The regression path: no Endpoint, no ApiVersion. This is what the cluster runs, and
        // it is exactly the case that used to produce a null HttpOptions and no retry.
        var options = new LlmOptions { Endpoint = null, ApiVersion = null };

        var http = GeminiChatClientFactory.BuildHttpOptions(options);

        Assert.NotNull(http.RetryOptions);
        Assert.Null(http.BaseUrl);
    }

    [Fact]
    public void Endpoint_override_still_reaches_the_transport()
    {
        var options = new LlmOptions { Endpoint = "https://gateway.internal", ApiVersion = "v1beta" };

        var http = GeminiChatClientFactory.BuildHttpOptions(options);

        Assert.Equal("https://gateway.internal", http.BaseUrl);
        Assert.Equal("v1beta", http.ApiVersion);
        Assert.NotNull(http.RetryOptions);
    }

    [Fact]
    public void Attempts_of_one_disables_retry_rather_than_configuring_a_useless_one()
    {
        var options = new LlmOptions();
        options.Retry.Attempts = 1;

        var http = GeminiChatClientFactory.BuildHttpOptions(options);

        Assert.Null(http.RetryOptions);
    }

    [Fact]
    public void Backoff_cannot_sleep_away_the_investigation_wall_clock()
    {
        var options = new LlmOptions();

        var retry = options.Retry;
        var worstCase = TimeSpan.Zero;

        for (var attempt = 1; attempt < retry.Attempts; attempt++)
        {
            var delay = retry.InitialDelay.TotalSeconds * Math.Pow(retry.ExpBase, attempt - 1)
                + retry.Jitter;

            worstCase += TimeSpan.FromSeconds(Math.Min(delay, retry.MaxDelay.TotalSeconds));
        }

        // One step's retries must stay a small fraction of the whole investigation's clock,
        // or a provider blip converts WallClockExhausted into the new default outcome.
        Assert.True(
            worstCase < options.Investigation.MaxWallClock / 4,
            $"worst-case backoff {worstCase} is too close to {options.Investigation.MaxWallClock}");
    }
}
