namespace Vara.Engine

open System
open Microsoft.Data.Sqlite

type internal CommandOperation =
    | Text of string
    | Transaction of (SqliteTransaction | null)
    | Parameters of (string * obj) list

type internal ParameterBuilder() =
    member _.Yield(parameter: string * 'T) =
        let name, value = parameter
        let value = box value |> Option.ofObj |> Option.defaultValue (DBNull.Value :> obj)

        [ name, value ]

    member _.Zero() = []
    member _.Combine(first, second) = first @ second
    member _.Delay(generator) = generator ()
    member _.Run(parameters) = Parameters parameters

/// Builds and configures a SQLite command.
type internal CommandBuilder(connection: SqliteConnection) =
    member _.Yield(operation: CommandOperation) = [ operation ]
    member _.Zero() = []
    member _.Combine(first, second) = first @ second
    member _.Delay(generator) = generator ()

    member _.Run(operations: CommandOperation list) =
        let command = connection.CreateCommand()

        try
            for operation in operations do
                match operation with
                | Text commandText -> command.CommandText <- commandText
                | Transaction transaction -> command.Transaction <- transaction
                | Parameters parameters ->
                    for name, value in parameters do
                        command.Parameters.AddWithValue(name, value) |> ignore

            command
        with _ ->
            command.Dispose()
            reraise ()

module internal CommandBuilder =
    let parameters = ParameterBuilder()

    let text (commandText: string) = Text commandText

    let transaction (transaction: SqliteTransaction | null) = Transaction transaction

    let command (connection: SqliteConnection) = CommandBuilder(connection)
