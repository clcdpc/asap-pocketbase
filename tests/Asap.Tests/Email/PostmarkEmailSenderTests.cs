using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Asap.Web.Features.Email;
using Microsoft.Extensions.Logging.Abstractions;

namespace Asap.Tests.Email;

[TestClass]
public sealed class PostmarkEmailSenderTests
{
    [TestMethod]
    public async Task LiveSenderBuildsPostmarkRequestAndReturnsProviderMessageId()
    {
        var handler = new RecordingHandler(
            HttpStatusCode.OK,
            "{\"ErrorCode\":0,\"Message\":\"OK\",\"MessageID\":\"pm-123\"}");
        using var client = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://api.postmarkapp.com/")
        };
        var configuration = TestConfigurationFactory.Create();
        configuration.EmailTransport.Mode = EmailDeliveryModes.Live;
        var sender = new PostmarkEmailSender(
            client,
            configuration,
            new FakeTokenResolver(new EmailServerTokenResolution("server-secret", null)),
            NullLogger<PostmarkEmailSender>.Instance);

        var result = await sender.SendAsync(
            new EmailEnvelope(
                41,
                2,
                "operational-test:41",
                "admin@example.org",
                "noreply@example.org",
                "ASAP",
                "Test subject",
                "Plain body",
                "<p>HTML body</p>"),
            CancellationToken.None);

        Assert.AreEqual(EmailSendOutcome.Sent, result.Outcome);
        Assert.AreEqual("pm-123", result.ProviderMessageId);
        Assert.AreEqual(EmailDeliveryModes.Live, result.DeliveryMode);
        Assert.AreEqual("POST", handler.Method.Method);
        Assert.AreEqual("https://api.postmarkapp.com/email", handler.RequestUri!.ToString());
        Assert.AreEqual("server-secret", handler.ServerToken);
        Assert.IsFalse(handler.Body!.Contains("server-secret", StringComparison.Ordinal));
        using var json = JsonDocument.Parse(handler.Body);
        Assert.AreEqual("ASAP <noreply@example.org>", json.RootElement.GetProperty("From").GetString());
        Assert.AreEqual("admin@example.org", json.RootElement.GetProperty("To").GetString());
        Assert.AreEqual("Plain body", json.RootElement.GetProperty("TextBody").GetString());
    }

    [TestMethod]
    public async Task LiveReadinessReportsMissingOrInvalidCredentialWithoutExposingIt()
    {
        var configuration = TestConfigurationFactory.Create();
        configuration.EmailTransport.Mode = EmailDeliveryModes.Live;
        var sender = new PostmarkEmailSender(
            new HttpClient { BaseAddress = new Uri("https://api.postmarkapp.com/") },
            configuration,
            new FakeTokenResolver(new EmailServerTokenResolution(null, "mail_credentials_invalid")),
            NullLogger<PostmarkEmailSender>.Instance);

        var readiness = await sender.CheckReadinessAsync(2, CancellationToken.None);

        Assert.IsFalse(readiness.IsConfigured);
        Assert.AreEqual(EmailDeliveryModes.Live, readiness.DeliveryMode);
        Assert.AreEqual("mail_credentials_invalid", readiness.Code);
    }

    [TestMethod]
    public async Task ProviderAuthenticationFailureIsSafeAndNeverMarkedSent()
    {
        var handler = new RecordingHandler(
            HttpStatusCode.Unauthorized,
            "{\"ErrorCode\":10,\"Message\":\"secret provider detail\"}");
        using var client = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://api.postmarkapp.com/")
        };
        var configuration = TestConfigurationFactory.Create();
        configuration.EmailTransport.Mode = EmailDeliveryModes.Live;
        var sender = new PostmarkEmailSender(
            client,
            configuration,
            new FakeTokenResolver(new EmailServerTokenResolution("server-secret", null)),
            NullLogger<PostmarkEmailSender>.Instance);

        var exception = await Assert.ThrowsAsync<EmailTransportException>(() => sender.SendAsync(
            new EmailEnvelope(1, 2, null, "to@example.org", "from@example.org", null, "subject", "body", null),
            CancellationToken.None));

        Assert.AreEqual("provider_authentication_failed", exception.Code);
        Assert.IsFalse(exception.IsAmbiguous);
        Assert.IsFalse(exception.Message.Contains("secret provider detail", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task ProviderErrorCodeInSuccessfulHttpResponseIsRejected()
    {
        var handler = new RecordingHandler(
            HttpStatusCode.OK,
            "{\"ErrorCode\":10,\"Message\":\"provider detail\",\"MessageID\":\"not-sent\"}");
        using var client = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://api.postmarkapp.com/")
        };
        var configuration = TestConfigurationFactory.Create();
        configuration.EmailTransport.Mode = EmailDeliveryModes.Live;
        var sender = new PostmarkEmailSender(
            client,
            configuration,
            new FakeTokenResolver(new EmailServerTokenResolution("server-secret", null)),
            NullLogger<PostmarkEmailSender>.Instance);

        var exception = await Assert.ThrowsAsync<EmailTransportException>(() => sender.SendAsync(
            new EmailEnvelope(1, 2, null, "to@example.org", "from@example.org", null, "subject", "body", null),
            CancellationToken.None));

        Assert.AreEqual("provider_rejected", exception.Code);
        Assert.IsFalse(exception.IsAmbiguous);
    }

    [TestMethod]
    public async Task SuccessfulResponseWithoutMessageIdIsAmbiguous()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK, "{\"ErrorCode\":0}");
        using var client = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://api.postmarkapp.com/")
        };
        var configuration = TestConfigurationFactory.Create();
        configuration.EmailTransport.Mode = EmailDeliveryModes.Live;
        var sender = new PostmarkEmailSender(
            client,
            configuration,
            new FakeTokenResolver(new EmailServerTokenResolution("server-secret", null)),
            NullLogger<PostmarkEmailSender>.Instance);

        var exception = await Assert.ThrowsAsync<EmailTransportException>(() => sender.SendAsync(
            new EmailEnvelope(1, 2, null, "to@example.org", "from@example.org", null, "subject", "body", null),
            CancellationToken.None));

        Assert.AreEqual("provider_malformed_response", exception.Code);
        Assert.IsTrue(exception.IsAmbiguous);
    }

    private sealed class FakeTokenResolver(EmailServerTokenResolution resolution) : IEmailServerTokenResolver
    {
        public Task<EmailServerTokenResolution> ResolveAsync(int organizationId, CancellationToken cancellationToken) =>
            Task.FromResult(resolution);
    }

    private sealed class RecordingHandler(HttpStatusCode statusCode, string body) : HttpMessageHandler
    {
        public HttpMethod Method { get; private set; } = HttpMethod.Get;
        public Uri? RequestUri { get; private set; }
        public string? ServerToken { get; private set; }
        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Method = request.Method;
            RequestUri = request.RequestUri;
            ServerToken = request.Headers.GetValues("X-Postmark-Server-Token").Single();
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            var response = new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(body)
            };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            return response;
        }
    }
}
