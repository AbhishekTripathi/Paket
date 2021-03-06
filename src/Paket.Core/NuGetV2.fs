/// Contains NuGet support.
module Paket.NuGetV2

open System
open System.IO
open System.Net
open Newtonsoft.Json
open Ionic.Zip
open System.Xml
open System.Text.RegularExpressions
open Paket.Logging
open System.Text

open Paket.Domain
open Paket.Utils
open Paket.Xml
open Paket.PackageSources
open Paket.NuGetV3

type NugetPackageCache =
    { Dependencies : (string * VersionRequirement * (FrameworkIdentifier option)) list
      Name : string
      SourceUrl: string
      Unlisted : bool
      DownloadUrl : string}

let rec private followODataLink getUrlContents url = 
    async { 
        let! raw = getUrlContents url
        let doc = XmlDocument()
        doc.LoadXml raw
        let feed = 
            match doc |> getNode "feed" with
            | Some node -> node
            | None -> failwithf "unable to parse data from %s" url

        let readEntryVersion = Some 
                               >> optGetNode "properties"
                               >> optGetNode "Version"
                               >> Option.map (fun node -> node.InnerText)

        let entriesVersions = feed |> getNodes "entry" |> List.choose readEntryVersion

        let! linksVersions = 
            feed 
            |> getNodes "link"
            |> List.filter (fun node -> node |> getAttribute "rel" = Some "next")
            |> List.choose (getAttribute "href")
            |> List.map (followODataLink getUrlContents)
            |> Async.Parallel

        return
            linksVersions
            |> Seq.collect id
            |> Seq.append entriesVersions
    }

/// Gets versions of the given package via OData via /Packages?$filter=Id eq 'packageId'
let getAllVersionsFromNugetODataWithFilter (getUrlContents, nugetURL, package) = 
    // we cannot cache this
    let url = sprintf "%s/Packages?$filter=Id eq '%s'" nugetURL package
    followODataLink getUrlContents url

/// Gets versions of the given package via OData via /FindPackagesById()?id='packageId'.
let getAllVersionsFromNugetOData (getUrlContents, nugetURL, package) = 
    async { 
        // we cannot cache this
        try 
            let url = sprintf "%s/FindPackagesById()?id='%s'" nugetURL package
            return! followODataLink getUrlContents url
        with _ -> return! getAllVersionsFromNugetODataWithFilter (getUrlContents, nugetURL, package)
    }

/// Gets all versions no. of the given package.
let getAllVersionsFromNuGet2(auth,nugetURL,package) = 
    // we cannot cache this
    async { 
        let! raw = safeGetFromUrl(auth,sprintf "%s/package-versions/%s?includePrerelease=true" nugetURL package)
        let getUrlContents url = getFromUrl(auth, url)
        match raw with
        | None -> let! result = getAllVersionsFromNugetOData(getUrlContents, nugetURL, package)
                  return result
        | Some data -> 
            try 
                try 
                    let result = JsonConvert.DeserializeObject<string []>(data) |> Array.toSeq
                    return result
                with _ -> let! result = getAllVersionsFromNugetOData(getUrlContents, nugetURL, package)
                          return result
            with exn -> 
                return! failwithf "Could not get data from %s for package %s.%s Message: %s" nugetURL package 
                    Environment.NewLine exn.Message
    }


let getAllVersions(auth, nugetURL, package) = 
    async { 
        let! raw = getViaNuGet3(auth, nugetURL, package)

        match raw with
        | None -> let! result = getAllVersionsFromNuGet2(auth,nugetURL,package)
                  return result
        | Some data -> 
            try 
                try 
                    let result = getJSONLDDetails data
                    return (Array.toSeq result)
                with _ -> let! result = getAllVersionsFromNuGet2(auth,nugetURL, package)
                          return result
            with exn ->
                return! failwithf "Could not get data from %s for package %s.%s Message: %s" nugetURL package
                    Environment.NewLine exn.Message
    }

/// Gets versions of the given package from local Nuget feed.
let getAllVersionsFromLocalPath (localNugetPath, package) =
    async {
        return Directory.EnumerateFiles(localNugetPath,"*.nupkg",SearchOption.AllDirectories)
               |> Seq.choose (fun fileName ->
                                   let fi = FileInfo(fileName)
                                   let _match = Regex(sprintf @"^%s\.(\d.*)\.nupkg" package, RegexOptions.IgnoreCase).Match(fi.Name)
                                   if _match.Groups.Count > 1 then Some _match.Groups.[1].Value else None)
    }


let parseODataDetails(nugetURL,packageName,version,raw) = 
    let doc = XmlDocument()
    doc.LoadXml raw
                
    let entry = 
        match orElse (doc |> getNode "entry") (doc |> getNode "feed" |> optGetNode "entry" ) with
        | Some node -> node
        | _ -> failwithf "unable to find entry node for package %s %O" packageName version

    let officialName =
        match orElse (entry |> getNode "properties" |> optGetNode "Id") (entry |> getNode "title") with
        | Some node -> node.InnerText
        | _ -> failwithf "Could not get official package name for package %s %O" packageName version
        
    let publishDate =
        match entry |> getNode "properties" |> optGetNode "Published" with
        | Some node -> 
            match DateTime.TryParse node.InnerText with
            | true, date -> date
            | _ -> DateTime.MinValue
        | _ -> DateTime.MinValue
    
    let downloadLink =
        match entry |> getNode "content" |> optGetAttribute "type", 
              entry |> getNode "content" |> optGetAttribute "src"  with
        | Some "application/zip", Some link -> link
        | Some "binary/octet-stream", Some link -> link
        | _ -> failwithf "unable to find downloadLink for package %s %O" packageName version
        
    let dependencies =
        match entry |> getNode "properties" |> optGetNode "Dependencies" with
        | Some node -> node.InnerText
        | None -> failwithf "unable to find dependencies for package %s %O" packageName version

    let packages = 
        dependencies
        |> fun s -> s.Split([| '|' |], System.StringSplitOptions.RemoveEmptyEntries)
        |> Array.map (fun d -> d.Split ':')
        |> Array.filter (fun d -> Array.isEmpty d |> not && d.[0] <> "")
        |> Array.map (fun a -> a.[0],(if a.Length > 1 then a.[1] else "0"),(if a.Length > 2 && a.[2] <> "" then FrameworkIdentifier.Extract a.[2] else None))
        |> Array.map (fun (name, version, restricted) -> name, NugetVersionRangeParser.parse version, restricted)
        |> Array.toList

    { Name = officialName
      DownloadUrl = downloadLink
      Dependencies = packages
      SourceUrl = nugetURL
      Unlisted = publishDate = Constants.MagicUnlistingDate }


let getDetailsFromNuGetViaODataFast auth nugetURL package (version:SemVerInfo) = 
    async {         
        try 
            let! raw = getFromUrl(auth,sprintf "%s/Packages?$filter=Id eq '%s' and NormalizedVersion eq '%s'" nugetURL package (version.Normalize()))
            return parseODataDetails(nugetURL,package,version,raw)
        with _ ->         
            let! raw = getFromUrl(auth,sprintf "%s/Packages?$filter=Id eq '%s' and Version eq '%s'" nugetURL package (version.ToString()))
            return parseODataDetails(nugetURL,package,version,raw)
    }

/// Gets package details from Nuget via OData
let getDetailsFromNuGetViaOData auth nugetURL package (version:SemVerInfo) = 
    async {         
        try 
            return! getDetailsFromNuGetViaODataFast auth nugetURL package version
        with _ ->         
            let! raw = getFromUrl(auth,sprintf "%s/Packages(Id='%s',Version='%s')" nugetURL package (version.ToString()))
            return parseODataDetails(nugetURL,package,version,raw)
    }

/// The NuGet cache folder.
let CacheFolder = 
    let appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
    let di = DirectoryInfo(Path.Combine(Path.Combine(appData, "NuGet"), "Cache"))
    if not di.Exists then
        di.Create()
    di.FullName

let private loadFromCacheOrOData force fileName auth nugetURL package version = 
    async {
        if not force && File.Exists fileName then
            try 
                let json = File.ReadAllText(fileName)
                let cachedObject = JsonConvert.DeserializeObject<NugetPackageCache>(json)                
                if cachedObject.Name = null || cachedObject.DownloadUrl = null || cachedObject.SourceUrl = null then
                    let! details = getDetailsFromNuGetViaOData auth nugetURL package version
                    return true,details
                else
                    return false,cachedObject
            with _ -> 
                let! details = getDetailsFromNuGetViaOData auth nugetURL package version
                return true,details
        else
            let! details = getDetailsFromNuGetViaOData auth nugetURL package version
            return true,details
    }

/// Tries to get download link and direct dependencies from Nuget
/// Caches calls into json file
let getDetailsFromNuget force auth nugetURL package (version:SemVerInfo) = 
    async {
        try            
            let fi = FileInfo(Path.Combine(CacheFolder,sprintf "%s.%s.json" package (version.Normalize())))
            let! (invalidCache,details) = loadFromCacheOrOData force fi.FullName auth nugetURL package version
            if details.SourceUrl <> nugetURL then
                return! getDetailsFromNuGetViaOData auth nugetURL package version 
            else
                if invalidCache then
                    File.WriteAllText(fi.FullName,JsonConvert.SerializeObject(details))
                return details
        with
        | _ -> return! getDetailsFromNuGetViaOData auth nugetURL package version 
    }    
    
/// Reads direct dependencies from a nupkg file
let getDetailsFromLocalFile path package (version:SemVerInfo) =
    async {
        let nupkg = FileInfo(Path.Combine(path, sprintf "%s.%s.nupkg" package (version.Normalize())))
        let zip = ZipFile.Read(nupkg.FullName)
        let zippedNuspec = (zip |> Seq.find (fun f -> f.FileName.EndsWith ".nuspec"))

        zippedNuspec.Extract(Path.GetTempPath(), ExtractExistingFileAction.OverwriteSilently)

        let fileName = FileInfo(Path.Combine(Path.GetTempPath(), zippedNuspec.FileName)).FullName

        let nuspec = Nuspec.Load fileName        

        File.Delete(fileName)

        return 
            { Name = nuspec.OfficialName
              DownloadUrl = package
              Dependencies = nuspec.Dependencies
              SourceUrl = path
              Unlisted = false }
    }


let inline isExtracted fileName =
    let fi = FileInfo(fileName)
    if not fi.Exists then false else
    let di = fi.Directory
    di.EnumerateFileSystemInfos()
    |> Seq.exists (fun f -> f.FullName <> fi.FullName)    

/// Extracts the given package to the ./packages folder
let ExtractPackage(fileName:string, targetFolder, name, version:SemVerInfo) =    
    async {
        if  isExtracted fileName then
             verbosefn "%s %A already extracted" name version
        else
            use zip = ZipFile.Read(fileName)
            Directory.CreateDirectory(targetFolder) |> ignore
            for e in zip do
                try
                    e.Extract(targetFolder, ExtractExistingFileAction.OverwriteSilently)
                with
                | exn ->                    
                    failwithf "Error during unzipping %s in %s %A: %s" e.FileName name version exn.Message 

            // cleanup folder structure
            let rec cleanup (dir : DirectoryInfo) = 
                for sub in dir.GetDirectories() do
                    let newName = sub.FullName.Replace("%2B", "+").Replace("%20", " ")
                    if sub.FullName <> newName && not (Directory.Exists newName) then 
                        Directory.Move(sub.FullName, newName)
                        cleanup (DirectoryInfo newName)
                    else
                        cleanup sub
                for file in dir.GetFiles() do
                    let newName = file.Name.Replace("%2B", "+").Replace("%20", " ")
                    if file.Name <> newName && not (File.Exists <| Path.Combine(file.DirectoryName, newName)) then
                        File.Move(file.FullName, Path.Combine(file.DirectoryName, newName))
            cleanup (DirectoryInfo targetFolder)
            tracefn "%s %A unzipped to %s" name version targetFolder
        return targetFolder
    }

/// Extracts the given package to the ./packages folder
let CopyFromCache(root, cacheFileName, name, version:SemVerInfo, force) = 
    async { 
        let targetFolder = DirectoryInfo(Path.Combine(root, "packages", name)).FullName
        let fi = FileInfo(cacheFileName)
        let targetFile = FileInfo(Path.Combine(targetFolder, fi.Name))
        if not force && targetFile.Exists then           
            verbosefn "%s %A already copied" name version        
        else
            CleanDir targetFolder
            File.Copy(cacheFileName, targetFile.FullName)            
        try
            return! ExtractPackage(targetFile.FullName,targetFolder,name,version)            
        with
        | exn -> 
            File.Delete targetFile.FullName
            Directory.Delete(targetFolder,true)
            return! raise exn
    }

/// Downloads the given package to the NuGet Cache folder
let DownloadPackage(root, auth, url, name, version:SemVerInfo, force) = 
    async { 
        let targetFileName = Path.Combine(CacheFolder, name + "." + version.Normalize() + ".nupkg")
        let targetFile = FileInfo targetFileName
        if not force && targetFile.Exists && targetFile.Length > 0L then 
            verbosefn "%s %A already downloaded" name version            
        else 
            // discover the link on the fly
            let! nugetPackage = getDetailsFromNuget force auth url name version
            try
                tracefn "Downloading %s %A to %s" name version targetFileName

                let request = HttpWebRequest.Create(Uri nugetPackage.DownloadUrl) :?> HttpWebRequest
                request.AutomaticDecompression <- DecompressionMethods.GZip ||| DecompressionMethods.Deflate

                match auth with
                | None -> request.UseDefaultCredentials <- true
                | Some auth -> 
                    // htttp://stackoverflow.com/questions/16044313/webclient-httpwebrequest-with-basic-authentication-returns-404-not-found-for-v/26016919#26016919
                    //this works ONLY if the server returns 401 first
                    //client DOES NOT send credentials on first request
                    //ONLY after a 401
                    //client.Credentials <- new NetworkCredential(auth.Username,auth.Password)

                    //so use THIS instead to send credentials RIGHT AWAY
                    let credentials = Convert.ToBase64String(Encoding.ASCII.GetBytes(auth.Username + ":" + auth.Password))
                    request.Headers.[HttpRequestHeader.Authorization] <- String.Format("Basic {0}", credentials)

                request.Proxy <- Utils.defaultProxy
                use! httpResponse = request.AsyncGetResponse()
            
                use httpResponseStream = httpResponse.GetResponseStream()
            
                let bufferSize = 4096
                let buffer : byte [] = Array.zeroCreate bufferSize
                let bytesRead = ref -1

                use fileStream = File.Create(targetFileName)
            
                while !bytesRead <> 0 do
                    let! bytes = httpResponseStream.AsyncRead(buffer, 0, bufferSize)
                    bytesRead := bytes
                    do! fileStream.AsyncWrite(buffer, 0, !bytesRead)

            with
            | exn -> failwithf "Could not download %s %A.%s    %s" name version Environment.NewLine exn.Message
        return! CopyFromCache(root, targetFile.FullName, name, version, force)
    }

/// Finds all libraries in a nuget package.
let GetLibFiles(targetFolder) =    
    let libs = 
        let dir = DirectoryInfo(targetFolder)
        let libPath = dir.FullName.ToLower() + Path.DirectorySeparatorChar.ToString() + "lib" + Path.DirectorySeparatorChar.ToString()
        if dir.Exists then
            dir.GetFiles("*.*",SearchOption.AllDirectories)
            |> Array.filter (fun fi -> fi.FullName.ToLower().Contains(libPath))
        else
            Array.empty

    if Logging.verbose then
        if Array.isEmpty libs then 
            verbosefn "No libraries found in %s" targetFolder 
        else
            let s = String.Join(Environment.NewLine + "  - ",libs |> Array.map (fun l -> l.FullName))
            verbosefn "Libraries found in %s:%s  - %s" targetFolder Environment.NewLine s

    libs

let GetPackageDetails force sources (PackageName package) (version:SemVerInfo) : PackageResolver.PackageDetails= 
    let rec tryNext xs = 
        match xs with
        | source :: rest -> 
            try 
                match source with
                | Nuget source -> 
                    getDetailsFromNuget 
                        force 
                        (source.Authentication |> Option.map toBasicAuth)
                        source.Url 
                        package 
                        version 
                    |> Async.RunSynchronously
                | LocalNuget path -> 
                    getDetailsFromLocalFile path package version |> Async.RunSynchronously
                |> fun x -> source,x
            with _ -> tryNext rest
        | [] -> failwithf "Couldn't get package details for package %s on %A." package (sources |> List.map (fun (s:PackageSource) -> s.ToString()))
    
    let source,nugetObject = tryNext sources
    { Name = PackageName nugetObject.Name
      Source = source
      DownloadLink = nugetObject.DownloadUrl
      Unlisted = nugetObject.Unlisted
      DirectDependencies = nugetObject.Dependencies |> List.map (fun (name, v, f) -> PackageName name, v, f) |> Set.ofList }

/// Allows to retrieve all version no. for a package from the given sources.
let GetVersions(sources, PackageName packageName) = 
    sources
    |> Seq.map (fun source -> 
           match source with
           | Nuget source -> getAllVersions (
                                source.Authentication |> Option.map toBasicAuth, 
                                source.Url, 
                                packageName)
           | LocalNuget path -> getAllVersionsFromLocalPath (path, packageName))
    |> Async.Parallel
    |> Async.RunSynchronously
    |> Seq.concat
    |> Seq.toList
    |> List.map SemVer.Parse