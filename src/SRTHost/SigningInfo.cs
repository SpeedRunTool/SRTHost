using System.Security.Cryptography.X509Certificates;

namespace SRTHost
{
    public static class SigningInfo
    {
        public static X509Certificate? GetSigningInfo(string location)
        {
            try
            {
                return X509Certificate.CreateFromSignedFile(location);
            }
            catch
            {
                return null;
            }
        }

        public static X509Certificate2? GetSigningInfo2(string location)
        {
            try
            {
                X509Certificate? signInfo = GetSigningInfo(location);
                if (signInfo is not null)
                    return new X509Certificate2(signInfo);
                else
                    return null;
            }
            catch
            {
                return null;
            }
        }
    }
}
