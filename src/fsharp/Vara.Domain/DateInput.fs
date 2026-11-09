namespace Vara.Domain

open System
open System.Globalization

/// A date/time option value that could not be read: which option, and what it was given.
type DateInputError = { Option: string; Value: string }

/// The date/time options (`--at`, `--since`, `--left-at`, `--right-at`): one parser, one help example, one error.
module DateInput =
    /// What the help text of every date/time option and the error message show, so that the two cannot differ.
    let example: string = "for example, '2025-01-15' or '2025-01-15 14:30'"

    /// The description to give an option that takes a date/time: what it means, then the accepted format.
    let help (meaning: string) : string = $"{meaning} ({example})."

    // Only unambiguous ISO 8601 forms are accepted: a date, a date with a time, either with a UTC offset, and the
    // round-trip format. A space may stand in for the "T". A locale-dependent slash date is never accepted.
    let private withOffset: string[] =
        [| "yyyy-MM-ddTHH:mm:sszzz"; "yyyy-MM-dd HH:mmzzz"; "yyyy-MM-dd HH:mm:sszzz"; "o"; "O" |]

    let private withoutOffset: string[] =
        [| "yyyy-MM-dd"; "yyyy-MM-ddTHH:mm:ss"; "yyyy-MM-dd HH:mm"; "yyyy-MM-dd HH:mm:ss" |]

    /// The value as a moment. A value without an offset is in the local zone, whose offset at that time `offsetAt`
    /// gives (a parameter, so that a test fixes it).
    let parse (offsetAt: DateTime -> TimeSpan) (optionName: string) (text: string) : Result<DateTimeOffset, DateInputError> =
        match DateTimeOffset.TryParseExact(text, withOffset, CultureInfo.InvariantCulture, DateTimeStyles.None) with
        | true, exact -> Ok exact
        | false, _ ->
            match DateTime.TryParseExact(text, withoutOffset, CultureInfo.InvariantCulture, DateTimeStyles.None) with
            | true, local -> Ok(DateTimeOffset(local, offsetAt local))
            | false, _ -> Error { Option = optionName; Value = text }

    let describe (error: DateInputError) : string =
        $"Invalid value '{error.Value}' for option '{error.Option}': expected a date/time ({example})."
