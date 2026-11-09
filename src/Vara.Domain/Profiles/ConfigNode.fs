namespace Vara.Domain

/// The configuration file as a tree, independent of the YAML library that read it.
[<RequireQualifiedAccess>]
type ConfigNode =
    | Scalar of string
    | Sequence of ConfigNode list
    /// Only scalar keys appear; the engine drops the others, as they can never match a lookup.
    | Mapping of (string * ConfigNode) list
    | Unsupported

module ConfigNode =
    /// The first entry whose key matches, ignoring letter case.
    let tryField (key: string) (fields: (string * ConfigNode) list) : ConfigNode option =
        fields
        |> List.tryFind (fun (name, _) -> System.String.Equals(name, key, System.StringComparison.OrdinalIgnoreCase))
        |> Option.map snd

    let describe (node: ConfigNode) : string =
        match node with
        | ConfigNode.Mapping _ -> "a mapping"
        | ConfigNode.Sequence _ -> "a nested list"
        | ConfigNode.Scalar _ -> "a scalar"
        | ConfigNode.Unsupported -> "an unsupported node type"
