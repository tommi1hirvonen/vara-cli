/// Reads configuration text with YamlDotNet's DOM API (no reflection, so it is AOT safe) into the neutral tree,
/// and applies the rules about the file itself: syntax, the shape of the root, and the `profiles` key.
module Vara.Engine.YamlConfig

open System.IO
open YamlDotNet.Core
open YamlDotNet.RepresentationModel
open Vara.Domain

let rec private toNode (node: YamlNode) : ConfigNode =
    match node with
    | :? YamlScalarNode as scalar -> ConfigNode.Scalar(Option.ofObj scalar.Value |> Option.defaultValue "")
    | :? YamlSequenceNode as sequence -> ConfigNode.Sequence(sequence.Children |> Seq.map toNode |> List.ofSeq)
    | :? YamlMappingNode as mapping ->
        mapping.Children
        |> Seq.choose (fun entry ->
            match entry.Key with
            | :? YamlScalarNode as key -> Some(Option.ofObj key.Value |> Option.defaultValue "", toNode entry.Value)
            | _ -> None)
        |> List.ofSeq
        |> ConfigNode.Mapping
    | _ -> ConfigNode.Unsupported

let private documents (configPath: string) (text: string) : Result<ConfigNode list, ConfigError> =
    let stream = YamlStream()

    try
        stream.Load(new StringReader(text))
        Ok(stream.Documents |> Seq.map (fun document -> toNode document.RootNode) |> List.ofSeq)
    with :? YamlException as error ->
        Error(ConfigError.Malformed(configPath, $"could not be parsed as YAML - {error.Message}"))

/// The entries under `profiles`. An empty file, and a file without that key, define zero profiles.
let parse (configPath: string) (text: string) : Result<ConfigNode list, ConfigError> =
    let malformed reason = Error(ConfigError.Malformed(configPath, reason))

    documents configPath text
    |> Result.bind (fun documents ->
        match documents with
        | [] -> Ok []
        | ConfigNode.Mapping fields :: _ ->
            match ConfigNode.tryField "profiles" fields with
            | None -> Ok []
            | Some(ConfigNode.Sequence entries) -> Ok entries
            | Some _ -> malformed "'profiles' must be a list"
        | _ -> malformed "the document root must be a mapping")

let rec private toYaml (node: ConfigNode) : YamlNode =
    match node with
    | ConfigNode.Scalar text -> YamlScalarNode text :> YamlNode
    | ConfigNode.Sequence items ->
        let sequence = YamlSequenceNode()

        for item in items do
            sequence.Add(toYaml item)

        sequence :> YamlNode
    | ConfigNode.Mapping fields ->
        let mapping = YamlMappingNode()

        for key, value in fields do
            mapping.Add(YamlScalarNode key, toYaml value)

        mapping :> YamlNode
    | ConfigNode.Unsupported -> invalidOp "A node of an unsupported type cannot be written."

/// The document for these entries under `profiles`. It is built with the DOM API, as `parse` reads it (no reflection, so
/// it is AOT safe); no comments and no layout of an earlier file survive, and the scalars are quoted as the text needs.
let render (entries: ConfigNode list) : string =
    let root = YamlMappingNode()
    root.Add(YamlScalarNode "profiles", toYaml (ConfigNode.Sequence entries))
    let stream = YamlStream(YamlDocument root)
    use writer = new StringWriter()
    stream.Save(writer, false)
    writer.ToString()
