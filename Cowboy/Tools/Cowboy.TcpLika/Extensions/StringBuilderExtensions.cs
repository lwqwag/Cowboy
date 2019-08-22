using System.Text;

namespace Cowboy.TcpLika
{
    internal static class StringBuilderExtensions
    {
        private readonly static char[] Crcf = new char[] { '\r', '\n' };

        public static void AppendFormatWithCrCf(this StringBuilder builder, string format, object arg)
        {
            builder.AppendFormat(format, arg);
            builder.Append(Crcf);
        }

        public static void AppendFormatWithCrCf(this StringBuilder builder, string format, params object[] args)
        {
            builder.AppendFormat(format, args);
            builder.Append(Crcf);
        }

        public static void AppendWithCrCf(this StringBuilder builder, string text)
        {
            builder.Append(text);
            builder.Append(Crcf);
        }

        public static void AppendWithCrCf(this StringBuilder builder)
        {
            builder.Append(Crcf);
        }
    }
}
