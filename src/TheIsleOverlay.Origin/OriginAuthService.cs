using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace TheIsleOverlay.Origin;

public enum OriginAuthValidationState
{
    Valid,
    Invalid,
    Unavailable
}

public static class OriginAuthService
{
    public static async Task<OriginAuthValidationState> ValidateAsync(
        HttpClient httpClient,
        string cookieHeader,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        if (string.IsNullOrWhiteSpace(cookieHeader)
            || cookieHeader.Length > 32_768
            || cookieHeader.Contains('\r')
            || cookieHeader.Contains('\n'))
        {
            return OriginAuthValidationState.Invalid;
        }

        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                new Uri(OriginStatsClient.BaseUri, "api/auth/me"));
            request.Headers.TryAddWithoutValidation("Cookie", cookieHeader);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using var response = await httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!OriginStatsClient.IsTrustedUri(response.RequestMessage?.RequestUri))
            {
                return OriginAuthValidationState.Unavailable;
            }

            if ((int)response.StatusCode is >= 300 and <= 399)
            {
                // Authentication endpoints should answer directly. A redirect
                // is treated as unavailable so no session cookie is ever
                // forwarded to a different host.
                return OriginAuthValidationState.Unavailable;
            }

            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return OriginAuthValidationState.Invalid;
            }

            if (!response.IsSuccessStatusCode)
            {
                return OriginAuthValidationState.Unavailable;
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken)
                .ConfigureAwait(false);
            try
            {
                using var document = JsonDocument.Parse(body);
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    return OriginAuthValidationState.Invalid;
                }

                if (root.TryGetProperty("user", out var user)
                    && user.ValueKind is not JsonValueKind.Null
                    && user.ValueKind is not JsonValueKind.Undefined)
                {
                    return OriginAuthValidationState.Valid;
                }

                return root.TryGetProperty("authenticated", out var authenticated)
                       && authenticated.ValueKind == JsonValueKind.True
                    ? OriginAuthValidationState.Valid
                    : OriginAuthValidationState.Invalid;
            }
            catch (JsonException)
            {
                return OriginAuthValidationState.Invalid;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return OriginAuthValidationState.Unavailable;
        }
    }
}
