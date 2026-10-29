using System;
using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using FxFixGateway.Domain.ValueObjects;

namespace FxFixGateway.Infrastructure.QuickFix
{
    /// <summary>
    /// Checks a session's TLS client certificate at start, found the way QuickFIX/n finds it (a file path, else the subject in
    /// the Windows store "My", LocalMachine then CurrentUser, valid now). QuickFIX/n only writes a certificate it cannot load to
    /// the console and connects without one, so the venue refuses the TLS handshake with nothing in our log; here it is an error.
    /// </summary>
    public static class ClientCertificateCheck
    {
        /// <summary>A certificate this close to its end is reported (Bloomberg's last two years; renewed in the Enterprise Console).</summary>
        public static readonly TimeSpan RenewWarning = TimeSpan.FromDays(30);

        /// <summary>
        /// Why the session's client certificate cannot be used (<paramref name="error"/>), or that it is about to expire
        /// (<paramref name="warning"/>); both null when it is fine or the session has none (no TLS, an SSL tunnel, no
        /// SslCertificate).
        /// </summary>
        public static void Check(SessionConfiguration config, DateTime nowUtc, out string? error, out string? warning)
        {
            if (config == null) throw new ArgumentNullException(nameof(config));
            error = null;
            warning = null;
            if (!config.UseSsl || config.UseSSLTunnel || string.IsNullOrEmpty(config.SslCertificate))
                return;

            X509Certificate2? certificate;
            try
            {
                certificate = Load(config.SslCertificate, config.SslCertificatePassword);
            }
            catch (Exception ex) when (ex is CryptographicException || ex is IOException || ex is UnauthorizedAccessException)
            {
                error = $"'{config.SslCertificate}' cannot be read: {ex.Message}";
                return;
            }

            if (certificate == null)
            {
                error = $"'{config.SslCertificate}' is neither a file nor a valid certificate in the Windows store (My, LocalMachine or CurrentUser)";
                return;
            }
            if (!certificate.HasPrivateKey)
            {
                error = $"'{certificate.Subject}' has no private key (a client certificate needs one: export it as .pfx with the key)";
                return;
            }
            var notAfterUtc = certificate.NotAfter.ToUniversalTime();
            var notBeforeUtc = certificate.NotBefore.ToUniversalTime();
            if (notAfterUtc <= nowUtc)
            {
                error = $"'{certificate.Subject}' expired {notAfterUtc:yyyy-MM-dd HH:mm} UTC";
                return;
            }
            if (notBeforeUtc > nowUtc)
            {
                error = $"'{certificate.Subject}' is valid only from {notBeforeUtc:yyyy-MM-dd HH:mm} UTC";
                return;
            }
            if (notAfterUtc - nowUtc <= RenewWarning)
                warning = $"'{certificate.Subject}' expires {notAfterUtc:yyyy-MM-dd HH:mm} UTC: renew it";
        }

        // As QuickFIX/n's SslCertCache: a file (with its password when one is given), else the store by subject.
        private static X509Certificate2? Load(string name, string password)
        {
            if (File.Exists(name))
                return string.IsNullOrEmpty(password) ? new X509Certificate2(name) : new X509Certificate2(name, password);
            return FromStore(name, StoreLocation.LocalMachine) ?? FromStore(name, StoreLocation.CurrentUser);
        }

        private static X509Certificate2? FromStore(string name, StoreLocation location)
        {
            try
            {
                using var store = new X509Store(StoreName.My, location);
                store.Open(OpenFlags.ReadOnly);
                var valid = store.Certificates.Find(X509FindType.FindByTimeValid, DateTime.Now, false);
                var found = valid.Find(name.Contains("CN=") ? X509FindType.FindBySubjectDistinguishedName : X509FindType.FindBySubjectName, name, false);
                return found.Count == 0 ? null : found[0];
            }
            catch (CryptographicException)
            {
                return null;   // a store that cannot be opened here (e.g. LocalMachine\My off Windows)
            }
        }
    }
}
