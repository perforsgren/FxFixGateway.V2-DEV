using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using FxFixGateway.Application.Mappers;
using FxFixGateway.Domain.Entities;
using FxFixGateway.Domain.ValueObjects;
using FxFixGateway.Infrastructure.QuickFix;
using Xunit;

namespace FxFixGateway.Tests
{
    /// <summary>
    /// Bloomberg's FIX connectivity policy (29 Oct): mutual TLS with a client certificate per session, the server's certificate
    /// checked, and sequence numbers reset only at the agreed time, which needs each session's own StartTime/EndTime.
    /// </summary>
    public class TlsAndScheduleTests
    {
        private static SessionConfiguration Config(string targetCompId, TimeSpan start, TimeSpan end, bool useSsl = false,
                                                   string sslServerName = "", string? certificate = null, string? password = null,
                                                   bool validate = false, string? ca = null) => new(
            connectionId: 1, sessionKey: "S_" + targetCompId, venueCode: "BLOOMBERG", connectionType: "initiator", description: "",
            fixVersion: "FIX.4.4", host: "69.191.198.32", port: 8228, senderCompId: "SEKOUAT_OPT", targetCompId: targetCompId,
            heartBtIntSec: 30, useSsl: useSsl, sslServerName: sslServerName, logonUsername: "", password: "", ackSupported: false,
            ackMode: "", reconnectIntervalSeconds: 30, startTime: start, endTime: end, useDataDictionary: false, dataDictionaryFile: "",
            isEnabled: true, createdUtc: DateTime.UtcNow, updatedUtc: DateTime.UtcNow, updatedBy: "", notes: "",
            resetSeqNumOnLogon: false, sslCertificate: certificate, sslCertificatePassword: password,
            sslValidateCertificates: validate, sslCaCertificate: ca);

        private static string Content(params SessionConfiguration[] configs)
            => new SessionSettingsBuilder(Path.GetTempPath()).BuildConfigFileContentPublic(configs);

        private static string Section(string content, string targetCompId)
            => content.Split("[SESSION]").Single(s => s.Contains($"TargetCompID={targetCompId}"));

        [Fact]
        public void Each_session_runs_on_its_own_start_and_end_time()
        {
            var bloomberg = Config("BLPUAT_OPT", new TimeSpan(5, 1, 0), new TimeSpan(4, 59, 0));
            var other = Config("TFSICAP", TimeSpan.Zero, TimeSpan.Zero);

            var content = Content(bloomberg, other);

            var bbg = Section(content, "BLPUAT_OPT");
            Assert.Contains("StartTime=05:01:00", bbg);
            Assert.Contains("EndTime=04:59:00", bbg);
            var tfs = Section(content, "TFSICAP");
            Assert.Contains("StartTime=00:00:00", tfs);
            Assert.Contains("EndTime=00:00:00", tfs);
        }

        [Fact]
        public void A_session_with_a_client_certificate_and_validation_writes_them()
        {
            var bloomberg = Config("BLPUAT_OPT", TimeSpan.Zero, TimeSpan.Zero, useSsl: true, sslServerName: "fixbeta-ficc.bloomberg.com",
                                   certificate: "CN=SEKOUAT_OPT", validate: true, ca: @"C:\certs\bloomberg-ca.cer");

            var bbg = Section(Content(bloomberg), "BLPUAT_OPT");

            Assert.Contains("SSLEnable=Y", bbg);
            Assert.Contains("SSLProtocols=Tls12", bbg);
            Assert.Contains("SSLValidateCertificates=Y", bbg);
            Assert.Contains("SSLServerName=fixbeta-ficc.bloomberg.com", bbg);
            Assert.Contains("SSLCertificate=CN=SEKOUAT_OPT", bbg);
            Assert.DoesNotContain("SSLCertificatePassword", bbg);   // a certificate in the store has none
            Assert.Contains(@"SSLCACertificate=C:\certs\bloomberg-ca.cer", bbg);
        }

        [Fact]
        public void A_pfx_certificate_writes_its_password()
        {
            var bloomberg = Config("BLPUAT_OPT", TimeSpan.Zero, TimeSpan.Zero, useSsl: true, certificate: @"C:\certs\bbg.pfx", password: "s3cret");
            var bbg = Section(Content(bloomberg), "BLPUAT_OPT");
            Assert.Contains(@"SSLCertificate=C:\certs\bbg.pfx", bbg);
            Assert.Contains("SSLCertificatePassword=s3cret", bbg);
        }

        [Fact]
        public void A_tls_session_without_the_new_settings_is_as_before()
        {
            var plain = Config("FXOHUB", TimeSpan.Zero, TimeSpan.Zero, useSsl: true, sslServerName: "fix.example.com");
            var section = Section(Content(plain), "FXOHUB");
            Assert.Contains("SSLValidateCertificates=N", section);
            Assert.DoesNotContain("SSLCertificate", section);
            Assert.DoesNotContain("SSLCACertificate", section);
        }

        [Fact]
        public void Copies_and_the_dto_keep_the_tls_settings()
        {
            var config = Config("BLPUAT_OPT", TimeSpan.Zero, TimeSpan.Zero, useSsl: true, certificate: "CN=SEKOUAT_OPT", password: "pw",
                                validate: true, ca: "CN=Bloomberg CA");
            foreach (var copy in new[] { config.WithHost("h"), config.WithPort(1), config.WithEnabled(false),
                                         config.WithSSLTunnel(false, "", null, null, ""),
                                         SessionMapper.ToConfiguration(SessionMapper.ToDto(new FixSession(config))) })
            {
                Assert.Equal("CN=SEKOUAT_OPT", copy.SslCertificate);
                Assert.Equal("pw", copy.SslCertificatePassword);
                Assert.True(copy.SslValidateCertificates);
                Assert.Equal("CN=Bloomberg CA", copy.SslCaCertificate);
            }
        }

        [Fact]
        public void Copies_keep_the_ssl_tunnel()
        {
            var config = Config("X", TimeSpan.Zero, TimeSpan.Zero).WithSSLTunnel(true, "remote", 443, 7000, "sni");
            foreach (var copy in new[] { config.WithHost("h"), config.WithPort(1), config.WithEnabled(false) })
            {
                Assert.True(copy.UseSSLTunnel);
                Assert.Equal(7000, copy.SslLocalPort);
            }
        }

        // ── The client certificate check ─────────────────────────────────────

        private static readonly DateTime Now = DateTime.UtcNow;

        /// <summary>A self-signed certificate for CN=SEKOUAT_OPT in a temporary file: .pfx with its key, or .cer without.</summary>
        private static string CertificateFile(DateTime notBefore, DateTime notAfter, string password = "pw", bool withKey = true)
        {
            using var rsa = RSA.Create(2048);
            var request = new CertificateRequest("CN=SEKOUAT_OPT", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            using var certificate = request.CreateSelfSigned(notBefore, notAfter);
            var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + (withKey ? ".pfx" : ".cer"));
            File.WriteAllBytes(path, withKey ? certificate.Export(X509ContentType.Pfx, password) : certificate.Export(X509ContentType.Cert));
            return path;
        }

        private static (string? Error, string? Warning) Check(string? certificate, string? password = null, bool useSsl = true)
        {
            ClientCertificateCheck.Check(Config("BLPUAT_OPT", TimeSpan.Zero, TimeSpan.Zero, useSsl: useSsl, certificate: certificate, password: password),
                                         Now, out var error, out var warning);
            return (error, warning);
        }

        [Fact]
        public void A_valid_pfx_with_its_key_is_fine()
            => Assert.Equal((null, null), Check(CertificateFile(Now.AddDays(-1), Now.AddYears(2)), "pw"));

        [Fact]
        public void No_client_certificate_or_no_tls_is_not_checked()
        {
            Assert.Equal((null, null), Check(null));
            Assert.Equal((null, null), Check("C:/missing.pfx", useSsl: false));
        }

        [Fact]
        public void A_missing_certificate_is_an_error()
            => Assert.StartsWith("'/no/such/bbg.pfx' is neither a file nor a valid certificate", Check("/no/such/bbg.pfx").Error);

        [Fact]
        public void A_certificate_without_its_private_key_is_an_error()
            => Assert.Contains("has no private key", Check(CertificateFile(Now.AddDays(-1), Now.AddYears(2), withKey: false)).Error);

        [Fact]
        public void A_wrong_password_is_an_error()
            => Assert.Contains("cannot be read", Check(CertificateFile(Now.AddDays(-1), Now.AddYears(2)), "wrong").Error);

        [Fact]
        public void An_expired_certificate_is_an_error()
            => Assert.Contains("expired", Check(CertificateFile(Now.AddYears(-2), Now.AddDays(-1)), "pw").Error);

        [Fact]
        public void A_certificate_that_expires_within_30_days_is_a_warning()
        {
            var (error, warning) = Check(CertificateFile(Now.AddYears(-2), Now.AddDays(10)), "pw");
            Assert.Null(error);
            Assert.EndsWith("renew it", warning);
        }
    }
}
