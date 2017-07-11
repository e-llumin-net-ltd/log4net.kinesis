// --------------------------------------------------------------------------------------
// FAKE build script 
// --------------------------------------------------------------------------------------

#r @"packages/FAKE/tools/FakeLib.dll"
open Fake 
open Fake.Git
open Fake.AssemblyInfoFile
open Fake.ReleaseNotesHelper
open System

let buildDir   = "build/"
let build40Dir = "build40/"
let testDir    = "test/"
let tempDir    = "temp/"

// --------------------------------------------------------------------------------------
// START TODO: Provide project-specific details below
// --------------------------------------------------------------------------------------

// Information about the project are used
//  - for version and project name in generated AssemblyInfo file
//  - by the generated NuGet package 
//  - to run tests and to publish documentation on GitHub gh-pages
//  - for documentation, you also need to edit info in "docs/tools/generate.fsx"

// The name of the project 
// (used by attributes in AssemblyInfo, name of a NuGet package and directory in 'src')
let project = "log4net.Kinesis"

// Short summary of the project
// (used as description in AssemblyInfo and as a short summary for NuGet package)
let summary = "log4net appender for logging into an Amazon Kinesis stream"

// Longer description of the project
// (used as a description for NuGet package; line breaks are automatically cleaned up)
let description = """
"""
// List of author names (for NuGet package)
let authors = [ "Yan Cui" ]
// Tags for your project (for NuGet package)
let tags = "f# fsharp aws amazon kinesis bigdata log4net logging"

// File system information 
// (<solutionFile>.sln is built during the building process)
let projectFile    = "log4net.kinesis.fsproj"
let project40File  = "log4net.kinesis40.fsproj"

// Pattern specifying assemblies to be tested using NUnit
let testAssemblies = ["tests/*/bin/*/log4net.kinesis*Tests*.dll"]

// Git configuration (used for publishing documentation in gh-pages branch)
// The profile where the project is posted 
let gitHome = "https://github.com/theburningmonk"
// The name of the project on GitHub
let gitName = "log4net.kinesis"

// --------------------------------------------------------------------------------------
// END TODO: The rest of the file includes standard build steps 
// --------------------------------------------------------------------------------------

// Read additional information from the release notes document
Environment.CurrentDirectory <- __SOURCE_DIRECTORY__
let release = parseReleaseNotes (IO.File.ReadAllLines "RELEASE_NOTES.md")

// Generate assembly info files with the right version & up-to-date information
Target "AssemblyInfo" (fun _ ->
  let fileName = "src/log4net.Kinesis/AssemblyInfo.fs"
  CreateFSharpAssemblyInfo fileName
      [ Attribute.Title         project
        Attribute.Product       project
        Attribute.Description   summary
        Attribute.Version       release.AssemblyVersion
        Attribute.FileVersion   release.AssemblyVersion ] 

  let fileName40 = "src/log4net.Kinesis40/AssemblyInfo.fs"
  CreateFSharpAssemblyInfo fileName40
      [ Attribute.Title         project
        Attribute.Product       project
        Attribute.Description   summary
        Attribute.Version       release.AssemblyVersion
        Attribute.FileVersion   release.AssemblyVersion ] 
)

// --------------------------------------------------------------------------------------
// Clean build results & restore NuGet packages

Target "RestorePackages" RestorePackages

Target "Clean" (fun _ ->
    CleanDirs [ buildDir; build40Dir; testDir; tempDir ]
)

Target "CleanDocs" (fun _ ->
    CleanDirs [ "docs/output" ]
)

// --------------------------------------------------------------------------------------
// Build library & test project

let files includes = 
  { BaseDirectory = __SOURCE_DIRECTORY__
    Includes = includes
    Excludes = [] } 

Target "Build" (fun _ ->
    files [ "src/log4net.kinesis/" + projectFile ]
    |> MSBuildRelease buildDir "Rebuild"
    |> ignore

    files [ "src/log4net.kinesis40/" + project40File ]
    |> MSBuildRelease build40Dir "Rebuild"
    |> ignore
)

// --------------------------------------------------------------------------------------
// Run the unit tests using test runner & kill test runner when complete

Target "RunTests" (fun _ ->
    ActivateFinalTarget "CloseTestRunner"

    { BaseDirectory = __SOURCE_DIRECTORY__
      Includes = testAssemblies
      Excludes = [] } 
    |> NUnit (fun p ->
        { p with
            DisableShadowCopy = true
            TimeOut = TimeSpan.FromMinutes 20.
            OutputFile = "TestResults.xml" })
)

FinalTarget "CloseTestRunner" (fun _ ->  
    ProcessHelper.killProcess "nunit-agent.exe"
)

// --------------------------------------------------------------------------------------
// Build a NuGet package

Target "NuGet" (fun _ ->
    // Format the description to fit on a single line (remove \r\n and double-spaces)
    let description = description.Replace("\r", "")
                                 .Replace("\n", "")
                                 .Replace("  ", " ")

    NuGet (fun p -> 
        { p with   
            Authors = authors
            Project = project
            Summary = summary
            Description = description
            Version = release.NugetVersion
            ReleaseNotes = String.Join(Environment.NewLine, release.Notes)
            Tags = tags
            OutputPath = "nuget"
            AccessKey = getBuildParamOrDefault "nugetkey" ""
            Publish = hasBuildParam "nugetkey"
            Dependencies = 
                [ "AWSSDK.Kinesis",  GetPackageVersion "packages" "AWSSDK.Kinesis"
                  "AWSSDK.Core",  GetPackageVersion "packages" "AWSSDK.Core"
                  "log4net", GetPackageVersion "packages" "log4net" ] })
        ("nuget/" + project + ".nuspec")
)

// --------------------------------------------------------------------------------------
// Generate the documentation

Target "GenerateDocs" (fun _ ->
    executeFSIWithArgs "docs/tools" "generate.fsx" ["--define:RELEASE"] [] |> ignore
)

// --------------------------------------------------------------------------------------
// Release Scripts

Target "ReleaseDocs" (fun _ ->
    let ghPages      = "gh-pages"
    let ghPagesLocal = "temp/gh-pages"
    Repository.clone "temp" (gitHome + "/" + gitName + ".git") ghPages
    Branches.checkoutBranch ghPagesLocal ghPages
    fullclean ghPagesLocal
    CopyRecursive "docs/output" ghPagesLocal true |> printfn "%A"
    CommandHelper.runSimpleGitCommand ghPagesLocal "add ." |> printfn "%s"
    let cmd = sprintf """commit -a -m "Update generated documentation for version %s""" release.NugetVersion
    CommandHelper.runSimpleGitCommand ghPagesLocal cmd |> printfn "%s"
    Branches.push ghPagesLocal
)

Target "Release" DoNothing

// --------------------------------------------------------------------------------------
// Run all targets by default. Invoke 'build <Target>' to override

Target "All" DoNothing

"Clean"
  ==> "RestorePackages"
  ==> "AssemblyInfo"
  ==> "Build"
//  ==> "RunTests"
  ==> "All"

"All" 
//  ==> "CleanDocs"
//  ==> "GenerateDocs"
//  ==> "ReleaseDocs"
  ==> "NuGet"
  ==> "Release"

RunTargetOrDefault "All"
