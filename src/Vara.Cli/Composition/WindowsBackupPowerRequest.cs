using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Vara.Cli.Composition;

internal interface IBackupPowerRequestFactory
{
    IDisposable? Acquire();
}

internal interface IWindowsPowerRequestApi
{
    nint Create(string reason);
    void SetSystemRequired(nint handle);
    void ClearSystemRequired(nint handle);
    void Close(nint handle);
}

internal sealed class PowerRequestException(string message, Exception? innerException = null)
    : Exception(message, innerException);

internal sealed class WindowsBackupPowerRequestFactory : IBackupPowerRequestFactory
{
    public static WindowsBackupPowerRequestFactory Instance { get; } = new(new WindowsPowerRequestApi());

    private readonly IWindowsPowerRequestApi _api;

    internal WindowsBackupPowerRequestFactory(IWindowsPowerRequestApi api) => _api = api;

    public IDisposable? Acquire()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        return new WindowsBackupPowerRequest(_api);
    }
}

internal sealed class WindowsBackupPowerRequest : IDisposable
{
    private readonly IWindowsPowerRequestApi _api;
    private nint _handle;

    internal WindowsBackupPowerRequest(IWindowsPowerRequestApi api)
    {
        _api = api;
        var handle = api.Create("Vara backup in progress");

        try
        {
            api.SetSystemRequired(handle);
            _handle = handle;
        }
        catch (PowerRequestException activationException)
        {
            try
            {
                api.Close(handle);
            }
            catch (PowerRequestException closeException)
            {
                throw new PowerRequestException(
                    $"{activationException.Message} Closing the power-request handle also failed: {closeException.Message}",
                    activationException);
            }

            throw;
        }
    }

    public void Dispose()
    {
        var handle = _handle;
        if (handle == 0)
        {
            return;
        }

        _handle = 0;
        List<string>? failures = null;

        try
        {
            _api.ClearSystemRequired(handle);
        }
        catch (PowerRequestException exception)
        {
            (failures ??= []).Add(exception.Message);
        }

        try
        {
            _api.Close(handle);
        }
        catch (PowerRequestException exception)
        {
            (failures ??= []).Add(exception.Message);
        }

        if (failures is not null)
        {
            throw new PowerRequestException(string.Join(" ", failures));
        }
    }
}

internal sealed partial class WindowsPowerRequestApi : IWindowsPowerRequestApi
{
    private const uint RequestContextVersion = 0;
    private const uint RequestContextSimpleString = 0x0000_0001;
    private const int PowerRequestSystemRequired = 1;

    public nint Create(string reason)
    {
        var reasonPointer = Marshal.StringToHGlobalUni(reason);
        try
        {
            var context = new PowerRequestContext
            {
                Version = RequestContextVersion,
                Flags = RequestContextSimpleString,
                Reason = new PowerRequestContextReason { SimpleReasonString = reasonPointer },
            };

            var handle = PowerCreateRequest(ref context);
            if (handle == 0 || handle == new nint(-1))
            {
                throw NativeFailure("PowerCreateRequest");
            }

            return handle;
        }
        finally
        {
            Marshal.FreeHGlobal(reasonPointer);
        }
    }

    public void SetSystemRequired(nint handle)
    {
        if (!PowerSetRequest(handle, PowerRequestSystemRequired))
        {
            throw NativeFailure("PowerSetRequest");
        }
    }

    public void ClearSystemRequired(nint handle)
    {
        if (!PowerClearRequest(handle, PowerRequestSystemRequired))
        {
            throw NativeFailure("PowerClearRequest");
        }
    }

    public void Close(nint handle)
    {
        if (!CloseHandle(handle))
        {
            throw NativeFailure("CloseHandle");
        }
    }

    private static PowerRequestException NativeFailure(string operation)
    {
        var exception = new Win32Exception(Marshal.GetLastWin32Error());
        return new PowerRequestException($"{operation} failed: {exception.Message}", exception);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PowerRequestContext
    {
        public uint Version;
        public uint Flags;
        public PowerRequestContextReason Reason;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct PowerRequestContextReason
    {
        [FieldOffset(0)]
        public nint SimpleReasonString;

        [FieldOffset(0)]
        public DetailedPowerRequestReason Detailed;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DetailedPowerRequestReason
    {
        public nint LocalizedReasonModule;
        public uint ResourceId;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "PowerCreateRequest", SetLastError = true)]
    private static partial nint PowerCreateRequest(ref PowerRequestContext context);

    [LibraryImport("kernel32.dll", EntryPoint = "PowerSetRequest", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PowerSetRequest(nint powerRequest, int requestType);

    [LibraryImport("kernel32.dll", EntryPoint = "PowerClearRequest", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PowerClearRequest(nint powerRequest, int requestType);

    [LibraryImport("kernel32.dll", EntryPoint = "CloseHandle", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);
}
