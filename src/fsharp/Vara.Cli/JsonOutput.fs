/// Writes a `JsonValue` as one compact line, once, to a writer of its own.
module Vara.Cli.JsonOutput

open System.IO
open System.Text
open System.Text.Json
open Vara.Domain

let rec private writeValue (writer: Utf8JsonWriter) (value: JsonValue) : unit =
    match value with
    | JsonValue.String text -> writer.WriteStringValue text
    | JsonValue.Bool flag -> writer.WriteBooleanValue flag
    | JsonValue.Number number -> writer.WriteNumberValue number
    | JsonValue.Time time -> writer.WriteStringValue time
    | JsonValue.Array items ->
        writer.WriteStartArray()

        for item in items do
            writeValue writer item

        writer.WriteEndArray()
    | JsonValue.Object members ->
        writer.WriteStartObject()

        for name, member' in members do
            writer.WritePropertyName name
            writeValue writer member'

        writer.WriteEndObject()

/// The document as text: one line, no indentation.
let render (value: JsonValue) : string =
    use stream = new MemoryStream()

    (use writer = new Utf8JsonWriter(stream)
     writeValue writer value)

    Encoding.UTF8.GetString(stream.ToArray())

/// Writes the document and a line break to `output`, and nothing else.
let write (output: TextWriter) (value: JsonValue) : unit =
    output.WriteLine(render value)
    output.Flush()
