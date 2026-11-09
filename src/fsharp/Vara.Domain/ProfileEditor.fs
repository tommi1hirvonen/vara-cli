namespace Vara.Domain

/// A line the next screen shows above its contents: the result of what the user just did.
[<RequireQualifiedAccess>]
type Notice =
    | Saved of name: string
    | Deleted of name: string
    /// Save was chosen and the draft's current validation failed.
    | CannotSave of ConfigError
    | WriteFailed of ConfigWriteError

/// A draft being edited, and the saved profiles it is edited among. `Saved` is the in-memory list: it changes only when
/// a write has succeeded.
type Session =
    { Saved: Profile list
      Draft: ProfileDraft
      Notice: Notice option }

/// Which screen of the profile editor is showing.
[<RequireQualifiedAccess>]
type Page =
    | Fields
    | Sources
    | Source of index: int
    | Entries of index: int * list: StringList
    | Retention
    | Concurrency

[<RequireQualifiedAccess>]
type MenuInput =
    | Open of index: int
    | Add
    | StartDelete
    | Quit

[<RequireQualifiedAccess>]
type PickerInput =
    | Pick of index: int
    | Cancel

[<RequireQualifiedAccess>]
type ConfirmInput =
    | Delete
    | Keep

[<RequireQualifiedAccess>]
type FieldsInput =
    | SetName of string option
    | SetTarget of string option
    | ManageSources
    | EditRetention
    | EditConcurrency
    | Save
    | Discard

[<RequireQualifiedAccess>]
type SourcesInput =
    | Add
    | Open of index: int
    | Back

[<RequireQualifiedAccess>]
type SourceInput =
    | SetPath of string option
    | SetRecursive of bool
    | OpenList of StringList
    | Remove
    | Back

[<RequireQualifiedAccess>]
type EntriesInput =
    | Add of string
    | Remove of index: int
    | Done

[<RequireQualifiedAccess>]
type RetentionInput =
    | Set of RetentionKey * string option
    | Clear
    | Back

[<RequireQualifiedAccess>]
type ConcurrencyInput =
    | Set of ConcurrencyKey * string option
    | Clear
    | Back

/// What a user can do, by the screen it is done on, and what the loop reports back about a write.
[<RequireQualifiedAccess>]
type Input =
    | Menu of MenuInput
    | Picker of PickerInput
    | Confirm of ConfirmInput
    | Fields of FieldsInput
    | Sources of SourcesInput
    | Source of SourceInput
    | Entries of EntriesInput
    | Retention of RetentionInput
    | Concurrency of ConcurrencyInput
    | Persisted
    | PersistFailed of ConfigWriteError
    /// Ctrl+C: discards any draft and leaves.
    | Interrupt

/// A list written, and what to show once it has been: where the editor goes on success or on failure.
type Pending =
    { Candidate: Profile list
      Done: Notice
      Back: EditorState }

/// The screens of `vara profiles`. The loop shows one, asks its question, and feeds the answer to `update`.
and [<RequireQualifiedAccess>] EditorState =
    | MainMenu of profiles: Profile list * notice: Notice option
    | PickingDelete of profiles: Profile list
    | ConfirmingDelete of profiles: Profile list * profile: Profile
    | Editing of Session * Page
    /// A write is out. Nothing but its result (or an interrupt) moves the editor on.
    | Persisting of Pending
    | Exited

/// What the loop does for the editor. The editor never touches a file: it asks.
[<RequireQualifiedAccess>]
type Effect =
    /// Write this whole list of profiles to the configuration file, and answer `Persisted` or `PersistFailed`.
    | Persist of Profile list
