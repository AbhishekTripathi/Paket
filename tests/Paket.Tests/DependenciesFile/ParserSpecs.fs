module paket.dependenciesFile.ParserSpecs

open Paket
open Paket.PackageSources
open NUnit.Framework
open FsUnit
open TestHelpers
open System
open Paket.Domain

[<Test>]
let ``should read emptx config``() = 
    let cfg = DependenciesFile.FromCode("")
    cfg.Options.Strict |> shouldEqual false

    cfg.Packages.Length |> shouldEqual 0
    cfg.RemoteFiles.Length |> shouldEqual 0

let configWithSourceOnly = """
source http://nuget.org/api/v2
"""

[<Test>]
let ``should read config which only contains a source``() = 
    let cfg = DependenciesFile.FromCode(configWithSourceOnly)
    cfg.Options.Strict |> shouldEqual false

    cfg.Sources.Length |> shouldEqual 1
    cfg.Sources.Head  |> shouldEqual (Nuget({ Url = "http://nuget.org/api/v2"; Authentication = None }))

let config1 = """
source "http://nuget.org/api/v2"

nuget "Castle.Windsor-log4net" "~> 3.2"
nuget "Rx-Main" "~> 2.0"
nuget "FAKE" "= 1.1"
nuget "SignalR" "= 3.3.2"
"""

[<Test>]
let ``should read simple config``() = 
    let cfg = DependenciesFile.FromCode(config1)
    cfg.Options.Strict |> shouldEqual false

    cfg.DirectDependencies.[PackageName "Rx-Main"].Range |> shouldEqual (VersionRange.Between("2.0", "3.0"))
    cfg.DirectDependencies.[PackageName "Castle.Windsor-log4net"].Range |> shouldEqual (VersionRange.Between("3.2", "4.0"))
    cfg.DirectDependencies.[PackageName "FAKE"].Range |> shouldEqual (VersionRange.Exactly "1.1")
    cfg.DirectDependencies.[PackageName "SignalR"].Range |> shouldEqual (VersionRange.Exactly "3.3.2")

let config2 = """
source "http://nuget.org/api/v2"

printfn "hello world from config"

nuget "FAKE" "~> 3.0"
nuget "Rx-Main" "~> 2.2"
nuget "MinPackage" "1.1.3"
"""

[<Test>]
let ``should read simple config with additional F# code``() = 
    let cfg = DependenciesFile.FromCode(config2)
    cfg.DirectDependencies.[PackageName "Rx-Main"].Range |> shouldEqual (VersionRange.Between("2.2", "3.0"))
    cfg.DirectDependencies.[PackageName "FAKE"].Range |> shouldEqual (VersionRange.Between("3.0", "4.0"))
    cfg.DirectDependencies.[PackageName "MinPackage"].Range |> shouldEqual (VersionRange.Exactly "1.1.3")

let config3 = """
source "https://nuget.org/api/v2" // here we are

nuget "FAKE" "~> 3.0" // born to rule
nuget "Rx-Main" "~> 2.2"
nuget "MinPackage" "1.1.3"
"""

[<Test>]
let ``should read simple config with comments``() = 
    let cfg = DependenciesFile.FromCode(config3)
    cfg.DirectDependencies.[PackageName "Rx-Main"].Range |> shouldEqual (VersionRange.Between("2.2", "3.0"))
    (cfg.Packages |> List.find (fun p -> p.Name = PackageName "Rx-Main")).Sources |> List.head |> shouldEqual PackageSources.DefaultNugetSource
    cfg.DirectDependencies.[PackageName "FAKE"].Range |> shouldEqual (VersionRange.Between("3.0", "4.0"))
    (cfg.Packages |> List.find (fun p -> p.Name = PackageName "FAKE")).Sources |> List.head  |> shouldEqual PackageSources.DefaultNugetSource

let config4 = """
source "https://nuget.org/api/v2" // first source
source "http://nuget.org/api/v3"  // second

nuget "FAKE" "~> 3.0" 
nuget "Rx-Main" "~> 2.2"
nuget "MinPackage" "1.1.3"
"""

[<Test>]
let ``should read config with multiple sources``() = 
    let cfg = DependenciesFile.FromCode(config4)
    cfg.Options.Strict |> shouldEqual false

    (cfg.Packages |> List.find (fun p -> p.Name = PackageName "Rx-Main")).Sources |> shouldEqual [PackageSources.DefaultNugetSource; PackageSource.NugetSource "http://nuget.org/api/v3"]
    (cfg.Packages |> List.find (fun p -> p.Name = PackageName "MinPackage")).Sources |> shouldEqual [PackageSources.DefaultNugetSource; PackageSource.NugetSource "http://nuget.org/api/v3"]
    (cfg.Packages |> List.find (fun p -> p.Name = PackageName "FAKE")).Sources |> shouldEqual [PackageSources.DefaultNugetSource; PackageSource.NugetSource "http://nuget.org/api/v3"]

[<Test>]
let ``should read source file from config``() =
    let config = """github "fsharp/FAKE:master" "src/app/FAKE/Cli.fs"
                    github "fsharp/FAKE:bla123zxc" "src/app/FAKE/FileWithCommit.fs" """
    let dependencies = DependenciesFile.FromCode(config)
    dependencies.RemoteFiles
    |> shouldEqual
        [ { Owner = "fsharp"
            Project = "FAKE"
            Name = "src/app/FAKE/Cli.fs"
            Origin = ModuleResolver.SingleSourceFileOrigin.GitHubLink
            Commit = Some "master" }
          { Owner = "fsharp"
            Project = "FAKE"
            Origin = ModuleResolver.SingleSourceFileOrigin.GitHubLink
            Name = "src/app/FAKE/FileWithCommit.fs"
            Commit = Some "bla123zxc" } ]

let strictConfig = """
references strict
source "http://nuget.org/api/v2" // first source

nuget "FAKE" "~> 3.0"
"""

[<Test>]
let ``should read strict config``() = 
    let cfg = DependenciesFile.FromCode(strictConfig)
    cfg.Options.Strict |> shouldEqual true
    cfg.Options.Redirects |> shouldEqual false

    (cfg.Packages |> List.find (fun p -> p.Name = PackageName "FAKE")).Sources |> shouldEqual [PackageSource.NugetSource "http://nuget.org/api/v2"]

let redirectsConfig = """
redirects on
source "http://nuget.org/api/v2" // first source

nuget "FAKE" "~> 3.0"
"""

[<Test>]
let ``should read config with redirects``() = 
    let cfg = DependenciesFile.FromCode(redirectsConfig)
    cfg.Options.Strict |> shouldEqual false
    cfg.Options.Redirects |> shouldEqual true

    (cfg.Packages |> List.find (fun p -> p.Name = PackageName "FAKE")).Sources |> shouldEqual [PackageSource.NugetSource "http://nuget.org/api/v2"]

let noRedirectsConfig = """
redirects off
source "http://nuget.org/api/v2" // first source

nuget "FAKE" "~> 3.0"
"""

[<Test>]
let ``should read config with no redirects``() = 
    let cfg = DependenciesFile.FromCode(noRedirectsConfig)
    cfg.Options.Strict |> shouldEqual false
    cfg.Options.Redirects |> shouldEqual false

    (cfg.Packages |> List.find (fun p -> p.Name = PackageName "FAKE")).Sources |> shouldEqual [PackageSource.NugetSource "http://nuget.org/api/v2"]

let noneContentConfig = """
content none
source "http://nuget.org/api/v2" // first source

nuget "Microsoft.SqlServer.Types"
"""

[<Test>]
let ``should read content none config``() = 
    let cfg = DependenciesFile.FromCode(noneContentConfig)
    cfg.Options.OmitContent |> shouldEqual true

    (cfg.Packages |> List.find (fun p -> p.Name = PackageName "Microsoft.SqlServer.Types")).Sources |> shouldEqual [PackageSource.NugetSource "http://nuget.org/api/v2"]

let configWithoutQuotes = """
source http://nuget.org/api/v2

nuget Castle.Windsor-log4net ~> 3.2
nuget Rx-Main ~> 2.0
nuget FAKE = 1.1
nuget SignalR = 3.3.2
"""

[<Test>]
let ``should read config without quotes``() = 
    let cfg = DependenciesFile.FromCode(configWithoutQuotes)
    cfg.Options.Strict |> shouldEqual false
    cfg.DirectDependencies.Count |> shouldEqual 4

    cfg.DirectDependencies.[PackageName "Rx-Main"].Range |> shouldEqual (VersionRange.Between("2.0", "3.0"))
    cfg.DirectDependencies.[PackageName "Castle.Windsor-log4net"].Range |> shouldEqual (VersionRange.Between("3.2", "4.0"))
    cfg.DirectDependencies.[PackageName "FAKE"].Range |> shouldEqual (VersionRange.Exactly "1.1")
    cfg.DirectDependencies.[PackageName "SignalR"].Range |> shouldEqual (VersionRange.Exactly "3.3.2")

let configWithoutQuotesButLotsOfWhiteSpace = """
source      http://nuget.org/api/v2

nuget   Castle.Windsor-log4net   ~>     3.2
nuget Rx-Main ~> 2.0
nuget FAKE =    1.1
nuget SignalR    = 3.3.2
"""

[<Test>]
let ``should read config without quotes but lots of whitespace``() = 
    let cfg = DependenciesFile.FromCode(configWithoutQuotes)
    cfg.Options.Strict |> shouldEqual false
    cfg.DirectDependencies.Count |> shouldEqual 4

    cfg.DirectDependencies.[PackageName "Rx-Main"].Range |> shouldEqual (VersionRange.Between("2.0", "3.0"))
    cfg.DirectDependencies.[PackageName "Castle.Windsor-log4net"].Range |> shouldEqual (VersionRange.Between("3.2", "4.0"))
    cfg.DirectDependencies.[PackageName "FAKE"].Range |> shouldEqual (VersionRange.Exactly "1.1")
    cfg.DirectDependencies.[PackageName "SignalR"].Range |> shouldEqual (VersionRange.Exactly "3.3.2")


[<Test>]
let ``should read github source file from config without quotes``() =
    let config = """github fsharp/FAKE:master   src/app/FAKE/Cli.fs
                    github    fsharp/FAKE:bla123zxc src/app/FAKE/FileWithCommit.fs """
    let dependencies = DependenciesFile.FromCode(config)
    dependencies.RemoteFiles
    |> shouldEqual
        [ { Owner = "fsharp"
            Project = "FAKE"
            Name = "src/app/FAKE/Cli.fs"
            Origin = ModuleResolver.SingleSourceFileOrigin.GitHubLink
            Commit = Some "master" }
          { Owner = "fsharp"
            Project = "FAKE"
            Origin = ModuleResolver.SingleSourceFileOrigin.GitHubLink
            Name = "src/app/FAKE/FileWithCommit.fs"
            Commit = Some "bla123zxc" } ]

[<Test>]
let ``should read github source file from config with quotes``() =
    let config = """github fsharp/FAKE:master  "src/app/FAKE/Cli.fs"
                    github fsharp/FAKE:bla123zxc "src/app/FAKE/FileWith Space.fs" """
    let dependencies = DependenciesFile.FromCode(config)
    dependencies.RemoteFiles
    |> shouldEqual
        [ { Owner = "fsharp"
            Project = "FAKE"
            Name = "src/app/FAKE/Cli.fs"
            Origin = ModuleResolver.SingleSourceFileOrigin.GitHubLink
            Commit = Some "master" }
          { Owner = "fsharp"
            Project = "FAKE"
            Name = "src/app/FAKE/FileWith Space.fs"
            Origin = ModuleResolver.SingleSourceFileOrigin.GitHubLink
            Commit = Some "bla123zxc" } ]

[<Test>]
let ``should read github source files withou sha1``() =
    let config = """github fsharp/FAKE  src/app/FAKE/Cli.fs
                    github    fsharp/FAKE:bla123zxc src/app/FAKE/FileWithCommit.fs """
    let dependencies = DependenciesFile.FromCode(config)
    dependencies.RemoteFiles
    |> shouldEqual
        [ { Owner = "fsharp"
            Project = "FAKE"
            Name = "src/app/FAKE/Cli.fs"
            Origin = ModuleResolver.SingleSourceFileOrigin.GitHubLink
            Commit = None }
          { Owner = "fsharp"
            Project = "FAKE"
            Name = "src/app/FAKE/FileWithCommit.fs"
            Origin = ModuleResolver.SingleSourceFileOrigin.GitHubLink
            Commit = Some "bla123zxc" } ]

[<Test>]
let ``should read http source file from config without quotes with file specs``() =
    let config = """http http://www.fssnip.net/raw/1M test1.fs
                    http http://www.fssnip.net/raw/1M/1 src/test2.fs """
    let dependencies = DependenciesFile.FromCode(config)
    dependencies.RemoteFiles
    |> shouldEqual
        [ { Owner = "www.fssnip.net"
            Project = "raw/1M"
            Name = "test1.fs"
            Origin = ModuleResolver.SingleSourceFileOrigin.HttpLink "http://www.fssnip.net/raw/1M"
            Commit = None }
          { Owner = "www.fssnip.net"
            Project = "raw/1M"
            Name = "src/test2.fs"
            Origin = ModuleResolver.SingleSourceFileOrigin.HttpLink "http://www.fssnip.net/raw/1M/1"
            Commit = None } ]

[<Test>]
let ``should read gist source file from config without quotes with file specs``() =
    let config = """gist Thorium/1972308 gistfile1.fs
                    gist Thorium/6088882 """ //Gist supports multiple files also
    let dependencies = DependenciesFile.FromCode(config)
    dependencies.RemoteFiles
    |> shouldEqual
        [ { Owner = "Thorium"
            Project = "1972308"
            Name = "gistfile1.fs"
            Origin = ModuleResolver.SingleSourceFileOrigin.GistLink
            Commit = None }
          { Owner = "Thorium"
            Project = "6088882"
            Name = "FULLPROJECT"
            Origin = ModuleResolver.SingleSourceFileOrigin.GistLink
            Commit = None } ]


[<Test>]
let ``should read http source file from config without quotes, parsing rules``() =
    // The empty "/" should be ommited. After that, parsing amount of "/"-marks:
    let config = """
        http http://example/
        http http://example/item
        http http://example/item/
        http http://example/item/3
        http http://example/item/3/1"""
    let dependencies = DependenciesFile.FromCode(config)
    dependencies.RemoteFiles
    |> shouldEqual
        [ { Owner = "example"
            Project = "example"
            Name = "example.fs"
            Origin = ModuleResolver.SingleSourceFileOrigin.HttpLink "http://example"
            Commit = None }
          { Owner = "example"
            Project = "item"
            Name = "item.fs"
            Origin = ModuleResolver.SingleSourceFileOrigin.HttpLink "http://example/item"
            Commit = None }
          { Owner = "example"
            Project = "item"
            Name = "item.fs"
            Origin = ModuleResolver.SingleSourceFileOrigin.HttpLink "http://example/item"
            Commit = None }
          { Owner = "example"
            Project = "item/3"
            Name = "3.fs"
            Origin = ModuleResolver.SingleSourceFileOrigin.HttpLink "http://example/item/3"
            Commit = None }
          { Owner = "example"
            Project = "item/3"
            Name = "1.fs"
            Origin = ModuleResolver.SingleSourceFileOrigin.HttpLink "http://example/item/3/1"
            Commit = None } ]

[<Test>]
let ``should read http binary references from config``() =
    let config = """
        http http://www.frijters.net/ikvmbin-8.0.5449.0.zip
        http http://www.frijters.net/ikvmbin-8.0.5449.0.zip ikvmbin.zip"""
    let dependencies = DependenciesFile.FromCode(config)
    dependencies.RemoteFiles
    |> shouldEqual
        [ { Owner = "www.frijters.net"
            Project = "ikvmbin-8.0.5449.0.zip"
            Name = "ikvmbin-8.0.5449.0.zip"
            Origin = ModuleResolver.SingleSourceFileOrigin.HttpLink "http://www.frijters.net/ikvmbin-8.0.5449.0.zip"
            Commit = None }
          { Owner = "www.frijters.net"
            Project = "ikvmbin-8.0.5449.0.zip"
            Name = "ikvmbin.zip"
            Origin = ModuleResolver.SingleSourceFileOrigin.HttpLink "http://www.frijters.net/ikvmbin-8.0.5449.0.zip"
            Commit = None } ]


let configWithoutVersions = """
source "http://nuget.org/api/v2"

nuget Castle.Windsor-log4net
nuget Rx-Main
nuget "FAKE"
"""

[<Test>]
let ``should read config without versions``() = 
    let cfg = DependenciesFile.FromCode(configWithoutVersions)

    cfg.DirectDependencies.[PackageName "Rx-Main"] .Range|> shouldEqual (VersionRange.AtLeast "0")
    cfg.DirectDependencies.[PackageName "Castle.Windsor-log4net"].Range |> shouldEqual (VersionRange.AtLeast "0")
    cfg.DirectDependencies.[PackageName "FAKE"].Range |> shouldEqual (VersionRange.AtLeast "0")


let configWithPassword = """
source http://nuget.org/api/v2 username: "tat� tata" password: "you got hacked!"
nuget Rx-Main
"""

[<Test>]
let ``should read config with encapsulated password source``() = 
    let cfg = DependenciesFile.FromCode( configWithPassword)
    
    (cfg.Packages |> List.find (fun p -> p.Name = PackageName "Rx-Main")).Sources 
    |> shouldEqual [ 
        PackageSource.Nuget { 
            Url = "http://nuget.org/api/v2"
            Authentication = Some (PlainTextAuthentication("tat� tata", "you got hacked!")) } ]

let configWithPasswordInSingleQuotes = """
source http://nuget.org/api/v2 username: 'tat� tata' password: 'you got hacked!'
nuget Rx-Main
"""

[<Test>]
let ``should read config with single-quoted password source``() = 
    try
        DependenciesFile.FromCode( configWithPasswordInSingleQuotes) |> ignore
        failwith "Expected error"
    with
    | exn when exn.Message <> "Expected error" -> ()

let configWithPasswordInEnvVariable = """
source http://nuget.org/api/v2 username: "%FEED_USERNAME%" password: "%FEED_PASSWORD%"
nuget Rx-Main
"""

[<Test>]
let ``should read config with password in env variable``() = 
    Environment.SetEnvironmentVariable("FEED_USERNAME", "user XYZ", EnvironmentVariableTarget.Process)
    Environment.SetEnvironmentVariable("FEED_PASSWORD", "pw Love", EnvironmentVariableTarget.Process)
    let cfg = DependenciesFile.FromCode( configWithPasswordInEnvVariable)
    
    (cfg.Packages |> List.find (fun p -> p.Name = PackageName "Rx-Main")).Sources 
    |> shouldEqual [ 
        PackageSource.Nuget { 
            Url = "http://nuget.org/api/v2"
            Authentication = Some (EnvVarAuthentication
                                    ({Variable = "%FEED_USERNAME%"; Value = "user XYZ"},
                                     {Variable = "%FEED_PASSWORD%"; Value = "pw Love"}))} ]

let configWithExplicitVersions = """
source "http://nuget.org/api/v2"

nuget FSharp.Compiler.Service == 0.0.62 
nuget FsReveal == 0.0.5-beta
"""

[<Test>]
let ``should read config explicit versions``() = 
    let cfg = DependenciesFile.FromCode(configWithExplicitVersions)

    cfg.DirectDependencies.[PackageName "FSharp.Compiler.Service"].Range |> shouldEqual (VersionRange.OverrideAll (SemVer.Parse "0.0.62"))
    cfg.DirectDependencies.[PackageName "FsReveal"].Range |> shouldEqual (VersionRange.OverrideAll (SemVer.Parse "0.0.5-beta"))

let configWithLocalSource = """
source ./nugets

nuget Nancy.Owin 0.22.2
"""

[<Test>]
let ``should read config with local source``() = 
    let cfg = DependenciesFile.FromCode(configWithLocalSource)

    cfg.DirectDependencies.[PackageName "Nancy.Owin"].Range |> shouldEqual (VersionRange.Specific (SemVer.Parse "0.22.2"))

[<Test>]
let ``should read config with package name containing nuget``() = 
    let config = """
    nuget nuget.Core 0.1
    """
    let cfg = DependenciesFile.FromCode(config)

    cfg.DirectDependencies.[PackageName "nuget.Core"].Range |> shouldEqual (VersionRange.Specific (SemVer.Parse "0.1"))
