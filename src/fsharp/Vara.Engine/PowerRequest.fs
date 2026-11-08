namespace Vara.Engine

#nowarn "9" // the request context is passed to Win32 by reference

open System
open System.ComponentModel
open System.Runtime.InteropServices
open System.Threading.Tasks

/// The calls a power request makes, one function each, so that a test replaces them to force every failure. Each
/// returns the Win32 error code when it fails.
type PowerCalls =
    { Create: string -> Result<nativeint, int>
      SetSystemRequired: nativeint -> Result<unit, int>
      ClearSystemRequired: nativeint -> Result<unit, int>
      Close: nativeint -> Result<unit, int> }

/// Keeps Windows from sleeping while a backup runs. The display may still turn off: only the system-required request
/// is made (backup-execution: "Automatic system sleep is prevented during a real backup").
module PowerRequest =
    /// REASON_CONTEXT with the simple-string reason. The C union's other member is as large as the second field
    /// pair below, so `Reserved` stands in for its remainder and the structure has the size Windows expects.
    [<Struct; StructLayout(LayoutKind.Sequential)>]
    type private ReasonContext =
        val mutable Version: uint32
        val mutable Flags: uint32
        val mutable SimpleReasonString: nativeint
        val mutable Reserved: nativeint

    [<DllImport("kernel32.dll", EntryPoint = "PowerCreateRequest", SetLastError = true)>]
    extern nativeint private PowerCreateRequest(ReasonContext& context)

    [<DllImport("kernel32.dll", EntryPoint = "PowerSetRequest", SetLastError = true)>]
    extern bool private PowerSetRequest(nativeint request, int requestType)

    [<DllImport("kernel32.dll", EntryPoint = "PowerClearRequest", SetLastError = true)>]
    extern bool private PowerClearRequest(nativeint request, int requestType)

    [<DllImport("kernel32.dll", EntryPoint = "CloseHandle", SetLastError = true)>]
    extern bool private CloseHandle(nativeint handle)

    let private contextVersion = 0u
    let private contextSimpleString = 1u
    let private requestSystemRequired = 1
    let private invalidHandle = nativeint -1

    let private lastError () : Result<'a, int> = Error(Marshal.GetLastWin32Error())

    let private create (reason: string) : Result<nativeint, int> =
        let text = Marshal.StringToHGlobalUni reason

        try
            let mutable context = ReasonContext()
            context.Version <- contextVersion
            context.Flags <- contextSimpleString
            context.SimpleReasonString <- text
            let handle = PowerCreateRequest(&context)

            if handle = 0n || handle = invalidHandle then lastError () else Ok handle
        finally
            Marshal.FreeHGlobal text

    let private ofBool (succeeded: bool) : Result<unit, int> = if succeeded then Ok() else lastError ()

    /// The real Win32 calls.
    let win32: PowerCalls =
        { Create = create
          SetSystemRequired = fun handle -> ofBool (PowerSetRequest(handle, requestSystemRequired))
          ClearSystemRequired = fun handle -> ofBool (PowerClearRequest(handle, requestSystemRequired))
          Close = fun handle -> ofBool (CloseHandle handle) }

    let private describe (operation: string) (code: int) : string =
        $"{operation} failed: {Win32Exception(code).Message}"

    let private reason = "Vara backup in progress"

    /// Creates the request and sets it. A request that was created but could not be set is closed again.
    let private acquire (calls: PowerCalls) : Result<nativeint, string> =
        match calls.Create reason with
        | Error code -> Error(describe "PowerCreateRequest" code)
        | Ok handle ->
            match calls.SetSystemRequired handle with
            | Ok() -> Ok handle
            | Error code ->
                let setFailure = describe "PowerSetRequest" code

                match calls.Close handle with
                | Ok() -> Error setFailure
                | Error closeCode ->
                    let closeFailure = describe "CloseHandle" closeCode
                    Error $"{setFailure} Closing the power-request handle also failed: {closeFailure}"

    /// Clears and closes the request. Each failure is reported and none is raised, so that the body's own result or
    /// exception is never replaced.
    let private release (calls: PowerCalls) (report: string -> unit) (handle: nativeint) : unit =
        let failures =
            [ calls.ClearSystemRequired handle |> Result.mapError (describe "PowerClearRequest")
              calls.Close handle |> Result.mapError (describe "CloseHandle") ]
            |> List.choose (function Error message -> Some message | Ok() -> None)

        match failures with
        | [] -> ()
        | messages -> report $"Warning: Windows sleep prevention could not be released: {String.Join(' ', messages)}"

    /// Runs `body` with the system kept awake. A request that cannot be made is reported through `report` and the body
    /// runs without it; the request is cleared when the body returns, throws or is cancelled.
    let withSleepPrevention (calls: PowerCalls) (report: string -> unit) (body: unit -> Task<'a>) : Task<'a> =
        task {
            match acquire calls with
            | Error message ->
                report $"Warning: Windows sleep prevention could not be activated: {message}"
                return! body ()
            | Ok handle ->
                try
                    return! body ()
                finally
                    release calls report handle
        }
