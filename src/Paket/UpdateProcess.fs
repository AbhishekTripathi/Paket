﻿/// Contains methods for the update process.
module Paket.UpdateProcess

open Paket

/// Update command
let Update(dependenciesFileName, forceResolution, force, hard) = 
    let lockFileName = DependenciesFile.FindLockfile dependenciesFileName
    
    let lockFile = 
        if forceResolution || not lockFileName.Exists then 
            let dependenciesFile = DependenciesFile.ReadFromFile dependenciesFileName
            let resolution = DependencyResolution.Analyze(dependenciesFile, force)
            let lockFile = LockFile(lockFileName.FullName, dependenciesFile.Strict, resolution)
            lockFile.Save()
            lockFile
        else LockFile.LoadFrom lockFileName.FullName
    InstallProcess.Install(force, hard, lockFile)