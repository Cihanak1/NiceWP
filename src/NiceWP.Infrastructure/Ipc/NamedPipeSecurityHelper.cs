using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;

namespace NiceWP.Infrastructure.Ipc;

/// <summary>
/// Configures security for named pipes restricted to the current user session on the local machine.
/// </summary>
public static class NamedPipeSecurityHelper
{
    public static PipeSecurity CreateCurrentUserSecurity()
    {
        var security = new PipeSecurity();
        var currentUser = WindowsIdentity.GetCurrent().User;
        if (currentUser != null)
        {
            security.AddAccessRule(new PipeAccessRule(
                currentUser,
                PipeAccessRights.ReadWrite,
                AccessControlType.Allow));
        }

        return security;
    }

    public static NamedPipeServerStream CreateServerStream(string pipeName, int maxInstances = 1)
    {
        var security = CreateCurrentUserSecurity();
        return NamedPipeServerStreamAcl.Create(
            pipeName,
            PipeDirection.InOut,
            maxInstances,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous,
            inBufferSize: 4096,
            outBufferSize: 4096,
            security);
    }
}
