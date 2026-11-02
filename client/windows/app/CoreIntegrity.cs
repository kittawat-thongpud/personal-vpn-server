using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;

namespace PersonalVpn;
public static class CoreIntegrity
{
    public static string Verify(string name)
    {
        bool x86 = !Environment.Is64BitProcess;
        string expected = name switch
        {
            "xray.exe" => x86 ? "DD6CF590C81D7E2818E314F57EA42611555286E13911FD2FB4F72E99B9D2C8D3" : "D5B558191CCCB1E92A5772B68B8CAB67717B26F634B0F15153215F65AE982A7E",
            "hysteria.exe" => x86 ? "92A47F51B71234DAAA6E7DF139AD4FA35160338F47AB7A2EB99C930D54BD1819" : "162EF8FE55DC7EC810DDA662908F8E65EC36BD6DA39CB87C3BD66184AB68F067",
            "sing-box.exe" => typeof(CoreIntegrity).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(x => x.Key == "SingBoxHash").Value ?? "",
            "wintun.dll" => x86 ? "D694FA46AB4CFEBCB2632D094C7AA97278EEF2F8052438621766D863AE98A931" : "E5DA8447DC2C320EDC0FC52FA01885C103DE8C118481F683643CACC3220DAFCE",
            _ => throw new InvalidOperationException("Unknown native component.")
        };
        string path = Path.Combine(AppContext.BaseDirectory, "cores", name);
        using var file = File.OpenRead(path);
        if (Convert.ToHexString(SHA256.HashData(file)) != expected) throw new InvalidOperationException("Native component checksum mismatch; reinstall the verified package.");
        return path;
    }
}