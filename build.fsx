// --------------------------------------------------------------------------------------
// FAKE build script
// --------------------------------------------------------------------------------------

#I "packages/FAKE/tools/"
#r "FakeLib.dll"

#if MONO
#else
#load "packages/SourceLink.Fake/tools/SourceLink.fsx"
#endif

open System
open System.IO
open Fake
open Fake.AssemblyInfoFile
open Fake.Git

Environment.CurrentDirectory <- __SOURCE_DIRECTORY__
let (!!) includes = (!! includes).SetBaseDirectory __SOURCE_DIRECTORY__

// --------------------------------------------------------------------------------------
// Information about the project to be used at NuGet and in AssemblyInfo files
// --------------------------------------------------------------------------------------

let project = "BsonTypeProvider"
let authors = ["Max Hirschhorn"]
let summary = "Type provider for BSON."
let description = "Type provider for BSON."
let tags = "F# fsharp data typeprovider bson mongodb mongo"

let gitHome = "https://github.com/visemet"
let gitName = "FSharp.Data.Bson"
let gitRaw = environVarOrDefault "gitRaw" "https://raw.github.com/visemet"

// Read release notes & version info from RELEASE_NOTES.md
let release =
    File.ReadLines "RELEASE_NOTES.md"
    |> ReleaseNotesHelper.parseReleaseNotes

let isAppVeyorBuild = environVar "APPVEYOR" <> null
let nugetVersion =
    if isAppVeyorBuild then sprintf "%s-a%s" release.NugetVersion (DateTime.UtcNow.ToString "yyMMddHHmm")
    else release.NugetVersion

Target "AppVeyorBuildVersion" (fun _ ->
    Shell.Exec("appveyor", sprintf "UpdateBuild -Version \"%s\"" nugetVersion) |> ignore
)

// --------------------------------------------------------------------------------------
// Generate assembly info files with the right version & up-to-date information

Target "AssemblyInfo" <| fun () ->
    for file in !! "src/AssemblyInfo*.fs" do
        let replace (oldValue:string) newValue (str:string) = str.Replace(oldValue, newValue)
        let title =
            Path.GetFileNameWithoutExtension file
            |> replace ".Portable47" ""
            |> replace "AssemblyInfo" "BsonTypeProvider"
        let versionSuffix =
            if file.Contains ".Portable47" then ".47"
            else ".0"
        let version = release.AssemblyVersion + versionSuffix
        CreateFSharpAssemblyInfo file
           [ Attribute.Title title
             Attribute.Product project
             Attribute.Description summary
             Attribute.Version version
             Attribute.FileVersion version]

// --------------------------------------------------------------------------------------
// Clean build results & restore NuGet packages

Target "RestorePackages" RestorePackages

Target "Clean" <| fun () ->
    CleanDirs ["bin"]

Target "CleanDocs" <| fun () ->
    CleanDirs ["docs/output"]

// --------------------------------------------------------------------------------------
// Build library & test projects

Target "Build" <| fun () ->
    !! "BsonTypeProvider.sln"
    |> MSBuildRelease "" "Rebuild"
    |> ignore

Target "BuildTests" <| fun () ->
    !! "BsonTypeProvider.Tests.sln"
    |> MSBuildRelease "" "Rebuild"
    |> ignore

// --------------------------------------------------------------------------------------
// Run the unit tests using test runner
Target "RunTests" <| ignore

let runTestTask name =
    let taskName = sprintf "RunTest_%s" name
    Target taskName <| fun () ->
        !! (sprintf "tests/*/bin/Release/%s.dll" name)
        |> NUnit (fun p ->
            { p with
                DisableShadowCopy = true
                TimeOut = TimeSpan.FromMinutes 20.
                Framework = "4.0"
                Domain = MultipleDomainModel
                OutputFile = "TestResults.xml" })
    taskName ==> "RunTests" |> ignore

["BsonTypeProvider.DesignTime.Tests"]
|> List.iter runTestTask

// --------------------------------------------------------------------------------------
// Source link the pdb files

#if MONO

Target "SourceLink" <| id

#else

open SourceLink

Target "SourceLink" <| fun () ->
    use repo = new GitRepo(__SOURCE_DIRECTORY__)
    for file in !! "src/*.fsproj" do
        let proj = VsProj.LoadRelease file
        logfn "source linking %s" proj.OutputFilePdb
        let files = proj.Compiles -- "**/AssemblyInfo*.fs"
        repo.VerifyChecksums files
        proj.VerifyPdbChecksums files
        proj.CreateSrcSrv (sprintf "%s/%s/{0}/%%var2%%" gitRaw gitName) repo.Revision (repo.Paths files)
        Pdbstr.exec proj.OutputFilePdb proj.OutputFilePdbSrcSrv
    CopyFiles "bin" (!! "src/bin/Release/FSharp.Data.Bson.*")

#endif

// --------------------------------------------------------------------------------------
// Build a NuGet package

Target "NuGet" <| fun () ->
    // Format the release notes
    let releaseNotes = release.Notes |> String.concat "\n"
    NuGet (fun p ->
        { p with
            Authors = authors
            Project = project
            Summary = summary
            Description = description
            Version = nugetVersion
            ReleaseNotes = releaseNotes
            Tags = tags
            OutputPath = "bin"
            AccessKey = getBuildParamOrDefault "nugetkey" ""
            Publish = hasBuildParam "nugetkey"
            Dependencies = [] })
        "nuget/BsonTypeProvider.nuspec"

// --------------------------------------------------------------------------------------
// Generate the documentation

Target "GenerateDocs" <| fun () ->
    executeFSIWithArgs "docs/tools" "generate.fsx" ["--define:RELEASE"] [] |> ignore

// --------------------------------------------------------------------------------------
// Release Scripts

let publishFiles what branch fromFolder toFolder =
    let tempFolder = "temp/" + branch
    CleanDir tempFolder
    Repository.cloneSingleBranch "" (gitHome + "/" + gitName + ".git") branch tempFolder
    fullclean tempFolder
    CopyRecursive fromFolder (tempFolder + "/" + toFolder) true |> tracefn "%A"
    StageAll tempFolder
    Commit tempFolder <| sprintf "Update %s for version %s" what release.NugetVersion
    Branches.push tempFolder

Target "ReleaseDocs" <| fun () ->
    publishFiles "generated documentation" "gh-pages" "docs/output" ""

Target "ReleaseBinaries" <| fun () ->
    publishFiles "binaries" "release" "bin" "bin"

Target "Release" DoNothing

"CleanDocs" ==> "GenerateDocs" ==> "ReleaseDocs"
"ReleaseDocs" ==> "Release"
"ReleaseBinaries" ==> "Release"
"NuGet" ==> "Release"

// --------------------------------------------------------------------------------------
// Help

Target "Help" <| fun () ->
    printfn ""
    printfn "  Please specify the target by calling 'build <Target>'"
    printfn ""
    printfn "  Targets for building:"
    printfn "  * Build"
    printfn "  * BuildTests"
    printfn "  * RunTests"
    printfn "  * All (calls previous 3)"
    printfn ""
    printfn "  Targets for releasing (requires write access to the 'https://github.com/visemet/FSharp.Data.Bson.git' repository):"
    printfn "  * GenerateDocs"
    printfn "  * ReleaseDocs (calls previous)"
    printfn "  * ReleaseBinaries"
    printfn "  * NuGet (creates package only, doesn't publish)"
    printfn "  * Release (calls previous 4)"
    printfn ""
    printfn "  Other targets:"
#if MONO
#else
    printfn "  * SourceLink (requires autocrlf=input)"
#endif
    printfn ""

Target "All" DoNothing

"Clean" ==> "RestorePackages"  ==> "AssemblyInfo" ==> "Build"
"Build" ==> "All"
"BuildTests" ==> "All"
"RunTests" ==> "All"

RunTargetOrDefault "Help"
