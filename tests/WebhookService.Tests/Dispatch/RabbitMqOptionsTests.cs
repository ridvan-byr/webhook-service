using Microsoft.Extensions.Configuration;
using WebhookService.Infrastructure.Dispatch;
using WebhookService.Infrastructure.Dispatch.RabbitMq;

namespace WebhookService.Tests.Dispatch;

public class RabbitMqOptionsTests
{
    private static RabbitMqOptions Valid() => new() { Host = "broker.internal", Username = "service", Password = "not-a-real-password" };

    [Fact]
    public void Options_with_a_host_and_credentials_are_valid_and_the_rest_has_sensible_defaults()
    {
        var options = Valid();

        options.Validate();

        Assert.Equal(5672, options.Port);
        Assert.Equal("/", options.VirtualHost);
        Assert.False(options.UseTls);
        Assert.Equal("webhooks.deliveries", options.Exchange);
        Assert.Equal("webhooks.deliveries.due", options.Queue);
        Assert.Equal("webhooks.deliveries.poison", options.PoisonQueue);
        Assert.Equal(TimeSpan.FromSeconds(10), options.PublishTimeout);
    }

    [Fact]
    public void There_are_no_default_credentials_because_a_default_would_be_known_to_everyone()
    {
        var options = new RabbitMqOptions();

        Assert.Equal("", options.Username);
        Assert.Equal("", options.Password);
        Assert.Throws<InvalidOperationException>(options.Validate);
    }

    [Theory]
    [InlineData("Host")]
    [InlineData("Username")]
    [InlineData("Password")]
    [InlineData("Exchange")]
    [InlineData("Queue")]
    [InlineData("PoisonQueue")]
    [InlineData("VirtualHost")]
    public void A_missing_setting_is_named_in_the_error(string setting)
    {
        var options = Valid();
        typeof(RabbitMqOptions).GetProperty(setting)!.SetValue(options, "  ");

        var error = Assert.Throws<InvalidOperationException>(options.Validate);

        Assert.Contains($"Delivery:RabbitMq:{setting}", error.Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(65536)]
    public void A_port_that_cannot_exist_is_refused(int port)
    {
        var options = Valid();
        options.Port = port;

        Assert.Contains("Delivery:RabbitMq:Port", Assert.Throws<InvalidOperationException>(options.Validate).Message);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void A_publish_timeout_that_is_not_positive_is_refused(int seconds)
    {
        var options = Valid();
        options.PublishTimeout = TimeSpan.FromSeconds(seconds);

        Assert.Contains("Delivery:RabbitMq:PublishTimeout", Assert.Throws<InvalidOperationException>(options.Validate).Message);
    }

    [Fact]
    public void The_error_never_repeats_the_password()
    {
        var options = Valid();
        options.Password = "very-secret-value";
        options.Host = "";

        var error = Assert.Throws<InvalidOperationException>(options.Validate);

        Assert.DoesNotContain("very-secret-value", error.Message);
    }

    [Fact]
    public void The_text_of_the_options_never_contains_the_password()
    {
        var options = Valid();
        options.Password = "very-secret-value";

        Assert.DoesNotContain("very-secret-value", options.ToString());
    }

    [Fact]
    public void The_exchange_and_the_queues_must_all_have_different_names()
    {
        var options = Valid();
        options.PoisonQueue = options.Queue;

        Assert.Throws<InvalidOperationException>(options.Validate);
    }

    // ---------- the choice of transport ----------

    [Fact]
    public void The_database_is_the_default_transport()
    {
        Assert.Equal(DeliveryTransport.Postgres, new DeliveryOptions().Transport);
    }

    [Fact]
    public void A_transport_that_does_not_exist_is_refused()
    {
        var options = new DeliveryOptions { Transport = (DeliveryTransport)99 };

        Assert.Contains("Delivery:Transport", Assert.Throws<InvalidOperationException>(options.EnsureValid).Message);
    }

    [Theory]
    [InlineData("Postgres", DeliveryTransport.Postgres)]
    [InlineData("RabbitMq", DeliveryTransport.RabbitMq)]
    [InlineData("rabbitmq", DeliveryTransport.RabbitMq)]
    public void The_transport_is_read_from_the_settings_by_name(string value, DeliveryTransport expected)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Delivery:Transport"] = value })
            .Build();

        var options = configuration.GetSection(DeliveryOptions.SectionName).Get<DeliveryOptions>()!;

        Assert.Equal(expected, options.Transport);
    }
}
