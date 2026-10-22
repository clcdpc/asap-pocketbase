using System.Runtime.ExceptionServices;
using System.Net;
using Clc.Polaris.Api;
using Clc.Polaris.Api.Configuration;
using Clc.Polaris.Api.Models;
using Clc.Rest;
using Microsoft.Extensions.Logging;

namespace Asap.Web.Features.Patron;

public sealed partial class PolarisPatronProvider
{
    private static bool IsExpectedProviderFailure(Exception exception) =>
        exception is HttpRequestException or TimeoutException or OperationCanceledException or
            System.Text.Json.JsonException or Newtonsoft.Json.JsonException;

    private static void CheckResponse<T>(
        IRestResponse<T> response, CancellationToken cancellationToken,
        bool mutationDispatched, string? mutationPath)
    {
        // Rest.Client beta.3 stores preparation, transport, body-read and formatting
        // faults in Exception. Mutation response-content disposal is isolated at the
        // dispatched response boundary, after the package has parsed the result.
        // Preserve every genuine pre-result fault and actual caller cancellation.
        if (response.Exception is { } exception)
        {
            if (!IsExpectedProviderFailure(exception) ||
                exception is OperationCanceledException && cancellationToken.IsCancellationRequested)
            {
                ExceptionDispatchInfo.Capture(exception).Throw();
            }

            // Keep the established mutation classifications for expected transport and
            // formatter faults. These faults precede complete response evidence and
            // callers retain their existing operational/ambiguous handling.
            if (mutationDispatched && mutationPath?.Contains("/holdrequest", StringComparison.Ordinal) == true)
            {
                if (response.Data is null)
                {
                    return;
                }

                throw Operational("polaris_hold_response_failed", exception);
            }
            if (mutationDispatched && mutationPath?.Contains("/patron/", StringComparison.Ordinal) == true)
            {
                var code = exception is System.Text.Json.JsonException or Newtonsoft.Json.JsonException
                    ? "polaris_pickup_update_protocol_failed"
                    : "polaris_pickup_update_failed";
                throw Operational(code, exception);
            }

            ExceptionDispatchInfo.Capture(exception).Throw();
        }

        // The pinned client parses Data before it disposes the response. A caller token
        // canceled by that disposal must not erase an already returned mutation response;
        // the owning provider still applies its strict status and identity classifier.
        // A missing response or parsed value remains an unknown result.
        if (mutationDispatched && response.Exception is null &&
            response.Response is not null && response.Data is not null)
        {
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
    }

    private sealed class DispatchAwarePapiClient(
        HttpClient httpClient,
        PapiSettings settings,
        ILogger<PolarisPatronProvider> logger)
        : PapiClient(httpClient, settings)
    {
        public bool MutationDispatched { get; private set; }
        public string? MutationPath { get; private set; }
        private Exception? authenticationFailure;

        public async Task<IRestResponse<T>> CallAsync<T>(
            Func<Task<IRestResponse<T>>> call, CancellationToken cancellationToken)
        {
            IRestResponse<T> response;
            try
            {
                response = await call();
            }
            catch (InvalidOperationException) when (authenticationFailure is not null && !MutationDispatched)
            {
                // The package replaces a failed protected-token response with an
                // InvalidOperationException. Preserve the original classification,
                // including defects that Rest.Client captured during authentication.
                ExceptionDispatchInfo.Capture(authenticationFailure).Throw();
                throw;
            }
            authenticationFailure = null;
            CheckResponse(response, cancellationToken, MutationDispatched, MutationPath);
            return response;
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = request.RequestUri!.AbsolutePath;
            var isMutationRequest = IsMutationRequest(request.Method, path);
            if (!path.Contains("/authenticator/staff", StringComparison.Ordinal))
            {
                authenticationFailure = null;
            }
            // Observe the package's transport hook; PAPI still owns routing,
            // signing, serialization and staff-token acquisition. Authentication
            // requests are reads for mutation-safety purposes.
            if (isMutationRequest)
            {
                MutationDispatched = true;
                MutationPath = path;
            }
            try
            {
                var response = await base.SendAsync(request, cancellationToken);
                if (isMutationRequest)
                {
                    response.Content = new MutationResponseContent(
                        response.Content,
                        RecordMutationResponseCleanupFailure);
                }
                if (path.Contains("/authenticator/staff", StringComparison.Ordinal) && !response.IsSuccessStatusCode)
                {
                    authenticationFailure = new PolarisOperationalException("polaris_staff_authentication_failed", "Polaris staff authentication was unavailable.");
                }
                return response;
            }
            catch (Exception exception) when (path.Contains("/authenticator/staff", StringComparison.Ordinal))
            {
                authenticationFailure = IsExpectedProviderFailure(exception)
                    ? Operational("polaris_staff_authentication_failed", exception) : exception;
                throw;
            }
        }

        private static bool IsMutationRequest(HttpMethod method, string path) =>
            method == HttpMethod.Post && path.EndsWith("/holdrequest", StringComparison.Ordinal) ||
            method == HttpMethod.Put &&
            (path.Contains("/holdrequest/", StringComparison.Ordinal) ||
             path.Contains("/patron/", StringComparison.Ordinal));

        private void RecordMutationResponseCleanupFailure(Exception cause)
        {
            var diagnostic = new PolarisMutationResponseCleanupDiagnostic(cause);
            try
            {
                logger.Log(
                    LogLevel.Warning,
                    new EventId(36801, "PolarisMutationResponseContentCleanupFailed"),
                    diagnostic,
                    exception: null,
                    static (state, _) => state.ToString());
            }
            catch (Exception)
            {
                // Diagnostic logging cannot replace an already parsed mutation result.
            }
        }

        public override async Task<T?> FormatResponseAsync<T>(
            HttpResponseMessage response, string? content, CancellationToken cancellationToken = default)
            where T : default
        {
            if (typeof(T) != typeof(ProtectedToken))
            {
                return await base.FormatResponseAsync<T>(response, content, cancellationToken);
            }
            try
            {
                // The package owns token usability/expiry. This evidence is used
                // only if it rejects the authentication response before the real
                // operation; a completed call clears it before inspecting errors.
                authenticationFailure ??= new PolarisOperationalException("polaris_staff_authentication_failed", "Polaris did not return usable staff authentication.");
                return await base.FormatResponseAsync<T>(response, content, cancellationToken);
            }
            catch (Exception exception)
            {
                authenticationFailure = IsExpectedProviderFailure(exception)
                    ? Operational("polaris_staff_authentication_failed", exception) : exception;
                throw;
            }
        }

        private sealed class MutationResponseContent : HttpContent
        {
            private readonly HttpContent originalContent;
            private readonly Action<Exception> onCleanupFailure;
            private int disposeStarted;

            public MutationResponseContent(
                HttpContent originalContent,
                Action<Exception> onCleanupFailure)
            {
                this.originalContent = originalContent;
                this.onCleanupFailure = onCleanupFailure;
                foreach (var header in originalContent.Headers)
                {
                    Headers.TryAddWithoutValidation(header.Key, header.Value);
                }
            }

            protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
                originalContent.CopyToAsync(stream, context);

            protected override Task SerializeToStreamAsync(
                Stream stream, TransportContext? context, CancellationToken cancellationToken) =>
                originalContent.CopyToAsync(stream, context, cancellationToken);

            protected override bool TryComputeLength(out long length)
            {
                if (originalContent.Headers.ContentLength is long contentLength)
                {
                    length = contentLength;
                    return true;
                }

                length = 0;
                return false;
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing && Interlocked.Exchange(ref disposeStarted, 1) == 0)
                {
                    try
                    {
                        originalContent.Dispose();
                    }
                    catch (Exception exception)
                    {
                        onCleanupFailure(exception);
                    }
                }

                base.Dispose(disposing);
            }
        }
    }
}

internal sealed class PolarisMutationResponseCleanupDiagnostic(Exception cause)
{
    private readonly Exception originalException = cause;

    internal Exception GetOriginalException() => originalException;

    public override string ToString() =>
        $"Polaris mutation response content cleanup failed ({originalException.GetType().Name}).";
}

/// <summary>Positive evidence that the requested mutation never reached the transport hook.</summary>
public sealed class PolarisMutationNotDispatchedException(Exception cause)
    : Exception("The Polaris mutation was not dispatched.", cause)
{
    public void RethrowCause() => ExceptionDispatchInfo.Capture(InnerException!).Throw();
}

public sealed class PolarisPickupRejectedException(int papiErrorCode)
    : PolarisOperationalException("polaris_pickup_rejected", "Polaris rejected the pickup preference without changing it.")
{
    public int PapiErrorCode { get; } = papiErrorCode;
}
