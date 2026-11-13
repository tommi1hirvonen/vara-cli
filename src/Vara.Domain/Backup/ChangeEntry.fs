namespace Vara.Domain

/// One change, whatever recorded or planned it: the mirror path, what happened to it, and for a move the path it came
/// from. The neutral input of a change tree, so a snapshot's rows and a backup plan can be shown the same way.
[<Struct>]
type ChangeEntry =
    { ChangedPath: string
      ChangedKind: ChangeKind
      MovedFrom: string voption }
