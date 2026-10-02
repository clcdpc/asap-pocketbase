using System.Runtime.ExceptionServices;
using Clc.Polaris.Api;
using Clc.Polaris.Api.Configuration;
using Clc.Polaris.Api.Models;
using Clc.Rest;

namespace Asap.Web.Features.Patron;

public sealed partial class PolarisPatronProvider
{
    private static bool IsExpectedProviderFailure(Exception exception) =>
        exception is HttpRequestException or TimeoutException or OperationCanceledException or
            System.Text.Json.JsonException or Newtonsoft.Json.JsonException;

    private static void CheckResponse<T>(IRestResponse<T> response, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Rest.Client beta.3 also stores request preparation and programming errors
        // in Exception. A missing HTTP response alone is not transport evidence.
        if (response.Exception is { } exception && !IsExpectedProviderFailure(exception))
        {
            ExceptionDispatchInfo.Capture(exception).Throw();
        }
    }

    private sealed class DispatchAwarePapiClient(HttpClient httpClient, PapiSettings settings)
        : PapiClient(httpClient, settings)
    {
        public bool MutationDispatched { get; private set; }
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
            CheckResponse(response, cancellationToken);
            return response;
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = request.RequestUri!.AbsolutePath;
            if (!path.Contains("/authenticator/staff", StringComparison.Ordinal))
            {
                authenticationFailure = null;
            }
            // Observe the package's transport hook; PAPI still owns routing,
            // signing, serialization and staff-token acquisition. Authentication
            // requests are reads for mutation-safety purposes.
            if (request.Method == HttpMethod.Post && path.EndsWith("/holdrequest", StringComparison.Ordinal) ||
                request.Method == HttpMethod.Put &&
                (path.Contains("/holdrequest/", StringComparison.Ordinal) || path.Contains("/patron/", StringComparison.Ordinal)))
            {
                MutationDispatched = true;
            }
            try
            {
                var response = await base.SendAsync(request, cancellationToken);
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
    }
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
