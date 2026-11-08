module Vara.Domain.Tests.ConfigErrorTests

open Xunit
open Vara.Domain

// The expected texts are the ones the C# exception types build in ProfileConfigExceptions.cs.

[<Fact>]
let ``a missing file renders the expected location`` () =
    Assert.Equal(
        "Profile configuration file not found at 'C:\\x\\profiles.yml'.",
        ConfigError.render (ConfigError.FileNotFound @"C:\x\profiles.yml")
    )

[<Fact>]
let ``a malformed file renders its path and reason`` () =
    Assert.Equal(
        "Profile configuration file 'p.yml' is malformed: 'profiles' must be a list",
        ConfigError.render (ConfigError.Malformed("p.yml", "'profiles' must be a list"))
    )

[<Fact>]
let ``an invalid profile renders its name and reason`` () =
    Assert.Equal(
        "Profile 'work' is invalid: missing required field 'target'",
        ConfigError.render (ConfigError.InvalidProfile("work", "missing required field 'target'"))
    )

[<Fact>]
let ``a duplicate name renders the name`` () =
    Assert.Equal(
        "Profile name 'work' is defined more than once in the configuration file.",
        ConfigError.render (ConfigError.DuplicateName "work")
    )

[<Fact>]
let ``an unknown profile renders the name`` () =
    Assert.Equal(
        "No profile named 'work' was found in the configuration file.",
        ConfigError.render (ConfigError.UnknownProfile "work")
    )

[<Fact>]
let ``a missing profile name renders the instruction`` () =
    Assert.Equal(
        "No profile name was given, and the current directory is not inside a profile's target root. Specify a profile name.",
        ConfigError.render ConfigError.ProfileNameRequired
    )
