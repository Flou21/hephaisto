using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using Hephaisto.Agent.Llm;
using Hephaisto.Agent.Persistence;

namespace Hephaisto.Tests.Llm;

/// <summary>
/// A model without a price has no cost cap, so it does not start (backlog #140).
/// </summary>
/// <remarks>
/// An unpriced model bills as $0: the hourly, daily, per-incident and per-investigation cost caps
/// never bind and the console reports 0.0% utilisation. A model served through a gateway under a
/// name of the operator's choosing was in exactly that position, and the price could not even be
/// set from the chart, because a model id is not a valid environment variable name.
/// </remarks>
public sealed class OperatorPricingTests
{
    private static ServiceProvider Build(params (string Key, string Value)[] settings)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
            [
                new KeyValuePair<string, string?>("GEMINI_API_KEY", string.Empty),
                .. settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value)),
            ])
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(configuration);
        services.Configure<LlmBudgetOptions>(configuration.GetSection(LlmBudgetOptions.SectionName));
        services.AddHephaistoLlm(configuration);

        return services.BuildServiceProvider();
    }

    private static Action Check(ServiceProvider sp) =>
        () => _ = sp.GetRequiredService<IOptions<LlmPricingCheck>>().Value;

    [Fact]
    public void A_price_set_as_a_list_entry_is_merged_into_the_table()
    {
        using var sp = Build(
            ("Llm:Prices:0:Model", "true-relevance"),
            ("Llm:Prices:0:InputPerMillionUsd", "0.5"),
            ("Llm:Prices:0:OutputPerMillionUsd", "2"));

        var pricing = sp.GetRequiredService<IOptions<LlmOptions>>().Value.Pricing;

        pricing.Should().ContainKey("true-relevance");
        pricing["true-relevance"].OutputPerMillionUsd.Should().Be(2m);
    }

    [Fact]
    public void A_list_entry_replaces_a_built_in_price_of_the_same_name()
    {
        using var sp = Build(
            ("Llm:Prices:0:Model", "gemini-3.7-flash"),
            ("Llm:Prices:0:InputPerMillionUsd", "1.5"),
            ("Llm:Prices:0:OutputPerMillionUsd", "7.5"));

        sp.GetRequiredService<IOptions<LlmOptions>>().Value.Pricing["gemini-3.7-flash"]
            .InputPerMillionUsd.Should().Be(1.5m);
    }

    [Fact]
    public void The_shipped_default_model_starts() =>
        Check(Build()).Should().NotThrow();

    [Fact]
    public void An_unpriced_model_under_a_cost_cap_is_refused()
    {
        using var sp = Build(("Llm:Provider", "openai"), ("Llm:ApiKey", "sk-test"), ("Llm:Model", "gateway-alias"));

        Check(sp).Should().Throw<OptionsValidationException>()
            .WithMessage("*'gateway-alias'*llm.pricing*");
    }

    /// <summary>A free model is a legitimate configuration, and saying so is the acknowledgement.</summary>
    [Fact]
    public void An_explicit_price_of_zero_starts()
    {
        using var sp = Build(
            ("Llm:Provider", "openai"),
            ("Llm:ApiKey", "sk-test"),
            ("Llm:Model", "stand-in"),
            ("Llm:Prices:0:Model", "stand-in"),
            ("Llm:Prices:0:InputPerMillionUsd", "0"),
            ("Llm:Prices:0:OutputPerMillionUsd", "0"));

        Check(sp).Should().NotThrow();
    }

    [Fact]
    public void An_unpriced_planning_model_is_refused_too()
    {
        using var sp = Build(("Llm:PlanningModel", "someone-elses-pro"));

        Check(sp).Should().Throw<OptionsValidationException>().WithMessage("*'someone-elses-pro'*");
    }

    /// <summary>
    /// The check is a refusal to START. Reading the model options in a container that never
    /// starts a host - the eval harness - must keep working with any model.
    /// </summary>
    [Fact]
    public void Reading_the_options_does_not_run_the_check()
    {
        using var sp = Build(("Llm:Model", "gateway-alias"));

        var read = () => sp.GetRequiredService<IOptions<LlmOptions>>().Value;

        read.Should().NotThrow();
    }

    /// <summary>
    /// A gateway may answer with a model id of its own. Pricing only what came back charged the
    /// configured, priced model at zero.
    /// </summary>
    [Fact]
    public void A_response_under_an_unknown_id_is_priced_as_the_configured_model()
    {
        var pricing = new LlmPricing(new Dictionary<string, ModelPrice>(StringComparer.OrdinalIgnoreCase)
        {
            ["configured"] = new() { InputPerMillionUsd = 1m, OutputPerMillionUsd = 2m },
        });

        pricing.CostOf("gateway-internal-name", 1_000_000, 1_000_000, fallbackModelId: "configured")
            .Should().Be(3m);
    }

    [Fact]
    public void The_id_that_came_back_is_preferred_when_it_has_a_price()
    {
        var pricing = new LlmPricing(new Dictionary<string, ModelPrice>(StringComparer.OrdinalIgnoreCase)
        {
            ["configured"] = new() { InputPerMillionUsd = 1m, OutputPerMillionUsd = 2m },
            ["answered"] = new() { InputPerMillionUsd = 10m, OutputPerMillionUsd = 20m },
        });

        pricing.CostOf("answered", 1_000_000, 0, fallbackModelId: "configured").Should().Be(10m);
    }

    [Theory]
    [InlineData("gemini-2.5-pro", true)]
    [InlineData("gemini-2.5-pro-preview-06-05", true)]
    [InlineData("GEMINI-2.5-PRO", true)]
    [InlineData("nobody-priced-this", false)]
    [InlineData("", false)]
    public void Resolves_is_exact_then_longest_prefix(string model, bool expected) =>
        LlmPricing.Resolves(new LlmOptions().Pricing, model).Should().Be(expected);
}
