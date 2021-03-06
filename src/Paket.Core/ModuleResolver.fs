﻿/// Contains logic which helps to resolve the dependency graph for modules
module Paket.ModuleResolver

open System.IO
open Paket.Domain
open Paket.Requirements

type SingleSourceFileOrigin = 
| GitHubLink 
| GistLink 
| HttpLink of string

// Represents details on a dependent source file.
type UnresolvedSourceFile =
    { Owner : string
      Project : string
      Name : string      
      Origin : SingleSourceFileOrigin
      Commit : string option }

    override this.ToString() = 
        match this.Commit with
        | Some commit -> sprintf "%s/%s:%s %s" this.Owner this.Project commit this.Name
        | None -> sprintf "%s/%s %s" this.Owner this.Project this.Name

type ResolvedSourceFile =
    { Owner : string
      Project : string
      Name : string      
      Commit : string
      Dependencies : Set<PackageName*VersionRequirement>
      Origin : SingleSourceFileOrigin
      }
    member this.FilePath = this.ComputeFilePath(this.Name)

    member this.ComputeFilePath(name:string) =
        let path = normalizePath (name.TrimStart('/'))

        let di = DirectoryInfo(Path.Combine(Constants.PaketFilesFolderName, this.Owner, this.Project, path))
        di.FullName

    override this.ToString() = sprintf "%s/%s:%s %s" this.Owner this.Project this.Commit this.Name

let resolve getDependencies getSha1 (file : UnresolvedSourceFile) : ResolvedSourceFile = 
    let sha = 
        defaultArg file.Commit "master"
        |> getSha1 file.Origin file.Owner file.Project
    
    let resolved = 
        { Commit = sha
          Owner = file.Owner
          Origin = file.Origin
          Project = file.Project
          Dependencies = Set.empty
          Name = file.Name }
    
    let dependencies = 
        getDependencies resolved 
        |> List.map (fun (package:PackageRequirement) -> package.Name, package.VersionRequirement)
        |> Set.ofList

    { resolved with Dependencies = dependencies }

// TODO: github has a rate limit - try to convince them to whitelist Paket
let Resolve(getDependencies, getSha1, remoteFiles : UnresolvedSourceFile list) : ResolvedSourceFile list = 
    remoteFiles |> List.map (resolve getDependencies getSha1)