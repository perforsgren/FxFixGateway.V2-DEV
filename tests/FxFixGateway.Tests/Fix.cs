using System.Text;
using FxFixGateway.Infrastructure.QuickFix;
using QF = global::QuickFix;

namespace FxFixGateway.Tests
{
    /// <summary>Builds FIX messages from "tag=value|" text and parses them as the gateway does (the session's dictionary).</summary>
    internal static class Fix
    {
        /// <summary>The repository's root (the folder with FxFixGateway.V2.sln).</summary>
        public static string RepoRoot
        {
            get
            {
                var dir = new DirectoryInfo(AppContext.BaseDirectory);
                while (dir != null && !File.Exists(Path.Combine(dir.FullName, "FxFixGateway.V2.sln"))) dir = dir.Parent;
                return dir?.FullName ?? throw new InvalidOperationException("FxFixGateway.V2.sln not found above " + AppContext.BaseDirectory);
            }
        }

        public static string BloombergDictionaryPath => Path.Combine(RepoRoot, "src", "FxFixGateway.UI", "FIX44_Bloomberg.xml");

        private static readonly Lazy<QF.DataDictionary.DataDictionary> Dictionary =
            new(() => new QF.DataDictionary.DataDictionary(BloombergDictionaryPath));

        /// <summary>
        /// A complete message from Bloomberg (BLP → SWED): header, <paramref name="body"/> ("131=X|537=1|…", each field ending
        /// in '|'), BodyLength and CheckSum.
        /// </summary>
        public static string Raw(string msgType, string body, string extraHeader = "")
        {
            var content = $"35={msgType}|49=BLP|56=SWED|34=2|52=20261022-12:00:00.000|{extraHeader}{body}".Replace('|', '\u0001');
            var head = $"8=FIX.4.4\u00019={Encoding.ASCII.GetByteCount(content)}\u0001";
            var sum = Encoding.ASCII.GetBytes(head + content).Sum(b => b) % 256;
            return head + content + $"10={sum:000}\u0001";
        }

        /// <summary>Parses <paramref name="raw"/> with FIX44_Bloomberg.xml and the gateway's message factory, as a session does.</summary>
        public static QF.Message Parse(string raw)
        {
            var factory = new LenientMessageFactory();
            var msgType = QF.Message.GetMsgType(raw);
            var message = factory.Create("FIX.4.4", msgType);
            message.FromString(raw, true, Dictionary.Value, Dictionary.Value, factory);
            return message;
        }
    }
}
