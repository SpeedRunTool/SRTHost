using System.Security.Cryptography.X509Certificates;

namespace SRTHost
{
    public static class SigningInfo
    {
        public static X509Certificate GetSigningInfo(string location)
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

        public static X509Certificate2 GetSigningInfo2(string location)
        {
            try
            {
                return new X509Certificate2(GetSigningInfo(location));
            }
            catch
            {
                return null;
            }
        }
    }
}
