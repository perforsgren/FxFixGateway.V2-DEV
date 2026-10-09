using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace FxFixGateway.Infrastructure.PostMarker
{
    /// <summary>
    /// PostMarker (Tullett FXO Hub) settings. Enabled, ServiceUrl, ProxyAddress and TestFlag come
    /// from the gateway's appsettings.json; the account — UserName, Password, SystemId — from the
    /// "PostMarker" section of the shared fx_appsettings.json, so the password is not in this repository.
    /// </summary>
    public sealed class PostMarkerSettings
    {
        public bool Enabled { get; init; }

        /// <summary>PostMarker's SOAP endpoint (the SDK's WEB_SERVICE_ADDRESS).</summary>
        public string ServiceUrl { get; init; } = "https://www.postmarker.com/pm2svc/PostMarker.asmx";

        /// <summary>HTTP proxy, used with the gateway user's Windows credentials. Empty for none.</summary>
        public string ProxyAddress { get; init; } = string.Empty;

        /// <summary>Connects as a test session. Off in production.</summary>
        public bool TestFlag { get; init; }

        public string UserName { get; init; } = string.Empty;

        public string Password { get; init; } = string.Empty;

        public int SystemId { get; init; }

        /// <summary>Why the account could not be read from fx_appsettings.json, or null.</summary>
        public string? AccountError { get; init; }

        /// <summary>True when the account part (from fx_appsettings.json) is filled in.</summary>
        public bool HasAccount =>
            !string.IsNullOrWhiteSpace(UserName) && !string.IsNullOrWhiteSpace(Password) && SystemId > 0;

        /// <summary>
        /// Reads the "PostMarker" section from the gateway's configuration and the account from
        /// <paramref name="sharedConfigPath"/> (fx_appsettings.json). Never throws: a missing or
        /// unreadable shared file leaves the account empty with AccountError set, and the service
        /// then logs it and stays idle — the rest of the gateway starts as usual.
        /// </summary>
        public static PostMarkerSettings Load(IConfiguration configuration, string? sharedConfigPath)
        {
            var section = configuration.GetSection("PostMarker");
            var defaults = new PostMarkerSettings();

            var userName = string.Empty;
            var password = string.Empty;
            var systemId = 0;
            string? accountError = null;

            try
            {
                if (string.IsNullOrWhiteSpace(sharedConfigPath) || !File.Exists(sharedConfigPath))
                {
                    accountError = $"fx_appsettings.json not found ({sharedConfigPath})";
                }
                else
                {
                    var options = new JsonDocumentOptions
                    {
                        CommentHandling = JsonCommentHandling.Skip,
                        AllowTrailingCommas = true
                    };

                    using var document = JsonDocument.Parse(File.ReadAllText(sharedConfigPath), options);

                    if (document.RootElement.TryGetProperty("PostMarker", out var account))
                    {
                        userName = GetString(account, "UserName");
                        password = GetString(account, "Password");
                        systemId = account.TryGetProperty("SystemId", out var id) && id.TryGetInt32(out var value) ? value : 0;
                    }
                    else
                    {
                        accountError = "no PostMarker section in fx_appsettings.json";
                    }
                }
            }
            catch (Exception ex)
            {
                accountError = "fx_appsettings.json could not be read: " + ex.Message;
            }

            return new PostMarkerSettings
            {
                Enabled = section.GetValue("Enabled", false),
                ServiceUrl = section.GetValue("ServiceUrl", defaults.ServiceUrl) ?? defaults.ServiceUrl,
                ProxyAddress = section.GetValue("ProxyAddress", string.Empty) ?? string.Empty,
                TestFlag = section.GetValue("TestFlag", false),
                UserName = userName,
                Password = password,
                SystemId = systemId,
                AccountError = accountError
            };
        }

        private static string GetString(JsonElement element, string name) =>
            element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? string.Empty
                : string.Empty;
    }
}