using System.Security.Cryptography;

namespace FulcrumFS.Utilities;

internal static class SecureGuid
{
    public static Guid Create()
    {
        byte[] bytes = new byte[16];
        RandomNumberGenerator.Fill(bytes);
        return new Guid(bytes);
    }
}
