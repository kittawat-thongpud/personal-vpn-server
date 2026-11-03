using System.Reflection;

namespace PersonalVpn;

internal static class ClientVersion
{
    public static string Current => typeof(ClientVersion).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!
        .InformationalVersion.Split('+')[0];
}
