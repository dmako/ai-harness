using System.Security;
using System.Security.Principal;

namespace Harness.Infrastructure.Memory;

public static class HumanActor
{
    public static string Current()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                using var identity = WindowsIdentity.GetCurrent();
                return identity.Name.Length is 0 ? "human" : "human:" + identity.Name;
            }
            var user = Environment.UserName;
            return user.Length is 0 ? "human" : "human:" + user;
        }
        catch (SecurityException) { return "human"; }
    }
}
